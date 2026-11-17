using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Bench;
using Ohanami.Core.Vlm;
using SkiaSharp;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>vlm-check の純粋な部分: 画像の選び方、評価の結果との比較、文章のトークン数の逆算、合成画像(EXIF の向き)。llama-server も GPU も使わない。</summary>
public sealed class VlmCheckTests
{
    private static VlmEvalImage EvalImage(string name, bool ok = true, string ocr = "o", string desc = "d", string[]? tags = null, int? completion = 100, string? note = null) =>
        new(name, 200, ok ? "stop" : "length", ok, ok ? ocr : null, ok ? desc : null, ok ? tags ?? ["t"] : null, 1000, completion, null, null, 0, 4096, note ?? (ok ? null : "finish_reason=length"));

    private static VlmResult Product(string ocr = "o", string desc = "d", string[]? tags = null) =>
        new(VlmOutcome.Ok, new VlmCaption(ocr, desc, tags ?? ["t"]), 1, "stop", 1000, "{}", null);

    private static VlmResult Failed(string error, string? finish = null) => new(VlmOutcome.Failed, null, 1, finish, 1000, "{}", error);

    // ---------------------------------------------------------------- 比較

    [Fact]
    public void Identical_captions_match()
    {
        VlmCompare c = VlmCheckLogic.Compare(EvalImage("a.png"), Product());
        Assert.Equal(VlmCompare.Match, c.Kind);
        Assert.True(c.IsMatch);
    }

    [Fact]
    public void Each_field_is_compared_exactly_and_the_difference_is_described()
    {
        VlmCompare ocr = VlmCheckLogic.Compare(EvalImage("a.png", ocr: "こんにちは世界"), Product(ocr: "こんにちは世間"));
        Assert.Equal(VlmCompare.Differs, ocr.Kind);
        Assert.False(ocr.OcrSame);
        Assert.True(ocr.DescriptionSame);
        Assert.Contains("6 文字目から違う", ocr.Detail, StringComparison.Ordinal);

        VlmCompare tags = VlmCheckLogic.Compare(EvalImage("a.png", tags: ["猫", "犬"]), Product(tags: ["犬", "猫"]));
        Assert.Equal(VlmCompare.Differs, tags.Kind);
        Assert.False(tags.TagsSame);
        Assert.Contains("順序が違う", tags.Detail, StringComparison.Ordinal);

        VlmCompare tags2 = VlmCheckLogic.Compare(EvalImage("a.png", tags: ["猫"]), Product(tags: ["鳥"]));
        Assert.Contains("評価だけ [猫]", tags2.Detail, StringComparison.Ordinal);
        Assert.Contains("製品だけ [鳥]", tags2.Detail, StringComparison.Ordinal);

        Assert.False(VlmCheckLogic.Compare(EvalImage("a.png", desc: "x"), Product(desc: "x ")).DescriptionSame);   // 末尾の空白の差も差(加工しない)
    }

    [Fact]
    public void Failures_are_classified_by_which_side_failed()
    {
        Assert.Equal(VlmCompare.ProductFailed, VlmCheckLogic.Compare(EvalImage("a.png"), Failed("HTTP 400")).Kind);
        Assert.Equal(VlmCompare.VlmEvalFailed, VlmCheckLogic.Compare(EvalImage("a.png", ok: false), Product()).Kind);
        VlmCompare both = VlmCheckLogic.Compare(EvalImage("a.png", ok: false), Failed("finish_reason=length", "length"));
        Assert.Equal(VlmCompare.BothFailed, both.Kind);
        Assert.True(both.IsMatch);          // どちらも失敗は、構成の違いではない
        Assert.False(VlmCheckLogic.Compare(EvalImage("a.png"), Failed("e")).IsMatch);
    }

