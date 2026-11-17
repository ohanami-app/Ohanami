using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Ohanami.Bench;

/// <summary>評価の記録の known-item の結果(<c>stage3-known-items.tsv</c>)の 1 行。「-」は、その経路にヒットが無かった(順位なし)。</summary>
internal sealed record SearchEvalKnownRow(string Filename, string Query, double? FtsTags, double? Siglip2, double? FusedTags);

/// <summary>評価の記録の precision@10 の、クエリごとの値(<c>stage3-precision-by-query.tsv</c>)。</summary>
internal sealed record SearchEvalPrecisionRow(string Query, double FtsTags, double Siglip2, double FusedTags);

/// <summary>
/// 評価の記録(spikes/S2-siglip2 が出した数字。以下「記録」)を、製品の検索で再現できたかの確認。合格線は結果を見る前に決めたもの:
/// <list type="number">
/// <item>全文検索の単独のクエリごとの順位(見た目 45 件 + セリフ 10 件)が、記録の <c>fts_tags_mean_rank</c> と**完全に一致**する(GPU の浮動小数に依らない部分。
///   前提: 同じ SQLite のビルド=Microsoft.Data.Sqlite 10.0.14 同梱の 3.53.3。BM25 の rank の計算は SQLite の版で変わりうる)。</item>
/// <item>融合の known-item(見た目)の上位 20 位以内の件数が、記録の件数(29)± 2(記録は TF32 オン、製品はオフで、SigLIP2 の順位が数件ずれうる)。</item>
/// <item>precision@10(下限)が、融合は記録の平均(0.477)± 0.02、全文検索単独は記録の平均(0.297)± 0.01(同点の塊が 10 位をまたぐクエリの切り方が、記録は画像の番号順、製品は別の順なので、
///   完全一致までは求めない。差のあるクエリは一覧に出す)。</item>
/// <item>セリフ: 全文検索単独の上位 20 位以内が記録の件数(7)と同じ、融合が記録の件数(8)± 1。</item>
/// </list>
/// 外れたら、<c>SearchPlan</c> → SQL → 融合 → 索引文書の組み立ての順に、クエリごとの差を見て原因を特定する。
/// </summary>
internal static class SearchEvalCheck
{
    public sealed record Report(IReadOnlyList<CheckResult> Criteria, string Details)
    {
        public bool AllPassed => Criteria.All(c => c.Passed);
    }

