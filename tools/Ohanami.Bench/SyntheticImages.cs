using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Ohanami.Bench;

/// <summary>
/// vlm-check が使う合成画像(私物でない。Skia で描く)。形式・向き・アルファの確認と、4K の大きい画像の確認に使う。
/// 画面は、左上に赤い丸、右下に青い四角、中ほどに大きな文字の行(上下・左右を取り違えたり、回っていたりすると、読みにくくなる)。
/// </summary>
internal static class SyntheticImages
{
    /// <summary>画面に描く文字の行(上から順)。OCR の結果と照らす。</summary>
    public static readonly string[] Lines = ["ここは テスト です", "合成画像 ABC 123", "上が上、右が右"];

    private static SKTypeface JapaneseTypeface()
    {
        SKTypeface? tf = SKFontManager.Default.MatchCharacter('あ');
        return tf ?? throw new InvalidOperationException("日本語を描けるフォントが見つからない(Windows の標準フォント以外では、この道具は動かない)");
    }

    /// <summary>基本の場面を描く(<paramref name="transparent"/> なら背景を透明にして、文字を黒で描く。そうでなければ薄い色のグラデーションの背景)。</summary>
    public static SKBitmap Scene(int width, int height, bool transparent = false)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bmp);
        if (transparent)
        {
            canvas.Clear(SKColors.Transparent);
        }
        else
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [new SKColor(0xF4, 0xF1, 0xE8), new SKColor(0xD6, 0xE4, 0xEE)], SKShaderTileMode.Clamp);
            using var bg = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, width, height, bg);
        }
        using SKTypeface typeface = JapaneseTypeface();
        float fontSize = Math.Max(16f, height / 9f);
        using var font = new SKFont(typeface, fontSize);
        using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        for (int i = 0; i < Lines.Length; i++)
            canvas.DrawText(Lines[i], width * 0.18f, height * (0.38f + 0.2f * i), SKTextAlign.Left, font, text);
        using var red = new SKPaint { Color = new SKColor(0xD0, 0x20, 0x20), IsAntialias = true };
        canvas.DrawCircle(width * 0.08f, height * 0.12f, height * 0.07f, red);
        using var blue = new SKPaint { Color = new SKColor(0x20, 0x40, 0xC0), IsAntialias = true };
        canvas.DrawRect(width * 0.86f, height * 0.80f, width * 0.10f, height * 0.14f, blue);
        canvas.Flush();
        return bmp;
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(format, quality) ?? throw new InvalidOperationException($"{format} にエンコードできない");
        return data.ToArray();
    }

    /// <summary>
    /// 透明な部分(アルファ 0)の RGB が**白**の PNG(文字は黒)。アルファを捨てて RGB をそのまま使う読み手には白背景、アルファで黒に合成する読み手には黒背景に見える。
    /// Skia の描画・エンコードは事前乗算で、完全に透明な画素の RGB を保てない(0 になる)ので、PNG を自分で書く。
    /// </summary>
    public static byte[] TransparentWhitePng(int width, int height)
    {
        using SKBitmap scene = Scene(width, height, transparent: true);
        byte[] rgba = scene.Bytes;      // Rgba8888
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] != 0) continue;
            rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
        }
        return RgbaPng(width, height, rgba);
    }

    /// <summary>8 ビットの RGBA(非事前乗算)から PNG を書く(フィルタなし、zlib 圧縮)。</summary>
    public static byte[] RgbaPng(int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("RGBA のバイト数が幅×高さ×4 でない", nameof(rgba));
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);   // フィルタなし
            raw.Write(rgba, y * width * 4, width * 4);
        }
        using var compressed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw.GetBuffer(), 0, (int)raw.Length);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)width);
        WriteBigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;    // ビット深度
        ihdr[9] = 6;    // RGBA
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
        byte[] len = new byte[4];
        WriteBigEndian(len, 0, (uint)data.Length);
        s.Write(len);
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in t.Concat(data)) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        byte[] c = new byte[4];
        WriteBigEndian(c, 0, ~crc);
        s.Write(c);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] Crc32Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static byte[] Png(SKBitmap b) => Encode(b, SKEncodedImageFormat.Png, 100);

    public static byte[] Jpeg(SKBitmap b) => Encode(b, SKEncodedImageFormat.Jpeg, 92);

    public static byte[] Webp(SKBitmap b) => Encode(b, SKEncodedImageFormat.Webp, 90);

    /// <summary>
    /// EXIF の向き = 6(「表示するときに時計回りに 90° 回す」)の JPEG。保存されている画素は、正しい向きの場面を**反時計回りに 90° 回した**もの
    /// (向きの情報を無視する読み手には、横倒しに見える)。向きを守る読み手には、正しい向きの場面に見える。
    /// </summary>
    public static byte[] JpegWithExifOrientation6(SKBitmap upright)
    {
        using SKBitmap stored = RotateCounterClockwise(upright);
        return InsertExifOrientation(Jpeg(stored), 6);
    }

    /// <summary>反時計回りに 90° 回した画像(幅と高さが入れ替わる)。</summary>
    public static SKBitmap RotateCounterClockwise(SKBitmap src)
    {
        var dst = new SKBitmap(new SKImageInfo(src.Height, src.Width, src.ColorType, src.AlphaType));
        using var canvas = new SKCanvas(dst);
        canvas.Translate(0, src.Width);
        canvas.RotateDegrees(-90);
        using SKImage image = SKImage.FromBitmap(src);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        canvas.Flush();
        return dst;
    }

    /// <summary>SOI の直後に、向きだけを持つ EXIF の APP1 を差し込む(ビッグエンディアンの TIFF、IFD0 にタグ 0x0112 だけ)。</summary>
    public static byte[] InsertExifOrientation(byte[] jpeg, ushort orientation)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new InvalidDataException("JPEG の先頭(SOI)でない");
        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 0x22,                               // APP1、長さ 34(長さの 2 バイトを含む)
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // TIFF ヘッダ(ビッグエンディアン、IFD0 はオフセット 8)
            0x00, 0x01,                                           // エントリ 1 つ
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,       // タグ 0x0112(Orientation)、型 SHORT、個数 1
            (byte)(orientation >> 8), (byte)(orientation & 0xFF), 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                               // 次の IFD なし
        ];
        var result = new List<byte>(jpeg.Length + app1.Length);
        result.AddRange(jpeg.AsSpan(0, 2));
        result.AddRange(app1);
        result.AddRange(jpeg.AsSpan(2));
        return [.. result];
    }
}
