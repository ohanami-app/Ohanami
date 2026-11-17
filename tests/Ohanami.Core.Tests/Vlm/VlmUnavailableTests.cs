using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Indexing;
using Ohanami.Core.Tests.Indexing;
using Ohanami.Core.Vlm;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>
/// VLM のモデルのファイルが無いときは、実行が失敗せず、特徴ベクトルの作成だけが進んで待機に入る。ファイルがそろったあとの開始で、VLM が始まる。
/// 実物の <see cref="VlmRunner"/> と <see cref="Indexer"/>、別プロセスの偽の llama-server で確かめる。
/// </summary>
public sealed class VlmUnavailableTests : IDisposable
{
    private readonly VlmFixture _fx = new("ok");
    private readonly FakeEmbedderFactory _factory = new();
    private readonly List<string> _embedded = [];
    private readonly Indexer _indexer;

    public VlmUnavailableTests()
    {
        var embed = new EmbedRunner(_fx.Scan.Db, _factory, new FakePreprocessor(), _fx.Scan.Time, _fx.Log, (hash, _) =>
        {
            lock (_embedded) _embedded.Add(hash);
        });
        _indexer = new Indexer(_fx.Scan.Db, _fx.Scan.Scanner, [embed, _fx.Runner], _fx.Scan.Time, "test-1.0", _fx.Log);
    }

    public void Dispose()
    {
        _indexer.StopAsync().GetAwaiter().GetResult();
        _fx.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ModelPath(string relative) => Path.Combine(_fx.ModelDirectory, relative);

    [Theory]
    [InlineData("m.gguf")]
    [InlineData("mm.gguf")]
    public async Task When_either_model_file_is_missing_embedding_proceeds_and_the_run_waits_without_starting_the_server(string missing)
    {
        await _fx.AddImagesAsync(3);
        byte[] saved = File.ReadAllBytes(ModelPath(missing));
        File.Delete(ModelPath(missing));

        await _indexer.StartAsync([JobKind.Embed, JobKind.Vlm], Ct);

        IndexState state = _indexer.State;
        Assert.Equal(IndexRunState.Waiting, state.Run);                          // Failed ではない
        Assert.Null(state.LastError);
        Assert.Equal(3, _embedded.Count);                                        // 特徴ベクトルは普通に進む
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM runs WHERE job_kind = 'embed'"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM runs WHERE job_kind = 'vlm'"));
        Assert.DoesNotContain(_fx.Log.Messages, m => m.Contains("llama-server を起動した", StringComparison.Ordinal));
        Assert.Equal(3, state.Stages.Single(s => s.Stage == Stage.Caption).Pending);   // VLM の待ちは待ちのまま
        Assert.Equal(3, state.Stages.Single(s => s.Stage == Stage.Embed).Done);
        UnavailableJob unavailable = Assert.Single(state.UnavailableJobs);
        Assert.Equal(JobKind.Vlm, unavailable.Job);
        Assert.Equal([missing], unavailable.MissingFiles);

        File.WriteAllBytes(ModelPath(missing), saved);                           // 利用者がファイルをそろえた
        Assert.Empty(_indexer.State.UnavailableJobs);
        await _indexer.StartAsync([JobKind.Embed, JobKind.Vlm], Ct);

        Assert.Equal(IndexRunState.Waiting, _indexer.State.Run);
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM runs WHERE job_kind = 'vlm'"));
        Assert.Equal(3, _indexer.State.Stages.Single(s => s.Stage == Stage.Caption).Done);
        Assert.Empty(_indexer.State.UnavailableJobs);
    }

    [Fact]
    public async Task A_model_file_with_a_wrong_size_still_fails_the_run()
    {
        await _fx.AddImagesAsync(2);
        File.WriteAllBytes(ModelPath("m.gguf"), new byte[10]);                   // 在るが、サイズが違う(途中のファイル)。「未取得」ではなく、検証の失敗

        await _indexer.StartAsync([JobKind.Embed, JobKind.Vlm], Ct);

        Assert.Equal(IndexRunState.Failed, _indexer.State.Run);
        Assert.Empty(_indexer.State.UnavailableJobs);
    }
}
