using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ohanami.Core;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>経路ごとのスコアの行(Metrics.ScoreRows)、分布の要約・ヒストグラム、返した分の当たり率、所要時間、scores.tsv の読み書きと、古い形式の run との互換。合成のデータだけ(DB・GPU・私物の画像は使わない)。</summary>
public sealed class ScoreRowTests
{
    private static ScoreRow Row(string path, string file, double? score, double? rank, bool top10, double? top1, string targets = "", bool? judged = null, string query = "q") =>
        new(query, file, targets, judged, path, score, rank, top10, top1, path == "fused" ? null : false);   // 旗は、経路の結果の旗(Fx.Response は補正なし)。融合後の行は null

    [Fact]
    public void Score_rows_carry_each_paths_score_rank_top10_and_the_query_top1()
    {
        // Fx.Response: 順位 r の経路のスコアは 1/r
        SearchResponse r = Fx.Response("q", ("a.png", 1, 2, 5), ("b.png", 2.5, 1, null), ("c.png", 3, null, 1));
        var byName = Metrics.IndexByFileName(r);
        Qrels qrels = Fx.Qrels("q\ta.png\t1\ts", "q\tb.png\t0\ts");
        var targets = new Dictionary<string, string> { ["c.png"] = "visual" };

        List<ScoreRow> rows = Metrics.ScoreRows("q", r, byName, ["c.png", "b.png", "a.png"], targets, qrels);

        Assert.Equal(9, rows.Count);   // 3 ファイル × (fused, visual, fulltext)。ファイル名の順
        Assert.Equal(["a.png", "a.png", "a.png", "b.png", "b.png", "b.png", "c.png", "c.png", "c.png"], rows.Select(x => x.Filename));
        ScoreRow Get(string file, string path) => rows.Single(x => x.Filename == file && x.Path == path);

        Assert.Equal(Row("fused", "a.png", 1.0, 1, true, 1.0, judged: true), Get("a.png", "fused"));
        Assert.Equal(Row("visual", "a.png", 0.5, 2, true, 1.0, judged: true), Get("a.png", "visual"));
        Assert.Equal(Row("fulltext", "a.png", 0.2, 5, true, 1.0, judged: true), Get("a.png", "fulltext"));
        Assert.Equal(Row("visual", "b.png", 1.0, 1, true, 1.0, judged: false), Get("b.png", "visual"));
        // 母集団に入っていない経路は、スコアも順位も無い(上位 10 でもない)。1 位のスコアはクエリの値のまま
        Assert.Equal(Row("fulltext", "b.png", null, null, false, 1.0, judged: false), Get("b.png", "fulltext"));
        Assert.Equal(Row("visual", "c.png", null, null, false, 1.0, "visual", null), Get("c.png", "visual"));
        Assert.Equal(Row("fulltext", "c.png", 1.0, 1, true, 1.0, "visual", null), Get("c.png", "fulltext"));
        Assert.Equal(1.0 / 2.5, Get("b.png", "fused").Score);
    }

    [Fact]
    public void The_top10_flag_is_the_same_selection_as_precision_at_10()
    {
        // 12 枚。経路ごとに上位 10 件の取り方(順位 → ファイル名)が precision@10 と同じこと
        (string, double, double?, double?)[] items = Enumerable.Range(1, 12).Select(i => ($"f{i:00}.png", (double)i, (double?)(13 - i), i <= 11 ? (double?)i : null)).ToArray();
        SearchResponse r = Fx.Response("q", items);
        var byName = Metrics.IndexByFileName(r);
        Qrels qrels = Fx.Qrels("q\tf01.png\t1\ts");
        List<ScoreRow> rows = Metrics.ScoreRows("q", r, byName, items.Select(x => x.Item1), new Dictionary<string, string>(), qrels);

        foreach (string path in Metrics.PathNames(r))
        {
            string[] flagged = rows.Where(x => x.Path == path && x.Top10).Select(x => x.Filename).Order(StringComparer.Ordinal).ToArray();
            string[] top = Metrics.Precision(r, path, "q", qrels).Top.Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(top, flagged);
            Assert.Equal(Math.Min(10, rows.Count(x => x.Path == path && x.Score is not null)), flagged.Length);
        }
        // visual は順位が逆(f12 が 1 位)なので、上位 10 に f01・f02 は入らない
        Assert.DoesNotContain(rows, x => x.Path == "visual" && x.Top10 && x.Filename is "f01.png" or "f02.png");
    }

