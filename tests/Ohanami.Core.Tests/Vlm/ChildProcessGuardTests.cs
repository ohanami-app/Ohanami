using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Ohanami.Core.Vlm;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>孤児の防止・掃除。pid ファイルの同一性の判定は偽のプロセス調べで、実際の kill とジョブオブジェクトは偽サーバの exe で確かめる。</summary>
public sealed class ChildProcessGuardTests
{
    private const string Exe = @"C:\app\llama-server.exe";
    private const long Start = 638_000_000_000_000_000;

    /// <summary>プロセスの調べ方の偽物: 決めた pid だけ「居る」と答え、KillTree の呼び出しを覚える。</summary>
    private sealed class FakeInspector : IProcessInspector
    {
        public Dictionary<int, ProcessSnapshot> Running { get; } = [];
        public List<int> Killed { get; } = [];

        public ProcessSnapshot? Find(int pid) => Running.TryGetValue(pid, out ProcessSnapshot? s) ? s : null;

        public void KillTree(int pid) => Killed.Add(pid);
    }

    private static string Write(TempDirectory dir, PidRecord record)
    {
        string path = dir.File("llama-server.pid");
        File.WriteAllText(path, ChildProcessGuard.Serialize(record));
        return path;
    }

    private static (ChildProcessGuard Guard, FakeInspector Inspector, TestLog Log) NewGuard(TempDirectory dir)
    {
        var inspector = new FakeInspector();
        var log = new TestLog();
        return (FakeLlama.Guard(dir, log, inspector, useJob: false), inspector, log);
    }

