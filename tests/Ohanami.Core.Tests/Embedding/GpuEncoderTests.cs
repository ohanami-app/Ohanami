using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Ohanami.Core.Models;
using Ohanami.Core.Native;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// GPU の画像エンコーダ(Category=Gpu)。環境変数 OHANAMI_GPU_TESTS=1 のときだけ動く手元専用のテスト(CI は無い。GPU が空いているときに手で実行する)。
/// 実行の仕方:
/// <code>
/// $env:OHANAMI_GPU_TESTS = '1'     # ビルドでも効く(テストの exe の隣に CUDA / cuDNN の DLL をコピーする。build/OhanamiCudaRuntime.targets)
/// $env:OHANAMI_MODELS_DIR = ...    # worktree など models/ が無い場所のとき
/// $env:OHANAMI_RUNTIME_DIR = ...   # worktree など runtime/ が無い場所のとき
/// dotnet test --project tests/Ohanami.Core.Tests --filter-trait "Category=Gpu"
/// </code>
/// GPU を使う他のプロセス(llama-server など)が動いていないこと。VRAM の測定は GPU 全体(nvidia-smi)の値なので、他のプロセスの変動が混ざる。
/// NativeRuntime.Configure はプロセス全体の設定なので、このテストを動かすと、同じプロセスの以後のテストの DLL の検索パスも変わる
/// (Category=Gpu だけを選んで動かすのが前提)。
/// <b>Gpu のテストは、同じコレクション("Gpu")に入れて、順番に動かす</b>: xunit はクラスを並列に動かすので、別々だと llama-server の VRAM(約 6GB)が、GPU 全体の値で測るこのテストに混ざって落ちる。
/// </summary>
[Collection("Gpu")]
public sealed class GpuEncoderTests(ITestOutputHelper output)
{
    // 製品は CUDA EP の use_tf32=0(全 GPU 世代で同じ fp32 の計算。TF32 は Ampere 以降にしか無く、オンのままだと GPU の世代で数値が 2 通りになる)。
    // この条件での CPU の参照との差は小さい(手元の実測: 1−cos の最大 2e-11。TF32 オンのときは 1.8e-4)ので、基準を 1−cos ≤ 1e-6 に締める。
    // TF32 を戻す、前処理や転送が壊れる、などの本物の不具合が、広い基準(cos ≥ 0.999)の陰に隠れないようにするため。
    // 代価: Run が約 30% 遅い(64 → 82〜85ms。1 万枚で 3〜4 分の差)。
    private const double MaxOneMinusCosAgainstCpuReference = 1e-6;
    private const int DisposeSettleMs = 3000;
    private const long DisposeBudgetMiB = 600; // 評価の基準(ADR 0011): 作成前 + 600MiB 以下

    private static bool Enabled => Environment.GetEnvironmentVariable("OHANAMI_GPU_TESTS") == "1";

    private static string ModelDirectory => Path.Combine(TestFiles.ModelsDir, ModelCatalog.DirectoryName);

