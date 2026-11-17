using System;
using System.Collections.Generic;
using System.Windows;

namespace S4Wpf;

public static class Program
{
    public static int ExitCode = 1;

    [STAThread]
    public static int Main(string[] argv)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            Options o = Options.Parse(argv);
            if (o.CheckImage != null) return CheckImage(o.CheckImage);
            List<ThumbItem> items = ItemSet.Build(o.ImagesDir, o.Count);
            Console.WriteLine($"項目 {items.Count} 件(元ファイル {System.IO.Directory.GetFiles(o.ImagesDir).Length} 個を水増し)。{(o.Bench == null ? "対話モード" : "ベンチ: " + o.Bench)}");

            var app = new Application();
            app.DispatcherUnhandledException += (_, e) =>
            {
                // 想定外は握りつぶさず、内容を出して非0で終わる
                Console.Error.WriteLine("ERROR(未処理の例外): " + e.Exception);
                ExitCode = 1;
                e.Handled = true;
                app.Shutdown(1);
            };
            var window = new MainWindow(o, items);
            if (o.Bench == null) ExitCode = 0;   // 対話モードは閉じたら正常終了
            app.Run(window);
            return ExitCode;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.IOException)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
    }

    /// <summary>WIC で1枚開けるか(WebP の対応確認など)。ウィンドウは作らない。</summary>
    private static int CheckImage(string path)
    {
        try
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
            var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(fs, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var f = dec.Frames[0];
            Console.WriteLine($"開けた: {path}");
            Console.WriteLine($"  デコーダ: {dec.CodecInfo.FriendlyName} ({dec.CodecInfo.ContainerFormat})");
            Console.WriteLine($"  サイズ: {f.PixelWidth}x{f.PixelHeight} / 形式 {f.Format} / フレーム数 {dec.Frames.Count}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"開けなかった: {path}");
            Console.Error.WriteLine($"  {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }
}
