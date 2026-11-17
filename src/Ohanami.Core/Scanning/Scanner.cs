using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;

namespace Ohanami.Core.Scanning;

/// <summary>
/// フォルダの走査。登録済みのフォルダを再帰的に列挙し、(mtime_ticks, size) の差分があるものだけ
/// ファイルのバイト列の SHA-256(ストリームのまま)を取り、images / image_paths / file_issues に反映する。
/// <list type="bullet">
/// <item>対象の拡張子は .png / .jpg / .jpeg / .webp。形式は先頭のバイト列で判定する(拡張子と中身が違えば中身を信じる。どれでもなければ file_issues の unsupported)。</item>
/// <item>1 ハッシュに複数パス。内容が変わったパスは行のハッシュを更新する(古い images は残す)。</item>
/// <item>見えなくなったパスは missing_since を付けて残す。再び現れたら NULL に戻す。</item>
/// <item>到達できない根・サブディレクトリ(列挙に失敗した場所)の配下は今回の走査の対象外で、missing_since を付けない。folders.last_error に記録し、Unreachable に数える。</item>
/// <item>開けなかったファイル: 共有違反など一時的なものは記録せず次回に回す。アクセス拒否など恒久的なものは file_issues の unreadable に記録する(次回も再試行する)。</item>
/// <item>書き込みの途中かもしれないファイル(ADR 0010)は、今回は登録せず次回に回す(記録しない。<see cref="ScanSummary.TransientlyUnreadable"/> に数える):
///   (1) 更新時刻が「0 ≤ 今 − 更新時刻 &lt; 2 秒」のもの(未来の更新時刻は絶対値で判定せず、ふつうに登録する)、
///   (2) 開いたあと、ハンドルのサイズが列挙のサイズと違う、またはハンドルの更新時刻と列挙の更新時刻の差が 2 秒を超えるもの(列挙してから開くまでの間に書き足された)。
///   一致したときだけ登録するので、DB に入る mtime とサイズは列挙の値のままで、ハッシュの内容と対応する。</item>
/// <item>シンボリックリンク / ジャンクション(ReparsePoint)のディレクトリは辿らない(循環を避けるため)。黙ってスキップせず、辿らなかった件数を SkippedLinks に数える(エラーではないので last_error には入れない)。根そのものがリンクでも、根は辿る。</item>
/// </list>
/// ハッシュは最大 4 並列、DB の書き込みは 128 件ずつのバッチ(1 バッチ = 1 トランザクション)。キャンセルされたら missing の掃き出しは行わない
/// (列挙が完了していない状態で「見えない」と判断しない)。
/// </summary>
internal sealed class Scanner
{
    private const int HashParallelism = 4;
    private const int WriteBatchSize = 128;
    private const int LastErrorMaxEntries = 20;

    /// <summary>更新から、この時間より新しいファイルは、書き手がまだ書いているかもしれないので、今回は登録しない(推測の初期値)。</summary>
    internal static readonly TimeSpan RecentWriteWindow = TimeSpan.FromSeconds(2);

    /// <summary>ハンドルの更新時刻と列挙の更新時刻の許容差。FS によって更新時刻の刻みや表し方が違っても、永久に登録されないことが無いように(完全一致を求めない)。</summary>
    internal static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    private readonly OhanamiDatabase _db;
    private readonly TimeProvider _time;

    /// <summary>テスト用の継ぎ目: ファイルを開く直前に呼ばれる(パス)。「列挙してから開くまでの間に書き足された」場面を、決まったタイミングで作るため。製品では null。</summary>
    internal Action<string>? BeforeOpenForTests { get; set; }

    public Scanner(OhanamiDatabase db, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        _db = db;
        _time = time;
    }

