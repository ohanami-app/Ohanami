using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Core.Embedding;
using Ohanami.Core.Thumbnails;
using SkiaSharp;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>サムネイルの画素の作り方(DB もキャッシュも触らない部分): 箱への収め方、デコード、JPEG の往復。</summary>
public sealed class ThumbnailRendererTests
{
    // ---------------------------------------------------------------- 寸法

    [Theory]
    [InlineData(1920, 1080, 256, 144, 256, 144)]     // 同じアスペクト比
    [InlineData(2560, 1440, 256, 144, 256, 144)]
    [InlineData(4000, 1000, 256, 144, 256, 64)]      // 横長: 幅が箱に合う
    [InlineData(1000, 4000, 256, 144, 36, 144)]      // 縦長: 高さが箱に合う(1000 × 0.036 = 36)
    [InlineData(100, 50, 256, 144, 100, 50)]         // 箱より小さい原本は拡大しない
    [InlineData(256, 144, 256, 144, 256, 144)]       // ちょうど箱
    [InlineData(257, 144, 256, 144, 256, 143)]       // 1 ピクセルだけはみ出す(256/257 倍 → 高さは 143.4 → 143)
    [InlineData(1, 1, 256, 144, 1, 1)]               // 1×1
    [InlineData(10000, 10, 256, 144, 256, 1)]        // 極端な横長: 高さは 1 未満にならない
    [InlineData(10, 10000, 256, 144, 1, 144)]        // 極端な縦長
    [InlineData(1920, 1080, 1, 1, 1, 1)]             // 箱が 1×1
    [InlineData(1920, 1080, 8192, 8192, 1920, 1080)] // 大きな箱でも拡大しない
    public void Fit_keeps_the_aspect_ratio_never_enlarges_and_stays_within_the_box(int sw, int sh, int bw, int bh, int ew, int eh)
    {
        Assert.Equal((ew, eh), ThumbnailRenderer.Fit(sw, sh, bw, bh));
    }

    [Fact]
    public void Fit_stays_within_the_box_and_close_to_the_aspect_ratio_for_many_sizes()
    {
        var rng = new Random(11);
        for (int i = 0; i < 2000; i++)
        {
            int sw = rng.Next(1, 8000), sh = rng.Next(1, 8000), bw = rng.Next(1, 600), bh = rng.Next(1, 600);
            (int w, int h) = ThumbnailRenderer.Fit(sw, sh, bw, bh);
            Assert.InRange(w, 1, bw);
            Assert.InRange(h, 1, bh);
            Assert.True(w <= sw && h <= sh, $"拡大している: {sw}x{sh} → {w}x{h}");
            // 丸めは 1 ピクセル以内(拡大・縮小の倍率は同じ)
            double scale = Math.Min(1.0, Math.Min((double)bw / sw, (double)bh / sh));
            Assert.InRange(w, Math.Max(1, sw * scale - 1), sw * scale + 1);
            Assert.InRange(h, Math.Max(1, sh * scale - 1), sh * scale + 1);
        }
    }

