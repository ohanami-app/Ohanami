using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Core;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>検索結果の組み立て(テスト用)。経路は visual と、必要なら fulltext。</summary>
internal static class Fx
{
    /// <summary>(ファイル名, 融合後の順位, visual の順位, fulltext の順位)。順位 null はその経路の母集団に無い。</summary>
    public static SearchResponse Response(string query, params (string File, double Fused, double? Visual, double? Fulltext)[] items) =>
        Response(query, withFulltext: items.Any(i => i.Fulltext is not null), items);

    public static SearchResponse Response(string query, bool withFulltext, params (string File, double Fused, double? Visual, double? Fulltext)[] items)
    {
        var results = items.OrderBy(i => i.Fused).Select(i =>
        {
            var hits = new List<PathHit>();
            if (i.Visual is { } v) hits.Add(new PathHit("visual", "m", v, 1.0 / v, new VisualDetail(1.0 / v, 1.0 / v)));
            if (i.Fulltext is { } f) hits.Add(new PathHit("fulltext", null, f, 1.0 / f, new VisualDetail(0, 0)));
            return new SearchResult(Hash(i.File), [@"C:\images\" + i.File], i.Fused, 1.0 / i.Fused, hits);
        }).ToList();
        var paths = new List<PathOutcome> { new("visual", "m", items.Count(i => i.Visual is not null), PathState.Ok, null) };
        if (withFulltext) paths.Add(new PathOutcome("fulltext", null, items.Count(i => i.Fulltext is not null), PathState.Ok, null));
        return new SearchResponse(query, results, paths, items.Length, items.Length, TimeSpan.Zero);
    }

    public static string Hash(string file) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file)));

    public static Qrels Qrels(params string[] lines) => Ohanami.Bench.Qrels.Parse(["query_ja\tfilename\trelevant\tsource", .. lines]);
}

public sealed class InputTests
{
    [Fact]
    public void Qrels_parse_reads_judgements_and_merges_identical_duplicates()
    {
        Qrels q = Fx.Qrels("猫\ta.png\t1\ts1", "猫\tb.png\t0\ts1", "猫\ta.png\t1\ts2");
        Assert.Equal(2, q.Count);
        Assert.True(q.TryGet("猫", "a.png"));
        Assert.False(q.TryGet("猫", "b.png"));
        Assert.Null(q.TryGet("猫", "c.png"));
        Assert.Null(q.TryGet("犬", "a.png"));
    }

    [Fact]
    public void Qrels_parse_rejects_conflicts_bad_values_bad_headers_and_short_rows()
    {
        Assert.Throws<InvalidDataException>(() => Fx.Qrels("猫\ta.png\t1\ts1", "猫\ta.png\t0\ts2"));
        Assert.Throws<InvalidDataException>(() => Fx.Qrels("猫\ta.png\t2\ts1"));
        Assert.Throws<InvalidDataException>(() => Fx.Qrels("猫\ta.png\t1"));
        Assert.Throws<InvalidDataException>(() => Qrels.Parse(["query\tfile\tx\ty"]));
        Assert.Throws<InvalidDataException>(() => Qrels.Parse([]));
    }

    [Fact]
    public void Qrels_file_names_are_compared_after_unicode_normalization()
    {
        string nfd = "ガ.png".Normalize(System.Text.NormalizationForm.FormD);
        string nfc = "ガ.png".Normalize(System.Text.NormalizationForm.FormC);
        Qrels q = Fx.Qrels($"猫\t{nfd}\t1\ts");
        Assert.True(q.TryGet("猫", nfc));
        Assert.True(q.TryGet("猫", nfd));
    }

    [Fact]
    public void Known_items_skip_rows_without_a_japanese_query_and_count_them()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "filename\tquery_ja\tquery_en\na.png\t猫\tcat\nb.png\t\tdog\n\nc.png\t犬\t\n");
            List<KnownItem> items = BenchInputs.ReadKnownItems(path, "visual", out int skipped);
            Assert.Equal(1, skipped);
            Assert.Equal(["a.png", "c.png"], items.Select(i => i.Filename));
            Assert.All(items, i => Assert.Equal("visual", i.Set));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Known_items_reject_a_file_without_the_required_columns_or_a_row_without_a_file_name()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "name\tquery\na.png\t猫\n");
            Assert.Throws<InvalidDataException>(() => BenchInputs.ReadKnownItems(path, "visual", out _));
            File.WriteAllText(path, "filename\tquery_ja\n\t猫\n");
            Assert.Throws<InvalidDataException>(() => BenchInputs.ReadKnownItems(path, "visual", out _));
        }
        finally { File.Delete(path); }
    }
}

