using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace S2Siglip2.Tests;

/// <summary>
/// 3段の一致のうち「段2 モデル」と「段3 最初から最後まで」(S2 手順4。ORT は CPU の実行プロバイダだけ。GPU は使わない)。
/// 参照は Python の onnxruntime(CPU)の出力(reference/data/*-embeddings.f32。reference/gen_embeddings.py が一度だけ生成)。
///   段2: 前処理済みのテンソル → Run → cos ≥ 0.9999。テンソルは Python の golden PNG(Pillow のリサイズ結果)から作る
///        (段1 のテストで、そこから作ったテンソルが numpy とビット一致することを確認している)
///   段3: 元の画像ファイル → C# のデコード + リサイズ + 正規化 → Run → cos ≥ 0.999(30枚以上)。テキストは同じ id 列を入れて cos ≥ 0.9999
/// </summary>
public sealed class EmbeddingTests(ITestOutputHelper output)
{
    private const int Threads = 4; // 他の測定(S1 の GPU の測定)を乱さないよう、CPU のスレッドは絞る
    private const double Stage2MinCosine = 0.9999;
    private const double Stage3MinCosineImage = 0.999;
    private const double Stage3MinCosineText = 0.9999;

    private static readonly IImageDecoder Decoder = new SkiaImageDecoder();

    private static float[][] ReadF32(string path, int dim)
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

    private static List<string> ImageNames() =>
        File.ReadLines(TestFiles.Reference("data", "image-corpus.tsv")).Skip(1).Select(l => l.Split('\t')[0]).ToList();

    private static string SourceFile(string name) =>
        File.ReadLines(TestFiles.Reference("data", "image-corpus.tsv")).Skip(1).Select(l => l.Split('\t')).First(c => c[0] == name)[1];

