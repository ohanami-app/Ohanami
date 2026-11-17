using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Indexing;
using Ohanami.Core.Tests.Indexing;
using Ohanami.Core.Tests.Scanning;
using Ohanami.Core.Vlm;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>
/// Indexer に VlmRunner を載せた統合(ADR 0027): ジョブの順序(埋め込み → VLM)、一時停止・再開、埋め込みへの切り替え(走査の完了での切り替えの合図)、
/// スリープ抑止の寿命。埋め込みは偽のエンコーダ、VLM は別プロセスの偽の llama-server。
/// </summary>
public sealed class VlmIndexerTests : IDisposable
{
    private sealed class CountingGuard : IDisposable
    {
        private readonly Action _onDispose;

        public CountingGuard(Action onDispose) { _onDispose = onDispose; }

        public void Dispose() => _onDispose();
    }

    private readonly VlmFixture _fx;
    private readonly FakeEmbedderFactory _factory = new();
    private readonly List<string> _embedded = [];
    private readonly Indexer _indexer;
    private int _guardStarts, _guardDisposes;

    public VlmIndexerTests()
    {
        _fx = new VlmFixture("ok");
        var embed = new EmbedRunner(_fx.Scan.Db, _factory, new FakePreprocessor(), _fx.Scan.Time, _fx.Log, (hash, _) =>
        {
            lock (_embedded) _embedded.Add(hash);
        });
        _indexer = new Indexer(_fx.Scan.Db, _fx.Scan.Scanner, [embed, _fx.Runner], _fx.Scan.Time, "test-1.0", _fx.Log, () =>
        {
            Interlocked.Increment(ref _guardStarts);
            return new CountingGuard(() => Interlocked.Increment(ref _guardDisposes));
        });
    }

    public void Dispose()
    {
        _indexer.StopAsync().GetAwaiter().GetResult();
        _fx.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 30_000) => FakeLlama.UntilAsync(condition, timeoutMs);

    // ---------------------------------------------------------------- 順序・スリープ抑止