public sealed class MetricsTests
{
    [Fact]
    public void Ranks_of_a_target_come_from_the_fused_order_and_each_path()
    {
        SearchResponse r = Fx.Response("q", ("a.png", 1, 2, 5), ("b.png", 2.5, 1, null), ("c.png", 3, null, 1));
        var byName = Metrics.IndexByFileName(r);
        List<(string Path, double? Rank)> ranks = Metrics.RanksOf(r, byName, "b.png");
        Assert.Equal([("fused", (double?)2.5), ("visual", 1), ("fulltext", null)], ranks);
    }

    [Fact]
    public void A_target_missing_from_the_results_is_an_error_not_a_silent_miss()
    {
        SearchResponse r = Fx.Response("q", ("a.png", 1, 1, null));
        var byName = Metrics.IndexByFileName(r);
        Assert.Throws<InvalidDataException>(() => Metrics.RanksOf(r, byName, "zzz.png"));
    }

    [Fact]
    public void The_same_file_name_in_two_results_is_an_error()
    {
        SearchResponse r = Fx.Response("q", ("a.png", 1, 1, null));
        SearchResult dup = r.Results[0] with { Hash = "other", Paths = [@"D:\elsewhere\a.png"] };
        r = r with { Results = [r.Results[0], dup] };
        Assert.Throws<InvalidDataException>(() => Metrics.IndexByFileName(r));
    }

    [Fact]
    public void A_result_with_two_paths_is_found_under_either_file_name()
    {
        SearchResponse r = Fx.Response("q", ("a.png", 1, 1, null));
        r = r with { Results = [r.Results[0] with { Paths = [@"C:\images\a.png", @"C:\images\copy of a.png"] }] };
        var byName = Metrics.IndexByFileName(r);
        Assert.Equal(1.0, Metrics.RanksOf(r, byName, "copy of a.png")[0].Rank);
    }

    [Fact]
    public void Precision_counts_relevant_judged_misses_and_unjudged_in_the_top_ten()
    {
        var items = Enumerable.Range(1, 12).Select(i => ($"{i}.png", (double)i, (double?)i, (double?)null)).ToArray();
        SearchResponse r = Fx.Response("猫", items);
        // 1, 2 が当たり。3 が外れ。4〜10 は未判定。11, 12 は上位 10 件の外なので数えない(当たりでも)
        Qrels q = Fx.Qrels("猫\t1.png\t1\ts", "猫\t2.png\t1\ts", "猫\t3.png\t0\ts", "猫\t11.png\t1\ts");
        PrecisionRow row = Metrics.Precision(r, Metrics.Fused, "猫", q);
        Assert.Equal((2, 1, 7, 10), (row.Relevant, row.JudgedMiss, row.Unjudged, row.Returned));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"{i}.png"), row.Top);
    }

    [Fact]
    public void Precision_for_a_path_uses_that_paths_order_not_the_fused_order()
    {
        // 融合後は a が 1 位だが、visual の 1 位は b
        SearchResponse r = Fx.Response("q", ("a.png", 1, 2, null), ("b.png", 2, 1, null));
        Qrels q = Fx.Qrels("q\tb.png\t1\ts");
        PrecisionRow visual = Metrics.Precision(r, "visual", "q", q);
        Assert.Equal(["b.png", "a.png"], visual.Top);
        Assert.Equal((1, 0, 1, 2), (visual.Relevant, visual.JudgedMiss, visual.Unjudged, visual.Returned));
        Assert.Equal(["a.png", "b.png"], Metrics.Precision(r, Metrics.Fused, "q", q).Top);
    }

    [Fact]
    public void Precision_for_a_sparse_path_counts_only_what_the_path_returned_and_the_rest_as_misses_in_the_denominator()
    {
        SearchResponse r = Fx.Response("q", ("a.png", 1, 1, 1), ("b.png", 2, 2, null), ("c.png", 3, 3, null));
        Qrels q = Fx.Qrels("q\ta.png\t1\ts");
        PrecisionRow full = Metrics.Precision(r, "fulltext", "q", q);
        Assert.Equal((1, 0, 0, 1), (full.Relevant, full.JudgedMiss, full.Unjudged, full.Returned));
        PrecisionSummary s = Metrics.Summarize("fulltext", [full]);
        Assert.Equal(10, s.Slots);          // 分母は常に 10 × クエリ数
        Assert.Equal(0.1, s.Lower, 10);
    }

    [Fact]
    public void Conflicting_judgements_on_two_names_of_the_same_image_are_an_error()
    {
        SearchResponse r = Fx.Response("q", ("a.png", 1, 1, null));
        r = r with { Results = [r.Results[0] with { Paths = [@"C:\i\a.png", @"C:\i\b.png"] }] };
        Qrels q = Fx.Qrels("q\ta.png\t1\ts", "q\tb.png\t0\ts");
        Assert.Throws<InvalidDataException>(() => Metrics.Precision(r, Metrics.Fused, "q", q));
    }

    [Fact]
    public void Precision_summary_gives_lower_and_upper_bounds()
    {
        var rows = new[]
        {
            new PrecisionRow("a", "fused", 3, 2, 5, 10, []),
            new PrecisionRow("b", "fused", 1, 4, 5, 10, []),
            new PrecisionRow("a", "visual", 9, 0, 0, 10, []),
        };
        PrecisionSummary s = Metrics.Summarize("fused", rows);
        Assert.Equal(2, s.Queries);
        Assert.Equal(0.2, s.Lower, 10);     // 4 / 20
        Assert.Equal(0.7, s.Upper, 10);     // (4 + 10) / 20
    }

    [Fact]
    public void Known_item_summary_uses_mean_ranks_for_the_thresholds_and_the_median()
    {
        double?[] ranks = [1, 4.5, 5, 20, 20.5, 300];
        List<KnownItemRow> rows = ranks.Select((r, i) => new KnownItemRow("visual", $"q{i}", "f", "fused", r)).ToList();
        rows.Add(new KnownItemRow("visual", "none", "f", "fused", null));
        rows.Add(new KnownItemRow("dialogue", "x", "f", "fused", 1));
        KnownItemSummary s = Metrics.Summarize("visual", "fused", rows);
        Assert.Equal(7, s.Count);
        Assert.Equal(1, s.Missing);
        Assert.Equal((1, 3, 4), (s.Top1, s.Top5, s.Top20));
        Assert.Equal((5 + 20) / 2.0, s.Median);
    }
}

