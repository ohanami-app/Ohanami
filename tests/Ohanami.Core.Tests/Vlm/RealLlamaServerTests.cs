using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Indexing;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Ohanami.Core.Vlm;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>
/// 実物の llama-server(runtime/llama.cpp/b12110)と実物のモデル(models/Qwen3-VL-8B-Instruct-GGUF)を使う確認(Category=Gpu。
/// 環境変数 OHANAMI_GPU_TESTS=1 のときだけ動く手元専用のテスト)。VRAM を約 6GB 使うので、GPU が空いているときに手で実行する。
/// <code>
/// $env:OHANAMI_GPU_TESTS = '1'
/// dotnet test --project tests/Ohanami.Core.Tests --filter-class "*RealLlamaServerTests"
/// </code>
/// 環境変数 OHANAMI_RUNTIME_DIR(runtime/ に当たるディレクトリ)と OHANAMI_MODELS_DIR は、git の worktree から本体の runtime/・models/ を指すとき。
/// <b>Gpu のテストは、同じコレクション("Gpu")に入れて、順番に動かす</b>: xunit はクラスを並列に動かすので、別々だと llama-server の VRAM(約 6GB)が、GPU 全体の値で測るこのテストに混ざって落ちる。
/// </summary>
[Collection("Gpu")]
public sealed class RealLlamaServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Enabled => Environment.GetEnvironmentVariable("OHANAMI_GPU_TESTS") == "1";

    private static string ServerExe
    {
        get
        {
            string runtime = Environment.GetEnvironmentVariable("OHANAMI_RUNTIME_DIR") is { Length: > 0 } d ? d : Path.Combine(RepoPaths.Root, "runtime");
            return Path.Combine(runtime, "llama.cpp", "b" + VlmCatalog.LlamaServerBuild.ToString(CultureInfo.InvariantCulture), "llama-server.exe");
        }
    }

    private static string ModelDirectory => Path.Combine(TestFiles.ModelsDir, VlmCatalog.DirectoryName);

    private static LlamaServerLaunch RealLaunch(TempDirectory dir) => new(
        ServerExe,
        Path.Combine(ModelDirectory, VlmCatalog.GgufFileName),
        Path.Combine(ModelDirectory, VlmCatalog.MmprojFileName),
        ImageMinTokens: null, VlmCatalog.ImageMaxTokens, ChatTemplateKwargs: null,
        LogDirectory: dir.File("logs"), HealthTimeout: LlamaServerLaunch.DefaultHealthTimeout);

    [Fact]
    [Trait("Category", "Gpu")]
    public async Task The_real_server_and_model_files_verify_then_the_server_starts_becomes_healthy_and_stops()
    {
        Assert.SkipUnless(Enabled, "環境変数 OHANAMI_GPU_TESTS=1 が無い(GPU の手元専用のテスト)");
        using var dir = new TempDirectory();
        var log = new TestLog();
        var verifier = new VlmVerifier();

        var sw = Stopwatch.StartNew();
        await verifier.VerifyModelAsync(ModelDirectory, VlmCatalog.Current, Ct);
        TimeSpan verifyModels = sw.Elapsed;
        LlamaServerVersion version = await verifier.VerifyLlamaServerAsync(ServerExe, VlmCatalog.LlamaServerExeSha256, VlmCatalog.LlamaServerBuild, VlmCatalog.LlamaServerCommit, Ct);
        Assert.Equal(VlmCatalog.LlamaServerBuild, version.Build);

        using ChildProcessGuard guard = new(dir.File("llama-server.pid"), log);
        Assert.True(guard.JobObjectAvailable);
        sw.Restart();
        LlamaServer server = await LlamaServer.StartAsync(RealLaunch(dir), guard, log, Ct);
        TimeSpan start = sw.Elapsed;
        int pid = server.ProcessId;
        try
        {
            Assert.True(await server.CheckHealthAsync(Ct));
            Assert.Contains("--image-max-tokens " + VlmCatalog.ImageMaxTokens.ToString(CultureInfo.InvariantCulture), string.Join(' ', server.Arguments), StringComparison.Ordinal);
            Assert.True(File.Exists(guard.PidFilePath));
        }
        finally
        {
            await server.StopAsync();
        }
        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(pid), 15_000), "停止のあと、llama-server が残らない");
        Assert.False(File.Exists(guard.PidFilePath));
        Assert.Contains("llama-server", string.Join("\n", log.Messages), StringComparison.Ordinal);
        TestContext.Current.SendDiagnosticMessage($"実物の llama-server: モデルの検証 {verifyModels.TotalSeconds:0.0} 秒、起動(/health まで){start.TotalSeconds:0.0} 秒、ビルド {version.Build}、commit {version.Commit}");
    }

    /// <summary>
    /// 実物の llama-server に、文字を描いた合成画像を 1 枚送り、製品の <see cref="VlmClient"/> で結果を得る(評価で決めた最終構成のリクエストが通ること)。
    /// 私物の画像は使わない。結果(出力・所要時間)は、確認のため一時ディレクトリの <c>ohanami-gpu-test-vlm.txt</c> に書く。
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public async Task The_real_server_captions_one_synthetic_image_through_the_product_client()
    {
        Assert.SkipUnless(Enabled, "環境変数 OHANAMI_GPU_TESTS=1 が無い(GPU の手元専用のテスト)");
        using var dir = new TempDirectory();
        var log = new TestLog();
        var verifier = new VlmVerifier();
        await verifier.VerifyModelAsync(ModelDirectory, VlmCatalog.Current, Ct);
        await verifier.VerifyLlamaServerAsync(ServerExe, VlmCatalog.LlamaServerExeSha256, VlmCatalog.LlamaServerBuild, VlmCatalog.LlamaServerCommit, Ct);
        byte[] png = RenderTextImage("HELLO 123", "こんにちは");

        using ChildProcessGuard guard = new(dir.File("llama-server.pid"), log);
        await using LlamaServer server = await LlamaServer.StartAsync(RealLaunch(dir), guard, log, Ct);
        using var client = new VlmClient(server.BaseUri);

        var sw = Stopwatch.StartNew();
        VlmResult r = await client.CaptionAsync(png, Ohanami.Core.Data.ImageFormat.Png, Ct);
        sw.Stop();

        string report = string.Join(Environment.NewLine,
            $"outcome={r.Outcome} attempt={r.Attempt} finish={r.FinishReason} prompt_tokens={r.PromptTokens} wall={sw.Elapsed.TotalSeconds:0.0}s error={r.Error}",
            $"ocr={r.Caption?.Ocr}",
            $"description={r.Caption?.Description}",
            $"tags={string.Join(", ", r.Caption?.Tags ?? [])}",
            $"settings={r.SettingsJson}");
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "ohanami-gpu-test-vlm.txt"), report, Ct);
        Assert.True(r.Outcome == VlmOutcome.Ok, "実物の llama-server への 1 枚が成功しない: " + r.Error);
        Assert.Equal("stop", r.FinishReason);
        Assert.NotNull(r.PromptTokens);
        Assert.NotEmpty(r.Caption!.Description);
    }

    /// <summary>
    /// 製品の経路の通し(実物): OhanamiCore に VLM を設定し、合成画像(文字を描いた 3 枚。私物ではない)を走査して、Indexer で VLM のジョブを流す。
    /// 検証(GGUF・mmproj・exe)→ 起動 → 3 枚の結果の確定(captions・stage_status・全文検索の索引)→ 終了(llama-server が残らない)。
    /// そのあと、OCR で読めた語を全文検索の経路で検索して、画像が見つかる(VLM から検索までの一連の流れ)。
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public async Task The_core_runs_the_vlm_job_on_real_images_and_the_text_is_found_by_full_text_search()
    {
        Assert.SkipUnless(Enabled, "環境変数 OHANAMI_GPU_TESTS=1 が無い(GPU の手元専用のテスト)");
        using var dir = new TempDirectory();
        var log = new TestLog();
        string library = dir.File("library");
        Directory.CreateDirectory(library);
        string[] words = ["SUNRISE", "MOUNTAIN", "TELEPHONE"];
        for (int i = 0; i < words.Length; i++)
        {
            string image = Path.Combine(library, $"img{i}.png");
            await File.WriteAllBytesAsync(image, RenderTextImage(words[i], "テスト" + i), Ct);
            File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddHours(-1));   // 走査は更新から 2 秒以内のファイルを今回は登録しない(ADR 0010)
        }
        string runtime = Environment.GetEnvironmentVariable("OHANAMI_RUNTIME_DIR") is { Length: > 0 } d ? d : Path.Combine(RepoPaths.Root, "runtime");
        var options = new CoreOptions(
            dir.File("data/ohanami.db"), dir.File("thumbs"), Path.Combine(TestFiles.ModelsDir, TestFiles.ModelRepo), AppContext.BaseDirectory, log,
            new VlmOptions(ServerExe, ModelDirectory), WatchFolders: false);

        await using OhanamiCore core = await OhanamiCore.OpenAsync(options, configureNative: false, time: null, Ct);
        FolderInfo folder = await core.Folders.AddAsync(library, Ct);
        await core.Indexer.ScanAsync([folder.Id], Ct);
        var sw = Stopwatch.StartNew();
        await core.Indexer.StartAsync([JobKind.Vlm], Ct);
        sw.Stop();

        IndexState state = core.Indexer.State;
        string report = string.Join(Environment.NewLine, $"run={state.Run} error={state.LastError} wall={sw.Elapsed.TotalSeconds:0.0}s", string.Join(" | ", state.Stages.Select(s => $"{s.Stage}: pending {s.Pending} done {s.Done} failed {s.Failed}")));
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "ohanami-gpu-test-vlm-core.txt"), report, Ct);
        Assert.True(state.Run == IndexRunState.Waiting, "VLM のジョブが失敗した: " + state.LastError);
        Assert.Equal(3, state.Stages.Single(s => s.Stage == Stage.Caption).Done);
        Assert.Equal(0, state.Stages.Single(s => s.Stage == Stage.Caption).Failed);
        // ジョブが終わったら、llama-server は残らない(終了の直後は、プロセスの一覧からの消えるのが少し遅れることがあるので、待つ)
        Assert.True(await FakeLlama.UntilAsync(() => Process.GetProcessesByName("llama-server").Length == 0, 15_000), "ジョブのあとも llama-server が残っている");

        // 全文検索の経路(VLM が書いた OCR・説明文・tags の索引)で、描いた語が見つかる。ベクトルの読み込み(別の経路)が終わるのも待つ
        Assert.True(await FakeLlama.UntilAsync(() => core.Status.VectorsLoaded, 60_000));
        SearchResponse response = await core.Search.SearchAsync(new SearchQuery("MOUNTAIN"), Ct);
        Assert.Contains(response.Results, r => r.Paths.Any(p => p.EndsWith("img1.png", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(response.Results.First(r => r.Paths.Any(p => p.EndsWith("img1.png", StringComparison.OrdinalIgnoreCase))).Hits, h => h.PathId == "fulltext");
        _ = runtime;
    }

    /// <summary>
    /// WebP(非可逆・可逆)と JPEG を、変換せずそのまま、製品の <see cref="VlmClient"/> で実物の llama-server に送り、OCR が正しく読めること
    /// (WebP → PNG の変換を外した、製品の経路での確認。ADR 0025)。
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public async Task The_real_server_reads_webp_and_jpeg_sent_as_is_through_the_product_client()
    {
        Assert.SkipUnless(Enabled, "環境変数 OHANAMI_GPU_TESTS=1 が無い(GPU の手元専用のテスト)");
        using var dir = new TempDirectory();
        var log = new TestLog();
        var verifier = new VlmVerifier();
        await verifier.VerifyModelAsync(ModelDirectory, VlmCatalog.Current, Ct);
        await verifier.VerifyLlamaServerAsync(ServerExe, VlmCatalog.LlamaServerExeSha256, VlmCatalog.LlamaServerBuild, VlmCatalog.LlamaServerCommit, Ct);
        byte[] png = RenderTextImage("SUNRISE 42", "テスト");
        using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(png);
        using SkiaSharp.SKImage image = SkiaSharp.SKImage.FromBitmap(bitmap);
        var cases = new (string Name, Ohanami.Core.Data.ImageFormat Format, byte[] Bytes)[]
        {
            ("webp-lossy", Ohanami.Core.Data.ImageFormat.Webp, image.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 80).ToArray()),
            ("webp-lossless", Ohanami.Core.Data.ImageFormat.Webp, image.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 100).ToArray()),
            ("jpeg", Ohanami.Core.Data.ImageFormat.Jpeg, image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90).ToArray()),
        };

        using ChildProcessGuard guard = new(dir.File("llama-server.pid"), log);
        await using LlamaServer server = await LlamaServer.StartAsync(RealLaunch(dir), guard, log, Ct);
        using var client = new VlmClient(server.BaseUri);

        var report = new System.Text.StringBuilder();
        foreach ((string name, Ohanami.Core.Data.ImageFormat format, byte[] bytes) in cases)
        {
            VlmResult r = await client.CaptionAsync(bytes, format, Ct);
            report.AppendLine($"{name}: {r.Outcome} ocr=[{r.Caption?.Ocr?.ReplaceLineEndings(" / ")}] mime={System.Text.Json.Nodes.JsonNode.Parse(r.SettingsJson)!["image"]!["mime"]} error={r.Error}");
            Assert.True(r.Outcome == VlmOutcome.Ok, name + ": " + r.Error);
            Assert.Contains("SUNRISE", r.Caption!.Ocr, StringComparison.OrdinalIgnoreCase);
        }
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "ohanami-gpu-test-vlm-formats.txt"), report.ToString(), Ct);
    }

    private static byte[] RenderTextImage(string line1, string line2)
    {
        using var bmp = new SkiaSharp.SKBitmap(640, 360);
        using var canvas = new SkiaSharp.SKCanvas(bmp);
        canvas.Clear(SkiaSharp.SKColors.DarkSlateBlue);
        using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.FromFamilyName("Yu Gothic UI") ?? SkiaSharp.SKTypeface.Default, 64);
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, IsAntialias = true };
        canvas.DrawText(line1, 40, 140, SkiaSharp.SKTextAlign.Left, font, paint);
        canvas.DrawText(line2, 40, 260, SkiaSharp.SKTextAlign.Left, font, paint);
        using var image = SkiaSharp.SKImage.FromBitmap(bmp);
        using SkiaSharp.SKData data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// 実物の llama-server を、ジョブオブジェクト(製品と同じ <see cref="JobObject"/> の実装)に入れた子として起動し、親を強制終了して、llama-server が消えることを確かめる
    /// (ADR 0026。入れ子のジョブとの相性を含む=このテストは dotnet test のホストの下で動く)。
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public async Task Killing_the_parent_makes_the_os_kill_the_real_llama_server()
    {
        Assert.SkipUnless(Enabled, "環境変数 OHANAMI_GPU_TESTS=1 が無い(GPU の手元専用のテスト)");
        int port = FreePort();
        var psi = new ProcessStartInfo(FakeLlama.ExePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("--guard-parent");
        psi.ArgumentList.Add("--exec");
        psi.ArgumentList.Add(ServerExe);
        psi.ArgumentList.Add("--");
        foreach (string a in LlamaServerArguments.Build(RealLaunch(new TempDirectory()), port)) psi.ArgumentList.Add(a);
        using Process parent = Process.Start(psi)!;
        string? line = await parent.StandardOutput.ReadLineAsync(Ct);
        int serverPid = int.Parse(line!.Split(' ')[1], CultureInfo.InvariantCulture);
        try
        {
            Assert.True(await FakeLlama.UntilAsync(() => IsHealthy(port), 300_000), "実物の llama-server が /health になる");
            Assert.True(FakeLlama.IsRunning(serverPid));

            parent.Kill(entireProcessTree: false); // 親だけを強制終了
            await parent.WaitForExitAsync(Ct);

            Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(serverPid), 30_000), "親が強制終了されたら、llama-server も OS に終わらされる");
            Assert.False(IsHealthy(port));
        }
        finally
        {
            if (FakeLlama.IsRunning(serverPid)) Process.GetProcessById(serverPid).Kill(entireProcessTree: true);
            if (!parent.HasExited) parent.Kill(entireProcessTree: true);
        }
    }

    private static bool IsHealthy(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using HttpResponseMessage r = http.GetAsync(new Uri($"http://127.0.0.1:{port}/health"), Ct).GetAwaiter().GetResult();
            return r.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
