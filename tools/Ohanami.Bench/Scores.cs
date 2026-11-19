using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Ohanami.Core;
using Ohanami.Core.Search;

namespace Ohanami.Bench;

/// <summary>
/// 1 つの (クエリ, 画像) の、1 つの経路でのスコアと順位(<c>scores.tsv</c> の 1 行)。
/// 対象は、その クエリの qrels に載っている画像(判定済みの全組)と、そのクエリを使う known-item の目的の画像。
/// <paramref name="Path"/> は <see cref="Metrics.Fused"/> か経路の PathId。fused のスコアは RRF の合計(<see cref="SearchResult.FusedScore"/>)。
/// 経路の母集団に入っていない画像は <paramref name="Score"/>・<paramref name="Rank"/> が null(見た目の経路は全画像が入るので、常に値を持つ)。
/// <paramref name="Targets"/> は、その画像を目的にする known-item の集合名(visual / dialogue)を '|' でつないだもの(目的でなければ空)。
/// <paramref name="Judged"/> は qrels の判定(当たり = true。qrels に無ければ null)。
/// <paramref name="Top10"/> はその経路の上位 10 件に入っていたか(precision@10 と同じ取り方: <see cref="Metrics.TopOf"/>)。
/// <paramref name="PathTop1"/> はそのクエリでのその経路の 1 位のスコア(経路が何も返さなければ null)。1 位との差を、この行だけから出せるように持つ。
/// </summary>
internal sealed record ScoreRow(string Query, string Filename, string Targets, bool? Judged, string Path, double? Score, double? Rank, bool Top10, double? PathTop1);

/// <summary>段階の名前と、かかった秒数(run の所要時間の記録)。</summary>
internal sealed record StageTime(string Name, double Seconds);

/// <summary>段階ごとの所要時間を測る(<see cref="Mark"/> を呼ぶたびに、前の Mark からの秒数を 1 段階として記録する)。時計は差し替えられる(テスト用)。</summary>
internal sealed class StageClock
{
    private readonly Func<double> _seconds;
    private readonly double _start;
    private double _last;
    private readonly List<StageTime> _stages = [];

    public StageClock(Func<double>? seconds = null)
    {
        if (seconds is null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            seconds = () => sw.Elapsed.TotalSeconds;
        }
        _seconds = seconds;
        _start = _last = _seconds();
    }

    /// <summary>前の Mark(または開始)から今までを、<paramref name="name"/> の段階として記録する。</summary>
    public void Mark(string name)
    {
        double now = _seconds();
        _stages.Add(new StageTime(name, now - _last));
        _last = now;
    }

    public IReadOnlyList<StageTime> Stages => _stages;

    /// <summary>開始から最後の Mark までの合計。</summary>
    public double Total => _last - _start;
}

/// <summary>値の並びの要約(件数・最小・10%・25%・中央値・75%・90%・最大)。分位点は昇順に並べた値の線形補間(R の type 7)。</summary>
internal sealed record ScoreQuantiles(int Count, double Min, double P10, double P25, double Median, double P75, double P90, double Max)
{
    /// <summary>空なら null。NaN・無限大が入っていれば例外(黙って分布を歪めない)。</summary>
    public static ScoreQuantiles? Describe(IEnumerable<double> values)
    {
        double[] v = values.ToArray();
        if (v.Length == 0) return null;
        if (v.Any(x => double.IsNaN(x) || double.IsInfinity(x))) throw new ArgumentException("NaN か無限大の値がある");
        Array.Sort(v);
        return new ScoreQuantiles(v.Length, v[0], Quantile(v, 0.10), Quantile(v, 0.25), Quantile(v, 0.50), Quantile(v, 0.75), Quantile(v, 0.90), v[^1]);
    }

    /// <summary>昇順の値の p 分位点(0〜1)。位置 (n-1)·p の前後を線形に補間する。</summary>
    public static double Quantile(double[] sorted, double p)
    {
        if (sorted.Length == 0) throw new ArgumentException("空", nameof(sorted));
        double pos = (sorted.Length - 1) * p;
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}

/// <summary>スコアの行(<see cref="ScoreRow"/>)の要約の文章と、文字のヒストグラム。DB も GPU も使わない純粋な計算。</summary>
internal static class ScoreReport
{
    public const string VisualPath = "visual";
    public const string FulltextPath = "fulltext";

    /// <summary>ヒストグラムの棒の最大の長さ(文字数)。</summary>
    private const int BarWidth = 40;

    /// <summary>ヒストグラムの行数の上限(これを超える範囲なら、幅を 2 倍ずつ広げる)。</summary>
    private const int MaxBins = 40;

