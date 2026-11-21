using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ohanami.Core.Native;

/// <summary>
/// 自己検査の判定(純粋な関数。GPU も DLL も要らない)。「ロードされているモジュールの一覧」と「アプリのディレクトリ」を受けて、
/// 外れたもの(CUDA 系の DLL がアプリのディレクトリの外から読まれている、必要な DLL がロードされていない)を返す。
/// 目的: 開発機のように PATH に CUDA Toolkit があると、同梱の漏れが PATH から黙って補われて隠れる。それを検出する。
/// </summary>
internal static class NativeSelfCheck
{
    /// <summary>
    /// 報告の一覧(<see cref="SelfCheckReport.Modules"/>)に載せる DLL の名前(先頭一致。大文字小文字を区別しない): cu*、nv*、onnxruntime*、msvcp140*、vcruntime140*。
    /// 載せるだけで、違反の判定の対象は <see cref="IsEnforced"/> が決める(nv* の多くは、ドライバが差し込む部品で、載せても違反にはしない)。
    /// msvcp140*・vcruntime140* は VC++ ランタイム(ADR 0044)。アプリのフォルダに同梱するが、違反の対象にはしない:
    /// 同じ名前の DLL が、アプリの DLL より先に別のモジュール(IME・常駐ソフトの注入など)から System32 の物としてロード済みだと、ローダーは同名のロード済みの物を使う。
    /// System32 の物で正常に動く環境を落とさず、どこから・どの版が読まれたかを、報告(パスと版)に残す。
    /// </summary>
    public static readonly string[] ReportedPrefixes = ["cu", "nv", "onnxruntime", "msvcp140", "vcruntime140"];

    /// <summary>
    /// 違反の判定の対象にする nv* の名前の先頭: <b>CUDA Toolkit の系統</b>のライブラリ。これらは、どこから読まれても(System32 でも)アプリのディレクトリの下でなければ違反。
    /// 規則は「違反にするもの」を Toolkit の系統に限り、それ以外の nv* は場所を問わず違反にしない。「nv* はすべて検査し、System32 とドライバの置き場だけ免除」では足りない:
    /// ウィンドウを出した App に、ドライバがキャプチャ用の <c>nvspcap64.dll</c> を差し込み(NVIDIA App / GeForce Experience の版によっては Program Files の下からも来る)、
    /// 差し込まれる部品は版と環境で変わり、名前も置き場も追えないため。
    /// <c>nvcudart</c> は含めない: ドライバ側の <c>nvcudart_hybrid64.dll</c>(この機の実測: DriverStore の下から読まれる)と名前が重なる。Toolkit の cudart は <c>cudart64_*</c>(cu* で検査)。
    /// </summary>
    public static readonly string[] EnforcedNvPrefixes = ["nvrtc", "nvjitlink", "nvfatbin", "nvblas", "nvtoolsext", "nvinfer", "nvonnxparser", "nvvm", "nvptxcompiler"];

    /// <summary>
    /// 画像エンコーダ(GPU)を作る前に、アプリのディレクトリにあるべきファイル(平らに置く。RuntimeIdentifier = win-x64 で ORT は平らに入る)。
    /// ORT 本体(3 個)、cuBLAS・cuBLASLt(2 個)、cuDNN(7 個。ADR 0016)の計 12 個(実行時に読まれるもの)。
    /// cudart64_13.dll は含めない: ORT 1.31.1 の CUDA EP はこの機では cudart をロードしなかった(実機で確かめた。cudart は ORT に静的に入っていると推定=未確認)。
    /// </summary>
    public static readonly string[] RequiredFiles =
    [
        "onnxruntime.dll", "onnxruntime_providers_shared.dll", "onnxruntime_providers_cuda.dll",
        "cublas64_13.dll", "cublasLt64_13.dll",
        "cudnn64_9.dll", "cudnn_engines_precompiled64_9.dll", "cudnn_engines_runtime_compiled64_9.dll",
        "cudnn_engines_tensor_ir64_9.dll", "cudnn_graph64_9.dll", "cudnn_heuristic64_9.dll", "cudnn_ops64_9.dll",
    ];

    /// <summary>
    /// GPU の画像エンコーダを動かしたあとにロードされているべき DLL(ファイル名のパターン。先頭 + 末尾の一致。* は 1 か所だけ)。
    /// ORT 本体、CUDA EP、cuBLAS、cuDNN。1 つでも無ければ、動いたように見えても環境が想定と違う。
    /// (cudart はロードされなかったので含めない。<see cref="RequiredFiles"/> の説明を参照)
    /// </summary>
    public static readonly string[] RequiredAfterRun =
    [
        "onnxruntime.dll",
        "onnxruntime_providers_cuda.dll",
        "cublas64_*.dll",
        "cudnn64_*.dll",
    ];

