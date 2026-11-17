using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Ohanami.Core.Native;

/// <summary>
/// ネイティブ DLL(ORT、CUDA、cuDNN)を「アプリのディレクトリから」読むための、プロセスの設定と、ロード済みモジュールの取得
/// (ADR 0015)。判定そのものは <see cref="NativeSelfCheck"/>(純粋な関数)。
/// </summary>
internal static class NativeRuntime
{
    // LoadLibrary の検索に使うディレクトリの指定(winbase.h)
    private const uint LoadLibrarySearchApplicationDir = 0x00000200;
    private const uint LoadLibrarySearchUserDirs = 0x00000400;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    /// <summary>アプリのディレクトリ + <see cref="AddDllDirectory"/> で足したディレクトリ + System32 だけを探す。PATH と、カレントディレクトリは探さない。</summary>
    internal const uint SearchFlags = LoadLibrarySearchApplicationDir | LoadLibrarySearchUserDirs | LoadLibrarySearchSystem32;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    private static readonly object Gate = new();
    private static string? _configuredDirectory;

    /// <summary>
    /// <see cref="Configure"/> で設定した(正規化した)ネイティブ DLL のディレクトリ。設定していなければ null。
    /// GPU のセッションを作る側(<c>Siglip2ImageEncoder</c>)は、呼び出し側の指定したディレクトリがこれと同じであることを確かめる
    /// (設定前に作ると、PATH にある DLL に黙って頼ってしまう。自己検査は事後の検出なので、事前にも止める)。
    /// </summary>
    public static string? ConfiguredDirectory
    {
        get { lock (Gate) return _configuredDirectory; }
    }

    /// <summary>
    /// <paramref name="nativeDirectory"/> を対象に <see cref="Configure"/> 済みであることを確かめる。未設定、または別のディレクトリで設定済みなら
    /// <see cref="NativeRuntimeException"/>(GPU のセッションを作る前に呼ぶ)。
    /// </summary>
    public static void RequireConfigured(string nativeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        string full = NativeSelfCheck.NormalizeDirectory(nativeDirectory);
        string? configured = ConfiguredDirectory;
        if (configured is null)
            throw new NativeRuntimeException($"NativeRuntime.Configure({nativeDirectory}) が呼ばれていない(プロセスで最初に 1 度、ORT や CUDA の DLL を読む前に呼ぶ。OhanamiCore.OpenAsync の仕事)。");
        if (!string.Equals(configured, full, StringComparison.OrdinalIgnoreCase))
            throw new NativeRuntimeException($"NativeRuntime.Configure は別のディレクトリで設定済み: 設定済み {configured}、今回の指定 {full}。");
    }

    /// <summary>
    /// このプロセスの DLL の検索パスを、アプリのディレクトリ + <paramref name="nativeDirectory"/> + System32 だけにする(PATH を検索から外す)。
    /// ORT や CUDA の DLL を読み込む前(<c>OhanamiCore.OpenAsync</c> の最初)に 1 度呼ぶ。プロセス全体の設定で、元に戻せない。
    /// 同じディレクトリで 2 回目に呼んでも何もしない(何度呼んでも同じ状態)。別のディレクトリで呼ぶと <see cref="NativeRuntimeException"/>。
    /// 失敗したら <see cref="NativeRuntimeException"/>(黙って PATH 頼みで進まない)。
    /// </summary>
    public static void Configure(string nativeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        if (!OperatingSystem.IsWindows()) throw new NativeRuntimeException("ネイティブ DLL の検索パスの設定は Windows でしか行えない。");

        string full = Path.GetFullPath(nativeDirectory);
        if (!Directory.Exists(full)) throw new NativeRuntimeException($"ネイティブ DLL のディレクトリが無い: {full}");
        string normalized = NativeSelfCheck.NormalizeDirectory(full);

        lock (Gate)
        {
            if (_configuredDirectory is not null)
            {
                if (string.Equals(_configuredDirectory, normalized, StringComparison.OrdinalIgnoreCase)) return;
                throw new NativeRuntimeException($"NativeRuntime.Configure は別のディレクトリで設定済み(プロセスで 1 回だけ): 設定済み {_configuredDirectory}、今回の指定 {normalized}。");
            }

            if (!SetDefaultDllDirectories(SearchFlags))
            {
                throw new NativeRuntimeException(
                    $"SetDefaultDllDirectories に失敗した(Win32 エラー {Marshal.GetLastWin32Error()}: {new Win32Exception(Marshal.GetLastWin32Error()).Message})。");
            }
            if (AddDllDirectory(full) == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                throw new NativeRuntimeException($"AddDllDirectory({full}) に失敗した(Win32 エラー {error}: {new Win32Exception(error).Message})。");
            }
            _configuredDirectory = normalized;
        }
    }

    /// <summary>
    /// GPU のセッションを作る前の事前検査: <see cref="NativeSelfCheck.RequiredFiles"/> がすべてアプリのディレクトリにあることを確かめる。
    /// 足りなければ、足りないファイル名を挙げて <see cref="NativeRuntimeException"/>。
    /// ORT が、cuDNN が見つからない状況によってはプロセスごと異常終了する(例外にならない)ことがあるため、ORT を呼ぶ前に止める。
    /// </summary>
    public static void VerifyRequiredFiles(string nativeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        string full = Path.GetFullPath(nativeDirectory);
        if (!Directory.Exists(full)) throw new NativeRuntimeException($"ネイティブ DLL のディレクトリが無い: {full}");

        var names = new List<string>();
        foreach (string file in Directory.EnumerateFiles(full, "*.dll", SearchOption.TopDirectoryOnly)) names.Add(Path.GetFileName(file));
        IReadOnlyList<string> missing = NativeSelfCheck.FindMissingFiles(names);
        if (missing.Count > 0)
        {
            throw new NativeRuntimeException(
                $"アプリのディレクトリ({full})に、GPU の実行に必要な DLL が無い: {string.Join(", ", missing)}。" +
                "PATH に CUDA Toolkit があると動いてしまうことがあるが、同梱の漏れとして扱う(別の PC では動かない)。");
        }
    }

    /// <summary>いまプロセスにロードされているモジュールの一覧(名前・フルパス・ファイルの版)。自己検査の入力。</summary>
    public static IReadOnlyList<LoadedModule> CaptureLoadedModules()
    {
        var list = new List<LoadedModule>();
        using Process process = Process.GetCurrentProcess();
        foreach (ProcessModule m in process.Modules)
        {
            string? version = null;
            try { version = m.FileVersionInfo.FileVersion; }
            catch (Exception e) when (e is IOException or Win32Exception) { /* 版が読めなくても、パスと名前は記録する */ }
            list.Add(new LoadedModule(m.ModuleName, m.FileName, version));
        }
        return list;
    }
}