    [Fact]
    public void A_file_missing_from_the_results_is_an_error()
    {
        SearchResponse r = Fx.Response("q", ("a.png", 1, 1, null));
        Assert.Throws<InvalidDataException>(() => Metrics.ScoreRows("q", r, Metrics.IndexByFileName(r), ["zzz.png"], new Dictionary<string, string>(), Fx.Qrels()));
    }

    [Fact]
    public void A_file_in_both_qrels_and_known_items_gets_one_row_per_path()
    {
        SearchResponse r = Fx.Response("q", ("a.png", 1, 1, null));
        List<ScoreRow> rows = Metrics.ScoreRows("q", r, Metrics.IndexByFileName(r), ["a.png", "a.png"], new Dictionary<string, string> { ["a.png"] = "dialogue|visual" }, Fx.Qrels("q\ta.png\t1\ts"));
        Assert.Equal(2, rows.Count);   // fused と visual
        Assert.All(rows, x => { Assert.Equal("dialogue|visual", x.Targets); Assert.True(x.Judged); });
    }
}

public sealed class ScoreReportTests
{
    private static ScoreRow V(string file, double cosine, bool top10, double top1, bool? judged, string targets = "", string query = "q1") =>
        new(query, file, targets, judged, "visual", cosine, null, top10, top1);

    private static ScoreRow F(string file, double rank, string query = "q1") => new(query, file, "", null, "fused", 0.01, rank, false, 0.02);

    /// <summary>1 クエリ(1 位の cosine 0.30)。当たり 2・外れ 2・目的 2(見た目 1 = 1 位そのもの、セリフ 1 = 融合 400 位)。</summary>
    private static List<ScoreRow> Sample() =>
    [
        V("r1", 0.20, true, 0.30, true), F("r1", 3),
        V("r2", 0.10, false, 0.30, true), F("r2", 30),
        V("r3", 0.25, true, 0.30, false), F("r3", 2),
        V("r4", 0.05, false, 0.30, false), F("r4", 900),
        V("t1", 0.30, true, 0.30, null, "visual"), F("t1", 1),
        V("t2", 0.12, false, 0.30, null, "dialogue"), F("t2", 400),
    ];

    [Fact]
    public void Populations_split_judged_top10_and_known_item_targets()
    {
        var pops = ScoreReport.Populations(Sample(), "visual").ToDictionary(p => p.Label, p => p.Values.Order().ToList());
        Assert.Equal([0.10, 0.20], pops["判定が当たり"]);
        Assert.Equal([0.05, 0.25], pops["判定が外れ"]);
        Assert.Equal([0.20], pops["当たりで、この経路の上位10"]);   // 守り 2 の (b)
        Assert.Equal([0.25], pops["外れで、この経路の上位10"]);     // 効き目の下限の母集団
        Assert.Equal([0.30], pops["known-item の目的(見た目)"]);
        Assert.Equal([0.12], pops["known-item の目的(セリフ)"]);
        Assert.Equal([0.30], pops["目的で、融合の上位300件以内"]);   // 守り 2 の (a): t2 は融合 400 位なので入らない
    }

    [Fact]
    public void Top1_per_query_and_the_gap_to_the_top1_are_computed_from_the_rows()
    {
        List<ScoreRow> rows = Sample();
        rows.Add(V("x", 0.18, true, 0.22, true, "visual", "q2"));
        Assert.Equal([0.22, 0.30], ScoreReport.QueryTop1(rows, "visual").Order());
        Assert.Equal([0.0, 0.04], ScoreReport.TargetGapToTop1(rows, "visual", "visual").Select(g => Math.Round(g, 9)));
        Assert.Equal([0.30 - 0.12], ScoreReport.TargetGapToTop1(rows, "visual", "dialogue").Select(g => Math.Round(g, 9)));
        Assert.Equal(3, ScoreReport.TargetGapToTop1(rows, "visual").Count);
    }

    [Fact]
    public void Quantiles_interpolate_linearly_and_reject_nan()
    {
        ScoreQuantiles d = ScoreQuantiles.Describe([5, 1, 4, 2, 3])!;
        Assert.Equal((5, 1.0, 1.4, 2.0, 3.0, 4.0, 4.6, 5.0), (d.Count, d.Min, Math.Round(d.P10, 9), d.P25, d.Median, d.P75, Math.Round(d.P90, 9), d.Max));
        ScoreQuantiles one = ScoreQuantiles.Describe([0.5])!;
        Assert.Equal((1, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5), (one.Count, one.Min, one.P10, one.P25, one.Median, one.P75, one.P90, one.Max));
        Assert.Null(ScoreQuantiles.Describe([]));
        Assert.Throws<ArgumentException>(() => ScoreQuantiles.Describe([1, double.NaN]));
    }