    /// <summary>GPU 全体の使用中 VRAM(MiB)。nvidia-smi を呼ぶ(評価の測定と同じ方式)。</summary>
    private static long UsedVramMiB()
    {
        var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("nvidia-smi を起動できない");
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return long.Parse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim(), CultureInfo.InvariantCulture);
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public async Task Gpu_encoder_passes_the_self_check_matches_the_cpu_reference_and_releases_vram_on_dispose()
    {
        Assert.SkipUnless(Enabled, "環境変数 OHANAMI_GPU_TESTS=1 が無い(GPU の手元専用のテスト)");
        string native = AppContext.BaseDirectory;

        // 検索パスを設定(PATH を外す)。画像側のモデルのハッシュを検証してから作る(製品の手順)
        NativeRuntime.Configure(native);
        Assert.Equal(NativeSelfCheck.NormalizeDirectory(native), NativeRuntime.ConfiguredDirectory);
        var sw = Stopwatch.StartNew();
        await ModelVerifier.VerifyImageSideAsync(ModelDirectory, ModelCatalog.Current, TestContext.Current.CancellationToken);
        output.WriteLine($"画像側のモデルのハッシュ検証: {sw.Elapsed.TotalSeconds:F1} s");

        long vramBefore = UsedVramMiB();

        // ---- 作成(1 回目): 検索パス → 必要ファイル → セッション → ダミーの Run → 自己検査
        sw.Restart();
        Siglip2ImageEncoder encoder = Siglip2ImageEncoder.Create(ModelDirectory, ModelCatalog.Current, native);
        double createSeconds = sw.Elapsed.TotalSeconds;
        long vramCreated;
        double medianRunMs, minCos = 1;
        try
        {
            SelfCheckReport? report = encoder.SelfCheck;
            Assert.NotNull(report);
            Assert.True(report!.Passed, string.Join(" / ", report.Violations));
            output.WriteLine($"自己検査: 合格。ORT {report.OrtVersion}、検査の対象の DLL {report.Modules.Count} 個");
            foreach (LoadedModule m in report.Modules) output.WriteLine($"    {m.Name,-40} {m.Version ?? "",-28} {m.Path}");
            Assert.Contains(report.Modules, m => m.Name.Equals("cudnn64_9.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(report.Modules, m => m.Name.Equals("onnxruntime_providers_cuda.dll", StringComparison.OrdinalIgnoreCase));
            Assert.All(report.Modules.Where(m => m.Name.StartsWith("cudnn", StringComparison.OrdinalIgnoreCase)),
                m => Assert.StartsWith(NativeSelfCheck.NormalizeDirectory(native), NativeSelfCheck.NormalizeDirectory(Path.GetDirectoryName(m.Path)!), StringComparison.OrdinalIgnoreCase));

            // ---- 参照(CPU の onnxruntime の出力。TestData)と一致
            float[][] reference = EmbeddingTests.ReadF32(TestFiles.TestData("image-embeddings.f32"), ModelCatalog.EmbeddingDim);
            List<string> names = EmbeddingTests.ImageNames();
            IImageDecoder decoder = new SkiaImageDecoder();
            var runTimes = new List<double>();
            for (int i = 0; i < names.Count; i++)
            {
                byte[] source = File.ReadAllBytes(TestFiles.TestData("images", EmbeddingTests.SourceFile(names[i])));
                float[] tensor = Siglip2ImagePreprocessor.Preprocess(source, decoder);
                sw.Restart();
                float[] e = encoder.Embed(tensor);
                runTimes.Add(sw.Elapsed.TotalMilliseconds);
                double cos = EmbeddingTests.Cosine(e, Embeddings.L2Normalize(reference[i]));
                minCos = Math.Min(minCos, cos);
                Assert.True(1 - cos <= MaxOneMinusCosAgainstCpuReference, $"{names[i]}: 1−cos {1 - cos:E3}(基準 {MaxOneMinusCosAgainstCpuReference:E1})");
            }
            medianRunMs = Median(runTimes);
            vramCreated = UsedVramMiB();
        }
        finally
        {
            sw.Restart();
            encoder.Dispose();
            output.WriteLine($"Dispose: {sw.Elapsed.TotalMilliseconds:F0} ms");
        }

        await Task.Delay(DisposeSettleMs, TestContext.Current.CancellationToken);
        long vramAfter = UsedVramMiB();

        // ---- 作り直し(Pause → Resume): 2 回目の作成も同じ手順で通り、続けて動く
        sw.Restart();
        using (Siglip2ImageEncoder second = Siglip2ImageEncoder.Create(ModelDirectory, ModelCatalog.Current, native))
        {
            double secondCreateSeconds = sw.Elapsed.TotalSeconds;
            Assert.True(second.SelfCheck!.Passed);
            byte[] source = File.ReadAllBytes(TestFiles.TestData("images", EmbeddingTests.SourceFile(EmbeddingTests.ImageNames()[0])));
            float[] again = second.Embed(Siglip2ImagePreprocessor.Preprocess(source, new SkiaImageDecoder()));
            Assert.Equal(1.0, Math.Sqrt(again.Sum(x => (double)x * x)), 5);
            output.WriteLine($"作成の時間: 1 回目 {createSeconds:F2} s、作り直し {secondCreateSeconds:F2} s(ダミーの Run と自己検査を含む)");
        }

        output.WriteLine($"Run(GPU)1 枚あたり(エンコーダの Embed。正規化を含む): 中央値 {medianRunMs:F0} ms、参照との最小 cos {minCos:F10}(1−cos の最大 {1 - minCos:E2}。基準 {MaxOneMinusCosAgainstCpuReference:E1})");
        output.WriteLine($"VRAM(GPU 全体): 作成前 {vramBefore} MiB → 作成後・推論後 {vramCreated} MiB → Dispose の {DisposeSettleMs / 1000} 秒後 {vramAfter} MiB(基準 作成前 + {DisposeBudgetMiB} MiB 以下)");
        Assert.True(vramCreated > vramBefore + 500, $"作成後の VRAM が増えていない({vramBefore} → {vramCreated})。GPU を使っていない?");
        Assert.True(vramAfter <= vramBefore + DisposeBudgetMiB, $"Dispose 後の VRAM が戻っていない: 作成前 {vramBefore} MiB、Dispose 後 {vramAfter} MiB");
    }
}
