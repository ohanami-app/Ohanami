using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Core;

namespace Ohanami.Bench;

/// <summary>1 つの known-item の、経路ごとの順位(<paramref name="Path"/> = "fused" か経路の PathId)。順位は同点の平均順位(1 始まり、小数になりうる)。経路の母集団に入っていなければ null。</summary>
internal sealed record KnownItemRow(string Set, string Query, string Filename, string Path, double? Rank);

/// <summary>precision@10 の 1 クエリ × 1 経路。Top は上位 10 件(最大)のファイル名(順位順)。</summary>
internal sealed record PrecisionRow(string Query, string Path, int Relevant, int JudgedMiss, int Unjudged, int Returned, IReadOnlyList<string> Top);

/// <summary>known-item の集計(1 つの集合 × 1 つの経路)。上位 n 位以内は「順位 ≤ n」(同点の平均順位をそのまま比べる)。</summary>
internal sealed record KnownItemSummary(string Set, string Path, int Count, int Missing, int Top1, int Top5, int Top20, double Median);

/// <summary>precision@10 の集計(1 つの経路)。分母は常に 10 × クエリ数(経路が 10 件返せなければ、足りない分は外れ)。未判定は外れとして数える(下限)。</summary>
internal sealed record PrecisionSummary(string Path, int Queries, int Relevant, int JudgedMiss, int Unjudged, int Returned)
{
    public int Slots => Queries * Metrics.PrecisionTop;

    /// <summary>未判定を外れとして数えた平均(下限)。</summary>
    public double Lower => Slots == 0 ? 0 : (double)Relevant / Slots;

    /// <summary>未判定がすべて当たりだったときの平均(上限)。</summary>
    public double Upper => Slots == 0 ? 0 : (double)(Relevant + Unjudged) / Slots;

    /// <summary>
    /// 返した分の当たり率(当たり ÷ 返した件数。未判定は外れとして数えた下限)。分母は経路が実際に返した件数(最大 10 × クエリ数)で、
    /// <see cref="Lower"/>(分母は常に 10 × クエリ数)と違い、経路が少ししか返さなくても薄まらない。何も返していなければ NaN。
    /// 全文は、かなの同一視やあいまい一致で返す件数が増えるので、この値の変化が偽陽性の量そのもの。
    /// </summary>
    public double HitRateLower => Returned == 0 ? double.NaN : (double)Relevant / Returned;

    /// <summary>未判定がすべて当たりだったときの、返した分の当たり率(上限)。何も返していなければ NaN。</summary>
    public double HitRateUpper => Returned == 0 ? double.NaN : (double)(Relevant + Unjudged) / Returned;

    /// <summary>判定済みの枠の数(当たり + 判定済みの外れ)。</summary>
    public int JudgedSlots => Relevant + JudgedMiss;

    /// <summary>判定済みの枠だけで数えた当たり率(未判定の枠は分母にも入れない)。判定済みの枠が無ければ NaN。<see cref="Lower"/>(未判定は外れ)と並べて見る。</summary>
    public double JudgedOnly => JudgedSlots == 0 ? double.NaN : (double)Relevant / JudgedSlots;
}

/// <summary>
/// 検索の結果(<see cref="SearchResponse"/>。全件を含む = Limit が母集団以上)から、known-item の順位と precision@10 を出す。DB も GPU も使わない純粋な計算。
/// 画像の識別はファイル名(NFC)。同じファイル名が 2 つの結果にあれば例外(フォルダが入れ子でファイル名が重なる場合などは、この道具では測れない)。
/// </summary>
internal static class Metrics
{
    /// <summary>融合の後の順位を表す経路名。</summary>
    public const string Fused = "fused";

    public const int PrecisionTop = 10;

    public static string FileName(string path) => BenchInputs.Nfc(System.IO.Path.GetFileName(path));

    /// <summary>結果ごとの (正規化したファイル名 → 結果) の表。同じファイル名が 2 つの結果にあれば例外。</summary>
    public static Dictionary<string, SearchResult> IndexByFileName(SearchResponse response)
    {
        var map = new Dictionary<string, SearchResult>(StringComparer.Ordinal);
        foreach (SearchResult r in response.Results)
        {
            foreach (string name in r.Paths.Select(FileName).Distinct(StringComparer.Ordinal))
            {
                if (!map.TryAdd(name, r))
                    throw new InvalidDataException($"ファイル名「{name}」が複数の画像にある(入れ子のフォルダなど)。この道具はファイル名で画像を指すので測れない。");
            }
        }
        return map;
    }

