using System;
using System.IO;
using Ohanami.Core.Embedding;
using SkiaSharp;

namespace Ohanami.Core.Thumbnails;

/// <summary>縮小の方法(速さと画質の比較用。製品は <see cref="ThumbnailRenderer.DefaultMethod"/> を使う)。</summary>
internal enum ResizeMethod
{
    /// <summary>Skia: 線形補間 + ミップマップ(2 の冪の段で先に縮めてから線形補間)。</summary>
    SkiaLinearMipmap,

    /// <summary>Skia: 3 次補間(Mitchell)。ミップマップなし。</summary>
    SkiaMitchell,

    /// <summary>Skia: 3 次補間(Catmull-Rom)。ミップマップなし。</summary>
    SkiaCatmullRom,

    /// <summary>Skia: まず線形 + ミップマップで目標の 2 倍の大きさまで縮め、そこから 3 次補間(Mitchell)で目標の大きさにする(2 段)。</summary>
    SkiaMipThenMitchell,

    /// <summary>Skia: 同じく 2 段で、2 段目が Catmull-Rom。</summary>
    SkiaMipThenCatmullRom,

    /// <summary>自前の Pillow 互換の bicubic(縮小の幅に応じてフィルタを広げる。埋め込みの前処理と同じ実装。RGB8 専用)。</summary>
    PillowBicubic,
}

/// <summary>原本が画像としてデコードできないとき(壊れている、未対応の形式、大きすぎる)。</summary>
internal sealed class ThumbnailDecodeException : Exception
{
    public ThumbnailDecodeException(string message) : base(message) { }

    public ThumbnailDecodeException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// サムネイルの画素を作る純粋な部分(DB もファイルも触らない): デコード、箱に収まる縮小、JPEG の書き出しと読み込み。
/// デコードも縮小も SkiaSharp(縮小の方法は <see cref="DefaultMethod"/> に理由を書いた)。
/// <list type="bullet">
/// <item>箱に収まるよう縮小する。アスペクト比は維持し、<b>拡大しない</b>(原本が箱より小さければ、そのままの大きさ)。</item>
/// <item>アルファは黒の上に合成して捨てる(premultiplied のままの RGB。結果は不透明)。EXIF の向きは見ない(埋め込みのデコードと同じ)。</item>
/// </list>
/// </summary>
internal static class ThumbnailRenderer
{
    /// <summary>キャッシュの JPEG の品質(ADR 0041)。</summary>
    public const int JpegQuality = 85;

    /// <summary>箱の大きさの上限(ピクセル)。</summary>
    public const int MaxBox = 8192;

    /// <summary>デコードする原本の画素数の上限(16384 × 16384。これを超えるものはデコードしない=メモリを食いつぶさない)。</summary>
    internal const long MaxSourcePixels = 16384L * 16384L;

    /// <summary>
    /// 製品の縮小の方法: Skia の線形補間 + ミップマップ。測定(開発者の手元のスクリーンショット 1030 枚(PNG、840MB。データは公開していない)、箱 256×144、1 スレッド。
    /// tests の ThumbnailBenchmarkTests。画質は Pillow の Lanczos の縮小との PSNR、40 枚の平均):
    /// <code>
    /// 方法                     デコード + 縮小    PSNR(Lanczos との差)
    /// Skia 3 次(Mitchell)     7.1 ms            26.5 dB   ← 縮小のフィルタが広がらず、エイリアシング(ジャギーと縞)が出る
    /// Skia 3 次(Catmull-Rom)  6.9 ms            25.5 dB   ← 同上
    /// Skia 線形 + ミップマップ  8.0 ms            33.3 dB   ← 採用
    /// Skia ミップ → Mitchell   11.4 ms           35.2 dB   (2 段。+3.4 ms で +2 dB。見た目の差は小さい)
    /// 自前 Pillow 互換 bicubic  49.4 ms           43.0 dB   (RGB への変換と 2 パスの計算で約 6 倍遅い)
    /// </code>
    /// 時間の大半は PNG のデコード(約 6〜7 ms)で、縮小の差は 1〜4 ms。3 次補間は画質が悪い(エイリアシング)ので採らない。2 段にするか自前の bicubic にすると画質は
    /// 上がるが、初回の作成(1 万枚)が遅くなる(8 ワーカーで約 20 秒 → 約 30 秒 / 約 65 秒の見込み)。256×144 の一覧表示では、線形 + ミップマップと Lanczos の見た目の差は
    /// 小さい(拡大して並べて確かめた)ので、速さを取る。画質を上げたくなったら、キャッシュの書式の版(<see cref="ThumbnailService.CacheFormatVersion"/>)を上げて方法を替える。
    /// </summary>
    public const ResizeMethod DefaultMethod = ResizeMethod.SkiaLinearMipmap;

