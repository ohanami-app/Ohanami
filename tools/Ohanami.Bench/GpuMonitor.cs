using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Ohanami.Bench;

/// <summary>
/// GPU 全体の <c>memory.used</c>(MiB)を 200ms 間隔で取る(VLM の評価(spikes/S1-vlm)の <c>Monitors.GpuMonitor</c> と同じ方式: <c>nvidia-smi -lms 200</c> を常駐させて出力を読む。
/// WDDM ではプロセスごとの値が取れないことが多いので、GPU 全体の値)。窓(<see cref="BeginWindow"/> / <see cref="EndWindow"/>)ごとのピークと、起動以来のピークを持つ。
/// サンプリング間隔より短いピークは取れない。
/// </summary>
internal sealed class GpuMonitor : IDisposable
{
    private readonly Process _proc;
    private readonly Thread _reader;
    private readonly object _lock = new();
    private int _current = -1;
    private int _windowPeak = -1;
    private int _allPeak = -1;
    private string? _error;
    private volatile bool _disposed;

    public GpuMonitor(int gpuIndex = 0)
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
        var sw = Stopwatch.StartNew();
        while (true)
        {
            lock (_lock)
            {
                if (_error is not null) throw new InvalidOperationException("nvidia-smi: " + _error);
                if (_current >= 0) break;
            }
            if (sw.ElapsedMilliseconds > 10000) throw new InvalidOperationException("nvidia-smi から 10 秒たっても値が来ない");
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

    /// <summary>いまの値(MiB)。</summary>
    public int Current
    {
        get { ThrowIfError(); lock (_lock) return _current; }
    }

    /// <summary>起動以来のピーク(MiB)。</summary>
    public int AllPeak
    {
        get { ThrowIfError(); lock (_lock) return _allPeak; }
    }

    /// <summary>窓を始める。直前の値を窓の初期ピークにする(始めた時点の使用量を取りこぼさないため)。</summary>
    public void BeginWindow()
    {
        ThrowIfError();
        lock (_lock) _windowPeak = _current;
    }

    /// <summary>窓のピーク(MiB)。</summary>
    public int EndWindow()
    {
        ThrowIfError();
        lock (_lock) return _windowPeak;
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (!_proc.HasExited) _proc.Kill(true); } catch (InvalidOperationException) { /* もう終わっている */ }
        _proc.Dispose();
    }
}
