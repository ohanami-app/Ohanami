using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ohanami.Bench;

/// <summary>基準線と今回で、順位が下がった known-item。</summary>
internal sealed record KnownItemDrop(string Set, string Query, string Filename, string Path, double? Before, double? After, bool LeftTop20)
{
    /// <summary>下がった幅(圏外になったときは大きい値として並べる)。</summary>
    public double Delta => (After ?? double.PositiveInfinity) - (Before ?? double.PositiveInfinity);
}

/// <summary>基準線と今回で、当たりの数が減った precision@10 のクエリ。</summary>
internal sealed record PrecisionDrop(string Query, string Path, int Before, int After, int UnjudgedBefore, int UnjudgedAfter);

/// <summary>比較の結果。「下がったクエリ」の一覧と、集計の変化。</summary>
internal sealed class Comparison
{
    public required IReadOnlyList<KnownItemDrop> KnownItemDrops { get; init; }
    public required IReadOnlyList<KnownItemRow> KnownItemImproved { get; init; }
    public required IReadOnlyList<PrecisionDrop> PrecisionDrops { get; init; }
    public required IReadOnlyList<PrecisionDrop> PrecisionImproved { get; init; }

    /// <summary>両方にある (集合, 経路) の集計: 基準線と今回。</summary>
    public required IReadOnlyList<(KnownItemSummary Before, KnownItemSummary After)> KnownItemSummaries { get; init; }
    public required IReadOnlyList<(PrecisionSummary Before, PrecisionSummary After)> PrecisionSummaries { get; init; }

    /// <summary>片方にしかない経路やクエリ(比較から外したもの)。</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    /// <summary>集計が基準線より下がったか(上位 20 位以内の数、または precision@10 の当たりの数が、どれかの経路で減った)。</summary>
    public bool AggregateWorse => KnownItemSummaries.Any(s => s.After.Top20 < s.Before.Top20) || PrecisionSummaries.Any(s => s.After.Relevant < s.Before.Relevant);

    /// <summary>順位の比較で、同じ値とみなす幅(同点の平均順位の浮動小数の誤差)。</summary>
    private const double Epsilon = 1e-9;

