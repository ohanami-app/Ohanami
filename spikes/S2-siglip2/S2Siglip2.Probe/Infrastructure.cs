using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace S2Siglip2.Probe;

/// <summary>コマンドライン引数(--key value と --flag)。想定外のキーは例外にする(綴りの間違いを黙って無視しない)。</summary>
public sealed class Args
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public string Command { get; }

    public Args(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("コマンドが無い");
        Command = args[0];
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"想定外の引数: {a}");
            string key = a[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) _values[key] = args[++i];
            else _values[key] = null;
        }
    }

    public string? Get(string key)
    {
        _used.Add(key);
        return _values.TryGetValue(key, out var v) ? v ?? throw new ArgumentException($"--{key} に値が要る") : null;
    }

    public string GetOr(string key, string fallback) => Get(key) ?? fallback;

    public int GetInt(string key, int fallback) => Get(key) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : fallback;

    public bool Flag(string key)
    {
        _used.Add(key);
        if (!_values.TryGetValue(key, out var v)) return false;
        if (v is not null) throw new ArgumentException($"--{key} は値を取らないフラグ");
        return true;
    }

    /// <summary>使われなかったキーがあれば例外(綴りの間違い)。</summary>
    public void RequireAllUsed()
    {
        var unused = _values.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unused.Count > 0) throw new ArgumentException("未知のオプション: " + string.Join(", ", unused.Select(k => "--" + k)));
    }
}

/// <summary>標準出力とファイル(UTF-8、BOM なし)の両方に1行ずつ書く。1行ごとに flush する(途中で落ちても残る)。</summary>
public sealed class Report : IDisposable
{
    private readonly StreamWriter _file;
    private readonly object _lock = new();

    public Report(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _file = new StreamWriter(path, false, new UTF8Encoding(false));
    }

    public void Line(string s = "")
    {
        lock (_lock)
        {
            Console.WriteLine(s);
            _file.WriteLine(s);
            _file.Flush();
        }
    }

    public void Dispose() => _file.Dispose();
}

public static class Paths
{
    private static readonly Lazy<string> RootLazy = new(() =>
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) && Directory.Exists(Path.Combine(dir, "spikes"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("リポジトリのルートが見つからない(docs/ と spikes/ があるディレクトリを探した)");
    });

    public static string Root => RootLazy.Value;
    /// <summary>
    /// models/ の下のモデルのディレクトリ名。既定は Immich の ONNX。自前変換の ONNX(tools/convert-siglip2/)で測るときは、
    /// 環境変数 S2_MODEL_REPO=ViT-SO400M-16-SigLIP2-384__ohanami を指定する。
    /// </summary>
    public static string ModelRepo => Environment.GetEnvironmentVariable("S2_MODEL_REPO") is { Length: > 0 } v ? v : "ViT-SO400M-16-SigLIP2-384__webli";

    public static string Models => Path.Combine(Root, "models", ModelRepo);

    /// <summary>textual/ の中の外部データのファイルか。Immich の ONNX は断片(onnx__*, text.*)、自前変換の ONNX は 1 ファイル(model.onnx_data)。</summary>
    public static bool IsTextualExternalData(string fileName) =>
        fileName.StartsWith("onnx__", StringComparison.Ordinal) || fileName.StartsWith("text.", StringComparison.Ordinal) || fileName == "model.onnx_data";
    public static string Reference(params string[] parts) => Path.Combine(new[] { Root, "spikes", "S2-siglip2", "reference" }.Concat(parts).ToArray());

    /// <summary>models/<repo>/<relative> が MANIFEST.tsv の sha256 と一致することを確かめて、パスを返す(違えば例外)。</summary>
    public static string VerifiedModelFile(string relative, Report? log = null)
    {
        string path = Path.Combine(Models, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new FileNotFoundException($"モデルのファイルが無い(spikes/tools/fetch_models.py で取得する): {path}");
        string manifest = Path.Combine(Root, "models", "MANIFEST.tsv");
        string? expected = null;
        foreach (string line in File.ReadLines(manifest))
        {
            string[] c = line.Split('\t');
            if (c.Length >= 6 && c[0].EndsWith("/" + ModelRepo, StringComparison.Ordinal) && c[2] == relative)
            {
                if (c[5] != "OK") throw new InvalidDataException($"MANIFEST.tsv の {relative} の sha256_match が OK でない: {c[5]}");
                expected = c[4];
            }
        }
        if (expected is null) throw new InvalidDataException($"MANIFEST.tsv に {relative} の行が無い");
        string actual = Sha256Hex(path);
        if (actual != expected) throw new InvalidDataException($"{relative} の sha256 が MANIFEST.tsv と違う(期待 {expected}、実際 {actual})");
        log?.Line($"  確認済み: {relative}(sha256 {actual[..12]}…)");
        return path;
    }

    public static string Sha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
    }
}

