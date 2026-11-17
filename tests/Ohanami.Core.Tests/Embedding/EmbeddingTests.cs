using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Ohanami.Core.Models;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 出荷するモデルの出力が、参照(Python の onnxruntime CPU の出力。TestData/*-embeddings.f32)と一致すること(Category=Model。
/// モデルの実物 4.6GB を読み、CPU で実行するので約 2 分かかる。GPU は使わない)。
/// 画像: 前処理済みのテンソル(Pillow のリサイズ結果の PNG から作る)→ Run → cos ≥ 0.9999。
///       元の画像ファイル → C# のデコード + リサイズ + 正規化 → Run → cos ≥ 0.999。
/// テキスト: 同じ id 列 → Run → cos ≥ 0.9999。
/// 製品のエンコーダ(<see cref="Siglip2ImageEncoder"/>、<see cref="Siglip2TextEncoder"/>)を通して確かめる。
/// </summary>
public sealed class EmbeddingTests(ITestOutputHelper output)
{
    private const int Threads = 4;
    private const double TensorMinCosine = 0.9999;
    private const double FileMinCosineImage = 0.999;
    private const double TextMinCosine = 0.9999;

    private static readonly IImageDecoder Decoder = new SkiaImageDecoder();

    private static string ModelDirectory => Path.Combine(TestFiles.ModelsDir, ModelCatalog.DirectoryName);

    internal static float[][] ReadF32(string path, int dim)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(0, bytes.Length % (dim * sizeof(float)));
        int n = bytes.Length / (dim * sizeof(float));
        var rows = new float[n][];
        for (int i = 0; i < n; i++)
        {
            rows[i] = new float[dim];
            Buffer.BlockCopy(bytes, i * dim * sizeof(float), rows[i], 0, dim * sizeof(float));
        }
        return rows;
    }

    internal static List<string> ImageNames() =>
        File.ReadLines(TestFiles.TestData("image-corpus.tsv")).Skip(1).Select(l => l.Split('\t')[0]).ToList();

    internal static string SourceFile(string name) =>
        File.ReadLines(TestFiles.TestData("image-corpus.tsv")).Skip(1).Select(l => l.Split('\t')).First(c => c[0] == name)[1];

    internal static double Cosine(float[] a, float[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task Image_FromTensorAndFromFile_OnCpu_MatchPython()
    {
        // 実物のハッシュを製品の検証で確かめてから使う
        await ModelVerifier.VerifyImageSideAsync(ModelDirectory, ModelCatalog.Current, TestContext.Current.CancellationToken);
        float[][] reference = ReadF32(TestFiles.TestData("image-embeddings.f32"), ModelCatalog.EmbeddingDim);
        List<string> names = ImageNames();
        Assert.Equal(names.Count, reference.Length);
        Assert.True(names.Count >= 30);

        var sw = Stopwatch.StartNew();
        using Siglip2ImageEncoder encoder = Siglip2ImageEncoder.CreateCpuForTests(ModelDirectory, ModelCatalog.Current, Threads);
        output.WriteLine($"画像側のセッション作成(CPU、{Threads} スレッド): {sw.Elapsed.TotalSeconds:F1} s");

        double minTensor = 1, minFile = 1;
        var failures = new List<string>();
        var runTimes = new List<double>();
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            float[] refEmb = Embeddings.L2Normalize(reference[i]);

            // 元の画像ファイルから(デコード + リサイズ + 正規化 + Run)
            byte[] source = File.ReadAllBytes(TestFiles.TestData("images", SourceFile(name)));
            float[] fileTensor = Siglip2ImagePreprocessor.Preprocess(source, Decoder);
            sw.Restart();
            float[] fileEmb = encoder.Embed(fileTensor);
            runTimes.Add(sw.Elapsed.TotalMilliseconds);
            double fileCos = Cosine(fileEmb, refEmb);
            minFile = Math.Min(minFile, fileCos);
            if (fileCos < FileMinCosineImage) failures.Add($"ファイル {name}: cos {fileCos:F6}");

            // Python の前処理済みテンソル(= golden PNG から作ったもの)から。4 枚おきに確かめる
            if (i % 4 == 0)
            {
                float[] goldenTensor = Siglip2ImagePreprocessor.ToTensor(Decoder.DecodeRgb(File.ReadAllBytes(TestFiles.TestData("golden", name + ".png"))));
                float[] tensorEmb = encoder.Embed(goldenTensor);
                double tensorCos = Cosine(tensorEmb, refEmb);
                minTensor = Math.Min(minTensor, tensorCos);
                if (tensorCos < TensorMinCosine) failures.Add($"テンソル {name}: cos {tensorCos:F7}");
                output.WriteLine($"{name,-36} テンソル cos {tensorCos:F8}  ファイル cos {fileCos:F8}");
            }
            else output.WriteLine($"{name,-36}                       ファイル cos {fileCos:F8}");
        }
        output.WriteLine($"テンソル → Run: 最小 cos {minTensor:F8}、1−cos の最大 {1 - minTensor:E2}(基準 cos ≥ {TensorMinCosine})");
        output.WriteLine($"ファイル → 埋め込み: 最小 cos {minFile:F8}、1−cos の最大 {1 - minFile:E2}(基準 cos ≥ {FileMinCosineImage})、{names.Count} 枚");
        output.WriteLine($"Run(CPU、{Threads} スレッド)1枚あたり: 中央値 {Median(runTimes):F0} ms、最大 {runTimes.Max():F0} ms");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task Text_OnCpu_SameIds_MatchPython()
    {
        await ModelVerifier.VerifyTextSideAsync(ModelDirectory, ModelCatalog.Current, TestContext.Current.CancellationToken);
        Siglip2Tokenizer tok = Siglip2Tokenizer.Load(TestFiles.VerifiedModelFile("textual/tokenizer.json"));
        float[][] reference = ReadF32(TestFiles.TestData("text-embeddings.f32"), ModelCatalog.EmbeddingDim);
        string[] picked = File.ReadLines(TestFiles.TestData("text-embeddings.tsv")).Skip(1).Select(l => l.Split('\t')[0]).ToArray();
        Assert.Equal(picked.Length, reference.Length);
        var corpus = File.ReadLines(TestFiles.TestData("tokenizer-corpus.tsv")).Skip(1).Select(l => l.Split('\t')).ToDictionary(c => c[0]);

        var sw = Stopwatch.StartNew();
        using Siglip2TextEncoder encoder = Siglip2TextEncoder.Create(ModelDirectory, ModelCatalog.Current, Threads);
        output.WriteLine($"テキスト側のセッション作成(CPU、{Threads} スレッド): {sw.Elapsed.TotalSeconds:F1} s");

        double min = 1;
        var times = new List<double>();
        var failures = new List<string>();
        for (int i = 0; i < picked.Length; i++)
        {
            string[] row = corpus[picked[i]];
            string text = JsonSerializer.Deserialize<string>(row[2])!;
            int[] ids = tok.Encode(text); // C# のトークナイザの出力(= Python の id 列と一致することは TokenizerTests で確認済み)
            Assert.Equal(row[4].Split(' ').Select(int.Parse).ToArray(), ids);
            sw.Restart();
            float[] emb = encoder.Embed(ids);
            times.Add(sw.Elapsed.TotalMilliseconds);
            double c = Cosine(emb, Embeddings.L2Normalize(reference[i]));
            min = Math.Min(min, c);
            if (c < TextMinCosine) failures.Add($"no={picked[i]} [{row[1]}]: cos {c:F7}");

            // 文字列から直接(整理 → トークナイズ → Run)も同じ結果になる
            if (i % 8 == 0) Assert.Equal(emb, encoder.EmbedQuery(text, tok));
        }
        output.WriteLine($"テキスト: {picked.Length} 件、最小 cos {min:F8}、1−cos の最大 {1 - min:E2}(基準 cos ≥ {TextMinCosine})");
        output.WriteLine($"テキスト側の Run(CPU、{Threads} スレッド)1クエリあたり: 中央値 {Median(times):F0} ms、最大 {times.Max():F0} ms");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