    public static SelfCheckReport Evaluate(
        string ortVersion,
        IReadOnlyList<LoadedModule> loadedModules,
        string nativeDirectory,
        string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ortVersion);
        ArgumentNullException.ThrowIfNull(loadedModules);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);

        string nativeDir = NormalizeDirectory(nativeDirectory);
        string systemDir = NormalizeDirectory(systemDirectory);

        var relevant = loadedModules
            .Where(m => IsReported(m.Name))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var violations = new List<string>();

        foreach (LoadedModule m in relevant)
        {
            if (!IsEnforced(m.Name)) continue; // ドライバ・オーバーレイの部品など。報告には載せるが、違反にしない
            string dir = NormalizeDirectory(Path.GetDirectoryName(m.Path) ?? "");
            if (IsUnder(dir, nativeDir)) continue;
            violations.Add($"{m.Name} がアプリのディレクトリの外から読まれている: {m.Path} (アプリのディレクトリ: {nativeDirectory})");
        }

        foreach (string pattern in RequiredAfterRun)
        {
            if (!loadedModules.Any(m => MatchesPattern(m.Name, pattern)))
                violations.Add($"{pattern} がロードされていない(同梱の漏れ、または読み込みの失敗)");
        }

        return new SelfCheckReport(violations.Count == 0, ortVersion, relevant, violations);
    }

    /// <summary>
    /// 事前の検査(純粋な関数): <see cref="RequiredFiles"/> のうち、アプリのディレクトリ(<paramref name="existingFileNames"/> = そこにあるファイル名)に無いものを返す。
    /// セッションを作る前に行う。理由: cuDNN が PATH にだけあり、アプリのディレクトリに無いと、検索パスの設定で PATH が外れたとき、
    /// ORT(cudnn_frontend)が例外でなくプロセスを異常終了させる(実機で確かめた: stderr に「Invalid handle. Cannot load symbol cudnnCreate」、終了コード 0xC0000409)。
    /// そうなる前に、足りないファイル名を挙げて <see cref="NativeRuntimeException"/> にする。
    /// </summary>
    public static IReadOnlyList<string> FindMissingFiles(IEnumerable<string> existingFileNames)
    {
        ArgumentNullException.ThrowIfNull(existingFileNames);
        var present = new HashSet<string>(existingFileNames, StringComparer.OrdinalIgnoreCase);
        return RequiredFiles.Where(f => !present.Contains(f)).ToList();
    }

    /// <summary>報告の一覧に載せる名前か(cu* / nv* / onnxruntime* / msvcp140* / vcruntime140*)。</summary>
    public static bool IsReported(string moduleName) => StartsWithAny(moduleName, ReportedPrefixes);

    /// <summary>違反の判定の対象か: cu*、onnxruntime*、CUDA Toolkit 系統の nv*(<see cref="EnforcedNvPrefixes"/>)。</summary>
    public static bool IsEnforced(string moduleName) =>
        StartsWithAny(moduleName, ["cu", "onnxruntime"]) || StartsWithAny(moduleName, EnforcedNvPrefixes);

    private static bool StartsWithAny(string name, string[] prefixes)
    {
        foreach (string prefix in prefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>"cudnn64_*.dll" のような、* が高々 1 つのパターンとの一致(大文字小文字を区別しない)。</summary>
    internal static bool MatchesPattern(string name, string pattern)
    {
        int star = pattern.IndexOf('*', StringComparison.Ordinal);
        if (star < 0) return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        if (pattern.IndexOf('*', star + 1) >= 0) throw new ArgumentException($"* は 1 つまで: {pattern}", nameof(pattern));
        string head = pattern[..star], tail = pattern[(star + 1)..];
        return name.Length >= head.Length + tail.Length
            && name.StartsWith(head, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>フルパスにし、末尾の区切りを 1 つにそろえる(「配下か」の判定を、前方一致の取り違え(C:\app と C:\app2)なしで行うため)。</summary>
    internal static string NormalizeDirectory(string dir)
    {
        if (dir.Length == 0) return "";
        string full = Path.GetFullPath(dir);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private static bool IsUnder(string dir, string root) =>
        dir.Length > 0 && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase);
}