    [Fact]
    public void Stage2And3_Image_OnCpu()
    {
        string onnx = TestFiles.VerifiedModelFile("visual/model.onnx");
        float[][] reference = ReadF32(TestFiles.EmbeddingData("image-embeddings.f32"), Siglip2Model.EmbeddingDim);
        List<string> names = ImageNames();
        Assert.Equal(names.Count, reference.Length);
        Assert.True(names.Count >= 30);

        var sw = Stopwatch.StartNew();
        using Siglip2Model model = Siglip2Model.OpenVisualCpu(onnx, Threads);
        output.WriteLine($"画像側のセッション作成(CPU、{Threads} スレッド): {sw.Elapsed.TotalSeconds:F1} s");

        double minStage2 = 1, minStage3 = 1;
        var failures = new List<string>();
        var runTimes = new List<double>();
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            float[] refEmb = Siglip2Model.L2Normalize(reference[i]);

            // 段3: 元の画像ファイルから(デコード + リサイズ + 正規化 + Run)
            byte[] source = File.ReadAllBytes(TestFiles.Reference("data", "images", SourceFile(name)));
            float[] tensor3 = Siglip2ImagePreprocessor.Preprocess(source, Decoder);
            sw.Restart();
            float[] emb3 = model.EmbedImage(tensor3);
            runTimes.Add(sw.Elapsed.TotalMilliseconds);
            double c3 = Siglip2Model.Cosine(emb3, refEmb);
            minStage3 = Math.Min(minStage3, c3);
            if (c3 < Stage3MinCosineImage) failures.Add($"段3 {name}: cos {c3:F6}");

            // 段2: Python の前処理済みテンソル(= golden PNG から作ったもの)から。8 枚おきに確かめる
            if (i % 4 == 0)
            {
                float[] tensor2 = Siglip2ImagePreprocessor.ToTensor(Decoder.DecodeRgb(File.ReadAllBytes(TestFiles.Reference("data", "golden", name + ".png"))));
                float[] emb2 = model.EmbedImage(tensor2);
                double c2 = Siglip2Model.Cosine(emb2, refEmb);
                minStage2 = Math.Min(minStage2, c2);
                if (c2 < Stage2MinCosine) failures.Add($"段2 {name}: cos {c2:F7}");
                output.WriteLine($"{name,-36} 段2 cos {c2:F8}  段3 cos {c3:F8}");
            }
            else output.WriteLine($"{name,-36}               段3 cos {c3:F8}");
        }
        output.WriteLine($"段2(テンソル → Run): 最小 cos {minStage2:F8}、1−cos の最大 {1 - minStage2:E2}(基準 cos ≥ {Stage2MinCosine})");
        output.WriteLine($"段3(ファイル → 埋め込み): 最小 cos {minStage3:F8}、1−cos の最大 {1 - minStage3:E2}(基準 cos ≥ {Stage3MinCosineImage})、{names.Count} 枚");
        output.WriteLine($"Run(CPU、{Threads} スレッド)1枚あたり: 中央値 {Median(runTimes):F0} ms、最大 {runTimes.Max():F0} ms");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Stage3_Text_OnCpu_SameIds_MatchPython()
    {
        string onnx = TestFiles.VerifiedModelFile("textual/model.onnx");
        // 外部データの断片も、MANIFEST.tsv の sha256 と照合してから使う(model.onnx のスタブだけでは本物と言えない)
        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(onnx)!))
        {
            string fragment = Path.GetFileName(file);
            if (fragment.StartsWith("onnx__", StringComparison.Ordinal) || fragment.StartsWith("text.", StringComparison.Ordinal)
                || fragment == "model.onnx_data")   // 自前変換の ONNX は外部データを1ファイル(model.onnx_data)にまとめている
            {
                TestFiles.VerifiedModelFile("textual/" + fragment);
            }
        }
        Siglip2Tokenizer tok = Siglip2Tokenizer.Load(TestFiles.VerifiedModelFile("textual/tokenizer.json"));
        float[][] reference = ReadF32(TestFiles.EmbeddingData("text-embeddings.f32"), Siglip2Model.EmbeddingDim);
        string[] picked = File.ReadLines(TestFiles.EmbeddingData("text-embeddings.tsv")).Skip(1).Select(l => l.Split('\t')[0]).ToArray();
        Assert.Equal(picked.Length, reference.Length);
        var corpus = File.ReadLines(TestFiles.Reference("data", "tokenizer-corpus.tsv")).Skip(1).Select(l => l.Split('\t')).ToDictionary(c => c[0]);

        var sw = Stopwatch.StartNew();
        using Siglip2Model model = Siglip2Model.OpenTextualCpu(onnx, Threads);
        output.WriteLine($"テキスト側のセッション作成(CPU、{Threads} スレッド): {sw.Elapsed.TotalSeconds:F1} s");

        double min = 1;
        var times = new List<double>();
        var failures = new List<string>();
        for (int i = 0; i < picked.Length; i++)
        {
            string[] row = corpus[picked[i]];
            string text = System.Text.Json.JsonSerializer.Deserialize<string>(row[2])!;
            int[] ids = tok.Encode(text); // C# のトークナイザの出力(= Python の id 列と一致することは TokenizerTests で確認済み)
            Assert.Equal(row[4].Split(' ').Select(int.Parse).ToArray(), ids);
            sw.Restart();
            float[] emb = model.EmbedText(ids);
            times.Add(sw.Elapsed.TotalMilliseconds);
            double c = Siglip2Model.Cosine(emb, Siglip2Model.L2Normalize(reference[i]));
            min = Math.Min(min, c);
            if (c < Stage3MinCosineText) failures.Add($"no={picked[i]} [{row[1]}]: cos {c:F7}");
        }
        output.WriteLine($"テキスト 段3: {picked.Length} 件、最小 cos {min:F8}、1−cos の最大 {1 - min:E2}(基準 cos ≥ {Stage3MinCosineText})");
        output.WriteLine($"テキスト側の Run(CPU、{Threads} スレッド)1クエリあたり: 中央値 {Median(times):F0} ms、最大 {times.Max():F0} ms");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