    // ------------------------------------------------------------------ 集める(ScoreRow → 母集団ごとの値)

    /// <summary>見た目の経路の行(スコアがある行だけ)。</summary>
    private static IEnumerable<ScoreRow> Of(IReadOnlyList<ScoreRow> rows, string path) => rows.Where(r => r.Path == path && r.Score is not null);

    private static bool IsTarget(ScoreRow r, string set) => r.Targets.Split('|', StringSplitOptions.RemoveEmptyEntries).Contains(set, StringComparer.Ordinal);

    /// <summary>
    /// 経路のスコアの母集団。(クエリ, 画像) の組ごとに 1 件。
    /// 当たり / 外れ = qrels の判定、「上位 10」= その経路の上位 10 件に入っていたもの
    /// (ADR 0034 の線の置き方が使う母集団: 守る集合は「当たり・上位 10」、除く効き目を測る母集団は「外れ・上位 10」)。
    /// </summary>
    internal static List<(string Label, List<double> Values)> Populations(IReadOnlyList<ScoreRow> rows, string path)
    {
        List<ScoreRow> r = Of(rows, path).ToList();
        var fusedRank = rows.Where(x => x.Path == Metrics.Fused && x.Rank is not null).ToDictionary(x => (x.Query, x.Filename), x => x.Rank!.Value);
        List<double> S(IEnumerable<ScoreRow> xs) => xs.Select(x => x.Score!.Value).ToList();
        return
        [
            ("判定が当たり", S(r.Where(x => x.Judged == true))),
            ("判定が外れ", S(r.Where(x => x.Judged == false))),
            ("当たりで、この経路の上位10", S(r.Where(x => x.Judged == true && x.Top10))),
            ("外れで、この経路の上位10", S(r.Where(x => x.Judged == false && x.Top10))),
            ("known-item の目的(見た目)", S(r.Where(x => IsTarget(x, "visual")))),
            ("known-item の目的(セリフ)", S(r.Where(x => IsTarget(x, "dialogue")))),
            // 追加の known-item(known-items-one-image.tsv)は、あるときだけ別の行で出す
            .. BenchInputs.OptionalSets.Where(set => rows.Any(x => IsTarget(x, set))).Select(set => ($"known-item の目的({set})", S(r.Where(x => IsTarget(x, set))))),
            // 守り 2 の (a): 今の上位 300 件(画面に出る範囲 = SearchQuery.Limit の既定)に入っている目的の画像
            ("目的で、融合の上位300件以内", S(r.Where(x => x.Targets.Length > 0 && fusedRank.TryGetValue((x.Query, x.Filename), out double fr) && fr <= TopScreen))),
        ];
    }

    /// <summary>画面に出る上位の件数(<see cref="SearchQuery.Limit"/> の既定。ADR 0034 の線の置き方では、この件数に入る known-item の目的の画像を守る)。</summary>
    public const int TopScreen = 300;

    /// <summary>クエリごとの、その経路の 1 位のスコア(クエリの数だけ。1 件も返さなかったクエリは入らない)。</summary>
    internal static List<double> QueryTop1(IReadOnlyList<ScoreRow> rows, string path) =>
        rows.Where(r => r.Path == path && r.PathTop1 is not null).GroupBy(r => r.Query, StringComparer.Ordinal).Select(g => g.First().PathTop1!.Value).ToList();

    /// <summary>known-item の目的の画像ごとの、その経路の「1 位のスコア − 目的の画像のスコア」(0 以上。0 なら目的の画像が 1 位)。</summary>
    internal static List<double> TargetGapToTop1(IReadOnlyList<ScoreRow> rows, string path, string? set = null) =>
        Of(rows, path).Where(r => r.PathTop1 is not null && (set is null ? r.Targets.Length > 0 : IsTarget(r, set))).Select(r => r.PathTop1!.Value - r.Score!.Value).ToList();

    // ------------------------------------------------------------------ 文章

