using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Ohanami.Core.Data;

/// <summary>
/// Ohanami の DB(SQLite)。開く・作る・版を検査する・マイグレーションする・接続を配る。
/// <list type="bullet">
/// <item>書き込みは 1 本の接続をセマフォで直列化し、1 回の呼び出し = 1 トランザクション(<see cref="WriteAsync{T}"/>)。</item>
/// <item>読み取りは用途ごとの短命の接続(<see cref="CreateReadConnection"/>。呼び出し側が Dispose。query_only)。WAL なので書き込みを妨げない。</item>
/// <item>接続のたびに foreign_keys、synchronous、busy_timeout を明示的に設定する(既定値に頼らない)。接続プールは使わない(Dispose でファイルが確実に閉じる)。</item>
/// </list>
/// 新規作成のときだけ、最初の表を作る前に page_size = 8192 を設定し、そのあと WAL にする(WAL のままでは page_size を変えられない)。
/// 開くときは application_id と user_version を検査し、知らないものは <see cref="UnknownSchemaVersionException"/> で止まる。
/// </summary>
internal sealed class OhanamiDatabase : IDisposable
{
    /// <summary>PRAGMA application_id に入れる値('OHNM')。</summary>
    public const int ApplicationId = 0x4F484E4D;

    /// <summary>新規作成時の page_size。1 行約 4.7KB のベクトルが overflow ページに分かれない大きさ(ADR 0005)。</summary>
    public const int PageSize = 8192;

    private const int BusyTimeoutMs = 5000;
    private const int SqliteNotADatabase = 26; // SQLITE_NOTADB

    private readonly string _connectionString;
    private readonly SqliteConnection _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public string Path { get; }

    /// <summary>開いた時点のスキーマの版(マイグレーション後)。</summary>
    public int SchemaVersion { get; }

    private OhanamiDatabase(string path, string connectionString, SqliteConnection writer, int schemaVersion)
    {
        Path = path;
        _connectionString = connectionString;
        _writer = writer;
        SchemaVersion = schemaVersion;
    }

    public static OhanamiDatabase Open(string path) => Open(path, DatabaseSchema.Default);

    public static OhanamiDatabase Open(string path, DatabaseSchema schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(schema);

        string full = System.IO.Path.GetFullPath(path);
        string? dir = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            // 接続を開いた時点から待つ(Microsoft.Data.Sqlite の既定は 30 秒。ここで明示して既定に頼らない)
            DefaultTimeout = BusyTimeoutMs / 1000,
        };
        string connectionString = csb.ToString();

