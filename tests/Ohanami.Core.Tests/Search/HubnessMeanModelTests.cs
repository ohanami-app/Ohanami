using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Ohanami.Core.Embedding.Generated;
using Ohanami.Core.Models;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 生成した μ(<see cref="HubnessReferenceMean"/>)が、参照の語の一覧を製品のテキスト埋め込み(CPU)に通した平均と合うこと(Category=Model。モデルの実物を読む)。
/// 生成の流れ(Bench の hubness-mean)と同じ道を、Core のエンコーダで辿る: 一覧の語 → <see cref="Siglip2TextEncoder.EmbedQuery"/> → double で足して語数で割る。
/// </summary>
public sealed class HubnessMeanModelTests(ITestOutputHelper output)
{
    /// <summary>
    /// 成分ごとの差の許す大きさ(絶対値)。
    /// 手元(CPU 4 スレッド)で作り直した値と生成物の差の最大は 1.33e-8 だった。生成物は float で持つので、double の平均との差には、float に丸める分が必ず入る。
    /// μ の成分の絶対値の最大は 0.498 で、その幅(0.25 以上 0.5 未満)の float の刻みは 2.98e-8、丸めの差の上限はその半分の 1.49e-8。測った値はこの上限の中にあり、
    /// 作り直しの計算そのものの揺れは、この PC では見えなかった。
    /// 別の PC や別のスレッド数で、ONNX Runtime の行列積の足す順が変わる分は、手元では測っていない。そこで、測った値の 75 倍の 1e-6 を許す(float の丸めの揺れに足りる余裕)。
    /// 一方、一覧の語が 1 語入れ替わると、μ の成分は 1/411 倍の差(語のベクトルの成分は 0.03 前後なので、1e-4 の桁)だけ動く。
    /// 1e-6 はこれより 2 桁小さいので、一覧やモデルの取り違えは見逃さない。
    /// </summary>
    private const double ComponentTolerance = 1e-6;

    private static string ModelDirectory => Path.Combine(TestFiles.ModelsDir, ModelCatalog.DirectoryName);

    [Fact]
    [Trait("Category", "Model")]
    public async Task The_generated_mean_matches_the_mean_recomputed_from_the_reference_list()
    {
        await ModelVerifier.VerifyTextSideAsync(ModelDirectory, ModelCatalog.Current, TestContext.Current.CancellationToken);
        string[] words = [.. File.ReadAllLines(Path.Combine(RepoPaths.Root, "tools", "Ohanami.Bench", "hubness-reference-ja.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];
        Assert.Equal(HubnessReferenceMean.WordCount, words.Length);

        Siglip2Tokenizer tokenizer = Siglip2Tokenizer.Load(TestFiles.VerifiedModelFile("textual/tokenizer.json"));
        using Siglip2TextEncoder encoder = Siglip2TextEncoder.Create(ModelDirectory, ModelCatalog.Current, 4);
        var sum = new double[ModelCatalog.EmbeddingDim];
        foreach (string w in words)
        {
            float[] v = encoder.EmbedQuery(w, tokenizer);
            for (int d = 0; d < sum.Length; d++) sum[d] += v[d];
        }

        double maxDiff = 0, dot = 0, na = 0, nb = 0;
        for (int d = 0; d < sum.Length; d++)
        {
            double mean = sum[d] / words.Length;
            double stored = HubnessReferenceMean.Mean[d];
            maxDiff = Math.Max(maxDiff, Math.Abs(mean - stored));
            dot += mean * stored;
            na += mean * mean;
            nb += stored * stored;
        }
        double cosine = dot / Math.Sqrt(na * nb);
        output.WriteLine($"μ の作り直し: 成分ごとの差の最大 {maxDiff:E2}(許す差 {ComponentTolerance:E1})、生成物との cos {cosine:F9}、{words.Length} 語");
        Assert.True(maxDiff <= ComponentTolerance, $"成分ごとの差の最大 {maxDiff:E2} が許す差 {ComponentTolerance:E1} を超えた。参照の一覧かモデルを替えたなら、Bench の hubness-mean で μ を作り直す。");
        Assert.True(cosine > 0.999999, $"cos {cosine:F9}");
    }
}