    [Fact]
    public void A_length_failure_caused_by_the_smaller_max_tokens_is_marked_as_a_known_difference()
    {
        VlmCompare c = VlmCheckLogic.Compare(EvalImage("a.png", completion: 3000), Failed("finish_reason=length", "length"));
        Assert.Equal(VlmCompare.ProductFailed, c.Kind);
        Assert.Contains("既知の差", c.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("既知の差", VlmCheckLogic.Compare(EvalImage("a.png", completion: 500), Failed("finish_reason=length", "length")).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_failure_is_not_a_comparison()
    {
        var r = new VlmResult(VlmOutcome.ServerFailure, null, 1, null, null, "{}", "HTTP 500");
        Assert.Throws<InvalidOperationException>(() => VlmCheckLogic.Compare(EvalImage("a.png"), r));
    }

    [Fact]
    public void First_difference_reports_the_position_and_handles_prefixes()
    {
        Assert.Contains("0 文字目から違う", VlmCheckLogic.FirstDifference("abc", "xbc"), StringComparison.Ordinal);
        Assert.Contains("3 文字目から違う", VlmCheckLogic.FirstDifference("abc", "abcd"), StringComparison.Ordinal);
        Assert.Contains("(ここで終わり)", VlmCheckLogic.FirstDifference("abc", "abcd"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 選び方

    [Fact]
    public void Samples_come_first_in_file_order_then_a_seeded_random_pick_of_other_successes_in_name_order()
    {
        List<VlmEvalImage> evalImages = Enumerable.Range(0, 30).Select(i => EvalImage($"img{i:D2}.png", ok: i % 10 != 0)).ToList();   // 0, 10, 20 は評価で失敗
        string[] samples = ["img05.png", "img01.png", "img10.png"];                                         // 失敗した画像が samples にあってもよい

        List<VlmCheckImage> a = VlmCheckLogic.SelectImages(evalImages, samples, 5, seed: 1);
        List<VlmCheckImage> b = VlmCheckLogic.SelectImages(evalImages, samples, 5, seed: 1);
        List<VlmCheckImage> c = VlmCheckLogic.SelectImages(evalImages, samples, 5, seed: 2);

        Assert.Equal(["img05.png", "img01.png", "img10.png"], a.Take(3).Select(i => i.VlmEval.FileName));
        Assert.All(a.Take(3), i => Assert.Equal("sample", i.Origin));
        Assert.Equal(5, a.Count(i => i.Origin == "random"));
        string[] random = a.Skip(3).Select(i => i.VlmEval.FileName).ToArray();
        Assert.Equal(random.Order(StringComparer.Ordinal), random);                                         // 無作為の分は名前の順
        Assert.All(a.Skip(3), i => Assert.True(i.VlmEval.ParsedOk));                                             // 評価で成功したものだけ
        Assert.DoesNotContain(random, samples.Contains);                                                    // samples と重ならない
        Assert.Equal(a.Select(i => i.VlmEval.FileName), b.Select(i => i.VlmEval.FileName));                           // 同じ seed なら同じ
        Assert.NotEqual(a.Select(i => i.VlmEval.FileName), c.Select(i => i.VlmEval.FileName));                        // 違う seed なら(ほぼ)違う
    }

    [Fact]
    public void Unknown_samples_and_too_many_random_images_are_errors()
    {
        List<VlmEvalImage> evalImages = [EvalImage("a.png"), EvalImage("b.png")];
        Assert.Throws<InvalidDataException>(() => VlmCheckLogic.SelectImages(evalImages, ["zzz.png"], 0, 1));
        Assert.Throws<InvalidDataException>(() => VlmCheckLogic.SelectImages(evalImages, ["a.png"], 2, 1));        // 残りは 1 枚だけ
        Assert.Empty(VlmCheckLogic.SelectImages(evalImages, [], 0, 1));
    }

    [Fact]
    public void Sample_names_are_read_from_the_first_column_and_checked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ohanami-vlmcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ok = Path.Combine(dir, "ok.tsv");
            File.WriteAllText(ok, "filename\twidth\tx\nが.png\t1\t2\n\nb.png\t3\t4\n");
            Assert.Equal(["が.png", "b.png"], VlmCheckLogic.ReadSampleNames(ok));
            string dup = Path.Combine(dir, "dup.tsv");
            File.WriteAllText(dup, "filename\na.png\na.png\n");
            Assert.Throws<InvalidDataException>(() => VlmCheckLogic.ReadSampleNames(dup));
            string bad = Path.Combine(dir, "bad.tsv");
            File.WriteAllText(bad, "name\na.png\n");
            Assert.Throws<InvalidDataException>(() => VlmCheckLogic.ReadSampleNames(bad));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_format_comes_from_the_extension()
    {
        Assert.Equal(Ohanami.Core.Data.ImageFormat.Png, VlmCheckLogic.FormatOf("a.PNG"));
        Assert.Equal(Ohanami.Core.Data.ImageFormat.Jpeg, VlmCheckLogic.FormatOf("a.jpeg"));
        Assert.Equal(Ohanami.Core.Data.ImageFormat.Webp, VlmCheckLogic.FormatOf("a.webp"));
        Assert.Throws<InvalidDataException>(() => VlmCheckLogic.FormatOf("a.gif"));
    }

    // ---------------------------------------------------------------- 文章のトークン数

    [Fact]
    public void The_text_token_count_is_what_remains_after_the_assumed_image_tokens_and_must_agree_across_sizes()
    {
        TextTokenEstimate ok = VlmCheckLogic.EstimateTextTokens([(32, 32, 601), (64, 64, 604), (256, 128, 632)]);   // 画像 1、4、32 トークン + 文章 600
        Assert.True(ok.Consistent);
        Assert.Equal(600, ok.TextTokens);

        TextTokenEstimate bad = VlmCheckLogic.EstimateTextTokens([(32, 32, 601), (64, 64, 610)]);
        Assert.False(bad.Consistent);
        Assert.Equal([600, 606], bad.PerSample);

        Assert.Throws<ArgumentException>(() => VlmCheckLogic.EstimateTextTokens([(32, 32, 601)]));
        Assert.Throws<ArgumentException>(() => VlmCheckLogic.EstimateTextTokens([(30, 32, 601), (64, 64, 604)]));
    }

    // ---------------------------------------------------------------- 合成画像

    [Fact]
    public void The_exif_orientation_jpeg_is_read_by_skia_as_orientation_6_and_is_stored_sideways()
    {
        using SKBitmap scene = SyntheticImages.Scene(320, 180);
        byte[] jpeg = SyntheticImages.JpegWithExifOrientation6(scene);
        using SKCodec codec = SKCodec.Create(SKData.CreateCopy(jpeg))!;
        Assert.Equal(SKEncodedOrigin.RightTop, codec.EncodedOrigin);       // EXIF の向き 6
        Assert.Equal(180, codec.Info.Width);                               // 保存された画素は横倒し(幅と高さが入れ替わる)
        Assert.Equal(320, codec.Info.Height);
    }

    [Fact]
    public void Exif_is_inserted_right_after_soi_and_a_non_jpeg_is_refused()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0xFF, 0xD9];
        byte[] result = SyntheticImages.InsertExifOrientation(jpeg, 3);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x22], result.Take(6));
        Assert.Equal(jpeg.Length + 36, result.Length);
        Assert.Equal(jpeg.Skip(2), result.Skip(2 + 36));                  // 元の続きはそのまま
        Assert.Throws<InvalidDataException>(() => SyntheticImages.InsertExifOrientation([1, 2, 3, 4], 6));
    }

    [Fact]
    public void The_transparent_scene_really_has_a_transparent_background_and_the_normal_one_does_not()
    {
        using SKBitmap clear = SyntheticImages.Scene(200, 120, transparent: true);
        using SKBitmap normal = SyntheticImages.Scene(200, 120);
        Assert.Equal(0, clear.GetPixel(199, 0).Alpha);                     // 右上は何も描かない
        Assert.Equal(255, normal.GetPixel(199, 0).Alpha);
    }

    [Fact]
    public void The_hand_written_png_keeps_white_rgb_under_alpha_0_and_Skia_can_read_it()
    {
        byte[] png = SyntheticImages.TransparentWhitePng(200, 120);
        var unpremul = new SKImageInfo(200, 120, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap decoded = SKBitmap.Decode(png, unpremul);
        Assert.Equal(200, decoded.Width);
        Assert.Equal(120, decoded.Height);
        // Skia の読み込みは事前乗算を経ないので、透明な画素の RGB(白)がそのまま読める。文字(黒、不透明)も残っている
        Assert.Equal(0, decoded.GetPixel(199, 0).Alpha);
        Assert.Equal(new SKColor(255, 255, 255, 0), decoded.GetPixel(199, 0));
        Assert.Contains(decoded.Pixels, p => p.Alpha == 255 && p.Red < 40 && p.Green < 40 && p.Blue < 40);
        Assert.Throws<ArgumentException>(() => SyntheticImages.RgbaPng(2, 2, new byte[3]));
    }

    [Fact]
    public void The_scene_survives_png_jpeg_and_webp_encoding()
    {
        using SKBitmap scene = SyntheticImages.Scene(256, 144);
        foreach (byte[] bytes in new[] { SyntheticImages.Png(scene), SyntheticImages.Jpeg(scene), SyntheticImages.Webp(scene) })
        {
            using SKBitmap decoded = SKBitmap.Decode(bytes);
            Assert.Equal(256, decoded.Width);
            Assert.Equal(144, decoded.Height);
        }
    }
}
