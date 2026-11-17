using System;
using System.Collections.Generic;
using System.Linq;
using Ohanami.Bench;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>
/// llama-server のプロセスごとの VRAM の測定(<see cref="ProcessVramMonitor"/>)と、big の判定。性能カウンタは偽物に替える(GPU も PDH も使わない)。
/// 実物のカウンタの読み取りは、<c>Ohanami.Bench gpu-process-memory</c> で確かめる。
/// </summary>
public sealed class ProcessVramTests
{
    private const long MiB = 1024L * 1024;
    private const string AdapterA = "luid_0x00000000_0x00010CC1_phys_0";
    private const string AdapterB = "luid_0x00000000_0x00011E95_phys_0";

    /// <summary>1 回ごとの答えを順に返す偽物。答えが尽きたら最後を繰り返す。例外を返すこともできる。</summary>
    private sealed class FakeSource : IProcessGpuMemory
    {
        private readonly List<object> _answers = [];
        private int _next;
        public int Reads { get; private set; }

        /// <summary>この回の答え。アダプタは AdapterA(llama-server の GPU)。</summary>
        public FakeSource Then(params (int Pid, long MiBs)[] values)
        {
            _answers.Add(values.ToDictionary(v => new GpuInstance(v.Pid, AdapterA), v => v.MiBs * MiB));
            return this;
        }

        /// <summary>この回の答え。アダプタを指定する。</summary>
        public FakeSource ThenOn(params (int Pid, string Adapter, long MiBs)[] values)
        {
            _answers.Add(values.ToDictionary(v => new GpuInstance(v.Pid, v.Adapter), v => v.MiBs * MiB));
            return this;
        }

        public FakeSource ThenFail(string message)
        {
            _answers.Add(new ProcessGpuMemoryException(message));
            return this;
        }

        public IReadOnlyDictionary<GpuInstance, long> ReadDedicatedBytes()
        {
            Reads++;
            object answer = _answers[Math.Min(_next, _answers.Count - 1)];
            _next++;
            if (answer is Exception e) throw e;
            return (IReadOnlyDictionary<GpuInstance, long>)answer;
        }
    }

    private static ProcessVramMonitor Monitor(IProcessGpuMemory source, params int[] pids) => new(source, () => pids);

    // ---------------------------------------------------------------- インスタンス名

    [Theory]
    [InlineData("pid_29652_luid_0x00000000_0x00010CC1_phys_0", 29652)]
    [InlineData("pid_4_luid_0x00000000_0x00010CC1_phys_0", 4)]
    [InlineData("pid_77", 77)]
    public void A_pid_is_read_from_the_instance_name(string name, int expected)
    {
        Assert.True(PdhProcessGpuMemory.TryParseInstanceName(name, out GpuInstance instance));
        Assert.Equal(expected, instance.Pid);
    }

    [Fact]
    public void The_adapter_is_the_part_of_the_instance_name_after_the_pid()
    {
        Assert.True(PdhProcessGpuMemory.TryParseInstanceName("pid_29652_luid_0x00000000_0x00010CC1_phys_0", out GpuInstance a));
        Assert.Equal(new GpuInstance(29652, "luid_0x00000000_0x00010CC1_phys_0"), a);
        Assert.True(PdhProcessGpuMemory.TryParseInstanceName("pid_29652_luid_0x00000000_0x00011E95_phys_0", out GpuInstance b));
        Assert.NotEqual(a, b);                                         // 同じ pid でもアダプタが違えば別のインスタンス
        Assert.True(PdhProcessGpuMemory.TryParseInstanceName("pid_77", out GpuInstance c));
        Assert.Equal("", c.Adapter);
    }

    [Fact]
    public void A_name_that_does_not_start_with_pid_is_ignored_but_a_pid_name_that_cannot_be_read_fails_loudly()
    {
        Assert.False(PdhProcessGpuMemory.TryParseInstanceName("_Total", out _));
        Assert.Throws<ProcessGpuMemoryException>(() => PdhProcessGpuMemory.TryParseInstanceName("pid_abc_luid_0x0_0x1_phys_0", out _));
        Assert.Throws<ProcessGpuMemoryException>(() => PdhProcessGpuMemory.TryParseInstanceName("pid__luid", out _));
        Assert.Throws<ProcessGpuMemoryException>(() => PdhProcessGpuMemory.TryParseInstanceName("pid_-5_luid", out _));
    }

    // ---------------------------------------------------------------- ピーク