    /// <summary>
    /// 目標のファイルの、融合後と経路ごとの順位。経路の並びは response.Paths の順(融合が先頭)。
    /// 結果に目標が無ければ例外(全件を返す検索なので、無いのは未索引か表記の違い。黙って圏外にしない)。
    /// </summary>
    public static List<(string Path, double? Rank)> RanksOf(SearchResponse response, IReadOnlyDictionary<string, SearchResult> byName, string filename)
    {
        if (!byName.TryGetValue(BenchInputs.Nfc(filename), out SearchResult? hit))
            throw new InvalidDataException($"「{filename}」が検索の結果に無い(クエリ「{response.Query}」。未索引か、ファイル名の違い)。");
        var ranks = new List<(string, double?)> { (Fused, hit.Rank) };
        foreach (PathOutcome p in response.Paths)
        {
            PathHit? h = hit.Hits.FirstOrDefault(x => string.Equals(x.PathId, p.PathId, StringComparison.Ordinal));
            ranks.Add((p.PathId, h?.Rank));
        }
        return ranks;
    }

    public static IEnumerable<KnownItemRow> KnownItemRows(string set, string query, string filename, SearchResponse response, IReadOnlyDictionary<string, SearchResult> byName) =>
        RanksOf(response, byName, filename).Select(r => new KnownItemRow(set, query, filename, r.Path, r.Rank));

    /// <summary>一覧に出す経路名(融合 + 結果の経路)。</summary>
    public static List<string> PathNames(SearchResponse response) => [Fused, .. response.Paths.Select(p => p.PathId)];

    /// <summary>
    /// 1 つのクエリの、ある経路の上位 <see cref="PrecisionTop"/> 件(良い順)。<paramref name="pathId"/> が <see cref="Fused"/> なら融合後の順(response.Results の順)、そうでなければその経路の順位
    /// (Hits の Rank。同点はファイル名の序数順(評価の記録(spikes/S2-siglip2)の「画像の番号」と同じ並びで、数字を比べられるようにする))。precision@10 と、スコアの行の「上位 10 件か」が同じ取り方になるように、ここ 1 か所で決める。
    /// </summary>
    public static List<SearchResult> TopOf(SearchResponse response, string pathId)
    {
        IEnumerable<SearchResult> ordered = pathId == Fused
            ? response.Results
            : response.Results
                .Select((r, i) => (Result: r, Index: i, Hit: r.Hits.FirstOrDefault(h => h.PathId == pathId)))
                .Where(x => x.Hit is not null)
                // 同点の中は、ファイル名の順(評価の記録の「画像の番号」= ファイル名の序数順と同じ)。他の経路の順位(融合の並び)を持ち込まない: その経路だけの precision@10 を測るため
                .OrderBy(x => x.Hit!.Rank).ThenBy(x => x.Result.Paths.Select(FileName).Order(StringComparer.Ordinal).First(), StringComparer.Ordinal)
                .Select(x => x.Result);
        return ordered.Take(PrecisionTop).ToList();
    }

    /// <summary>
    /// 1 つのクエリの、ある経路の上位 10 件の判定。<paramref name="pathId"/> が <see cref="Fused"/> なら融合後の順(response.Results の順)、そうでなければその経路の順位
    /// (Hits の Rank。同点はファイル名の序数順)で上位 10 件を取る。判定は qrels(その画像のどれかのファイル名が判定済みなら採用。同じ画像の別名で判定が食い違えば例外)。
    /// </summary>
    public static PrecisionRow Precision(SearchResponse response, string pathId, string query, Qrels qrels)
    {
        List<SearchResult> top = TopOf(response, pathId);

        int rel = 0, miss = 0, unj = 0;
        var names = new List<string>();
        foreach (SearchResult r in top)
        {
            (string name, bool? judged) = Judge(r, query, qrels);
            names.Add(name);
            if (judged is null) unj++;
            else if (judged.Value) rel++;
            else miss++;
        }
        return new PrecisionRow(query, pathId, rel, miss, unj, top.Count, names);
    }

