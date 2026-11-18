using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ohanami.KanaYardstick;

/// <summary>
/// 仕様から画像を描く(WPF の DrawingVisual → RenderTargetBitmap → Bgr24 の PNG)。呼ぶスレッドは STA。
/// 文字は窓(半透明の板)の中に、行の高さ = 字の高さ × 1.5 で並べる。窓は画面の下寄り(下の余白 5%)。
/// 背景: dark = 暗い単色に明るい字、light = 明るい単色に暗い字、busy = にぎやかな絵(ゲームの背景の代わり)の上に半透明の窓と白い字。
/// </summary>
internal static class ImageRenderer
{
    public static void CheckFonts()
    {
        foreach (string f in ImageSpecs.Fonts)
            if (!Fonts.SystemFontFamilies.Any(x => x.FamilyNames.Values.Contains(f)))
                throw new InvalidOperationException("フォントが無い: " + f);
    }

    public static BitmapSource Render(Spec s)
    {
        var dv = new DrawingVisual();
        int pad = Math.Max(12, s.Height);
        int lineHeight = (int)Math.Round(s.Height * 1.5);
        int chars = s.Lines.Max(l => l.Length);
        int windowWidth = chars * s.Height + 2 * pad;
        int windowHeight = s.Lines.Length * lineHeight + 2 * pad;
        int x = (s.W - windowWidth) / 2;
        int y = s.H - windowHeight - (int)Math.Round(s.H * 0.05);
        using (DrawingContext dc = dv.RenderOpen())
        {
            Brush textBrush;
            if (s.Bg == "dark")
            {
                dc.DrawRectangle(Frozen(Color.FromRgb(0x1B, 0x1D, 0x26)), null, new Rect(0, 0, s.W, s.H));
                textBrush = Frozen(Color.FromRgb(0xF4, 0xF4, 0xF4));
            }
            else if (s.Bg == "light")
            {
                dc.DrawRectangle(Frozen(Color.FromRgb(0xF3, 0xEF, 0xE4)), null, new Rect(0, 0, s.W, s.H));
                textBrush = Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E));
            }
            else if (s.Bg == "busy")
            {
                DrawBusy(dc, s.W, s.H, new Random(s.Seed ^ 0x5bd1e995));
                dc.DrawRectangle(Frozen(Color.FromArgb(150, 14, 20, 48)), new Pen(Frozen(Color.FromArgb(190, 255, 255, 255)), 2), new Rect(x, y, windowWidth, windowHeight));
                textBrush = Frozen(Colors.White);
            }
            else throw new InvalidOperationException("知らない背景: " + s.Bg);
            var typeface = new Typeface(new FontFamily(s.Font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            for (int i = 0; i < s.Lines.Length; i++)
            {
                var text = new FormattedText(s.Lines[i], CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, typeface, s.Height, textBrush, 1.0);
                dc.DrawText(text, new Point(x + pad, y + pad + i * lineHeight + (lineHeight - text.Height) / 2));
            }
        }
        var bitmap = new RenderTargetBitmap(s.W, s.H, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dv);
        var rgb = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        rgb.Freeze();
        return rgb;
    }

    public static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream fs = File.Create(path);
        encoder.Save(fs);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Hsv(double h, double s, double v, byte a = 255)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb(a, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>にぎやかな絵: グラデーション + 色とりどりの楕円・三角・細かい点。大きさは画面の幅に比例させる。</summary>
    private static void DrawBusy(DrawingContext dc, int w, int h, Random r)
    {
        double k = w / 1920.0;
        var gradient = new LinearGradientBrush(Hsv(r.NextDouble() * 360, 0.6, 0.95), Hsv(r.NextDouble() * 360, 0.7, 0.55), 90);
        gradient.Freeze();
        dc.DrawRectangle(gradient, null, new Rect(0, 0, w, h));
        for (int i = 0; i < 45; i++)
        {
            Color c = Hsv(r.NextDouble() * 360, 0.4 + r.NextDouble() * 0.6, 0.4 + r.NextDouble() * 0.6, (byte)(110 + r.Next(145)));
            dc.DrawEllipse(Frozen(c), null, new Point(r.NextDouble() * w, r.NextDouble() * h), (30 + r.NextDouble() * 320) * k, (30 + r.NextDouble() * 220) * k);
        }
        for (int i = 0; i < 25; i++)
        {
            Color c = Hsv(r.NextDouble() * 360, 0.4 + r.NextDouble() * 0.6, 0.5 + r.NextDouble() * 0.5, (byte)(110 + r.Next(145)));
            var g = new StreamGeometry();
            using (StreamGeometryContext gc = g.Open())
            {
                double cx = r.NextDouble() * w, cy = r.NextDouble() * h, size = (60 + r.NextDouble() * 360) * k;
                gc.BeginFigure(new Point(cx, cy), true, true);
                gc.LineTo(new Point(cx + (r.NextDouble() - 0.5) * size * 2, cy + (r.NextDouble() - 0.5) * size * 2), true, false);
                gc.LineTo(new Point(cx + (r.NextDouble() - 0.5) * size * 2, cy + (r.NextDouble() - 0.5) * size * 2), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(Frozen(c), null, g);
        }
        for (int i = 0; i < 400; i++)
        {
            Color c = Hsv(r.NextDouble() * 360, r.NextDouble(), 0.5 + r.NextDouble() * 0.5, (byte)(120 + r.Next(135)));
            double size = (2 + r.NextDouble() * 14) * k;
            dc.DrawRectangle(Frozen(c), null, new Rect(r.NextDouble() * w, r.NextDouble() * h, size, size));
        }
    }
}