/// <summary>自プロセスのメモリ(プライベートバイトとワーキングセット)。</summary>
public static class ProcMem
{
    private const double MiB = 1024.0 * 1024.0;

    public readonly record struct Snapshot(double PrivateMiB, double WorkingSetMiB, double PeakWorkingSetMiB)
    {
        public override string ToString() => $"プライベート {PrivateMiB:F0} MiB / ワーキングセット {WorkingSetMiB:F0} MiB(ピーク {PeakWorkingSetMiB:F0})";
    }

    public static Snapshot Now()
    {
        using var p = Process.GetCurrentProcess();
        return new Snapshot(p.PrivateMemorySize64 / MiB, p.WorkingSet64 / MiB, p.PeakWorkingSet64 / MiB);
    }
}

/// <summary>自プロセスのメモリを一定間隔で取り、窓(Begin/End)ごとのピークを持つ。</summary>
public sealed class ProcMemSampler : IDisposable
{
    private readonly Thread _thread;
    private readonly object _lock = new();
    private volatile bool _stop;
    private double _peakPrivate, _peakWs;
    private readonly int _intervalMs;

    public ProcMemSampler(int intervalMs = 50)
    {
        _intervalMs = intervalMs;
        _thread = new Thread(Loop) { IsBackground = true, Name = "procmem-sampler" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            var s = ProcMem.Now();
            lock (_lock)
            {
                if (s.PrivateMiB > _peakPrivate) _peakPrivate = s.PrivateMiB;
                if (s.WorkingSetMiB > _peakWs) _peakWs = s.WorkingSetMiB;
            }
            Thread.Sleep(_intervalMs);
        }
    }

    public void BeginWindow()
    {
        var s = ProcMem.Now();
        lock (_lock) { _peakPrivate = s.PrivateMiB; _peakWs = s.WorkingSetMiB; }
    }

    public (double PrivateMiB, double WorkingSetMiB) EndWindow()
    {
        var s = ProcMem.Now();
        lock (_lock) return (Math.Max(_peakPrivate, s.PrivateMiB), Math.Max(_peakWs, s.WorkingSetMiB));
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
    }
}

/// <summary>
/// GPU 全体の memory.used(MiB)を 200ms 間隔で取る。`nvidia-smi -lms 200` を常駐させて出力を読む方式(S1 の Monitors.cs と同じ。
/// サンプリングごとにプロセスを起動しない)。WDDM ではプロセスごとの値が取れないことが多いので GPU 全体の値を使う。
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

    public int Current { get { ThrowIfError(); lock (_lock) return _current; } }
    public int AllPeak { get { ThrowIfError(); lock (_lock) return _allPeak; } }

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
/// プロセス別の専用 VRAM(MiB)。`typeperf "\GPU Process Memory(pid_&lt;pid&gt;*)\Dedicated Usage" -sc 1` を1回呼んで読む
/// (GPU 全体の値に他のプロセスの変動が混ざるため、自プロセスだけの値を併記する)。取れなければ null と理由を返す。
/// </summary>
public static class GpuProcessMemory
{
    public static (double? MiB, string? Why) Read(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "typeperf",
                Arguments = $"\"\\GPU Process Memory(pid_{pid.ToString(CultureInfo.InvariantCulture)}*)\\Dedicated Usage\" -sc 1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("typeperf を起動できなかった");
            string output = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            if (string.IsNullOrWhiteSpace(output)) return (null, $"typeperf の標準出力が空(終了コード {p.ExitCode}、標準エラー: {err.Trim()})");
            // 出力: 1行目 = 列名(カウンタのパス)、2行目 = 値("時刻","値1","値2",...)
            string[] lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            if (lines.Length < 2 || !lines[0].StartsWith('"')) return (null, $"typeperf の出力にカウンタが無い(GPU を使い始める前など)。行数 {lines.Length}、先頭 {string.Join(" | ", lines.Take(3).Select(l => l.Length > 80 ? l[..80] : l))}");
            string[] header = SplitCsv(lines[0]);
            string[] values = SplitCsv(lines[1]);
            double sum = 0;
            int n = 0;
            for (int i = 1; i < values.Length && i < header.Length; i++)
            {
                if (double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double bytes))
                {
                    sum += bytes;
                    n++;
                }
            }
            if (n == 0) return (null, "typeperf の値を読めなかった: " + lines[1]);
            return (sum / (1024.0 * 1024.0), null);
        }
        catch (Exception e)
        {
            return (null, "typeperf が使えない: " + e.Message);
        }
    }

    private static string[] SplitCsv(string line) => line.Trim().Trim('"').Split("\",\"");
}
