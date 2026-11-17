using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Indexing;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Ohanami.Core.Vlm;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>OhanamiCore の VLM の組み込み(<see cref="CoreOptions.Vlm"/>): 開くときには何も検証・起動しない、前回の孤児の掃除、VLM を設定しないときの扱い、致命的な失敗の伝わり方。</summary>
public sealed class CoreVlmTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CoreOptions Options(TempDirectory dir, TestLog log, VlmOptions? vlm) => new(
        DatabasePath: dir.File("sub/ohanami.db"),
        ThumbnailDirectory: dir.File("thumbs"),
        ModelDirectory: dir.File("no-models"),
        NativeDirectory: AppContext.BaseDirectory,
        Log: log,
        Vlm: vlm);

    private static Task<OhanamiCore> Open(CoreOptions options) => OhanamiCore.OpenAsync(options, configureNative: false, time: null, Ct);

    [Fact]
    public async Task Opening_with_vlm_options_starts_and_verifies_nothing()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File("no-models"));
        var log = new TestLog();
        var vlm = new VlmOptions(FakeLlama.ExePath, dir.File("vlm-models"));   // モデルの置き場は存在しない

        await using OhanamiCore core = await Open(Options(dir, log, vlm));
        await core.WaitUntilStartedAsync(Ct);

        Assert.Equal(IndexRunState.Idle, core.Indexer.State.Run);
        Assert.DoesNotContain(log.Messages, m => m.Contains("llama-server を起動した", StringComparison.Ordinal));
        Assert.False(File.Exists(dir.File("sub/llama-server.pid")));      // pid ファイルも、起動するまで作られない
        Assert.Contains(core.Indexer.State.Stages, s => s.Stage == Stage.Caption);   // VLM のランナーは組み立てられている(段階の件数が出る)
    }

    [Fact]
    public async Task A_vlm_job_without_the_model_files_is_not_fetched_not_failed_and_starts_nothing()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File("no-models"));
        var log = new TestLog();
        await using OhanamiCore core = await Open(Options(dir, log, new VlmOptions(FakeLlama.ExePath, dir.File("vlm-models"))));
        string library = dir.File("library");
        Directory.CreateDirectory(library);
        TestFiles.WriteAged(Path.Combine(library, "a.png"), Scanning.ScanFixture.FakePng("a"));
        FolderInfo folder = await core.Folders.AddAsync(library, Ct);
        await core.Indexer.ScanAsync([folder.Id], Ct);

        await core.Indexer.StartAsync([JobKind.Vlm], Ct);

        IndexState state = core.Indexer.State;
        Assert.Equal(IndexRunState.Waiting, state.Run);   // 失敗ではない。モデルのファイルがそろえば、次の開始で始まる
        Assert.Null(state.LastError);
        Assert.Equal(EnginePhase.NotCreated, state.Engine.Phase);
        UnavailableJob unavailable = Assert.Single(state.UnavailableJobs);   // 足りないファイルが、画面に出る
        Assert.Equal(JobKind.Vlm, unavailable.Job);
        Assert.Equal(ModelFiles.InspectVlm(dir.File("vlm-models")).MissingFiles, unavailable.MissingFiles);
        Assert.Equal(2, unavailable.MissingFiles.Count);
        Assert.Equal(0, state.Stages.Single(s => s.Stage == Stage.Caption).Done);
        Assert.Equal(1, state.Stages.Single(s => s.Stage == Stage.Caption).Pending);
        Assert.False(File.Exists(dir.File("sub/llama-server.pid")));
    }

    [Fact]
    public async Task Without_vlm_options_there_is_no_vlm_job()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File("no-models"));
        await using OhanamiCore core = await Open(Options(dir, new TestLog(), vlm: null));

        Assert.DoesNotContain(core.Indexer.State.Stages, s => s.Stage == Stage.Caption);
        ArgumentException ex = Assert.Throws<ArgumentException>(() => { _ = core.Indexer.StartAsync([JobKind.Vlm], Ct); });
        Assert.Contains("ランナーが無い", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_sweeps_an_orphaned_llama_server_recorded_by_the_previous_run()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File("no-models"));
        Directory.CreateDirectory(dir.File("sub"));
        var log = new TestLog();
        // 前回の孤児の代わり: 偽サーバの眠るモードのプロセスと、その記録(DB と同じディレクトリの llama-server.pid)
        var psi = new ProcessStartInfo(FakeLlama.ExePath) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--sleep");
        using Process orphan = Process.Start(psi)!;
        try
        {
            string exe = FakeLlama.ModuleFileName(orphan, FakeLlama.ExePath);
            await File.WriteAllTextAsync(dir.File("sub/llama-server.pid"), ChildProcessGuard.Serialize(new PidRecord(orphan.Id, orphan.StartTime.ToUniversalTime().Ticks, exe, 4321)), Ct);

            await using OhanamiCore core = await Open(Options(dir, log, new VlmOptions(FakeLlama.ExePath, dir.File("vlm-models"))));

            Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(orphan.Id)), "DB を開いたあとに、前回の孤児を掃除する");
            Assert.False(File.Exists(dir.File("sub/llama-server.pid")));
            Assert.Contains(log.Messages, m => m.Contains("前回の llama-server の掃除: Killed", StringComparison.Ordinal));
        }
        finally
        {
            if (!orphan.HasExited) orphan.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Without_vlm_options_a_stale_pid_file_is_left_alone()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.File("no-models"));
        Directory.CreateDirectory(dir.File("sub"));
        await File.WriteAllTextAsync(dir.File("sub/llama-server.pid"), "{}", Ct);

        await using OhanamiCore core = await Open(Options(dir, new TestLog(), vlm: null));

        Assert.True(File.Exists(dir.File("sub/llama-server.pid"))); // VLM を使わない設定では、触らない
    }
}
