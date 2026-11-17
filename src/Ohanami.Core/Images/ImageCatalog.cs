using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.Tags;

namespace Ohanami.Core;

/// <summary>
/// 画像 1 枚の詳細と、失敗の一覧の読み出し。**読むだけ**(書き込まない)。<c>Indexer</c> の外に置く(ジョブの状態を持たない)。
/// <list type="bullet">
/// <item><see cref="GetDetailAsync"/>: 画像のパス・寸法・サイズ・段階ごとの状態・現行の caption・手動タグを、**1 つの読み取りのトランザクションの中で**(同じ時点で)読む。
///   画像が無ければ null(検索のあとで、見つからない画像の記録が消された場合など)。</item>
/// <item><see cref="ListFailuresAsync"/>: 段階ごとの失敗の行(新しい順、上限つき)。件数は <c>StageSummary.Failed</c> と同じ定義(stage_status の failed の行。現存するパスの有無は問わない)。</item>
/// </list>
/// 段階の「現行の model_id」(Done と Outdated の分かれ目)は、作るときに明示的に渡す(<c>Indexer</c> のランナーと同じ値)。
/// </summary>
public sealed class ImageCatalog
{
    private readonly OhanamiDatabase _db;
    private readonly IReadOnlyDictionary<Stage, string> _currentModelIds;

    internal ImageCatalog(OhanamiDatabase db, IReadOnlyDictionary<Stage, string> currentModelIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(currentModelIds);
        foreach (Stage stage in Enum.GetValues<Stage>())
        {
            if (!currentModelIds.ContainsKey(stage)) throw new ArgumentException($"段階 {stage} の現行の model_id が無い", nameof(currentModelIds));
        }
        _db = db;
        _currentModelIds = currentModelIds;
    }

    // ---------------------------------------------------------------- 詳細

    /// <summary>
    /// 画像 1 枚の詳細。<paramref name="hash"/> は 64 桁の小文字の 16 進(違えば <see cref="ArgumentException"/>)。images に行が無ければ null。
    /// </summary>
    public Task<ImageDetail?> GetDetailAsync(string hash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (!IsHash(hash)) throw new ArgumentException($"画像のハッシュは 64 桁の小文字の 16 進(実際「{hash}」)", nameof(hash));
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _db.ReadSnapshot(conn => ReadDetail(conn, hash));
        }, ct);
    }

    private ImageDetail? ReadDetail(SqliteConnection conn, string hash)
    {
        long size;
        string format;
        int? width, height;
        DateTimeOffset firstSeen;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT size, format, width, height, first_seen_at FROM images WHERE hash = $h";
            cmd.Parameters.AddWithValue("$h", hash);
            using SqliteDataReader r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            size = r.GetInt64(0);
            format = r.GetString(1);
            width = r.IsDBNull(2) ? null : (int)r.GetInt64(2);
            height = r.IsDBNull(3) ? null : (int)r.GetInt64(3);
            firstSeen = DbTime.Parse(r.GetString(4));
        }

        var present = new List<ImagePathDetail>();
        var missing = new List<ImagePathDetail>();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT path, mtime_ticks, size, missing_since FROM image_paths WHERE hash = $h ORDER BY path COLLATE NOCASE, path";
            cmd.Parameters.AddWithValue("$h", hash);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                bool isMissing = !r.IsDBNull(3);
                var path = new ImagePathDetail(
                    r.GetString(0), isMissing, isMissing ? DbTime.Parse(r.GetString(3)) : null,
                    new DateTimeOffset(r.GetInt64(1), TimeSpan.Zero), r.GetInt64(2));
                (isMissing ? missing : present).Add(path);
            }
        }

        var stages = new List<StageDetail>();
        foreach (Stage stage in Enum.GetValues<Stage>())
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status, model_id, error, attempts, updated_at FROM stage_status WHERE hash = $h AND stage = $s";
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$s", DbEnums.ToDb(stage));
            using SqliteDataReader r = cmd.ExecuteReader();
            if (!r.Read())
            {
                stages.Add(new StageDetail(stage, StageState.Pending, null, null, 0, null));
                continue;
            }
            string modelId = r.GetString(1);
            StageState state = DbEnums.StageStatusFromDb(r.GetString(0)) switch
            {
                StageStatus.Failed => StageState.Failed,
                StageStatus.Done => string.Equals(modelId, _currentModelIds[stage], StringComparison.Ordinal) ? StageState.Done : StageState.Outdated,
                var other => throw new InvalidOperationException($"想定外の段階の状態 {other}"),
            };
            stages.Add(new StageDetail(stage, state, modelId, r.IsDBNull(2) ? null : r.GetString(2), (int)r.GetInt64(3), DbTime.Parse(r.GetString(4))));
        }

        CaptionRow? row = CaptionStore.FindLatest(conn, hash);
        CaptionDetail? caption = row is null ? null : new CaptionDetail(row.ModelId, row.Ocr, row.Description, row.Tags, DbTime.Parse(row.CreatedAt));
        IReadOnlyList<string> manualTags = ManualTags.ForImage(conn, null, hash);

        return new ImageDetail(hash, size, format, width, height, firstSeen, [.. present, .. missing], stages, caption, manualTags);
    }

    private static bool IsHash(string s) =>
        s.Length == 64 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    // ---------------------------------------------------------------- 失敗の一覧

    /// <summary>失敗の一覧の、既定の上限(件数)。</summary>
    public const int DefaultFailureLimit = 200;

    /// <summary>
    /// 段階の失敗の一覧(stage_status の failed の行)。新しい順(updated_at が新しいものから。同じなら hash 順)で、先頭から <paramref name="limit"/> 件まで。
    /// <see cref="FailureList.Total"/> は上限で切る前の数(<c>StageSummary.Failed</c> と同じ)。件数と一覧は同じ読み取りのトランザクションで読む。
    /// </summary>
    public Task<FailureList> ListFailuresAsync(Stage stage, int limit = DefaultFailureLimit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _db.ReadSnapshot(conn =>
            {
                int total;
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT count(*) FROM stage_status WHERE stage = $s AND status = $failed";
                    cmd.Parameters.AddWithValue("$s", DbEnums.ToDb(stage));
                    cmd.Parameters.AddWithValue("$failed", DbEnums.ToDb(StageStatus.Failed));
                    total = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                var items = new List<FailureItem>();
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT s.hash,
                               (SELECT p.path FROM image_paths p WHERE p.hash = s.hash AND p.missing_since IS NULL ORDER BY p.path COLLATE NOCASE, p.path LIMIT 1),
                               s.error, s.attempts, s.updated_at, s.model_id
                        FROM stage_status s
                        WHERE s.stage = $s AND s.status = $failed
                        ORDER BY s.updated_at DESC, s.hash
                        LIMIT $limit
                        """;
                    cmd.Parameters.AddWithValue("$s", DbEnums.ToDb(stage));
                    cmd.Parameters.AddWithValue("$failed", DbEnums.ToDb(StageStatus.Failed));
                    cmd.Parameters.AddWithValue("$limit", limit);
                    using SqliteDataReader r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        items.Add(new FailureItem(
                            r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                            (int)r.GetInt64(3), DbTime.Parse(r.GetString(4)), r.GetString(5)));
                    }
                }
                return new FailureList(stage, items, total);
            });
        }, ct);
    }
}