    [Fact]
    public void The_report_lists_every_population_with_counts_and_quantiles()
    {
        string text = ScoreReport.Render(Sample());
        Assert.Contains("判定が当たり: n=2  0.1000 / 0.1100 / 0.1250 / 0.1500 / 0.1750 / 0.1900 / 0.2000", text);
        Assert.Contains("外れで、この経路の上位10: n=1  0.2500 / 0.2500 / 0.2500 / 0.2500 / 0.2500 / 0.2500 / 0.2500", text);
        Assert.Contains("目的で、融合の上位300件以内: n=1", text);
        Assert.Contains("各クエリの 1 位の点(1 クエリ): n=1  0.3000", text);
        Assert.Contains("1 位との差: known-item の目的(見た目): n=1  0.0000", text);
        Assert.Contains("1 位との差: known-item の目的(セリフ): n=1  0.1800", text);
        Assert.Contains("目的の画像が見た目の経路の 1 位だった数: 1", text);
        Assert.Contains("ヒストグラム", text);
    }

    [Fact]
    public void The_visual_scores_are_split_into_queries_with_the_correction_and_queries_without_it()
    {
        // 点の水準が違う(補正をかけた検索は cosine − μ·r)ので、混ぜた 1 つの分布にしない。分けるのは、製品の検索の結果の旗。検索語から関門を計算し直さない
        // (検索語は旗と食い違う文字列にしてある: 旗が「補正あり」の検索語が英字、「補正なし」の検索語が短い日本語)
        ScoreRow Vq(string q, double score, bool corrected) => new(q, "x.png", "visual", true, "visual", score, 1, true, score, corrected);
        string text = ScoreReport.Render([Vq("night", 0.05, true), Vq("rain window", 0.07, true), Vq("夜の駅", 0.20, false), Vq("雨の窓辺", 0.22, false)]);

        int corrected = text.IndexOf("補正をかけた検索語(結果の旗が補正あり)", StringComparison.Ordinal);
        int plain = text.IndexOf("補正をかけない検索語(結果の旗が補正なし)", StringComparison.Ordinal);
        Assert.InRange(corrected, 0, plain - 1);
        string correctedPart = text[corrected..plain], plainPart = text[plain..];
        Assert.Contains("2 クエリ", correctedPart, StringComparison.Ordinal);
        Assert.Contains("各クエリの 1 位の点(2 クエリ): n=2  0.0500 / 0.0520 / 0.0550 / 0.0600 / 0.0650 / 0.0680 / 0.0700", correctedPart, StringComparison.Ordinal);
        Assert.Contains("2 クエリ", plainPart, StringComparison.Ordinal);
        Assert.Contains("各クエリの 1 位の点(2 クエリ): n=2  0.2000 / 0.2020 / 0.2050 / 0.2100 / 0.2150 / 0.2180 / 0.2200", plainPart, StringComparison.Ordinal);
        Assert.DoesNotContain("同じ尺度", text, StringComparison.Ordinal);

        // 片方の群しか無ければ、その群だけを出す
        string onlyPlain = ScoreReport.Render([Vq("night station", 0.20, false)]);
        Assert.DoesNotContain("[補正をかけた検索語", onlyPlain, StringComparison.Ordinal);
        Assert.Contains("[補正をかけない検索語", onlyPlain, StringComparison.Ordinal);
        Assert.DoesNotContain("[補正の有無が分からない", onlyPlain, StringComparison.Ordinal);

        // 旗の無い古い形式の run は、3 つ目の群に分けて出す(補正の有無を推測しない)
        string unknown = ScoreReport.Render([new ScoreRow("夜の駅", "x.png", "visual", true, "visual", 0.2, 1, true, 0.2)]);
        Assert.Contains("[補正の有無が分からない検索語", unknown, StringComparison.Ordinal);
        Assert.DoesNotContain("[補正をかけた検索語", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_rows_take_the_corrected_flag_from_the_response_paths()
    {
        SearchResponse r = Fx.Response("q", ("a.png", 1, 2, 5), ("b.png", 2.5, 1, null));
        SearchResponse flagged = r with { Paths = [.. r.Paths.Select(p => p.PathId == "visual" ? p with { ScoreCorrected = true } : p)] };
        List<ScoreRow> rows = Metrics.ScoreRows("q", flagged, Metrics.IndexByFileName(flagged), ["a.png"], new Dictionary<string, string>(), Fx.Qrels("q\ta.png\t1\ts"));
        Assert.True(rows.Single(x => x.Path == "visual").ScoreCorrected);
        Assert.Null(rows.Single(x => x.Path == "fused").ScoreCorrected);
        Assert.False(rows.Single(x => x.Path == "fulltext").ScoreCorrected);
    }

    [Fact]
    public void The_fulltext_section_counts_how_many_were_in_the_population()
    {
        var rows = new List<ScoreRow>
        {
            new("q", "a", "", true, "fulltext", 4.0, 1, true, 4.0),
            new("q", "b", "", true, "fulltext", null, null, false, 4.0),
            new("q", "c", "visual", null, "fulltext", 2.0, 2, true, 4.0),
            new("q", "d", "", false, "fulltext", null, null, false, 4.0),
        };
        string text = ScoreReport.Render(rows);
        Assert.Contains("判定が当たり: 入っていた 1/2", text);
        Assert.Contains("判定が外れ: 入っていた 0/1", text);
        Assert.Contains("known-item の目的(見た目): 入っていた 1/1", text);
        Assert.DoesNotContain("ヒストグラム", text);   // 見た目の経路が無いので cosine の節は出ない
    }

    [Fact]
    public void Empty_scores_give_an_empty_report()
    {
        Assert.Equal("", ScoreReport.Render([]));
    }

    [Fact]
    public void Histogram_bins_are_shared_and_bars_are_relative_to_each_populations_max()
    {
        var pops = new List<(string, List<double>)> { ("A", [0.10, 0.10, 0.11]), ("B", [0.13]) };
        string text = ScoreReport.Histograms(pops);
        string bar40 = new('#', 40);
        Assert.Contains("〜 " + bar40 + " 2", text);                           // A の 0.10 区間(最大 = 40 文字)
        Assert.Contains("〜 " + new string('#', 20).PadRight(40) + " 1", text); // A の 0.11 区間(半分 = 20 文字)
        Assert.Contains("A(n=3)", text);
        Assert.Contains("B(n=1)", text);
        // 区間は共通: 0.10, 0.11, 0.12, 0.13 の 4 行ずつ
        Assert.Equal(8, text.Split('\n').Count(l => l.Contains("〜")));
        Assert.Contains(" 0.13〜", text);
    }

    [Fact]
    public void Bin_layout_widens_when_the_range_is_large_and_every_value_lands_in_a_bin()
    {
        (double w, double start, int bins) = ScoreReport.BinLayout(0.052, 0.187);
        Assert.Equal((0.01, 0.05, 14), (w, Math.Round(start, 9), bins));
        (double w2, double start2, int bins2) = ScoreReport.BinLayout(-1, 1);
        Assert.True(bins2 <= 40 && w2 > 0.01);
        int[] counts = ScoreReport.Bin([-1, -0.3, 0, 0.77, 1], w2, start2, bins2);
        Assert.Equal(5, counts.Sum());
        // 区間の下端の値は、その区間に入る(0.05 は [0.05, 0.06))
        Assert.Equal([1, 1, 1], ScoreReport.Bin([0.05, 0.06, 0.0700001], 0.01, 0.05, 3));
        Assert.Equal([1, 2, 0], ScoreReport.Bin([0.05, 0.06, 0.0699999], 0.01, 0.05, 3));   // 0.07 未満は 1 つ手前の区間
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreReport.Bin([0.2], 0.01, 0.05, 3));
    }

    [Fact]
    public void Stage_clock_records_each_stage_from_the_previous_mark_and_the_total()
    {
        double now = 100;
        var clock = new StageClock(() => now);
        now = 102.5; clock.Mark("走査");
        now = 102.5; clock.Mark("埋め込み");   // 何もしなかった段階は 0 秒
        now = 110; clock.Mark("検索");
        Assert.Equal([new StageTime("走査", 2.5), new StageTime("埋め込み", 0), new StageTime("検索", 7.5)], clock.Stages);
        Assert.Equal(10.0, clock.Total);
        string text = ScoreReport.RenderTimings(clock.Stages);
        Assert.Contains("  走査: 2.5 秒", text);
        Assert.Contains("  埋め込み: 0.0 秒", text);
        Assert.Contains("  検索: 7.5 秒", text);
        Assert.Contains("  合計: 10.0 秒", text);
        Assert.Equal("", ScoreReport.RenderTimings(null));
        Assert.Equal("", ScoreReport.RenderTimings([]));
    }
}

public sealed class HitRateTests
{
    [Fact]
    public void The_hit_rate_of_what_was_returned_is_relevant_over_returned_not_over_the_slots()
    {
        // 10 クエリ(= 100 枠)のうち、全文が 40 件だけ返し、当たり 30・外れ 8・未判定 2
        var s = new PrecisionSummary("fulltext", 10, 30, 8, 2, 40);
        Assert.Equal(30 / 100.0, s.Lower);          // 下限は、枠全体が分母
        Assert.Equal(30 / 40.0, s.HitRateLower);    // 返した分の当たり率
        Assert.Equal(32 / 40.0, s.HitRateUpper);
        Assert.True(double.IsNaN(new PrecisionSummary("fulltext", 10, 0, 0, 0, 0).HitRateLower));
        Assert.True(double.IsNaN(new PrecisionSummary("fulltext", 10, 0, 0, 0, 0).HitRateUpper));
    }

