using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Ohanami.Core.Embedding.Generated;
using Ohanami.Core.Models;
using Ohanami.Core.Search;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests;

/// <summary>
/// 特徴ベクトル検索の補正(ADR 0051): 生成した μ の出どころの記録、関門の境目、点の式、モデル違いの例外。
/// μ を参照の一覧から計算し直して生成物と比べるテストは、モデルの実物が要るので <see cref="HubnessMeanModelTests"/>(Category=Model)。
/// </summary>
public sealed class HubnessCorrectionTests
{
    private static string ReferencePath => Path.Combine(RepoPaths.Root, "tools", "Ohanami.Bench", "hubness-reference-ja.txt");

    /// <summary>参照の一覧の語(空行と # の行を除く)。</summary>
    private static string[] ReferenceWords() =>
        [.. File.ReadAllLines(ReferencePath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];

    // ------------------------------------------------------------------ 生成した μ

    [Fact]
    public void The_generated_mean_records_the_current_model_id_and_the_sha256_and_word_count_of_the_committed_reference_list()
    {
        Assert.Equal(HubnessReferenceMean.ModelId, ModelCatalog.Current.ModelId);                  // モデルを替えたら μ を作り直す(ここで気づく)
        Assert.Equal(HubnessReferenceMean.ReferenceSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ReferencePath))));   // 一覧を変えたら μ を作り直す
        string[] words = ReferenceWords();
        Assert.Equal(HubnessReferenceMean.WordCount, words.Length);
        Assert.Equal(words.Length, words.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_generated_mean_has_the_embedding_dimension_and_only_finite_values_and_is_not_normalized()
    {
        Assert.Equal(ModelCatalog.EmbeddingDim, HubnessReferenceMean.Mean.Length);
        Assert.Equal(1152, HubnessReferenceMean.Mean.Length);
        Assert.All(HubnessReferenceMean.Mean, x => Assert.True(float.IsFinite(x)));
        double norm = Math.Sqrt(HubnessReferenceMean.Mean.Sum(x => (double)x * x));
        Assert.InRange(norm, 0.1, 0.99);       // 単位ベクトルの平均なので、長さは 1 より小さい(正規化し直していない)。0 でもない
        Assert.Equal(ModelCatalog.Current.ModelId, HubnessCorrection.Default.ModelId);
        Assert.Equal(ModelCatalog.EmbeddingDim, HubnessCorrection.Default.Dim);
    }

    // ------------------------------------------------------------------ 関門

    [Theory]
    [InlineData("あいうえおかきく", true)]          // 8 字は通る
    [InlineData("あいうえおかきくけ", false)]        // 9 字は通らない
    [InlineData("あ い う え", true)]                // 空白も 1 字(7 字)
    [InlineData("あ い う え お", false)]            // 空白も 1 字(9 字)
    [InlineData("あ  い", true)]                     // 空白の連なりは整えて 1 字(3 字)
    [InlineData("cat", false)]                       // 英字だけは通らない
    [InlineData("cat in the box", false)]
    [InlineData("猫 cat", true)]                     // 日本語と英字の混在は通る
    [InlineData("cat 猫dog", true)]                  // 3 + 1 + 1 + 3 = 8 字
    [InlineData("a b c d e f g 猫", false)]          // 混在でも 9 字を超えれば通らない
    [InlineData("あいうえおかきく!", true)]          // 句読点は整えるときに消えるので、数えない(8 字)
    [InlineData("あいうえおかき𠮷", true)]           // コードポイントで数える(UTF-16 では 9)
    [InlineData("あいうえおかきく𠮷", false)]        // コードポイントで 9
    [InlineData("ﾈｺ", true)]                         // 半角カナは、正規化して全角のカナになってから日本語の文字になる
    [InlineData("123", false)]                       // 数字だけ
    [InlineData("!!!", false)]
    public void The_gate_passes_a_query_with_a_japanese_character_and_at_most_8_code_points_after_cleaning(string query, bool expected)
    {
        Assert.Equal(expected, HubnessGate.InScope(query));
        Assert.Equal(expected, HubnessCorrection.Default.AppliesTo(query));
    }

    [Fact]
    public void The_gate_counts_code_points_after_the_cleaning_the_text_encoder_applies()
    {
        Assert.Equal(8, HubnessGate.MaxCodePoints);
        Assert.Equal(8, HubnessGate.CodePointCount("あいうえおかきく"));
        Assert.Equal(8, HubnessGate.CodePointCount("あいうえおかき𠮷"));
        Assert.Equal(7, HubnessGate.CodePointCount("あ い う え"));
        Assert.Equal(3, HubnessGate.CodePointCount("  あ   い  "));      // 両端の空白は消え、連なりは 1 字
        Assert.Throws<ArgumentNullException>(() => HubnessGate.InScope(null!));
    }

    // ------------------------------------------------------------------ 点の式

    private static float[] Mean(double a, double b)
    {
        var m = new float[Dim];
        m[0] = (float)a;
        m[1] = (float)b;
        return m;
    }

    private static VectorTable ReadyTable(string modelId, params (int Id, double Angle)[] items)
    {
        var t = new VectorTable(modelId, Dim);
        foreach ((int id, double angle) in items) t.Add(Hash(id), Unit(angle));
        t.BeginLoading();
        t.EndLoading();
        return t;
    }

    [Fact]
    public void The_bias_is_the_inner_product_of_the_mean_and_the_image_vector_without_renormalizing()
    {
        var c = new HubnessCorrection(HubnessReferenceMean.ModelId, Mean(0.5, 0.25));
        Assert.Equal(0.5, c.Bias(Unit(0)), 6);                                        // 0.5·1 + 0.25·0
        Assert.Equal(0.25, c.Bias(Unit(90)), 6);                                      // 0.5·0 + 0.25·1
        Assert.Equal(0.5 * Math.Cos(Math.PI / 3) + 0.25 * Math.Sin(Math.PI / 3), c.Bias(Unit(60)), 6);
        Assert.Throws<ArgumentException>(() => c.Bias(new float[Dim + 1]));
    }

    [Fact]
    public async Task A_query_through_the_gate_ranks_by_cosine_minus_the_bias_and_keeps_the_original_cosine()
    {
        // 画像 1(角度 0)はクエリ e0 に最も近い(cos 1)が、μ とも近いので補正値が大きい。画像 2(角度 40)は補正値が小さい
        var correction = new HubnessCorrection("model-x", Mean(0.9, 0));
        VectorTable table = ReadyTable("model-x", (1, 0), (2, 40), (3, 80));
        var path = new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), correction);

        PathSearchResult r = await path.SearchAsync("猫", TestContext.Current.CancellationToken);

        Assert.True(r.ScoreCorrected);
        for (int i = 0; i < 3; i++)
        {
            float[] v = Unit(new[] { 0.0, 40, 80 }[i]);
            double cos = v[0];                                            // クエリ e0 との cosine
            double expected = cos - 0.9 * v[0];                           // 点 = cosine − μ·r
            Assert.Equal(expected, r.Scores[i], 6);
            var d = Assert.IsType<VisualDetail>(r.DetailOf(i));
            Assert.Equal(r.Scores[i], d.Score);                           // 順位を決めた点
            Assert.Equal(cos, d.Cosine, 6);                               // 元の cosine
        }
        // 補正なしなら 1 位の画像 1(cos 1)は、補正後(0.1)に 3 位(画像 3 は cos 0.17 − 0.9·0.17 = 0.017)より上、画像 2(0.766 − 0.69 = 0.077)より上
        Assert.Equal(0.1, r.Scores[0], 6);
        Assert.True(r.Scores[0] > r.Scores[1] && r.Scores[1] > r.Scores[2]);
    }

    [Fact]
    public async Task The_correction_can_change_the_order()
    {
        var correction = new HubnessCorrection("model-x", Mean(1.0, 0));
        VectorTable table = ReadyTable("model-x", (1, 0), (2, 40));
        PathSearchResult plain = await new VisualPath(table, (_, _) => Task.FromResult(Unit(0))).SearchAsync("猫", TestContext.Current.CancellationToken);
        PathSearchResult corrected = await new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), correction).SearchAsync("猫", TestContext.Current.CancellationToken);

        Assert.True(plain.Scores[0] > plain.Scores[1]);                   // 補正なし: 画像 1 が上
        Assert.Equal(0.0, corrected.Scores[0], 6);                        // 1 − 1·1
        Assert.Equal(0.0, corrected.Scores[1], 6);                        // cos 40° − 1·cos 40° (μ = e0 なので、点はどの画像も 0。順位は同点になる)
    }

    [Fact]
    public async Task A_query_outside_the_gate_gets_the_cosine_as_the_score_and_is_marked_uncorrected()
    {
        var correction = new HubnessCorrection("model-x", Mean(0.9, 0));
        VectorTable table = ReadyTable("model-x", (1, 0), (2, 40));
        var path = new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), correction);

        foreach (string query in new[] { "a cat in the box", "あいうえおかきくけ" })
        {
            PathSearchResult r = await path.SearchAsync(query, TestContext.Current.CancellationToken);
            Assert.False(r.ScoreCorrected);
            Assert.Equal(1.0, r.Scores[0], 6);
            Assert.Equal(Math.Cos(40 * Math.PI / 180), r.Scores[1], 6);
            var d = Assert.IsType<VisualDetail>(r.DetailOf(1));
            Assert.Equal(d.Cosine, d.Score);
        }
    }

    [Fact]
    public async Task Without_a_correction_no_query_is_corrected()
    {
        VectorTable table = ReadyTable("model-x", (1, 0), (2, 40));
        PathSearchResult r = await new VisualPath(table, (_, _) => Task.FromResult(Unit(0))).SearchAsync("猫", TestContext.Current.CancellationToken);
        Assert.False(r.ScoreCorrected);
        Assert.Equal(1.0, r.Scores[0], 6);
    }

    [Fact]
    public async Task An_overwritten_image_is_corrected_with_only_its_newer_vector()
    {
        var correction = new HubnessCorrection("model-x", Mean(0.5, 0));
        VectorTable table = ReadyTable("model-x", (1, 0), (2, 90));
        table.Add(Hash(1), Unit(60));                                     // 再埋め込みで値が変わった(古い行は無効)
        PathSearchResult r = await new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), correction).SearchAsync("猫", TestContext.Current.CancellationToken);

        Assert.Equal(2, r.Hashes.Count);
        double Score(int id) => r.Scores[r.Hashes.ToList().IndexOf(Hash(id))];
        Assert.Equal(0.5 - 0.5 * 0.5, Score(1), 5);                       // cos 60° = 0.5、b = 0.5·0.5
        Assert.Equal(0.0 - 0.0, Score(2), 5);
        Assert.True(r.ScoreCorrected);
    }

    // ------------------------------------------------------------------ モデル違いは例外

    [Fact]
    public void A_mean_made_with_another_model_or_dimension_is_rejected_when_the_path_is_made()
    {
        VectorTable table = ReadyTable("model-x");
        var other = new HubnessCorrection("model-y", Mean(0.5, 0));
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), other));
        Assert.Contains("model-y", e.Message, StringComparison.Ordinal);
        Assert.Contains("model-x", e.Message, StringComparison.Ordinal);

        var shortMean = new HubnessCorrection("model-x", new float[Dim - 1]);
        Assert.Throws<InvalidOperationException>(() => new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), shortMean));

        _ = new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), new HubnessCorrection("model-x", Mean(0.5, 0)));   // 合えば作れる
    }

    [Fact]
    public void A_mean_that_is_empty_or_not_finite_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new HubnessCorrection("m", ReadOnlySpan<float>.Empty));
        Assert.Throws<InvalidDataException>(() => new HubnessCorrection("m", new[] { 0.1f, float.NaN }));
        Assert.Throws<InvalidDataException>(() => new HubnessCorrection("m", new[] { 0.1f, float.PositiveInfinity }));
        Assert.Throws<ArgumentException>(() => new HubnessCorrection("", new[] { 0.1f }));
    }

    [Fact]
    public async Task The_search_service_reports_whether_the_query_was_corrected_and_the_hits_carry_both_values()
    {
        var (dir, db) = NewDb();
        using (dir) using (db)
        {
            string model = await RegisterModelAsync(db);
            long folder = await AddFolderAsync(db);
            foreach ((int id, double angle) in new[] { (1, 0.0), (2, 40.0) })
            {
                await AddImageAsync(db, folder, Hash(id), [$@"C:\shots\{id}.png"]);
                await AddVectorAsync(db, model, Hash(id), Unit(angle));
            }
            var table = new VectorTable(model, Dim);
            await table.LoadAsync(db, TestContext.Current.CancellationToken);
            var path = new VisualPath(table, (_, _) => Task.FromResult(Unit(0)), new HubnessCorrection(model, Mean(0.5, 0)));
            var service = new SearchService(db, [path], table);

            SearchResponse corrected = await service.SearchAsync(new SearchQuery("猫"), TestContext.Current.CancellationToken);
            Assert.True(Assert.Single(corrected.Paths).ScoreCorrected);
            PathHit hit = corrected.Results.First(r => r.Hash == Hash(2)).Hits.Single();
            var detail = Assert.IsType<VisualDetail>(hit.Detail);
            Assert.Equal(hit.Score, detail.Score);                                                // PathHit.Score も順位を決めた点
            Assert.Equal(Math.Cos(40 * Math.PI / 180), detail.Cosine, 5);
            Assert.Equal(detail.Cosine - 0.5 * Math.Cos(40 * Math.PI / 180), detail.Score, 5);

            SearchResponse plain = await service.SearchAsync(new SearchQuery("cat"), TestContext.Current.CancellationToken);
            Assert.False(Assert.Single(plain.Paths).ScoreCorrected);
        }
    }
}
