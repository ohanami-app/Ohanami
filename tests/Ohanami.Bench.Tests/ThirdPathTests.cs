using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Core;
using Ohanami.Core.Search;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>
/// 3 本目の経路の試し測り(ADR 0035): 埋め込みのファイルの読み書き、今の融合の再現(同じ RankedPath.Rrf)と fused3、run と同じ形の行、
/// 分類の割り当て、U・H3・G・L、precision@10 の Δ・3 層・対の標準誤差、文章にクエリ・ファイル名を入れないこと、集合 "context" の任意のファイル。合成のデータだけ(私物・GPU・埋め込みのモデルは使わない)。
/// </summary>
public sealed class ThirdPathTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "bench-test-" + Guid.NewGuid().ToString("N"));

    private const int Dim = 2;

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
            var hits = new List<PathHit> { new("visual", "m", vis.MeanRank[i], vis.Score[i], new VisualDetail(vis.Score[i])) };
            if (ft is not null && ft.Present[i]) hits.Add(new PathHit("fulltext", null, ft.MeanRank[i], ft.Score[i], new TextDetail([])));
            results.Add(new SearchResult(Fx.Hash(files[i]), [@"C:\images\" + files[i]], fused.MeanRank[i], fused.Score[i], hits));
        }
        var outcomes = new List<PathOutcome> { new("visual", "m", n, PathState.Ok, null) };
        if (ft is not null) outcomes.Add(new PathOutcome("fulltext", null, fulltext!.Count, PathState.Ok, null));
        return new SearchResponse(query, results, outcomes, n, n, TimeSpan.Zero);
    }

    private static readonly string[] Files = ["a.png", "b.png", "c.png", "d.png"];

    // ---------------------------------------------------------------- 埋め込みのファイル

    [Fact]
    public void Vectors_round_trip_and_documents_with_the_same_text_share_one_row()
    {
        string dir = TempDir();
        try
        {
            float[] same = V(1, 1), other = V(0, 1), q = V(1, 0);
            ThirdVectors.Write(dir, "ruri-test", Dim, [("doc", Fx.Hash("a.png"), same), ("doc", Fx.Hash("b.png"), same), ("doc", Fx.Hash("c.png"), other), ("query", "猫", q)]);

            ThirdVectors v = ThirdVectors.Read(dir);

            Assert.Equal(("ruri-test", Dim), (v.Model, v.Dim));
            Assert.Equal(3, v.Docs.Count);
            Assert.Equal(same, v.Docs[Fx.Hash("a.png")]);
            Assert.Equal(same, v.Docs[Fx.Hash("b.png")]);
            Assert.Equal(q, v.Queries["猫"]);
            Assert.Equal(3 * Dim * sizeof(float), new FileInfo(Path.Combine(dir, ThirdVectors.VectorsFile)).Length);   // 同じ文字列の説明文は 1 行(3 行 = same・other・q)
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_malformed_vectors_directory_is_an_error_not_a_silent_miss()
    {
        string dir = TempDir();
        try
        {
            Assert.Throws<FileNotFoundException>(() => ThirdVectors.Read(dir));

            ThirdVectors.Write(dir, "m", Dim, [("doc", "h1", V(1, 0)), ("query", "q", V(0, 1))]);
            string index = Path.Combine(dir, ThirdVectors.IndexFile), meta = Path.Combine(dir, ThirdVectors.MetaFile), vec = Path.Combine(dir, ThirdVectors.VectorsFile);
            string goodIndex = File.ReadAllText(index), goodMeta = File.ReadAllText(meta);
            byte[] goodVec = File.ReadAllBytes(vec);

            File.WriteAllText(index, "wrong\n");
            Assert.Throws<InvalidDataException>(() => ThirdVectors.Read(dir));
            File.WriteAllText(index, goodIndex + "doc\th1\t0\n");                      // 同じ doc の key が 2 回
            Assert.Throws<InvalidDataException>(() => ThirdVectors.Read(dir));
            File.WriteAllText(index, goodIndex + "doc\th2\t9\n");                      // 行数を超える row
            Assert.Throws<InvalidDataException>(() => ThirdVectors.Read(dir));
            File.WriteAllText(index, goodIndex.Replace("doc\t", "page\t"));            // kind が違う
            Assert.Throws<InvalidDataException>(() => ThirdVectors.Read(dir));
            File.WriteAllText(index, goodIndex);

            File.WriteAllText(meta, "{\"model\":\"m\"}");                              // dim が無い
            Assert.Throws<InvalidDataException>(() => ThirdVectors.Read(dir));
            File.WriteAllText(meta, goodMeta);

            File.WriteAllBytes(vec, goodVec[..^3]);                                   // 行の大きさの整数倍でない
            Assert.Throws<InvalidDataException>(() => ThirdVectors.Read(dir));
            byte[] notUnit = (byte[])goodVec.Clone();
            BitConverter.GetBytes(5f).CopyTo(notUnit, 0);                               // 長さが 1 でない
            File.WriteAllBytes(vec, notUnit);
            Assert.Throws<InvalidDataException>(() => ThirdVectors.Read(dir));
            File.WriteAllBytes(vec, goodVec);
            Assert.Single(ThirdVectors.Read(dir).Docs);                                  // 直せば読める
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Python(tools/third-path-trial/embed_descriptions.py)が書いたファイルを、ベンチの読み取りが読めること(書式の食い違いを、実際の Python の出力で確かめる)。
    /// 環境変数 OHANAMI_THIRD_VECTORS_DIR(Python の出力のディレクトリ)のときだけ動く手元専用のテスト。合成の DB・クエリで作った出力を指して使う。
    /// </summary>
    [Fact]
    public void Files_written_by_the_python_script_are_read_by_the_bench_reader()
    {
        string? dir = Environment.GetEnvironmentVariable("OHANAMI_THIRD_VECTORS_DIR");
        Assert.SkipUnless(!string.IsNullOrEmpty(dir), "環境変数 OHANAMI_THIRD_VECTORS_DIR が無い(Python の出力を読む手元専用の確認)");

        ThirdVectors v = ThirdVectors.Read(dir!);

        Assert.Equal(768, v.Dim);
        Assert.Contains("ruri-v3-310m", v.Model, StringComparison.Ordinal);
        Assert.Contains(v.Variant, new[] { "desc-tags", "desc-only" });                       // 入れ方(主 = desc-tags、診断 = desc-only)が meta.json に入っている
        Assert.NotEmpty(v.Docs);
        Assert.NotEmpty(v.Queries);
        // 同じ説明文の画像は、同じベクトル(同じ配列)を指す
        Assert.Equal(v.Docs.Values.Distinct().Count(), v.Docs.Values.Select(x => string.Join(",", x.Take(4))).Distinct().Count());
    }

    // ---------------------------------------------------------------- 今の融合の再現と fused3

    [Fact]
    public void The_third_path_ranks_by_cosine_and_fused3_adds_it_to_the_current_two_with_rrf_k60()
    {
        SearchResponse product = Product("q", Files, [0.4, 0.3, 0.2, 0.1], new Dictionary<int, double> { [1] = 5, [2] = 3 });   // 全文: b(1 位)・c(2 位)
        // 3 本目: b は説明文が空(埋め込みなし)。cosine は d = 1.0、c = 0.8、a = 0.0
        var docs = new Dictionary<string, float[]> { [Fx.Hash("a.png")] = V(0, 1), [Fx.Hash("c.png")] = V(0.8, 0.6), [Fx.Hash("d.png")] = V(1, 0) };

        ThirdAugmentation a = ThirdPath.Augment(product, V(1, 0), docs, "ruri-test");

        Assert.Equal(3, a.ThirdPopulation);
        SearchResponse r = a.Augmented;
        Assert.Equal(["visual", "fulltext", "third"], r.Paths.Select(p => p.PathId));
        Assert.Equal(3, r.Paths[2].Population);

        // 手計算: 見た目 a1 b2 c3 d4、全文 b1 c2、3 本目 d1 c2 a3(b は無し)
        double A = 1.0 / 61 + 1.0 / 63, B = 1.0 / 62 + 1.0 / 61, C = 1.0 / 63 + 1.0 / 62 + 1.0 / 62, D = 1.0 / 64 + 1.0 / 61;
        var expected = new[] { ("a.png", A), ("b.png", B), ("c.png", C), ("d.png", D) }.OrderByDescending(x => x.Item2).ToArray();
        Assert.Equal(expected.Select(x => Fx.Hash(x.Item1)), r.Results.Select(x => x.Hash));          // fused3 の順に並ぶ
        Assert.Equal([1.0, 2.0, 3.0, 4.0], r.Results.Select(x => x.Rank));
        for (int i = 0; i < 4; i++) Assert.Equal(expected[i].Item2, r.Results[i].FusedScore, 12);

        SearchResult b = r.Results.Single(x => x.Hash == Fx.Hash("b.png"));
        Assert.DoesNotContain(b.Hits, h => h.PathId == "third");                                        // 空の説明文は母集団に入らない
        PathHit third = r.Results.Single(x => x.Hash == Fx.Hash("d.png")).Hits.Single(h => h.PathId == "third");
        Assert.Equal((1.0, 1.0), (third.Rank, third.Score), new PairComparer());
        Assert.Equal(1.0, Assert.IsType<ThirdDetail>(third.Detail).Cosine, 6);
        Assert.Equal("ruri-test", third.ModelId);
        Assert.Equal(product.Results.Count, r.Results.Count);                                           // 全件のまま
    }

    private sealed class PairComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 1e-6 && Math.Abs(x.Item2 - y.Item2) < 1e-6;

        public int GetHashCode((double, double) obj) => 0;
    }

    [Fact]
    public void Equal_cosines_share_the_mean_rank_like_every_other_path()
    {
        SearchResponse product = Product("q", Files, [0.4, 0.3, 0.2, 0.1]);
        var docs = new Dictionary<string, float[]> { [Fx.Hash("a.png")] = V(1, 0), [Fx.Hash("b.png")] = V(1, 0), [Fx.Hash("c.png")] = V(0, 1) };

        SearchResponse r = ThirdPath.Augment(product, V(1, 0), docs, "m").Augmented;

        Assert.Equal(1.5, r.Results.Single(x => x.Hash == Fx.Hash("a.png")).Hits.Single(h => h.PathId == "third").Rank);
        Assert.Equal(1.5, r.Results.Single(x => x.Hash == Fx.Hash("b.png")).Hits.Single(h => h.PathId == "third").Rank);
        Assert.Equal(3.0, r.Results.Single(x => x.Hash == Fx.Hash("c.png")).Hits.Single(h => h.PathId == "third").Rank);
    }

    [Fact]
    public void If_the_current_fusion_cannot_be_reproduced_fused3_is_not_made()
    {
        SearchResponse product = Product("q", Files, [0.4, 0.3, 0.2, 0.1], new Dictionary<int, double> { [1] = 5 });
        SearchResult first = product.Results[0];
        SearchResponse tampered = product with { Results = [first with { Rank = first.Rank + 1 }, .. product.Results.Skip(1)] };      // 製品の順位が、再現と違う
        SearchResponse tamperedScore = product with { Results = [first with { FusedScore = first.FusedScore * 2 }, .. product.Results.Skip(1)] };
        var docs = new Dictionary<string, float[]> { [Fx.Hash("a.png")] = V(1, 0) };

        InvalidOperationException e1 = Assert.Throws<InvalidOperationException>(() => ThirdPath.Augment(tampered, V(1, 0), docs, "m"));
        Assert.Contains("再現できない", e1.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => ThirdPath.Augment(tamperedScore, V(1, 0), docs, "m"));
        ThirdPath.Augment(product, V(1, 0), docs, "m");   // 元のままなら通る
    }

    [Fact]
    public void An_incomplete_response_a_path_that_is_not_ok_and_a_wrong_dimension_are_rejected()
    {
        SearchResponse product = Product("q", Files, [0.4, 0.3, 0.2, 0.1]);
        var docs = new Dictionary<string, float[]> { [Fx.Hash("a.png")] = V(1, 0) };
        Assert.Throws<ArgumentException>(() => ThirdPath.Augment(product with { TotalImages = 9 }, V(1, 0), docs, "m"));
        Assert.Throws<ArgumentException>(() => ThirdPath.Augment(product with { Paths = [new PathOutcome("visual", "m", 4, PathState.NotReady, "x")] }, V(1, 0), docs, "m"));
        Assert.Throws<ArgumentException>(() => ThirdPath.Augment(product, [1f, 0f, 0f], docs, "m"));   // クエリの次元が違う
    }

    // ---------------------------------------------------------------- run と同じ形の行

    private static BenchRunner.Collected Collected(Dictionary<string, SearchResponse> responses, IReadOnlyList<KnownItem> items, Qrels qrels)
    {
        var byName = responses.ToDictionary(kv => kv.Key, kv => Metrics.IndexByFileName(kv.Value), StringComparer.Ordinal);
        string[] queries = [.. items.Select(i => i.Query).Concat(qrels.Queries).Distinct(StringComparer.Ordinal)];
        return new BenchRunner.Collected(new BenchInputs.InputSet(items, qrels, "入力"), queries, responses, byName, 4, 4, 0, 4, [TimeSpan.FromMilliseconds(5)]);
    }

    [Fact]
    public void Build_gives_the_same_shape_of_rows_as_a_run_with_third_and_fused3_added_and_the_product_rows_untouched()
    {
        var q1 = Product("猫", Files, [0.4, 0.3, 0.2, 0.1], new Dictionary<int, double> { [1] = 5, [2] = 3 });
        var q2 = Product("犬", Files, [0.1, 0.2, 0.3, 0.4]);
        KnownItem[] items = [new("visual", "d.png", "猫"), new("dialogue", "a.png", "犬")];
        Qrels qrels = Fx.Qrels("猫\ta.png\t1\ts", "猫\td.png\t0\ts", "犬\ta.png\t1\ts");
        BenchRunner.Collected c = Collected(new() { ["猫"] = q1, ["犬"] = q2 }, items, qrels);
        string d1 = Fx.Hash("d.png"), a1 = Fx.Hash("a.png");
        var docs = new Dictionary<string, float[]> { [a1] = V(0, 1), [d1] = V(1, 0), [Fx.Hash("c.png")] = V(0.8, 0.6) };
        string dir = TempDir();
        try
        {
            ThirdVectors.Write(dir, "ruri-test", Dim, [.. docs.Select(kv => ("doc", kv.Key, kv.Value)), ("query", "猫", V(1, 0)), ("query", "犬", V(0, 1))]);
            ThirdVectors v = ThirdVectors.Read(dir);

            RunData data = ThirdPath.Build(c, v, v.Model, out int reproduced, out int population);

            Assert.Equal(2, reproduced);
            Assert.Equal(3, population);
            RunData product = BenchRunner.BuildData(c, out _);
            Assert.Equal(new[] { "fused", "visual", "fulltext", "third", "fused3" }.Order(), data.KnownItems.Select(r => r.Path).Distinct().Order());
            // 製品の 3 経路の行は、そのまま(順位も)
            foreach (KnownItemRow r in product.KnownItems) Assert.Contains(r, data.KnownItems);
            foreach (PrecisionRow r in product.Precision)   // PrecisionRow の Top はリストなので、値で比べる
                Assert.Contains(data.Precision, x => x.Query == r.Query && x.Path == r.Path && x.Relevant == r.Relevant && x.JudgedMiss == r.JudgedMiss && x.Unjudged == r.Unjudged && x.Returned == r.Returned && x.Top.SequenceEqual(r.Top));
            // 3 本目の行: d.png は猫で、cosine 1.0 の 1 位
            Assert.Equal(1.0, data.KnownItems.Single(r => r.Path == "third" && r.Set == "visual").Rank);
            Assert.Equal(new[] { "fused", "visual", "fulltext", "third", "fused3" }.Order(), data.Precision.Select(r => r.Path).Distinct().Order());
            Assert.Equal(2, data.Precision.Count(r => r.Path == "fused3"));                      // qrels の 2 クエリ
            Assert.Contains(data.Scores!, r => r.Path == "third" && r.Score is not null);
            Assert.Contains(data.Scores!, r => r.Path == "fused3");
            // 往復できる(run のフォルダに書ける)
            string runDir = Path.Combine(dir, "run");
            RunFiles.Write(runDir, data, "s");
            RunData back = RunFiles.Read(runDir);
            Assert.Equal(data.KnownItems, back.KnownItems);
            Assert.Equal(data.Scores, back.Scores);

            // クエリのベクトルが無ければ、黙って飛ばさず例外
            ThirdVectors.Write(Path.Combine(dir, "v2"), "m", Dim, [.. docs.Select(kv => ("doc", kv.Key, kv.Value)), ("query", "猫", V(1, 0))]);
            Assert.Throws<InvalidDataException>(() => ThirdPath.Build(c, ThirdVectors.Read(Path.Combine(dir, "v2")), "m", out _, out _));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_diagnostic_embedding_adds_only_known_item_rows_for_the_description_only_path_and_leaves_the_main_rows_alone()
    {
        var q1 = Product("猫", Files, [0.4, 0.3, 0.2, 0.1], new Dictionary<int, double> { [1] = 5, [2] = 3 });
        KnownItem[] items = [new("visual", "d.png", "猫"), new("dialogue", "a.png", "猫")];
        Qrels qrels = Fx.Qrels("猫\ta.png\t1\ts", "猫\td.png\t0\ts");
        BenchRunner.Collected c = Collected(new() { ["猫"] = q1 }, items, qrels);
        string dir = TempDir();
        try
        {
            // 主(説明文 + タグ)は d.png が 1 位、診断(説明文だけ)は a.png が 1 位 — 入れ方で順位が変わる
            ThirdVectors.Write(Path.Combine(dir, "main"), "m", Dim, [("doc", Fx.Hash("d.png"), V(1, 0)), ("doc", Fx.Hash("a.png"), V(0, 1)), ("query", "猫", V(1, 0))], variant: "desc-tags");
            ThirdVectors.Write(Path.Combine(dir, "diag"), "m", Dim, [("doc", Fx.Hash("d.png"), V(0, 1)), ("doc", Fx.Hash("a.png"), V(1, 0)), ("query", "猫", V(1, 0))], variant: "desc-only");
            ThirdVectors main = ThirdVectors.Read(Path.Combine(dir, "main")), diag = ThirdVectors.Read(Path.Combine(dir, "diag"));
            Assert.Equal(("desc-tags", "desc-only"), (main.Variant, diag.Variant));

            RunData without = ThirdPath.Build(c, main, main.Model, out _, out _);
            RunData with = ThirdPath.Build(c, main, main.Model, out _, out _, diag);

            // 診断の行は、known-item に third-desc と fused3-desc だけ。precision と scores には入らない
            Assert.Equal(["third-desc", "fused3-desc"], with.KnownItems.Select(r => r.Path).Except(without.KnownItems.Select(r => r.Path)).Distinct());
            Assert.Equal(without.Precision.Count, with.Precision.Count);
            Assert.Equal(without.Scores!.Count, with.Scores!.Count);
            Assert.All(without.KnownItems, r => Assert.Contains(r, with.KnownItems));
            // 診断では a.png(セリフの目的)が 3 本目の 1 位、主では d.png(見た目の目的)が 1 位
            Assert.Equal(1.0, with.KnownItems.Single(r => r.Path == "third-desc" && r.Set == "dialogue").Rank);
            Assert.Equal(1.0, with.KnownItems.Single(r => r.Path == "third" && r.Set == "visual").Rank);
            Assert.Equal(2.0, with.KnownItems.Single(r => r.Path == "third-desc" && r.Set == "visual").Rank);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_compare_command_still_works_between_an_old_form_baseline_and_a_third_path_run()
    {
        // 新しい経路(third・fused3)の行は、基準線に無いので比べない(注意の行に出る)。ほかは比べる
        var baseline = new RunData([new KnownItemRow("visual", "q", "a", "fused", 30)], [new PrecisionRow("q", "fused", 4, 6, 0, 10, [])]);
        var run = new RunData([new KnownItemRow("visual", "q", "a", "fused", 30), new KnownItemRow("visual", "q", "a", "third", 3), new KnownItemRow("visual", "q", "a", "fused3", 10)],
            [new PrecisionRow("q", "fused", 4, 6, 0, 10, []), new PrecisionRow("q", "fused3", 5, 5, 0, 10, [])]);

        Comparison c = Comparison.Compare(baseline, run);

        Assert.False(c.AggregateWorse);
        Assert.Contains(c.Skipped, s => s.Contains("fused3", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 集計と文章

    private static KnownItemRow K(string set, string q, string file, string path, double? rank) => new(set, q, file, path, rank);

    /// <summary>known-item 5 件: 今の当たり 2(a・b)と、今の外れ 3(c・d・e)。経路ごとの順位を指定する。</summary>
    private static (RunData Data, KnownItem[] Items) Sample()
    {
        KnownItem[] items = [new("visual", "a.png", "qa"), new("visual", "b.png", "qb"), new("visual", "c.png", "qc"), new("dialogue", "d.png", "qd"), new("one-image", "e.png", "qe")];
        (string Set, string Q, string F, double? Vis, double? Ft, double? Third, double? Fused, double? Fused3)[] t =
        [
            ("visual", "qa", "a.png", 5, null, 40, 5, 30),       // 今の当たり。3 本目は 20 位の外。fused3 で落ちた(L)
            ("visual", "qb", "b.png", 9, 2, 8, 4, 3),            // 今の当たり。3 本目も 20 位以内(H3)。そのまま
            ("visual", "qc", "c.png", 90, null, 5, 60, 12),      // 今の外れ。3 本目で 20 位以内(U)。fused3 で入った(G)
            ("dialogue", "qd", "d.png", 100, null, 30, 80, 70),  // 今の外れ。3 本目も 20 位の外
            ("one-image", "qe", "e.png", 300, null, null, null, 100),   // 今の外れ(圏外)。3 本目の母集団に無い
        ];
        var known = new List<KnownItemRow>();
        foreach (var r in t)
        {
            known.Add(K(r.Set, r.Q, r.F, "visual", r.Vis)); known.Add(K(r.Set, r.Q, r.F, "fulltext", r.Ft)); known.Add(K(r.Set, r.Q, r.F, "third", r.Third));
            known.Add(K(r.Set, r.Q, r.F, "fused", r.Fused)); known.Add(K(r.Set, r.Q, r.F, "fused3", r.Fused3));
        }
        // precision: 4 クエリ。fused の当たり [4,3,2,0]、fused3 の当たり [5,3,1,2] → 差 [+1,0,-1,+2]。全文の返した件数 [10,5,0,0]
        (string Q, int Fused, int Fused3, int FtReturned, int Third)[] p = [("p1", 4, 5, 10, 2), ("p2", 3, 3, 5, 1), ("p3", 2, 1, 0, 0), ("p4", 0, 2, 0, 3)];
        var precision = new List<PrecisionRow>();
        foreach (var r in p)
        {
            precision.Add(new PrecisionRow(r.Q, "fused", r.Fused, 10 - r.Fused, 0, 10, []));
            precision.Add(new PrecisionRow(r.Q, "fused3", r.Fused3, 10 - r.Fused3 - (r.Q == "p4" ? 1 : 0), r.Q == "p4" ? 1 : 0, 10, []));
            precision.Add(new PrecisionRow(r.Q, "fulltext", 0, 0, 0, r.FtReturned, []));
            precision.Add(new PrecisionRow(r.Q, "third", r.Third, 10 - r.Third, 0, 10, []));
        }
        return (new RunData(known, precision), items);
    }

    private static Qrels FourQueries() => Fx.Qrels("p1\tx.png\t1\ts", "p2\tx.png\t1\ts", "p3\tx.png\t1\ts", "p4\tx.png\t1\ts");

    [Fact]
    public void Counts_u_h3_g_and_l_per_set_and_overall()
    {
        (RunData data, KnownItem[] items) = Sample();

        ThirdPathStats s = ThirdPathReport.Compute(data, FourQueries(), items, null);

        ThirdSetCounts all = s.Sets.Single(x => x.Set == "全体");
        Assert.Equal((5, 3, 1, 2, 1, 1, 1), (all.Items, all.Outliers, all.OutliersThirdTop, all.Hits, all.HitsThirdTop, all.Gained, all.Lost));   // 外れ 3(U 1)、当たり 2(H3 1)、G 1、L 1
        ThirdSetCounts visual = s.Sets.Single(x => x.Set == "visual");
        Assert.Equal((3, 1, 1, 2, 1, 1, 1), (visual.Items, visual.Outliers, visual.OutliersThirdTop, visual.Hits, visual.HitsThirdTop, visual.Gained, visual.Lost));
        ThirdSetCounts dialogue = s.Sets.Single(x => x.Set == "dialogue");
        Assert.Equal((1, 1, 0, 0, 0, 0, 0), (dialogue.Items, dialogue.Outliers, dialogue.OutliersThirdTop, dialogue.Hits, dialogue.HitsThirdTop, dialogue.Gained, dialogue.Lost));
        Assert.Equal(["visual", "dialogue", "one-image", "全体"], s.Sets.Select(x => x.Set));
        Assert.Equal(["(分類なし)"], s.OutlierClasses.Keys);                                          // 割り当ての無い実行では、外れは「分類なし」
        Assert.All(s.Entries.Where(e => !e.Outlier), e => Assert.Equal(ThirdEntry.HitClass, e.Class));
    }

    [Fact]
    public void The_ask_the_user_first_notice_appears_only_when_fused3_has_unjudged_pairs()
    {
        (RunData data, KnownItem[] items) = Sample();
        ThirdPathStats withUnjudged = ThirdPathReport.Compute(data, FourQueries(), items, null);
        Assert.Equal(1, withUnjudged.Fused3.Unjudged);

        string text = ThirdPathReport.Render(withUnjudged, "m", 768, 1000, 1030, 51);
        Assert.Contains("fused3 の上位 10 件に未判定がある: 1 組(1 クエリ)。判定は、この数字を見せる前に、判定する人へ頼む", text, StringComparison.Ordinal);

        ThirdPathStats allJudged = withUnjudged with { Fused3 = withUnjudged.Fused3 with { Unjudged = 0 }, UnjudgedQueries = 0 };
        string judgedText = ThirdPathReport.Render(allJudged, "m", 768, 1000, 1030, 51);
        Assert.Contains("fused3 の上位 10 件の未判定: 0 組", judgedText, StringComparison.Ordinal);
        Assert.DoesNotContain("判定する人へ頼む", judgedText, StringComparison.Ordinal);
        Assert.DoesNotContain("未判定がある", judgedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Precision_totals_the_paired_difference_the_layers_by_fulltext_count_and_the_standard_error()
    {
        (RunData data, KnownItem[] items) = Sample();

        ThirdPathStats s = ThirdPathReport.Compute(data, FourQueries(), items, null);

        Assert.Equal((9, 11, 4), (s.Fused.Relevant, s.Fused3.Relevant, s.Queries));
        Assert.Equal(1, s.Fused3.Unjudged);
        Assert.Equal((2, 2, 1), (s.Delta, s.Up, s.Down));                                             // クエリごとの差 [+1, 0, -1, +2]
        Assert.Equal(Math.Sqrt(5.0 / 3), s.DeltaSd, 9);                                              // 平均 0.5、標本標準偏差 = √(5/3)
        Assert.Equal(Math.Sqrt(5.0 / 3) * 2, s.DeltaSe, 9);                                          // 当たりの合計の差の標準誤差 = SD × √4
        Assert.Equal(Math.Sqrt(5.0 / 3) / (10 * 2), s.DeltaSeInPrecision, 9);                        // precision@10 の平均の差の標準誤差 = SD ÷ (10 × √4)
        Assert.Equal(1, s.UnjudgedQueries);
        Assert.Equal(["全文 0 件", "全文 1〜9 件", "全文 10 件"], s.Layers.Select(l => l.Name));
        // (クエリ数, Δ, 上がった, 下がった): 0 件 = p3(-1)・p4(+2)、1〜9 件 = p2(0)、10 件 = p1(+1)
        Assert.Equal([(2, 1, 1, 1), (1, 0, 0, 0), (1, 1, 1, 0)], s.Layers.Select(l => (l.Queries, l.Delta, l.Up, l.Down)));
        Assert.Equal(6.0 / 40, s.Third.Lower, 9);                                                    // 3 本目の単独: 当たり 2+1+0+3 = 6、枠 10 × 4
        Assert.Equal(6.0 / 40, s.Third.Upper, 9);
    }

    /// <summary>Sample() に、診断(説明文だけ)の known-item の順位の行を足す: 3 本目 a=3・b=50・c=9・d=4・e=なし、fused3 a=6・b=25・c=18・d=15・e=100。</summary>
    private static RunData WithDiagnostic(RunData data)
    {
        (string Set, string Q, string F, double? T, double? F3)[] d = [("visual", "qa", "a.png", 3, 6), ("visual", "qb", "b.png", 50, 25), ("visual", "qc", "c.png", 9, 18), ("dialogue", "qd", "d.png", 4, 15), ("one-image", "qe", "e.png", null, 100)];
        return new RunData([.. data.KnownItems, .. d.SelectMany(r => new[] { K(r.Set, r.Q, r.F, "third-desc", r.T), K(r.Set, r.Q, r.F, "fused3-desc", r.F3) })], data.Precision, data.Scores);
    }

    [Fact]
    public void The_diagnostic_counts_use_the_description_only_ranks_and_appear_only_when_the_rows_exist()
    {
        (RunData data, KnownItem[] items) = Sample();
        Assert.False(ThirdPathReport.Compute(data, FourQueries(), items, null).HasDiagnostic);
        Assert.DoesNotContain("診断(後知恵)", ThirdPathReport.Render(ThirdPathReport.Compute(data, FourQueries(), items, null), "m", 768, 1000, 1030, 51), StringComparison.Ordinal);

        ThirdPathStats s = ThirdPathReport.Compute(WithDiagnostic(data), FourQueries(), items, null);

        Assert.True(s.HasDiagnostic);
        ThirdSetCounts all = s.DiagnosticSets!.Single(x => x.Set == "全体");
        Assert.Equal((3, 2, 2, 1, 2, 1), (all.Outliers, all.OutliersThirdTop, all.Gained, all.HitsThirdTop, all.Gained, all.Lost));   // U 2(c・d)、G 2(c・d)、H3 1(a)、L 1(b)
        ThirdSetCounts visual = s.DiagnosticSets!.Single(x => x.Set == "visual");
        Assert.Equal((1, 1, 1, 1), (visual.OutliersThirdTop, visual.Gained, visual.HitsThirdTop, visual.Lost));
        // 主の数え上げは、診断の行があっても変わらない
        Assert.Equal((1, 1, 1), (s.Sets.Single(x => x.Set == "全体").OutliersThirdTop, s.Sets.Single(x => x.Set == "全体").Gained, s.Sets.Single(x => x.Set == "全体").Lost));
        Assert.Equal(5.0, s.Entries.Single(e => e.Set == "visual" && e.Index == 1).ThirdDesc is null ? 0 : 5.0);

        string text = ThirdPathReport.Render(s, "m", 768, 1000, 1030, 51, null, "desc-tags", 1000);
        Assert.Contains("主の入れ方: desc-tags", text, StringComparison.Ordinal);
        Assert.Contains("診断(後知恵): 説明文だけの入れ方", text, StringComparison.Ordinal);
        Assert.Contains("precision・判定は無い。主の判断には使わない", text, StringComparison.Ordinal);
        foreach (string secret in new[] { "qa", "qc", "a.png", "c.png" }) Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        string tsv = ThirdPathReport.RenderEntriesTsv(s);
        Assert.StartsWith("set\tn\tquery\tfilename\tclass\tvisual\tfulltext\tthird\tfused\tfused3\tthird_desc\tfused3_desc\n", tsv, StringComparison.Ordinal);
        Assert.Contains("visual\t3\tqc\tc.png\t(分類なし)\t90\t\t5\t60\t12\t9\t18\n", tsv, StringComparison.Ordinal);
    }

    [Fact]
    public void Classes_are_assigned_to_the_current_outliers_and_counted_by_class()
    {
        (RunData data, KnownItem[] items) = Sample();
        ThirdClassRow[] classes = [new("visual", "qc", "c.png", "A"), new("dialogue", "qd", "d.png", "C"), new("one-image", "qe", "e.png", "B")];

        ThirdPathStats s = ThirdPathReport.Compute(data, FourQueries(), items, classes);

        Assert.Equal(["A", "B", "C"], s.OutlierClasses.Keys.ToArray());
        Assert.Equal((1, 1, 1), (s.OutlierClasses["A"].Outliers, s.OutlierClasses["A"].OutliersThirdTop, s.OutlierClasses["A"].Gained));   // c.png: U と G
        Assert.Equal((1, 0, 0), (s.OutlierClasses["C"].Outliers, s.OutlierClasses["C"].OutliersThirdTop, s.OutlierClasses["C"].Gained));
        Assert.Equal(0, s.ClassesNotOutlier);

        // 割り当ての無い外れは、数えずに止まる(測る前に固定したファイルに足す)
        InvalidDataException missing = Assert.Throws<InvalidDataException>(() => ThirdPathReport.Compute(data, FourQueries(), items, [classes[0], classes[1]]));
        Assert.Contains("1 件", missing.Message, StringComparison.Ordinal);
        // known-item に無い行(綴りの違い)は、止まる
        Assert.Throws<InvalidDataException>(() => ThirdPathReport.Compute(data, FourQueries(), items, [.. classes, new("visual", "qz", "z.png", "A")]));
        // 今は外れでない割り当ては、数えて注意に出す
        Assert.Equal(1, ThirdPathReport.Compute(data, FourQueries(), items, [.. classes, new("visual", "qa", "a.png", "A")]).ClassesNotOutlier);
    }

    // ---------------------------------------------------------------- 分類の割り当て(集合 + 番号の形。classification.tsv)

    private const string IndexHeader = "set\tindex\tfused_rank_at_classification\tclass\tdoubtful";

    /// <summary>Sample() の one-image の known-item(e.png)の融合の順位を、圏外でなく 250 位にしたもの(分類の割り当ての「固定したときの順位」と合わせるため)。集合名 one-image は、私物の追加の known-item のファイル名に合わせてある。</summary>
    private static RunData SampleWithRankedExtraSet()
    {
        (RunData data, _) = Sample();
        return new RunData([.. data.KnownItems.Select(r => r.Set == "one-image" && r.Path == "fused" ? r with { Rank = 250 } : r)], data.Precision);
    }

    private static readonly ThirdClassIndexRow[] IndexClasses = [new("visual", 3, 60, "A", false), new("dialogue", 1, 80, "C", true), new("one-image", 1, 250, "B", false)];

    [Fact]
    public void The_index_form_of_the_classes_file_is_read_and_told_apart_from_the_name_form()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "classification.tsv");
            File.WriteAllText(path, IndexHeader + "\nvisual\t6\t267\tB\t1\nvisual\t8\t768\tA\t0\none-image\t1\t20.5\tD\t0\n");

            IReadOnlyList<ThirdClassIndexRow> rows = ThirdPathReport.ReadClassesByIndex(path);

            Assert.Equal([new ThirdClassIndexRow("visual", 6, 267, "B", true), new ThirdClassIndexRow("visual", 8, 768, "A", false), new ThirdClassIndexRow("one-image", 1, 20.5, "D", false)], rows);
            (IReadOnlyList<ThirdClassRow>? byName, IReadOnlyList<ThirdClassIndexRow>? byIndex) = ThirdPathReport.ReadClassAssignments(path);
            Assert.Null(byName);
            Assert.Equal(3, byIndex!.Count);

            File.WriteAllText(path, "set\tquery\tfilename\tclass\nvisual\tq\tf.png\tA\n");
            (byName, byIndex) = ThirdPathReport.ReadClassAssignments(path);
            Assert.Single(byName!);
            Assert.Null(byIndex);

            File.WriteAllText(path, "wrong\n");
            Assert.Throws<InvalidDataException>(() => ThirdPathReport.ReadClassAssignments(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("visual\t0\t10\tA\t0\n")]          // index が 0
    [InlineData("visual\tx\t10\tA\t0\n")]          // index が整数でない
    [InlineData("visual\t1\tabc\tA\t0\n")]         // 順位が数でない
    [InlineData("visual\t1\t10\t\t0\n")]           // class が空
    [InlineData("visual\t1\t10\tA\t2\n")]          // doubtful が 0 でも 1 でもない
    [InlineData("visual\t1\t10\tA\n")]              // 列が足りない
    [InlineData("visual\t1\t10\tA\t0\nvisual\t1\t11\tB\t0\n")]   // 同じ (set, index) が 2 回
    public void A_malformed_index_form_is_an_error(string body)
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "classification.tsv");
            File.WriteAllText(path, IndexHeader + "\n" + body);
            Assert.Throws<InvalidDataException>(() => ThirdPathReport.ReadClassesByIndex(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Classes_by_index_are_checked_against_the_fused_rank_at_the_time_and_assigned_to_the_outliers()
    {
        (_, KnownItem[] items) = Sample();

        ThirdPathStats s = ThirdPathReport.Compute(SampleWithRankedExtraSet(), FourQueries(), items, null, IndexClasses);

        Assert.DoesNotContain(s.OutlierClasses.Keys, k => k.StartsWith("(分類", StringComparison.Ordinal) || k.StartsWith("(割り当て", StringComparison.Ordinal));   // 割り当てを使った実行では、出ない
        Assert.Equal(new[] { "(迷いの合計)", "A", "B", "C?" }.Order(StringComparer.Ordinal), s.OutlierClasses.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((1, 1, 1), (s.OutlierClasses["A"].Outliers, s.OutlierClasses["A"].OutliersThirdTop, s.OutlierClasses["A"].Gained));   // visual 3(c.png): U と G
        Assert.Equal((1, 0, 0), (s.OutlierClasses["C?"].Outliers, s.OutlierClasses["C?"].OutliersThirdTop, s.OutlierClasses["C?"].Gained));   // dialogue 1(d.png): 迷いあり
        Assert.Equal(1, s.OutlierClasses["(迷いの合計)"].Outliers);
        Assert.Equal(0, s.ClassesNotOutlier);
        ThirdEntry c = s.Entries.Single(e => e.Set == "visual" && e.Index == 3);
        Assert.Equal(("A", false), (c.Class, c.Doubtful));
        Assert.True(s.Entries.Single(e => e.Set == "dialogue").Doubtful);
        Assert.Contains("(迷いの合計)", ThirdPathReport.Render(s, "m", 768, 1000, 1030, 51), StringComparison.Ordinal);
    }

    [Fact]
    public void A_fused_rank_that_does_not_match_stops_the_run_so_a_wrong_index_convention_is_not_passed_silently()
    {
        (_, KnownItem[] items) = Sample();
        ThirdClassIndexRow[] shifted = [new("visual", 3, 80, "A", false), new("dialogue", 1, 80, "C", false), new("one-image", 1, 250, "B", false)];   // visual 3 の順位が、今は 60

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => ThirdPathReport.Compute(SampleWithRankedExtraSet(), FourQueries(), items, null, shifted));

        Assert.Contains("一致しない 1 件", e.Message, StringComparison.Ordinal);
        Assert.Contains("集合 visual の番号 3(ファイル 80、今 60)", e.Message, StringComparison.Ordinal);
        // 番号が known-item の数を超える
        Assert.Throws<InvalidDataException>(() => ThirdPathReport.Compute(SampleWithRankedExtraSet(), FourQueries(), items, null, [.. IndexClasses, new("visual", 9, 60, "A", false)]));
    }

    [Fact]
    public void An_outlier_missing_from_a_set_the_file_covers_stops_the_run()
    {
        (_, KnownItem[] items) = Sample();
        // visual に、割り当てが無い外れをもう 1 件足す(visual は番号 3 の行があるので、割り当ての範囲の集合)
        KnownItem extra = new("visual", "g.png", "qg");
        RunData data = SampleWithRankedExtraSet();
        var rows = new List<KnownItemRow>(data.KnownItems);
        foreach (string path in new[] { "visual", "fulltext", "third", "fused", "fused3" }) rows.Add(K("visual", "qg", "g.png", path, path == "fused" ? 70 : 50));

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => ThirdPathReport.Compute(new RunData(rows, data.Precision), FourQueries(), [.. items, extra], null, IndexClasses));

        Assert.Contains("分類の割り当てが無いものが 1 件", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_outlier_in_a_set_the_file_does_not_mention_such_as_context_becomes_unclassified_and_does_not_stop_the_run()
    {
        (_, KnownItem[] items) = Sample();
        RunData data = SampleWithRankedExtraSet();
        KnownItem ctx = new("context", "f.png", "qf");
        var extra = new List<KnownItemRow>();
        foreach (string path in new[] { "visual", "fulltext", "third", "fused", "fused3" }) extra.Add(K("context", "qf", "f.png", path, path == "fused" ? 90 : 50));
        var all = new RunData([.. data.KnownItems, .. extra], data.Precision);

        ThirdPathStats s = ThirdPathReport.Compute(all, FourQueries(), [.. items, ctx], null, IndexClasses);

        ThirdEntry c = s.Entries.Single(e => e.Set == "context");
        Assert.True(c.Outlier);
        Assert.Equal("(未分類。割り当ての無い集合)", c.Class);
        Assert.Equal(4, s.OutlierClasses.Where(kv => kv.Key != "(迷いの合計)").Sum(kv => kv.Value.Outliers));   // A・B・C?・未分類 の外れの合計(迷いの合計は別枠)
    }

    [Fact]
    public void An_index_row_for_an_item_that_is_not_an_outlier_now_is_counted_and_the_two_forms_cannot_be_mixed()
    {
        (_, KnownItem[] items) = Sample();
        // visual 1(a.png)は今の当たり(融合 5 位)。割り当てがあっても、外れとして数えず、注意に出す
        ThirdPathStats s = ThirdPathReport.Compute(SampleWithRankedExtraSet(), FourQueries(), items, null, [.. IndexClasses, new("visual", 1, 5, "A", false)]);
        Assert.Equal(1, s.ClassesNotOutlier);
        Assert.Contains("今は外れでない known-item が 1 件", ThirdPathReport.Render(s, "m", 768, 1000, 1030, 51, null), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => ThirdPathReport.Compute(SampleWithRankedExtraSet(), FourQueries(), items, [new("visual", "qc", "c.png", "A")], IndexClasses));
    }

    [Fact]
    public void The_classes_file_is_read_with_its_header_and_rejects_duplicates_and_empty_cells()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "classes.tsv");
            File.WriteAllText(path, "set\tquery\tfilename\tclass\nvisual\tqc\tc.png\tA\ndialogue\tqd\td.png\tC?\n");
            Assert.Equal([new ThirdClassRow("visual", "qc", "c.png", "A"), new ThirdClassRow("dialogue", "qd", "d.png", "C?")], ThirdPathReport.ReadClasses(path));

            File.WriteAllText(path, "wrong\n");
            Assert.Throws<InvalidDataException>(() => ThirdPathReport.ReadClasses(path));
            File.WriteAllText(path, "set\tquery\tfilename\tclass\nvisual\tqc\tc.png\tA\nvisual\tqc\tc.png\tB\n");
            Assert.Throws<InvalidDataException>(() => ThirdPathReport.ReadClasses(path));
            File.WriteAllText(path, "set\tquery\tfilename\tclass\nvisual\tqc\tc.png\t\n");
            Assert.Throws<InvalidDataException>(() => ThirdPathReport.ReadClasses(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_report_has_the_tables_and_never_contains_a_query_or_a_file_name_while_the_tsv_does()
    {
        (RunData data, KnownItem[] items) = Sample();
        ThirdPathStats s = ThirdPathReport.Compute(data, FourQueries(), items, [new("visual", "qc", "c.png", "A"), new("dialogue", "qd", "d.png", "C"), new("one-image", "qe", "e.png", "B")]);

        string text = ThirdPathReport.Render(s, "ruri-test", 768, 1000, 1030, 51);

        Assert.Contains("モデル: ruri-test(次元 768)", text, StringComparison.Ordinal);
        Assert.Contains("説明文の埋め込みがある画像 1000 / 1030 枚", text, StringComparison.Ordinal);
        Assert.Contains("51 クエリのすべてで一致した", text, StringComparison.Ordinal);
        Assert.Contains("最初の表", text, StringComparison.Ordinal);
        Assert.Contains($"{"visual",-8}{3,2}", text, StringComparison.Ordinal);                  // 集合 + 番号
        Assert.Contains("U = 今の外れのうち 3 本目の単独で 20 位以内", text, StringComparison.Ordinal);
        Assert.Contains("Δ = +2、上がったクエリ 2、下がったクエリ 1", text, StringComparison.Ordinal);
        Assert.Contains("全文 0 件", text, StringComparison.Ordinal);
        Assert.Contains("判定・採否はしない", text, StringComparison.Ordinal);
        foreach (string secret in new[] { "qa", "qb", "qc", "qd", "qe", "a.png", "b.png", "c.png", "d.png", "e.png" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);                  // クエリ・ファイル名は出さない

        string tsv = ThirdPathReport.RenderEntriesTsv(s);
        Assert.StartsWith("set\tn\tquery\tfilename\tclass\tvisual\tfulltext\tthird\tfused\tfused3\tthird_desc\tfused3_desc\n", tsv, StringComparison.Ordinal);
        Assert.Contains("visual\t3\tqc\tc.png\tA\t90\t\t5\t60\t12\t\t\n", tsv, StringComparison.Ordinal);   // 私物のファイルには名前を入れる(全文に無い・診断なし = 空)
    }

    // ---------------------------------------------------------------- 集合 "context" の任意のファイル

    [Fact]
    public void The_context_set_is_an_optional_file_read_like_the_extra_set_and_the_summary_names_both()
    {
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "known-items.tsv"), "filename\tquery_ja\na.png\t猫\n");
            File.WriteAllText(Path.Combine(dir, "known-items-dialogue.tsv"), "filename\tquery_ja\nb.png\tこんにちは\n");
            File.WriteAllText(Path.Combine(dir, "qrels.tsv"), "query_ja\tfilename\trelevant\tsource\n猫\ta.png\t1\ts\n");

            BenchInputs.InputSet without = BenchInputs.Load(dir);
            Assert.Equal(["visual", "dialogue"], without.Items.Select(i => i.Set));
            Assert.Contains("context のファイル known-items-context.tsv は無い", without.Summary, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(dir, BenchInputs.ContextKnownItemsFile), "filename\tquery_ja\tquery_en\nc.png\t黒い服の人が走っている場面\t\nd.png\t\t\n");
            BenchInputs.InputSet with = BenchInputs.Load(dir);
            Assert.Equal(["visual", "dialogue", "context"], with.Items.Select(i => i.Set));
            Assert.Equal(new KnownItem("context", "c.png", "黒い服の人が走っている場面"), with.Items[2]);
            Assert.Contains("context 1 件(除外 1)", with.Summary, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(dir, BenchInputs.ContextKnownItemsFile), "name\tq\nx\ty\n");   // あって壊れていれば、黙って空にしない
            Assert.Throws<InvalidDataException>(() => BenchInputs.Load(dir));
            Assert.Equal(["one-image", "context"], BenchInputs.OptionalSets);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_score_summary_shows_the_context_set_only_when_it_has_targets()
    {
        ScoreRow V1(string q, string targets) => new(q, "x.png", targets, null, "visual", 0.1, 1, true, 0.2);
        Assert.Contains("known-item の目的(context): n=1", ScoreReport.Render([V1("q", "context")]), StringComparison.Ordinal);
        Assert.Contains("1 位との差: known-item の目的(context)", ScoreReport.Render([V1("q", "context")]), StringComparison.Ordinal);
        Assert.DoesNotContain("(context)", ScoreReport.Render([V1("q", "visual")]), StringComparison.Ordinal);
    }
}
