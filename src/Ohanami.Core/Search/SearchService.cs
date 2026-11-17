using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Search;

namespace Ohanami.Core;

/// <summary>
/// 検索。経路の配列(明示的)の結果を RRF(k = 60 固定、同点は平均順位)で融合し、
/// 上位 <see cref="SearchQuery.Limit"/> 件を返す。公開 API はすべて任意のスレッドから同時に呼んでよい。
/// <list type="bullet">
/// <item>結果に入るのは、missing_since の無いパスが 1 つ以上ある画像だけ(DB の印だけで決める。ファイルの実在は確認しない)。母集団から<b>順位を付ける前に</b>除くので、
///   順位は詰まったまま(抜けない)。</item>
/// <item>Limit は融合の<b>後</b>に適用する。経路ごとに上位 N 件に切ってから融合しない(評価と同じ条件にするため。ADR 0033)。</item>
/// <item>準備中の経路(ベクトルを読み込み中など)は <see cref="PathState.NotReady"/> として報告し、結果には入れない。この検索で失敗した経路は
///   <see cref="PathState.Failed"/>(理由つき)で、他の経路の結果で続ける。キャンセルは例外のまま。</item>
/// <item><see cref="SearchResult.Hits"/> は、経路の配列の順(順位順ではない)。その画像が母集団に入っている経路だけ。</item>
/// </list>
/// </summary>
public sealed class SearchService
{
    /// <summary>RRF の k(固定)。調整すると評価に過適合するため、本実装でも固定。</summary>
    internal const int RrfK = 60;

    private const int SqlParameterChunk = 500;

    private readonly OhanamiDatabase _db;
    private readonly ISearchPath[] _paths;
    private readonly VectorTable _vectors;

