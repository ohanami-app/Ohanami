using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Core;
using Ohanami.Core.Embedding.Generated;
using Ohanami.Core.Search;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>
/// 繰り返し出る画像の物差し(RepeatMetrics)、上位 10 件の表(top10.tsv)の保存・比較、μ の作り方(HubnessMean)と run の要約に書く μ の出どころ。
/// 合成のデータだけ(私物・GPU・モデルは使わない)。
/// </summary>
public sealed class RepeatMetricsTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "bench-test-" + Guid.NewGuid().ToString("N"));

    private static float[] V(double x, double y)
    {
        double n = Math.Sqrt(x * x + y * y);
        return [(float)(x / n), (float)(y / n)];
    }

    /// <summary>製品の検索と同じ作りの結果(全件): 見た目は全画像、全文は一部。融合は RankedPath.Rrf(k = 60)で、順位は同点の平均順位。</summary>
    private static SearchResponse Product(string query, string[] files, double[] visual, Dictionary<int, double>? fulltext = null)
    {
        int n = files.Length;
        RankedPath vis = RankedPath.Dense(visual);
        RankedPath? ft = fulltext is null ? null : RankedPath.Sparse(n, fulltext.Select(kv => (kv.Key, kv.Value)));
        RankedPath fused = RankedPath.Rrf(ft is null ? [vis] : [vis, ft], SearchService.RrfK);
        var results = new List<SearchResult>();
        foreach (int i in fused.Order)
        {
            var hits = new List<PathHit> { new("visual", "m", vis.MeanRank[i], vis.Score[i], new VisualDetail(vis.Score[i], vis.Score[i])) };
            if (ft is not null && ft.Present[i]) hits.Add(new PathHit("fulltext", null, ft.MeanRank[i], ft.Score[i], new TextDetail([])));
            results.Add(new SearchResult(Fx.Hash(files[i]), [@"C:\images\" + files[i]], fused.MeanRank[i], fused.Score[i], hits));
        }
        var outcomes = new List<PathOutcome> { new("visual", "m", n, PathState.Ok, null) };
        if (ft is not null) outcomes.Add(new PathOutcome("fulltext", null, fulltext!.Count, PathState.Ok, null));
        return new SearchResponse(query, results, outcomes, n, n, TimeSpan.Zero);
    }

    private static BenchRunner.Collected Collected(Dictionary<string, SearchResponse> responses, IReadOnlyList<KnownItem> items, Qrels qrels, int images)
    {
        var byName = responses.ToDictionary(kv => kv.Key, kv => Metrics.IndexByFileName(kv.Value), StringComparer.Ordinal);
        string[] queries = [.. items.Select(i => i.Query).Concat(qrels.Queries).Distinct(StringComparer.Ordinal)];
        return new BenchRunner.Collected(new BenchInputs.InputSet(items, qrels, "入力"), queries, responses, byName, images, images, 0, 4, [TimeSpan.FromMilliseconds(5)]);
    }

    private static readonly string[] Files = ["a.png", "b.png", "c.png", "d.png"];

    // ---------------------------------------------------------------- 参照の語

    // ---------------------------------------------------------------- 上位 10 件の表

    [Fact]
    public void Top_rows_have_the_text_match_and_the_judgement_and_mark_queries_outside_qrels()
    {
        var q1 = Product("猫", Files, [0.4, 0.3, 0.2, 0.1], new Dictionary<int, double> { [2] = 5 });   // c だけ全文に一致
        var q2 = Product("犬", Files, [0.1, 0.2, 0.3, 0.4]);                                            // known-item だけのクエリ
        KnownItem[] items = [new("visual", "d.png", "犬")];
        Qrels qrels = Fx.Qrels("猫\ta.png\t1\ts", "猫\tc.png\t0\ts");
        BenchRunner.Collected c = Collected(new() { ["猫"] = q1, ["犬"] = q2 }, items, qrels, 4);

        List<TopRow> rows = RepeatMetrics.TopRows(c);
        List<TopRow> fusedCat = [.. rows.Where(r => r.Query == "猫" && r.Path == "fused")];
        Assert.Equal(4, fusedCat.Count);
        Assert.Equal([1, 2, 3, 4], fusedCat.Select(r => r.Pos));
        Assert.Equal("c.png", fusedCat[0].Filename);                      // 全文と見た目の両方に出る c が融合の 1 位
        Assert.Equal(["c.png"], fusedCat.Where(r => r.TextMatched).Select(r => r.Filename));
        Assert.Equal(JudgeKind.Miss, fusedCat.Single(r => r.Filename == "c.png").Judge);
        Assert.Equal(JudgeKind.Hit, fusedCat.Single(r => r.Filename == "a.png").Judge);
        Assert.Equal(JudgeKind.Unjudged, fusedCat.Single(r => r.Filename == "b.png").Judge);
        Assert.All(rows.Where(r => r.Query == "犬"), r => Assert.Equal(JudgeKind.NoQrels, r.Judge));
        Assert.Equal(["c.png"], rows.Where(r => r.Query == "猫" && r.Path == "fulltext").Select(r => r.Filename));   // 全文だけの上位は、ヒットした画像だけ
        Assert.Equal(["a.png", "b.png", "c.png", "d.png"], rows.Where(r => r.Query == "猫" && r.Path == "visual").Select(r => r.Filename));
    }

    // ---------------------------------------------------------------- ① 繰り返し

    private static TopRow T(string query, string path, int pos, string file, bool text = false, JudgeKind judge = JudgeKind.Unjudged) => new(query, path, pos, file, text, judge);

    [Fact]
    public void Repeat_stats_count_distinct_images_repeat_images_their_slots_and_the_maximum_number_of_queries()
    {
        var rows = new List<TopRow>();
        for (int q = 0; q < 6; q++)
        {
            rows.Add(T($"q{q}", "fused", 1, "hub.png"));                          // 6 クエリに出る → 繰り返し
            rows.Add(T($"q{q}", "fused", 2, q < 5 ? "near.png" : "x.png"));       // 5 クエリに出る → 繰り返し(線は 5 以上)
            rows.Add(T($"q{q}", "fused", 3, $"own{q}.png"));                      // 1 クエリだけ
            rows.Add(T($"q{q}", "visual", 1, $"own{q}.png"));                     // 別の経路は混ぜない
        }
        RepeatStats s = RepeatMetrics.Repeat(rows, "fused");
        Assert.Equal(6, s.Queries);
        Assert.Equal(18, s.Slots);
        Assert.Equal(9, s.DistinctImages);                // hub、near、x、own0〜own5
        Assert.Equal(2, s.RepeatImages);                  // hub と near
        Assert.Equal(11, s.RepeatSlots);                  // hub 6 + near 5
        Assert.Equal(6, s.MaxQueriesPerImage);
        Assert.Equal(11.0 / 18, s.RepeatRatio, 9);
        RepeatStats v = RepeatMetrics.Repeat(rows, "visual");
        Assert.Equal((6, 6, 0, 0), (v.DistinctImages, v.Slots, v.RepeatImages, v.RepeatSlots));
        // qrels の無いクエリ(known-item だけ)の行は、数えない
        rows.Add(T("known-only", "fused", 1, "hub.png", false, JudgeKind.NoQrels));
        Assert.Equal(s, RepeatMetrics.Repeat(rows, "fused"));
        Assert.Equal(0, RepeatMetrics.Repeat(rows, "none").Slots);   // 経路が無ければ 0 件
        // 同じクエリに同じ画像が 2 回あっても、クエリの数は 1(数えるのはクエリ)
        Assert.Equal(1, RepeatMetrics.Repeat([T("q", "p", 1, "a"), T("q", "p", 2, "a")], "p").MaxQueriesPerImage);
    }

    // ---------------------------------------------------------------- ② 当たり率の内訳

    [Fact]
    public void Breakdown_splits_the_fused_top_10_by_text_match_by_the_number_of_text_matches_per_query_and_by_repeat()
    {
        var rows = new List<TopRow>();
        // 6 つの qrels のクエリ(qa〜qf)。h.png は qa・qb・qd・qe・qf の上位に出る(繰り返し)。
        // qa: 全文一致 0 枚(見た目だけ)。h = 外れ、v = 当たり
        // qb: 全文一致 2 枚(1〜9)。h = 外れ(見た目だけ)、t1 = 当たり(全文)、t2 = 未判定(全文)
        // qc: 全文一致 10 枚(上位が全部全文): 10 枚のうち 4 枚当たり、6 枚外れ(h は入らない)
        // qd, qe: h だけ(全文一致 0 枚)
        rows.Add(T("qa", "fused", 1, "h.png", false, JudgeKind.Miss));
        rows.Add(T("qa", "fused", 2, "v.png", false, JudgeKind.Hit));
        rows.Add(T("qb", "fused", 1, "h.png", false, JudgeKind.Miss));
        rows.Add(T("qb", "fused", 2, "t1.png", true, JudgeKind.Hit));
        rows.Add(T("qb", "fused", 3, "t2.png", true, JudgeKind.Unjudged));
        for (int i = 0; i < 10; i++) rows.Add(T("qc", "fused", i + 1, $"c{i}.png", true, i < 4 ? JudgeKind.Hit : JudgeKind.Miss));
        rows.Add(T("qd", "fused", 1, "h.png", false, JudgeKind.Miss));
        rows.Add(T("qe", "fused", 1, "h.png", false, JudgeKind.Miss));
        rows.Add(T("qf", "fused", 1, "h.png", false, JudgeKind.Miss));   // h は 5 クエリ(qa・qb・qd・qe・qf)に出る → 繰り返し
        rows.Add(T("known-only", "fused", 1, "h.png", false, JudgeKind.NoQrels));   // qrels のクエリでない行は、内訳にも繰り返しの数にも入れない

        Dictionary<string, HitBreakdownRow> b = RepeatMetrics.Breakdown(rows, "fused").ToDictionary(r => r.Label);
        // 全文に一致した枠: qb の 2 + qc の 10 = 12、当たり 1 + 4 = 5、外れ 6、未判定 1
        Assert.Equal((2, 12, 5, 6, 1), Of(b["全文に一致した枠"]));
        // 見た目の経路だけの枠: qa の 2(h 外れ・v 当たり)+ qb の h + qd の h + qe の h = 5 枠、当たり 1、外れ 4(クエリは qa・qb・qd・qe の 4 つ)
        Assert.Equal((5, 6, 1, 5, 0), Of(b["見た目の経路だけの枠"]));
        Assert.Equal((4, 5, 1, 4, 0), Of(b["クエリの全文一致 0 枚"]));      // qa(2 枠)、qd、qe、qf
        Assert.Equal((1, 3, 1, 1, 1), Of(b["クエリの全文一致 1〜9 枚"]));   // qb
        Assert.Equal((1, 10, 4, 6, 0), Of(b["クエリの全文一致 10 枚"]));    // qc
        // 繰り返し画像 = h だけ(qa・qb・qd・qe・qf の 5 クエリ。known-only は数えない)。qrels の枠は qa・qb・qd・qe・qf の 5 枠(どれも外れ)
        Assert.Equal((5, 5, 0, 5, 0), Of(b["繰り返し画像の枠"]));
        Assert.Equal((3, 13, 6, 6, 1), Of(b["繰り返し以外の枠"]));          // qa の v、qb の t1・t2、qc の 10 枠
        Assert.Equal((0, 0, 0, 0, 0), Of(b["全文一致 かつ 繰り返し"]));
        Assert.Equal((2, 12, 5, 6, 1), Of(b["全文一致 かつ 繰り返し以外"]));
        Assert.Equal((5, 5, 0, 5, 0), Of(b["見た目だけ かつ 繰り返し"]));
        Assert.Equal((1, 1, 1, 0, 0), Of(b["見た目だけ かつ 繰り返し以外"]));   // qa の v だけ
        Assert.Equal(5.0 / 12, b["全文に一致した枠"].Lower, 9);
        Assert.Equal(6.0 / 12, b["全文に一致した枠"].Upper, 9);
        Assert.True(double.IsNaN(b["全文一致 かつ 繰り返し"].Lower));       // 枠が無ければ当たり率なし
    }

    private static (int, int, int, int, int) Of(HitBreakdownRow r) => (r.Queries, r.Slots, r.Relevant, r.Miss, r.Unjudged);

    [Fact]
    public void Hit_rate_is_given_two_ways_unjudged_as_misses_and_judged_slots_only_and_the_slots_line_shows_queries_times_10()
    {
        // 6 つの qrels のクエリ × 1 枠: 当たり 2、判定済みの外れ 1、未判定 3
        var rows = new List<TopRow>
        {
            T("q0", "fused", 1, "a", false, JudgeKind.Hit), T("q1", "fused", 1, "b", false, JudgeKind.Hit), T("q2", "fused", 1, "c", false, JudgeKind.Miss),
            T("q3", "fused", 1, "d"), T("q4", "fused", 1, "e"), T("q5", "fused", 1, "f"),
        };
        HitBreakdownRow all = RepeatMetrics.Breakdown(rows, "fused").Single(r => r.Label == "見た目の経路だけの枠");
        Assert.Equal(2.0 / 6, all.Lower, 9);        // 未判定を外れとして数える
        Assert.Equal(2.0 / 3, all.JudgedOnly, 9);   // 判定済みの枠だけ
        Assert.True(double.IsNaN(new HitBreakdownRow("x", 0, 3, 0, 0, 3).JudgedOnly));
        var p = new PrecisionSummary("fused", 6, 2, 1, 3, 6);
        Assert.Equal(2.0 / 3, p.JudgedOnly, 9);
        Assert.Equal(3, p.JudgedSlots);

        string text = RepeatMetrics.Render(rows, ["fused"], ["fused"]);
        Assert.Contains("クエリ 6、枠 6(クエリ × 10 = 60)", text, StringComparison.Ordinal);
        Assert.Contains("判定済みだけ 66.7%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeat_can_be_counted_over_the_qrels_queries_only_or_over_all_queries()
    {
        var rows = new List<TopRow>();
        for (int q = 0; q < 3; q++) rows.Add(T($"q{q}", "fused", 1, "hub.png"));                                    // qrels のクエリ 3 つ
        for (int q = 0; q < 3; q++) rows.Add(T($"k{q}", "fused", 1, "hub.png", false, JudgeKind.NoQrels));          // known-item だけのクエリ 3 つ
        RepeatStats qrelsOnly = RepeatMetrics.Repeat(rows, "fused");
        RepeatStats all = RepeatMetrics.Repeat(rows, "fused", RepeatMetrics.RepeatScope.AllQueries);
        Assert.Equal((3, 3, 0, 0, 3), (qrelsOnly.Queries, qrelsOnly.Slots, qrelsOnly.RepeatImages, qrelsOnly.RepeatSlots, qrelsOnly.MaxQueriesPerImage));   // 5 クエリに届かない
        Assert.Equal((6, 6, 1, 6, 6), (all.Queries, all.Slots, all.RepeatImages, all.RepeatSlots, all.MaxQueriesPerImage));
        // 内訳の枠は qrels のクエリのまま、繰り返し画像だけ全クエリで決まる
        HitBreakdownRow rep = RepeatMetrics.Breakdown(rows, "fused", RepeatMetrics.RepeatScope.AllQueries).Single(r => r.Label == "繰り返し画像の枠");
        Assert.Equal((3, 3), (rep.Queries, rep.Slots));
        Assert.Equal(0, RepeatMetrics.Breakdown(rows, "fused").Single(r => r.Label == "繰り返し画像の枠").Slots);
        string text = RepeatMetrics.Render(rows, ["fused"], ["fused"]);
        Assert.Contains("主の定義", text, StringComparison.Ordinal);
        Assert.Contains("比べる定義", text, StringComparison.Ordinal);
    }


    [Fact]
    public void Tops_round_trip_through_top10_tsv_and_a_run_without_the_file_reads_as_null()
    {
        string dir = TempDir();
        try
        {
            var tops = new List<TopRow> { T("猫", "fused", 1, "a.png", true, JudgeKind.Hit), T("猫", "visual", 1, "b.png", false, JudgeKind.Unjudged), T("犬", "fused", 1, "c.png", false, JudgeKind.NoQrels), T("犬", "fused", 2, "d.png", false, JudgeKind.Miss) };
            RunFiles.Write(dir, new RunData([], [], null, tops), "s");
            Assert.Equal(tops, RunFiles.Read(dir).Tops);

            RunFiles.Write(dir + "-old", new RunData([], []), "s");
            Assert.Null(RunFiles.Read(dir + "-old").Tops);
            Assert.False(File.Exists(Path.Combine(dir + "-old", RunFiles.TopsFile)));

            File.WriteAllText(Path.Combine(dir, RunFiles.TopsFile), "query\tpath\tpos\tfilename\ttext\tjudge\n猫\tfused\t1\ta.png\t1\tx\n");
            Assert.Throws<InvalidDataException>(() => RunFiles.Read(dir));
            File.WriteAllText(Path.Combine(dir, RunFiles.TopsFile), "wrong\n");
            Assert.Throws<InvalidDataException>(() => RunFiles.Read(dir));
        }
        finally
        {
            foreach (string d in new[] { dir, dir + "-old" }) if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }

    [Fact]
    public void Comparison_shows_the_repeat_changes_when_both_have_tops_and_notes_it_when_one_does_not_and_never_fails_on_them()
    {
        var before = new RunData([], [], null, [T("q1", "fused", 1, "h.png"), T("q2", "fused", 1, "h.png")]);
        var after = new RunData([], [], null, [T("q1", "fused", 1, "x.png"), T("q2", "fused", 1, "y.png")]);
        Comparison c = Comparison.Compare(before, after);
        (RepeatStats b, RepeatStats a) = Assert.Single(c.RepeatChanges);
        Assert.Equal(("fused", 1, 2), (b.Path, b.DistinctImages, a.DistinctImages));
        Assert.Contains("繰り返し出る画像", c.Render(), StringComparison.Ordinal);
        Assert.False(c.AggregateWorse);   // 順位の比較ではない: 終了コードには入れない

        Comparison noBaselineTops = Comparison.Compare(new RunData([], []), after);
        Assert.Empty(noBaselineTops.RepeatChanges);
        Assert.Contains(noBaselineTops.Skipped, s => s.Contains("top10.tsv", StringComparison.Ordinal));
        // 両方に無い(古い形式どうし)なら、何も言わない
        Assert.DoesNotContain(Comparison.Compare(new RunData([], []), new RunData([], [])).Skipped, s => s.Contains("top10.tsv", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 補正した行と合格線


    // ---------------------------------------------------------------- μ の作り方

    [Fact]
    public void Reference_file_skips_comments_and_blank_lines_and_gives_the_sha256_of_the_bytes()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "ref.txt");
            File.WriteAllText(path, "# 見出し\n\n犬\n  猫  \n# 途中の見出し\n鳥\r\n");
            (IReadOnlyList<string> words, string sha) = HubnessMean.ReadReference(path);
            Assert.Equal(["犬", "猫", "鳥"], words);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), sha);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Reference_file_rejects_a_duplicate_word_an_empty_list_and_a_missing_file()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string dup = Path.Combine(dir, "dup.txt"), empty = Path.Combine(dir, "empty.txt");
            File.WriteAllText(dup, "犬\n猫\n犬\n");
            File.WriteAllText(empty, "# だけ\n\n");
            Assert.Throws<InvalidDataException>(() => HubnessMean.ReadReference(dup));
            Assert.Throws<InvalidDataException>(() => HubnessMean.ReadReference(empty));
            Assert.Throws<FileNotFoundException>(() => HubnessMean.ReadReference(Path.Combine(dir, "none.txt")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_committed_reference_list_matches_what_the_generated_mean_recorded()
    {
        (IReadOnlyList<string> words, string sha) = HubnessMean.ReadReference(HubnessMean.FindInRepository(Path.Combine("tools", "Ohanami.Bench", HubnessMean.ReferenceFileName)));
        Assert.Equal(HubnessReferenceMean.WordCount, words.Count);
        Assert.Equal(HubnessReferenceMean.ReferenceSha256, sha);
    }

    [Fact]
    public void The_mean_is_the_plain_average_without_renormalizing_and_rejects_empty_or_uneven_input()
    {
        float[] mean = HubnessMean.Mean([[1f, 0f], [0f, 1f], [1f, 0f]]);
        Assert.Equal(2f / 3, mean[0], 6);
        Assert.Equal(1f / 3, mean[1], 6);
        Assert.Throws<ArgumentException>(() => HubnessMean.Mean([]));
        Assert.Throws<ArgumentException>(() => HubnessMean.Mean([[1f, 0f], [1f]]));
    }

    [Fact]
    public void The_generated_source_is_deterministic_and_carries_the_model_id_the_sha256_the_count_and_every_value_so_it_reads_back_exactly()
    {
        float[] mean = [0.1f, -0.25f, 1f / 3, 0f, 1e-8f, 0.5f, -0.123456789f];
        string a = HubnessMean.RenderSource("m:abc", "ABCD", 7, mean), b = HubnessMean.RenderSource("m:abc", "ABCD", 7, mean);
        Assert.Equal(a, b);
        Assert.Contains("ModelId = \"m:abc\"", a, StringComparison.Ordinal);
        Assert.Contains("ReferenceSha256 = \"ABCD\"", a, StringComparison.Ordinal);
        Assert.Contains("WordCount = 7;", a, StringComparison.Ordinal);
        // 並んだ値を読み直すと、元の float と同じ(往復できる書式)
        string[] tokens = [.. a[a.IndexOf("Mean =", StringComparison.Ordinal)..].Split([' ', ',', '\n', '[', ']', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.EndsWith('f') && char.IsAsciiDigit(t[^2]))];
        Assert.Equal(mean, tokens.Select(t => float.Parse(t[..^1], System.Globalization.CultureInfo.InvariantCulture)).ToArray());
    }

    [Fact]
    public void The_committed_generated_source_is_what_the_command_would_write_for_its_own_values()
    {
        string path = HubnessMean.FindInRepository(HubnessMean.GeneratedRelativePath);
        string rendered = HubnessMean.RenderSource(HubnessReferenceMean.ModelId, HubnessReferenceMean.ReferenceSha256, HubnessReferenceMean.WordCount, HubnessReferenceMean.Mean);
        Assert.Equal(rendered, File.ReadAllText(path).Replace("\r\n", "\n"));   // 手で書き換えたり、書式を変えて作り直し忘れたりすると、ここで気づく
    }

    [Fact]
    public void The_summary_states_where_the_mean_came_from_with_numbers_only_and_no_queries_or_file_names()
    {
        var q1 = Product("猫の秘密", Files, [0.9, 0.8, 0.7, 0.6], new Dictionary<int, double> { [2] = 5 });
        var q2 = Product("犬の秘密", Files, [0.9, 0.2, 0.3, 0.4]);
        KnownItem[] items = [new("visual", "b.png", "猫の秘密"), new("dialogue", "c.png", "犬の秘密")];
        Qrels qrels = Fx.Qrels("猫の秘密\ta.png\t0\ts", "猫の秘密\tb.png\t1\ts");
        BenchRunner.Collected c = Collected(new() { ["猫の秘密"] = q1, ["犬の秘密"] = q2 }, items, qrels, 4);
        var data = new RunData([], [], null, RepeatMetrics.TopRows(c));

        var info = new RunInfo("t", DateTimeOffset.Now, "dir", 4, 4, 0, "", null);
        string summary = Report.Summary(info, data, ["fused", "visual", "fulltext"], 1, ["fused"]);

        Assert.Contains(HubnessReferenceMean.ReferenceSha256, summary, StringComparison.Ordinal);
        Assert.Contains($"{HubnessReferenceMean.WordCount} 語", summary, StringComparison.Ordinal);
        Assert.Contains(HubnessReferenceMean.ModelId, summary, StringComparison.Ordinal);
        Assert.Contains("① 繰り返し出る画像", summary, StringComparison.Ordinal);
        Assert.Contains("② 融合の上位 10 件の当たり率の内訳", summary, StringComparison.Ordinal);
        foreach (string secret in new[] { "猫の秘密", "犬の秘密", "a.png", "b.png", "c.png", "d.png" })
            Assert.DoesNotContain(secret, summary, StringComparison.Ordinal);
    }
}
