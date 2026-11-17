using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Tags;

namespace Ohanami.Core.FullText;

/// <summary>
/// 全文検索の索引(<c>fts_docs</c> と <c>search_text</c>)の DB 層。captions と手動タグから作る**派生データ**で、マイグレーションの外に置く(ADR 0030)。
/// <list type="bullet">
/// <item><see cref="EnsureAsync"/>: <c>index_meta['fts_spec']</c>(<see cref="FtsIndexSpec.CanonicalString"/>)と現行の規約を比べ、違えば(または fts_docs が無ければ)
///   **1 つの書き込みトランザクション**で、fts_docs と search_text を captions から作り直す。この間 <see cref="State"/> = <see cref="FtsIndexState.Rebuilding"/>
///   (全文の経路は NotReady を返す。読みは WAL で妨げない。他の書き込みは待つ)。呼び出しは何回でもよく、同時に呼ばれても作り直しは 1 回。失敗したら <see cref="FtsIndexState.Failed"/>
///   (トランザクションは巻き戻り、古い索引と規約の記録はそのまま)。もう一度呼べばやり直す。</item>
/// <item><see cref="IndexImage"/>: 1 枚の更新(captions またはタグの書き込みと同じトランザクションの中で呼ぶ)。**索引文書の元は、その画像の captions のうち created_at が最新のもの**
///   (同じなら rowid が大きいほう。モデルは問わない)と、その画像の手動タグ(<see cref="FtsSpec.DocSelectVersion"/>)。
///   **caption が無くても、手動タグがあれば文書を作る**(search_text.model_id は NULL)。caption も手動タグも無い画像の索引の行は消す。
///   作り直しも同じ選び方なので、増分で作った索引と作り直した索引は同じ中身になる。</item>
/// </list>
/// 作り直しで索引文書が作れない画像(<see cref="InvalidOperationException"/>)は、飛ばして警告をログに出す(件数は <see cref="LastRebuildSkipped"/>)。それ以外の例外は作り直し全体の失敗。
/// </summary>
internal sealed class FtsIndex
{
    private readonly OhanamiDatabase _db;
    private readonly ICoreLog? _log;
    private readonly object _gate = new();
    private Task? _ensure;
    private FtsIndexState _state = FtsIndexState.NotChecked;
    private string? _failure;
    private int _rebuildCount;
    private int _lastIndexed;
    private int _lastSkipped;

    public FtsIndexSpec Spec { get; }

    /// <summary>ログの出し先(<see cref="Captions.CaptionStore"/> が、索引に入れずに保存したことの警告に使う)。</summary>
    internal ICoreLog? Log => _log;

    /// <summary>索引文書の作り方(ocr、説明文、VLM の tags、手動タグ)。製品は <see cref="FtsDocumentBuilder.Build(string, string, IReadOnlyList{string}, IReadOnlyList{string})"/>(テストが差し替えて、失敗や途中の例外を作る)。</summary>
    internal Func<string, string, IReadOnlyList<string>, IReadOnlyList<string>, FtsDocument> BuildDocument { get; set; } = FtsDocumentBuilder.Build;

    public FtsIndex(OhanamiDatabase db, ICoreLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _log = log;
        Spec = FtsIndexSpec.Current;
    }

    public FtsIndexState State
    {
        get { lock (_gate) return _state; }
    }

    public string? FailureMessage
    {
        get { lock (_gate) return _failure; }
    }

    /// <summary>これまでに作り直した回数(テスト・診断用)。</summary>
    public int RebuildCount => Volatile.Read(ref _rebuildCount);

    /// <summary>直近の作り直しで索引に入れた画像の数と、索引文書が作れず飛ばした画像の数。</summary>
    public (int Indexed, int Skipped) LastRebuild
    {
        get { lock (_gate) return (_lastIndexed, _lastSkipped); }
    }

    private void SetState(FtsIndexState state, string? failure = null)
    {
        lock (_gate)
        {
            _state = state;
            _failure = failure;
        }
    }

    // ---------------------------------------------------------------- 規約の照合と作り直し

    /// <summary>
    /// 規約を照合して、必要なら作り直す。**待ち終わったら索引は Ready**(失敗なら例外)。同時に呼ばれても、作り直しは 1 回。失敗したあとに呼ぶと、もう一度やる。
    /// <paramref name="ct"/> は待つのをやめるだけで、始まった作り直しは終わらせる。
    /// </summary>
    public Task EnsureAsync(CancellationToken ct = default)
    {
        Task task;
        lock (_gate)
        {
            if (_ensure is null || _ensure.IsFaulted || _ensure.IsCanceled) _ensure = Task.Run(EnsureCoreAsync, CancellationToken.None);
            task = _ensure;
        }
        return task.WaitAsync(ct);
    }

