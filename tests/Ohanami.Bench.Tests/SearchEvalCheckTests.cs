using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Ohanami.Core;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>評価の記録の数字との突き合わせ。</summary>
public sealed class SearchEvalCheckTests
{
    private static KnownItemRow K(string set, string q, string path, double? rank) => new(set, q, q + ".png", path, rank);

    private static SearchEvalKnownRow S(string q, double? fts, double? sig, double? fused) => new(q + ".png", q, fts, sig, fused);

    /// <summary>見た目 3 件・セリフ 2 件の、記録と完全に同じ結果の run。</summary>
    private static (RunData Run, List<SearchEvalKnownRow> Visual, List<SearchEvalKnownRow> Dialogue, List<SearchEvalPrecisionRow> Precision) Baseline()
    {
        var visual = new List<SearchEvalKnownRow> { S("a", 3, 30, 4), S("b", null, 10, 12), S("c", 50, 60, 40) };
        var dialogue = new List<SearchEvalKnownRow> { S("x", 1, 100, 1), S("y", null, 200, 150) };
        var known = new List<KnownItemRow>();
        foreach (SearchEvalKnownRow s in visual) known.AddRange([K("visual", s.Query, "fulltext", s.FtsTags), K("visual", s.Query, "visual", s.Siglip2), K("visual", s.Query, "fused", s.FusedTags)]);
        foreach (SearchEvalKnownRow s in dialogue) known.AddRange([K("dialogue", s.Query, "fulltext", s.FtsTags), K("dialogue", s.Query, "visual", s.Siglip2), K("dialogue", s.Query, "fused", s.FusedTags)]);
        // precision: 2 クエリ。全文 0.3 / 0.0、融合 0.5 / 0.4
        var prec = new List<PrecisionRow>
        {
            new("q1", "fulltext", 3, 0, 0, 10, []), new("q2", "fulltext", 0, 0, 0, 0, []),
            new("q1", "fused", 5, 5, 0, 10, []), new("q2", "fused", 4, 6, 0, 10, []),
        };
        var s2p = new List<SearchEvalPrecisionRow> { new("q1", 0.3, 0.1, 0.5), new("q2", 0.0, 0.1, 0.4) };
        return (new RunData(known, prec), visual, dialogue, s2p);
    }

    private static SearchEvalCheck.Report Eval((RunData Run, List<SearchEvalKnownRow> V, List<SearchEvalKnownRow> D, List<SearchEvalPrecisionRow> P) b) => SearchEvalCheck.Evaluate(b.Run, b.V, b.D, b.P);

    private static CheckResult C(SearchEvalCheck.Report r, string prefix) => r.Criteria.Single(c => c.Name.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void A_run_identical_to_the_record_passes_every_criterion()
    {
        SearchEvalCheck.Report r = Eval(Baseline());
        Assert.Equal(6, r.Criteria.Count);
        Assert.True(r.AllPassed, string.Join("\n", r.Criteria.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Detail)));
    }

