using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ohanami.Bench;

/// <summary>
/// 性能カウンタの 1 つのインスタンス = (プロセス, GPU のアダプタ)。<paramref name="Adapter"/> はインスタンス名の pid の後ろ(<c>luid_0x…_0x…_phys_0</c>)。
/// アダプタごとに別のインスタンスで、同じ pid が複数のアダプタに値を持ちうる(dwm など)。アダプタをまたいで足さない。
/// </summary>
internal readonly record struct GpuInstance(int Pid, string Adapter);

/// <summary>プロセスごとの GPU メモリ(専用 = Dedicated)の読み取り。性能カウンタの <c>GPU Process Memory(pid_*)\Dedicated Usage</c> を読む(テストでは偽物に替える)。</summary>
internal interface IProcessGpuMemory
{
    /// <summary>(pid, アダプタ) → Dedicated Usage(バイト)。値を持つインスタンスだけが載る。アダプタをまたいで合計しない。読めなければ <see cref="ProcessGpuMemoryException"/>。</summary>
    IReadOnlyDictionary<GpuInstance, long> ReadDedicatedBytes();
}

/// <summary>性能カウンタを読めなかった(カウンタの種類が無い、PDH のエラー、想定外のインスタンス名)。</summary>
internal sealed class ProcessGpuMemoryException : Exception
{
    public ProcessGpuMemoryException(string message) : base(message) { }
}

/// <summary>
/// Windows の PDH(<c>pdh.dll</c>)で <c>\GPU Process Memory(*)\Dedicated Usage</c> を読む(NuGet を足さない。<c>System.Diagnostics.PerformanceCounter</c> は NuGet なので使わない)。
/// WDDM では <c>nvidia-smi</c> のプロセスごとのメモリは N/A になるが、このカウンタは WDDM のビデオメモリ管理が数えた値なので、プロセスごとに取れる。
/// インスタンス名は <c>pid_&lt;pid&gt;_luid_0x…_0x…_phys_0</c>(GPU のアダプタごとに別のインスタンス。(pid, アダプタ)ごとに返し、合計しない)。
/// 読むたびにクエリを開き直す: ワイルドカードのインスタンスは追加した時点の分が展開されるかもしれず(PDH の仕様の細部は未確認)、
/// llama-server は一時停止・再開や失敗のたびに別のプロセスとして起動し直すので、あとから現れるインスタンスを取りこぼさないため。
/// 値は「読んだ瞬間の使用量」(割合を取るカウンタではないので、1 回の収集でよい)。
/// </summary>
internal sealed class PdhProcessGpuMemory : IProcessGpuMemory
{
    public const string CounterPath = @"\GPU Process Memory(*)\Dedicated Usage";

    private const uint PdhFmtLarge = 0x00000400;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhNoData = 0x800007D5;
    private const uint PdhCstatusNoInstance = 0x800007D1;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W(x64 で 24 バイト): 名前のポインタ、CStatus、値(LONGLONG)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public IntPtr Name;
        public uint Status;
        public long Value;
    }

    public IReadOnlyDictionary<GpuInstance, long> ReadDedicatedBytes()
    {
        Check(PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query), "PdhOpenQuery");
        try
        {
            Check(PdhAddEnglishCounterW(query, CounterPath, IntPtr.Zero, out IntPtr counter), "PdhAddEnglishCounter(" + CounterPath + ")");
            uint rc = PdhCollectQueryData(query);
            if (rc == PdhNoData) return new Dictionary<GpuInstance, long>();
            Check(rc, "PdhCollectQueryData");

            uint size = 0, count = 0;
            rc = PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref size, ref count, IntPtr.Zero);
            if (rc == PdhNoData || rc == PdhCstatusNoInstance || (rc == 0 && count == 0)) return new Dictionary<GpuInstance, long>();
            if (rc != PdhMoreData) Check(rc, "PdhGetFormattedCounterArray(大きさの問い合わせ)");

            // 問い合わせと取得のあいだにインスタンスが増えると PDH_MORE_DATA が返るので、数回やり直す
            for (int attempt = 0; attempt < 5; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
                try
                {
                    rc = PdhGetFormattedCounterArrayW(counter, PdhFmtLarge, ref size, ref count, buffer);
                    if (rc == PdhMoreData) continue;
                    if (rc == PdhNoData || rc == PdhCstatusNoInstance) return new Dictionary<GpuInstance, long>();
                    Check(rc, "PdhGetFormattedCounterArray");
                    return Parse(buffer, (int)count);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new ProcessGpuMemoryException("PdhGetFormattedCounterArray: インスタンスが増え続けて、バッファの大きさが決まらない");
        }
        finally { PdhCloseQuery(query); }
    }

    private static Dictionary<GpuInstance, long> Parse(IntPtr buffer, int count)
    {
        var result = new Dictionary<GpuInstance, long>();
        int itemSize = Marshal.SizeOf<CounterItem>();
        for (int i = 0; i < count; i++)
        {
            CounterItem item = Marshal.PtrToStructure<CounterItem>(buffer + i * itemSize);
            string name = Marshal.PtrToStringUni(item.Name) ?? "";
            // CStatus が有効(0)か新しいデータ(1)でない行(読む直前に終わったプロセスなど)は、値が無いものとして飛ばす
            if (item.Status != 0 && item.Status != 1) continue;
            if (!TryParseInstanceName(name, out GpuInstance instance)) continue;
            result[instance] = item.Value;   // 同じ名前は 2 つ無いはず(あれば後のもの)。足さない
        }
        return result;
    }

    /// <summary>インスタンス名 <c>pid_1234_luid_0x…_phys_0</c> から (pid, アダプタ) を取り出す。<c>pid_</c> で始まらない名前は false(このカウンタには無いはずだが、無視する)。始まるのに読めなければ例外(形式が変わった)。</summary>
    internal static bool TryParseInstanceName(string name, out GpuInstance instance)
    {
        instance = default;
        const string prefix = "pid_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int end = name.IndexOf('_', prefix.Length);
        string digits = end < 0 ? name[prefix.Length..] : name[prefix.Length..end];
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            throw new ProcessGpuMemoryException($"想定外のインスタンス名: '{name}'(pid_<数>_… の形のはず)");
        instance = new GpuInstance(pid, end < 0 ? "" : name[(end + 1)..]);
        return true;
    }

    private static void Check(uint status, string what)
    {
        if (status != 0) throw new ProcessGpuMemoryException($"{what} が失敗した(PDH のエラー 0x{status:X8})");
    }
}