    [Fact]
    public void The_summary_prints_the_hit_rate_per_path_and_the_sections_only_when_there_is_data()
    {
        var prec = new List<PrecisionRow>
        {
            new("q1", "fused", 5, 3, 2, 10, []), new("q1", "visual", 4, 6, 0, 10, []), new("q1", "fulltext", 3, 0, 1, 4, []),
            new("q2", "fused", 5, 5, 0, 10, []), new("q2", "visual", 2, 8, 0, 10, []), new("q2", "fulltext", 0, 0, 0, 0, []),
        };
        var info = new RunInfo("t", DateTimeOffset.Now, "dir", 10, 10, 1, "");
        string[] names = ["fused", "visual", "fulltext"];

        string oldFormat = Report.Summary(info, new RunData([], prec), names, 2);   // 古い形式(スコアも所要時間も無い)
        Assert.Contains("[返した分の当たり率", oldFormat);
        Assert.Contains("fulltext 75.0%(下限。3/4)  上限 100.0%", oldFormat);   // 返した 4 件(q2 は 0 件)のうち当たり 3、未判定 1
        Assert.Contains("fused    50.0%(下限。10/20)  上限 60.0%", oldFormat);
        Assert.Contains("visual   30.0%(下限。6/20)  上限 30.0%", oldFormat);
        Assert.Contains("fulltext 返した件数 0(当たり率なし)", Report.Summary(info, new RunData([], [new PrecisionRow("q1", "fulltext", 0, 0, 0, 0, [])]), ["fulltext"], 1));
        Assert.DoesNotContain("経路ごとのスコア", oldFormat);
        Assert.DoesNotContain("所要時間", oldFormat);

        var info2 = info with { Stages = [new StageTime("走査", 1.0), new StageTime("検索(2 クエリ)", 2.5)] };
        string full = Report.Summary(info2, new RunData([], prec, [new ScoreRow("q1", "a", "", true, "visual", 0.2, 1, true, 0.2)]), names, 2);
        Assert.Contains("[経路ごとのスコア", full);
        Assert.Contains("[所要時間", full);
        Assert.Contains("3.5 秒", full);   // 合計 = 1.0 + 2.5
    }
}

public sealed class ScoresFileAndCompatibilityTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "bench-test-" + Guid.NewGuid().ToString("N"));

    private static readonly IReadOnlyList<ScoreRow> SampleScores =
    [
        new("猫", "a b.png", "visual|dialogue", true, "visual", 0.1234567890123, 12.5, true, 0.3, true),
        new("猫", "a b.png", "visual|dialogue", true, "fulltext", null, null, false, null),
        new("猫", "c.png", "", null, "fused", 0.0163, 3, false, 0.0328),
        new("犬", "d.png", "", false, "fulltext", -1.5, 7, false, 4.25),
        new("犬", "d.png", "", false, "visual", 0.21, 8, false, 0.25, false),
    ];

    private static RunData Run(IReadOnlyList<ScoreRow>? scores) => new(
        [new KnownItemRow("visual", "猫", "a b.png", "fused", 23.5), new KnownItemRow("visual", "猫", "a b.png", "visual", 12.5)],
        [new PrecisionRow("猫", "fused", 4, 3, 3, 10, ["a b.png"]), new PrecisionRow("猫", "visual", 4, 6, 0, 10, [])],
        scores);

    [Fact]
    public void Scores_round_trip_exactly_including_nulls_and_full_precision()
    {
        string dir = TempDir();
        try
        {
            RunFiles.Write(dir, Run(SampleScores), "s");
            RunData back = RunFiles.Read(dir);
            Assert.Equal(SampleScores, back.Scores);
            Assert.Equal("query\tfilename\ttargets\tjudged\tpath\tscore\trank\ttop10\tpath_top1\tscore_corrected", File.ReadAllLines(Path.Combine(dir, RunFiles.ScoresFile))[0]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_scores_file_without_the_flag_column_reads_with_the_flag_unknown()
    {
        string dir = TempDir();
        try
        {
            RunFiles.Write(dir, Run(SampleScores), "s");
            string path = Path.Combine(dir, RunFiles.ScoresFile);
            // 旗の列を足す前の形式(9 列)に直す
            string[] lines = File.ReadAllLines(path);
            File.WriteAllLines(path, lines.Select(l => string.Join('\t', l.Split('\t').Take(9))));
            RunData back = RunFiles.Read(dir);
            Assert.Equal(SampleScores.Select(r => r with { ScoreCorrected = null }), back.Scores);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_scores_flag_that_is_not_1_0_or_empty_is_an_error()
    {
        string dir = TempDir();
        try
        {
            RunFiles.Write(dir, Run(SampleScores), "s");
            string path = Path.Combine(dir, RunFiles.ScoresFile);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\t0.3\t1\n", "\t0.3\t2\n"));
            Assert.Throws<InvalidDataException>(() => RunFiles.Read(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_run_without_scores_writes_no_file_and_reads_back_null()
    {
        string dir = TempDir();
        try
        {
            RunFiles.Write(dir, Run(null), "s");
            Assert.False(File.Exists(Path.Combine(dir, RunFiles.ScoresFile)));
            Assert.Null(RunFiles.Read(dir).Scores);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_broken_scores_file_is_an_error_not_silently_ignored()
    {
        string dir = TempDir();
        try
        {
            RunFiles.Write(dir, Run(SampleScores), "s");
            string path = Path.Combine(dir, RunFiles.ScoresFile);
            string good = File.ReadAllText(path);

            File.WriteAllText(path, "wrong header\n");
            Assert.Throws<InvalidDataException>(() => RunFiles.Read(dir));

            File.WriteAllText(path, good.Replace("\t1\tvisual\t", "\t2\tvisual\t"));   // judged が 1・0・空でない
            Assert.Throws<InvalidDataException>(() => RunFiles.Read(dir));

            File.WriteAllText(path, good + "猫\tx.png\t\t\tvisual\tabc\t1\t1\t0.3\n");   // score が数でない
            Assert.Throws<InvalidDataException>(() => RunFiles.Read(dir));

            File.WriteAllText(path, good + "猫\tx.png\t\t\tvisual\t0.1\t1\t1\n");   // 列が足りない
            Assert.Throws<InvalidDataException>(() => RunFiles.Read(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_cell_with_a_tab_in_the_scores_fails()
    {
        string dir = TempDir();
        try
        {
            Assert.Throws<ArgumentException>(() => RunFiles.Write(dir, Run([new ScoreRow("a\tb", "f", "", null, "visual", 1, 1, true, 1)]), "s"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>古い書式そのまま(列名と並び)の基準線。ここを書き換えたら、古い基準線を読めなくなる印。</summary>
    private static void WriteOldFormatBaseline(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "known-items.tsv"),
            "set\tquery\tfilename\tpath\trank\n" +
            "visual\t猫\ta b.png\tfused\t30\n" +
            "visual\t猫\ta b.png\tvisual\t10\n" +
            "visual\t猫\ta b.png\tfulltext\t\n");
        File.WriteAllText(Path.Combine(dir, "precision.tsv"),
            "query\tpath\trelevant\tjudged_miss\tunjudged\treturned\ttop10\n" +
            "猫\tfused\t4\t3\t3\t10\ta b.png|c.png\n" +
            "猫\tvisual\t4\t6\t0\t10\ta b.png\n" +
            "猫\tfulltext\t0\t0\t0\t0\t\n");
        File.WriteAllText(Path.Combine(dir, "summary.txt"), "old format summary");
        File.WriteAllText(Path.Combine(dir, "from.txt"), "x\n");
    }

    [Fact]
    public void An_old_format_baseline_reads_without_scores_and_compares_with_a_new_run_that_has_them()
    {
        string root = TempDir();
        try
        {
            string baselineDir = Path.Combine(root, "baseline");
            WriteOldFormatBaseline(baselineDir);
            RunData baseline = RunFiles.Read(baselineDir);
            Assert.Null(baseline.Scores);
            Assert.Equal(3, baseline.KnownItems.Count);
            Assert.Null(baseline.KnownItems[2].Rank);

            // 新しい run: 同じ順位 + scores.tsv(書いて読み戻した形で比べる)
            string runDir = Path.Combine(root, "runs", "20261110-000000-new");
            RunData now = new([.. baseline.KnownItems], [.. baseline.Precision], SampleScores);
            RunFiles.Write(runDir, now, "s");
            RunData nowBack = RunFiles.Read(runDir);
            Assert.NotNull(nowBack.Scores);

            Comparison withScores = Comparison.Compare(baseline, nowBack);
            Comparison without = Comparison.Compare(baseline, baseline);
            Assert.Equal(without.Render(), withScores.Render());   // スコアの有る無しで、比較の出力は 1 字も変わらない
            Assert.False(withScores.AggregateWorse);
            Assert.Empty(withScores.Skipped);

            // 順位が上がった run は下がりではない。逆向き(上がった run を基準にすると、元の run は 20 位以内から落ちる)は検出する: スコアがあっても比較は働く
            RunData better = new([baseline.KnownItems[0] with { Rank = 5 }, .. baseline.KnownItems.Skip(1)], baseline.Precision, SampleScores);
            Assert.False(Comparison.Compare(baseline, better).AggregateWorse);
            Assert.True(Comparison.Compare(better, baseline).AggregateWorse);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void The_baseline_command_copies_scores_when_the_run_has_them_and_not_otherwise()
    {
        string root = TempDir();
        try
        {
            string withScores = Path.Combine(root, "runs", "a");
            string oldFormat = Path.Combine(root, "runs", "b");
            RunFiles.Write(withScores, Run(SampleScores), "s");
            WriteOldFormatBaseline(oldFormat);

            Assert.Equal(0, Program.SetBaseline(root, [withScores]));
            string baseline = Path.Combine(root, "baseline");
            Assert.True(File.Exists(Path.Combine(baseline, RunFiles.ScoresFile)));
            Assert.Equal(SampleScores, RunFiles.Read(baseline).Scores);

            // 古い形式の run を基準線にし直すと、前の基準線の scores.tsv は残らない
            Assert.Equal(0, Program.SetBaseline(root, [oldFormat]));
            Assert.False(File.Exists(Path.Combine(baseline, RunFiles.ScoresFile)));
            Assert.Null(RunFiles.Read(baseline).Scores);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

/// <summary>任意の known-item のファイル(known-items-one-image.tsv。集合 "one-image")と、基準線に無い集合の行の扱い。</summary>
public sealed class OneImageKnownItemsTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "bench-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void An_optional_known_items_file_that_does_not_exist_gives_nothing()
    {
        string dir = TempDir();
        List<KnownItem> items = BenchInputs.ReadOptionalKnownItems(Path.Combine(dir, BenchInputs.OneImageKnownItemsFile), BenchInputs.OneImageSet, out int skipped);
        Assert.Empty(items);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void An_optional_known_items_file_is_read_like_the_record_files_with_an_empty_english_column()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, BenchInputs.OneImageKnownItemsFile);
            File.WriteAllText(path, "filename\tquery_ja\tquery_en\nあ い.png\t掃除\t\nあ い.png\t毛布\t\nあ い.png\t\t\n");   // 3 行目はクエリなし
            List<KnownItem> items = BenchInputs.ReadOptionalKnownItems(path, BenchInputs.OneImageSet, out int skipped);
            Assert.Equal([new KnownItem("one-image", "あ い.png", "掃除"), new KnownItem("one-image", "あ い.png", "毛布")], items);
            Assert.Equal(1, skipped);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_optional_known_items_file_that_exists_but_is_malformed_is_an_error_not_silently_empty()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, BenchInputs.OneImageKnownItemsFile);
            File.WriteAllText(path, "name\tquery\nx.png\tq\n");
            Assert.Throws<InvalidDataException>(() => BenchInputs.ReadOptionalKnownItems(path, BenchInputs.OneImageSet, out _));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_file_with_three_rows_for_one_target_gives_three_items_with_the_same_file_name()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bench-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, BenchInputs.OneImageKnownItemsFile);
            File.WriteAllText(path, "filename\tquery_ja\tquery_en\nあ い.png\t掃除\t\nあ い.png\t毛布\t\nあ い.png\t銀髪\t\n");
            List<KnownItem> items = BenchInputs.ReadOptionalKnownItems(path, BenchInputs.OneImageSet, out int skipped);
            Assert.Equal(0, skipped);
            Assert.Equal(["掃除", "毛布", "銀髪"], items.Select(i => i.Query));
            Assert.Single(items.Select(i => i.Filename).Distinct());
            Assert.All(items, i => Assert.Equal("one-image", i.Set));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static KnownItemRow K(string set, string q, string path, double? rank) => new(set, q, q + ".png", path, rank);

    [Fact]
    public void Rows_of_a_set_the_baseline_does_not_have_are_new_rows_not_drops_and_not_an_error()
    {
        var baseline = new RunData([K("visual", "a", "fused", 3), K("visual", "a", "visual", 3)], [new PrecisionRow("a", "fused", 4, 6, 0, 10, [])]);
        var current = new RunData(
            [K("visual", "a", "fused", 3), K("visual", "a", "visual", 3), K("one-image", "掃除", "fused", 500), K("one-image", "掃除", "visual", null), K("one-image", "銀髪", "fused", 126)],
            [new PrecisionRow("a", "fused", 4, 6, 0, 10, [])]);
        Comparison c = Comparison.Compare(baseline, current);
        Assert.Empty(c.KnownItemDrops);
        Assert.False(c.AggregateWorse);
        Assert.DoesNotContain(c.KnownItemSummaries, s => s.Before.Set == "one-image");
        Assert.Contains(c.Skipped, s => s.Contains("集合「one-image」は基準線に無い") && s.Contains("新しい行 3 行"));
        Assert.DoesNotContain(c.Skipped, s => s.Contains("片方にしかない行"));   // 二重に数えない
        Assert.Contains("集合「one-image」は基準線に無い", c.Render());
    }

    [Fact]
    public void Rows_of_the_new_set_are_compared_once_the_baseline_has_them()
    {
        var baseline = new RunData([K("one-image", "掃除", "fused", 10), K("one-image", "銀髪", "fused", 50)], []);
        var current = new RunData([K("one-image", "掃除", "fused", 30), K("one-image", "銀髪", "fused", 50)], []);
        Comparison c = Comparison.Compare(baseline, current);
        Assert.Equal(["掃除"], c.KnownItemDrops.Select(d => d.Query));
        Assert.True(c.KnownItemDrops[0].LeftTop20);
        Assert.True(c.AggregateWorse);    // one-image の 20 位以内の数が 1 → 0
        Assert.Empty(c.Skipped);
    }

    [Fact]
    public void A_set_that_only_the_baseline_has_is_reported_and_not_compared()
    {
        var baseline = new RunData([K("visual", "a", "fused", 3), K("one-image", "掃除", "fused", 10)], []);
        var current = new RunData([K("visual", "a", "fused", 3)], []);
        Comparison c = Comparison.Compare(baseline, current);
        Assert.False(c.AggregateWorse);
        Assert.Contains(c.Skipped, s => s.Contains("集合「one-image」は今回の結果に無い"));
    }

    /// <summary>追加の known-item の集合(集合名は one-image。同じ 1 枚を複数の語で探す)。</summary>
    [Fact]
    public void The_summary_lists_the_extra_set_separately_and_the_scores_show_it_only_when_present()
    {
        var info = new RunInfo("t", DateTimeOffset.Now, "dir", 10, 10, 1, "");
        var known = new List<KnownItemRow> { K("visual", "a", "fused", 3), K("one-image", "掃除", "fused", 126), K("one-image", "銀髪", "fused", 5) };
        string text = Report.Summary(info, new RunData(known, []), ["fused"], 0);
        Assert.Contains("見た目  fused", text);
        Assert.Contains("one-image fused", text);
        Assert.Contains("1/2", text.Split('\n').Single(l => l.Contains("one-image fused")));

        // スコアの節: one-image の目的があれば one-image の行が出る。無ければ出ない
        ScoreRow V(string q, string targets) => new(q, "x.png", targets, null, "visual", 0.1, 1, true, 0.2);
        Assert.Contains("known-item の目的(one-image): n=1", ScoreReport.Render([V("掃除", "one-image")]));
        Assert.Contains("1 位との差: known-item の目的(one-image)", ScoreReport.Render([V("掃除", "one-image")]));
        Assert.DoesNotContain("(one-image)", ScoreReport.Render([V("猫", "visual")]));
    }
}