    /// <summary>
    /// 1 つの結果(画像)の、そのクエリでの判定。<c>Name</c> は画像を指すファイル名(別名のうち序数順で先頭)、<c>Judged</c> は qrels で当たりなら true・外れなら false・載っていなければ null(未判定)。
    /// 同じ画像の別名で判定が食い違えば例外。
    /// </summary>
    public static (string Name, bool? Judged) Judge(SearchResult r, string query, Qrels qrels)
    {
        string[] files = r.Paths.Select(FileName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidDataException("パスの無い結果がある(missing_since の画像は結果に入らないはず)");
        bool[] judged = files.Select(f => qrels.TryGet(query, f)).Where(j => j is not null).Select(j => j!.Value).Distinct().ToArray();
        if (judged.Length > 1) throw new InvalidDataException($"同じ画像の別名で qrels の判定が食い違う(クエリ「{query}」: {string.Join("、", files)})");
        return (files[0], judged.Length == 0 ? null : judged[0]);
    }

    /// <summary>
    /// 1 つのクエリの、スコアの行(<see cref="ScoreRow"/>): <paramref name="files"/>(そのクエリの qrels の画像と、そのクエリを使う known-item の目的の画像。ファイル名)ごとに、
    /// 融合後と各経路(response.Paths の順)の 1 行。結果に無いファイルは例外(<see cref="RanksOf"/> と同じ。全件を返す検索なので、無いのは未索引か表記の違い)。
    /// <paramref name="targetSets"/> はファイル名 → そのファイルを目的にする集合名(このクエリのもの)。
    /// </summary>
    public static List<ScoreRow> ScoreRows(string query, SearchResponse response, IReadOnlyDictionary<string, SearchResult> byName, IEnumerable<string> files,
        IReadOnlyDictionary<string, string> targetSets, Qrels qrels)
    {
        var rows = new List<ScoreRow>();
        List<string> paths = PathNames(response);
        // 経路ごとの、上位 10 件のハッシュと 1 位のスコア(クエリごとに 1 回)
        var top10 = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var top1 = new Dictionary<string, double?>(StringComparer.Ordinal);
        var corrected = new Dictionary<string, bool?>(StringComparer.Ordinal) { [Fused] = null };
        foreach (PathOutcome outcome in response.Paths) corrected[outcome.PathId] = outcome.ScoreCorrected;
        foreach (string path in paths)
        {
            top10[path] = TopOf(response, path).Select(r => r.Hash).ToHashSet(StringComparer.Ordinal);
            top1[path] = path == Fused
                ? (response.Results.Count == 0 ? null : response.Results.Max(r => r.FusedScore))
                : response.Results.SelectMany(r => r.Hits).Where(h => h.PathId == path).Select(h => (double?)h.Score).Max();
        }
        foreach (string file in files.Select(BenchInputs.Nfc).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!byName.TryGetValue(file, out SearchResult? hit))
                throw new InvalidDataException($"「{file}」が検索の結果に無い(クエリ「{query}」。未索引か、ファイル名の違い)。");
            string targets = targetSets.GetValueOrDefault(file) ?? "";
            bool? judged = qrels.TryGet(query, file);
            foreach (string path in paths)
            {
                double? score, rank;
                if (path == Fused) { score = hit.FusedScore; rank = hit.Rank; }
                else
                {
                    PathHit? h = hit.Hits.FirstOrDefault(x => string.Equals(x.PathId, path, StringComparison.Ordinal));
                    score = h?.Score; rank = h?.Rank;
                }
                rows.Add(new ScoreRow(query, file, targets, judged, path, score, rank, top10[path].Contains(hit.Hash), top1[path], corrected[path]));
            }
        }
        return rows;
    }

    public static KnownItemSummary Summarize(string set, string path, IEnumerable<KnownItemRow> rows)
    {
        List<KnownItemRow> mine = rows.Where(r => r.Set == set && r.Path == path).ToList();
        // 順位が無い(経路の母集団に入っていない)ものは、どの「上位 n 位以内」にも数えない
        List<double> ranks = mine.Where(r => r.Rank is not null).Select(r => r.Rank!.Value).OrderBy(x => x).ToList();
        int n = ranks.Count;
        double median = n == 0 ? double.NaN : n % 2 == 1 ? ranks[n / 2] : (ranks[n / 2 - 1] + ranks[n / 2]) / 2.0;
        return new KnownItemSummary(set, path, mine.Count, mine.Count - n,
            ranks.Count(r => r <= 1), ranks.Count(r => r <= 5), ranks.Count(r => r <= 20), median);
    }

    public static PrecisionSummary Summarize(string path, IEnumerable<PrecisionRow> rows)
    {
        List<PrecisionRow> mine = rows.Where(r => r.Path == path).ToList();
        return new PrecisionSummary(path, mine.Count, mine.Sum(r => r.Relevant), mine.Sum(r => r.JudgedMiss), mine.Sum(r => r.Unjudged), mine.Sum(r => r.Returned));
    }
}