/// <summary>ある時間のあいだの、プロセスごとの VRAM(Dedicated Usage)の測定の結果。</summary>
/// <param name="PeakMiB">ピーク(MiB。切り上げ)。<see cref="Problem"/> があれば null(使わない)。</param>
/// <param name="Problem">測れなかった理由(測れていれば null)。</param>
/// <param name="ObservedPeakMiB">取れた値のうちの最大(<see cref="Problem"/> があっても、参考に。何も取れなければ null)。</param>
/// <param name="PeakPid">ピークのときのプロセス。</param>
/// <param name="Samples">llama-server が動いていた標本の数。</param>
/// <param name="ValuedSamples">そのうち、値が取れた標本の数。</param>
/// <param name="LostSamples">一度値が取れたプロセスなのに、インスタンスが見つからなかった標本の数(終了の直前・直後に起きうる)。</param>
/// <param name="Errors">カウンタの読み取りが例外になった回数。</param>
/// <param name="AdapterConflicts">1 つのプロセスが 2 つ以上のアダプタで 0 でない値を持っていた標本の数(想定外。どちらかを選ぶことも足すこともしない)。</param>
/// <param name="PeakAdapter">ピークのときのアダプタ。</param>
internal sealed record ProcessVramMeasurement(int? PeakMiB, string? Problem, int? ObservedPeakMiB, int PeakPid, int Samples, int ValuedSamples, int LostSamples, int Errors, int AdapterConflicts = 0, string PeakAdapter = "")
{
    public string Describe() =>
        $"標本 {Samples}(値が取れた {ValuedSamples}、一度取れたあとの取りこぼし {LostSamples}、読み取りの失敗 {Errors}、アダプタの競合 {AdapterConflicts})"
        + (PeakPid != 0 ? $"、ピークの pid {PeakPid}" + (PeakAdapter.Length > 0 ? $"({PeakAdapter})" : "") : "");
}

/// <summary>
/// llama-server のプロセスごとの VRAM を一定の間隔(既定 200ms)で読み、ピークを持つ(<see cref="GpuMonitor"/> と同じ窓の形)。
/// どのプロセスを見るかは <c>serverPids</c> が毎回返す: llama-server は一時停止・再開や失敗で起動し直して pid が変わるので、固定の pid ではなく、標本ごとに引く。
/// 標本ごとに、見ているプロセスのうち最大の値を取る(起動し直しの途中で古いものと新しいものが重なっても、足さない。合格線は「1 枚の GPU の上のサーバ 1 つの VRAM」)。
/// 1 つのプロセスが 2 つ以上のアダプタ(luid)で 0 でない値を持っていたら想定外で、**どちらを選ぶことも足すこともせず**、測れなかったことにする(理由にアダプタごとの値を並べる)。
/// <b>黙って別の値に戻さない</b>: カウンタが読めない・値が一度も取れないときは、<see cref="ProcessVramMeasurement.Problem"/> に理由が入り、ピークは null。
/// </summary>
internal sealed class ProcessVramMonitor : IDisposable
{
    private const double MiB = 1024.0 * 1024.0;