    [Fact]
    public async Task Embedding_runs_first_then_the_vlm_and_the_sleep_guard_lives_exactly_as_long_as_the_run()
    {
        await _fx.AddImagesAsync(3);

        await _indexer.StartAsync([JobKind.Embed, JobKind.Vlm], Ct);

        IndexState state = _indexer.State;
        Assert.Equal(IndexRunState.Waiting, state.Run);
        Assert.Equal([Stage.Embed, Stage.Ocr, Stage.Caption], state.Stages.Select(s => s.Stage).ToArray());
        Assert.All(state.Stages, s => Assert.Equal((0, 3, 0, 0), (s.Pending, s.Done, s.Failed, s.Outdated)));
        Assert.Equal(3, _embedded.Count);
        Assert.Equal(1, _guardStarts);    // ジョブが Running の間だけ
        Assert.Equal(1, _guardDisposes);  // 待機(Waiting)に入ったら解除(資源を空けている)
        // runs: 埋め込みの 1 回と VLM の 1 回(ジョブの切り替えで、資源を空けてから次のランナーを動かす)
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM runs WHERE job_kind = 'embed'"));
        Assert.Equal(1, _fx.Count("SELECT count(*) FROM runs WHERE job_kind = 'vlm'"));
        Assert.Equal(1, _fx.Log.Messages.Count(m => m.Contains("llama-server を起動した", StringComparison.Ordinal)));
        // ログ(ADR 0028): ジョブの完了(件数と所要)、待機に入る、VLM を止める理由、llama-server の停止(pid)
        Assert.Contains(_fx.Log.Messages, m => m == "INFO ジョブ: 埋め込みが終わった(処理 3 枚、失敗 0、飛ばした(画像) 0 枚、所要 00:00:00)");
        Assert.Contains(_fx.Log.Messages, m => m == "INFO ジョブ: VLM が終わった(処理 3 枚、失敗 0、飛ばした(画像) 0 枚、所要 00:00:00)");
        Assert.Contains(_fx.Log.Messages, m => m == "INFO ジョブ: VLM を止める(理由: 待機)");
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("INFO llama-server を止めた(pid ", StringComparison.Ordinal));
        Assert.Contains(_fx.Log.Messages, m => m == "INFO ジョブ: 待機に入る(新しい画像が来たら続ける。処理 埋め込み 3 枚・VLM 3 枚、失敗 埋め込み 0・VLM 0、飛ばした(画像) 0 枚、所要 00:00:00)");
    }

    [Fact]
    public async Task A_sleep_guard_that_cannot_be_started_only_warns_and_the_job_runs()
    {
        var embed = new EmbedRunner(_fx.Scan.Db, _factory, new FakePreprocessor(), _fx.Scan.Time, _fx.Log);
        var indexer = new Indexer(_fx.Scan.Db, _fx.Scan.Scanner, [embed], _fx.Scan.Time, "test-1.0", _fx.Log, () => throw new InvalidOperationException("抑止できない(テスト)"));
        await _fx.AddImagesAsync(2);

        await indexer.StartAsync([JobKind.Embed], Ct);

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(2, indexer.State.Stages.Single().Done);
        Assert.Contains(_fx.Log.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal) && m.Contains("スリープの抑止を始められなかった", StringComparison.Ordinal));
        await indexer.StopAsync();
    }

    // ---------------------------------------------------------------- 一時停止・再開

    [Fact]
    public async Task Pausing_in_the_middle_of_a_vlm_request_kills_the_server_and_resuming_redoes_that_image_in_a_new_run()
    {
        using var fx = new VlmFixture("hangonce");
        var embed = new EmbedRunner(fx.Scan.Db, _factory, new FakePreprocessor(), fx.Scan.Time, fx.Log);
        await using var indexerHolder = new IndexerHolder(new Indexer(fx.Scan.Db, fx.Scan.Scanner, [embed, fx.Runner], fx.Scan.Time, "test-1.0", fx.Log));
        Indexer indexer = indexerHolder.Indexer;
        await fx.AddImagesAsync(2);

        Task run = indexer.StartAsync([JobKind.Embed, JobKind.Vlm], Ct);
        Assert.True(await WaitAsync(() => File.Exists(fx.Settings.ServerEnvironment!["FAKE_LLAMA_MARKER"]))); // VLM の最初のリクエストが来て、止まっている
        PidRecord record = ChildProcessGuard.Parse(await File.ReadAllTextAsync(fx.Guard.PidFilePath, Ct));

        await indexer.PauseAsync();
        await run;

        Assert.Equal(IndexRunState.Paused, indexer.State.Run);
        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(record.Pid)), "一時停止で llama-server を kill する");
        Assert.Contains(fx.Log.Messages, m => m == "INFO ジョブ: VLM を止める(理由: 一時停止)");
        Assert.Contains(fx.Log.Messages, m => m == $"INFO llama-server を止めた(pid {record.Pid})");
        Assert.Equal(0, fx.StageCount("caption", "done"));    // 処理中の 1 枚は、未処理のまま

        await indexer.ResumeAsync(Ct);

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(2, fx.StageCount("caption", "done"));
        Assert.Equal(2, fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(2, fx.Count("SELECT count(*) FROM runs WHERE job_kind = 'vlm'"));   // 再開は新しい runs の行
    }

    private sealed class IndexerHolder : IAsyncDisposable
    {
        public IndexerHolder(Indexer indexer) { Indexer = indexer; }

        public Indexer Indexer { get; }

        public async ValueTask DisposeAsync() => await Indexer.StopAsync();
    }

    // ---------------------------------------------------------------- 走査の完了での切り替え

    [Fact]
    public async Task One_new_image_during_the_vlm_does_not_interrupt_it_and_is_embedded_right_after_the_vlm_finishes()
    {
        // 新着が 1 枚(20 枚・10 分に届かない)では、VLM を中断して llama-server を起動し直さない。VLM が待ちを使い切ったあと、取り残さずに埋め込む(ADR 0027)
        using var fx = new VlmFixture("slow", extraEnv: FakeLlama.Env(("FAKE_LLAMA_SLOW_MS", "700")));
        string? newHash = null;
        long captionsDoneWhenNewImageEmbedded = -1;
        var embed = new EmbedRunner(fx.Scan.Db, _factory, new FakePreprocessor(), fx.Scan.Time, fx.Log, (hash, _) =>
        {
            if (hash == Volatile.Read(ref newHash)) captionsDoneWhenNewImageEmbedded = fx.StageCount("caption", "done");
        });
        await using var holder = new IndexerHolder(new Indexer(fx.Scan.Db, fx.Scan.Scanner, [embed, fx.Runner], fx.Scan.Time, "test-1.0", fx.Log));
        Indexer indexer = holder.Indexer;
        await fx.AddImagesAsync(3);

        Task run = indexer.StartAsync([JobKind.Embed, JobKind.Vlm], Ct);
        Assert.True(await WaitAsync(() => fx.Log.Messages.Any(m => m.Contains("llama-server を起動した", StringComparison.Ordinal)))); // VLM が動き始めた(埋め込みは済んだ)
        byte[] bytes = ScanFixture.FakePng("new-arrival");
        Volatile.Write(ref newHash, VlmFixture.Sha(bytes));
        fx.Scan.Write("new.png", bytes);
        await indexer.ScanAsync(null, Ct);   // 走査の完了。優先のジョブ(埋め込み)の新しく入った待ちは 1 枚で、切り替える条件に届かない
        await run;

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(4, indexer.State.Stages.Single(s => s.Stage == Stage.Embed).Done);
        Assert.Equal(4, fx.StageCount("caption", "done"));   // VLM も最後まで続いた(新着の 1 枚も、VLM の次の待ちの取り直しで拾った)
        Assert.Equal(4, captionsDoneWhenNewImageEmbedded);   // 新着の画像の埋め込みは、VLM が全部終わったあと
        // VLM は中断されず、llama-server は 1 回だけ起動した(切り替えの往復が無い)
        Assert.Equal(1, fx.Count("SELECT count(*) FROM runs WHERE job_kind = 'vlm'"));
        Assert.Equal(1, fx.Log.Messages.Count(m => m.Contains("llama-server を起動した", StringComparison.Ordinal)));
        // VLM が使い切ったあと、待機に入らずに埋め込みへ戻った(埋め込みの実行は 2 回。待機に入るのは最後に 1 回だけ)
        Assert.Equal(2, fx.Log.Messages.Count(m => m.StartsWith("INFO ジョブ: 埋め込みが終わった(", StringComparison.Ordinal)));
        Assert.Equal(1, fx.Log.Messages.Count(m => m.StartsWith("INFO ジョブ: 待機に入る(", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Twenty_new_images_during_the_vlm_switch_to_embedding_before_the_vlm_finishes()
    {
        using var fx = new VlmFixture("slow", extraEnv: FakeLlama.Env(("FAKE_LLAMA_SLOW_MS", "400")));
        var embed = new EmbedRunner(fx.Scan.Db, _factory, new FakePreprocessor(), fx.Scan.Time, fx.Log);
        await using var holder = new IndexerHolder(new Indexer(fx.Scan.Db, fx.Scan.Scanner, [embed, fx.Runner], fx.Scan.Time, "test-1.0", fx.Log));
        Indexer indexer = holder.Indexer;
        await fx.AddImagesAsync(2);

        Task run = indexer.StartAsync([JobKind.Embed, JobKind.Vlm], Ct);
        Assert.True(await WaitAsync(() => fx.Log.Messages.Any(m => m.Contains("llama-server を起動した", StringComparison.Ordinal))));
        for (int i = 0; i < 20; i++) fx.Scan.Write($"new{i:00}.png", ScanFixture.FakePng($"new-arrival-{i}"));
        await indexer.ScanAsync(null, Ct);   // 走査の完了で、新しく入った埋め込みの待ちが 20 枚 → 切り替えの合図

        Assert.True(await WaitAsync(() => indexer.State.Stages.Single(s => s.Stage == Stage.Embed).Done == 22));
        Assert.True(fx.StageCount("caption", "done") < 22);   // 新着は、VLM が全部終わる前に埋め込まれた
        await indexer.PauseAsync();
        await run;
        Assert.True(fx.Count("SELECT count(*) FROM runs WHERE job_kind = 'vlm'") >= 1);
        Assert.Contains(fx.Log.Messages, m => m.Contains("ジョブ: VLM を止める(理由: 切り替え)", StringComparison.Ordinal));   // 切り替えでの llama-server の停止(Indexer の行)
        Assert.Contains(fx.Log.Messages, m => m.Contains("llama-server を止めた(pid ", StringComparison.Ordinal));          // LlamaServer の行
    }

    // ---------------------------------------------------------------- 手動の再処理

    [Fact]
    public async Task Retrying_failed_vlm_images_puts_both_stages_back_in_the_queue()
    {
        using var fx = new VlmFixture("400");
        var embed = new EmbedRunner(fx.Scan.Db, _factory, new FakePreprocessor(), fx.Scan.Time, fx.Log);
        await using var holder = new IndexerHolder(new Indexer(fx.Scan.Db, fx.Scan.Scanner, [embed, fx.Runner], fx.Scan.Time, "test-1.0", fx.Log));
        await fx.AddImagesAsync(2);
        await holder.Indexer.StartAsync([JobKind.Vlm], Ct);
        Assert.Equal(2, fx.StageCount("caption", "failed"));

        await holder.Indexer.RetryFailedAsync(JobKind.Vlm, Ct);

        Assert.Equal(0, fx.StageCount("caption", "failed"));
        Assert.Equal(0, fx.StageCount("ocr", "failed"));
        Assert.All(fx.Runner.Summarize(), s => Assert.Equal(2, s.Pending));
    }
}
