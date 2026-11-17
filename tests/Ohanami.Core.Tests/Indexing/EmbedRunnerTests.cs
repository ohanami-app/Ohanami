using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Ohanami.Core.Indexing;
using Ohanami.Core.Tests.Scanning;
using SkiaSharp;
using Xunit;

namespace Ohanami.Core.Tests.Indexing;

/// <summary>EmbedRunner を直接(Indexer なしで)。JobRunner の契約: RunEnd の 3 種類、資源の持ち方、画像の境目。</summary>
public sealed class EmbedRunnerTests : IDisposable
{
    private readonly IndexingFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RunRecorder NewRecorder() => new(_fx.Scan.Db, JobKind.Embed, "test-1.0", _fx.Scan.Time);

    [Fact]
    public async Task Drained_when_everything_is_done_and_resources_are_kept_until_release()
    {
        await _fx.AddImagesAsync(4);
        RunRecorder run = NewRecorder();

        RunEnd end = await _fx.Runner.RunAsync(run, () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(4, _fx.Factory.EmbedCount);
        Assert.Equal(0, _fx.Factory.DisposeCount); // 戻っても資源は握ったまま
        Assert.True(run.Started);
        Assert.Equal(4, run.CompletedCount);

        // 続けて呼べば、同じエンコーダで(作り直さずに)新しい分を処理する
        await _fx.AddImagesAsync(1, "more");
        Assert.Equal(RunEnd.Drained, await _fx.Runner.RunAsync(run, () => false, Ct));
        Assert.Equal(1, _fx.Factory.CreateCount);
        Assert.Equal(5, _fx.Factory.EmbedCount);

        await _fx.Runner.ReleaseAsync();
        Assert.Equal(1, _fx.Factory.DisposeCount);
        await _fx.Runner.ReleaseAsync(); // 2 回呼んでもよい
        Assert.Equal(1, _fx.Factory.DisposeCount);
    }

    [Fact]
    public async Task A_session_recreation_inside_the_encoder_is_logged_once_with_the_count_and_the_reason()
    {
        // スリープの復帰のあとの最初の Run が失敗して、エンコーダが作り直して成功した場合は、結果の上では見えない。ログに残す(ADR 0026)
        await _fx.AddImagesAsync(5);
        _fx.Factory.OnEmbed = index =>
        {
            if (index != 2) return;
            _fx.Factory.Last!.RecreateCount = 1;
            _fx.Factory.Last.LastRecreateReason = "CUDA error: the launch failed";
        };

        RunEnd end = await _fx.Runner.RunAsync(NewRecorder(), () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(5, _fx.Runner.Summarize().Single().Done);
        string line = Assert.Single(_fx.Log.Messages, m => m.Contains("セッションを作り直した", StringComparison.Ordinal));   // 1 回の作り直しは 1 行(以降の画像で重ねて出さない)
        Assert.StartsWith("INFO ", line, StringComparison.Ordinal);
        Assert.Contains("累計 1 回", line, StringComparison.Ordinal);
        Assert.Contains("CUDA error: the launch failed", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_recreation_means_no_recreation_line_and_a_second_recreation_is_a_second_line()
    {
        await _fx.AddImagesAsync(6);
        _fx.Factory.OnEmbed = index =>
        {
            if (index == 2) { _fx.Factory.Last!.RecreateCount = 1; _fx.Factory.Last.LastRecreateReason = "first"; }
            if (index == 5) { _fx.Factory.Last!.RecreateCount = 2; _fx.Factory.Last.LastRecreateReason = "second"; }
        };

        await _fx.Runner.RunAsync(NewRecorder(), () => false, Ct);

        string[] lines = _fx.Log.Messages.Where(m => m.Contains("セッションを作り直した", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains("累計 2 回", lines[1], StringComparison.Ordinal);
        Assert.Contains("second", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_any_recreation_nothing_is_logged()
    {
        await _fx.AddImagesAsync(3);

        await _fx.Runner.RunAsync(NewRecorder(), () => false, Ct);

        Assert.DoesNotContain(_fx.Log.Messages, m => m.Contains("セッションを作り直した", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Yielded_after_exactly_the_current_image_when_the_signal_turns_true()
    {
        await _fx.AddImagesAsync(10);
        RunRecorder run = NewRecorder();
        bool yield = false;
        _fx.Factory.OnEmbed = index => { if (index == 3) yield = true; }; // 3 枚目の処理中に合図が立つ

        RunEnd end = await _fx.Runner.RunAsync(run, () => yield, Ct);

        Assert.Equal(RunEnd.Yielded, end);
        Assert.Equal(3, run.CompletedCount); // 処理中の 3 枚目は確定してから戻る(捨てない)
        Assert.Equal(3, _fx.Runner.Summarize().Single().Done);
        Assert.Equal(0, _fx.Factory.DisposeCount);

        yield = false;
        Assert.Equal(RunEnd.Drained, await _fx.Runner.RunAsync(run, () => yield, Ct));
        Assert.Equal(10, _fx.Runner.Summarize().Single().Done);
        Assert.Equal(10, _fx.Factory.EmbedCount); // 先読みした分を含め、重複して Embed していない
        Assert.Equal(1, _fx.Factory.CreateCount);
    }

    [Fact]
    public async Task Yielded_before_the_first_image_when_the_signal_is_already_true()
    {
        await _fx.AddImagesAsync(3);
        RunEnd end = await _fx.Runner.RunAsync(NewRecorder(), () => true, Ct);
        Assert.Equal(RunEnd.Yielded, end);
        Assert.Equal(0, _fx.Factory.EmbedCount);
    }

    [Fact]
    public async Task Cancelled_at_an_image_boundary_without_throwing()
    {
        await _fx.AddImagesAsync(10);
        using var cts = new CancellationTokenSource();
        _fx.Factory.OnEmbed = index => { if (index == 4) cts.Cancel(); };
        RunRecorder run = NewRecorder();

        RunEnd end = await _fx.Runner.RunAsync(run, () => false, cts.Token);

        Assert.Equal(RunEnd.Cancelled, end);
        Assert.Equal(4, run.CompletedCount); // 処理中の 4 枚目は確定してから止まる
        Assert.Equal(4, _fx.Runner.Summarize().Single().Done);
        Assert.Equal(4, _fx.Count("SELECT count(*) FROM image_vectors"));
    }

    [Fact]
    public async Task Already_cancelled_token_does_nothing_and_creates_no_encoder()
    {
        await _fx.AddImagesAsync(3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        RunEnd end = await _fx.Runner.RunAsync(NewRecorder(), () => false, cts.Token);
        Assert.Equal(RunEnd.Cancelled, end);
        Assert.Equal(0, _fx.Factory.CreateCount);
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM runs"));
    }

    [Fact]
    public async Task Cancelled_while_creating_the_encoder_reports_cancelled_and_leaves_no_run_row()
    {
        await _fx.AddImagesAsync(3);
        using var cts = new CancellationTokenSource();
        _fx.Factory.BeforeCreate = async ct => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); };

        RunEnd end = await _fx.Runner.RunAsync(NewRecorder(), () => false, cts.Token);

        Assert.Equal(RunEnd.Cancelled, end);
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM runs"));
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM stage_status"));
    }

    [Fact]
    public async Task Model_row_is_registered_once_and_current_model_ids_and_stages_are_declared()
    {
        await _fx.AddImagesAsync(2);
        Assert.Equal(JobKind.Embed, _fx.Runner.Kind);
        Assert.Equal([Stage.Embed], _fx.Runner.Stages);
        Assert.Equal(_fx.Factory.Model.ModelId, _fx.Runner.CurrentModelIds[Stage.Embed]);

        await _fx.Runner.RunAsync(NewRecorder(), () => false, Ct);
        await _fx.Runner.RunAsync(NewRecorder(), () => false, Ct);

        Assert.Equal(1, _fx.Count("SELECT count(*) FROM models"));
        Assert.Equal("image-embedding", _fx.Scan.Text("SELECT kind FROM models"));
        await _fx.Runner.ReleaseAsync();
    }

    [Fact]
    public async Task Run_id_is_unavailable_before_the_run_row_exists()
    {
        RunRecorder run = NewRecorder();
        Assert.False(run.Started);
        Assert.Throws<InvalidOperationException>(() => run.RunId);
        await run.StartAsync("{}", Ct);
        Assert.True(run.RunId > 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.StartAsync("{}", Ct)); // 1 回しか作れない
        await run.EndAsync(Ct);
        Assert.NotNull(_fx.Scan.Text("SELECT ended_at FROM runs"));
    }

    [Fact]
    public async Task End_without_start_writes_nothing()
    {
        await NewRecorder().EndAsync(Ct);
        Assert.Equal(0, _fx.Count("SELECT count(*) FROM runs"));
    }

    // ---- 前処理の実物

    [Fact]
    public void Skia_preprocessor_returns_the_original_size_and_a_384_tensor()
    {
        byte[] png = ScanFixture.RealImage(SKEncodedImageFormat.Png, 7);
        PreprocessedImage image = new SkiaImagePreprocessor().Preprocess(png);
        Assert.Equal(8, image.Width);  // ScanFixture.RealImage は 8×8
        Assert.Equal(8, image.Height);
        Assert.Equal(3 * 384 * 384, image.Tensor.Length);
        Assert.All(image.Tensor.Take(1000), v => Assert.InRange(v, -1.0001f, 1.0001f)); // 平均・標準偏差 0.5 の正規化
    }

    [Fact]
    public void Skia_preprocessor_throws_for_data_that_is_not_an_image()
    {
        Assert.ThrowsAny<Exception>(() => new SkiaImagePreprocessor().Preprocess([1, 2, 3, 4]));
    }
}
