using System;

namespace Ohanami.Core;

/// <summary>
/// サムネイルの画素。幅 × 高さの BGRA(8 ビット × 4、行は詰めて並べる = 行の長さ <c>Width * 4</c> バイト)。アルファは常に 255(不透明)。
/// WPF の型を含まない。App が <c>BitmapSource.Create</c> で表示用に作る(<c>PixelFormats.Bgr32</c> または <c>Pbgra32</c>)。
/// </summary>
public sealed record ThumbnailPixels(int Width, int Height, byte[] Bgra)
{
    /// <summary>1 行のバイト数。</summary>
    public int Stride => Width * 4;
}

/// <summary>サムネイルを作った結果の種類。</summary>
public enum ThumbnailStatus
{
    /// <summary>作れた(またはキャッシュにあった)。</summary>
    Ok,

    /// <summary>原本が使えない(missing_since の無いパスが無い、どれも開けない・サイズか更新日時が走査のときと違う)。「画像なし」の表示にする。</summary>
    SourceUnavailable,

    /// <summary>原本は開けたが、画像としてデコードできない(壊れている、未対応の形式)。</summary>
    SourceUndecodable,
}

/// <summary>
/// <see cref="ThumbnailService.GetAsync"/> の結果。原本が開けない・壊れているのは例外でなく結果で返す(一覧の表示を止めないため)。
/// <see cref="Pixels"/> は <see cref="Status"/> が Ok のときだけ。<see cref="Message"/> は Ok でないときの理由。
/// </summary>
public sealed record ThumbnailResult(ThumbnailStatus Status, ThumbnailPixels? Pixels, bool FromCache, string? Message)
{
    public static ThumbnailResult Ok(ThumbnailPixels pixels, bool fromCache) => new(ThumbnailStatus.Ok, pixels, fromCache, null);

    public static ThumbnailResult Failure(ThumbnailStatus status, string message) => new(status, null, false, message);
}