public sealed class RunFilesTests
{
    [Fact]
    public void A_run_round_trips_through_the_tsv_files()
    {
        var data = new RunData(
            [new KnownItemRow("visual", "猫", "ス クリーン.png", "fused", 23.5), new KnownItemRow("visual", "猫", "ス クリーン.png", "visual", null), new KnownItemRow("dialogue", "あ！", "x.png", "fused", 1)],
            [new PrecisionRow("猫", "fused", 4, 3, 3, 10, ["a.png", "b.png"]), new PrecisionRow("犬", "visual", 0, 0, 0, 0, [])]);
        string dir = Path.Combine(Path.GetTempPath(), "bench-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunFiles.Write(dir, data, "summary");
            RunData back = RunFiles.Read(dir);
            Assert.Equal(data.KnownItems, back.KnownItems);
            Assert.Equal(3, back.Precision.Count + 1);
            Assert.Equal(data.Precision[0].Top, back.Precision[0].Top);
            Assert.Equal(data.Precision[0] with { Top = [] }, back.Precision[0] with { Top = [] });
            Assert.Empty(back.Precision[1].Top);
            Assert.Equal("summary", File.ReadAllText(Path.Combine(dir, RunFiles.SummaryFile)));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reading_a_file_with_an_unexpected_header_or_column_count_fails()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bench-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunFiles.Write(dir, new RunData([new KnownItemRow("visual", "q", "f", "fused", 1)], []), "s");
            File.AppendAllText(Path.Combine(dir, RunFiles.KnownItemsFile), "visual\tq\tf\tfused\n");
            Assert.Throws<InvalidDataException>(() => RunFiles.Read(dir));
            File.WriteAllText(Path.Combine(dir, RunFiles.KnownItemsFile), "wrong\n");
            Assert.Throws<InvalidDataException>(() => RunFiles.Read(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Writing_a_cell_with_a_tab_fails()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bench-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<ArgumentException>(() => RunFiles.Write(dir, new RunData([new KnownItemRow("visual", "a\tb", "f", "fused", 1)], []), "s"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public sealed class ComparisonTests
{
    private static KnownItemRow K(string q, string path, double? rank, string set = "visual") => new(set, q, q + ".png", path, rank);

    private static PrecisionRow P(string q, string path, int rel, int unj = 0) => new(q, path, rel, 10 - rel - unj, unj, 10, []);

    [Fact]
    public void Identical_runs_have_no_drops_and_are_not_worse()
    {
        var run = new RunData([K("a", "fused", 3), K("b", "fused", 50)], [P("a", "fused", 4)]);
        Comparison c = Comparison.Compare(run, run);
        Assert.Empty(c.KnownItemDrops);
        Assert.Empty(c.PrecisionDrops);
        Assert.False(c.AggregateWorse);
    }

    [Fact]
    public void A_rank_that_leaves_the_top_twenty_is_listed_first_and_makes_the_aggregate_worse()
    {
        var before = new RunData([K("a", "fused", 5), K("b", "fused", 18), K("c", "fused", 40)], []);
        var after = new RunData([K("a", "fused", 9), K("b", "fused", 21), K("c", "fused", 41)], []);
        Comparison c = Comparison.Compare(before, after);
        Assert.Equal(new[] { "b.png", "a.png", "c.png" }, c.KnownItemDrops.Select(d => d.Filename));   // 圏外へ → 下がった幅の大きい順
        Assert.True(c.KnownItemDrops[0].LeftTop20);
        Assert.False(c.KnownItemDrops[1].LeftTop20);
        Assert.True(c.AggregateWorse);                                                                    // 上位 20 位以内が 2 → 1
    }

    [Fact]
    public void Drops_that_do_not_change_the_top_twenty_count_are_listed_but_not_an_aggregate_worsening()
    {
        var before = new RunData([K("a", "fused", 5), K("b", "fused", 8)], []);
        var after = new RunData([K("a", "fused", 7), K("b", "fused", 6)], []);
        Comparison c = Comparison.Compare(before, after);
        Assert.Single(c.KnownItemDrops);
        Assert.Single(c.KnownItemImproved);
        Assert.False(c.AggregateWorse);
        Assert.Contains("a.png", c.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_tie_rank_difference_below_float_noise_is_not_a_drop()
    {
        var before = new RunData([K("a", "fused", 23.5)], []);
        var after = new RunData([K("a", "fused", 23.5 + 1e-12)], []);
        Assert.Empty(Comparison.Compare(before, after).KnownItemDrops);
    }

    [Fact]
    public void A_rank_that_disappears_counts_as_leaving_the_top_twenty()
    {
        var c = Comparison.Compare(new RunData([K("a", "visual", 3)], []), new RunData([K("a", "visual", null)], []));
        Assert.True(c.KnownItemDrops.Single().LeftTop20);
        Assert.Contains("圏外", c.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void Precision_drops_are_listed_per_query_and_path_and_note_new_unjudged_images()
    {
        var before = new RunData([], [P("a", "fused", 5, 0), P("b", "fused", 3, 0), P("c", "fused", 2, 0)]);
        var after = new RunData([], [P("a", "fused", 4, 2), P("b", "fused", 3, 0), P("c", "fused", 5, 0)]);
        Comparison c = Comparison.Compare(before, after);
        PrecisionDrop d = Assert.Single(c.PrecisionDrops);
        Assert.Equal(("a", 5, 4, 0, 2), (d.Query, d.Before, d.After, d.UnjudgedBefore, d.UnjudgedAfter));
        Assert.Single(c.PrecisionImproved);
        Assert.False(c.AggregateWorse);            // 当たりの合計 10 → 12
        Assert.Contains("未判定", c.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_precision_total_that_falls_makes_the_aggregate_worse()
    {
        var c = Comparison.Compare(new RunData([], [P("a", "fused", 5)]), new RunData([], [P("a", "fused", 4)]));
        Assert.True(c.AggregateWorse);
    }

    [Fact]
    public void Paths_present_on_only_one_side_are_reported_and_not_compared()
    {
        var before = new RunData([K("a", "fused", 3), K("a", "visual", 3)], [P("a", "fused", 4), P("a", "visual", 4)]);
        var after = new RunData([K("a", "fused", 3), K("a", "visual", 3), K("a", "fulltext", 1)], [P("a", "fused", 4), P("a", "visual", 4), P("a", "fulltext", 9)]);
        Comparison c = Comparison.Compare(before, after);
        Assert.False(c.AggregateWorse);
        Assert.Contains(c.Skipped, s => s.Contains("fulltext", StringComparison.Ordinal) && s.Contains("基準線に無い", StringComparison.Ordinal));
        Assert.DoesNotContain(c.PrecisionSummaries, s => s.After.Path == "fulltext");
        Assert.DoesNotContain(c.KnownItemSummaries, s => s.After.Path == "fulltext");
    }

    [Fact]
    public void Summaries_are_compared_over_the_queries_both_runs_have()
    {
        // 今回に新しいクエリ b が増えて、そこだけ順位が悪くても、共通のクエリ a の集計は変わらない
        var before = new RunData([K("a", "fused", 3)], []);
        var after = new RunData([K("a", "fused", 3), K("b", "fused", 400)], []);
        Comparison c = Comparison.Compare(before, after);
        Assert.False(c.AggregateWorse);
        Assert.Equal(1, c.KnownItemSummaries.Single().After.Count);
        Assert.Contains(c.Skipped, s => s.Contains("片方にしかない", StringComparison.Ordinal));
    }

    [Fact]
    public void Sets_are_compared_separately()
    {
        var before = new RunData([K("a", "fused", 3), K("a", "fused", 3, "dialogue")], []);
        var after = new RunData([K("a", "fused", 3), K("a", "fused", 30, "dialogue")], []);
        Comparison c = Comparison.Compare(before, after);
        Assert.Equal("dialogue", c.KnownItemDrops.Single().Set);
        Assert.True(c.AggregateWorse);
    }
}