    /// <param name="db">DB(結果に出してよい画像とそのパスを読む)。</param>
    /// <param name="paths">経路の配列。順序が <see cref="SearchResult.Hits"/> と <see cref="SearchResponse.Paths"/> の順。</param>
    /// <param name="vectors">現行の埋め込みモデルのメモリ上のベクトルの表(<see cref="SearchResponse.IndexedImages"/> の数え方に使う)。</param>
    internal SearchService(OhanamiDatabase db, IReadOnlyList<ISearchPath> paths, VectorTable vectors)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(vectors);
        if (paths.Count == 0) throw new ArgumentException("経路が無い", nameof(paths));
        if (paths.Select(p => p.PathId).Distinct(StringComparer.Ordinal).Count() != paths.Count) throw new ArgumentException("経路の PathId が重複している", nameof(paths));
        _db = db;
        _paths = [.. paths];
        _vectors = vectors;
    }

    /// <summary>経路の一覧(PathId と表示名)。</summary>
    public IReadOnlyList<SearchPathInfo> Paths => _paths.Select(p => new SearchPathInfo(p.PathId, p.DisplayName)).ToArray();

    public async Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Text)) throw new ArgumentException("検索のテキストが空", nameof(query));
        if (query.Limit < 1) throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "Limit は 1 以上");
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        // 結果に出してよい画像(missing_since の無いパスが 1 つ以上ある)。母集団はここで絞る
        HashSet<string> visible = await Task.Run(LoadVisibleHashes, ct).ConfigureAwait(false);

        // 経路は並行に動かす。失敗はその経路だけの Failed
        PathRun[] runs = await Task.WhenAll(_paths.Select(p => RunPathAsync(p, query.Text, ct))).ConfigureAwait(false);

        // クエリごとの「ハッシュ → 添字」の表: 母集団(可視のものだけ)の和
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var hashes = new List<string>();
        foreach (PathRun run in runs.Where(r => r.Result is { State: PathState.Ok }))
        {
            foreach (string h in run.Result!.Hashes)
            {
                if (visible.Contains(h) && indexOf.TryAdd(h, hashes.Count)) hashes.Add(h);
            }
        }

        // 経路ごとの RankedPath(添字ごとにスコア。可視の画像だけ)と、添字 → その経路の結果の中の位置
        int n = hashes.Count;
        var okRuns = new List<PathRun>();
        var ranked = new List<RankedPath>();
        var positions = new List<int[]>();
        var outcomes = new List<PathOutcome>();   // 経路の配列の順(Ok でない経路も同じ順で入れる)
        foreach (PathRun run in runs)
        {
            if (run.Result is not { State: PathState.Ok } ok)
            {
                outcomes.Add(new PathOutcome(run.Path.PathId, run.Path.ModelId, 0, run.State, run.Message));
                continue;
            }
            int[] pos = new int[n];
            Array.Fill(pos, -1);
            var hits = new List<(int, double)>();
            for (int i = 0; i < ok.Hashes.Count; i++)
            {
                if (!indexOf.TryGetValue(ok.Hashes[i], out int idx)) continue; // 不可視
                pos[idx] = i;
                hits.Add((idx, ok.Scores[i]));
            }
            okRuns.Add(run);
            ranked.Add(RankedPath.Sparse(n, hits));
            positions.Add(pos);
            outcomes.Add(new PathOutcome(run.Path.PathId, run.Path.ModelId, hits.Count, PathState.Ok, ok.Message));
        }

        var results = new List<SearchResult>();
        if (ranked.Count > 0 && n > 0)
        {
            RankedPath fused = RankedPath.Rrf(ranked, RrfK);
            IReadOnlyList<int> top = fused.Top(query.Limit);
            // 上位が決まったあと・DetailOf の前に、経路ごとに 1 回だけ、結果に載る画像の位置を知らせる(経路が詳細をバッチで読むため)
            for (int j = 0; j < okRuns.Count; j++)
            {
                int[] indices = top.Where(i => ranked[j].Present[i]).Select(i => positions[j][i]).ToArray();
                okRuns[j].Result!.PrepareDetails(indices);
            }
            Dictionary<string, List<string>> pathsOf = await Task.Run(() => LoadPaths(top.Select(i => hashes[i])), ct).ConfigureAwait(false);
            foreach (int i in top)
            {
                var hitsOfImage = new List<PathHit>();
                for (int j = 0; j < okRuns.Count; j++)
                {
                    if (!ranked[j].Present[i]) continue;
                    hitsOfImage.Add(new PathHit(okRuns[j].Path.PathId, okRuns[j].Path.ModelId, ranked[j].MeanRank[i], ranked[j].Score[i],
                        okRuns[j].Result!.DetailOf(positions[j][i])));
                }
                string hash = hashes[i];
                results.Add(new SearchResult(hash, pathsOf.TryGetValue(hash, out List<string>? p) ? p : [], fused.MeanRank[i], fused.Score[i], hitsOfImage));
            }
        }

        int indexed = visible.Count(_vectors.Contains);
        return new SearchResponse(query.Text, results, outcomes, indexed, visible.Count, sw.Elapsed);
    }

    private sealed record PathRun(ISearchPath Path, PathSearchResult? Result, PathState State, string? Message);

    private static async Task<PathRun> RunPathAsync(ISearchPath path, string text, CancellationToken ct)
    {
        try
        {
            PathSearchResult r = await path.SearchAsync(text, ct).ConfigureAwait(false);
            return new PathRun(path, r, r.State, r.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new PathRun(path, null, PathState.Failed, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 全文検索の索引(<c>search_text</c>)に文書が<b>まだ無い</b>画像(結果の画面の「文字の索引がまだ無い(VLM の前)」の表示の材料)。
    /// 索引に文書があるのは、caption のある画像と、手動タグのある画像(ADR 0036)なので、VLM の前の画像(手動タグも無い)がここに入る。
    /// <paramref name="hashes"/> のうち、文書が無いものを返す(順序は入力の順。入力に無いハッシュは返さない)。索引を作り直している最中は、文書が一時的に無いので、
    /// 呼び出し側は全文の経路の状態(<see cref="PathOutcome.State"/>)が Ok のときだけ使う。
    /// </summary>
    public async Task<IReadOnlyList<string>> FindWithoutTextIndexAsync(IReadOnlyList<string> hashes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            var indexed = new HashSet<string>(StringComparer.Ordinal);
            using SqliteConnection conn = _db.CreateReadConnection();
            foreach (string[] chunk in hashes.Distinct(StringComparer.Ordinal).Chunk(SqlParameterChunk))
            {
                ct.ThrowIfCancellationRequested();
                using SqliteCommand cmd = conn.CreateCommand();
                var sql = new StringBuilder("SELECT hash FROM search_text WHERE hash IN (");
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (i > 0) sql.Append(',');
                    sql.Append("$h").Append(i);
                    cmd.Parameters.AddWithValue("$h" + i, chunk[i]);
                }
                sql.Append(')');
                cmd.CommandText = sql.ToString();
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read()) indexed.Add(r.GetString(0));
            }
            return (IReadOnlyList<string>)hashes.Where(h => !indexed.Contains(h)).Distinct(StringComparer.Ordinal).ToArray();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 検索していないときの一覧(ADR 0041): 今あるパス(missing_since が無い)のある画像を、ファイルの更新日時(<c>image_paths.mtime_ticks</c>。
    /// 今あるパスのうち最も新しいもの。<c>images.first_seen_at</c> は、最初の走査で取り込んだ画像が全部同じ日時になるので使わない)の新しい順に、上位 <paramref name="limit"/> 件(既定は上限なし = 全部。App の新しい順の一覧は、上限なしで呼ぶ。ADR 0041。
    /// 時間とメモリは画像の数に比例する: 合成の DB で、5 万枚で約 0.1 秒・約 20MB、1 万枚で約 0.02 秒・約 4MB。上限は読み出しを軽くしない=全パスを読んで並べる)。
    /// 検索の母集団と同じ画像(見つからない画像は出さない)で、埋め込みや VLM の前の画像も入る。
    /// <b>同じ日時のときの並び</b>: 並べた日時のもとになったパスの降順(大文字小文字を区別しない序数順。同じなら序数順)。パスは重複しないので並びは一意に決まる
    /// (連番のファイル名を同じ更新日時でコピーしたとき、番号の大きいほうが先に出る。「新しい順」と同じ向き)。パスが複数ある画像は、一番新しいパスの日時で並べ、
    /// 日時が同じパスが複数あれば、その中でパスの降順で先頭のものを代表にする。<see cref="SearchAsync"/> は変えない(空のクエリは例外)。
    /// </summary>
    public async Task<RecentResponse> ListRecentAsync(int limit = int.MaxValue, CancellationToken ct = default)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit は 1 以上");
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        (List<RecentImage> images, int total) = await Task.Run(() => LoadRecent(limit, ct), ct).ConfigureAwait(false);
        return new RecentResponse(images, total, sw.Elapsed);
    }

    private (List<RecentImage> Images, int Total) LoadRecent(int limit, CancellationToken ct)
    {
        // 今あるパスを全部読む(画像 1 万枚でも数万行)。画像ごとに「一番新しいパス」を選ぶ規則(日時の降順、同じならパスの降順)が SQL だけだと書きにくいので、C# で決める
        var byHash = new Dictionary<string, List<(string Path, long Ticks)>>(StringComparer.Ordinal);
        using (SqliteConnection conn = _db.CreateReadConnection())
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hash, path, mtime_ticks FROM image_paths WHERE missing_since IS NULL";
            using SqliteDataReader r = cmd.ExecuteReader();
            int n = 0;
            while (r.Read())
            {
                if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
                string hash = r.GetString(0);
                if (!byHash.TryGetValue(hash, out List<(string, long)>? list)) byHash[hash] = list = [];
                list.Add((r.GetString(1), r.GetInt64(2)));
            }
        }

        // 画像ごとの代表のパス(日時が最も新しい。同じ日時ならパスの降順で先頭)
        var reps = new List<(string Hash, string Path, long Ticks)>(byHash.Count);
        foreach ((string hash, List<(string Path, long Ticks)> paths) in byHash)
        {
            (string Path, long Ticks) best = paths[0];
            for (int i = 1; i < paths.Count; i++)
                if (RecentOrder.Compare(paths[i].Ticks, paths[i].Path, best.Ticks, best.Path) < 0) best = paths[i];
            reps.Add((hash, best.Path, best.Ticks));
        }
        reps.Sort((a, b) => RecentOrder.Compare(a.Ticks, a.Path, b.Ticks, b.Path));

        var images = new List<RecentImage>(Math.Min(limit, reps.Count));
        foreach ((string hash, string repPath, long ticks) in reps.Take(limit))
        {
            // 先頭は代表のパス、残りはパスの昇順(大文字小文字を区別しない序数順。検索の結果のパスの並びと同じ向き)
            List<string> rest = byHash[hash].Select(p => p.Path).Where(p => !string.Equals(p, repPath, StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.Ordinal).ToList();
            images.Add(new RecentImage(hash, [repPath, .. rest], new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc))));
        }
        return (images, reps.Count);
    }

    /// <summary>新しい順の並べ方(<see cref="ListRecentAsync"/>)。負なら a が先。日時の降順、同じならパスの降順(大文字小文字を区別しない序数順、同じなら序数順)。</summary>
    internal static class RecentOrder
    {
        public static int Compare(long ticksA, string pathA, long ticksB, string pathB)
        {
            int byTime = ticksB.CompareTo(ticksA);
            if (byTime != 0) return byTime;
            int byPath = string.Compare(pathB, pathA, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : string.Compare(pathB, pathA, StringComparison.Ordinal);
        }
    }

    private HashSet<string> LoadVisibleHashes()
    {
        using SqliteConnection conn = _db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT hash FROM image_paths WHERE missing_since IS NULL";
        using SqliteDataReader r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>ハッシュごとの、missing_since の無いパス(パスの昇順)。</summary>
    private Dictionary<string, List<string>> LoadPaths(IEnumerable<string> hashes)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using SqliteConnection conn = _db.CreateReadConnection();
        foreach (string[] chunk in hashes.Chunk(SqlParameterChunk))
        {
            using SqliteCommand cmd = conn.CreateCommand();
            var sql = new StringBuilder("SELECT hash, path FROM image_paths WHERE missing_since IS NULL AND hash IN (");
            for (int i = 0; i < chunk.Length; i++)
            {
                if (i > 0) sql.Append(',');
                sql.Append("$h").Append(i);
                cmd.Parameters.AddWithValue("$h" + i, chunk[i]);
            }
            sql.Append(") ORDER BY hash, path");
            cmd.CommandText = sql.ToString();
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string hash = r.GetString(0);
                if (!map.TryGetValue(hash, out List<string>? list)) map[hash] = list = [];
                list.Add(r.GetString(1));
            }
        }
        return map;
    }
}
