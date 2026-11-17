using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Ohanami.App;

/// <summary>
/// 「エクスプローラーで表示」と「クリップボードへのコピー」(Chrome・エクスプローラー・Discord・ペイントで動作を確認した方式のうち、
/// 仕組みの少ないもの。ADR 0041)。失敗は例外で返す(呼び出し側が画面の状態表示とログに出す)。
/// </summary>
internal static class ShellActions
{
    // ---------------------------------------------------------------- エクスプローラーで表示

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILClone(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern bool ILRemoveLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    /// <summary>エクスプローラーでフォルダを開いてファイルを選ぶ(シェル API。<c>explorer.exe /select</c> と違い、パスの引用符の問題がない)。</summary>
    public static void RevealInExplorer(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IntPtr pidlFull = IntPtr.Zero, pidlFolder = IntPtr.Zero;
        try
        {
            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidlFull, 0, out _);
            if (hr != 0) throw new IOException($"SHParseDisplayName が失敗(HRESULT 0x{hr:X8}): {path}");
            pidlFolder = ILClone(pidlFull);
            ILRemoveLastID(pidlFolder);
            IntPtr child = ILFindLastID(pidlFull);
            hr = SHOpenFolderAndSelectItems(pidlFolder, 1, [child], 0);
            if (hr != 0) throw new IOException($"SHOpenFolderAndSelectItems が失敗(HRESULT 0x{hr:X8}): {path}");
        }
        finally
        {
            if (pidlFolder != IntPtr.Zero) ILFree(pidlFolder);
            if (pidlFull != IntPtr.Zero) ILFree(pidlFull);
        }
    }

    /// <summary>エクスプローラーでフォルダを開く(<see cref="RevealInExplorer"/> と同じシェル API。選ぶ項目の数を 0 にすると、フォルダそのものを開く)。</summary>
    public static void OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IntPtr pidl = IntPtr.Zero;
        try
        {
            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            if (hr != 0) throw new IOException($"SHParseDisplayName が失敗(HRESULT 0x{hr:X8}): {path}");
            hr = SHOpenFolderAndSelectItems(pidl, 0, [], 0);
            if (hr != 0) throw new IOException($"SHOpenFolderAndSelectItems が失敗(HRESULT 0x{hr:X8}): {path}");
        }
        finally
        {
            if (pidl != IntPtr.Zero) ILFree(pidl);
        }
    }

    // ---------------------------------------------------------------- クリップボード

    /// <summary>原寸の画像を別スレッドでデコードして、UI スレッドで <c>Clipboard.SetImage</c> に渡す(呼ぶのは UI スレッド)。</summary>
    public static async Task CopyImageAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        BitmapSource bmp = await Task.Run(() => DecodeFull(path));
        RetryClipboard(() => Clipboard.SetImage(bmp));
    }

    /// <summary>ファイルとしてコピー(エクスプローラーや、ファイルを受けるアプリへ貼れる)。</summary>
    public static void CopyFiles(string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0) throw new ArgumentException("ファイルが無い", nameof(paths));
        var sc = new StringCollection();
        foreach (string p in paths) sc.Add(p);
        RetryClipboard(() => Clipboard.SetFileDropList(sc));
    }

    private static BitmapSource DecodeFull(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = fs;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>クリップボードは他のアプリが開いていると CLIPBRD_E_CANT_OPEN で失敗しうるので、少し待って再試行する(6 回まで)。</summary>
    private static void RetryClipboard(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException) when (attempt < 6)
            {
                Thread.Sleep(60);
            }
        }
    }
}
