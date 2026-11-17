using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;

namespace Ohanami.Core.Tags;

/// <summary>手動タグの候補 1 件。<paramref name="ImageCount"/> は、そのタグが付いている画像の数(現存するパスの有無は問わない)。</summary>
public sealed record TagCandidate(string Name, int ImageCount);

/// <summary>
/// 手動タグ(ADR 0036)。利用者が画像に付ける名前で、全文検索の索引に入る(索引文書の 4 つ目の欄)。
/// <list type="bullet">
/// <item>保存は <c>tags</c> / <c>image_tags</c>(スキーマ v1 から有る表)を、<c>kind = manual</c>・<c>source = manual</c> で使う。名前は <see cref="ManualTagName.Normalize"/> で整える
///   (決まりに合わなければ <see cref="InvalidTagNameException"/>。何も書かない)。同じ名前(ASCII の大文字・小文字を区別しない)は 1 つの tags の行に付ける
///   (すでにある名前と大文字・小文字だけ違う名前を付けると、すでにある表記のほうに付く)。</item>
/// <item><b>付け外しと索引の更新は 1 トランザクション</b>(<see cref="FtsIndex.TryIndexImage"/>)。複数の画像にまとめて付けるのも 1 トランザクション(途中で失敗すれば何も付かない)。
///   索引は派生データなので、索引の都合でタグを捨てない: 索引が Failed(作り直しに失敗している)のときは、タグだけ保存して索引の行は作らず、警告をログに出す
///   (<see cref="CaptionStore"/> と同じ扱い。索引が直れば、作り直しで captions とタグから入る)。</item>
/// <item>どの画像にも付いていない tags の行は、外したときに消す(一覧に死んだタグを残さない)。</item>
/// <item>画像が無い(images に行が無い)ハッシュは <see cref="ArgumentException"/>(黙って何もしない、にしない)。</item>
/// </list>
/// 画像の詳細(ImageDetail)に手動タグを入れるのは詳細の読み出しの側(ここは <see cref="GetAsync"/> を使う)。
/// </summary>
public sealed class ManualTags
{
    private readonly OhanamiDatabase _db;
    private readonly FtsIndex _index;