    [Fact]
    public void The_peak_is_the_largest_value_over_time_rounded_up_to_whole_mib()
    {
        var source = new FakeSource().Then((100, 3000)).Then((100, 6058)).Then((100, 5000));
        using ProcessVramMonitor m = Monitor(source, 100);
        for (int i = 0; i < 3; i++) m.Sample();
        ProcessVramMeasurement r = m.AllResult();
        Assert.Null(r.Problem);
        Assert.Equal(6058, r.PeakMiB);
        Assert.Equal(100, r.PeakPid);
        Assert.Equal(AdapterA, r.PeakAdapter);
        Assert.Equal(3, r.Samples);
        Assert.Equal(3, r.ValuedSamples);

        // バイトから MiB は切り上げ(足切りに対して甘くしない)
        using ProcessVramMonitor m2 = Monitor(new OddSource(6056L * MiB + 1), 100);
        m2.Sample();
        Assert.Equal(6057, m2.AllResult().PeakMiB);
    }

    private sealed class OddSource(long bytes) : IProcessGpuMemory
    {
        public IReadOnlyDictionary<GpuInstance, long> ReadDedicatedBytes() => new Dictionary<GpuInstance, long> { [new GpuInstance(100, AdapterA)] = bytes };
    }

    [Fact]
    public void Other_processes_are_not_counted()
    {
        // dwm(pid 2112)が 9,000MiB 持っていても、見るのは llama-server の pid だけ
        var source = new FakeSource().Then((2112, 9000), (100, 6058));
        using ProcessVramMonitor m = Monitor(source, 100);
        m.Sample();
        Assert.Equal(6058, m.AllResult().PeakMiB);
    }

    [Fact]
    public void A_restarted_server_with_a_new_pid_is_followed()
    {
        // 一時停止・再開や失敗で llama-server が起動し直すと pid が変わる。標本ごとに pid の一覧を引き直す
        int[] pids = [100];
        var source = new FakeSource().Then((100, 6000), (200, 100)).Then((100, 6000), (200, 100)).Then((200, 6300));
        using var m = new ProcessVramMonitor(source, () => pids);
        m.Sample();
        m.Sample();
        pids = [200];                       // 古いのが終わり、新しいのが立った
        m.Sample();
        ProcessVramMeasurement r = m.AllResult();
        Assert.Null(r.Problem);
        Assert.Equal(6300, r.PeakMiB);
        Assert.Equal(200, r.PeakPid);       // 新しい pid のほうがピーク
        Assert.Equal(0, r.LostSamples);
    }

    [Fact]
    public void Overlapping_old_and_new_servers_are_not_added_together()
    {
        // 起動し直しの途中で 2 つが重なっても、合格線は「サーバ 1 つの VRAM」。足し合わせない(標本ごとに最大のプロセス)
        var source = new FakeSource().Then((100, 4000), (200, 3500));
        using ProcessVramMonitor m = Monitor(source, 100, 200);
        m.Sample();
        ProcessVramMeasurement r = m.AllResult();
        Assert.Equal(4000, r.PeakMiB);
        Assert.Equal(100, r.PeakPid);
    }

    // ---------------------------------------------------------------- 測れなかった

    [Fact]
    public void A_counter_that_cannot_be_read_makes_the_measurement_unmeasured_with_the_reason()
    {
        var source = new FakeSource().ThenFail("PdhAddEnglishCounter が失敗した(PDH のエラー 0xC0000BB8)");
        using ProcessVramMonitor m = Monitor(source, 100);
        m.Sample();
        m.Sample();
        ProcessVramMeasurement r = m.AllResult();
        Assert.Null(r.PeakMiB);
        Assert.NotNull(r.Problem);
        Assert.Contains("0xC0000BB8", r.Problem, StringComparison.Ordinal);
        Assert.Contains("2 回失敗", r.Problem, StringComparison.Ordinal);
        Assert.Equal(2, r.Errors);
        Assert.Null(r.ObservedPeakMiB);
    }

    [Fact]
    public void A_single_read_failure_amid_good_samples_makes_it_unmeasured_but_keeps_the_observed_peak_for_reference()
    {
        var source = new FakeSource().Then((100, 6000)).ThenFail("一時的な失敗").Then((100, 6100));
        using ProcessVramMonitor m = Monitor(source, 100);
        m.Sample();
        m.Sample();
        m.Sample();
        ProcessVramMeasurement r = m.AllResult();
        Assert.Null(r.PeakMiB);                      // ピークを取りこぼしたかもしれないので、値としては使わない
        Assert.NotNull(r.Problem);
        Assert.Equal(1, r.Errors);
        Assert.Equal(6100, r.ObservedPeakMiB);       // 取れた値の最大は参考に残る(使わない)
    }

