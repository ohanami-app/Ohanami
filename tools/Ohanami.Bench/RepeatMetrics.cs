using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Ohanami.Core;
using Ohanami.Core.Search;

namespace Ohanami.Bench;

/// <summary>qrels での判定の種類。<see cref="NoQrels"/> = そのクエリが qrels に無い(判定の対象外)、<see cref="Unjudged"/> = qrels のクエリだが、その画像は載っていない。</summary>
internal enum JudgeKind { NoQrels, Unjudged, Miss, Hit }

/// <summary>
/// ある経路の、あるクエリの上位 10 件の 1 枚。<paramref name="Pos"/> は 1 始まりの並び(<see cref="Metrics.TopOf"/> と同じ取り方)。
/// <paramref name="Filename"/> は画像を指すファイル名(<see cref="Metrics.Judge"/>)。<paramref name="TextMatched"/> は、その結果に全文検索のヒットがあるか(経路によらず、画像とクエリで決まる)。
/// </summary>
internal sealed record TopRow(string Query, string Path, int Pos, string Filename, bool TextMatched, JudgeKind Judge);

/// <summary>「繰り返し出る画像」の集計(経路 1 つ)。数字だけ。</summary>
/// <param name="Queries">上位を数えたクエリの数。</param>
/// <param name="Slots">上位に並んだ枠の数(クエリ × 返した件数の合計)。</param>
/// <param name="DistinctImages">上位 10 件のどれかに出た画像の種類。</param>
/// <param name="RepeatImages">繰り返し画像(<see cref="RepeatMetrics.RepeatThreshold"/> 個以上のクエリの上位 10 件に出た画像)の種類。</param>
/// <param name="RepeatSlots">繰り返し画像が占めた枠の数。</param>
/// <param name="MaxQueriesPerImage">1 枚の画像が上位 10 件に出たクエリの数の最大。</param>
internal sealed record RepeatStats(string Path, int Queries, int Slots, int DistinctImages, int RepeatImages, int RepeatSlots, int MaxQueriesPerImage)
{
    /// <summary>繰り返し画像が占めた枠の割合(0〜1)。枠が無ければ 0。</summary>
    public double RepeatRatio => Slots == 0 ? 0 : (double)RepeatSlots / Slots;
}

/// <summary>融合の上位 10 件の当たり率の 1 区分(数字だけ)。未判定は外れとして数えた下限と、すべて当たりの上限を持つ。</summary>
internal sealed record HitBreakdownRow(string Label, int Queries, int Slots, int Relevant, int Miss, int Unjudged)
{
    public double Lower => Slots == 0 ? double.NaN : (double)Relevant / Slots;
    public double Upper => Slots == 0 ? double.NaN : (double)(Relevant + Unjudged) / Slots;

    /// <summary>判定済みの枠だけで数えた当たり率(未判定の枠は分母にも入れない)。</summary>
    public double JudgedOnly => Relevant + Miss == 0 ? double.NaN : (double)Relevant / (Relevant + Miss);
}

/// <summary>
/// 検索の結果で同じ画像が何度も上位に出ること(hubness)を測る物差し。DB も GPU も使わない純粋な計算で、入力は <see cref="TopRow"/> の表だけ
/// (<c>top10.tsv</c> に保存するので、あとから基準線と比べられる)。
/// <list type="bullet">
/// <item>① 繰り返し: 経路ごとに、qrels のクエリの上位 10 件で、画像の種類・繰り返し画像の種類と占めた枠・1 枚が出たクエリの数の最大(<see cref="Repeat"/>)。</item>
/// <item>② 当たり率の内訳(融合の上位 10 件。qrels のクエリだけ): 全文に一致した画像か見た目の経路だけか、クエリごとの全文一致の枚数(0 / 1〜9 / 10)、繰り返し画像か(<see cref="Breakdown"/>)。</item>
/// </list>
/// </summary>
internal static class RepeatMetrics
{
    /// <summary>繰り返し画像の線: これだけのクエリの上位 10 件に出た画像。</summary>
    public const int RepeatThreshold = 5;

    public const string TextPathId = FullTextPath.Id;

    /// <summary>
    /// 「繰り返し画像」を数える検索語の範囲。<see cref="QrelsQueries"/> = precision@10 を測る検索語(qrels のある検索語)だけ(主の定義)、
    /// <see cref="AllQueries"/> = known-item だけの検索語も含めた全検索語(比べるための定義)。
    /// </summary>
    public enum RepeatScope { QrelsQueries, AllQueries }

    private static List<TopRow> InScope(IReadOnlyList<TopRow> rows, string path, RepeatScope scope) =>
        rows.Where(r => r.Path == path && (scope == RepeatScope.AllQueries || r.Judge != JudgeKind.NoQrels)).ToList();

