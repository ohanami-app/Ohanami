using System;
using System.Collections.Generic;
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

/// <summary>
/// LlamaServer: 起動・/health の待ち・異常終了・kill・ログ・孤児の掃除。偽の llama-server(実物の GPU を使わない)で確かめる。
/// 実物の llama-server を使うものは <see cref="RealLlamaServerTests"/>(Category=Gpu。手元専用)。
/// </summary>
public sealed class LlamaServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Arguments_are_built_explicitly_in_the_documented_order()
    {
        using var dir = new TempDirectory();
        LlamaServerLaunch launch = FakeLlama.Launch(dir) with { ImageMinTokens = 256, ChatTemplateKwargs = "{\"enable_thinking\":false}" };

        IReadOnlyList<string> args = LlamaServerArguments.Build(launch, 54321);

        Assert.Equal(
            [
                "-m", dir.File("model.gguf"), "--mmproj", dir.File("mmproj.gguf"), "--host", "127.0.0.1", "--port", "54321",
                "-lm", "none", "-ngl", "99", "-c", "8192", "-np", "1", "-cram", "0", "--no-mmproj-offload",
                "--image-min-tokens", "256", "--image-max-tokens", "3584", "--chat-template-kwargs", "{\"enable_thinking\":false}", "--no-webui",
            ],
            args);
    }

    [Fact]
    public void Optional_arguments_are_left_out_when_not_set_and_bad_input_is_rejected()
    {
        using var dir = new TempDirectory();
        LlamaServerLaunch plain = FakeLlama.Launch(dir, imageMaxTokens: null);

        IReadOnlyList<string> args = LlamaServerArguments.Build(plain, 1000);

        Assert.DoesNotContain("--image-max-tokens", args);
        Assert.DoesNotContain("--image-min-tokens", args);
        Assert.DoesNotContain("--chat-template-kwargs", args);
        Assert.Equal("--no-webui", args[^1]);
        Assert.Throws<ArgumentException>(() => LlamaServerArguments.Build(plain with { GgufPath = "C:\\a\"b.gguf" }, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => LlamaServerArguments.Build(plain, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LlamaServerArguments.Build(plain, 70000));
    }

    [Fact]
    public async Task Start_waits_through_503_until_health_is_200_and_passes_the_arguments()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using LlamaServer server = await LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_START_DELAY_MS", "1500"))), guard, log, Ct);

        Assert.True(sw.ElapsedMilliseconds >= 1200, $"503 の間は待つはず(待ち {sw.ElapsedMilliseconds}ms)");
        Assert.True(await server.CheckHealthAsync(Ct));
        Assert.False(server.HasExited);
        Assert.True(FakeLlama.IsRunning(server.ProcessId));
        Assert.Equal(new Uri($"http://127.0.0.1:{server.Port}"), server.BaseUri);
        Assert.Contains("--port " + server.Port, string.Join(' ', server.Arguments));
        // 子には、同じ引数が渡っている(偽サーバが出力する)
        Assert.True(await FakeLlama.UntilAsync(() => server.LogTail().Contains("--image-max-tokens 3584", StringComparison.Ordinal)));
        Assert.Contains("-ngl 99 -c 8192 -np 1 -cram 0", server.LogTail(), StringComparison.Ordinal);
        Assert.True(File.Exists(server.LogPath));
    }

    [Fact]
    public async Task Start_registers_the_child_in_the_pid_file_and_stop_kills_it_and_removes_the_record()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);
        LlamaServer server = await LlamaServer.StartAsync(FakeLlama.Launch(dir), guard, log, Ct);
        int pid = server.ProcessId;

        PidRecord record = ChildProcessGuard.Parse(await File.ReadAllTextAsync(guard.PidFilePath, Ct));
        Assert.Equal(pid, record.Pid);
        Assert.Equal(server.Port, record.Port);
        Assert.EndsWith("Ohanami.FakeLlamaServer.exe", record.ExePath, StringComparison.OrdinalIgnoreCase);

        await server.StopAsync();
        await server.StopAsync(); // 2 回呼んでもよい

        Assert.True(server.HasExited || !FakeLlama.IsRunning(pid));
        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(pid)));
        Assert.False(File.Exists(guard.PidFilePath));
        Assert.False(await server.CheckHealthAsync(Ct));
    }

    [Fact]
    public async Task A_first_start_that_exits_early_is_retried_on_another_port()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);
        string marker = dir.File("first.marker");

        await using LlamaServer server = await LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_EXIT_FIRST_MARKER", marker))), guard, log, Ct);

        Assert.True(File.Exists(marker)); // 1 回目は失敗した
        Assert.True(await server.CheckHealthAsync(Ct));
        Assert.Contains(log.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal) && m.Contains("起動中に終わった", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_every_attempt_exits_early_start_fails_with_the_log_tail_and_leaves_no_process_or_pid_file()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);

        LlamaServerException ex = await Assert.ThrowsAsync<LlamaServerException>(() => LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_FAIL_ALWAYS", "1")), attempts: 2), guard, log, Ct));

        Assert.Contains("起動中に終了した", ex.Message, StringComparison.Ordinal);
        Assert.Contains("address already in use", ex.Message, StringComparison.Ordinal); // ログの末尾(失敗の理由)が入っている
        Assert.Equal(2, log.Messages.Count(m => m.Contains("起動中に終わった", StringComparison.Ordinal)));
        Assert.False(File.Exists(guard.PidFilePath));
    }

    [Fact]
    public async Task A_start_that_ends_with_the_dll_not_found_code_fails_at_once_with_the_remedy_and_is_not_retried()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);

        // 0xC0000135(STATUS_DLL_NOT_FOUND)。偽のサーバは、何も出力せずにこの終了コードで終わる
        LlamaServerException ex = await Assert.ThrowsAsync<LlamaServerException>(() => LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_EXIT_CODE", "-1073741515")), attempts: 3), guard, log, Ct));

        Assert.StartsWith("アプリのファイルが足りないか、壊れています。zip を展開し直してください (", ex.Message, StringComparison.Ordinal);   // すべきことが先頭にある
        Assert.Contains("VLM の処理プログラムが必要な DLL を読めず、起動できませんでした。終了コード 0xC0000135", ex.Message, StringComparison.Ordinal);
        Assert.Contains(log.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal) && m.Contains("llama-server が DLL を読めずに終了した(終了コード 0xC0000135)", StringComparison.Ordinal));
        Assert.DoesNotContain("-1073741515", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Messages, m => m.Contains("起動中に終わった", StringComparison.Ordinal));   // ポート違いの再試行をしていない
        Assert.False(File.Exists(guard.PidFilePath));
    }

    [Fact]
    public async Task Other_early_exit_codes_keep_the_old_message_and_the_retries()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);

        LlamaServerException ex = await Assert.ThrowsAsync<LlamaServerException>(() => LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_EXIT_CODE", "7")), attempts: 2), guard, log, Ct));

        Assert.Contains("起動中に終了した(終了コード 7)", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, log.Messages.Count(m => m.Contains("起動中に終わった", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_start_where_cuda_could_not_be_initialised_fails_with_the_gpu_sentence_instead_of_running_on_the_cpu()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);

        // 偽のサーバは、CUDA を初期化できなかった行を出したあと、普通に起動を続ける(実物は CPU に落ちて、/health が 200 になる)
        LlamaServerException ex = await Assert.ThrowsAsync<LlamaServerException>(() => LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_CUDA_INIT_FAILED", "1"), ("FAKE_LLAMA_START_DELAY_MS", "300"))), guard, log, Ct));

        Assert.Equal(Ohanami.Core.Native.NativeFailure.GpuUnavailableMessage, ex.Message);
        Assert.Contains(log.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal) && m.Contains("CUDA を初期化できなかった", StringComparison.Ordinal));
        Assert.False(File.Exists(guard.PidFilePath));   // 起動を失敗にしたとき、プロセスは残らない
    }

    [Fact]
    public async Task Health_that_never_reaches_200_times_out_with_the_log_tail_and_the_process_is_killed()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);

        LlamaServerException ex = await Assert.ThrowsAsync<LlamaServerException>(() => LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_NEVER_HEALTHY", "1")), healthTimeout: TimeSpan.FromSeconds(1.5)), guard, log, Ct));

        Assert.Contains("200 にならなかった", ex.Message, StringComparison.Ordinal);
        Assert.Contains("503", ex.Message, StringComparison.Ordinal);
        int pid = await FakeLlama.PidFromLogAsync(dir); // 偽サーバが出した pid
        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(pid)), "タイムアウトしたプロセスを残さない");
        Assert.False(File.Exists(guard.PidFilePath));
    }

    [Fact]
    public async Task An_unexpected_health_status_fails_immediately_instead_of_waiting()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        LlamaServerException ex = await Assert.ThrowsAsync<LlamaServerException>(() => LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_HEALTH_CODE", "500")), healthTimeout: TimeSpan.FromSeconds(30)), guard, log, Ct));

        Assert.Contains("想定外の HTTP 500", ex.Message, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), "503 以外は待たずに失敗する");
        int pid = await FakeLlama.PidFromLogAsync(dir);
        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(pid)));
    }

    [Fact]
    public async Task A_crash_after_start_is_visible_as_exit_and_failed_health()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);

        await using LlamaServer server = await LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_EXIT_AFTER_MS", "1500"))), guard, log, Ct);
        Assert.True(await server.CheckHealthAsync(Ct));

        Assert.True(await FakeLlama.UntilAsync(() => server.HasExited, 15_000));
        Assert.Equal(3, server.ExitCode);
        Assert.False(await server.CheckHealthAsync(Ct));
        // HasExited は出力の読み取りの終わりを待たない。負荷が高いと、終了の直前の行(fake crash)は、少し遅れてログに入る。読み切るまで待つ製品の読み方で読む
        Assert.Contains("fake crash", await server.LogTailAfterExitAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_log_tail_after_exit_waits_for_the_output_but_only_up_to_the_limit_and_says_so_when_a_grandchild_holds_the_pipes()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);

        // 偽サーバは、出力のパイプを引き継いだ孫(眠る)を作ってから、0.5 秒後に落ちる。孫が生きているあいだ、読む側は EOF を受け取れない
        await using LlamaServer server = await LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_GRANDCHILD", "1"), ("FAKE_LLAMA_EXIT_AFTER_MS", "500")), outputDrainTimeout: TimeSpan.FromMilliseconds(700)), guard, log, Ct);
        int grandchildPid = 0;
        try
        {
            Assert.True(await FakeLlama.UntilAsync(() => server.HasExited, 15_000));
            // 読めた分(fake crash と孫の pid の行)が、非同期の読み取りでログに届くまで、条件で待つ。上限の 0.7 秒の間に届くことには頼らない(負荷で読み取りが遅れると、時間の余裕では落ちる)
            Assert.True(await FakeLlama.UntilAsync(() =>
            {
                string current = server.LogTail();
                return current.Contains("fake crash", StringComparison.Ordinal) && current.Contains("grandchild pid", StringComparison.Ordinal);
            }, 15_000), "fake crash と孫の pid の行がログに届かなかった: " + server.LogTail());
            var sw = System.Diagnostics.Stopwatch.StartNew();

            string tail = await server.LogTailAfterExitAsync();
            sw.Stop();

            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(tail, @"grandchild pid (\d+)");
            Assert.True(m.Success, "孫の pid の行が無い: " + tail);
            grandchildPid = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(FakeLlama.IsRunning(grandchildPid), "孫が生きていないと、パイプは握られていない");
            Assert.EndsWith(LlamaServer.OutputIncompleteNote, tail, StringComparison.Ordinal);   // 読み切れなかったことが分かる一行
            Assert.Contains("fake crash", tail, StringComparison.Ordinal);                         // 読めた分は入っている
            Assert.True(sw.ElapsedMilliseconds >= 600, $"上限(0.7 秒)まで待つはず(待ち {sw.ElapsedMilliseconds}ms)");
            Assert.True(sw.ElapsedMilliseconds < 8_000, $"上限なしで待ってはいけない(待ち {sw.ElapsedMilliseconds}ms)");
        }
        finally
        {
            if (grandchildPid == 0)
            {
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(server.LogTail(), @"grandchild pid (\d+)");
                if (m.Success) grandchildPid = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            KillIfRunning(grandchildPid);
        }
    }

    [Fact]
    public async Task The_log_tail_after_exit_has_no_note_when_the_output_was_read_to_the_end()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);

        await using LlamaServer server = await LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_EXIT_AFTER_MS", "500"))), guard, log, Ct);
        Assert.True(await FakeLlama.UntilAsync(() => server.HasExited, 15_000));

        string tail = await server.LogTailAfterExitAsync();

        Assert.EndsWith("fake crash", tail, StringComparison.Ordinal);
        Assert.DoesNotContain(LlamaServer.OutputIncompleteNote, tail, StringComparison.Ordinal);
    }

    private static void KillIfRunning(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { /* もう無い */ }
        catch (InvalidOperationException) { /* もう終わっている */ }
    }

    [Fact]
    public async Task Cancelling_the_start_kills_the_process()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<LlamaServer> start = LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_NEVER_HEALTHY", "1")), healthTimeout: TimeSpan.FromSeconds(60)), guard, log, cts.Token);
        // 固定の時間では切らない: 負荷が高いと、偽サーバ(.NET の起動)が「pid N」をログに書く前に殺されて、プロセスが残っていないことを pid で確かめられない。
        // 偽サーバが起動して pid をログに書いた(=サーバが立ち上がった。まだ /health に答えない)のを見てから切る
        int pid = await FakeLlama.PidFromLogAsync(dir);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);

        Assert.False(File.Exists(guard.PidFilePath)); // 止めた記録も残さない
        // 偽サーバのプロセスが残っていないこと: ログに残った pid で確かめる
        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(pid)));
    }

    [Fact]
    public async Task The_log_tail_keeps_the_last_40_lines_and_the_log_file_has_everything()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);

        await using LlamaServer server = await LlamaServer.StartAsync(
            FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_PRINT_LINES", "60"))), guard, log, Ct);
        // 標準出力と標準エラーは別々に非同期で読まれ、到着の順は決まらない。全部(出力 60 行 + エラー 60 行 + args の行)がログに届くまで待ってから、末尾を調べる
        string file = "";
        Assert.True(await FakeLlama.UntilAsync(() =>
        {
            file = ReadSharedAsync(server.LogPath).GetAwaiter().GetResult();
            return file.Contains("[err] fake-err-60", StringComparison.Ordinal) && file.Contains("[out] args:", StringComparison.Ordinal);
        }));

        string[] tail = server.LogTail().Split((char)10);
        Assert.True(tail.Length <= 40);
        Assert.DoesNotContain("fake-out-1", tail); // 古い行は落ちている
        Assert.Contains("[out] fake-out-1", file, StringComparison.Ordinal);
        Assert.Contains("[err] fake-err-60", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_latest_three_log_files_are_kept()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);

        for (int i = 0; i < 5; i++)
        {
            await using LlamaServer s = await LlamaServer.StartAsync(FakeLlama.Launch(dir), guard, log, Ct);
            await Task.Delay(5, Ct);
        }

        Assert.Equal(3, Directory.GetFiles(dir.File("logs"), "llama-server-*.log").Length);
    }

    [Fact]
    public async Task A_missing_executable_is_a_start_failure_not_a_crash()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);
        LlamaServerLaunch launch = FakeLlama.Launch(dir) with { ExePath = dir.File("no-such-llama-server.exe") };

        LlamaServerException ex = await Assert.ThrowsAsync<LlamaServerException>(() => LlamaServer.StartAsync(launch, guard, log, Ct));

        Assert.Contains("起動できなかった", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_first_sweeps_an_orphan_recorded_in_the_pid_file()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log);
        // 前回の孤児の代わり: 偽サーバの眠るモードのプロセスと、その記録
        var psi = new System.Diagnostics.ProcessStartInfo(FakeLlama.ExePath) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--sleep");
        using System.Diagnostics.Process orphan = System.Diagnostics.Process.Start(psi)!;
        try
        {
            string exe = FakeLlama.ModuleFileName(orphan, FakeLlama.ExePath);
            await File.WriteAllTextAsync(guard.PidFilePath, ChildProcessGuard.Serialize(new PidRecord(orphan.Id, orphan.StartTime.ToUniversalTime().Ticks, exe, 1234)), Ct);

            await using LlamaServer server = await LlamaServer.StartAsync(FakeLlama.Launch(dir), guard, log, Ct);

            Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(orphan.Id)), "前回の孤児を、起動の前に終わらせる");
            Assert.NotEqual(orphan.Id, server.ProcessId);
            PidRecord now = ChildProcessGuard.Parse(await File.ReadAllTextAsync(guard.PidFilePath, Ct));
            Assert.Equal(server.ProcessId, now.Pid); // 記録は、今の子に替わっている
        }
        finally
        {
            if (!orphan.HasExited) orphan.Kill(entireProcessTree: true);
        }
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        // 子の出力のログは、書かれている最中でも読む
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