    /// <summary>両方にある known-item(集合 × クエリ × ファイル × 経路)の順位と、precision@10(クエリ × 経路)を比べる。</summary>
    public static Comparison Compare(RunData baseline, RunData current)
    {
        var skipped = new List<string>();

        // known-item
        var baseKnown = baseline.KnownItems.ToDictionary(r => (r.Set, r.Query, r.Filename, r.Path));
        var drops = new List<KnownItemDrop>();
        var improved = new List<KnownItemRow>();
        var commonKeys = new HashSet<(string, string, string, string)>();
        foreach (KnownItemRow now in current.KnownItems)
        {
            if (!baseKnown.TryGetValue((now.Set, now.Query, now.Filename, now.Path), out KnownItemRow? before)) continue;
            commonKeys.Add((now.Set, now.Query, now.Filename, now.Path));
            double b = before.Rank ?? double.PositiveInfinity, a = now.Rank ?? double.PositiveInfinity;
            if (a > b + Epsilon) drops.Add(new KnownItemDrop(now.Set, now.Query, now.Filename, now.Path, before.Rank, now.Rank, b <= 20 && a > 20));
            else if (a < b - Epsilon) improved.Add(now);
        }
        foreach (string p in baseline.KnownItems.Select(r => r.Path).Distinct().Except(current.KnownItems.Select(r => r.Path).Distinct()))
            skipped.Add($"known-item: 経路「{p}」は今回の結果に無い(比較しない)");
        foreach (string p in current.KnownItems.Select(r => r.Path).Distinct().Except(baseline.KnownItems.Select(r => r.Path).Distinct()))
            skipped.Add($"known-item: 経路「{p}」は基準線に無い(比較しない)");
        int onlyOne = baseline.KnownItems.Count(r => !commonKeys.Contains((r.Set, r.Query, r.Filename, r.Path)))
                    + current.KnownItems.Count(r => !baseKnown.ContainsKey((r.Set, r.Query, r.Filename, r.Path)));
        // 基準線に集合ごと無い行(古い基準線に one-image の行が無い、など)は「新しい行」: 下がったとは数えず、例外にもしない。比べるのは両方にある行だけ
        var baseSets = baseline.KnownItems.Select(r => r.Set).ToHashSet(StringComparer.Ordinal);
        foreach (var g in current.KnownItems.Where(r => !baseSets.Contains(r.Set)).GroupBy(r => r.Set, StringComparer.Ordinal))
            skipped.Add($"known-item: 集合「{g.Key}」は基準線に無い(新しい行 {g.Count()} 行。比較しない。この集合を含む基準線を取ったあとから比べる)");
        foreach (var g in baseline.KnownItems.Where(r => !current.KnownItems.Any(c => c.Set == r.Set)).GroupBy(r => r.Set, StringComparer.Ordinal))
            skipped.Add($"known-item: 集合「{g.Key}」は今回の結果に無い({g.Count()} 行。比較しない)");
        int newSetRows = current.KnownItems.Count(r => !baseSets.Contains(r.Set));
        int goneSetRows = baseline.KnownItems.Count(r => !current.KnownItems.Any(c => c.Set == r.Set));
        onlyOne -= newSetRows + goneSetRows;   // 集合ごと片方に無い行は、上で別に書いた
        if (onlyOne > 0) skipped.Add($"known-item: 片方にしかない行が {onlyOne} 行ある(クエリの集合が変わった)。比較から外した");

        // precision
        var basePrec = baseline.Precision.ToDictionary(r => (r.Query, r.Path));
        var pdrops = new List<PrecisionDrop>();
        var pimproved = new List<PrecisionDrop>();
        foreach (PrecisionRow now in current.Precision)
        {
            if (!basePrec.TryGetValue((now.Query, now.Path), out PrecisionRow? before)) continue;
            var d = new PrecisionDrop(now.Query, now.Path, before.Relevant, now.Relevant, before.Unjudged, now.Unjudged);
            if (now.Relevant < before.Relevant) pdrops.Add(d);
            else if (now.Relevant > before.Relevant) pimproved.Add(d);
        }
        foreach (string p in baseline.Precision.Select(r => r.Path).Distinct().Except(current.Precision.Select(r => r.Path).Distinct()))
            skipped.Add($"precision@10: 経路「{p}」は今回の結果に無い(比較しない)");
        foreach (string p in current.Precision.Select(r => r.Path).Distinct().Except(baseline.Precision.Select(r => r.Path).Distinct()))
            skipped.Add($"precision@10: 経路「{p}」は基準線に無い(比較しない)");

        // 集計(両方にある (集合, 経路) を、両方にあるクエリだけで比べる: クエリの集合が変わっても同じ土俵で)
        var kSummaries = new List<(KnownItemSummary, KnownItemSummary)>();
        foreach ((string set, string path) in current.KnownItems.Select(r => (r.Set, r.Path)).Distinct())
        {
            var b = baseline.KnownItems.Where(r => commonKeys.Contains((r.Set, r.Query, r.Filename, r.Path)));
            var c = current.KnownItems.Where(r => commonKeys.Contains((r.Set, r.Query, r.Filename, r.Path)));
            if (!b.Any(r => r.Set == set && r.Path == path)) continue;
            kSummaries.Add((Metrics.Summarize(set, path, b), Metrics.Summarize(set, path, c)));
        }
        var pSummaries = new List<(PrecisionSummary, PrecisionSummary)>();
        var commonQ = current.Precision.Where(r => basePrec.ContainsKey((r.Query, r.Path))).Select(r => (r.Query, r.Path)).ToHashSet();
        foreach (string path in current.Precision.Select(r => r.Path).Distinct())
        {
            if (!commonQ.Any(x => x.Path == path)) continue;
            pSummaries.Add((Metrics.Summarize(path, baseline.Precision.Where(r => commonQ.Contains((r.Query, r.Path)))),
                            Metrics.Summarize(path, current.Precision.Where(r => commonQ.Contains((r.Query, r.Path))))));
        }

        return new Comparison
        {
            KnownItemDrops = drops.OrderByDescending(d => d.LeftTop20).ThenByDescending(d => d.Delta).ToList(),
            KnownItemImproved = improved,
            PrecisionDrops = pdrops.OrderBy(d => d.After - d.Before).ThenBy(d => d.Query, StringComparer.Ordinal).ToList(),
            PrecisionImproved = pimproved,
            KnownItemSummaries = kSummaries,
            PrecisionSummaries = pSummaries,
            Skipped = skipped,
        };
    }

