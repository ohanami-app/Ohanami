using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SkiaSharp;

namespace Ohanami.Bench;

/// <summary>
/// <c>sleep-fixture</c>: スリープと強制終了の手動確認用の合成画像のフォルダを作る(私物ではない。GPU は使わない)。
/// 場面ごとに別のサブフォルダを作る(画像の内容が全部違うので、フォルダを App に足すたびに「新しい待ち」ができる)。
/// 画像は 1280×720 の PNG で、大きな文字の 1 行目に「場面の名前 + 4 桁の番号」(例 <c>SLEEP-VLM 0007</c>)、2 行目に日本語、背景は番号ごとに違う色と図形。
/// <c>sleep-report</c> は、DB の OCR にファイル名の番号が含まれるかで、画像の取り違え・失われたものを調べる。
/// </summary>
internal static class SleepFixture
{
    /// <summary>場面ごとのフォルダ名と、画像の枚数の既定。VLM は 1 枚 約 4〜6 秒、埋め込みは 1 枚 約 0.1 秒。</summary>
    public static IReadOnlyList<(string Folder, string Label, int Count)> Plan(int embedCount, int vlmCount) =>
    [
        ("sleep-embed", "SLEEP-EMBED", embedCount),   // 埋め込みの最中のスリープ(ORT)。ジョブが数分続く枚数が要る
        ("sleep-vlm", "SLEEP-VLM", vlmCount),         // VLM の最中のスリープ
        ("sleep-vlm-long", "SLEEP-LONG", 20),         // 300 秒を超えるスリープ(任意)
        ("sleep-load", "SLEEP-LOAD", 8),              // llama-server の読み込み中のスリープ
        ("kill-app", "KILL-APP", vlmCount),           // App の強制終了
        ("kill-server", "KILL-SERVER", Math.Max(12, vlmCount * 4 / 5)),   // llama-server だけの kill
        ("hibernate", "HIBERNATE", 20),               // 休止状態(任意)
        ("cutoff", "CUTOFF", 8),                      // 連続のサーバ異常の打ち切り(4 枚以上が要る)
    ];

    public static int Run(string outDir, int embedCount, int vlmCount)
    {
        string root = Path.GetFullPath(outDir);
        Directory.CreateDirectory(root);
        using SKTypeface typeface = SKFontManager.Default.MatchCharacter('あ')
            ?? throw new InvalidOperationException("日本語を描けるフォントが見つからない(Windows の標準フォント以外では、この道具は動かない)");
        foreach ((string folder, string label, int count) in Plan(embedCount, vlmCount))
        {
            string dir = Path.Combine(root, folder);
            Directory.CreateDirectory(dir);
            int made = 0;
            for (int i = 1; i <= count; i++)
            {
                string path = Path.Combine(dir, FileName(label, i));
                if (File.Exists(path)) continue;   // 作り直さない(App に追加済みのものを変えない)
                File.WriteAllBytes(path, Render(typeface, label, i));
                made++;
            }
            Console.WriteLine($"{folder}: {count.ToString(CultureInfo.InvariantCulture)} 枚(今回新しく作ったのは {made.ToString(CultureInfo.InvariantCulture)} 枚)");
        }
        Console.WriteLine($"出力: {root}");
        return 0;
    }

    public static string FileName(string label, int index) => $"{label.ToLowerInvariant()}-{index.ToString("D4", CultureInfo.InvariantCulture)}.png";

    /// <summary>画像 1 枚。番号の文字は 4 桁(<see cref="FileName"/> と同じ)。</summary>
    internal static byte[] Render(SKTypeface typeface, string label, int index)
    {
        const int w = 1280, h = 720;
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bmp);
        var rng = new Random(HashCode(label) ^ (index * 7919));
        SKColor a = new((byte)(200 + rng.Next(56)), (byte)(200 + rng.Next(56)), (byte)(200 + rng.Next(56)));
        SKColor b = new((byte)(160 + rng.Next(96)), (byte)(160 + rng.Next(96)), (byte)(160 + rng.Next(96)));
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), [a, b], SKShaderTileMode.Clamp))
        using (var bg = new SKPaint { Shader = shader })
            canvas.DrawRect(0, 0, w, h, bg);
        for (int i = 0; i < 6; i++)
        {
            using var shape = new SKPaint { Color = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 90), IsAntialias = true };
            canvas.DrawCircle(rng.Next(w), rng.Next(h), 30 + rng.Next(90), shape);
        }
        using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var big = new SKFont(typeface, 96);
        using var mid = new SKFont(typeface, 64);
        canvas.DrawText($"{label} {index.ToString("D4", CultureInfo.InvariantCulture)}", 80, 300, SKTextAlign.Left, big, text);
        canvas.DrawText("合成画像 テスト", 80, 460, SKTextAlign.Left, mid, text);
        canvas.Flush();
        return SyntheticImages.Encode(bmp, SKEncodedImageFormat.Png, 100);
    }

    /// <summary>文字列の決まった(実行ごとに変わらない)ハッシュ。画像を、同じ引数で同じ内容にするため。</summary>
    private static int HashCode(string s)
    {
        int h = 17;
        foreach (char c in s) h = unchecked(h * 31 + c);
        return h;
    }
}
