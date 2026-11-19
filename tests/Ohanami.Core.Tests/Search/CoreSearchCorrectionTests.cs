using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Embedding.Generated;
using Ohanami.Core.Models;
using Ohanami.Core.Search;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests;

/// <summary>
/// <see cref="OhanamiCore"/> が組み立てた検索に、補正(ADR 0051)がかかっていること。
/// 経路を直接組むテスト(<see cref="HubnessCorrectionTests"/>)では、製品の組み立てで補正の引数が落ちても気づけない。
/// ここでは Core を開き、表の合成のベクトルと、差し替えた検索語のベクトルで、製品の <see cref="OhanamiCore.Search"/> を通す(モデルの実物は使わない)。
/// </summary>
public sealed class CoreSearchCorrectionTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<OhanamiCore> OpenAsync()
    {
        string models = _dir.File("no-models");
        Directory.CreateDirectory(models);
        var options = new CoreOptions(_dir.File("data/ohanami.db"), _dir.File("thumbs"), models, AppContext.BaseDirectory, new TestLog(), null, WatchFolders: false);
        return OhanamiCore.OpenAsync(options, configureNative: false, time: null, Ct);
    }

    [Fact]
    public async Task The_search_the_core_builds_subtracts_the_generated_mean_for_a_short_japanese_query_and_not_for_an_english_one()
    {
        await using OhanamiCore core = await OpenAsync();
        int dim = ModelCatalog.EmbeddingDim;

        // 画像 1 は μ の向き(補正値 μ·r が |μ| ≧ 0.1 になる)。画像 2 は e0 の向き(補正値は μ の第 0 成分)
        float[] mean = [.. HubnessReferenceMean.Mean];
        double norm = Math.Sqrt(mean.Sum(x => (double)x * x));
        float[] alongMean = [.. mean.Select(x => (float)(x / norm))];
        float[] alongE0 = new float[dim];
        alongE0[0] = 1;
        float[] query = new float[dim];
        query[0] = 1;

        long folder = await AddFolderAsync(core.DatabaseForTests);
        foreach ((int id, float[] v) in new[] { (1, alongMean), (2, alongE0) })
        {
            await AddImageAsync(core.DatabaseForTests, folder, Hash(id), [$@"C:\shots\{id}.png"]);
            core.VectorsForTests.Add(Hash(id), v);
        }
        core.QueryEmbedderForTests = (_, _) => Task.FromResult(query);

        double bias1 = HubnessCorrection.Default.Bias(alongMean);
        Assert.True(bias1 >= 0.1, $"補正値が小さすぎて、補正の有無を区別できない: {bias1}");

        SearchResponse corrected = await core.Search.SearchAsync(new SearchQuery("猫"), Ct);
        Assert.True(Assert.Single(corrected.Paths, p => p.PathId == VisualPath.Id).ScoreCorrected);
        var d1 = Assert.IsType<VisualDetail>(corrected.Results.First(r => r.Hash == Hash(1)).Hits.Single(h => h.PathId == VisualPath.Id).Detail);
        Assert.Equal(alongMean[0], d1.Cosine, 5);
        Assert.Equal(d1.Cosine - bias1, d1.Score, 5);
        var d2 = Assert.IsType<VisualDetail>(corrected.Results.First(r => r.Hash == Hash(2)).Hits.Single(h => h.PathId == VisualPath.Id).Detail);
        Assert.Equal(1.0, d2.Cosine, 5);
        Assert.Equal(1.0 - HubnessCorrection.Default.Bias(alongE0), d2.Score, 5);

        SearchResponse plain = await core.Search.SearchAsync(new SearchQuery("cat"), Ct);
        Assert.False(Assert.Single(plain.Paths, p => p.PathId == VisualPath.Id).ScoreCorrected);
        var p1 = Assert.IsType<VisualDetail>(plain.Results.First(r => r.Hash == Hash(1)).Hits.Single(h => h.PathId == VisualPath.Id).Detail);
        Assert.Equal(p1.Cosine, p1.Score);                                                  // 補正なしは、点 = cosine
    }
}