    /// <summary>
    /// 指定したフォルダ(null なら有効なフォルダすべて)を順に走査する。指定した id が無ければ <see cref="FolderRegistrationException"/>。
    /// 指定したフォルダは、無効でも走査する。
    /// </summary>
    public async Task<ScanSummary> ScanAsync(IReadOnlyCollection<long>? folderIds, Action<ScanProgress>? onProgress, CancellationToken ct)
    {
        DateTimeOffset started = _time.GetUtcNow();
        long startTimestamp = _time.GetTimestamp();

        List<(long Id, string Path)> folders = LoadFolders(folderIds);
        var total = new Counters();
        foreach ((long id, string path) in folders)
        {
            ct.ThrowIfCancellationRequested();
            Counters c = await ScanFolderAsync(id, path, onProgress, ct).ConfigureAwait(false);
            total.Add(c);
        }
        return new ScanSummary(total.Added, total.Changed, total.Missing, total.Unreadable, total.Unsupported, total.Unreachable, total.SkippedLinks,
            _time.GetElapsedTime(startTimestamp), total.TransientlyUnreadable);
    }

    /// <summary>
    /// <see cref="ScanAsync"/> が走査するフォルダの id(null なら有効なフォルダすべて)。指定した id が無ければ <see cref="FolderRegistrationException"/>(走査と同じ)。
    /// Indexer が、走査の完了の知らせに載せるために使う。
    /// </summary>
    internal IReadOnlyList<long> ResolveFolderIds(IReadOnlyCollection<long>? folderIds) => LoadFolders(folderIds).Select(f => f.Id).ToList();

    // ---- フォルダ 1 つ

    private async Task<Counters> ScanFolderAsync(long folderId, string root, Action<ScanProgress>? onProgress, CancellationToken ct)
    {
        var counters = new Counters();
        void Report() => onProgress?.Invoke(counters.ToProgress(folderId));

        // 1. 列挙(到達できなかった場所の集合 U も作る)
        Walk walk = WalkDirectory(root, ct);
        counters.FilesSeen = walk.Files.Count;
        counters.Unreachable = walk.Unreachable.Count;
        counters.SkippedLinks = walk.SkippedLinks;
        Report();

        // 2. 既知の行との突き合わせ
        Dictionary<string, PathRow> existing = LoadPathRows(folderId);
        Dictionary<string, IssueRow> issues = LoadIssueRows(folderId);
        var foundKeys = new HashSet<string>(StringComparer.Ordinal);
        var toProcess = new List<(FoundFile File, PathRow? Existing)>();
        var restore = new List<PathRow>();
        foreach (FoundFile f in walk.Files)
        {
            string key = PathKey.Normalize(f.Path);
            foundKeys.Add(key);
            existing.TryGetValue(key, out PathRow? row);
            if (row is not null && row.MtimeTicks == f.MtimeTicks && row.Size == f.Size)
            {
                if (row.MissingSince is not null) restore.Add(row); // 再び現れた(内容は同じ)
                continue;
            }
            if (issues.TryGetValue(key, out IssueRow? issue) && issue.Kind == FileIssueKind.Unsupported
                && issue.MtimeTicks == f.MtimeTicks && issue.Size == f.Size)
            {
                counters.Unsupported++; // 同じ mtime・サイズで未対応と分かっているものは、毎回やり直さない
                continue;
            }
            toProcess.Add((f, row));
        }
        if (restore.Count > 0)
        {
            await _db.WriteAsync((conn, tx) =>
            {
                foreach (PathRow row in restore)
                    Exec(conn, tx, "UPDATE image_paths SET missing_since = NULL WHERE path = $path", ("$path", row.Path));
            }, ct).ConfigureAwait(false);
        }

        // 3. 差分のあるファイルを読む(最大 4 並列)。結果は 128 件ずつ DB に書く
        var results = new ConcurrentQueue<FileResult>();
        var flushLock = new SemaphoreSlim(1, 1);
        async Task FlushAsync(bool all)
        {
            await flushLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (results.Count >= (all ? 1 : WriteBatchSize))
                {
                    var batch = new List<FileResult>(WriteBatchSize);
                    while (batch.Count < WriteBatchSize && results.TryDequeue(out FileResult? r)) batch.Add(r);
                    if (batch.Count == 0) break;
                    await _db.WriteAsync((conn, tx) => ApplyBatch(conn, tx, folderId, batch, counters), ct).ConfigureAwait(false);
                    Report();
                }
            }
            finally
            {
                flushLock.Release();
            }
        }

