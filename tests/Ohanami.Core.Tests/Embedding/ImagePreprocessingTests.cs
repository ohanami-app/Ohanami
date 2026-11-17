using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 画像の前処理の一致。C# の RGB8 384×384 と、Pillow(open_clip の前処理を書き写した参照)の結果との、画素の最大絶対差。
/// 正解は tools/siglip2-reference/gen_image_corpus.py が合成した画像と Pillow の出力(TestData/)。
/// </summary>
public sealed class ImagePreprocessingTests(ITestOutputHelper output)
{
    /// <summary>判定: 画素の最大絶対差 ≤ 1。</summary>
    private const int MaxAllowedDiff = 1;

    private sealed record Row(string Name, string File, string Format, string PillowMode, int Width, int Height, string Note,
        string SourceSha256, string DecodedRgbSha256, string TensorSha256);

    private static readonly IImageDecoder Decoder = new SkiaImageDecoder();
    private static readonly Lazy<List<Row>> Corpus = new(LoadCorpus);

    private static List<Row> LoadCorpus()
    {
        string path = TestFiles.TestData("image-corpus.tsv");
        var rows = new List<Row>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] c = line.Split('\t');
            if (c.Length != 10) throw new InvalidDataException($"列数が 10 でない: {line}");
            rows.Add(new Row(c[0], c[1], c[2], c[3], int.Parse(c[4]), int.Parse(c[5]), c[6], c[7], c[8], c[9]));
        }
        return rows;
    }

    private static byte[] ReadSource(Row r)
    {
        byte[] bytes = File.ReadAllBytes(TestFiles.TestData("images", r.File));
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (sha != r.SourceSha256) throw new InvalidDataException($"{r.File} の sha256 が image-corpus.tsv と違う(画像が書き換わっている)");
        return bytes;
    }

    private static RgbImage ReadGolden(Row r) => Decoder.DecodeRgb(File.ReadAllBytes(TestFiles.TestData("golden", r.Name + ".png")));

    public sealed record Diff(int Max, double Mean, int DifferingPixels, int Pixels);

    private static Diff Compare(RgbImage a, RgbImage b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        int max = 0, count = 0;
        long sum = 0;
        for (int i = 0; i < a.Pixels.Length; i += 3)
        {
            int px = 0;
            for (int c = 0; c < 3; c++)
            {
                int d = Math.Abs(a.Pixels[i + c] - b.Pixels[i + c]);
                if (d > px) px = d;
                sum += d;
            }
            if (px > 0) count++;
            if (px > max) max = px;
        }
        return new Diff(max, (double)sum / a.Pixels.Length, count, a.Width * a.Height);
    }

    /// <summary>
    /// 環境変数 OHANAMI_DUMP_DECODED にディレクトリを指定すると、デコード結果(元の解像度の RGB8)を <name>.rgb(幅と高さの int32 + 画素)に書く。
    /// tools/siglip2-reference/compare_decoded.py が Pillow のデコード結果と比べるのに使う(デコーダの差の大きさを見るための補助)。
    /// </summary>
    private static void DumpDecodedIfRequested(Row r, RgbImage decoded)
    {
        string? dir = Environment.GetEnvironmentVariable("OHANAMI_DUMP_DECODED");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        using var fs = File.Create(Path.Combine(dir, r.Name + ".rgb"));
        fs.Write(BitConverter.GetBytes(decoded.Width));
        fs.Write(BitConverter.GetBytes(decoded.Height));
        fs.Write(decoded.Pixels);
    }

    [Fact]
    public void Corpus_HasTheRequiredKinds()
    {
        var rows = Corpus.Value;
        Assert.True(rows.Count >= 30, $"画像が {rows.Count} 枚しかない");
        string all = string.Join("|", rows.Select(r => r.Format));
        foreach (string kind in new[] { "PNG RGB", "PNG RGBA", "PNG P", "PNG L", "PNG LA", "ICC", "gAMA", "16bit", "JPEG baseline", "JPEG progressive", "4:2:0", "WebP lossy", "WebP lossless" })
        {
            Assert.Contains(kind, all);
        }
        Assert.Contains(rows, r => r.Width == 64 && r.Height == 64);
        Assert.Contains(rows, r => r.Width == 2561 && r.Height == 1083);
        Assert.Contains(rows, r => r.Width >= 20 * r.Height || r.Height >= 20 * r.Width);
    }

    [Fact]
    public void PreprocessedPixels_MatchPillow_WithinOne()
    {
        var failures = new List<string>();
        var table = new List<string>();
        int worst = 0;
        foreach (Row r in Corpus.Value)
        {
            RgbImage decoded = Decoder.DecodeRgb(ReadSource(r));
            DumpDecodedIfRequested(r, decoded);
            Assert.Equal(r.Width, decoded.Width);
            Assert.Equal(r.Height, decoded.Height);
            RgbImage resized = Siglip2ImagePreprocessor.Resize(decoded);
            Diff d = Compare(resized, ReadGolden(r));
            bool decoderExact = Convert.ToHexStringLower(SHA256.HashData(decoded.Pixels)) == r.DecodedRgbSha256;
            table.Add($"{r.Name,-36} {r.Width,4}x{r.Height,-4} 最大差 {d.Max} 平均差 {d.Mean:F4} 差のある画素 {d.DifferingPixels}/{d.Pixels} デコーダは Pillow とビット一致={(decoderExact ? "はい" : "いいえ")}");
            worst = Math.Max(worst, d.Max);
            if (d.Max > MaxAllowedDiff) failures.Add($"{r.Name}: 最大差 {d.Max}");
        }
        output.WriteLine("名前 / 元サイズ / 画素(R,G,B)の最大絶対差 / 平均絶対差 / 差のある画素数 / デコード結果(元の解像度)が Pillow とビット一致か");
        foreach (string line in table) output.WriteLine(line);
        output.WriteLine($"全 {Corpus.Value.Count} 枚の最大差の最大 = {worst}");
        Assert.True(failures.Count == 0, "最大絶対差が " + MaxAllowedDiff + " を超えた:\n" + string.Join("\n", failures));
    }

    /// <summary>正規化(/255 → ×2 → −1、CHW)が、参照実装(Python の numpy)の結果とビット単位で同じ float32 になること(正解 PNG = Pillow のリサイズ結果から作る)。</summary>
    [Fact]
    public void Normalization_IsBitIdenticalToReferenceNumpy()
    {
        foreach (Row r in Corpus.Value)
        {
            // 正解 PNG(Pillow のリサイズ結果。可逆)をそのままテンソルにする → numpy の結果とビット単位で同じ float32 になること
            float[] tensor = Siglip2ImagePreprocessor.ToTensor(ReadGolden(r));
            byte[] bytes = new byte[tensor.Length * sizeof(float)];
            Buffer.BlockCopy(tensor, 0, bytes, 0, bytes.Length);
            Assert.Equal(r.TensorSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    [Fact]
    public void Resize_SameSize_ReturnsACopy_LikePillow()
    {
        var img = new RgbImage(384, 384, new byte[384 * 384 * 3]);
        for (int i = 0; i < img.Pixels.Length; i++) img.Pixels[i] = (byte)(i * 31);
        RgbImage r = Siglip2ImagePreprocessor.Resize(img);
        Assert.NotSame(img.Pixels, r.Pixels);
        Assert.Equal(img.Pixels, r.Pixels);
    }

    [Fact]
    public void Decoder_RejectsGarbage_AndNull()
    {
        Assert.Throws<InvalidDataException>(() => Decoder.DecodeRgb([1, 2, 3, 4, 5]));
        Assert.Throws<InvalidDataException>(() => Decoder.DecodeRgb([]));
        Assert.Throws<ArgumentNullException>(() => Decoder.DecodeRgb(null!));
    }

    [Fact]
    public void Decoder_IgnoresExifOrientation_AndDropsAlphaWithoutCompositing()
    {
        // EXIF の向きは無視される(縦横が入れ替わらない)
        Row exif = Corpus.Value.Single(r => r.Name.StartsWith("jpeg_exif_orientation6", StringComparison.Ordinal));
        RgbImage e = Decoder.DecodeRgb(ReadSource(exif));
        Assert.Equal((exif.Width, exif.Height), (e.Width, e.Height));
        // 半透明 PNG: アルファは捨てるだけ。合成しないので、完全透明に近い画素の RGB も値が残る(下の画素の色が黒にならない)
        Row rgba = Corpus.Value.Single(r => r.Name.StartsWith("png_rgba_semitransparent", StringComparison.Ordinal));
        RgbImage a = Decoder.DecodeRgb(ReadSource(rgba));
        Assert.Equal(rgba.DecodedRgbSha256, Convert.ToHexStringLower(SHA256.HashData(a.Pixels)));
    }

    /// <summary>
    /// 埋め込みの段階は、CPU のワーカーが複数の画像を同時にデコード・リサイズ・正規化する(同じ IImageDecoder と静的なリサイズを共有する)。
    /// 並行に回した結果が、1 枚ずつ回した結果とビット単位で同じであること。
    /// </summary>
    [Fact]
    public void Preprocess_InParallel_GivesTheSameTensorsAsSequential()
    {
        var rows = Corpus.Value;
        byte[][] sources = rows.Select(ReadSource).ToArray();
        string[] expected = sources.Select(b => TensorSha(Siglip2ImagePreprocessor.Preprocess(b, Decoder))).ToArray();
        // 全画像を数回ずつ、複数のスレッドで同時に
        const int Rounds = 4;
        var actual = new string[rows.Count * Rounds];
        Parallel.For(0, actual.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => actual[i] = TensorSha(Siglip2ImagePreprocessor.Preprocess(sources[i % rows.Count], Decoder)));
        for (int i = 0; i < actual.Length; i++) Assert.Equal(expected[i % rows.Count], actual[i]);
        output.WriteLine($"{rows.Count} 枚 × {Rounds} 回を 8 スレッドで並行に回した結果が、逐次の結果と一致");
    }

    private static string TensorSha(float[] tensor)
    {
        byte[] bytes = new byte[tensor.Length * sizeof(float)];
        Buffer.BlockCopy(tensor, 0, bytes, 0, bytes.Length);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
