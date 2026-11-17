using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace S4Wpf;

/// <summary>要件9: 「エクスプローラーで表示」と「クリップボードに画像をコピー」。結果は文字列で返す(画面の状態表示用)。</summary>
public static class Actions
{
    // ---------------------------------------------------------------- エクスプローラーで表示

    /// <summary>方式1: explorer.exe /select,"パス"(引用符で囲む)。</summary>
    public static string RevealWithProcessStart(string path)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false, Arguments = "/select,\"" + path + "\"" };
        Process.Start(psi);
        return $"explorer.exe /select を起動した({sw.ElapsedMilliseconds}ms)。選択されたか目で確認すること";
    }

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

    /// <summary>方式2: シェル API(SHOpenFolderAndSelectItems)。パスの引用の問題を避けられる。</summary>
    public static string RevealWithShellApi(string path)
    {
        var sw = Stopwatch.StartNew();
        IntPtr pidlFull = IntPtr.Zero, pidlFolder = IntPtr.Zero;
        try
        {
            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidlFull, 0, out _);
            if (hr != 0) return $"SHParseDisplayName が失敗(HRESULT 0x{hr:X8})";
            pidlFolder = ILClone(pidlFull);
            ILRemoveLastID(pidlFolder);
            IntPtr child = ILFindLastID(pidlFull);
            hr = SHOpenFolderAndSelectItems(pidlFolder, 1, new[] { child }, 0);
            if (hr != 0) return $"SHOpenFolderAndSelectItems が失敗(HRESULT 0x{hr:X8})";
            return $"シェル API で表示した({sw.ElapsedMilliseconds}ms)。選択されたか目で確認すること";
        }
        finally
        {
            if (pidlFolder != IntPtr.Zero) ILFree(pidlFolder);
            if (pidlFull != IntPtr.Zero) ILFree(pidlFull);
        }
    }

    // ---------------------------------------------------------------- クリップボード

    /// <summary>原寸の画像を別スレッドでデコードして、UI スレッドで Clipboard.SetImage に渡す。</summary>
    public static async Task<string> CopyImageWithSetImageAsync(string path)
    {
        var sw = Stopwatch.StartNew();
        BitmapSource bmp = await Task.Run(() => DecodeFull(path));
        long decodeMs = sw.ElapsedMilliseconds;
        RetryClipboard(() => Clipboard.SetImage(bmp));
        return $"Clipboard.SetImage した(原寸 {bmp.PixelWidth}x{bmp.PixelHeight}、デコード {decodeMs}ms、合計 {sw.ElapsedMilliseconds}ms)";
    }

    /// <summary>DIB 系の形式(SetImage 相当)に加えて、"PNG" 形式も同時に入れる。アルファを保てるアプリ向け。</summary>
    public static async Task<string> CopyImageWithDibAndPngAsync(string path)
    {
        var sw = Stopwatch.StartNew();
        (BitmapSource bmp, byte[] png) = await Task.Run(() =>
        {
            BitmapSource b = DecodeFull(path);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(b));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (b, ms.ToArray());
        });
        long prepMs = sw.ElapsedMilliseconds;
        var data = new DataObject();
        data.SetImage(bmp);
        data.SetData("PNG", new MemoryStream(png));
        RetryClipboard(() => Clipboard.SetDataObject(data, true));
        return $"DIB+PNG 形式で入れた(原寸 {bmp.PixelWidth}x{bmp.PixelHeight}、PNG {png.Length / 1024}KB、準備 {prepMs}ms、合計 {sw.ElapsedMilliseconds}ms)";
    }

    /// <summary>ファイルとしてコピー(エクスプローラーや、ファイルを受けるアプリへ貼れる)。</summary>
    public static string CopyFile(string[] paths)
    {
        var sc = new StringCollection();
        foreach (string p in paths) sc.Add(p);
        RetryClipboard(() => Clipboard.SetFileDropList(sc));
        return $"Clipboard.SetFileDropList した({paths.Length} ファイル)";
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

    /// <summary>クリップボードは他のアプリが開いていると CLIPBRD_E_CANT_OPEN で失敗しうるので、少し待って再試行する。</summary>
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