    internal ManualTags(OhanamiDatabase db, FtsIndex index)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(index);
        _db = db;
        _index = index;
    }

    // ---------------------------------------------------------------- 付ける・外す

    /// <summary>1 枚にタグを付ける。付けた(それまで付いていなかった)なら true、すでに付いていたなら false。</summary>
    public async Task<bool> AddAsync(string hash, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        return await AddToManyAsync([hash], name, ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// 選んだ複数の画像に、同じタグをまとめて付ける。**新しく付いた画像の数**を返す(すでに付いていた画像は数えない)。
    /// 1 トランザクション: 1 枚でも画像が無ければ何も付けずに例外。ハッシュの重複は 1 回だけ付ける。
    /// </summary>
    public async Task<int> AddToManyAsync(IReadOnlyCollection<string> hashes, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        string normalized = ManualTagName.Normalize(name);
        string[] distinct = hashes.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0) return 0;
        foreach (string h in distinct) ArgumentException.ThrowIfNullOrWhiteSpace(h, nameof(hashes));

        bool indexUsable = await _index.TryEnsureUsableAsync(ct).ConfigureAwait(false);
        string? problem = indexUsable ? null : IndexFailedMessage;
        int added = await _db.WriteAsync((conn, tx) =>
        {
            if (indexUsable) _index.RequireCurrent(conn, tx);
            foreach (string h in distinct) RequireImage(conn, tx, h);
            long tagId = FindOrCreateTag(conn, tx, normalized);
            int count = 0;
            foreach (string h in distinct)
            {
                if (!Attach(conn, tx, h, tagId)) continue;
                count++;
                if (indexUsable && _index.TryIndexImage(conn, tx, h) is { } why) problem ??= why;
            }
            return count;
        }, CancellationToken.None).ConfigureAwait(false);

        WarnIfNotIndexed(problem, normalized);
        return added;
    }

    /// <summary>1 枚からタグを外す。外した(付いていた)なら true、付いていなかった(タグが無い)なら false。そのタグがどの画像にも付かなくなれば、tags の行も消す。</summary>
    public async Task<bool> RemoveAsync(string hash, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        string normalized = ManualTagName.Normalize(name);

        bool indexUsable = await _index.TryEnsureUsableAsync(ct).ConfigureAwait(false);
        string? problem = indexUsable ? null : IndexFailedMessage;
        bool removed = await _db.WriteAsync((conn, tx) =>
        {
            if (indexUsable) _index.RequireCurrent(conn, tx);
            RequireImage(conn, tx, hash);
            long? tagId = FindTag(conn, tx, normalized);
            if (tagId is null) return false;
            if (!Detach(conn, tx, hash, tagId.Value)) return false;
            DeleteTagIfUnused(conn, tx, tagId.Value);
            if (indexUsable) problem = _index.TryIndexImage(conn, tx, hash);
            return true;
        }, CancellationToken.None).ConfigureAwait(false);

        WarnIfNotIndexed(problem, normalized);
        return removed;
    }

    private const string IndexFailedMessage = "索引が Failed の状態(作り直しに失敗している)";

    private void WarnIfNotIndexed(string? problem, string tag)
    {
        if (problem is null) return;
        _index.Log?.Warning($"手動タグ「{tag}」は保存したが、全文検索の索引は更新できなかった: {problem}");
    }

    // ---------------------------------------------------------------- 読む(画面用)

    /// <summary>画像に付いている手動タグの名前(名前順: 大文字・小文字を区別しない順、同じなら文字コード順)。無ければ空。</summary>
    public Task<IReadOnlyList<string>> GetAsync(string hash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            using SqliteConnection conn = _db.CreateReadConnection();
            return ForImage(conn, null, hash);
        }, ct);
    }

    /// <summary>
    /// タグの候補(使われている数の多い順。同じ数なら名前順)。<paramref name="limit"/> は返す件数の上限(1 以上)。
    /// <paramref name="exclude"/> に渡した名前(正規化して比べる)は除く(その画像にすでに付いているタグを出さないため)。
    /// </summary>
    public Task<IReadOnlyList<TagCandidate>> GetCandidatesAsync(int limit = 20, IEnumerable<string>? exclude = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (exclude is not null) foreach (string e in exclude) excluded.Add(ManualTagName.Normalize(e));
        return Task.Run<IReadOnlyList<TagCandidate>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            using SqliteConnection conn = _db.CreateReadConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT t.name, count(*) AS n
                FROM tags t JOIN image_tags it ON it.tag_id = t.id AND it.source = 'manual'
                WHERE t.kind = 'manual'
                GROUP BY t.id
                ORDER BY n DESC, t.name COLLATE NOCASE, t.name
                """;
            var list = new List<TagCandidate>();
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read() && list.Count < limit)
            {
                string name = r.GetString(0);
                if (excluded.Contains(name)) continue;
                list.Add(new TagCandidate(name, (int)r.GetInt64(1)));
            }
            return list;
        }, ct);
    }

    // ---------------------------------------------------------------- 索引・検索の詳細が使う読み出し(接続を受ける)

    private const string NameOrder = "ORDER BY t.name COLLATE NOCASE, t.name";

    /// <summary>画像の手動タグの名前(<see cref="GetAsync"/> と同じ並び)。索引文書の 4 つ目の欄の元になる。</summary>
    internal static IReadOnlyList<string> ForImage(SqliteConnection conn, SqliteTransaction? tx, string hash)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT t.name FROM image_tags it JOIN tags t ON t.id = it.tag_id
            WHERE it.hash = $h AND it.source = 'manual' AND t.kind = 'manual'
            {NameOrder}
            """;
        cmd.Parameters.AddWithValue("$h", hash);
        var list = new List<string>();
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>手動タグのある全部の画像の、手動タグの名前(索引の作り直し用)。タグの無い画像は入らない。</summary>
    internal static Dictionary<string, List<string>> ReadAll(SqliteConnection conn, SqliteTransaction? tx)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT it.hash, t.name FROM image_tags it JOIN tags t ON t.id = it.tag_id
            WHERE it.source = 'manual' AND t.kind = 'manual'
            ORDER BY it.hash, t.name COLLATE NOCASE, t.name
            """;
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string hash = r.GetString(0);
            if (!map.TryGetValue(hash, out List<string>? list)) map[hash] = list = [];
            list.Add(r.GetString(1));
        }
        return map;
    }

    /// <summary>複数の画像の手動タグ(検索結果の詳細用)。<see cref="CaptionStore.ReadChunk"/> 件ずつ引く。タグの無い画像は結果に入らない。</summary>
    internal static Dictionary<string, IReadOnlyList<string>> ForImages(SqliteConnection conn, IEnumerable<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (string[] chunk in hashes.Distinct(StringComparer.Ordinal).Chunk(Captions.CaptionStore.ReadChunk))
        {
            using SqliteCommand cmd = conn.CreateCommand();
            var names = new string[chunk.Length];
            for (int i = 0; i < chunk.Length; i++)
            {
                names[i] = "$h" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.AddWithValue(names[i], chunk[i]);
            }
            cmd.CommandText = $"""
                SELECT it.hash, t.name FROM image_tags it JOIN tags t ON t.id = it.tag_id
                WHERE it.hash IN ({string.Join(",", names)}) AND it.source = 'manual' AND t.kind = 'manual'
                ORDER BY it.hash, t.name COLLATE NOCASE, t.name
                """;
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string hash = r.GetString(0);
                if (!map.TryGetValue(hash, out List<string>? list)) map[hash] = list = [];
                list.Add(r.GetString(1));
            }
            foreach ((string hash, List<string> list) in map) result[hash] = list;
        }
        return result;
    }

    // ---------------------------------------------------------------- 書き込みの部品(書き込みのトランザクションの中)

    private static void RequireImage(SqliteConnection conn, SqliteTransaction tx, string hash)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM images WHERE hash = $h";
        cmd.Parameters.AddWithValue("$h", hash);
        if (cmd.ExecuteScalar() is null) throw new ArgumentException($"画像が無い(images に行が無い): {hash}", nameof(hash));
    }

    private static long? FindTag(SqliteConnection conn, SqliteTransaction tx, string normalizedName)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT id FROM tags WHERE name = $n COLLATE NOCASE AND kind = '{DbEnums.ToDb(TagKind.Manual)}'";
        cmd.Parameters.AddWithValue("$n", normalizedName);
        object? v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private static long FindOrCreateTag(SqliteConnection conn, SqliteTransaction tx, string normalizedName)
    {
        if (FindTag(conn, tx, normalizedName) is { } existing) return existing;
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO tags(name, kind) VALUES ($n, $k) RETURNING id";
        cmd.Parameters.AddWithValue("$n", normalizedName);
        cmd.Parameters.AddWithValue("$k", DbEnums.ToDb(TagKind.Manual));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>image_tags に行を足す。足したなら true、すでにあれば false。</summary>
    private static bool Attach(SqliteConnection conn, SqliteTransaction tx, string hash, long tagId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO image_tags(hash, tag_id, source, score) VALUES ($h, $t, $s, NULL)";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.Parameters.AddWithValue("$s", DbEnums.ToDb(TagSource.Manual));
        return cmd.ExecuteNonQuery() == 1;
    }

    private static bool Detach(SqliteConnection conn, SqliteTransaction tx, string hash, long tagId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM image_tags WHERE hash = $h AND tag_id = $t AND source = $s";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.Parameters.AddWithValue("$s", DbEnums.ToDb(TagSource.Manual));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>どの image_tags の行からも指されていない tags の行を消す(source を問わない。将来の Propagated が付いていれば残る)。</summary>
    private static void DeleteTagIfUnused(SqliteConnection conn, SqliteTransaction tx, long tagId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM tags WHERE id = $t AND NOT EXISTS (SELECT 1 FROM image_tags WHERE tag_id = $t)";
        cmd.Parameters.AddWithValue("$t", tagId);
        cmd.ExecuteNonQuery();
    }
}