    /// <summary>箱に収まる大きさ。アスペクト比を維持し、拡大しない。どちらも 1 以上、箱以下。</summary>
    public static (int Width, int Height) Fit(int sourceWidth, int sourceHeight, int boxWidth, int boxHeight)
    {
        if (sourceWidth < 1 || sourceHeight < 1) throw new ArgumentOutOfRangeException(nameof(sourceWidth), "原本の大きさは 1 以上");
        if (boxWidth < 1 || boxHeight < 1) throw new ArgumentOutOfRangeException(nameof(boxWidth), "箱の大きさは 1 以上");
        double scale = Math.Min(1.0, Math.Min((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight));
        int w = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, boxWidth);
        int h = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, boxHeight);
        return (w, h);
    }

    /// <summary>画像ファイルのバイト列(PNG / JPEG / WebP)をデコードして、箱に収まる大きさにする。デコードできなければ <see cref="ThumbnailDecodeException"/>。</summary>
    public static ThumbnailPixels Render(byte[] encoded, int boxWidth, int boxHeight, ResizeMethod method = DefaultMethod)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        using SKData data = SKData.CreateCopy(encoded);
        return Render(data, boxWidth, boxHeight, method);
    }

    /// <summary>
    /// <see cref="Render(byte[], int, int, ResizeMethod)"/> の、ネイティブのデータ(ファイルをメモリに割り当てて読んだもの。<see cref="SKData.Create(string)"/>)を受ける版。
    /// 原本(PNG は 1 枚 0.8MB 前後)をマネージドの配列に読むと、ラージオブジェクトヒープに載って、サムネイルのたびに gen2 の GC を起こすので、サービスはこちらを使う。
    /// </summary>
    public static ThumbnailPixels Render(SKData data, int boxWidth, int boxHeight, ResizeMethod method = DefaultMethod)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (method == ResizeMethod.PillowBicubic)
        {
            RgbImage rgb;
            try { rgb = new SkiaImageDecoder().DecodeRgb(data.ToArray()); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException) { throw new ThumbnailDecodeException("画像をデコードできない: " + e.Message, e); }
            return FromRgb(rgb, boxWidth, boxHeight);
        }

        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null) throw new ThumbnailDecodeException("画像をデコードできない(未対応の形式、または壊れたデータ)");
        SKImageInfo src = codec.Info;
        CheckSourceSize(src.Width, src.Height);
        using SKBitmap full = new(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        SKCodecResult result = codec.GetPixels(full.Info, full.GetPixels());
        // 途中で切れた画像(IncompleteInput)も失敗とする(半分だけの絵をキャッシュに残さない)
        if (result != SKCodecResult.Success) throw new ThumbnailDecodeException($"画像のデコードに失敗した: {result}");

        (int w, int h) = Fit(src.Width, src.Height, boxWidth, boxHeight);
        var dstInfo = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        if (method is ResizeMethod.SkiaMipThenMitchell or ResizeMethod.SkiaMipThenCatmullRom && (w < src.Width || h < src.Height))
        {
            // 2 段: 目標の 2 倍(原本より大きくしない)まで線形 + ミップマップで縮め、そこから 3 次補間で目標へ
            int mw = Math.Min(src.Width, w * 2), mh = Math.Min(src.Height, h * 2);
            using SKBitmap? mid = (mw == src.Width && mh == src.Height) ? null
                : full.Resize(new SKImageInfo(mw, mh, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (mid is null && !(mw == src.Width && mh == src.Height)) throw new ThumbnailDecodeException("画像の縮小に失敗した(1 段目)");
            using SKBitmap? second = (mid ?? full).Resize(dstInfo, method == ResizeMethod.SkiaMipThenMitchell ? new SKSamplingOptions(SKCubicResampler.Mitchell) : new SKSamplingOptions(SKCubicResampler.CatmullRom));
            if (second is null) throw new ThumbnailDecodeException("画像の縮小に失敗した(2 段目)");
            return ToPixels(second);
        }
        using SKBitmap? scaled = full.Resize(dstInfo, Sampling(method));
        if (scaled is null) throw new ThumbnailDecodeException("画像の縮小に失敗した");
        return ToPixels(scaled);
    }

    /// <summary>
    /// デコード済みの画素(RGB8。埋め込みの段階が作ったもの)から、箱に収まる大きさのサムネイルを作る。縮小は Pillow 互換の bicubic(縮小の幅に応じて
    /// フィルタを広げる antialias つき。埋め込みの前処理と同じ実装)。今は呼ばない(将来、埋め込みの段階からデコードを 1 回で済ませるための入口)。
    /// </summary>
    public static ThumbnailPixels FromRgb(RgbImage rgb, int boxWidth, int boxHeight)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        CheckSourceSize(rgb.Width, rgb.Height);
        (int w, int h) = Fit(rgb.Width, rgb.Height, boxWidth, boxHeight);
        RgbImage scaled = (w == rgb.Width && h == rgb.Height) ? rgb : PillowResample.ResizeBicubic(rgb, w, h);
        byte[] bgra = new byte[checked(w * h * 4)];
        byte[] p = scaled.Pixels;
        for (int i = 0, j = 0; i < p.Length; i += 3, j += 4)
        {
            bgra[j] = p[i + 2];
            bgra[j + 1] = p[i + 1];
            bgra[j + 2] = p[i];
            bgra[j + 3] = 255;
        }
        return new ThumbnailPixels(w, h, bgra);
    }

    internal static void CheckSourceSize(int width, int height)
    {
        if (width < 1 || height < 1) throw new ThumbnailDecodeException($"画像の大きさが不正: {width}x{height}");
        if ((long)width * height > MaxSourcePixels) throw new ThumbnailDecodeException($"画像が大きすぎる: {width}x{height}(上限 {MaxSourcePixels} 画素)");
    }

    private static SKSamplingOptions Sampling(ResizeMethod m) => m switch
    {
        ResizeMethod.SkiaLinearMipmap => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
        ResizeMethod.SkiaMitchell => new SKSamplingOptions(SKCubicResampler.Mitchell),
        ResizeMethod.SkiaCatmullRom => new SKSamplingOptions(SKCubicResampler.CatmullRom),
        ResizeMethod.SkiaMipThenMitchell or ResizeMethod.SkiaMipThenCatmullRom => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), // 縮小しない場合(原本が箱に収まる)の既定
        _ => throw new ArgumentOutOfRangeException(nameof(m), m, "Skia の縮小ではない方法"),
    };

    private static ThumbnailPixels ToPixels(SKBitmap bitmap)
    {
        if (bitmap.Info.ColorType != SKColorType.Bgra8888 || bitmap.RowBytes != bitmap.Width * 4)
            throw new InvalidOperationException($"想定外の画素の形式: {bitmap.Info.ColorType}、行 {bitmap.RowBytes} バイト");
        byte[] bgra = bitmap.GetPixelSpan().ToArray();
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255; // アルファは常に不透明
        return new ThumbnailPixels(bitmap.Width, bitmap.Height, bgra);
    }

    /// <summary>キャッシュに書く JPEG(品質 85)。</summary>
    public static byte[] EncodeJpeg(ThumbnailPixels pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Bgra.Length != checked(pixels.Width * pixels.Height * 4)) throw new ArgumentException("画素の長さが幅 × 高さ × 4 と違う", nameof(pixels));
        var info = new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using SKImage image = SKImage.FromPixelCopy(info, pixels.Bgra, info.RowBytes);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality) ?? throw new InvalidOperationException("JPEG への変換に失敗した");
        return encoded.ToArray();
    }

    /// <summary>キャッシュの JPEG を読む。JPEG として読めない・大きさが不正なら <see cref="InvalidDataException"/>(呼び出し側がキャッシュを作り直す)。</summary>
    public static ThumbnailPixels DecodeJpeg(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (jpeg.Length == 0) throw new InvalidDataException("キャッシュのファイルが空");
        using SKData data = SKData.CreateCopy(jpeg);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null) throw new InvalidDataException("キャッシュのファイルを画像として読めない");
        if (codec.EncodedFormat != SKEncodedImageFormat.Jpeg) throw new InvalidDataException($"キャッシュのファイルが JPEG でない: {codec.EncodedFormat}");
        SKImageInfo src = codec.Info;
        if (src.Width < 1 || src.Height < 1 || src.Width > MaxBox || src.Height > MaxBox) throw new InvalidDataException($"キャッシュの画像の大きさが不正: {src.Width}x{src.Height}");
        using SKBitmap bitmap = new(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        SKCodecResult result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException($"キャッシュの JPEG のデコードに失敗した: {result}"); // 途中で切れた JPEG は失敗として作り直す
        return ToPixels(bitmap);
    }
}
