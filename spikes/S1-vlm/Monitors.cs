using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace S1Vlm;

/// <summary>
/// GPU 全体の memory.used(MiB)を 200ms 間隔で取る。`nvidia-smi -lms 200` を常駐させて出力を読む方式
/// (サンプリングごとにプロセスを起動しない)。WDDM ではプロセスごとの値が取れないことが多いので GPU 全体の値を使う。
/// 窓(Begin/End)ごとのピークと、起動以来のピークを持つ。
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    private readonly Process _proc;
    private readonly Thread _reader;
    private readonly object _lock = new();
    private int _current = -1;
    private int _windowPeak = -1;
    private int _allPeak = -1;
    private string? _error;
    private volatile bool _disposed;

    public GpuMonitor(int gpuIndex)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = $"-i {gpuIndex.ToString(CultureInfo.InvariantCulture)} --query-gpu=memory.used --format=csv,noheader,nounits -lms 200",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("nvidia-smi を起動できなかった");
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "nvidia-smi-reader" };
        _reader.Start();
        // 最初の値が来るまで待つ(来なければ nvidia-smi が使えない=測定できないので失敗)
        var sw = Stopwatch.StartNew();
        while (true)
        {
            lock (_lock)
            {
                if (_error is not null) throw new InvalidOperationException("nvidia-smi: " + _error);
                if (_current >= 0) break;
            }
            if (sw.ElapsedMilliseconds > 10000) throw new InvalidOperationException("nvidia-smi から10秒経っても値が来ない");
            Thread.Sleep(20);
        }
    }

    private void ReadLoop()
    {
        try
        {
            string? line;
            while ((line = _proc.StandardOutput.ReadLine()) != null)
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mib))
                {
                    lock (_lock) _error = $"想定外の出力: '{t}'";
                    return;
                }
                lock (_lock)
                {
                    _current = mib;
                    if (mib > _windowPeak) _windowPeak = mib;
                    if (mib > _allPeak) _allPeak = mib;
                }
            }
            if (!_disposed)
            {
                string err = _proc.StandardError.ReadToEnd();
                lock (_lock) _error = "nvidia-smi が終了した: " + err.Trim();
            }
        }
        catch (Exception e)
        {
            if (!_disposed) lock (_lock) _error = e.Message;
        }
    }

    private void ThrowIfError()
    {
        lock (_lock)
        {
            if (_error is not null) throw new InvalidOperationException("VRAM 監視が止まった: " + _error);
        }
    }

    public int Current
    {
        get { ThrowIfError(); lock (_lock) return _current; }
    }

    /// <summary>起動以来のピーク(MiB)。</summary>
    public int AllPeak
    {
        get { ThrowIfError(); lock (_lock) return _allPeak; }
    }

    /// <summary>窓を開始する。直前の値を窓の初期ピークにする(開始時点の使用量を取りこぼさないため)。</summary>
    public void BeginWindow()
    {
        ThrowIfError();
        lock (_lock) _windowPeak = _current;
    }

    public int EndWindow()
    {
        ThrowIfError();
        lock (_lock) return _windowPeak;
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (!_proc.HasExited) _proc.Kill(true); } catch (InvalidOperationException) { /* 既に終了 */ }
        _proc.Dispose();
    }
}

/// <summary>
/// llama-server プロセスのプライベートバイト(Process.PrivateMemorySize64)を 200ms 間隔で取る。
/// ワーキングセットは mmap のページキャッシュで数字が狂うので主指標にしない(参考としてピークだけ別に持つ)。
/// </summary>
public sealed class PrivateMemoryMonitor : IDisposable
{
    private readonly Process _proc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private readonly object _lock = new();
    private long _current;
    private long _windowPeak;
    private long _allPeak;
    private long _windowPeakWs;
    private long _allPeakWs;
    private string? _error;

    public PrivateMemoryMonitor(Process proc)
    {
        _proc = proc;
        Sample(); // 起動直後の1回目を同期で取る(取れなければここで例外)
        _task = Task.Run(LoopAsync);
    }

    private void Sample()
    {
        _proc.Refresh();
        long priv = _proc.PrivateMemorySize64;
        long ws = _proc.WorkingSet64;
        lock (_lock)
        {
            _current = priv;
            if (priv > _windowPeak) _windowPeak = priv;
            if (priv > _allPeak) _allPeak = priv;
            if (ws > _windowPeakWs) _windowPeakWs = ws;
            if (ws > _allPeakWs) _allPeakWs = ws;
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                if (_proc.HasExited) return; // サーバが落ちたことは run 側が検出して報告する
                Sample();
            }
        }
        catch (OperationCanceledException) { /* 停止 */ }
        catch (Exception e)
        {
            lock (_lock) _error = e.Message;
        }
    }

    private void ThrowIfError()
    {
        lock (_lock)
        {
            if (_error is not null) throw new InvalidOperationException("RAM 監視が止まった: " + _error);
        }
    }

    private const double MiB = 1024.0 * 1024.0;

    public double AllPeakPrivateMiB { get { ThrowIfError(); lock (_lock) return _allPeak / MiB; } }
    public double AllPeakWorkingSetMiB { get { ThrowIfError(); lock (_lock) return _allPeakWs / MiB; } }

    public void BeginWindow()
    {
        ThrowIfError();
        lock (_lock) { _windowPeak = _current; _windowPeakWs = 0; }
    }

    public (double PrivateMiB, double WorkingSetMiB) EndWindow()
    {
        ThrowIfError();
        lock (_lock) return (_windowPeak / MiB, _windowPeakWs / MiB);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _task.Wait(2000); } catch (AggregateException) { /* 停止時の例外は無視 */ }
        _cts.Dispose();
    }
}