    [Fact]
    public void The_pid_file_and_its_temporary_file_are_named_the_way_the_data_removal_script_expects()
    {
        // データ削除の道具(src/Ohanami.RemoveData/Ohanami-RemoveData.ps1)は、データの場所の直下の
        // 「llama-server.pid」と「llama-server.pid.<32 桁の 16 進>.tmp」という名前だけを頼りに消す。ここを変えたら、道具の表も同じに直す。
        // (一時ファイルは、改名で消えて観測できないので、書き手のソースを読む)
        string guardSource = CommentStripper.StripCSharp(File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Ohanami.Core", "Vlm", "ChildProcessGuard.cs")));
        Assert.Contains("PidFilePath + \".\" + Guid.NewGuid().ToString(\"N\") + \".tmp\"", guardSource, StringComparison.Ordinal);
        string coreSource = CommentStripper.StripCSharp(File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Ohanami.Core", "OhanamiCore.cs")));
        Assert.Contains("new ChildProcessGuard(Path.Combine(dbDirectory, \"llama-server.pid\")", coreSource, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 同一性の判定(偽のプロセス調べ)

    [Fact]
    public void No_pid_file_means_nothing_to_do()
    {
        using var dir = new TempDirectory();
        (ChildProcessGuard guard, FakeInspector inspector, _) = NewGuard(dir);

        Assert.Equal(SweepOutcome.NoFile, guard.SweepOrphans());
        Assert.Empty(inspector.Killed);
    }

    [Fact]
    public void A_process_with_the_same_pid_start_time_and_exe_is_killed_and_the_file_removed()
    {
        using var dir = new TempDirectory();
        (ChildProcessGuard guard, FakeInspector inspector, TestLog log) = NewGuard(dir);
        Write(dir, new PidRecord(4242, Start, Exe, 5000));
        inspector.Running[4242] = new ProcessSnapshot(Start, Exe);

        SweepOutcome outcome = guard.SweepOrphans();

        Assert.Equal(SweepOutcome.Killed, outcome);
        Assert.Equal([4242], inspector.Killed);
        Assert.False(File.Exists(guard.PidFilePath));
        Assert.Contains(log.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal) && m.Contains("孤児", StringComparison.Ordinal));
    }

    [Fact]
    public void The_exe_path_is_compared_ignoring_case_and_redundant_segments()
    {
        using var dir = new TempDirectory();
        (ChildProcessGuard guard, FakeInspector inspector, _) = NewGuard(dir);
        Write(dir, new PidRecord(7, Start, @"C:\App\Llama-Server.exe", 1));
        inspector.Running[7] = new ProcessSnapshot(Start, @"c:\app\sub\..\llama-server.exe");

        Assert.Equal(SweepOutcome.Killed, guard.SweepOrphans());
    }

    [Fact]
    public void A_reused_pid_with_a_different_start_time_is_not_killed_but_the_stale_record_is_removed()
    {
        using var dir = new TempDirectory();
        (ChildProcessGuard guard, FakeInspector inspector, _) = NewGuard(dir);
        Write(dir, new PidRecord(4242, Start, Exe, 5000));
        inspector.Running[4242] = new ProcessSnapshot(Start + 10_000_000, Exe); // 1 秒後に始まった別のプロセス

        Assert.Equal(SweepOutcome.DifferentProcess, guard.SweepOrphans());
        Assert.Empty(inspector.Killed);
        Assert.False(File.Exists(guard.PidFilePath));
    }

    [Fact]
    public void A_reused_pid_running_a_different_exe_is_not_killed()
    {
        using var dir = new TempDirectory();
        (ChildProcessGuard guard, FakeInspector inspector, _) = NewGuard(dir);
        Write(dir, new PidRecord(4242, Start, Exe, 5000));
        inspector.Running[4242] = new ProcessSnapshot(Start, @"C:\Windows\System32\notepad.exe");

        Assert.Equal(SweepOutcome.DifferentProcess, guard.SweepOrphans());
        Assert.Empty(inspector.Killed);
    }

    [Fact]
    public void A_process_whose_exe_cannot_be_read_is_not_killed()
    {
        using var dir = new TempDirectory();
        (ChildProcessGuard guard, FakeInspector inspector, TestLog log) = NewGuard(dir);
        Write(dir, new PidRecord(4242, Start, Exe, 5000));
        inspector.Running[4242] = new ProcessSnapshot(Start, null);

        Assert.Equal(SweepOutcome.Unverifiable, guard.SweepOrphans());
        Assert.Empty(inspector.Killed);
        Assert.Contains(log.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_of_a_process_that_is_gone_is_just_removed()
    {
        using var dir = new TempDirectory();
        (ChildProcessGuard guard, FakeInspector inspector, _) = NewGuard(dir);
        Write(dir, new PidRecord(4242, Start, Exe, 5000));

        Assert.Equal(SweepOutcome.NotRunning, guard.SweepOrphans());
        Assert.Empty(inspector.Killed);
        Assert.False(File.Exists(guard.PidFilePath));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"pid\":1,\"startTimeUtcTicks\":5,\"exePath\":\"x\"}")]                       // port が無い
    [InlineData("{\"pid\":\"1\",\"startTimeUtcTicks\":5,\"exePath\":\"x\",\"port\":1}")]        // pid が文字列
    [InlineData("{\"pid\":0,\"startTimeUtcTicks\":5,\"exePath\":\"x\",\"port\":1}")]            // pid が 0
    [InlineData("{\"pid\":1,\"startTimeUtcTicks\":0,\"exePath\":\"x\",\"port\":1}")]            // 開始時刻が 0
    [InlineData("{\"pid\":1,\"startTimeUtcTicks\":5,\"exePath\":\"\",\"port\":1}")]             // exe のパスが空
    public void A_corrupt_pid_file_is_removed_with_a_warning_and_nothing_is_killed(string content)
    {
        using var dir = new TempDirectory();
        (ChildProcessGuard guard, FakeInspector inspector, TestLog log) = NewGuard(dir);
        File.WriteAllText(guard.PidFilePath, content);

        Assert.Equal(SweepOutcome.Corrupt, guard.SweepOrphans());
        Assert.Empty(inspector.Killed);
        Assert.False(File.Exists(guard.PidFilePath));
        Assert.Contains(log.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pid_file_round_trips()
    {
        var record = new PidRecord(12, 638_123_456_789_012_345, @"C:\Program Files\llama\llama-server.exe", 61843);

        PidRecord back = ChildProcessGuard.Parse(ChildProcessGuard.Serialize(record));

        Assert.Equal(record, back);
    }

    // ---------------------------------------------------------------- 実際のプロセス

    private static Process StartSleeper()
    {
        var psi = new ProcessStartInfo(FakeLlama.ExePath) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--sleep");
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task Sweep_kills_a_real_orphan_that_matches_its_record()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log, useJob: false);
        using Process orphan = StartSleeper();
        try
        {
            File.WriteAllText(guard.PidFilePath, ChildProcessGuard.Serialize(new PidRecord(orphan.Id, orphan.StartTime.ToUniversalTime().Ticks, FakeLlama.ModuleFileName(orphan, FakeLlama.ExePath), 1)));

            Assert.Equal(SweepOutcome.Killed, guard.SweepOrphans());

            Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(orphan.Id)));
        }
        finally
        {
            if (!orphan.HasExited) orphan.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Sweep_does_not_kill_a_real_process_whose_start_time_differs_from_the_record()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log, useJob: false);
        using Process other = StartSleeper();
        try
        {
            // pid の使い回しの模擬: pid と exe は同じだが、記録の開始時刻は別(前のプロセスのもの)
            File.WriteAllText(guard.PidFilePath, ChildProcessGuard.Serialize(new PidRecord(other.Id, other.StartTime.ToUniversalTime().Ticks - 50_000_000, FakeLlama.ModuleFileName(other, FakeLlama.ExePath), 1)));

            Assert.Equal(SweepOutcome.DifferentProcess, guard.SweepOrphans());

            Assert.True(FakeLlama.IsRunning(other.Id), "別のプロセスを殺してはいけない");
        }
        finally
        {
            if (!other.HasExited) other.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Register_writes_the_record_and_unregister_removes_only_the_matching_pid()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log, useJob: false);
        using Process child = StartSleeper();
        try
        {
            bool inJob = guard.Register(child, FakeLlama.ExePath, 4321);

            Assert.False(inJob); // ジョブを使わない設定
            PidRecord record = ChildProcessGuard.Parse(File.ReadAllText(guard.PidFilePath));
            Assert.Equal(child.Id, record.Pid);
            Assert.Equal(child.StartTime.ToUniversalTime().Ticks, record.StartTimeUtcTicks);
            Assert.Equal(4321, record.Port);

            guard.Unregister(child.Id + 1); // 別の pid の記録ではないので、消さない
            Assert.True(File.Exists(guard.PidFilePath));
            guard.Unregister(child.Id);
            Assert.False(File.Exists(guard.PidFilePath));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Register_records_the_launched_path_not_whatever_the_main_module_says_right_after_the_start()
    {
        // 起動した直後の MainModule は、まれに ntdll.dll など exe でない名前を返す(ADR 0026)。記録は、渡された起動したパス(フルパスに直したもの)にする
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log, useJob: false);
        using Process child = StartSleeper();
        try
        {
            string launched = Path.Combine(dir.Path, "sub", "..", "llama-server.exe");   // 子の本当の exe とは別の、正規化が要る名前

            guard.Register(child, launched, 1);

            PidRecord record = ChildProcessGuard.Parse(File.ReadAllText(guard.PidFilePath));
            Assert.Equal(Path.GetFullPath(launched), record.ExePath);
            Assert.False(string.Equals(Path.GetFullPath(FakeLlama.ExePath), record.ExePath, StringComparison.OrdinalIgnoreCase));   // 子の本当の exe のパスではない(MainModule を読んでいない)
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void A_record_with_a_wrong_exe_name_such_as_ntdll_is_not_swept_and_the_record_is_dropped()
    {
        // 掃除の側の確認(コードの読みを、実際に動かして固定する): 記録の exe が本当の exe と違えば(起動直後に ntdll.dll を読んで記録した場合など)、別のプロセスと判断して殺さず、記録も消える
        using var dir = new TempDirectory();
        var log = new TestLog();
        using ChildProcessGuard guard = FakeLlama.Guard(dir, log, useJob: false);
        using Process orphan = StartSleeper();
        try
        {
            // 起動した直後は、掃除の側が exe のパスを読めず(null → Unverifiable)、読めても ntdll.dll のことがある。本当のパスが読めるまで待ってから掃除する
            FakeLlama.ModuleFileName(orphan, FakeLlama.ExePath);
            string wrong = Path.Combine(Environment.SystemDirectory, "ntdll.dll");
            File.WriteAllText(guard.PidFilePath, ChildProcessGuard.Serialize(new PidRecord(orphan.Id, orphan.StartTime.ToUniversalTime().Ticks, wrong, 1)));

            Assert.Equal(SweepOutcome.DifferentProcess, guard.SweepOrphans());

            Assert.True(FakeLlama.IsRunning(orphan.Id));            // 殺さない(孤児が残る)
            Assert.False(File.Exists(guard.PidFilePath));            // 記録は消える(もう孤児として探されない)
        }
        finally
        {
            if (!orphan.HasExited) orphan.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Disposing_the_guard_closes_the_job_and_the_registered_child_is_killed_by_the_os()
    {
        using var dir = new TempDirectory();
        var log = new TestLog();
        ChildProcessGuard guard = FakeLlama.Guard(dir, log);
        Assert.True(guard.JobObjectAvailable);
        using Process child = StartSleeper();
        try
        {
            Assert.True(guard.Register(child, FakeLlama.ExePath, 1));

            guard.Dispose(); // ジョブのハンドルを閉じる = 親が終わったときと同じ

            Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(child.Id)), "ジョブを閉じたら、子は OS に終わらされる");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    /// <summary>親を強制終了したとき(TerminateProcess。<c>taskkill /F</c> と同じ効果)に、ジョブに入れた子が消えること(ADR 0026)。</summary>
    [Fact]
    public async Task Killing_the_parent_process_makes_the_os_kill_the_child_in_the_job_but_not_a_child_outside_it()
    {
        int withJob = await RunParentAndKillAsync(useJob: true);
        int withoutJob = await RunParentAndKillAsync(useJob: false);   // 対照: ジョブが無ければ、子は残る(テストが効いていることの確認)

        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(withJob), 15_000), "ジョブに入れた子は、親の強制終了で消える");
        Assert.True(FakeLlama.IsRunning(withoutJob), "ジョブに入れなかった子は残る(対照)");
        Process.GetProcessById(withoutJob).Kill(entireProcessTree: true);
    }

    private static async Task<int> RunParentAndKillAsync(bool useJob)
    {
        var psi = new ProcessStartInfo(FakeLlama.ExePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("--guard-parent");
        if (!useJob) psi.ArgumentList.Add("--no-job");
        using Process parent = Process.Start(psi)!;
        string? line = await parent.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(line);
        int childPid = int.Parse(line!.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(FakeLlama.IsRunning(childPid));

        parent.Kill(entireProcessTree: false); // 親だけを強制終了(子を道連れにしない)
        await parent.WaitForExitAsync(TestContext.Current.CancellationToken);
        return childPid;
    }
}