        var writer = new SqliteConnection(connectionString);
        try
        {
            writer.Open();
            // 別のプロセスがロックを持っていても、すぐ失敗せず待つ。Initialize の最初の読み取りより前に設定する
            // (App の単一インスタンスの制御[Mutex]が二重起動を防ぐ。これは、それをすり抜けたときの最後の砦)。
            ExecPragma(writer, $"PRAGMA busy_timeout = {BusyTimeoutMs.ToString(CultureInfo.InvariantCulture)}");
            int version = Initialize(writer, schema, full);
            ApplyConnectionPragmas(writer, queryOnly: false);
            return new OhanamiDatabase(full, connectionString, writer, version);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    /// <summary>読み取り専用(query_only)の短命の接続を開いて返す。呼び出し側が Dispose する。</summary>
    public SqliteConnection CreateReadConnection()
    {
        ThrowIfDisposed();
        var csb = new SqliteConnectionStringBuilder(_connectionString) { Mode = SqliteOpenMode.ReadWrite };
        var conn = new SqliteConnection(csb.ToString());
        try
        {
            conn.Open();
            ApplyConnectionPragmas(conn, queryOnly: true);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 読み取り用の短命の接続を開き、**1 つの読み取りのトランザクションの中**で work を実行して、結果を返す(接続は閉じる)。
    /// work の中の複数の SELECT は、WAL の同じスナップショット(最初の SELECT の時点)を見る。複数の文を別々に読むと、間に入った書き込みで
    /// 片方だけ新しい状態を読みうる(<see cref="InSnapshot{T}"/>)。書き込みは待たせない。
    /// </summary>
    public T ReadSnapshot<T>(Func<SqliteConnection, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using SqliteConnection conn = CreateReadConnection();
        return InSnapshot(conn, () => work(conn));
    }

    /// <summary>
    /// 読み取り用の接続 <paramref name="conn"/> の上で、生の <c>BEGIN</c> … <c>COMMIT</c> の中で work を実行する(最初の SELECT で読み取りの時点が決まり、COMMIT まで変わらない)。
    /// <c>SqliteTransaction</c> を作らないので、work の中のコマンドに Transaction を渡さなくてよい。読み取りだけなので、work が例外でも COMMIT で閉じる。
    /// </summary>
    internal static T InSnapshot<T>(SqliteConnection conn, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(work);
        ExecPragma(conn, "BEGIN");
        try
        {
            return work();
        }
        finally
        {
            ExecPragma(conn, "COMMIT");
        }
    }

    /// <summary>
    /// 書き込み用の接続で work を 1 トランザクションとして実行する。呼び出しはセマフォで直列化される。
    /// work が例外を投げたら、その呼び出しの書き込みはすべてなかったことになる。work の中では await しない(同期で書く)。
    /// </summary>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ThrowIfDisposed();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            using SqliteTransaction tx = _writer.BeginTransaction();
            T result = work(_writer, tx);
            tx.Commit();
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task WriteAsync(Action<SqliteConnection, SqliteTransaction> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return WriteAsync<object?>((conn, tx) => { work(conn, tx); return null; }, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 実行中の書き込みがあれば、それが終わるのを待ってから閉じる
        _writeLock.Wait();
        try { _writer.Dispose(); }
        finally { _writeLock.Release(); }
        _writeLock.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // ---- 開く・作る・版の検査・マイグレーション

    private static int Initialize(SqliteConnection conn, DatabaseSchema schema, string path)
    {
        int applicationId, userVersion;
        long objectCount;
        try
        {
            applicationId = (int)PragmaLong(conn, "application_id");
            userVersion = (int)PragmaLong(conn, "user_version");
            objectCount = ScalarLong(conn, "SELECT count(*) FROM sqlite_master");
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteNotADatabase)
        {
            throw new UnknownSchemaVersionException($"{path} は SQLite のデータベースではない(Ohanami の DB として開けない)。", e);
        }

        bool isEmpty = applicationId == 0 && userVersion == 0 && objectCount == 0;
        if (isEmpty)
        {
            CreateNew(conn, schema);
            return schema.CurrentVersion;
        }

        if (applicationId != ApplicationId)
        {
            throw new UnknownSchemaVersionException(
                $"{path} は Ohanami の DB ではない(application_id = 0x{applicationId:X8}。期待は 0x{ApplicationId:X8})。");
        }
        if (userVersion < 1)
        {
            throw new UnknownSchemaVersionException($"{path} のスキーマの版 {userVersion} は不正(表があるのに 1 未満)。");
        }
        if (userVersion > schema.CurrentVersion)
        {
            throw new UnknownSchemaVersionException(
                $"{path} のスキーマの版 {userVersion} は、このアプリが知っている版 {schema.CurrentVersion} より新しい(新しい版のアプリで作られた DB?)。");
        }

        if (userVersion < schema.CurrentVersion) Migrate(conn, schema, userVersion, path, makeBackup: true);

        EnsureWal(conn);
        VerifyTables(conn, schema, path);
        return schema.CurrentVersion;
    }

    private static void CreateNew(SqliteConnection conn, DatabaseSchema schema)
    {
        // 順序が重要: page_size は最初のテーブルを作る前、WAL にする前に設定する(WAL のままでは変えられない)。
        ExecPragma(conn, $"PRAGMA page_size = {PageSize.ToString(CultureInfo.InvariantCulture)}");
        EnsureWal(conn);

        // 版 1 を作る。版 2 以降は、移行と同じマイグレーションを順に適用して作る(新規作成と移行の経路は 1 つ。バックアップは要らない)
        using (SqliteTransaction tx = conn.BeginTransaction())
        {
            schema.CreateInitial(conn, tx);
            DatabaseSchema.Exec(conn, tx, $"PRAGMA application_id = {ApplicationId.ToString(CultureInfo.InvariantCulture)}");
            DatabaseSchema.Exec(conn, tx, "PRAGMA user_version = 1");
            tx.Commit();
        }
        if (schema.CurrentVersion > 1) Migrate(conn, schema, 1, path: null, makeBackup: false);

        long pageSize = PragmaLong(conn, "page_size");
        if (pageSize != PageSize)
            throw new InvalidOperationException($"page_size が {PageSize} にならなかった(実際は {pageSize})。page_size は最初のテーブルを作る前に設定する必要がある。");
        VerifyTables(conn, schema, "(新規作成した DB)");
    }

    /// <summary>
    /// fromVersion から現行の版まで、マイグレーションを 1 段ずつ適用する。
    /// <list type="number">
    /// <item>makeBackup なら、適用の前に <c>VACUUM INTO '&lt;DB&gt;.bak-v&lt;fromVersion&gt;'</c> で複製を取る(既にあれば <c>-2</c>、<c>-3</c> … と別名にする。上書きしない)。</item>
    /// <item>適用中は <c>foreign_keys = OFF</c>(表の作り直しに要る)を明示する。接続の既定 ON には、呼び出し側の <see cref="ApplyConnectionPragmas"/> が戻す。</item>
    /// <item>1 段 = 1 トランザクション。段のあとに <c>PRAGMA foreign_key_check</c> を行い、1 行でも返れば例外(その段は巻き戻る)。</item>
    /// </list>
    /// </summary>
    private static void Migrate(SqliteConnection conn, DatabaseSchema schema, int fromVersion, string? path, bool makeBackup)
    {
        if (makeBackup) BackupBeforeMigration(conn, path ?? throw new ArgumentNullException(nameof(path)), fromVersion);

        ExecPragma(conn, "PRAGMA foreign_keys = OFF");
        if (PragmaLong(conn, "foreign_keys") != 0) throw new InvalidOperationException("マイグレーション中に foreign_keys を OFF にできなかった");

        for (int v = fromVersion; v < schema.CurrentVersion; v++)
        {
            Migration step = schema.Migrations[v - 1];
            if (step.FromVersion != v) throw new InvalidOperationException($"マイグレーションの並びが不正(版 {v} の段が {step.FromVersion})");
            using SqliteTransaction tx = conn.BeginTransaction();
            step.Apply(conn, tx);
            DatabaseSchema.Exec(conn, tx, $"PRAGMA user_version = {(v + 1).ToString(CultureInfo.InvariantCulture)}");
            CheckForeignKeys(conn, tx, v, v + 1);
            tx.Commit();
        }
    }

    private static void BackupBeforeMigration(SqliteConnection conn, string path, int fromVersion)
    {
        string backup = $"{path}.bak-v{fromVersion.ToString(CultureInfo.InvariantCulture)}";
        for (int n = 2; File.Exists(backup); n++)
            backup = $"{path}.bak-v{fromVersion.ToString(CultureInfo.InvariantCulture)}-{n.ToString(CultureInfo.InvariantCulture)}";
        ExecPragma(conn, "VACUUM INTO '" + backup.Replace("'", "''", StringComparison.Ordinal) + "'");
    }

    private static void CheckForeignKeys(SqliteConnection conn, SqliteTransaction tx, int from, int to)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA foreign_key_check";
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return;
        string table = r.GetString(0);
        string rowid = r.IsDBNull(1) ? "(rowid なし)" : r.GetInt64(1).ToString(CultureInfo.InvariantCulture);
        string parent = r.GetString(2);
        throw new InvalidOperationException(
            $"マイグレーション {from} → {to} のあとで外部キーの不整合がある(例: 表 {table} の行 {rowid} が、親の表 {parent} に無い行を指している)。その段は巻き戻した。");
    }

    private static void EnsureWal(SqliteConnection conn)
    {
        string mode = ScalarString(conn, "PRAGMA journal_mode = WAL");
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"journal_mode を WAL にできなかった(実際は {mode})");
    }

    private static void VerifyTables(SqliteConnection conn, DatabaseSchema schema, string where)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(0));
        }
        var missing = new List<string>();
        foreach (string t in schema.ExpectedTables) if (!existing.Contains(t)) missing.Add(t);
        if (missing.Count > 0)
            throw new UnknownSchemaVersionException($"{where} に、版 {schema.CurrentVersion} のスキーマにあるはずの表が無い: {string.Join(", ", missing)}");
    }

    // ---- 接続ごとの設定

    private static void ApplyConnectionPragmas(SqliteConnection conn, bool queryOnly)
    {
        ExecPragma(conn, "PRAGMA foreign_keys = ON");
        ExecPragma(conn, "PRAGMA synchronous = NORMAL");
        ExecPragma(conn, $"PRAGMA busy_timeout = {BusyTimeoutMs.ToString(CultureInfo.InvariantCulture)}");
        if (queryOnly) ExecPragma(conn, "PRAGMA query_only = ON");

        if (PragmaLong(conn, "foreign_keys") != 1) throw new InvalidOperationException("foreign_keys を有効にできなかった");
    }

    // ---- 小さな補助

    private static void ExecPragma(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long PragmaLong(SqliteConnection conn, string name) => ScalarLong(conn, "PRAGMA " + name);

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? v = cmd.ExecuteScalar();
        return v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private static string ScalarString(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }
}