    private sealed class Tally
    {
        public long PeakBytes = -1;
        public int PeakPid;
        public int Samples, Valued, Lost, Errors, Conflicts;
        public string? FirstError, FirstConflict, PeakAdapter;
    }

    private readonly IProcessGpuMemory _source;
    private readonly Func<IReadOnlyCollection<int>> _serverPids;
    private readonly object _lock = new();
    private readonly HashSet<int> _valuedPids = [];
    private Tally _all = new();
    private Tally _window = new();
    private long _currentBytes = -1;
    private int _currentPid;
    private string _currentAdapter = "";
    private Thread? _thread;
    private readonly ManualResetEventSlim _stop = new(false);

    public ProcessVramMonitor(IProcessGpuMemory source, Func<IReadOnlyCollection<int>> serverPids)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _serverPids = serverPids ?? throw new ArgumentNullException(nameof(serverPids));
    }

    /// <summary>標本を 1 回取る(背景のスレッドが <paramref name="interval"/> ごとに呼ぶ。テストは直接呼ぶ)。見るプロセスが無ければ、カウンタは読まない。</summary>
    public void Sample()
    {
        IReadOnlyCollection<int> pids = _serverPids();
        if (pids.Count == 0)
        {
            lock (_lock) { _currentBytes = -1; _currentPid = 0; _currentAdapter = ""; }
            return;
        }
        IReadOnlyDictionary<GpuInstance, long>? map = null;
        string? error = null;
        try { map = _source.ReadDedicatedBytes(); }
        catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }   // PDH のエラー・DLL が無い、など。すべて「読めなかった」
        lock (_lock)
        {
            Apply(_all, pids, map, error);
            Apply(_window, pids, map, error);
        }
    }

    // 呼び出し側で _lock を取っている
    private void Apply(Tally t, IReadOnlyCollection<int> pids, IReadOnlyDictionary<GpuInstance, long>? map, string? error)
    {
        t.Samples++;
        if (map is null)
        {
            t.Errors++;
            t.FirstError ??= error;
            _currentBytes = -1;
            _currentPid = 0;
            _currentAdapter = "";
            return;
        }
        long best = -1;
        int bestPid = 0;
        string bestAdapter = "";
        bool conflict = false;
        foreach (int pid in pids)
        {
            var own = map.Where(kv => kv.Key.Pid == pid).ToList();
            if (own.Count == 0)
            {
                if (_valuedPids.Contains(pid)) t.Lost++;
                continue;
            }
            _valuedPids.Add(pid);
            List<KeyValuePair<GpuInstance, long>> nonzero = own.Where(kv => kv.Value > 0).ToList();
            if (nonzero.Count >= 2)
            {
                // 想定外: どちらを選ぶことも、足すこともしない
                conflict = true;
                t.FirstConflict ??= $"pid {pid} が {nonzero.Count} つのアダプタで 0 でない値を持っている: "
                    + string.Join("、", own.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key.Adapter} = {kv.Value / MiB:F1} MiB"));
                continue;
            }
            KeyValuePair<GpuInstance, long> pick = nonzero.Count == 1 ? nonzero[0] : own.OrderBy(kv => kv.Key.Adapter, StringComparer.Ordinal).First();
            if (pick.Value > best) { best = pick.Value; bestPid = pid; bestAdapter = pick.Key.Adapter; }
        }
        if (conflict) t.Conflicts++;
        _currentBytes = best;
        _currentPid = bestPid;
        _currentAdapter = bestAdapter;
        if (best < 0) return;
        t.Valued++;
        if (best > t.PeakBytes) { t.PeakBytes = best; t.PeakPid = bestPid; t.PeakAdapter = bestAdapter; }
    }

    /// <summary>窓を始める。直前の値を窓の初期ピークにする(始めた時点の使用量を取りこぼさないため)。</summary>
    public void BeginWindow()
    {
        lock (_lock)
        {
            _window = new Tally { PeakBytes = _currentBytes, PeakPid = _currentPid, PeakAdapter = _currentAdapter };
        }
    }

    /// <summary>窓のあいだの測定(窓はそのまま続く)。</summary>
    public ProcessVramMeasurement WindowResult()
    {
        lock (_lock) return Summarize(_window);
    }

    /// <summary>起動以来の測定。</summary>
    public ProcessVramMeasurement AllResult()
    {
        lock (_lock) return Summarize(_all);
    }

    private static ProcessVramMeasurement Summarize(Tally t)
    {
        int? observed = t.PeakBytes >= 0 ? (int)Math.Ceiling(t.PeakBytes / MiB) : null;
        string? problem = null;
        if (t.Errors > 0)
            problem = $"性能カウンタの読み取りが {t.Errors} 回失敗した(最初: {t.FirstError})。ピークを取りこぼしたかもしれない";
        else if (t.Conflicts > 0)
            problem = $"1 つのプロセスが 2 つ以上のアダプタで 0 でない値を持っていた({t.Conflicts} 回)。想定外なので、どれも選ばず、足さない。最初の例: {t.FirstConflict}";
        else if (t.Samples == 0)
            problem = "llama-server が動いているのを一度も見なかった(測るものが無かった)";
        else if (t.Valued == 0)
            problem = $"llama-server が動いている間({t.Samples} 回の標本)、プロセスごとの値(GPU Process Memory の Dedicated Usage)が一度も取れなかった(そのプロセスのインスタンスが無い)";
        return new ProcessVramMeasurement(problem is null ? observed : null, problem, observed, t.PeakPid, t.Samples, t.Valued, t.Lost, t.Errors, t.Conflicts, t.PeakAdapter ?? "");
    }

    /// <summary>背景のスレッドで <see cref="Sample"/> を繰り返す。</summary>
    public void StartBackground(TimeSpan interval)
    {
        if (_thread is not null) throw new InvalidOperationException("もう始めている");
        _thread = new Thread(() =>
        {
            while (!_stop.IsSet)
            {
                Sample();
                _stop.Wait(interval);
            }
        })
        { IsBackground = true, Name = "process-vram-sampler" };
        _thread.Start();
    }

    /// <summary>これまでに値が取れた pid(llama-server が起動し直した分も含む。一時停止のあとの参考表示に使う)。</summary>
    public IReadOnlyCollection<int> ValuedPids()
    {
        lock (_lock) return [.. _valuedPids];
    }

    /// <summary>カウンタが読めるか(プロセスは問わない)を、始める前に確かめる。読めなければ理由、読めれば null。</summary>
    public static string? Probe(IProcessGpuMemory source)
    {
        try
        {
            source.ReadDedicatedBytes();
            return null;
        }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(2000);
        _stop.Dispose();
    }
}