    [Fact]
    public void A_server_with_no_counter_instance_is_unmeasured_not_zero()
    {
        // llama-server は動いているが、そのプロセスのインスタンスが一度も現れない(別の pid だけがある)
        var source = new FakeSource().Then((999, 500));
        using ProcessVramMonitor m = Monitor(source, 100);
        m.Sample();
        m.Sample();
        ProcessVramMeasurement r = m.AllResult();
        Assert.Null(r.PeakMiB);
        Assert.Contains("一度も取れなかった", r.Problem, StringComparison.Ordinal);
        Assert.Equal(2, r.Samples);
        Assert.Equal(0, r.ValuedSamples);
    }

    [Fact]
    public void Never_seeing_a_server_is_unmeasured_and_the_counter_is_not_even_read()
    {
        var source = new FakeSource().Then((100, 6000));
        using var m = new ProcessVramMonitor(source, () => []);
        m.Sample();
        m.Sample();
        ProcessVramMeasurement r = m.AllResult();
        Assert.Null(r.PeakMiB);
        Assert.Contains("一度も見なかった", r.Problem, StringComparison.Ordinal);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void An_instance_missing_before_the_first_value_is_normal_startup_and_after_it_is_counted_as_lost_without_failing()
    {
        // 起動の直後はまだインスタンスが無い(GPU のメモリを持つまで)。値が取れたあとに消えた分は「取りこぼし」と数える(終了の直前・直後に起きる)
        var source = new FakeSource().Then().Then((100, 2000)).Then((100, 6000)).Then();
        using ProcessVramMonitor m = Monitor(source, 100);
        for (int i = 0; i < 4; i++) m.Sample();
        ProcessVramMeasurement r = m.AllResult();
        Assert.Null(r.Problem);
        Assert.Equal(6000, r.PeakMiB);
        Assert.Equal(4, r.Samples);
        Assert.Equal(2, r.ValuedSamples);
        Assert.Equal(1, r.LostSamples);              // 最後の 1 回(値が取れたあとのインスタンスの消失)。最初の 1 回は起動の直後なので数えない
    }

    // ---------------------------------------------------------------- 複数のアダプタ

    [Fact]
    public void A_server_with_nonzero_values_on_two_adapters_is_unmeasured_and_neither_chosen_nor_added()
    {
        // 想定外: どちらかを選ぶことも、足すこともしない。理由にアダプタごとの値を並べる
        var source = new FakeSource().ThenOn((100, AdapterA, 6000), (100, AdapterB, 300));
        using ProcessVramMonitor m = Monitor(source, 100);
        m.Sample();
        ProcessVramMeasurement r = m.AllResult();
        Assert.Null(r.PeakMiB);                          // 6,000 でも 6,300 でもない
        Assert.NotNull(r.Problem);
        Assert.Contains(AdapterA, r.Problem, StringComparison.Ordinal);
        Assert.Contains(AdapterB, r.Problem, StringComparison.Ordinal);
        Assert.Contains("6000.0", r.Problem, StringComparison.Ordinal);
        Assert.Contains("300.0", r.Problem, StringComparison.Ordinal);
        Assert.Equal(1, r.AdapterConflicts);
        Assert.Null(r.ObservedPeakMiB);                  // 競合した標本は値に使わない
    }

    [Fact]
    public void A_zero_value_on_another_adapter_is_not_a_conflict_and_the_nonzero_one_is_used()
    {
        var source = new FakeSource().ThenOn((100, AdapterA, 6058), (100, AdapterB, 0));
        using ProcessVramMonitor m = Monitor(source, 100);
        m.Sample();
        ProcessVramMeasurement r = m.AllResult();
        Assert.Null(r.Problem);
        Assert.Equal(6058, r.PeakMiB);
        Assert.Equal(AdapterA, r.PeakAdapter);
        Assert.Equal(0, r.AdapterConflicts);
    }

    [Fact]
    public void Another_process_on_two_adapters_does_not_matter_only_the_servers_are_checked()
    {
        // dwm(pid 2112)は 2 つのアダプタに値を持つが、見るのは llama-server の pid だけ
        var source = new FakeSource().ThenOn((2112, AdapterA, 9000), (2112, AdapterB, 140), (100, AdapterA, 6058));
        using ProcessVramMonitor m = Monitor(source, 100);
        m.Sample();
        Assert.Null(m.AllResult().Problem);
        Assert.Equal(6058, m.AllResult().PeakMiB);
    }

    [Fact]
    public void The_reference_after_a_pause_lists_the_leftover_instances_of_the_old_servers_and_the_bench_process()
    {
        var map = new Dictionary<GpuInstance, long>
        {
            [new GpuInstance(100, AdapterA)] = 50 * MiB,                  // 終わったはずの llama-server に、インスタンスが残っている
            [new GpuInstance(999, AdapterA)] = 2300 * MiB,                // ベンチ自身(ORT の CUDA のセッション)
            [new GpuInstance(999, AdapterB)] = 4 * MiB,
            [new GpuInstance(5, AdapterA)] = 9000 * MiB,                  // 関係ないプロセス
        };
        IReadOnlyList<string> lines = ProcessVramReference.AfterPause(map, [100, 200], 999);
        Assert.Contains(lines, l => l.Contains("pid 100", StringComparison.Ordinal) && l.Contains("50.0 MiB", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("pid 200", StringComparison.Ordinal) && l.Contains("解放された", StringComparison.Ordinal));
        string self = lines.Single(l => l.StartsWith("ベンチ自身", StringComparison.Ordinal));
        Assert.Contains("pid 999", self, StringComparison.Ordinal);
        Assert.Contains("2300.0 MiB", self, StringComparison.Ordinal);
        Assert.Contains("4.0 MiB", self, StringComparison.Ordinal);       // アダプタごとに並べる(足さない)
        Assert.DoesNotContain(lines, l => l.Contains("9000", StringComparison.Ordinal));
    }

    [Fact]
    public void The_reference_after_a_pause_says_so_when_there_is_nothing_to_compare()
    {
        IReadOnlyList<string> lines = ProcessVramReference.AfterPause(new Dictionary<GpuInstance, long>(), [], 999);
        Assert.Contains(lines, l => l.Contains("一度も値が取れていない", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("ベンチ自身", StringComparison.Ordinal) && l.Contains("持っていない", StringComparison.Ordinal));
    }

    [Fact]
    public void The_valued_pids_include_servers_that_were_restarted()
    {
        int[] pids = [100];
        var source = new FakeSource().Then((100, 6000)).Then((200, 6000));
        using var m = new ProcessVramMonitor(source, () => pids);
        m.Sample();
        pids = [200];
        m.Sample();
        Assert.Equal([100, 200], m.ValuedPids().OrderBy(p => p));
    }

    // ---------------------------------------------------------------- 窓

    [Fact]
    public void A_window_starts_from_the_latest_value_and_tracks_its_own_peak()
    {
        var source = new FakeSource().Then((100, 6000)).Then((100, 6500)).Then((100, 4000)).Then((100, 4500));
        using ProcessVramMonitor m = Monitor(source, 100);
        m.Sample();
        m.Sample();                                   // 全体のピークは 6,500
        m.Sample();                                   // 4,000(直前の値)
        m.BeginWindow();                              // 窓の初期ピークは直前の値(始めた時点の使用量を取りこぼさない)
        m.Sample();                                   // 4,500
        ProcessVramMeasurement w = m.WindowResult();
        Assert.Equal(4500, w.PeakMiB);
        Assert.Equal(1, w.Samples);
        Assert.Equal(6500, m.AllResult().PeakMiB);
    }

    [Fact]
    public void A_window_without_any_sample_is_unmeasured()
    {
        var source = new FakeSource().Then((100, 6000));
        using ProcessVramMonitor m = Monitor(source, 100);
        m.Sample();
        m.BeginWindow();
        ProcessVramMeasurement w = m.WindowResult();
        Assert.Null(w.PeakMiB);                       // 窓の中で一度も測っていない(直前の値だけで合格にはしない)
        Assert.NotNull(w.Problem);
    }

    // ---------------------------------------------------------------- 事前の確認

    [Fact]
    public void The_probe_reports_a_reason_when_the_counter_cannot_be_read_and_nothing_when_it_can()
    {
        Assert.Null(ProcessVramMonitor.Probe(new FakeSource().Then((1, 1))));
        string? problem = ProcessVramMonitor.Probe(new FakeSource().ThenFail("カウンタが無い"));
        Assert.NotNull(problem);
        Assert.Contains("カウンタが無い", problem, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- big の判定

    [Theory]
    [InlineData(false, 6122, "Ok", 0)]
    [InlineData(false, 6656, "Ok", 0)]               // 足切りちょうどは内側
    [InlineData(false, 6657, "OverLimit", 6)]
    [InlineData(true, 6122, "ServerFailure", 6)]
    [InlineData(true, null, "ServerFailure", 6)]     // サーバの異常が先
    [InlineData(false, null, "Unmeasured", 7)]       // 測れなかった(GPU 全体の値には戻さない)
    public void The_big_image_verdict_and_exit_code(bool serverFailure, int? vram, string expected, int exitCode)
    {
        BigVerdict v = VlmCheckLogic.JudgeBig(serverFailure, vram, 6656);
        Assert.Equal(Enum.Parse<BigVerdict>(expected), v);
        Assert.Equal(exitCode, VlmCheckLogic.BigExitCode(v));
    }
}
