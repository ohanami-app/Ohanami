using System;
using System.Security.Cryptography;
using SkiaSharp;

namespace Ohanami.Core.Tests;

/// <summary>サムネイルのテスト用に、SkiaSharp で小さな画像(PNG / JPEG / WebP)を作る。私物の画像は使わない。</summary>
internal static class ThumbnailTestImages
{
    /// <summary>左から右へ暗い → 明るいの横グラデーション(R = x に比例、G = y に比例、B = 128)。向きの取り違えを検出できる。</summary>
    public static byte[] Gradient(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 100)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            byte g = (byte)(height == 1 ? 0 : y * 255 / (height - 1));
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = (byte)(width == 1 ? 0 : x * 255 / (width - 1));
                pixels[i + 1] = g;
                pixels[i + 2] = 128;
                pixels[i + 3] = 255;
            }
        }
        return EncodeRgba(pixels, width, height, format, quality);
    }

    /// <summary>1 ピクセルごとの白黒の市松模様。</summary>
    public static byte[] Checkerboard(int size)
    {
        var pixels = new byte[checked(size * size * 4)];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte v = ((x + y) & 1) == 0 ? (byte)255 : (byte)0;
                int i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        return EncodeRgba(pixels, size, size, SKEncodedImageFormat.Png, 100);
    }

    private static byte[] EncodeRgba(byte[] rgba, int width, int height, SKEncodedImageFormat format, int quality)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using SKImage image = SKImage.FromPixelCopy(info, rgba, info.RowBytes);
        using SKData data = image.Encode(format, quality);
        return data.ToArray();
    }

    public static byte[] Solid(int width, int height, SKColor color, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bmp.Erase(color);
        return Encode(bmp, format, 100);
    }

    private static byte[] Encode(SKBitmap bmp, SKEncodedImageFormat format, int quality)
    {
        using SKImage image = SKImage.FromBitmap(bmp);
        using SKData data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>内容の SHA-256(小文字 16 進 64 桁)。images.hash と同じ形。</summary>
    public static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>IHDR だけで IDAT の無い PNG(幅・高さだけが巨大な、デコードできない画像)。サイズの上限の検査用。</summary>
    public static byte[] PngHeaderOnly(uint width, uint height)
    {
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;   // ビット深度
        ihdr[9] = 2;   // RGB
        using var ms = new System.IO.MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteBigEndian(byte[] b, int offset, uint v)
    {
        b[offset] = (byte)(v >> 24);
        b[offset + 1] = (byte)(v >> 16);
        b[offset + 2] = (byte)(v >> 8);
        b[offset + 3] = (byte)v;
    }

    private static void WriteChunk(System.IO.Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBigEndian(len, 0, (uint)data.Length);
        s.Write(len);
        byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crcInput = new byte[t.Length + data.Length];
        t.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, t.Length);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(crcInput));
        s.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}
