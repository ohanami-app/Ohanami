using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ohanami.Bench;

/// <summary>1 回の実行の情報(要約の見出し用)。<paramref name="Stages"/> は段階ごとの所要時間(無ければ null)。</summary>
internal sealed record RunInfo(string Label, DateTimeOffset At, string ImagesDirectory, int Images, int Embedded, double IndexSeconds, string Notes, IReadOnlyList<StageTime>? Stages = null);

/// <summary>要約のテキスト。</summary>
internal static class Report
{
    /// <summary>
    /// 経路ごとの known-item と precision@10 の要約。評価の記録(spikes/S2-siglip2 の出力。SigLIP2 単独 22/45、0.346)は、確認のための参考値として最後に付ける
    /// (評価の記録は TF32 オンの埋め込み。製品は TF32 オフなので、数件の順位のずれはありうる)。
    /// </summary>
    public static string Summary(RunInfo info, RunData data, IReadOnlyList<string> pathNames, int precisionQueries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Ohanami 検索の回帰ベンチ  {info.Label}  {info.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"画像: {info.Images} 枚(埋め込み済み {info.Embedded})  フォルダ: {info.ImagesDirectory}");
        if (info.IndexSeconds > 0) sb.AppendLine($"今回の走査と埋め込み: {info.IndexSeconds:F1} 秒");
        if (info.Notes.Length > 0) sb.AppendLine(info.Notes);
        sb.AppendLine($"経路: {string.Join("、", pathNames)}(fused = 融合の後)");
        sb.AppendLine();

        sb.AppendLine("[known-item: 探したい 1 枚の順位(同点は平均順位)。上位20位以内 / 5位以内 / 1位 / 中央値]");
        foreach (string set in data.KnownItems.Select(r => r.Set).Distinct())
        {
            foreach (string path in pathNames)
            {
                KnownItemSummary s = Metrics.Summarize(set, path, data.KnownItems);
                if (s.Count == 0) continue;
                sb.AppendLine($"  {Comparison.SetLabel(set),-4} {path,-8} {s.Top20,3}/{s.Count}  {s.Top5,3}  {s.Top1,3}  中央値 {Median(s.Median)}{(s.Missing > 0 ? $"  (順位なし {s.Missing})" : "")}");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"[precision@10: qrels の {precisionQueries} クエリ。未判定は外れとして数える(下限)。上限は未判定がすべて当たりのとき]");
        foreach (string path in pathNames)
        {
            PrecisionSummary s = Metrics.Summarize(path, data.Precision);
            if (s.Queries == 0) continue;
            sb.AppendLine($"  {path,-8} 下限 {s.Lower:F3}  上限 {s.Upper:F3}  当たり {s.Relevant}  判定済みの外れ {s.JudgedMiss}  未判定 {s.Unjudged}  返した件数 {s.Returned}/{s.Slots}");
        }
        sb.AppendLine();

        sb.AppendLine("[返した分の当たり率(当たり ÷ 返した件数。上位 10 件まで。未判定は外れ = 下限、すべて当たり = 上限)。全文の偽陽性の量の目安]");
        foreach (string path in pathNames)
        {
            PrecisionSummary s = Metrics.Summarize(path, data.Precision);
            if (s.Queries == 0) continue;
            sb.AppendLine(s.Returned == 0
                ? $"  {path,-8} 返した件数 0(当たり率なし)"
                : $"  {path,-8} {Percent(s.HitRateLower)}(下限。{s.Relevant}/{s.Returned})  上限 {Percent(s.HitRateUpper)}");
        }
        sb.AppendLine();

        if (data.Scores is { Count: > 0 } scores) sb.Append(ScoreReport.Render(scores));
        sb.Append(ScoreReport.RenderTimings(info.Stages));

        sb.AppendLine("[参考: 評価の記録(TF32 オン)]  SigLIP2 単独: known-item 見た目 22/45(上位20位以内)、precision@10 0.346。融合(tags あり)は known-item 29/45、precision@10 0.477、全文検索単独(tags あり)は 0.297。上の fused と fulltext の行を、これらと並べて見る(許容は SearchEvalCheck に書いてある)。");
        return sb.ToString();
    }

    private static string Percent(double r) => (r * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static string Median(double m) => double.IsNaN(m) ? "-" : m.ToString("0.#", CultureInfo.InvariantCulture);
}