        await Parallel.ForEachAsync(toProcess, new ParallelOptions { MaxDegreeOfParallelism = HashParallelism, CancellationToken = ct },
            async (item, token) =>
            {
                results.Enqueue(ReadFile(item.File, item.Existing));
                if (results.Count >= WriteBatchSize) await FlushAsync(all: false).ConfigureAwait(false);
            }).ConfigureAwait(false);
        await FlushAsync(all: true).ConfigureAwait(false);

        // 4. 見えなくなったパスの掃き出し(列挙も読み取りも最後まで終わったときだけ)
        ct.ThrowIfCancellationRequested();
        string[] unreachableKeys = walk.Unreachable.Select(u => PathKey.DirectoryKey(u.Directory)).ToArray();
        bool IsInUnreachable(string pathKey) => unreachableKeys.Any(d => PathKey.IsUnder(pathKey, d));
        var newlyMissing = existing
            .Where(kv => !foundKeys.Contains(kv.Key) && kv.Value.MissingSince is null && !IsInUnreachable(kv.Key))
            .Select(kv => kv.Value).ToList();
        var staleIssues = issues.Where(kv => !foundKeys.Contains(kv.Key) && !IsInUnreachable(kv.Key)).Select(kv => kv.Value).ToList();
        string now = Now();
        string? lastError = FormatLastError(walk.Unreachable);
        await _db.WriteAsync((conn, tx) =>
        {
            foreach (PathRow row in newlyMissing)
                Exec(conn, tx, "UPDATE image_paths SET missing_since = $now WHERE path = $path AND missing_since IS NULL", ("$now", now), ("$path", row.Path));
            foreach (IssueRow issue in staleIssues)
                Exec(conn, tx, "DELETE FROM file_issues WHERE path = $path", ("$path", issue.Path));
            Exec(conn, tx, "UPDATE folders SET last_scan_at = $now, last_error = $err WHERE id = $id",
                ("$now", now), ("$err", lastError), ("$id", folderId));
        }, ct).ConfigureAwait(false);
        counters.Missing += newlyMissing.Count;
        Report();
        return counters;
    }

    // ---- 列挙

    private sealed record FoundFile(string Path, long Size, long MtimeTicks);

    private sealed record UnreachablePlace(string Directory, string Reason);

    private sealed record Walk(List<FoundFile> Files, List<UnreachablePlace> Unreachable, int SkippedLinks);

    private static Walk WalkDirectory(string root, CancellationToken ct)
    {
        var files = new List<FoundFile>();
        var unreachable = new List<UnreachablePlace>();
        if (!Directory.Exists(root))
        {
            unreachable.Add(new UnreachablePlace(root, "存在しない、または開けない(取り外したドライブ、切れたネットワークなど)"));
            return new Walk(files, unreachable, 0);
        }

        int skippedLinks = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
                unreachable.Add(new UnreachablePlace(dir, $"{e.GetType().Name}: {e.Message}"));
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skippedLinks++; // リンクは辿らない(循環を避ける)が、数える
                        continue;
                    }
                    pending.Push(sub.FullName);
                }
                else if (entry is FileInfo file && ImageFormatDetector.HasSupportedExtension(file.Name))
                {
                    files.Add(new FoundFile(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks));
                }
            }
        }
        return new Walk(files, unreachable, skippedLinks);
    }

    // ---- ファイルを読む(並列に呼ばれる。DB には触らない)

    private abstract record FileResult(FoundFile File, PathRow? Existing);

    private sealed record HashedResult(FoundFile File, PathRow? Existing, string Hash, ImageFormat Format, long BytesRead) : FileResult(File, Existing);

    private sealed record UnsupportedResult(FoundFile File, PathRow? Existing, string Message) : FileResult(File, Existing);

    private sealed record UnreadableResult(FoundFile File, PathRow? Existing, string Message, bool Permanent) : FileResult(File, Existing);

    /// <summary>書き込みの途中かもしれないので、今回は登録しない(次の走査に回す。記録しない)。</summary>
    private sealed record DeferredResult(FoundFile File, PathRow? Existing, string Message) : FileResult(File, Existing);

    /// <summary>ファイルが走査の間に消えた(次回の走査で missing になる)。</summary>
    private sealed record VanishedResult(FoundFile File, PathRow? Existing) : FileResult(File, Existing);

    private FileResult ReadFile(FoundFile file, PathRow? existing)
    {
        // 更新から 2 秒以内は、書き手がまだ書いているかもしれない(チャンクの間が短い書き手)。判定は「0 ≤ 今 − 更新時刻 < 2 秒」に限る(未来は登録する)
        TimeSpan age = _time.GetUtcNow().UtcDateTime - new DateTime(file.MtimeTicks, DateTimeKind.Utc);
        if (age >= TimeSpan.Zero && age < RecentWriteWindow)
            return new DeferredResult(file, existing, "更新から 2 秒以内なので、書き込みの途中かもしれない");
        try
        {
            BeforeOpenForTests?.Invoke(file.Path);
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 4096, FileOptions.SequentialScan);
            // ハンドルの値(開いた時点の最新)と、列挙の値(DB に入る値)を比べる。違えば、列挙してから開くまでの間に書き足された
            if (stream.Length != file.Size)
                return new DeferredResult(file, existing, $"開いたときのサイズ {stream.Length} が列挙のとき {file.Size} と違う(書き足された)");
            long handleMtime = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
            if (Math.Abs(handleMtime - file.MtimeTicks) > MtimeTolerance.Ticks)
                return new DeferredResult(file, existing, "開いたときの更新時刻が列挙のときと 2 秒を超えて違う(書き足された)");
            var header = new byte[ImageFormatDetector.HeaderLength];
            int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            ImageFormat? format = ImageFormatDetector.Detect(header.AsSpan(0, read));
            if (format is null)
                return new UnsupportedResult(file, existing, "中身が PNG / JPEG / WebP でない(先頭: " + Convert.ToHexStringLower(header.AsSpan(0, Math.Min(read, 8))) + ")");

            stream.Position = 0;
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            return new HashedResult(file, existing, hash, format.Value, stream.Length);
        }
        catch (FileNotFoundException) { return new VanishedResult(file, existing); }
        catch (DirectoryNotFoundException) { return new VanishedResult(file, existing); }
        catch (UnauthorizedAccessException e) { return new UnreadableResult(file, existing, e.Message, Permanent: true); }
        catch (IOException e) { return new UnreadableResult(file, existing, e.Message, Permanent: false); } // 共有違反など。記録せず次回に回す
    }

    // ---- DB への反映(書き込みの接続の上で、1 バッチ = 1 トランザクション)

    private void ApplyBatch(SqliteConnection conn, SqliteTransaction tx, long folderId, List<FileResult> batch, Counters counters)
    {
        string now = Now();
        foreach (FileResult result in batch)
        {
            FoundFile f = result.File;
            switch (result)
            {
                case HashedResult h:
                    counters.Hashed++;
                    Exec(conn, tx, "INSERT OR IGNORE INTO images(hash, size, format, first_seen_at) VALUES ($hash, $size, $format, $now)",
                        ("$hash", h.Hash), ("$size", h.BytesRead), ("$format", DbEnums.ToDb(h.Format)), ("$now", now));
                    if (h.Existing is null)
                    {
                        Exec(conn, tx, "INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size, missing_since) " +
                                       "VALUES ($path, $hash, $folder, $mtime, $size, NULL)",
                            ("$path", f.Path), ("$hash", h.Hash), ("$folder", folderId), ("$mtime", f.MtimeTicks), ("$size", f.Size));
                        counters.Added++;
                    }
                    else
                    {
                        Exec(conn, tx, "UPDATE image_paths SET hash = $hash, mtime_ticks = $mtime, size = $size, missing_since = NULL WHERE path = $path",
                            ("$hash", h.Hash), ("$mtime", f.MtimeTicks), ("$size", f.Size), ("$path", h.Existing.Path));
                        if (!string.Equals(h.Existing.Hash, h.Hash, StringComparison.Ordinal)) counters.Changed++;
                    }
                    Exec(conn, tx, "DELETE FROM file_issues WHERE path = $path", ("$path", f.Path));
                    break;

                case UnsupportedResult u:
                    counters.Unsupported++;
                    UpsertIssue(conn, tx, folderId, f, FileIssueKind.Unsupported, u.Message, now);
                    MarkNoLongerAnImage(conn, tx, u.Existing, now);
                    break;

                case UnreadableResult r:
                    counters.Unreadable++;
                    if (r.Permanent) UpsertIssue(conn, tx, folderId, f, FileIssueKind.Unreadable, r.Message, now);
                    else counters.TransientlyUnreadable++;   // 共有違反など。記録せず次回に回す
                    break;

                case DeferredResult:
                    counters.TransientlyUnreadable++;   // 書き込みの途中かもしれない。記録せず次回に回す
                    break;

                case VanishedResult:
                    break;

                default:
                    throw new InvalidOperationException("未知の結果の型: " + result.GetType().Name);
            }
        }
    }

    private static void UpsertIssue(SqliteConnection conn, SqliteTransaction tx, long folderId, FoundFile f, FileIssueKind kind, string message, string now) =>
        Exec(conn, tx,
            "INSERT INTO file_issues(path, folder_id, kind, message, mtime_ticks, size, updated_at) " +
            "VALUES ($path, $folder, $kind, $message, $mtime, $size, $now) " +
            "ON CONFLICT(path) DO UPDATE SET kind = excluded.kind, message = excluded.message, mtime_ticks = excluded.mtime_ticks, size = excluded.size, updated_at = excluded.updated_at",
            ("$path", f.Path), ("$folder", folderId), ("$kind", DbEnums.ToDb(kind)), ("$message", message), ("$mtime", f.MtimeTicks), ("$size", f.Size), ("$now", now));

    /// <summary>以前は画像だったパスの中身が、画像でなくなった: 行は消さず、missing_since を付ける(検索の対象から外れる)。</summary>
    private static void MarkNoLongerAnImage(SqliteConnection conn, SqliteTransaction tx, PathRow? existing, string now)
    {
        if (existing is null) return;
        Exec(conn, tx, "UPDATE image_paths SET missing_since = $now WHERE path = $path AND missing_since IS NULL", ("$now", now), ("$path", existing.Path));
    }

    // ---- 読み取り

    private sealed record PathRow(string Path, string Hash, long MtimeTicks, long Size, string? MissingSince);

    private sealed record IssueRow(string Path, FileIssueKind Kind, long MtimeTicks, long Size);

    private List<(long Id, string Path)> LoadFolders(IReadOnlyCollection<long>? folderIds)
    {
        using SqliteConnection conn = _db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, enabled FROM folders ORDER BY id";
        var all = new List<(long Id, string Path, bool Enabled)>();
        using (SqliteDataReader r = cmd.ExecuteReader())
            while (r.Read()) all.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2) == 1));

        if (folderIds is null) return all.Where(f => f.Enabled).Select(f => (f.Id, f.Path)).ToList();

        var list = new List<(long Id, string Path)>();
        foreach (long id in folderIds.Distinct())
        {
            var found = all.FirstOrDefault(f => f.Id == id);
            if (found == default) throw new FolderRegistrationException($"フォルダ id {id} は登録されていない");
            list.Add((found.Id, found.Path));
        }
        return list;
    }

    private Dictionary<string, PathRow> LoadPathRows(long folderId)
    {
        using SqliteConnection conn = _db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, hash, mtime_ticks, size, missing_since FROM image_paths WHERE folder_id = $id";
        cmd.Parameters.AddWithValue("$id", folderId);
        var map = new Dictionary<string, PathRow>(StringComparer.Ordinal);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new PathRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4));
            map[PathKey.Normalize(row.Path)] = row;
        }
        return map;
    }

    private Dictionary<string, IssueRow> LoadIssueRows(long folderId)
    {
        using SqliteConnection conn = _db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, kind, mtime_ticks, size FROM file_issues WHERE folder_id = $id";
        cmd.Parameters.AddWithValue("$id", folderId);
        var map = new Dictionary<string, IssueRow>(StringComparer.Ordinal);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new IssueRow(r.GetString(0), DbEnums.FileIssueKindFromDb(r.GetString(1)), r.GetInt64(2), r.GetInt64(3));
            map[PathKey.Normalize(row.Path)] = row;
        }
        return map;
    }

    // ---- 補助

    private string Now() => DbTime.Format(_time.GetUtcNow());

    private static string? FormatLastError(List<UnreachablePlace> unreachable)
    {
        if (unreachable.Count == 0) return null;
        var sb = new StringBuilder();
        sb.Append("到達できなかった場所 ").Append(unreachable.Count).Append(" 件(配下は今回の走査の対象外):");
        foreach (UnreachablePlace u in unreachable.Take(LastErrorMaxEntries))
            sb.Append('\n').Append(u.Directory).Append(" — ").Append(u.Reason);
        if (unreachable.Count > LastErrorMaxEntries) sb.Append("\n…ほか ").Append(unreachable.Count - LastErrorMaxEntries).Append(" 件");
        return sb.ToString();
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private sealed class Counters
    {
        private readonly object _gate = new();
        private int _filesSeen, _hashed, _added, _changed, _missing, _unreadable, _unsupported, _unreachable, _skippedLinks, _transientlyUnreadable;

        public int FilesSeen { get { lock (_gate) return _filesSeen; } set { lock (_gate) _filesSeen = value; } }
        public int Hashed { get { lock (_gate) return _hashed; } set { lock (_gate) _hashed = value; } }
        public int Added { get { lock (_gate) return _added; } set { lock (_gate) _added = value; } }
        public int Changed { get { lock (_gate) return _changed; } set { lock (_gate) _changed = value; } }
        public int Missing { get { lock (_gate) return _missing; } set { lock (_gate) _missing = value; } }
        public int Unreadable { get { lock (_gate) return _unreadable; } set { lock (_gate) _unreadable = value; } }
        public int Unsupported { get { lock (_gate) return _unsupported; } set { lock (_gate) _unsupported = value; } }
        public int Unreachable { get { lock (_gate) return _unreachable; } set { lock (_gate) _unreachable = value; } }
        public int SkippedLinks { get { lock (_gate) return _skippedLinks; } set { lock (_gate) _skippedLinks = value; } }
        public int TransientlyUnreadable { get { lock (_gate) return _transientlyUnreadable; } set { lock (_gate) _transientlyUnreadable = value; } }

        public void Add(Counters other)
        {
            Added += other.Added; Changed += other.Changed; Missing += other.Missing;
            Unreadable += other.Unreadable; Unsupported += other.Unsupported; Unreachable += other.Unreachable; SkippedLinks += other.SkippedLinks;
            TransientlyUnreadable += other.TransientlyUnreadable;
        }

        public ScanProgress ToProgress(long folderId) => new(folderId, FilesSeen, Hashed, Added, Changed, Missing, Unreadable, Unreachable, SkippedLinks);
    }
}
