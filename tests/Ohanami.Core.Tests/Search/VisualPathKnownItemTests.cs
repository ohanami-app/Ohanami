using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Ohanami.Core.Models;
using Ohanami.Core.Search;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 製品の見た目の経路(<see cref="VisualPath"/>)が、SigLIP2 の評価(ADR 0037)で測った known-item の順位を再現すること(手元専用。コミットされないデータを使う)。
/// 評価の GPU 測定の出力ディレクトリ(embeddings.f32、embedding-names.tsv、known-items-result.tsv)を環境変数 OHANAMI_S2_RUN_DIR で指す
/// (spikes/S2-siglip2/runs/ の下。runs/ は Git の管理外)。無ければ skip。
/// 実物のテキスト側モデル(CPU)でクエリを埋め込み、全画像の cosine で順位を付けて(同点は平均順位)、評価の順位
/// (厳しく良い画像の数 + 1)+ 同点の数 / 2 と比べる。known-items-result.tsv のすべての行(日本語・英語など 123 行)で一致すること。
/// </summary>
public sealed class VisualPathKnownItemTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Model")]
    public async Task Visual_path_reproduces_the_known_item_ranks_of_the_evaluation_run()
    {
        string? dir = Environment.GetEnvironmentVariable("OHANAMI_S2_RUN_DIR");
        Assert.SkipUnless(!string.IsNullOrEmpty(dir), "環境変数 OHANAMI_S2_RUN_DIR が無い(SigLIP2 の評価の GPU 測定の出力ディレクトリ。手元専用の確認)");

        // 画像の埋め込み: 評価の embeddings.f32(1030 枚 × 1152 次元)を、ファイル名をキーにして表に入れる
        string[] names = File.ReadLines(Path.Combine(dir!, "embedding-names.tsv")).Skip(1).Select(l => l.Split('\t')[1]).ToArray();
        float[][] vectors = EmbeddingTests.ReadF32(Path.Combine(dir!, "embeddings.f32"), ModelCatalog.EmbeddingDim);
        Assert.Equal(names.Length, vectors.Length);
        var table = new VectorTable("known-item", ModelCatalog.EmbeddingDim);
        for (int i = 0; i < names.Length; i++) table.Add(names[i], vectors[i]);
        table.BeginLoading();
        table.EndLoading();

        // クエリの埋め込み: 製品のテキスト側(CPU)とトークナイザ
        string modelDir = Path.Combine(TestFiles.ModelsDir, ModelCatalog.DirectoryName);
        await ModelVerifier.VerifyTextSideAsync(modelDir, ModelCatalog.Current, TestContext.Current.CancellationToken);
        Siglip2Tokenizer tokenizer = Siglip2Tokenizer.Load(TestFiles.VerifiedModelFile("textual/tokenizer.json"));
        using Siglip2TextEncoder encoder = Siglip2TextEncoder.Create(modelDir, ModelCatalog.Current);
        var path = new VisualPath(table, VisualPath.TextEncoderEmbedder(encoder, Task.FromResult(tokenizer)));

        int checkedRows = 0, differing = 0;
        var examples = new List<string>();
        foreach (string line in File.ReadLines(Path.Combine(dir!, "known-items-result.tsv")).Skip(1))
        {
            string[] c = line.Split('\t');
            string file = c[1], query = c[2];
            int evaluationRank = int.Parse(c[3], CultureInfo.InvariantCulture), tied = int.Parse(c[4], CultureInfo.InvariantCulture);
            double expected = evaluationRank + tied / 2.0;                // 評価: 厳しく良い画像の数 + 1。tied は同点の数 → 平均順位

            PathSearchResult r = await path.SearchAsync(query, TestContext.Current.CancellationToken);
            int target = names.ToList().IndexOf(file);                    // 表の添字 = 追加した順 = 名前の並び
            RankedPath ranked = RankedPath.Dense(r.Scores.ToArray());
            double ours = ranked.MeanRank[target];
            checkedRows++;
            if (Math.Abs(ours - expected) > 1e-9)
            {
                differing++;
                if (examples.Count < 10) examples.Add($"[{c[0]}] 「{query}」 {file}: 製品 {ours} / 評価 {expected}");
            }
        }
        output.WriteLine($"評価の known-item {checkedRows} 行のうち、順位(同点は平均)が違う行: {differing}");
        foreach (string e in examples) output.WriteLine("  " + e);
        Assert.True(checkedRows >= 90);
        Assert.True(differing == 0, string.Join("\n", examples));
    }
}
