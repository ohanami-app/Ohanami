using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;

namespace Ohanami.Core.Native;

/// <summary>
/// ネイティブの失敗(ORT・llama-server)のうち、利用者に次の行動を言えるものを見分けて、平らな文にする。
/// 見分けるのは、本当にその場面だけ(広く拾わない): 当てはまらない失敗は、元の例外のまま通す。
/// <list type="bullet">
/// <item><b>GPU を使えない</b>: ORT の CUDA EP は、オプションを解く最初の段階で <c>cudaGetDeviceCount</c> を呼ぶ。GPU・ドライバが無いと、ここで失敗し、メッセージに
///   <c>expr=cudaGetDeviceCount</c> が入る(GPU もドライバも無い環境で確かめた。CUDA の失敗番号は 801)。llama-server は、CUDA を初期化できないと CPU に落ちて動き続けるので、
///   出力の <c>ggml_cuda_init: failed to initialize CUDA</c> の行で見分ける(<see cref="LlamaCudaInitFailedMarker"/>)。</item>
/// <item><b>アプリのフォルダの DLL が読めない</b>: ORT は、ネイティブの DLL が読めないと <see cref="TypeInitializationException"/> の中に <see cref="DllNotFoundException"/> が入る(外側の文では、どの DLL か分からない)。
///   llama-server は、DLL が読めないと、何も出さずに終了コード 0xC0000135(STATUS_DLL_NOT_FOUND)で終わる。</item>
/// </list>
/// 詳細(CUDA の失敗番号・元の例外)は、内側の例外に残る(ログに出る)。文は画面にそのまま出る(<c>UiText.Plain</c> は、この文を変えない)。
/// </summary>
internal static class NativeFailure
{
    // 文の並びは「すべきことを先に、細部を括弧の後ろに」。画面は長い文を省略記号で切る(状態バー・左パネルの 1 行)ので、切れても、すべきことが見えるように

    /// <summary>CUDA を使えないときの文(GPU・ドライバのどちらが原因かは、ここでは言い切れない)。</summary>
    public const string GpuUnavailableMessage =
        "NVIDIA の GPU を使えません。NVIDIA のドライバと GPU を確かめてください (CUDA を初期化できませんでした)";

    /// <summary>llama-server が CUDA を初期化できなかったとき、起動の出力に出る行の一部。</summary>
    public const string LlamaCudaInitFailedMarker = "ggml_cuda_init: failed to initialize CUDA";

    /// <summary>ORT の CUDA EP が、GPU の台数を数えられなかったときの、メッセージの一部(失敗した式)。</summary>
    private const string OrtDeviceCountExpr = "expr=cudaGetDeviceCount";

    /// <summary>DLL が読めない終了コード: STATUS_DLL_NOT_FOUND(0xC0000135)。</summary>
    public const int StatusDllNotFound = unchecked((int)0xC0000135);

    private const string FilesMissingAction = "アプリのファイルが足りないか、壊れています。zip を展開し直してください";

    private static readonly Regex QuotedName = new("'([^']+)'", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>アプリのフォルダに同梱している VC++ ランタイムの DLL(ADR 0044)。</summary>
    public static readonly string[] BundledVcRuntimeFiles =
        ["msvcp140.dll", "msvcp140_1.dll", "msvcp140_atomic_wait.dll", "vcruntime140.dll", "vcruntime140_1.dll"];

    /// <summary>ORT が CUDA を使えず失敗したか(GPU の台数を数える段階の失敗だけ)。</summary>
    public static bool IsGpuUnavailable(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e is OnnxRuntimeException && e.Message.Contains(OrtDeviceCountExpr, StringComparison.Ordinal);
    }

    /// <summary>例外(と内側の例外)に <see cref="DllNotFoundException"/> があれば、その DLL の名前(拡張子つき)。メッセージに名前が無ければ空の文字列。無ければ null。</summary>
    public static string? FindUnloadableDll(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is not DllNotFoundException) continue;
            Match m = QuotedName.Match(x.Message);
            if (!m.Success) return "";
            string name = m.Groups[1].Value;
            return Path.HasExtension(name) ? name : name + ".dll";
        }
        return null;
    }

    /// <summary>
    /// ORT の失敗を、見分けられるものだけ <see cref="NativeRuntimeException"/>(平らな文)にする。見分けられなければ null(呼び出し側は元の例外をそのまま投げる)。
    /// </summary>
    /// <param name="nativeDirectory">アプリのネイティブ DLL のディレクトリ(足りないファイルの検査に使う。分からなければ null)。</param>
    public static NativeRuntimeException? Translate(Exception e, string? nativeDirectory)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (IsGpuUnavailable(e)) return new NativeRuntimeException(GpuUnavailableMessage, e, NativeFailureKind.GpuUnavailable);
        if (FindUnloadableDll(e) is { } dll)
            return new NativeRuntimeException(FilesMissingMessage(dll, MissingFiles(nativeDirectory)), e, NativeFailureKind.FilesMissing);
        return null;
    }

    /// <summary>
    /// アプリのフォルダの DLL が足りないか、壊れているときの文。<paramref name="unloadable"/> は読めなかった DLL の名前(分からなければ空)、
    /// <paramref name="missing"/> はフォルダに無いと確かめられたファイル。
    /// </summary>
    public static string FilesMissingMessage(string unloadable, IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(unloadable);
        ArgumentNullException.ThrowIfNull(missing);
        string what = unloadable.Length > 0 ? $"{unloadable} を読み込めませんでした" : "DLL を読み込めませんでした";
        string detail = missing.Count > 0 ? $"。足りないファイル: {string.Join("、", missing)}" : "";
        return $"{FilesMissingAction} ({what}{detail})";
    }

    /// <summary>
    /// llama-server が DLL を読めずに終わったときの文(終了コード 0xC0000135)。「llama-server」の名前は、括弧の中に書かない: 画面の言い換え(<c>UiText.Plain</c>)が、
    /// 名前を「VLM の処理プログラム (llama-server)」に替えるとき、括弧が入れ子になるため。llama-server の名前は、ログ(警告の行)に残る。
    /// </summary>
    public static string LlamaServerFilesMissingMessage(int exitCode, IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        string detail = missing.Count > 0 ? $"。足りないファイル: {string.Join("、", missing)}" : "";
        return $"{FilesMissingAction} (VLM の処理プログラムが必要な DLL を読めず、起動できませんでした。終了コード {ExitCodeText(exitCode)}{detail})";
    }

    /// <summary>終了コードを 16 進で(NTSTATUS は 16 進で調べるものなので、10 進の負数にしない)。</summary>
    public static string ExitCodeText(int exitCode) => "0x" + exitCode.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>
    /// アプリのフォルダに無いと確かめられた、必要なファイル(GPU の実行に要る DLL と、同梱の VC++ ランタイム)。ディレクトリが分からない・無いときは空
    /// (「無い」と言い切れないので、何も言わない)。
    /// </summary>
    public static IReadOnlyList<string> MissingFiles(string? nativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(nativeDirectory) || !Directory.Exists(nativeDirectory)) return [];
        var present = new HashSet<string>(
            Directory.EnumerateFiles(nativeDirectory, "*.dll", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)!,
            StringComparer.OrdinalIgnoreCase);
        return BundledVcRuntimeFiles.Concat(NativeSelfCheck.RequiredFiles).Where(f => !present.Contains(f)).ToList();
    }
}
