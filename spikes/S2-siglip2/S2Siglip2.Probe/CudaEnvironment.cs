using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;

namespace S2Siglip2.Probe;

/// <summary>
/// CUDA EP を動かす環境の用意と点検。cuDNN なしの構成: S1 の llama.cpp の zip に入っている cudart64_13 / cublas64_13 / cublasLt64_13 を、
/// ORT のネイティブ DLL(onnxruntime_providers_cuda.dll)と同じディレクトリに置く(依存 DLL は provider の隣から探される)。
/// </summary>
public static class CudaEnvironment
{
    public static readonly string[] RequiredCudaDlls = ["cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll"];
    public static readonly string[] OrtNativeDlls = ["onnxruntime.dll", "onnxruntime_providers_cuda.dll", "onnxruntime_providers_shared.dll"];

    /// <summary>onnxruntime_providers_cuda.dll が実行時に動的ロードする(静的 import に無い)DLL。cuDNN なし構成では cudnn64_9 が無いのが想定。</summary>
    public static readonly string[] DynamicDeps = ["cudart64_13.dll", "cudnn64_9.dll", "cufft64_12.dll", "nvrtc64_130_0.dll", "nvrtc64_131_0.dll"];

    public static readonly string[] CudnnDlls =
    [
        "cudnn64_9.dll", "cudnn_adv64_9.dll", "cudnn_cnn64_9.dll", "cudnn_engines_precompiled64_9.dll", "cudnn_engines_runtime_compiled64_9.dll",
        "cudnn_engines_tensor_ir64_9.dll", "cudnn_ext64_9.dll", "cudnn_graph64_9.dll", "cudnn_heuristic64_9.dll", "cudnn_ops64_9.dll",
    ];