    public static List<SearchEvalKnownRow> ReadKnownItems(string path)
    {
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length == 0) throw new InvalidDataException($"{path} が空");
        string[] header = lines[0].TrimStart('﻿').Split('\t');
        int Col(string name) => Array.IndexOf(header, name) is var i and >= 0 ? i : throw new InvalidDataException($"{path} に列 {name} が無い: {lines[0]}");
        int iFile = Col("filename"), iQuery = Col("query_ja"), iFts = Col("fts_tags_mean_rank"), iSig = Col("siglip2_mean_rank"), iFused = Col("fused_tags_mean_rank");
        var rows = new List<SearchEvalKnownRow>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            string[] c = lines[i].TrimEnd('\r').Split('\t');
            if (c.Length != header.Length) throw new InvalidDataException($"{path} の {i + 1} 行目の列の数が違う");
            rows.Add(new SearchEvalKnownRow(BenchInputs.Nfc(c[iFile]), c[iQuery], Rank(c[iFts]), Rank(c[iSig]), Rank(c[iFused])));
        }
        return rows;

        static double? Rank(string s) => s == "-" ? null : double.Parse(s, CultureInfo.InvariantCulture);
    }

    public static List<SearchEvalPrecisionRow> ReadPrecisionByQuery(string path)
    {
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length == 0) throw new InvalidDataException($"{path} が空");
        string[] header = lines[0].TrimStart('﻿').Split('\t');
        int Col(string name) => Array.IndexOf(header, name) is var i and >= 0 ? i : throw new InvalidDataException($"{path} に列 {name} が無い: {lines[0]}");
        int iQuery = Col("query_ja"), iFts = Col("fts_tags_p10"), iSig = Col("siglip2_p10"), iFused = Col("fused_tags_p10");
        var rows = new List<SearchEvalPrecisionRow>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            string[] c = lines[i].TrimEnd('\r').Split('\t');
            if (c.Length != header.Length) throw new InvalidDataException($"{path} の {i + 1} 行目の列の数が違う");
            rows.Add(new SearchEvalPrecisionRow(c[iQuery], D(c[iFts]), D(c[iSig]), D(c[iFused])));
        }
        return rows;

        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    }

    private const double Epsilon = 1e-9;

    public static Report Evaluate(RunData run, IReadOnlyList<SearchEvalKnownRow> visual, IReadOnlyList<SearchEvalKnownRow> dialogue, IReadOnlyList<SearchEvalPrecisionRow> precision)
    {
        var details = new StringBuilder();
        var rank = new Dictionary<(string Set, string File, string Query, string Path), double?>();
        foreach (KnownItemRow r in run.KnownItems) rank[(r.Set, BenchInputs.Nfc(r.Filename), r.Query, r.Path)] = r.Rank;

        double? Bench(string set, SearchEvalKnownRow s, string path) =>
            rank.TryGetValue((set, s.Filename, s.Query, path), out double? v) ? v
                : throw new InvalidDataException($"ベンチの結果に「{s.Query}」×「{s.Filename}」({set})の経路 {path} の行が無い。記録の known-item と同じ入力で run したか確かめる");

        static bool Same(double? a, double? b) => (a is null && b is null) || (a is not null && b is not null && Math.Abs(a.Value - b.Value) < Epsilon);
        static string Fmt(double? r) => r is null ? "-" : r.Value.ToString("0.#", CultureInfo.InvariantCulture);

        // 1. 全文検索の順位の完全一致
        int ftsMismatch = 0, ftsRows = 0, sigMismatch = 0;
        double sigMaxDiff = 0;
        foreach ((string set, IReadOnlyList<SearchEvalKnownRow> rows) in new[] { ("visual", visual), ("dialogue", dialogue) })
        {
            foreach (SearchEvalKnownRow s in rows)
            {
                ftsRows++;
                double? ft = Bench(set, s, "fulltext");
                if (!Same(ft, s.FtsTags))
                {
                    ftsMismatch++;
                    details.AppendLine($"  全文の順位が違う [{set}] 「{s.Query}」 {s.Filename}: 記録 {Fmt(s.FtsTags)} / 製品 {Fmt(ft)}");
                }
                double? sg = Bench(set, s, "visual");
                if (!Same(sg, s.Siglip2))
                {
                    sigMismatch++;
                    if (sg is not null && s.Siglip2 is not null) sigMaxDiff = Math.Max(sigMaxDiff, Math.Abs(sg.Value - s.Siglip2.Value));
                    details.AppendLine($"  (参考)見た目の順位が違う [{set}] 「{s.Query}」 {s.Filename}: 記録 {Fmt(s.Siglip2)} / 製品 {Fmt(sg)}(TF32 の差の範囲か)");
                }
            }
        }
        var results = new List<CheckResult>
        {
            new("1. 全文検索の単独の順位が記録と完全に一致", ftsMismatch == 0,
                $"{ftsRows} 行(見た目 {visual.Count} + セリフ {dialogue.Count})中、食い違い {ftsMismatch}。参考: 見た目の経路の食い違い {sigMismatch} 行(最大 {sigMaxDiff:0.#} 位。記録は TF32 オン)。完全一致の前提: 同じ SQLite のビルド(3.53.3)"),
        };

        // 2. 融合の known-item(見た目)の上位 20 位以内
        int evalFused = visual.Count(s => s.FusedTags is <= 20);
        int benchFused = visual.Count(s => Bench("visual", s, "fused") is <= 20);
        results.Add(new CheckResult("2. 融合の known-item(見た目)の上位 20 位以内が記録の件数 ± 2", Math.Abs(benchFused - evalFused) <= 2,
            $"記録 {evalFused}/{visual.Count}、製品 {benchFused}/{visual.Count}(差 {benchFused - evalFused:+0;-0;0})"));
        foreach (SearchEvalKnownRow s in visual)
        {
            double? f = Bench("visual", s, "fused");
            bool wasIn = s.FusedTags is <= 20, isIn = f is <= 20;
            if (wasIn != isIn || (f is not null && s.FusedTags is not null && Math.Abs(f.Value - s.FusedTags.Value) > 3))
                details.AppendLine($"  (参考)融合の順位が変わった 「{s.Query}」 {s.Filename}: 記録 {Fmt(s.FusedTags)} / 製品 {Fmt(f)}{(wasIn != isIn ? "  ← 20 位の線をまたいだ" : "")}");
        }

        // 3. precision@10(下限)
        PrecisionSummary benchFts = Metrics.Summarize("fulltext", run.Precision), benchFusedP = Metrics.Summarize("fused", run.Precision);
        double evalFtsMean = precision.Count == 0 ? 0 : precision.Average(p => p.FtsTags);
        double evalFusedMean = precision.Count == 0 ? 0 : precision.Average(p => p.FusedTags);
        results.Add(new CheckResult("3a. 融合の precision@10(下限)が記録の平均 ± 0.02", benchFusedP.Queries == precision.Count && Math.Abs(benchFusedP.Lower - evalFusedMean) <= 0.02 + Epsilon,
            $"記録 {evalFusedMean:F3}、製品 {benchFusedP.Lower:F3}(上限 {benchFusedP.Upper:F3}、未判定 {benchFusedP.Unjudged}。クエリ {benchFusedP.Queries} / 記録 {precision.Count})"));
        results.Add(new CheckResult("3b. 全文検索単独の precision@10(下限)が記録の平均 ± 0.01", benchFts.Queries == precision.Count && Math.Abs(benchFts.Lower - evalFtsMean) <= 0.01 + Epsilon,
            $"記録 {evalFtsMean:F3}、製品 {benchFts.Lower:F3}(上限 {benchFts.Upper:F3}、未判定 {benchFts.Unjudged})"));
        var ftsByQuery = run.Precision.Where(p => p.Path == "fulltext").ToDictionary(p => p.Query);
        foreach (SearchEvalPrecisionRow s in precision)
        {
            if (ftsByQuery.TryGetValue(s.Query, out PrecisionRow? b) && Math.Abs(b.Relevant / 10.0 - s.FtsTags) > Epsilon)
                details.AppendLine($"  (参考)全文の precision@10 が違う 「{s.Query}」: 記録 {s.FtsTags:F1} / 製品 {b.Relevant / 10.0:F1}(未判定 {b.Unjudged})");
        }
        var fusedByQuery = run.Precision.Where(p => p.Path == "fused").ToDictionary(p => p.Query);
        foreach (SearchEvalPrecisionRow s in precision)
        {
            if (fusedByQuery.TryGetValue(s.Query, out PrecisionRow? b) && Math.Abs(b.Relevant / 10.0 - s.FusedTags) > Epsilon)
                details.AppendLine($"  (参考)融合の precision@10 が違う 「{s.Query}」: 記録 {s.FusedTags:F1} / 製品 {b.Relevant / 10.0:F1}(未判定 {b.Unjudged})");
        }

        // 4. セリフ
        int evalDialogueFts = dialogue.Count(s => s.FtsTags is <= 20), benchDialogueFts = dialogue.Count(s => Bench("dialogue", s, "fulltext") is <= 20);
        int evalDialogueFused = dialogue.Count(s => s.FusedTags is <= 20), benchDialogueFused = dialogue.Count(s => Bench("dialogue", s, "fused") is <= 20);
        results.Add(new CheckResult("4a. セリフ: 全文検索単独の上位 20 位以内が記録の件数と同じ", benchDialogueFts == evalDialogueFts,
            $"記録 {evalDialogueFts}/{dialogue.Count}、製品 {benchDialogueFts}/{dialogue.Count}"));
        results.Add(new CheckResult("4b. セリフ: 融合の上位 20 位以内が記録の件数 ± 1", Math.Abs(benchDialogueFused - evalDialogueFused) <= 1,
            $"記録 {evalDialogueFused}/{dialogue.Count}、製品 {benchDialogueFused}/{dialogue.Count}"));

        return new Report(results, details.ToString());
    }
}
