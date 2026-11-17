using System;
using Ohanami.Core.Data;

namespace Ohanami.Core.Scanning;

/// <summary>
/// 画像の形式を、ファイルの先頭のバイト列(マジックナンバー)で判定する。拡張子は見ない(拡張子と中身が違えば、中身を信じる)。
/// PNG: 89 50 4E 47 0D 0A 1A 0A / JPEG: FF D8 FF / WebP: "RIFF" + 4 バイト + "WEBP"。どれでもなければ null。
/// </summary>
internal static class ImageFormatDetector
{
    /// <summary>判定に必要な先頭のバイト数。</summary>
    public const int HeaderLength = 12;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= PngSignature.Length && header[..PngSignature.Length].SequenceEqual(PngSignature)) return ImageFormat.Png;
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ImageFormat.Jpeg;
        if (header.Length >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            return ImageFormat.Webp;
        }
        return null;
    }

    /// <summary>走査の対象にする拡張子(.png / .jpg / .jpeg / .webp。大文字小文字を区別しない)。</summary>
    public static bool HasSupportedExtension(string fileName)
    {
        string ext = System.IO.Path.GetExtension(fileName);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }
}