    [Fact]
    public void One_full_text_rank_that_differs_even_by_a_little_fails_the_exact_match_criterion()
    {
        var b = Baseline();
        var known = b.Run.KnownItems.Select(r => r.Set == "visual" && r.Query == "c" && r.Path == "fulltext" ? r with { Rank = 50.5 } : r).ToList();
        SearchEvalCheck.Report r = Eval((new RunData(known, b.Run.Precision), b.Visual, b.Dialogue, b.Precision));
        Assert.False(C(r, "1.").Passed);
        Assert.Contains("食い違い 1", C(r, "1.").Detail, StringComparison.Ordinal);
        Assert.Contains("「c」", r.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_text_hit_that_appears_or_disappears_is_a_mismatch_too()
    {
        var b = Baseline();
        var known = b.Run.KnownItems.Select(r => r.Set == "visual" && r.Query == "b" && r.Path == "fulltext" ? r with { Rank = 7 } : r).ToList();   // 記録は「ヒットなし」
        Assert.False(C(Eval((new RunData(known, b.Run.Precision), b.Visual, b.Dialogue, b.Precision)), "1.").Passed);
    }

    [Fact]
    public void The_visual_path_differences_are_only_reported_not_failed()
    {
        var b = Baseline();
        var known = b.Run.KnownItems.Select(r => r.Set == "visual" && r.Query == "a" && r.Path == "visual" ? r with { Rank = 31 } : r).ToList();   // TF32 の差の範囲
        SearchEvalCheck.Report r = Eval((new RunData(known, b.Run.Precision), b.Visual, b.Dialogue, b.Precision));
        Assert.True(r.AllPassed);
        Assert.Contains("見た目の順位が違う", r.Details, StringComparison.Ordinal);
        Assert.Contains("参考: 見た目の経路の食い違い 1 行", C(r, "1.").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fused_top_twenty_count_may_differ_from_the_record_by_two_but_not_three()
    {
        var b = Baseline();                                             // 記録: 融合で 20 位以内は a(4)、b(12) の 2 件(c は 40)
        IEnumerable<KnownItemRow> WithFused(double? c, double? bRank) => b.Run.KnownItems.Select(r =>
            r.Set == "visual" && r.Path == "fused" && r.Query == "c" ? r with { Rank = c } : r.Set == "visual" && r.Path == "fused" && r.Query == "b" ? r with { Rank = bRank } : r);
        Assert.True(C(Eval((new RunData(WithFused(15, 12).ToList(), b.Run.Precision), b.Visual, b.Dialogue, b.Precision)), "2.").Passed);    // +1
        Assert.True(C(Eval((new RunData(WithFused(40, 99).ToList(), b.Run.Precision), b.Visual, b.Dialogue, b.Precision)), "2.").Passed);    // -1
        var many = new List<SearchEvalKnownRow> { S("a", 3, 30, 4), S("b", null, 10, 12), S("c", 50, 60, 40), S("d", 1, 1, 1), S("e", 1, 1, 1), S("f", 1, 1, 1) };   // 記録は 5 件が 20 位以内
        var known = many.SelectMany(s => new[] { K("visual", s.Query, "fulltext", s.FtsTags), K("visual", s.Query, "visual", s.Siglip2), K("visual", s.Query, "fused", s.Query is "a" or "b" or "d" ? 99 : s.FusedTags) }).ToList();
        known.AddRange(b.Run.KnownItems.Where(r => r.Set == "dialogue"));
        SearchEvalCheck.Report r = SearchEvalCheck.Evaluate(new RunData(known, b.Run.Precision), many, b.Dialogue, b.Precision);
        Assert.False(C(r, "2.").Passed);                                 // 記録は 5 件、製品は a・b・d が圏外で 2 件(差 3)
    }

    [Fact]
    public void Precision_is_checked_against_the_record_means_with_the_two_tolerances()
    {
        var b = Baseline();                                             // 記録: 全文 平均 0.15、融合 平均 0.45
        PrecisionRow[] fusedUp = b.Run.Precision.Select(p => p.Path == "fused" && p.Query == "q1" ? p with { Relevant = 7 } : p).ToArray();   // 融合の平均 +0.1
        Assert.False(C(Eval((new RunData(b.Run.KnownItems, fusedUp), b.Visual, b.Dialogue, b.Precision)), "3a").Passed);
        PrecisionRow[] ftsUp = b.Run.Precision.Select(p => p.Path == "fulltext" && p.Query == "q1" ? p with { Relevant = 4 } : p).ToArray();    // 全文の平均 +0.05
        SearchEvalCheck.Report r = Eval((new RunData(b.Run.KnownItems, ftsUp), b.Visual, b.Dialogue, b.Precision));
        Assert.False(C(r, "3b").Passed);
        Assert.Contains("「q1」", r.Details, StringComparison.Ordinal);
        // 差が無ければ通る
        Assert.True(C(Eval(b), "3b").Passed);
    }

    [Fact]
    public void The_dialogue_criteria_require_the_same_full_text_count_and_a_fused_count_within_one()
    {
        var b = Baseline();                                             // 記録: セリフ 全文 1/2、融合 2/2
        IEnumerable<KnownItemRow> Change(string path, string q, double? rank) => b.Run.KnownItems.Select(r => r.Set == "dialogue" && r.Path == path && r.Query == q ? r with { Rank = rank } : r);
        Assert.False(C(Eval((new RunData(Change("fulltext", "y", 5).ToList(), b.Run.Precision), b.Visual, b.Dialogue, b.Precision)), "4a").Passed);   // 全文が 1 件増えた
        Assert.True(C(Eval((new RunData(Change("fused", "y", 999).ToList(), b.Run.Precision), b.Visual, b.Dialogue, b.Precision)), "4b").Passed);      // 融合 -1
    }

    [Fact]
    public void A_missing_row_in_the_run_is_a_loud_error_not_a_pass()
    {
        var b = Baseline();
        var known = b.Run.KnownItems.Where(r => !(r.Query == "a" && r.Path == "fused")).ToList();
        Assert.Throws<InvalidDataException>(() => Eval((new RunData(known, b.Run.Precision), b.Visual, b.Dialogue, b.Precision)));
    }

    [Fact]
    public void Record_files_are_read_by_column_name_with_a_dash_meaning_no_hit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bench-s2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string known = Path.Combine(dir, "k.tsv");
            File.WriteAllText(known, "filename\tquery_ja\tfts_notags_mean_rank\tfts_tags_mean_rank\tsiglip2_mean_rank\tfused_notags_mean_rank\tfused_tags_mean_rank\na.png\t猫\t-\t-\t13\t13\t13\nb.png\t犬\t15.5\t15\t51\t10\t10\n");
            List<SearchEvalKnownRow> rows = SearchEvalCheck.ReadKnownItems(known);
            Assert.Equal(2, rows.Count);
            Assert.Null(rows[0].FtsTags);
            Assert.Equal(13.0, rows[0].Siglip2);
            Assert.Equal(15.0, rows[1].FtsTags);
            Assert.Equal(10.0, rows[1].FusedTags);

            string prec = Path.Combine(dir, "p.tsv");
            File.WriteAllText(prec, "query_ja\tfts_notags_p10\tfts_notags_returned\tfts_tags_p10\tfts_tags_returned\tsiglip2_p10\tsiglip2_returned\tfused_notags_p10\tfused_notags_returned\tfused_tags_p10\tfused_tags_returned\n猫\t0.0\t0\t0.3\t5\t0.1\t10\t0.1\t10\t0.4\t10\n");
            SearchEvalPrecisionRow p = Assert.Single(SearchEvalCheck.ReadPrecisionByQuery(prec));
            Assert.Equal((0.3, 0.1, 0.4), (p.FtsTags, p.Siglip2, p.FusedTags));

            File.WriteAllText(known, "filename\tquery_ja\nx\ty\n");
            Assert.Throws<InvalidDataException>(() => SearchEvalCheck.ReadKnownItems(known));
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>経路ごとの precision@10 の同点の切り方(評価の記録の「画像の番号 = ファイル名の序数順」と同じにして、他の経路の順位を持ち込まない)。</summary>
public sealed class PathPrecisionTieTests
{
    [Fact]
    public void Ties_inside_one_path_are_cut_by_file_name_and_not_by_the_fused_order()
    {
        // 12 枚がすべて全文で同順位(5.5)。融合の順は番号の逆(12 が最良)
        (string, double, double?, double?)[] items = Enumerable.Range(1, 12).Select(i => ($"{i:00}.png", (double)(13 - i), (double?)null, (double?)5.5)).ToArray();
        SearchResponse response = Fx.Response("q", items);
        Qrels qrels = Fx.Qrels("q	12.png	1	s", "q	11.png	1	s", "q	01.png	0	s");

        PrecisionRow fulltext = Metrics.Precision(response, "fulltext", "q", qrels);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"{i:00}.png"), fulltext.Top);   // ファイル名の順の先頭 10 件
        Assert.Equal(0, fulltext.Relevant);                                                // 12 と 11 は 10 件の外(融合の順なら入って 2 になる)
        Assert.Equal(1, fulltext.JudgedMiss);

        PrecisionRow fused = Metrics.Precision(response, Metrics.Fused, "q", qrels);
        Assert.Equal(2, fused.Relevant);                                                   // 融合は response の順(12 と 11 が先頭)
    }

    [Fact]
    public void A_better_rank_still_beats_a_file_name_that_sorts_first()
    {
        SearchResponse response = Fx.Response("q", ("a.png", 2, null, 2.0), ("b.png", 1, null, 1.0));
        PrecisionRow row = Metrics.Precision(response, "fulltext", "q", Fx.Qrels("q	b.png	1	s"));
        Assert.Equal(["b.png", "a.png"], row.Top);
    }
}