/// <summary>いま動いている llama-server の pid(この実行より前から動いていたものを除く)。</summary>
internal static class LlamaServerProcesses
{
    public static HashSet<int> Running()
    {
        var set = new HashSet<int>();
        foreach (Process p in Process.GetProcessesByName("llama-server"))
        {
            set.Add(p.Id);
            p.Dispose();
        }
        return set;
    }

    public static IReadOnlyCollection<int> Except(IReadOnlySet<int> ignore)
    {
        var list = new List<int>();
        foreach (int id in Running()) if (!ignore.Contains(id)) list.Add(id);
        return list;
    }
}

/// <summary>
/// 一時停止の確認 B(合否は GPU 全体 − 起動前 ≤ 700MiB のまま)の参考の表示。合否には使わない(どの値なら解放と言えるかの線は、データを見てから決める)。
/// (a) llama-server だった pid のインスタンスが残っていないか、(b) ベンチ自身のプロセス(ORT の CUDA のセッションを持つ)の Dedicated Usage。
/// </summary>
internal static class ProcessVramReference
{
    public static IReadOnlyList<string> AfterPause(IReadOnlyDictionary<GpuInstance, long> map, IReadOnlyCollection<int> serverPids, int selfPid)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(serverPids);
        var lines = new List<string>();
        foreach (int pid in serverPids.OrderBy(p => p))
            lines.Add($"llama-server だった pid {pid}: " + Instances(map, pid, "インスタンスは無い(解放された)"));
        if (serverPids.Count == 0) lines.Add("llama-server だった pid: 一度も値が取れていない(比べるものが無い)");
        lines.Add($"ベンチ自身(pid {selfPid})の Dedicated Usage: " + Instances(map, selfPid, "インスタンスは無い(GPU のメモリを持っていない)"));
        return lines;
    }

    private static string Instances(IReadOnlyDictionary<GpuInstance, long> map, int pid, string none)
    {
        List<KeyValuePair<GpuInstance, long>> own = map.Where(kv => kv.Key.Pid == pid).OrderBy(kv => kv.Key.Adapter, StringComparer.Ordinal).ToList();
        return own.Count == 0 ? none : string.Join("、", own.Select(kv => $"{kv.Key.Adapter} = {kv.Value / 1024.0 / 1024.0:F1} MiB"));
    }
}