    [Fact]
    public void Fit_rejects_nonpositive_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThumbnailRenderer.Fit(0, 10, 256, 144));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThumbnailRenderer.Fit(10, 0, 256, 144));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThumbnailRenderer.Fit(10, 10, 0, 144));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThumbnailRenderer.Fit(10, 10, 256, 0));
    }

    // ---------------------------------------------------------------- デコードと縮小

    [Theory]
    [InlineData(SkiaImageFormat.Png)]
    [InlineData(SkiaImageFormat.Jpeg)]
    [InlineData(SkiaImageFormat.Webp)]
    public void Render_decodes_every_supported_format_and_fits_the_box(SkiaImageFormat format)
    {
        byte[] bytes = ThumbnailTestImages.Gradient(1000, 500, ToSkia(format), 90);
        ThumbnailPixels p = ThumbnailRenderer.Render(bytes, 256, 144);
        Assert.Equal(256, p.Width);
        Assert.Equal(128, p.Height);                                  // 1000×500 → 256×128(アスペクト比 2:1)
        Assert.Equal(256 * 128 * 4, p.Bgra.Length);
        Assert.Equal(256 * 4, p.Stride);
        Assert.All(Enumerable.Range(0, p.Width * p.Height).Select(i => p.Bgra[i * 4 + 3]), a => Assert.Equal(255, a));   // 不透明
    }

    public enum SkiaImageFormat { Png, Jpeg, Webp }

    private static SKEncodedImageFormat ToSkia(SkiaImageFormat f) => f switch
    {
        SkiaImageFormat.Png => SKEncodedImageFormat.Png,
        SkiaImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        _ => SKEncodedImageFormat.Webp,
    };

    [Fact]
    public void Render_keeps_the_orientation_and_the_channel_order()
    {
        byte[] bytes = ThumbnailTestImages.Gradient(800, 400);        // R は左 → 右に増え、G は上 → 下に増え、B = 128
        ThumbnailPixels p = ThumbnailRenderer.Render(bytes, 200, 100);
        (byte b, byte g, byte r) Px(int x, int y) => (p.Bgra[(y * p.Width + x) * 4], p.Bgra[(y * p.Width + x) * 4 + 1], p.Bgra[(y * p.Width + x) * 4 + 2]);
        Assert.True(Px(5, 50).r < 20);                                // 左端は R が小さい
        Assert.True(Px(p.Width - 5, 50).r > 235);                     // 右端は R が大きい
        Assert.True(Px(100, 3).g < 20);                               // 上は G が小さい
        Assert.True(Px(100, p.Height - 4).g > 235);                   // 下は G が大きい
        Assert.InRange(Px(100, 50).b, 120, 136);                      // B はどこでも約 128(BGRA の並びで入っている)
    }

    [Fact]
    public void Render_does_not_enlarge_a_small_image_and_keeps_its_pixels()
    {
        byte[] bytes = ThumbnailTestImages.Solid(20, 10, new SKColor(200, 100, 50));
        ThumbnailPixels p = ThumbnailRenderer.Render(bytes, 256, 144);
        Assert.Equal((20, 10), (p.Width, p.Height));
        for (int i = 0; i < p.Width * p.Height; i++)
        {
            Assert.Equal(50, p.Bgra[i * 4]);
            Assert.Equal(100, p.Bgra[i * 4 + 1]);
            Assert.Equal(200, p.Bgra[i * 4 + 2]);
            Assert.Equal(255, p.Bgra[i * 4 + 3]);
        }
    }

    [Fact]
    public void Render_handles_a_1x1_image_and_extreme_aspect_ratios()
    {
        Assert.Equal((1, 1), Size(ThumbnailRenderer.Render(ThumbnailTestImages.Solid(1, 1, SKColors.Red), 256, 144)));
        Assert.Equal((256, 1), Size(ThumbnailRenderer.Render(ThumbnailTestImages.Gradient(4000, 10), 256, 144)));
        Assert.Equal((1, 144), Size(ThumbnailRenderer.Render(ThumbnailTestImages.Gradient(10, 4000), 256, 144)));
        Assert.Equal((1, 1), Size(ThumbnailRenderer.Render(ThumbnailTestImages.Gradient(640, 480), 1, 1)));
    }

    private static (int, int) Size(ThumbnailPixels p) => (p.Width, p.Height);

    [Fact]
    public void Transparency_is_composited_over_black_and_the_result_is_opaque()
    {
        byte[] bytes = ThumbnailTestImages.Solid(40, 40, new SKColor(255, 255, 255, 128));   // 半透明の白
        ThumbnailPixels p = ThumbnailRenderer.Render(bytes, 20, 20);
        Assert.Equal(255, p.Bgra[3]);
        Assert.InRange(p.Bgra[0], 120, 136);                          // 黒の上に重ねた半透明の白 ≈ 128 のグレー
        Assert.InRange(p.Bgra[1], 120, 136);
        Assert.InRange(p.Bgra[2], 120, 136);
    }

    [Fact]
    public void Undecodable_sources_throw_ThumbnailDecodeException()
    {
        Assert.Throws<ThumbnailDecodeException>(() => ThumbnailRenderer.Render([], 256, 144));
        Assert.Throws<ThumbnailDecodeException>(() => ThumbnailRenderer.Render([1, 2, 3, 4, 5], 256, 144));
        Assert.Throws<ThumbnailDecodeException>(() => ThumbnailRenderer.Render(new byte[1000], 256, 144));
        byte[] png = ThumbnailTestImages.Gradient(600, 400);
        Assert.Throws<ThumbnailDecodeException>(() => ThumbnailRenderer.Render(png[..(png.Length / 2)], 256, 144));   // 途中で切れた画像は、半分だけの絵にしない
    }

    [Fact]
    public void A_source_larger_than_the_pixel_limit_is_refused()
    {
        var e = Assert.Throws<ThumbnailDecodeException>(() => ThumbnailRenderer.CheckSourceSize(40000, 40000));   // 16 億画素(上限は約 2.7 億)
        Assert.Contains("大きすぎる", e.Message, StringComparison.Ordinal);
        ThumbnailRenderer.CheckSourceSize(16384, 16384);                                                          // 上限ちょうどは通る
        Assert.Throws<ThumbnailDecodeException>(() => ThumbnailRenderer.CheckSourceSize(16385, 16384));
        Assert.Throws<ThumbnailDecodeException>(() => ThumbnailRenderer.CheckSourceSize(0, 10));
        // 幅・高さだけが巨大でデータの無い PNG は、デコードの前に(またはデコードできずに)断られる
        Assert.Throws<ThumbnailDecodeException>(() => ThumbnailRenderer.Render(ThumbnailTestImages.PngHeaderOnly(40000, 40000), 256, 144));
    }

    // ---------------------------------------------------------------- 縮小の方法

    [Fact]
    public void Every_resize_method_gives_the_same_size_and_a_similar_image()
    {
        byte[] bytes = ThumbnailTestImages.Gradient(1600, 900);
        ThumbnailPixels reference = ThumbnailRenderer.Render(bytes, 256, 144, ResizeMethod.SkiaLinearMipmap);
        foreach (ResizeMethod m in Enum.GetValues<ResizeMethod>())
        {
            ThumbnailPixels p = ThumbnailRenderer.Render(bytes, 256, 144, m);
            Assert.Equal((256, 144), (p.Width, p.Height));
            double mae = Enumerable.Range(0, p.Bgra.Length).Where(i => i % 4 != 3).Average(i => Math.Abs(p.Bgra[i] - reference.Bgra[i]));
            Assert.True(mae < 3.0, $"{m}: 平均絶対差 {mae:F2}(滑らかなグラデーションなのでどの方法でも近いはず)");
        }
        Assert.Equal(ResizeMethod.SkiaLinearMipmap, ThumbnailRenderer.DefaultMethod);
    }

    [Fact]
    public void Downscaling_a_fine_checkerboard_averages_instead_of_aliasing_with_the_default_method()
    {
        // 1 ピクセルごとの白黒の市松模様を 1/8 に縮める: 平均(灰色)になるべきで、白と黒の縞(エイリアシング)ではない
        byte[] png = ThumbnailTestImages.Checkerboard(1024);
        ThumbnailPixels p = ThumbnailRenderer.Render(png, 128, 128);
        var values = Enumerable.Range(0, p.Width * p.Height).Select(i => (int)p.Bgra[i * 4 + 1]).ToList();
        Assert.InRange(values.Average(), 100, 156);
        Assert.True(values.Max() - values.Min() < 40, $"縞が出ている(最大 {values.Max()}、最小 {values.Min()})");
    }

    // ---------------------------------------------------------------- JPEG の往復

    [Fact]
    public void Jpeg_round_trip_keeps_the_size_and_roughly_the_pixels()
    {
        ThumbnailPixels p = ThumbnailRenderer.Render(ThumbnailTestImages.Gradient(800, 450), 256, 144);
        byte[] jpeg = ThumbnailRenderer.EncodeJpeg(p);
        Assert.True(jpeg.Length > 100);
        ThumbnailPixels back = ThumbnailRenderer.DecodeJpeg(jpeg);
        Assert.Equal((p.Width, p.Height), (back.Width, back.Height));
        double mae = Enumerable.Range(0, p.Bgra.Length).Where(i => i % 4 != 3).Average(i => Math.Abs(p.Bgra[i] - back.Bgra[i]));
        Assert.True(mae < 4.0, $"JPEG(品質 {ThumbnailRenderer.JpegQuality})の往復の平均絶対差 {mae:F2}");
        Assert.All(Enumerable.Range(0, back.Width * back.Height).Select(i => back.Bgra[i * 4 + 3]), a => Assert.Equal(255, a));
        Assert.Equal(85, ThumbnailRenderer.JpegQuality);
    }

    [Fact]
    public void DecodeJpeg_rejects_anything_that_is_not_a_complete_jpeg()
    {
        byte[] jpeg = ThumbnailRenderer.EncodeJpeg(ThumbnailRenderer.Render(ThumbnailTestImages.Gradient(400, 300), 256, 144));
        Assert.Throws<InvalidDataException>(() => ThumbnailRenderer.DecodeJpeg([]));
        Assert.Throws<InvalidDataException>(() => ThumbnailRenderer.DecodeJpeg([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => ThumbnailRenderer.DecodeJpeg(jpeg[..(jpeg.Length / 2)]));             // 途中で切れた
        Assert.Throws<InvalidDataException>(() => ThumbnailRenderer.DecodeJpeg(ThumbnailTestImages.Gradient(50, 50)));   // PNG(JPEG でない)
    }

    [Fact]
    public void EncodeJpeg_rejects_a_pixel_array_of_the_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => ThumbnailRenderer.EncodeJpeg(new ThumbnailPixels(10, 10, new byte[10 * 10 * 4 - 1])));
    }

    // ---------------------------------------------------------------- デコード済みの画素から(将来の入口)

    [Fact]
    public void FromRgb_converts_RGB_to_BGRA_and_fits_the_box()
    {
        var rgb = new RgbImage(2, 1, [255, 0, 0, 0, 0, 255]);        // 赤、青
        ThumbnailPixels small = ThumbnailRenderer.FromRgb(rgb, 256, 144);
        Assert.Equal((2, 1), (small.Width, small.Height));           // 拡大しない
        Assert.Equal([0, 0, 255, 255, 255, 0, 0, 255], small.Bgra);  // BGRA: 赤 = (0,0,255,255)、青 = (255,0,0,255)

        var pixels = new byte[800 * 400 * 3];
        for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = 10; pixels[i + 1] = 20; pixels[i + 2] = 30; }
        ThumbnailPixels big = ThumbnailRenderer.FromRgb(new RgbImage(800, 400, pixels), 200, 100);
        Assert.Equal((200, 100), (big.Width, big.Height));
        Assert.Equal([30, 20, 10, 255], big.Bgra[..4]);
    }
}
