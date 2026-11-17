using System;
using Ohanami.Core.Data;
using SkiaSharp;

namespace Ohanami.Core.Vlm;

/// <summary>VLM に送る画像(MIME と、送るバイト列。ファイルの内容そのまま)。</summary>
internal sealed record VlmImagePayload(string Mime, byte[] Bytes);

/// <summary>画像をデコードできなかった(壊れている・未対応)。その画像は failed になる(再試行しても同じ)。</summary>
internal sealed class VlmImageException : Exception
{
    public VlmImageException(string message) : base(message) { }
}

/// <summary>
/// VLM に送る画像の用意(ADR 0025)。<b>PNG・JPEG・WebP とも、ファイルのバイト列をそのまま送る</b>(MIME は <c>image/png</c> / <c>image/jpeg</c> / <c>image/webp</c>)。
/// <list type="bullet">
/// <item>WebP も、Skia で PNG にしてから送る必要はない(llama.cpp が WebP を読めない、という推測は外れた)。実物で確かめると、llama-server b12110 は
/// WebP をそのまま読める(非可逆・可逆とも OCR が正しい)ので、変換しない。Skia の変換は事前乗算でデコードするので、完全に透明な画素の RGB を 0(黒)にしてしまい、
/// PNG をそのまま送る場合(llama.cpp はアルファを捨てて RGB をそのまま使う)と、透明部分の扱いが食い違う。そのまま送れば、PNG と同じ扱いに揃う。</item>
/// <item>デコードできるかの事前の確認(<see cref="SKCodec"/>。ヘッダだけで、画素のデコードはしない)は、WebP でだけ残してある: 壊れたファイルのためにサーバを呼ばない・
/// サーバの異常と取り違えないため。PNG・JPEG は、そのまま送る(サーバが 400 を返せば、その画像の失敗になる)。</item>
/// </list>
/// EXIF の向き・アルファつき PNG の背景は、llama.cpp 側の挙動で、扱わない(ADR 0025)。
/// </summary>
internal static class VlmImage
{
    public static VlmImagePayload Prepare(byte[] bytes, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) throw new VlmImageException("空のファイル");
        switch (format)
        {
            case ImageFormat.Png:
                return new VlmImagePayload("image/png", bytes);
            case ImageFormat.Jpeg:
                return new VlmImagePayload("image/jpeg", bytes);
            case ImageFormat.Webp:
                EnsureDecodable(bytes);
                return new VlmImagePayload("image/webp", bytes);
            default:
                throw new VlmImageException("未対応の形式: " + format);
        }
    }

    /// <summary>画像として開ける(コーデックが作れる)ことを確かめる。作れなければ <see cref="VlmImageException"/>。</summary>
    private static void EnsureDecodable(byte[] encoded)
    {
        using SKData data = SKData.CreateCopy(encoded);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null) throw new VlmImageException("画像としてデコードできない(壊れた WebP)");
    }
}