    /// <summary>
    /// 索引が使える(Ready になった)ときだけ true。索引が Failed(作り直しの失敗。この <see cref="FtsIndex"/> がエラーをログに出している)なら false:
    /// 保存のたびに作り直しをやり直さない(やり直しは <see cref="EnsureAsync"/> を呼ぶ側=起動時や再試行の操作)。<paramref name="ct"/> の取り消しは例外のまま。
    /// 索引は派生データなので、これが false でも、captions やタグの書き込みは捨てない(<see cref="Captions.CaptionStore"/>・<see cref="ManualTags"/>)。
    /// </summary>
    internal async Task<bool> TryEnsureUsableAsync(CancellationToken ct)
    {
        if (State == FtsIndexState.Failed) return false;
        try
        {
            await EnsureAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task EnsureCoreAsync()
    {
        try
        {
            bool upToDate;
            using (SqliteConnection read = _db.CreateReadConnection())
            {
                upToDate = StoredSpec(read, null) == Spec.CanonicalString() && TableExists(read, null);
            }
            if (upToDate)
            {
                SetState(FtsIndexState.Ready);
                return;
            }

            SetState(FtsIndexState.Rebuilding);
            long started = Stopwatch.GetTimestamp();
            (int indexed, int skipped) = await _db.WriteAsync(Rebuild, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _rebuildCount);
            lock (_gate)
            {
                _lastIndexed = indexed;
                _lastSkipped = skipped;
            }
            _log?.Info($"全文検索の索引を作り直した(規約が DB と違った、または無かった): {indexed} 枚を索引に入れ、{skipped} 枚を飛ばした({Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms)。");
            SetState(FtsIndexState.Ready);
        }
        catch (Exception e)
        {
            SetState(FtsIndexState.Failed, e.Message);
            _log?.Error("全文検索の索引を作り直せなかった(古い索引はそのまま): " + e.Message, e);
            throw;
        }
    }

    /// <summary>
    /// fts_docs と search_text を、captions と手動タグから作り直し、規約を記録する(1 トランザクションの中)。
    /// 文書にするのは「最新の caption のある画像」と「手動タグのある画像」の和(ハッシュの順に入れる)。
    /// </summary>
    private (int Indexed, int Skipped) Rebuild(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, tx, $"DROP TABLE IF EXISTS {FtsIndexSpec.TableName}");
        Exec(conn, tx, "DELETE FROM search_text");
        Exec(conn, tx, Spec.CreateTableSql());

        var captions = new Dictionary<string, CaptionRowForIndex>(StringComparer.Ordinal);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT c.hash, c.model_id, c.ocr, c.description, c.tags_json
                FROM captions c
                WHERE c.rowid = {FtsSpec.LatestCaptionRowid("c")}
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) captions[r.GetString(0)] = new CaptionRowForIndex(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));
        }
        Dictionary<string, List<string>> manual = ManualTags.ReadAll(conn, tx);

        string[] hashes = [.. captions.Keys.Union(manual.Keys, StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal)];
        int indexed = 0, skipped = 0;
        foreach (string hash in hashes)
        {
            captions.TryGetValue(hash, out CaptionRowForIndex? row);
            IReadOnlyList<string> manualTags = manual.TryGetValue(hash, out List<string>? m) ? m : [];
            FtsDocument doc;
            try
            {
                doc = row is null
                    ? BuildDocument("", "", [], manualTags)
                    : BuildDocument(row.Ocr, row.Description, ParseTags(hash, row.TagsJson), manualTags);
            }
            catch (InvalidOperationException e)
            {
                skipped++;
                _log?.Warning($"索引文書が作れないので、この画像は索引に入れない(captions とタグは残る): {hash}: {e.Message}");
                continue;
            }
            InsertNew(conn, tx, hash, row?.ModelId, doc);
            indexed++;
        }

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO index_meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$k", FtsIndexSpec.MetaKey);
            cmd.Parameters.AddWithValue("$v", Spec.CanonicalString());
            cmd.ExecuteNonQuery();
        }
        return (indexed, skipped);
    }

    private sealed record CaptionRowForIndex(string Hash, string ModelId, string Ocr, string Description, string TagsJson);

    // ---------------------------------------------------------------- 1 枚の更新(captions と同じトランザクションの中)

    /// <summary>
    /// 保存された規約が現行と同じで、fts_docs があることを確かめる(違えば <see cref="InvalidOperationException"/>。<see cref="EnsureAsync"/> の前に書こうとした、などの誤り)。
    /// </summary>
    internal void RequireCurrent(SqliteConnection conn, SqliteTransaction tx)
    {
        string? stored = StoredSpec(conn, tx);
        if (stored != Spec.CanonicalString() || !TableExists(conn, tx))
            throw new InvalidOperationException("全文検索の索引の規約が DB と違う(または fts_docs が無い)。EnsureAsync を待ってから書く。");
    }

    /// <summary>
    /// 1 枚の画像の索引を、その画像の最新の caption(created_at が最大、同じなら rowid が大きいほう)と手動タグから更新する。
    /// caption が無くても手動タグがあれば文書を作る(model_id は NULL)。caption も手動タグも無ければ、その画像の索引の行を消す(最後のタグを外した場合など)。
    /// 索引文書が作れなければ例外(呼び出し側が先に作れることを確かめておく。ここで失敗すると、トランザクションごと巻き戻る)。
    /// </summary>
    internal void IndexImage(SqliteConnection conn, SqliteTransaction tx, string hash)
    {
        CaptionRowForIndex? row = null;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT hash, model_id, ocr, description, tags_json FROM captions WHERE hash = $h ORDER BY {FtsSpec.LatestCaptionOrderBy} LIMIT 1";
            cmd.Parameters.AddWithValue("$h", hash);
            using SqliteDataReader r = cmd.ExecuteReader();
            if (r.Read()) row = new CaptionRowForIndex(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));
        }
        IReadOnlyList<string> manualTags = ManualTags.ForImage(conn, tx, hash);
        if (row is null && manualTags.Count == 0)
        {
            RemoveImage(conn, tx, hash);
            return;
        }

        FtsDocument doc = row is null
            ? BuildDocument("", "", [], manualTags)
            : BuildDocument(row.Ocr, row.Description, ParseTags(hash, row.TagsJson), manualTags);
        string? modelId = row?.ModelId;

        long? id = null;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM search_text WHERE hash = $h";
            cmd.Parameters.AddWithValue("$h", hash);
            object? v = cmd.ExecuteScalar();
            if (v is not null and not DBNull) id = Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        if (id is null)
        {
            InsertNew(conn, tx, hash, modelId, doc);
            return;
        }

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE search_text SET norm_text = $n, model_id = $m WHERE id = $id";
            cmd.Parameters.AddWithValue("$n", doc.NormText);
            cmd.Parameters.AddWithValue("$m", (object?)modelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id.Value);
            cmd.ExecuteNonQuery();
        }
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {FtsIndexSpec.TableName} WHERE rowid = $id";
            cmd.Parameters.AddWithValue("$id", id.Value);
            cmd.ExecuteNonQuery();
        }
        InsertFts(conn, tx, id.Value, doc);
    }

    /// <summary>
    /// <see cref="IndexImage"/> を、**失敗しても captions・タグの書き込みを巻き込まない**形で呼ぶ(セーブポイントで包む)。
    /// 索引文書が作れない、または最新の caption の tags_json が壊れている(どちらもその文字列に対して決定的な失敗で、再試行しても直らない)ときは、
    /// この画像の索引の行を消して(古い caption から作った行を残すと、最新の caption と食い違う)、理由を返す。成功なら null。
    /// それ以外の例外(DB の I/O など)はそのまま投げる(トランザクションごと巻き戻る)。
    /// </summary>
    internal string? TryIndexImage(SqliteConnection conn, SqliteTransaction tx, string hash)
    {
        Exec(conn, tx, "SAVEPOINT fts_image");
        try
        {
            IndexImage(conn, tx, hash);
            Exec(conn, tx, "RELEASE fts_image");
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
        {
            Exec(conn, tx, "ROLLBACK TO fts_image");
            Exec(conn, tx, "RELEASE fts_image");
            RemoveImage(conn, tx, hash);
            return e.Message;
        }
    }

    /// <summary>1 枚の画像の索引の行(search_text と fts_docs)を消す。無ければ何もしない。</summary>
    internal void RemoveImage(SqliteConnection conn, SqliteTransaction tx, string hash)
    {
        long? id = null;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM search_text WHERE hash = $h";
            cmd.Parameters.AddWithValue("$h", hash);
            object? v = cmd.ExecuteScalar();
            if (v is not null and not DBNull) id = Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
        if (id is null) return;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {FtsIndexSpec.TableName} WHERE rowid = $id";
            cmd.Parameters.AddWithValue("$id", id.Value);
            cmd.ExecuteNonQuery();
        }
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM search_text WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertNew(SqliteConnection conn, SqliteTransaction tx, string hash, string? modelId, FtsDocument doc)
    {
        long id;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO search_text(hash, norm_text, model_id) VALUES ($h, $n, $m) RETURNING id";
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$n", doc.NormText);
            cmd.Parameters.AddWithValue("$m", (object?)modelId ?? DBNull.Value);
            id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        InsertFts(conn, tx, id, doc);
    }

    private void InsertFts(SqliteConnection conn, SqliteTransaction tx, long id, FtsDocument doc)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = Spec.InsertSql();
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$tokens", doc.Tokens);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- 補助

    internal static string? StoredSpec(SqliteConnection conn, SqliteTransaction? tx)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM index_meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", FtsIndexSpec.MetaKey);
        object? v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (string)v;
    }

    internal static bool TableExists(SqliteConnection conn, SqliteTransaction? tx)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $n";
        cmd.Parameters.AddWithValue("$n", FtsIndexSpec.TableName);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>captions.tags_json を厳格に読む(JSON の文字列の配列。違えば <see cref="InvalidDataException"/>)。</summary>
    internal static IReadOnlyList<string> ParseTags(string hash, string tagsJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(tagsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"captions の tags_json が配列でない(画像 {hash})。");
            var list = new List<string>();
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) throw new InvalidDataException($"captions の tags_json に文字列でない要素がある(画像 {hash})。");
                list.Add(e.GetString()!);
            }
            return list;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"captions の tags_json が JSON として読めない(画像 {hash}): {e.Message}", e);
        }
    }
}