    /// <summary>
    /// 製品を入れる PC には CUDA Toolkit が入っていない(はず)。測定が PATH 上の CUDA Toolkit の DLL(cublas / cudart / cufft / nvrtc など)に
    /// 助けられないよう、このプロセスの PATH から、CUDA / cuDNN の DLL があるディレクトリを外す。外したものを返す(報告に書く)。
    /// </summary>
    public static List<string> SanitizePath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return [];
        var keep = new List<string>();
        var removed = new List<string>();
        string[] patterns = ["cudart64_*.dll", "cublas*64_*.dll", "cudnn*64_*.dll", "cufft64_*.dll", "nvrtc64_*.dll", "curand64_*.dll", "cusolver64_*.dll", "cusparse64_*.dll", "nvJitLink_*.dll"];
        foreach (string d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            bool hasCuda = false;
            try
            {
                if (Directory.Exists(d)) hasCuda = patterns.Any(pt => Directory.EnumerateFiles(d, pt).Any());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { /* 読めないディレクトリは残す */ }
            (hasCuda ? removed : keep).Add(d);
        }
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, keep));
        return removed;
    }

    /// <summary>onnxruntime_providers_cuda.dll があるディレクトリ(ORT のネイティブ DLL の置き場)。</summary>
    public static string FindOrtNativeDir()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            baseDir,
            Path.Combine(baseDir, "runtimes", "win-x64", "native"),
        ];
        foreach (string d in candidates)
        {
            if (File.Exists(Path.Combine(d, "onnxruntime_providers_cuda.dll"))) return d;
        }
        throw new FileNotFoundException("onnxruntime_providers_cuda.dll が見つからない(Microsoft.ML.OnnxRuntime.Gpu の restore/ビルドを確認)。探した場所: " + string.Join(", ", candidates));
    }

    /// <summary>
    /// 測定の前提を整える。CUDA の DLL を ORT の隣に置き(無ければコピー)、cuDNN が PATH などにないことを確かめる。結果は report に書く。
    /// cudnnDir を指定すると、cuDNN の DLL もコピーする(「cuDNN ありで再試行」用)。
    /// </summary>
    public static string Prepare(string cudaSourceDir, string? cudnnDir, bool allowCudnnOnPath, Report report)
    {
        string native = FindOrtNativeDir();
        report.Line($"ORT のネイティブ DLL のディレクトリ: {native}");
        foreach (string dll in OrtNativeDlls)
        {
            string p = Path.Combine(native, dll);
            if (!File.Exists(p)) throw new FileNotFoundException($"ORT のネイティブ DLL が無い: {p}");
            report.Line($"  {dll,-36} {new FileInfo(p).Length / 1048576.0,8:F1} MiB  版 {FileVersion(p)}");
        }

        report.Line($"CUDA の DLL(コピー元: {cudaSourceDir})");
        foreach (string dll in RequiredCudaDlls)
        {
            string src = Path.Combine(cudaSourceDir, dll);
            string dst = Path.Combine(native, dll);
            if (!File.Exists(dst))
            {
                if (!File.Exists(src)) throw new FileNotFoundException($"CUDA の DLL が無い(コピー元にも無い): {dll}。S1 の cudart-llama-bin-win-cuda-13.4-x64.zip を {cudaSourceDir} に展開してあるか確認");
                File.Copy(src, dst);
                report.Line($"  {dll,-20} をコピーした");
            }
            else if (File.Exists(src) && new FileInfo(src).Length != new FileInfo(dst).Length)
            {
                throw new InvalidOperationException($"{dll} がコピー元と置き場でサイズが違う({new FileInfo(src).Length} / {new FileInfo(dst).Length})。置き場のものを消してやり直す");
            }
            report.Line($"  {dll,-20} {new FileInfo(dst).Length / 1048576.0,8:F1} MiB  版 {FileVersion(dst)}  sha256 {Paths.Sha256Hex(dst)[..16]}…");
        }

        if (cudnnDir is not null)
        {
            report.Line($"cuDNN の DLL(コピー元: {cudnnDir})");
            foreach (string dll in CudnnDlls)
            {
                string src = Path.Combine(cudnnDir, dll);
                if (!File.Exists(src)) throw new FileNotFoundException($"cuDNN の DLL が無い: {src}");
                File.Copy(src, Path.Combine(native, dll), overwrite: true);
            }
            report.Line($"  {CudnnDlls.Length} 個をコピーした(cuDNN あり構成)");
        }
        else
        {
            // 前の実行で --cudnn-dir によって置いたコピーが残っていたら消す(cuDNN なしの測定が cuDNN ありにならないように)
            foreach (string dll in CudnnDlls)
            {
                string stale = Path.Combine(native, dll);
                if (File.Exists(stale))
                {
                    File.Delete(stale);
                    report.Line($"  前の実行で置いた {dll} を消した(cuDNN なしの構成にするため)");
                }
            }
            var found = FindOnSearchPath(CudnnDlls);
            if (found.Count > 0 && !allowCudnnOnPath)
            {
                throw new InvalidOperationException(
                    "cuDNN なしの構成を測るのに、cuDNN の DLL が見つかった(測定が cuDNN ありになってしまう): " + string.Join(", ", found.Take(5)) +
                    "。PATH から外すか、--allow-cudnn-on-path を付ける(その場合は報告に明記すること)");
            }
            report.Line(found.Count == 0 ? "cuDNN の DLL: 見つからない(cuDNN なしの構成)" : "cuDNN の DLL: PATH 上に見つかった(--allow-cudnn-on-path のため続行): " + string.Join(", ", found.Take(5)));
        }
        // ORT は cuDNN など一部の DLL を、DLL 名だけで LoadLibrary する(既定の検索順: アプリのディレクトリ → System32 → PATH)。
        // provider の隣(native)は、静的 import(cublas)には効くが、名前だけの LoadLibrary には効かない(実測: cuDNN が native にあっても error 2)。
        // 製品では「DLL を exe の隣に置く」か、起動時に SetDllDirectory / AddDllDirectory で native を検索順に足す。ここでは後者を使う。
        if (!SetDllDirectoryW(native)) throw new InvalidOperationException($"SetDllDirectory に失敗した: エラー {Marshal.GetLastWin32Error()}");
        report.Line($"SetDllDirectory({native}): 名前だけの LoadLibrary(cuDNN など)もここを探す");
        return native;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string path);

    private static List<string> FindOnSearchPath(string[] names)
    {
        var dirs = new List<string> { AppContext.BaseDirectory };
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null) dirs.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        var found = new List<string>();
        foreach (string d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string n in names)
            {
                try
                {
                    string p = Path.Combine(d, n);
                    if (File.Exists(p)) found.Add(p);
                }
                catch (ArgumentException) { /* PATH に不正な文字 */ }
            }
        }
        return found;
    }

    public static string FileVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrEmpty(v.FileVersion) ? "(なし)" : v.FileVersion;
        }
        catch (Exception e) { return "(取得失敗: " + e.Message + ")"; }
    }

    // ---------------------------------------------------------------- 失敗の説明(握りつぶさない)

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr hFile, uint flags);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr h);

    private const uint LoadWithAlteredSearchPath = 0x8;

    /// <summary>
    /// セッションの作成・Run が失敗したときに、どの DLL が足りないかを調べて文字列にする。
    /// provider(onnxruntime_providers_cuda.dll)を実際にロードしてみて、Windows のエラーコード(126 = 依存 DLL が見つからない、193 = 形式が違う など)と、
    /// 静的 import(PE を読む)のうち見つからないもの、動的ロードされる DLL(cuDNN など)の有無を並べる。
    /// </summary>
    public static string ExplainFailure(Exception e, string nativeDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"失敗: {e.GetType().Name}: {e.Message}");
        string provider = Path.Combine(nativeDir, "onnxruntime_providers_cuda.dll");
        var imports = PeImports.Read(provider).Where(n => !IsSystem(n)).ToList();
        sb.AppendLine("  onnxruntime_providers_cuda.dll の静的 import(起動時に必須)と動的ロードされる DLL を、ORT の隣 → PATH の順に探し、見つかったものは単体でロードを試した結果:");
        var all = imports.Select(n => (Name: n, Kind: "静的")).Concat(DynamicDeps.Select(n => (Name: n, Kind: "動的"))).ToList();
        var missing = new List<string>();
        foreach (var (name, kind) in all)
        {
            string? found = FindFirst(name, nativeDir);
            string state;
            if (found is null)
            {
                state = "【無い】";
                if (!(name == "cudnn64_9.dll" || kind == "動的" && name != "cudart64_13.dll")) missing.Add(name);
            }
            else
            {
                IntPtr h = LoadLibraryExW(found, IntPtr.Zero, LoadWithAlteredSearchPath);
                int err = h == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                if (h != IntPtr.Zero) FreeLibrary(h);
                state = h != IntPtr.Zero ? $"ロード可 ← {found}(版 {FileVersion(found)})" : $"【ロード失敗 {err}: {ErrorName(err)}】 ← {found}";
                if (h == IntPtr.Zero) missing.Add(name + $"(エラー {err})");
                if (kind == "動的")
                {
                    // ORT は動的ロードの DLL を、名前だけで LoadLibrary する(既定の検索順)。フルパスでロードできても、名前だけで見つかるとは限らない
                    IntPtr hn = LoadLibraryExW(name, IntPtr.Zero, 0);
                    int errn = hn == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                    if (hn != IntPtr.Zero) FreeLibrary(hn);
                    if (hn == IntPtr.Zero)
                    {
                        state += $"  ※ 名前だけの LoadLibrary(既定の検索順)では見つからない(エラー {errn})。exe の隣に置くか SetDllDirectory が要る";
                        missing.Add(name + "(名前だけでは見つからない)");
                    }
                }
            }
            string note = name == "cudnn64_9.dll" && found is null ? "  ※ cuDNN なし構成では想定内。失敗の原因が cuDNN なら cuDNN 9 の CUDA 13 版を足して再試行(--cudnn-dir)" : "";
            sb.AppendLine($"    [{kind}] {name,-34} {state}{note}");
        }
        sb.AppendLine(missing.Count == 0
            ? "  → 足りない・ロードできない DLL は見つからなかった(失敗は GPU・ドライバ側か、ORT の別の原因。上のメッセージと ort-verbose.log を見る)"
            : "  → 足りない・ロードできない DLL: " + string.Join(", ", missing));
        IntPtr hp = LoadLibraryExW(provider, IntPtr.Zero, LoadWithAlteredSearchPath);
        int perr = hp == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (hp != IntPtr.Zero) FreeLibrary(hp);
        sb.AppendLine(hp == IntPtr.Zero
            ? $"  (参考)onnxruntime_providers_cuda.dll 単体のロード: 失敗 {perr}({ErrorName(perr)})。ORT 本体を先に読んでいない単体ロードでは 1114 になることがあり、これだけでは原因を決めない"
            : "  (参考)onnxruntime_providers_cuda.dll 単体のロード: 成功");
        if (e.Message.Contains("cudnn", StringComparison.OrdinalIgnoreCase)) sb.AppendLine("  ※ エラーメッセージに cudnn が含まれる: cuDNN が必要な可能性が高い");
        return sb.ToString();
    }

    /// <summary>名前から DLL を探す: ORT のネイティブ DLL のディレクトリ、アプリのディレクトリ、PATH の順。</summary>
    private static string? FindFirst(string name, string nativeDir)
    {
        var dirs = new List<string> { nativeDir, AppContext.BaseDirectory };
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null) dirs.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (string d in dirs)
        {
            try
            {
                string p = Path.Combine(d, name);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { /* PATH に不正な文字 */ }
        }
        return null;
    }

    /// <summary>
    /// このプロセスが実際にロードしている CUDA 関連の DLL(どのファイルが使われたか)。PATH に別の CUDA があっても、
    /// 測定で使われたのが ORT の隣に置いた DLL かどうかがこれで分かる。
    /// </summary>
    public static List<string> LoadedCudaModules()
    {
        var list = new List<string>();
        using var p = Process.GetCurrentProcess();
        foreach (ProcessModule m in p.Modules)
        {
            string n = m.ModuleName ?? "";
            if (n.StartsWith("cu", StringComparison.OrdinalIgnoreCase) || n.StartsWith("nv", StringComparison.OrdinalIgnoreCase) || n.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase))
            {
                list.Add($"{n,-36} {m.FileVersionInfo.FileVersion,-16} {m.FileName}");
            }
        }
        return list.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsSystem(string name) =>
        name.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) || name.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("MSVCP", StringComparison.OrdinalIgnoreCase) || name.StartsWith("VCRUNTIME", StringComparison.OrdinalIgnoreCase)
        || name.Equals("nvcuda.dll", StringComparison.OrdinalIgnoreCase);

    private static string ErrorName(int code) => code switch
    {
        126 => "ERROR_MOD_NOT_FOUND: 指定したモジュール、またはその依存 DLL が見つからない",
        127 => "ERROR_PROC_NOT_FOUND: 依存 DLL に必要な関数が無い(版の不一致)",
        193 => "ERROR_BAD_EXE_FORMAT: DLL の形式が違う(32/64bit など)",
        1114 => "ERROR_DLL_INIT_FAILED: DLL の初期化に失敗",
        _ => "?",
    };
}