    // ------------------------------------------------------------------ 表を作る

    /// <summary>
    /// すべてのクエリ(<see cref="BenchRunner.Collected.UniqueQueries"/>)× すべての経路(融合 + 結果の経路)の上位 10 件の表。
    /// <paramref name="pathRename"/> があれば、経路の名前をそれで読み替える(補正した経路を別の名前で並べるときに使う)。
    /// </summary>
    public static List<TopRow> TopRows(BenchRunner.Collected c, Func<string, string>? pathRename = null)
    {
        Qrels qrels = c.Inputs.Qrels;
        var qrelsQueries = qrels.Queries.ToHashSet(StringComparer.Ordinal);
        var rows = new List<TopRow>();
        foreach (string q in c.UniqueQueries.Order(StringComparer.Ordinal))
        {
            SearchResponse response = c.Responses[q];
            foreach (string path in Metrics.PathNames(response))
            {
                List<SearchResult> top = Metrics.TopOf(response, path);
                for (int i = 0; i < top.Count; i++)
                {
                    SearchResult r = top[i];
                    (string name, bool? judged) = Metrics.Judge(r, q, qrels);
                    JudgeKind kind = !qrelsQueries.Contains(q) ? JudgeKind.NoQrels : judged is null ? JudgeKind.Unjudged : judged.Value ? JudgeKind.Hit : JudgeKind.Miss;
                    rows.Add(new TopRow(q, pathRename is null ? path : pathRename(path), i + 1, name, r.Hits.Any(h => h.PathId == TextPathId), kind));
                }
            }
        }
        return rows;
    }

    // ------------------------------------------------------------------ ① 繰り返し

    /// <summary>画像(ファイル名)→ その経路の上位 10 件に出たクエリの数。</summary>
    public static Dictionary<string, int> QueriesPerImage(IEnumerable<TopRow> pathRows) =>
        pathRows.GroupBy(r => r.Filename, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(r => r.Query).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);

    public static HashSet<string> RepeatImages(IEnumerable<TopRow> pathRows) =>
        QueriesPerImage(pathRows).Where(kv => kv.Value >= RepeatThreshold).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

    public static RepeatStats Repeat(IReadOnlyList<TopRow> rows, string path, RepeatScope scope = RepeatScope.QrelsQueries)
    {
        // 既定の対象は qrels のある検索語だけ(precision@10 を測る検索語。known-item だけの検索語は数えない)
        List<TopRow> mine = InScope(rows, path, scope);
        Dictionary<string, int> perImage = QueriesPerImage(mine);
        var repeat = perImage.Where(kv => kv.Value >= RepeatThreshold).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        return new RepeatStats(path, mine.Select(r => r.Query).Distinct(StringComparer.Ordinal).Count(), mine.Count, perImage.Count, repeat.Count,
            mine.Count(r => repeat.Contains(r.Filename)), perImage.Count == 0 ? 0 : perImage.Values.Max());
    }

    // ------------------------------------------------------------------ ② 当たり率の内訳

    /// <summary>
    /// 経路 <paramref name="path"/>(ふつうは融合)の上位 10 件の当たり率の内訳。qrels のクエリだけを数える。繰り返し画像も、その qrels のクエリの上位 10 件で決める(<see cref="RepeatImages"/>)。
    /// 返す行: 全文に一致した枠 / 見た目の経路だけの枠、クエリの全文一致の枚数(0 / 1〜9 / 10 枚)、繰り返し画像の枠 / それ以外の枠、全文一致 × 繰り返しの 4 区分。
    /// </summary>
    public static List<HitBreakdownRow> Breakdown(IReadOnlyList<TopRow> rows, string path, RepeatScope scope = RepeatScope.QrelsQueries)
    {
        // 枠は qrels のある検索語だけを数える。繰り返し画像は、既定ではその検索語の中の数で決める(<see cref="Repeat"/> と同じ)。
        // scope が AllQueries なら、繰り返し画像だけを全検索語の上位 10 件で決める(枠は qrels の検索語のまま)
        List<TopRow> judged = rows.Where(r => r.Path == path && r.Judge != JudgeKind.NoQrels).ToList();
        HashSet<string> repeat = RepeatImages(InScope(rows, path, scope));
        Dictionary<string, int> textCount = judged.GroupBy(r => r.Query, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(r => r.TextMatched), StringComparer.Ordinal);

        HitBreakdownRow Row(string label, Func<TopRow, bool> pick)
        {
            List<TopRow> picked = judged.Where(pick).ToList();
            return new HitBreakdownRow(label, picked.Select(r => r.Query).Distinct(StringComparer.Ordinal).Count(), picked.Count,
                picked.Count(r => r.Judge == JudgeKind.Hit), picked.Count(r => r.Judge == JudgeKind.Miss), picked.Count(r => r.Judge == JudgeKind.Unjudged));
        }

        return
        [
            Row("全文に一致した枠", r => r.TextMatched),
            Row("見た目の経路だけの枠", r => !r.TextMatched),
            Row("クエリの全文一致 0 枚", r => textCount[r.Query] == 0),
            Row("クエリの全文一致 1〜9 枚", r => textCount[r.Query] is >= 1 and <= 9),
            Row("クエリの全文一致 10 枚", r => textCount[r.Query] == 10),
            Row("繰り返し画像の枠", r => repeat.Contains(r.Filename)),
            Row("繰り返し以外の枠", r => !repeat.Contains(r.Filename)),
            Row("全文一致 かつ 繰り返し", r => r.TextMatched && repeat.Contains(r.Filename)),
            Row("全文一致 かつ 繰り返し以外", r => r.TextMatched && !repeat.Contains(r.Filename)),
            Row("見た目だけ かつ 繰り返し", r => !r.TextMatched && repeat.Contains(r.Filename)),
            Row("見た目だけ かつ 繰り返し以外", r => !r.TextMatched && !repeat.Contains(r.Filename)),
        ];
    }