    /// <summary>比較の結果の文章。下がったクエリを全部並べる。</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 基準線との比較 ==");
        foreach (string s in Skipped) sb.AppendLine("  注意: " + s);

        sb.AppendLine("[集計の変化(基準線 → 今回。両方にあるクエリだけ)]");
        foreach ((KnownItemSummary b, KnownItemSummary a) in KnownItemSummaries)
            sb.AppendLine($"  known-item {SetLabel(b.Set)} / {b.Path}: 上位20位以内 {b.Top20}/{b.Count} → {a.Top20}/{a.Count}{Mark(a.Top20 - b.Top20)}、5位以内 {b.Top5} → {a.Top5}、1位 {b.Top1} → {a.Top1}");
        foreach ((PrecisionSummary b, PrecisionSummary a) in PrecisionSummaries)
            sb.AppendLine($"  precision@10 / {b.Path}: {b.Lower:F3} → {a.Lower:F3}(当たり {b.Relevant} → {a.Relevant}{Mark(a.Relevant - b.Relevant)}、未判定 {b.Unjudged} → {a.Unjudged})");

        sb.AppendLine($"[下がった known-item: {KnownItemDrops.Count} 件(20位の線の外へ落ちたもの {KnownItemDrops.Count(d => d.LeftTop20)} 件)]");
        foreach (KnownItemDrop d in KnownItemDrops)
            sb.AppendLine($"  {(d.LeftTop20 ? "圏外へ " : "下がった")} {SetLabel(d.Set)} / {d.Path} / 「{d.Query}」 {d.Filename}: {Rank(d.Before)} → {Rank(d.After)}");
        sb.AppendLine($"[上がった known-item: {KnownItemImproved.Count} 件]");

        sb.AppendLine($"[当たりが減った precision@10 のクエリ: {PrecisionDrops.Count} 件]");
        foreach (PrecisionDrop d in PrecisionDrops)
            sb.AppendLine($"  {d.Path} / 「{d.Query}」: 当たり {d.Before} → {d.After}{(d.UnjudgedAfter > d.UnjudgedBefore ? $"(未判定 {d.UnjudgedBefore} → {d.UnjudgedAfter}。新しく上位に入った画像は qrels に無い=外れとして数えている)" : "")}");
        sb.AppendLine($"[当たりが増えた precision@10 のクエリ: {PrecisionImproved.Count} 件]");

        sb.AppendLine(AggregateWorse
            ? "判定: 基準線より下がった集計がある(上の ▼)。変更が意図したものか確かめる。"
            : "判定: 集計は基準線を下回っていない(個々のクエリの下がりは上の一覧)。");
        return sb.ToString();
    }

    private static string Mark(int delta) => delta < 0 ? " ▼" : delta > 0 ? " ▲" : "";

    private static string Rank(double? r) => r is null ? "圏外" : r.Value.ToString("0.#", CultureInfo.InvariantCulture);

    internal static string SetLabel(string set) => set switch { "visual" => "見た目", "dialogue" => "セリフ", _ => set };
}