/// <summary>PE ファイルの静的 import(DLL 名)を読む最小の実装。</summary>
public static class PeImports
{
    public static List<string> Read(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int pe = BitConverter.ToInt32(b, 0x3c);
        if (b[pe] != 'P' || b[pe + 1] != 'E') throw new InvalidDataException("PE ヘッダでない: " + path);
        int nsec = BitConverter.ToUInt16(b, pe + 6);
        int optsz = BitConverter.ToUInt16(b, pe + 20);
        int opt = pe + 24;
        if (BitConverter.ToUInt16(b, opt) != 0x20b) throw new InvalidDataException("PE32+(64bit)でない: " + path);
        int importRva = BitConverter.ToInt32(b, opt + 112 + 8);
        int sec = opt + optsz;
        var sections = new List<(int Va, int Size, int Raw)>();
        for (int i = 0; i < nsec; i++)
        {
            int o = sec + i * 40;
            sections.Add((BitConverter.ToInt32(b, o + 12), Math.Max(BitConverter.ToInt32(b, o + 8), BitConverter.ToInt32(b, o + 16)), BitConverter.ToInt32(b, o + 20)));
        }
        int Rva2Off(int rva)
        {
            foreach (var (va, size, raw) in sections) if (rva >= va && rva < va + size) return rva - va + raw;
            throw new InvalidDataException("RVA が範囲外: " + rva);
        }
        var names = new List<string>();
        if (importRva == 0) return names;
        int off = Rva2Off(importRva);
        while (true)
        {
            int nameRva = BitConverter.ToInt32(b, off + 12);
            if (nameRva == 0) break;
            int n = Rva2Off(nameRva);
            int e = Array.IndexOf(b, (byte)0, n);
            names.Add(Encoding.ASCII.GetString(b, n, e - n));
            off += 20;
        }
        return names;
    }
}

