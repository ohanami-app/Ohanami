// このファイルの PillowResample は、Pillow の Resample.c(ImagingResample の bicubic)を C# に写したもの。元のコードの表示:
//   The Python Imaging Library (PIL) is
//     Copyright © 1997-2011 by Secret Labs AB
//     Copyright © 1995-2011 by Fredrik Lundh and contributors
//   Pillow is the friendly PIL fork. It is
//     Copyright © 2010 by Jeffrey 'Alex' Clark and contributors
//   Like PIL, Pillow is licensed under the open source MIT-CMU License.
// 許諾文の全文は licenses/Pillow-MIT-CMU.txt(THIRD-PARTY-NOTICES.md の 4)。https://github.com/python-pillow/Pillow

using System;
using System.IO;
using SkiaSharp;

namespace Ohanami.Core.Embedding;

/// <summary>幅×高さの RGB8(R, G, B の順に3バイト、行は詰めて並べる)。</summary>
internal sealed class RgbImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "幅と高さは1以上");
        if (pixels.Length != checked(width * height * 3)) throw new ArgumentException("画素の長さが幅×高さ×3 と違う", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}

/// <summary>「バイト列 → RGB8 の幅×高さ」の1メソッド。デコーダはこの裏に隠し、取り替えられるようにする。</summary>
internal interface IImageDecoder
{
    /// <summary>
    /// 画像をデコードして RGB8 にする。アルファは合成せずに捨てる(RGB の値はそのまま)。パレットは展開する。
    /// カラーマネジメントはしない(ICC や gAMA は無視)。EXIF の向きも見ない。デコードできなければ例外。
    /// </summary>
    RgbImage DecodeRgb(byte[] encoded);
}

/// <summary>
/// SkiaSharp(libpng / libjpeg-turbo / libwebp)によるデコード。Unpremul・色空間なしで RGBA8 を要求し、アルファを捨てる。
/// SKCodec.GetPixels は EXIF の向きを適用しない。
/// </summary>
internal sealed class SkiaImageDecoder : IImageDecoder
{
    public RgbImage DecodeRgb(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        using SKData data = SKData.CreateCopy(encoded);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null) throw new InvalidDataException("画像をデコードできない(未対応の形式、または壊れたデータ)");
        SKImageInfo src = codec.Info;
        if (src.Width <= 0 || src.Height <= 0) throw new InvalidDataException($"画像のサイズが不正: {src.Width}x{src.Height}");

        // 色空間は指定しない(null)= 色変換をしない。Unpremul = 保存されている RGB をそのまま得る。
        var dst = new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var rgba = new byte[checked(dst.Width * dst.Height * 4)];
        SKCodecResult result = codec.GetPixels(dst, rgba);
        if (result != SKCodecResult.Success) throw new InvalidDataException($"デコードに失敗した: {result}");

        var rgb = new byte[dst.Width * dst.Height * 3];
        for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
        {
            rgb[j] = rgba[i];
            rgb[j + 1] = rgba[i + 1];
            rgb[j + 2] = rgba[i + 2];
        }
        return new RgbImage(dst.Width, dst.Height, rgb);
    }
}

/// <summary>
/// Pillow の ImagingResample(Resample.c)の bicubic を写したリサイズ。RGB8 専用。
/// <list type="bullet">
/// <item>水平 → 垂直の2パス。各パスの後に 8bit に丸める(固定小数点 PRECISION_BITS = 22。初期値は丸め用の 1 &lt;&lt; 21)</item>
/// <item>縮小のときはフィルタの幅を縮小率(in/out)の倍に広げる(support = 2 × filterscale)</item>
/// <item>bicubic は Keys のカーネルで a = −0.5。端はクランプ(範囲を画像の内側に切る)。係数は出力画素ごとに合計 1 に正規化する</item>
/// <item>係数は double で計算し、固定小数点には +0.5(負は −0.5)して切り捨て(C の (int) キャスト)</item>
/// <item>同じサイズなら Pillow と同じくコピーを返す。片方の軸だけが違うときは、その軸のパスだけを行う</item>
/// </list>
/// </summary>
internal static class PillowResample
{
    private const int PrecisionBits = 32 - 8 - 2;
    private const double Support = 2.0;

    public static RgbImage ResizeBicubic(RgbImage src, int outWidth, int outHeight)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (outWidth <= 0 || outHeight <= 0) throw new ArgumentOutOfRangeException(nameof(outWidth), "出力のサイズは1以上");
        if (src.Width == outWidth && src.Height == outHeight) return new RgbImage(src.Width, src.Height, (byte[])src.Pixels.Clone());

        bool needH = outWidth != src.Width;
        bool needV = outHeight != src.Height;
        Coeffs ch = Precompute(src.Width, outWidth);
        Coeffs cv = Precompute(src.Height, outHeight);

        // 垂直パスが使う入力の行の範囲(Pillow の ybox_first / ybox_last)
        int yFirst = cv.Bounds[0];
        int yLast = cv.Bounds[(outHeight - 1) * 2] + cv.Bounds[(outHeight - 1) * 2 + 1];