    // ------------------------------------------------------------------ 文章(数字だけ。クエリ・ファイル名は入れない)

    /// <summary>① を <paramref name="pathNames"/> の経路ごとに、② を <paramref name="breakdownPaths"/> の経路ごとに並べた文章。</summary>
    public static string Render(IReadOnlyList<TopRow> rows, IReadOnlyList<string> pathNames, IReadOnlyList<string> breakdownPaths)
    {
        var sb = new StringBuilder();
        foreach ((RepeatScope scope, string heading) in new[]
        {
            (RepeatScope.QrelsQueries, $"[① 繰り返し出る画像(主の定義): qrels のクエリ(precision@10 を測るクエリ)の上位 10 件だけ。繰り返し画像 = {RepeatThreshold} クエリ以上の上位 10 件に出た画像]"),
            (RepeatScope.AllQueries, $"[① 繰り返し出る画像(比べる定義): known-item だけのクエリも含めた全クエリの上位 10 件。繰り返し画像 = {RepeatThreshold} クエリ以上の上位 10 件に出た画像]"),
        })
        {
            sb.AppendLine(heading);
            foreach (string path in pathNames)
            {
                RepeatStats s = Repeat(rows, path, scope);
                if (s.Slots == 0) continue;
                sb.AppendLine($"  {path,-8} クエリ {s.Queries}、枠 {s.Slots}(クエリ × 10 = {s.Queries * Metrics.PrecisionTop})、画像の種類 {s.DistinctImages}、繰り返し画像 {s.RepeatImages} 枚が {s.RepeatSlots} 枠({Percent(s.RepeatRatio)})、1 枚が出たクエリの最大 {s.MaxQueriesPerImage}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("[② 融合の上位 10 件の当たり率の内訳: qrels のクエリだけ。当たり率は 2 つ: 未判定を外れとして数えた値(当たり ÷ 枠)と、判定済みの枠だけで数えた値(当たり ÷(当たり + 判定済みの外れ))。どちらかとは決めない]");
        foreach (string path in breakdownPaths)
        {
            if (!rows.Any(r => r.Path == path)) continue;
            sb.AppendLine($"  {path}:");
            foreach (HitBreakdownRow b in Breakdown(rows, path))
                sb.AppendLine($"    {b.Label,-22} クエリ {b.Queries,3}、枠 {b.Slots,4}、当たり {b.Relevant,4}、判定済みの外れ {b.Miss,4}、未判定 {b.Unjudged,4}、当たり率 未判定を外れ {Percent(b.Lower)}、判定済みだけ {Percent(b.JudgedOnly)}");
            sb.AppendLine("    (比べる定義: 繰り返し画像を全クエリの上位 10 件で決めたとき。枠は qrels のクエリのまま)");
            foreach (HitBreakdownRow b in Breakdown(rows, path, RepeatScope.AllQueries).Where(b => b.Label.Contains("繰り返し", StringComparison.Ordinal)))
                sb.AppendLine($"    {b.Label,-22} クエリ {b.Queries,3}、枠 {b.Slots,4}、当たり {b.Relevant,4}、判定済みの外れ {b.Miss,4}、未判定 {b.Unjudged,4}、当たり率 未判定を外れ {Percent(b.Lower)}、判定済みだけ {Percent(b.JudgedOnly)}");
        }
        return sb.ToString();
    }

    public static string Percent(double r) => double.IsNaN(r) ? "-" : (r * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