/// <summary>
/// ORT のログを受けて、全部をファイルに書き、ノードの配置の行から「どの実行プロバイダに何ノード載ったか」を集める。
/// CPU に落ちても例外は出ず、黙って遅くなるだけなので、(c) の判定にはこのログを必ず見る。
/// </summary>
public sealed class OrtLogCapture : IDisposable
{
    private static OrtLogCapture? _instance;
    private readonly StreamWriter _file;
    private readonly object _lock = new();
    private readonly DOrtLoggingFunction _callback;
    public List<string> PlacementLines { get; } = [];
    public int LineCount { get; private set; }

    private static readonly Regex AllNodes = new(@"All nodes placed on \[(?<ep>\w+)\]\. Number of nodes: (?<n>\d+)", RegexOptions.Compiled);
    private static readonly Regex SomeNodes = new(@"Node\(s\) placed on \[(?<ep>\w+)\]\. Number of nodes: (?<n>\d+)", RegexOptions.Compiled);

    private OrtLogCapture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _file = new StreamWriter(path, false, new UTF8Encoding(false));
        _callback = OnLog;
    }

    /// <summary>OrtEnv を、このログの受け口つきで作る(最初のセッションより前に1回だけ呼ぶ)。</summary>
    public static OrtLogCapture Initialize(string logPath, bool verbose)
    {
        if (_instance is not null) throw new InvalidOperationException("OrtLogCapture は1回だけ");
        var cap = new OrtLogCapture(logPath);
        var opts = new EnvironmentCreationOptions
        {
            logId = "s2probe",
            logLevel = verbose ? OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE : OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
            loggingFunction = cap._callback,
        };
        OrtEnv.CreateInstanceWithOptions(ref opts);
        _instance = cap;
        return cap;
    }

    private void OnLog(IntPtr param, OrtLoggingLevel severity, string category, string logId, string codeLocation, string message)
    {
        lock (_lock)
        {
            LineCount++;
            _file.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{severity}] [{category}] {message}");
            if (message.Contains("placed on", StringComparison.Ordinal) || message.Contains("Node placements", StringComparison.Ordinal)
                || message.Contains("Memcpy", StringComparison.Ordinal) || message.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                || message.Contains("falling back", StringComparison.OrdinalIgnoreCase) || severity >= OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING)
            {
                PlacementLines.Add($"[{severity}] {message.Trim()}");
            }
        }
    }

    /// <summary>配置の行を集計する。ノードの配置が1つのプロバイダに揃っているか、CPU に落ちたノードがあるか。</summary>
    public (bool Found, Dictionary<string, int> NodesByProvider) Placement()
    {
        var by = new Dictionary<string, int>();
        bool found = false;
        lock (_lock)
        {
            foreach (string line in PlacementLines)
            {
                var m = AllNodes.Match(line);
                if (m.Success) { by[m.Groups["ep"].Value] = by.GetValueOrDefault(m.Groups["ep"].Value) + int.Parse(m.Groups["n"].Value); found = true; continue; }
                m = SomeNodes.Match(line);
                if (m.Success) { by[m.Groups["ep"].Value] = by.GetValueOrDefault(m.Groups["ep"].Value) + int.Parse(m.Groups["n"].Value); found = true; }
            }
        }
        return (found, by);
    }

    /// <summary>次のセッション用に、配置の行の集計をリセットする。</summary>
    public void ResetPlacement()
    {
        lock (_lock) PlacementLines.Clear();
    }

    public void Dispose()
    {
        lock (_lock) _file.Dispose();
    }
}