        byte[] cur = src.Pixels;
        int curW = src.Width;
        int curRowOffset = 0; // cur の先頭行が、元画像の何行目か
        if (needH)
        {
            int rows = yLast - yFirst;
            var tmp = new byte[checked(rows * outWidth * 3)];
            for (int y = 0; y < rows; y++)
            {
                int srcRow = (yFirst + y) * src.Width * 3;
                int dstRow = y * outWidth * 3;
                for (int xx = 0; xx < outWidth; xx++)
                {
                    int xmin = ch.Bounds[xx * 2], xmax = ch.Bounds[xx * 2 + 1];
                    int kb = xx * ch.KSize;
                    int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0;
                    for (int x = 0; x < xmax; x++)
                    {
                        int k = ch.K[kb + x];
                        int p = srcRow + (x + xmin) * 3;
                        s0 += src.Pixels[p] * k;
                        s1 += src.Pixels[p + 1] * k;
                        s2 += src.Pixels[p + 2] * k;
                    }
                    int o = dstRow + xx * 3;
                    tmp[o] = Clip8(s0);
                    tmp[o + 1] = Clip8(s1);
                    tmp[o + 2] = Clip8(s2);
                }
            }
            cur = tmp;
            curW = outWidth;
            curRowOffset = yFirst;
        }

        if (!needV) return new RgbImage(outWidth, outHeight, cur);

        var result = new byte[checked(outWidth * outHeight * 3)];
        int stride = curW * 3;
        for (int yy = 0; yy < outHeight; yy++)
        {
            int ymin = cv.Bounds[yy * 2] - curRowOffset, ymax = cv.Bounds[yy * 2 + 1];
            int kb = yy * cv.KSize;
            int dstRow = yy * stride;
            for (int xx = 0; xx < stride; xx++)
            {
                int ss = 1 << (PrecisionBits - 1);
                for (int y = 0; y < ymax; y++) ss += cur[(y + ymin) * stride + xx] * cv.K[kb + y];
                result[dstRow + xx] = Clip8(ss);
            }
        }
        return new RgbImage(outWidth, outHeight, result);
    }

    private static byte Clip8(int v)
    {
        int s = v >> PrecisionBits; // 算術シフト(負は切り下げ)
        return (byte)(s < 0 ? 0 : s > 255 ? 255 : s);
    }

    private sealed class Coeffs(int ksize, int[] bounds, int[] k)
    {
        public int KSize { get; } = ksize;
        public int[] Bounds { get; } = bounds; // 出力画素ごとの (入力の開始位置, 係数の個数)
        public int[] K { get; } = k;           // 出力画素ごとに KSize 個の固定小数点の係数
    }

    private static double BicubicFilter(double x)
    {
        const double a = -0.5;
        if (x < 0.0) x = -x;
        if (x < 1.0) return ((a + 2.0) * x - (a + 3.0)) * x * x + 1;
        if (x < 2.0) return (((x - 5) * x + 8) * x - 4) * a;
        return 0.0;
    }

    /// <summary>Pillow の precompute_coeffs + normalize_coeffs_8bpc。入力の範囲は全体(in0 = 0, in1 = inSize)。</summary>
    private static Coeffs Precompute(int inSize, int outSize)
    {
        double scale = (double)((float)inSize - 0.0f) / outSize;
        double filterscale = scale < 1.0 ? 1.0 : scale;
        double support = Support * filterscale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;

        var bounds = new int[outSize * 2];
        var kk = new double[checked(outSize * ksize)];
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = 0.0 + (xx + 0.5) * scale;
            double ww = 0.0;
            double ss = 1.0 / filterscale;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;
            int kb = xx * ksize;
            int x;
            for (x = 0; x < xmax; x++)
            {
                double w = BicubicFilter((x + xmin - center + 0.5) * ss);
                kk[kb + x] = w;
                ww += w;
            }
            for (x = 0; x < xmax; x++)
            {
                if (ww != 0.0) kk[kb + x] /= ww;
            }
            for (; x < ksize; x++) kk[kb + x] = 0;
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }

        var k = new int[kk.Length];
        for (int i = 0; i < kk.Length; i++)
        {
            k[i] = kk[i] < 0
                ? (int)(-0.5 + kk[i] * (1 << PrecisionBits))
                : (int)(0.5 + kk[i] * (1 << PrecisionBits));
        }
        return new Coeffs(ksize, bounds, k);
    }
}

/// <summary>
/// SigLIP2 の画像の前処理(版: <see cref="SpecVersion"/>)。open_clip の preprocess_cfg(size 384、mode RGB、mean = std = 0.5、
/// interpolation = bicubic、resize_mode = squash)と同じ。RGB 化(デコーダが行う)→ bicubic で 384×384 に押しつぶす → /255 → (x − 0.5) / 0.5 → CHW。
/// 正規化は、参照実装(Python の numpy)と同じ順序・同じ float32 演算(x / 255f、× 2、− 1)で、結果はビット単位で一致する。
/// 参照との一致は、合成した画像 34 枚で確かめてある(tests の TestData/README と tools/siglip2-reference/README に出典と範囲を書いた)。
/// </summary>
internal static class Siglip2ImagePreprocessor
{
    public const int Size = 384;

    /// <summary>前処理の仕様の版。models テーブルの model_id に含める。</summary>
    public const string SpecVersion = "siglip2-squash384-bicubic-pillow-v1";

    public static RgbImage Resize(RgbImage rgb) => PillowResample.ResizeBicubic(rgb, Size, Size);

    /// <summary>384×384 の RGB8 から、形 [3, 384, 384] の float32(CHW)へ。</summary>
    public static float[] ToTensor(RgbImage resized)
    {
        if (resized.Width != Size || resized.Height != Size) throw new ArgumentException($"{Size}×{Size} でない: {resized.Width}x{resized.Height}", nameof(resized));
        int plane = Size * Size;
        var t = new float[3 * plane];
        byte[] p = resized.Pixels;
        for (int i = 0; i < plane; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                float x = p[i * 3 + c] / 255f;
                x *= 2f;
                x -= 1f;
                t[c * plane + i] = x;
            }
        }
        return t;
    }

    public static float[] Preprocess(byte[] encoded, IImageDecoder decoder) => ToTensor(Resize(decoder.DecodeRgb(encoded)));
}