    /// <summary>
    /// run の summary に付ける、スコアの要約(経路ごと。見た目の経路は点のヒストグラムつき)。行が無ければ空の文字列。
    /// 見た目の経路の点は、順位を決めた点(ADR 0051)。補正をかけた検索語(<see cref="HubnessGate.InScope"/>。製品と同じ関門)では cosine − μ·r、かけない検索語では cosine で、
    /// 水準が違うので、2 つの群に分けて出す。
    /// </summary>
    public static string Render(IReadOnlyList<ScoreRow> rows)
    {
        if (rows.Count == 0) return "";
        var sb = new StringBuilder();
        int pairs = rows.Count(x => x.Path == Metrics.Fused);
        sb.AppendLine($"[経路ごとのスコア(scores.tsv): qrels の判定済みの組と known-item の目的の画像、延べ {pairs} 組。各行は 件数 / 最小 / 10% / 25% / 中央値 / 75% / 90% / 最大]");

        // ---- 見た目の経路の点(補正をかけた検索語とかけない検索語は、水準が違うので分ける)
        if (Of(rows, VisualPath).Any())
        {
            sb.AppendLine("  見た目の経路の点(全画像に値がある。順位を決めた点。補正をかけた検索語とかけない検索語で水準が違うので、2 つの群に分け、同じ群の中でだけ並べる)");
            foreach (bool corrected in new[] { true, false })
            {
                List<ScoreRow> group = [.. rows.Where(r => r.Path != VisualPath || HubnessGate.InScope(r.Query) == corrected)];
                if (!Of(group, VisualPath).Any()) continue;
                int queries = Of(group, VisualPath).Select(r => r.Query).Distinct(StringComparer.Ordinal).Count();
                sb.AppendLine(corrected
                    ? $"  [補正をかけた検索語: 日本語の文字を含む {HubnessGate.MaxCodePoints} 文字以下。点 = cosine − μ·r。{queries} クエリ]"
                    : $"  [補正をかけない検索語: 点 = cosine。{queries} クエリ]");
                RenderVisualGroup(sb, group);
            }
        }

        // ---- 全文の経路(スコアは BM25 で、クエリごとに尺度が違う。プールした分布は目安。入っていた数が主)
        if (rows.Any(r => r.Path == FulltextPath))
        {
            List<ScoreRow> all = rows.Where(r => r.Path == FulltextPath).ToList();
            sb.AppendLine("  全文の経路のスコア(BM25 の符号を反転した値。クエリごとに尺度が違うので、プールした分布は目安。「入っていた」= この経路の母集団に入った組の数)");
            void Line(string label, Func<ScoreRow, bool> pick)
            {
                List<ScoreRow> g = all.Where(pick).ToList();
                int inPath = g.Count(x => x.Score is not null);
                sb.AppendLine($"    {label}: 入っていた {inPath}/{g.Count}");
                if (inPath > 0) sb.AppendLine(DistributionLine("      スコア", g.Where(x => x.Score is not null).Select(x => x.Score!.Value).ToList(), "F3"));
            }
            Line("判定が当たり", x => x.Judged == true);
            Line("判定が外れ", x => x.Judged == false);
            Line("known-item の目的(見た目)", x => IsTarget(x, "visual"));
            Line("known-item の目的(セリフ)", x => IsTarget(x, "dialogue"));
            foreach (string set in BenchInputs.OptionalSets.Where(set => all.Any(x => IsTarget(x, set)))) Line($"known-item の目的({set})", x => IsTarget(x, set));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>見た目の経路の点の要約(1 つの群の分)。</summary>
    private static void RenderVisualGroup(StringBuilder sb, IReadOnlyList<ScoreRow> rows)
    {
        var pops = Populations(rows, VisualPath);
        foreach ((string label, List<double> values) in pops) sb.AppendLine(DistributionLine(label, values, "F4"));
        List<double> top1 = QueryTop1(rows, VisualPath);
        sb.AppendLine(DistributionLine($"各クエリの 1 位の点({top1.Count} クエリ)", top1, "F4"));
        sb.AppendLine(DistributionLine("1 位との差: known-item の目的(見た目)", TargetGapToTop1(rows, VisualPath, "visual"), "F4"));
        sb.AppendLine(DistributionLine("1 位との差: known-item の目的(セリフ)", TargetGapToTop1(rows, VisualPath, "dialogue"), "F4"));
        foreach (string set in BenchInputs.OptionalSets.Where(set => rows.Any(x => IsTarget(x, set))))
            sb.AppendLine(DistributionLine($"1 位との差: known-item の目的({set})", TargetGapToTop1(rows, VisualPath, set), "F4"));
        int top1Targets = TargetGapToTop1(rows, VisualPath).Count(g => g <= 0);
        sb.AppendLine($"    目的の画像が見た目の経路の 1 位だった数: {top1Targets}");
        sb.AppendLine();
        List<(string Label, List<double> Values)> shown = pops.Where(p => p.Label is "判定が当たり" or "判定が外れ" or "当たりで、この経路の上位10" or "外れで、この経路の上位10").ToList();
        shown.Add(("known-item の目的(全集合)", pops.Where(p => p.Label.StartsWith("known-item", StringComparison.Ordinal)).SelectMany(p => p.Values).ToList()));
        sb.Append(Histograms(shown));
    }

    private static string DistributionLine(string label, IReadOnlyCollection<double> values, string format)
    {
        ScoreQuantiles? d = ScoreQuantiles.Describe(values);
        if (d is null) return $"    {label}: 0 件";
        string F(double x) => x.ToString(format, CultureInfo.InvariantCulture);
        return $"    {label}: n={d.Count}  {F(d.Min)} / {F(d.P10)} / {F(d.P25)} / {F(d.Median)} / {F(d.P75)} / {F(d.P90)} / {F(d.Max)}";
    }

    // ------------------------------------------------------------------ ヒストグラム

    /// <summary>
    /// 幅の決め方: 0.01 を基本にして、範囲が <see cref="MaxBins"/> 行を超えるなら 2 倍ずつ広げる。始点は幅の倍数に切り下げる(母集団どうしで区切りをそろえる)。
    /// </summary>
    internal static (double Width, double Start, int Bins) BinLayout(double min, double max)
    {
        double width = 0.01;
        while (true)
        {
            double start = Math.Floor(min / width + 1e-9) * width;
            int bins = (int)Math.Floor((max - start) / width + 1e-9) + 1;
            if (bins <= MaxBins) return (width, start, bins);
            width *= 2;
        }
    }

    /// <summary>
    /// 母集団ごとの文字のヒストグラム(縦に区間、横に棒と件数)。区間は母集団どうしでそろえる(全母集団の値の範囲)。
    /// 棒の長さはその母集団の最大の区間を <see cref="BarWidth"/> 文字として相対で決める(件数が少ない母集団でも形が見える)。件数は数字で並べる。
    /// </summary>
    internal static string Histograms(IReadOnlyList<(string Label, List<double> Values)> pops)
    {
        List<double> every = pops.SelectMany(p => p.Values).ToList();
        if (every.Count == 0) return "";
        (double width, double start, int bins) = BinLayout(every.Min(), every.Max());
        var sb = new StringBuilder();
        sb.AppendLine($"  見た目の経路の点のヒストグラム(区間の幅 {width.ToString("0.###", CultureInfo.InvariantCulture)}。区間は [下端, 上端)。棒の長さは母集団ごとの最大が {BarWidth} 文字)");
        foreach ((string label, List<double> values) in pops)
        {
            sb.AppendLine($"    {label}(n={values.Count})");
            if (values.Count == 0) continue;
            int[] counts = Bin(values, width, start, bins);
            int max = counts.Max();
            for (int i = 0; i < bins; i++)
            {
                double lo = start + i * width;
                int len = counts[i] == 0 ? 0 : Math.Max(1, (int)Math.Round((double)counts[i] / max * BarWidth, MidpointRounding.AwayFromZero));
                sb.AppendLine($"      {lo.ToString("F2", CultureInfo.InvariantCulture),6}〜 {new string('#', len),-BarWidth} {counts[i]}");
            }
        }
        return sb.ToString();
    }

    /// <summary>値を区間に数える。区間は [start + i·width, start + (i+1)·width)。範囲外が 1 つでもあれば例外(区間の決め方の誤りを黙って落とさない)。</summary>
    internal static int[] Bin(IEnumerable<double> values, double width, double start, int bins)
    {
        var counts = new int[bins];
        foreach (double v in values)
        {
            int i = (int)Math.Floor((v - start) / width + 1e-9);
            if (i < 0 || i >= bins) throw new ArgumentOutOfRangeException(nameof(values), $"値 {v} が区間(開始 {start}、幅 {width}、{bins} 個)の外");
            counts[i]++;
        }
        return counts;
    }

    // ------------------------------------------------------------------ 所要時間

    /// <summary>段階ごとの所要時間と合計の文章。段階が無ければ空。</summary>
    public static string RenderTimings(IReadOnlyList<StageTime>? stages)
    {
        if (stages is null || stages.Count == 0) return "";
        var sb = new StringBuilder();
        double total = stages.Sum(s => s.Seconds);
        sb.AppendLine("[所要時間(段階ごと。入力の読み込みから集計まで。この文章の書き出しと保存は含まない)]");
        foreach (StageTime s in stages) sb.AppendLine($"  {s.Name}: {FormatSeconds(s.Seconds)}");
        sb.AppendLine($"  合計: {FormatSeconds(total)}");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string FormatSeconds(double s) => s.ToString("F1", CultureInfo.InvariantCulture) + " 秒";
}
