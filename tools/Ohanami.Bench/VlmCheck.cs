using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Ohanami.Core.Data;
using Ohanami.Core.Models;
using Ohanami.Core.Vlm;
using SkiaSharp;

namespace Ohanami.Bench;

/// <summary>vlm-check の設定。パスはすべてフルパス。</summary>
/// <param name="EvalRunDirectory">VLM の評価の run のフォルダ(spikes/S1-vlm/runs/qwen3vl-8b-q4km-tags-all)。</param>
/// <param name="ImagesDirectory">評価が流した画像のフォルダ(読み取りだけ)。</param>
/// <param name="ModelsDirectory">models/ に当たるディレクトリ(中に <see cref="VlmCatalog.DirectoryName"/>)。</param>
/// <param name="LlamaServerExe">llama-server.exe(<see cref="VlmCatalog.LlamaServerExeSha256"/> と一致すること)。</param>
/// <param name="Root">結果の置き場(ベンチのルート。リポジトリの外)。</param>
/// <param name="SamplesPath">samples.tsv(spikes/S1-vlm/samples.tsv)。</param>
/// <param name="RandomCount">samples のほかに、評価で成功した画像から無作為に選ぶ枚数。</param>
/// <param name="Seed">無作為の選び方の種。</param>
/// <param name="ImageMaxTokens"><c>--image-max-tokens</c>(既定は <see cref="VlmCatalog.ImageMaxTokens"/>。確かめるために別の値で上げてもよい)。</param>
/// <param name="Stages">流す段階(<see cref="VlmCheck.AllStages"/> の部分集合)。</param>
internal sealed record VlmCheckOptions(
    string EvalRunDirectory, string ImagesDirectory, string ModelsDirectory, string LlamaServerExe, string Root, string SamplesPath,
    int RandomCount, int Seed, int ImageMaxTokens, IReadOnlySet<string> Stages);

/// <summary>
/// 製品の <see cref="LlamaServer"/> + <see cref="VlmClient"/> で、実物の確認をする。**llama-server を更新するときの確認にも使う**
/// (<see cref="VlmCatalog"/> のビルドの定数を替える前に実行して、差があれば内容を見て更新するか決める)。GPU を使う(VRAM 8GB のうち約 6GB)。
/// <list type="bullet">
/// <item><c>compare</c>: 評価の samples の 33 枚 + 無作為の数十枚を流し、VLM の評価の run の ocr・description・tags と一致する数を数える。</item>
/// <item><c>tokens</c>: 文章のトークン数を測り(<c>/tokenize</c> と、小さい画像の <c>prompt_tokens</c>)、評価の「画像トークンの最大」を数字で出す。</item>
/// <item><c>big</c>: 4K(3840×2160)の合成画像 1 枚の処理時間と、VRAM のピーク。足切り 6,656MiB の内側か。VRAM は llama-server のプロセスごとの値(性能カウンタ Dedicated Usage。
///   通し(vlm-full)で、GPU 全体 − 起動前の値には同時に動いていたブラウザの分が混ざると分かったため)。GPU 全体 − 起動前の値は参考に並べる。測れなければ「測れなかった」(終了コード 7)で、GPU 全体の値には戻さない。</item>
/// <item><c>formats</c>: PNG・JPEG・WebP(製品は、どの形式もファイルのバイト列をそのまま送る)・アルファつき PNG・EXIF の向きつき JPEG の合成画像を 1 枚ずつ流し、結果を保存する(目で見て確かめる)。
///   スキーマなしの短い問い合わせで、形式ごとの llama-server の応答も記録する(製品の経路ではない。アルファの扱いの比較には、WebP を Skia で PNG にしたものも、この中で明示的に作って比べる)。</item>
/// </list>
/// 結果(報告・画像・出力)は <c>&lt;root&gt;/vlm-check/&lt;時刻&gt;/</c>(リポジトリの外。評価の私物の画像の OCR を含むので、リポジトリに入れない)。
/// </summary>
internal static class VlmCheck
{
    public static readonly string[] AllStages = ["compare", "tokens", "big", "formats"];

    /// <summary>評価の足切り(プロセス分の VRAM のピーク)。</summary>
    public const int VramLimitMiB = 6656;

    private const int BigWidth = 3840;
    private const int BigHeight = 2160;

    public static async Task<int> RunAsync(VlmCheckOptions o, CancellationToken ct)
    {
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string outDir = Path.Combine(o.Root, "vlm-check", stamp + "-m" + o.ImageMaxTokens.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        void Out(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        // ---- 入力(GPU を使う前に、読めることを確かめる)
        IReadOnlyList<VlmEvalImage> evalImages = VlmEvalRun.ReadImages(o.EvalRunDirectory);
        List<VlmCheckImage> selected = o.Stages.Contains("compare")
            ? VlmCheckLogic.SelectImages(evalImages, VlmCheckLogic.ReadSampleNames(o.SamplesPath), o.RandomCount, o.Seed)
            : [];
        foreach (VlmCheckImage i in selected)
            if (!File.Exists(Path.Combine(o.ImagesDirectory, i.VlmEval.FileName))) throw new FileNotFoundException($"画像が無い: {Path.Combine(o.ImagesDirectory, i.VlmEval.FileName)}");
        Out($"入力: 評価の結果 {evalImages.Count} 枚(成功 {evalImages.Count(i => i.ParsedOk)})、比べる画像 {selected.Count} 枚(samples {selected.Count(i => i.Origin == "sample")} + 無作為 {selected.Count(i => i.Origin == "random")}、seed {o.Seed})、段階 {string.Join(",", AllStages.Where(o.Stages.Contains))}");

        // ---- ファイルの検証(製品と同じ。モデルのハッシュ、llama-server.exe の sha256 と --version)
        VlmSpec spec = VlmCatalog.Current;
        string modelDir = Path.Combine(o.ModelsDirectory, VlmCatalog.DirectoryName);
        var verifier = new VlmVerifier();
        await verifier.VerifyModelAsync(modelDir, spec, ct);
        LlamaServerVersion version = await verifier.VerifyLlamaServerAsync(o.LlamaServerExe, VlmCatalog.LlamaServerExeSha256, VlmCatalog.LlamaServerBuild, VlmCatalog.LlamaServerCommit, ct);
        Out($"検証済み: モデル 2 ファイルの sha256、llama-server build {version.Build} commit {version.Commit}。model_id(VlmCatalog)= {spec.ModelId}");
        if (o.ImageMaxTokens != VlmCatalog.ImageMaxTokens)
            Out($"注意: --image-max-tokens {o.ImageMaxTokens} は VlmCatalog の {VlmCatalog.ImageMaxTokens} と違う(この実行だけ。model_id は変わる)。");

        // ---- llama-server を起動(製品と同じ引数。VRAM の監視は起動の前から)
        using var gpu = new GpuMonitor();
        int baseline = gpu.Current;
        gpu.BeginWindow();
        // llama-server のプロセスごとの VRAM(性能カウンタ Dedicated Usage)。足切りの判定はこの値(GPU 全体の値は、ほかのプロセスの分が混ざるので参考)
        HashSet<int> preexisting = LlamaServerProcesses.Running();
        var processMemory = new PdhProcessGpuMemory();
        if (ProcessVramMonitor.Probe(processMemory) is { } probeProblem)
            Out($"**警告: 性能カウンタ(GPU Process Memory の Dedicated Usage)が読めない: {probeProblem}。VRAM は「測れなかった」になる(GPU 全体の値には戻さない)。**");
        using var processVram = new ProcessVramMonitor(processMemory, () => LlamaServerProcesses.Except(preexisting));
        processVram.StartBackground(TimeSpan.FromMilliseconds(200));
        processVram.BeginWindow();
        var log = new ConsoleLog();
        using var guard = new ChildProcessGuard(Path.Combine(o.Root, "vlm-check", "llama-server-pid.json"), log);
        var launch = new LlamaServerLaunch(
            Path.GetFullPath(o.LlamaServerExe),
            Path.Combine(modelDir, spec.Gguf.RelativePath), Path.Combine(modelDir, spec.Mmproj.RelativePath),
            spec.ImageMinTokens, o.ImageMaxTokens, string.IsNullOrEmpty(spec.ChatTemplateKwargs) ? null : spec.ChatTemplateKwargs,
            Path.Combine(o.Root, "vlm-check", "logs"), LlamaServerLaunch.DefaultHealthTimeout);
        var loadClock = Stopwatch.StartNew();
        await using LlamaServer server = await LlamaServer.StartAsync(launch, guard, log, ct);
        double loadSeconds = loadClock.Elapsed.TotalSeconds;
        int afterLoad = gpu.EndWindow();
        ProcessVramMeasurement load = processVram.WindowResult();
        Out($"llama-server: 起動 {loadSeconds:F1} 秒、VRAM(llama-server のプロセスごと){(load.PeakMiB is { } lp ? $"の読み込み中のピーク {lp} MiB" : "**測れなかった**: " + load.Problem)}。"
            + $"参考(GPU 全体): 起動前 {baseline} MiB → 読み込み後のピーク {afterLoad} MiB(差 {afterLoad - baseline} MiB。ほかのプロセスの分が混ざる)");
        Out("  引数: " + string.Join(" ", server.Arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a)));

        using var client = new VlmClient(server.BaseUri);
        int exit = 0;
        try
        {
            if (o.Stages.Contains("compare")) exit = Math.Max(exit, await CompareAsync(o, selected, client, server, outDir, Out, ct));
            int? textTokens = null;
            if (o.Stages.Contains("tokens")) textTokens = await TokensAsync(evalImages, o, client, server, Out, ct);
            if (o.Stages.Contains("big")) exit = Math.Max(exit, await BigAsync(o, client, gpu, processVram, baseline, textTokens, outDir, Out, ct));
            if (o.Stages.Contains("formats")) await FormatsAsync(client, server, outDir, Out, ct);
        }
        finally
        {
            ProcessVramMeasurement all = processVram.AllResult();
            Out($"VRAM(llama-server のプロセスごと)の、この実行全体のピーク: " + (all.PeakMiB is { } ap ? $"{ap} MiB(足切り {VramLimitMiB} MiB)" : "**測れなかった**: " + all.Problem) + "。" + all.Describe());
            Out($"VRAM(参考。GPU 全体)の、この実行全体のピーク: {gpu.AllPeak} MiB(起動前との差 {gpu.AllPeak - baseline} MiB。ほかのプロセスの分が混ざる)");
            File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString(), new UTF8Encoding(false));
            Console.WriteLine("結果: " + outDir);
        }
        return exit;
    }

    // ------------------------------------------------------------------ compare

    private static async Task<int> CompareAsync(VlmCheckOptions o, List<VlmCheckImage> selected, VlmClient client, LlamaServer server, string outDir, Action<string> Out, CancellationToken ct)
    {
        Out("");
        Out($"== compare: 評価の結果との一致({selected.Count} 枚。製品の max_tokens {VlmCheckLogic.ProductMaxTokens}、評価は 4096) ==");
        var rows = new StringBuilder("filename\torigin\tkind\tocr_same\tdescription_same\ttags_same\ts1_completion_tokens\tproduct_attempt\tproduct_prompt_tokens\ts1_prompt_tokens\twall_ms\n");
        var diffs = new StringBuilder();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var walls = new List<double>();
        var clock = Stopwatch.StartNew();
        for (int n = 0; n < selected.Count; n++)
        {
            VlmCheckImage image = selected[n];
            string path = Path.Combine(o.ImagesDirectory, image.VlmEval.FileName);
            byte[] bytes = await File.ReadAllBytesAsync(path, ct);
            var sw = Stopwatch.StartNew();
            VlmResult result = await client.CaptionAsync(bytes, VlmCheckLogic.FormatOf(image.VlmEval.FileName), ct);
            double wall = sw.Elapsed.TotalMilliseconds;
            if (result.Outcome == VlmOutcome.ServerFailure)
                throw new InvalidOperationException($"llama-server の異常({image.VlmEval.FileName}): {result.Error}\n直近のログ:\n{server.LogTail()}");
            VlmCompare c = VlmCheckLogic.Compare(image.VlmEval, result);
            counts[c.Kind] = counts.GetValueOrDefault(c.Kind) + 1;
            walls.Add(wall);
            rows.Append(string.Join('\t',
                image.VlmEval.FileName, image.Origin, c.Kind, Yn(c.OcrSame), Yn(c.DescriptionSame), Yn(c.TagsSame),
                image.VlmEval.CompletionTokens?.ToString(CultureInfo.InvariantCulture) ?? "", result.Attempt.ToString(CultureInfo.InvariantCulture),
                result.PromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "", image.VlmEval.PromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
                wall.ToString("F0", CultureInfo.InvariantCulture))).Append('\n');
            if (!c.IsMatch) diffs.Append(image.VlmEval.FileName).Append('(').Append(image.Origin).Append("): ").Append(c.Kind).Append(": ").Append(c.Detail).Append('\n');
            Console.WriteLine($"  [{n + 1}/{selected.Count}] {c.Kind} {image.VlmEval.FileName}({image.Origin}、{wall / 1000:F1} 秒)");
        }
        File.WriteAllText(Path.Combine(outDir, "compare.tsv"), rows.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "diffs.txt"), diffs.ToString(), new UTF8Encoding(false));

        int total = selected.Count;
        int matched = counts.GetValueOrDefault(VlmCompare.Match);
        Out($"  結果: {total} 枚中、一致 {matched}、差あり {counts.GetValueOrDefault(VlmCompare.Differs)}、製品だけ失敗 {counts.GetValueOrDefault(VlmCompare.ProductFailed)}、評価だけ失敗 {counts.GetValueOrDefault(VlmCompare.VlmEvalFailed)}、両方失敗 {counts.GetValueOrDefault(VlmCompare.BothFailed)}");
        foreach (string origin in new[] { "sample", "random" })
        {
            List<VlmCheckImage> part = selected.Where(i => i.Origin == origin).ToList();
            if (part.Count == 0) continue;
            Out($"    {origin}: {part.Count} 枚(処理時間の平均 {walls.Where((_, idx) => selected[idx].Origin == origin).Average() / 1000:F1} 秒)");
        }
        Out($"  所要 {clock.Elapsed.TotalMinutes:F1} 分。差の内容: {Path.Combine(outDir, "diffs.txt")}(私物の画像の文字を含む。リポジトリに入れない)、表: compare.tsv");
        return counts.GetValueOrDefault(VlmCompare.Differs) + counts.GetValueOrDefault(VlmCompare.ProductFailed) + counts.GetValueOrDefault(VlmCompare.VlmEvalFailed) > 0 ? 5 : 0;

        static string Yn(bool b) => b ? "y" : "n";
    }

    // ------------------------------------------------------------------ tokens

    /// <summary>文章のトークン数(測れて、画像の大きさごとに一致したときだけ。そうでなければ null)を返す。</summary>
    private static async Task<int?> TokensAsync(IReadOnlyList<VlmEvalImage> evalImages, VlmCheckOptions o, VlmClient client, LlamaServer server, Action<string> Out, CancellationToken ct)
    {
        Out("");
        Out("== tokens: 文章のトークン数と、評価の画像トークンの最大 ==");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // (a) /tokenize: プロンプトの本文だけ(テンプレートの印・画像は含まない)
        var body = new JsonObject { ["content"] = VlmPrompt.Text };
        using var content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
        using HttpResponseMessage r = await http.PostAsync(new Uri(server.BaseUri, "/tokenize"), content, ct);
        string text = await r.Content.ReadAsStringAsync(ct);
        int tokenizeCount = (JsonNode.Parse(text)?["tokens"] as JsonArray)?.Count ?? throw new InvalidOperationException($"/tokenize の応答に tokens が無い(HTTP {(int)r.StatusCode}): {text}");
        Out($"  /tokenize: プロンプトの本文 {VlmPrompt.Text.Length} 字 = {tokenizeCount} トークン(テンプレートの印・画像を含まない)");

        // (b) 小さい画像の prompt_tokens(製品のリクエストそのまま)。画像 (w × h) = (w/32)×(h/32) トークンと仮定して、文章の分を逆算する。
        //     小さすぎる画像(32×32、64×64)は、画像トークンが最小に切り上げられる(同じ prompt_tokens になる)ので、逆算には使わず、様子だけ見る
        var measured = new List<(int, int, int)>();
        foreach ((int w, int h) in new[] { (32, 32), (64, 64), (128, 128), (256, 128), (256, 256), (512, 256) })
        {
            using var bmp = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.White);
                using var dot = new SKPaint { Color = SKColors.Black };
                canvas.DrawCircle(w / 2f, h / 2f, Math.Min(w, h) / 4f, dot);
            }
            VlmResult res = await client.CaptionAsync(SyntheticImages.Png(bmp), ImageFormat.Png, ct);
            if (res.Outcome == VlmOutcome.ServerFailure) throw new InvalidOperationException($"llama-server の異常: {res.Error}\n{server.LogTail()}");
            if (res.PromptTokens is not { } p) throw new InvalidOperationException($"{w}×{h} の応答に prompt_tokens が無い({res.Outcome}: {res.Error})");
            if (w >= 128) measured.Add((w, h, p));
            Out($"  {w}×{h} の画像: prompt_tokens = {p}(画像を {(w / 32) * (h / 32)} トークンと仮定すると、文章 = {p - (w / 32) * (h / 32)}{(w < 128 ? "。逆算には使わない: 最小に切り上げられる" : "")})");
        }
        TextTokenEstimate est = VlmCheckLogic.EstimateTextTokens(measured);
        int textTokens = est.TextTokens;
        if (!est.Consistent)
        {
            Out($"  **仮定が合わない**: 画像の大きさごとの文章のトークン数が一致しない({string.Join("、", est.PerSample)})。文章のトークン数は未確定。以降の「画像トークンの最大」は参考値。");
        }
        else
        {
            Out($"  文章のトークン数(テンプレートの印を含む)= {textTokens}(4 つの大きさで一致。/tokenize の {tokenizeCount} との差 {textTokens - tokenizeCount} はテンプレートの印と、画像の前後の印)");
        }

        // (c) 評価の最大
        List<VlmEvalImage> byTokens = evalImages.Where(i => i.PromptTokens is not null).OrderByDescending(i => i.PromptTokens).Take(5).ToList();
        int evalMax = byTokens[0].PromptTokens!.Value;
        Out($"  評価の usage.prompt_tokens の最大 = {evalMax}({byTokens[0].FileName}。上位 5 枚: {string.Join("、", byTokens.Select(i => i.PromptTokens))})");
        string summary = Path.Combine(o.EvalRunDirectory, "summary.tsv");
        if (File.Exists(summary))
        {
            string[] lines = File.ReadAllLines(summary, new UTF8Encoding(false, true));
            int col = Array.IndexOf(lines[0].Split('\t'), "prompt_n");
            if (col >= 0)
            {
                int max = lines.Skip(1).Where(l => l.Length > 0).Select(l => int.Parse(l.Split('\t')[col], CultureInfo.InvariantCulture)).Max();
                Out($"  評価の summary.tsv の prompt_n の最大 = {max}(usage.prompt_tokens の最大と {evalMax - max} 違う。prompt_n はキャッシュ済みの分を除く値の可能性=未確認)");
            }
        }
        int imageMax = evalMax - textTokens;
        Out($"  **評価の画像トークンの最大 = {evalMax} − {textTokens} = {imageMax}**(usage.prompt_tokens から。大きい側の値)。--image-max-tokens {o.ImageMaxTokens} は これ{(o.ImageMaxTokens >= imageMax ? "以上(評価の画像にはどれにも効かない)" : "を下回る(評価の画像の一部に効く。取り込みの前提が崩れる)")}");
        return est.Consistent ? textTokens : null;
    }

    // ------------------------------------------------------------------ big

    private static async Task<int> BigAsync(VlmCheckOptions o, VlmClient client, GpuMonitor gpu, ProcessVramMonitor processVram, int baseline, int? textTokens, string outDir, Action<string> Out, CancellationToken ct)
    {
        Out("");
        Out($"== big: 4K({BigWidth}×{BigHeight})の合成画像 1 枚(--image-max-tokens {o.ImageMaxTokens}) ==");
        using SKBitmap scene = SyntheticImages.Scene(BigWidth, BigHeight);
        byte[] png = SyntheticImages.Png(scene);
        Directory.CreateDirectory(Path.Combine(outDir, "synthetic"));
        File.WriteAllBytes(Path.Combine(outDir, "synthetic", "big-3840x2160.png"), png);
        Out($"  PNG {png.Length / 1024 / 1024.0:F1} MiB");

        gpu.BeginWindow();
        processVram.BeginWindow();
        var sw = Stopwatch.StartNew();
        VlmResult res = await client.CaptionAsync(png, ImageFormat.Png, ct);
        double seconds = sw.Elapsed.TotalSeconds;
        int peak = gpu.EndWindow();
        ProcessVramMeasurement process = processVram.WindowResult();
        Out($"  結果: {res.Outcome}、{seconds:F1} 秒、試行 {res.Attempt}、finish_reason {res.FinishReason ?? "(無し)"}、prompt_tokens {res.PromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
            + (res.PromptTokens is { } pt && textTokens is { } tt ? $"(画像トークン = {pt - tt}。上限 {o.ImageMaxTokens})" : "")
            + (res.Error is null ? "" : "、error: " + res.Error));
        if (res.Caption is { } c) Out($"  ocr: 「{Flat(c.Ocr)}」 / description: 「{Flat(c.Description)}」 / tags: {string.Join("、", c.Tags)}");
        Out($"  VRAM(llama-server のプロセスごと。性能カウンタ Dedicated Usage): この 1 枚の間のピーク "
            + (process.PeakMiB is { } pm
                ? $"{pm} MiB。足切り {VramLimitMiB} MiB の{(pm <= VramLimitMiB ? "内側(余裕 " + (VramLimitMiB - pm) + " MiB)" : "**外側**(" + (pm - VramLimitMiB) + " MiB 超過)")}"
                : "**測れなかった**: " + process.Problem)
            + "。" + process.Describe());
        Out($"  VRAM(参考。合否に使わない。GPU 全体): 起動前 {baseline} MiB、この 1 枚の間のピーク {peak} MiB(差 {peak - baseline} MiB。ほかのプロセスの分が混ざる)");
        BigVerdict verdict = VlmCheckLogic.JudgeBig(res.Outcome == VlmOutcome.ServerFailure, process.PeakMiB, VramLimitMiB);
        Out(verdict switch
        {
            BigVerdict.Ok => $"  判定: --image-max-tokens {o.ImageMaxTokens} で、4K の画像は足切りの内側で処理できた。",
            BigVerdict.Unmeasured => "  判定: **VRAM を測れなかった**(GPU 全体の値には戻さない)。性能カウンタが読めるようにして、測り直す。終了コード 7。",
            _ => "  判定: **足切りの外、または処理できなかった**。値を下げて(例: 3072)測り直す。",
        });
        return VlmCheckLogic.BigExitCode(verdict);
    }

    // ------------------------------------------------------------------ formats

    private static async Task FormatsAsync(VlmClient client, LlamaServer server, string outDir, Action<string> Out, CancellationToken ct)
    {
        Out("");
        Out("== formats: 形式ごとの送り方(合成画像 1024×576。結果は目で見て確かめる) ==");
        string dir = Path.Combine(outDir, "synthetic");
        Directory.CreateDirectory(dir);
        Out($"  場面: 左上に赤い丸、右下に青い四角、文字 3 行「{string.Join("」「", SyntheticImages.Lines)}」(上から順)。画像は {dir}");

        using SKBitmap upright = SyntheticImages.Scene(1024, 576);
        using SKBitmap transparent = SyntheticImages.Scene(1024, 576, transparent: true);
        byte[] transparentWhitePng = SyntheticImages.TransparentWhitePng(1024, 576);
        byte[] exif = SyntheticImages.JpegWithExifOrientation6(upright);
        using (SKCodec codec = SKCodec.Create(SKData.CreateCopy(exif)) ?? throw new InvalidOperationException("EXIF つき JPEG を Skia が読めない"))
            Out($"  EXIF つき JPEG: Skia が読んだ向き = {codec.EncodedOrigin}(RightTop = 向き 6 なら、作りが合っている)、保存された画素 {codec.Info.Width}×{codec.Info.Height}(横倒し)");
        if (SKBitmap.Decode(SyntheticImages.Webp(upright)) is not { } webpCheck) throw new InvalidOperationException("WebP をデコードできない");
        webpCheck.Dispose();

        var cases = new (string Name, byte[] Bytes, ImageFormat Format, string Note)[]
        {
            ("png", SyntheticImages.Png(upright), ImageFormat.Png, "対照(PNG をそのまま送る)"),
            ("jpeg", SyntheticImages.Jpeg(upright), ImageFormat.Jpeg, "JPEG をそのまま送る"),
            ("webp", SyntheticImages.Webp(upright), ImageFormat.Webp, "WebP をそのまま送る(製品も変換しない)"),
            ("alpha-png", SyntheticImages.Png(transparent), ImageFormat.Png, "背景が透明(RGB は黒)で文字は黒(アルファを捨てて RGB をそのまま使うと、黒背景に黒い文字で見えない)"),
            ("alpha-webp-white", SyntheticImages.Encode(transparent, SKEncodedImageFormat.Webp, 90), ImageFormat.Webp, "アルファつき WebP をそのまま送る(製品も変換しない)"),
            ("alpha-png-white", transparentWhitePng, ImageFormat.Png, "背景が透明(RGB は白)で文字は黒(アルファを捨てて RGB をそのまま使うと、白背景に黒い文字で読める。白で合成しても読める)"),
            ("exif6-jpeg", exif, ImageFormat.Jpeg, "向き 6。向きを守れば正しい向き、無視すれば反時計回りに横倒し"),
        };
        foreach ((string name, byte[] bytes, ImageFormat format, string note) in cases)
        {
            string ext = format switch { ImageFormat.Png => "png", ImageFormat.Jpeg => "jpg", _ => "webp" };
            File.WriteAllBytes(Path.Combine(dir, name + "." + ext), bytes);
            VlmResult res = await client.CaptionAsync(bytes, format, ct);
            if (res.Outcome == VlmOutcome.ServerFailure) throw new InvalidOperationException($"llama-server の異常({name}): {res.Error}\n{server.LogTail()}");
            Out($"  [{name}] {note}: {res.Outcome}、prompt_tokens {res.PromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"}" + (res.Error is null ? "" : "、error: " + res.Error));
            if (res.Caption is { } c)
            {
                Out($"    ocr: 「{Flat(c.Ocr)}」");
                Out($"    description: 「{Flat(c.Description)}」");
                Out($"    tags: {string.Join("、", c.Tags)}");
            }
            File.WriteAllText(Path.Combine(dir, name + ".result.json"), res.SettingsJson, new UTF8Encoding(false));
        }

        // 形式ごとに、そのまま送ったときの llama-server の応答(WebP の変換を外した根拠の再確認。製品の経路ではない)
        Out("  [製品の経路ではない、そのまま送る確認(WebP をそのまま読めるか、EXIF の向きが守られるか)。スキーマなしの短い問い合わせ]");
        byte[] webpLossless = SyntheticImages.Encode(upright, SKEncodedImageFormat.Webp, 100);
        byte[] webpAlpha = SyntheticImages.Encode(transparent, SKEncodedImageFormat.Webp, 90);
        // 比較用(製品の経路ではない): WebP を Skia でデコード(事前乗算)して PNG にしたもの。完全に透明な画素の RGB は 0(黒)になる
        byte[] webpAlphaViaSkiaPng;
        using (SKBitmap decoded = SKBitmap.Decode(webpAlpha) ?? throw new InvalidOperationException("アルファつき WebP をデコードできない"))
            webpAlphaViaSkiaPng = SyntheticImages.Encode(decoded, SKEncodedImageFormat.Png, 100);
        const string readText = "この画像に書かれている文字を、そのまま書き出してください。";
        const string bgColor = "この画像の背景は何色ですか。一言で答えてください。";
        const string whereRed = "赤い丸と青い四角は、画像の上・下・左・右のどのあたりにありますか。短く答えてください。";
        foreach ((string label, byte[] bytes, string mime, string question) in new[]
        {
            ("webp(非可逆)をそのまま: 文字", cases[2].Bytes, "image/webp", readText),
            ("webp(可逆。quality 100)をそのまま: 文字", webpLossless, "image/webp", readText),
            ("webp(アルファつき)をそのまま: 文字", webpAlpha, "image/webp", readText),
            ("alpha png(透明部分の RGB は白)をそのまま: 背景の色(白なら RGB をそのまま使う、黒ならアルファを使って黒に合成)", cases.Single(c => c.Name == "alpha-png-white").Bytes, "image/png", bgColor),
            ("alpha png(透明部分の RGB は黒)をそのまま: 背景の色", cases.Single(c => c.Name == "alpha-png").Bytes, "image/png", bgColor),
            ("webp(アルファつき)をそのまま: 背景の色", webpAlpha, "image/webp", bgColor),
            ("webp(アルファつき)を、ここで明示的に Skia で PNG にしたもの: 背景の色", webpAlphaViaSkiaPng, "image/png", bgColor),
            ("png: 位置(対照。赤い丸は左上、青い四角は右下が正しい)", cases[0].Bytes, "image/png", whereRed),
            ("EXIF 向き 6 の jpeg: 位置(向きを守れば左上・右下、無視すれば左下・右上)", exif, "image/jpeg", whereRed),
        })
            Out("    " + label + " → " + await RawProbeAsync(server, bytes, mime, question, ct));
    }

    /// <summary>スキーマなしの短い問い合わせで、画像を data URI で送り、HTTP の状態と応答の先頭を返す(形式をそのまま受け付けるかの確認用)。</summary>
    private static async Task<string> RawProbeAsync(LlamaServer server, byte[] bytes, string mime, string question, CancellationToken ct)
    {
        var req = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:" + mime + ";base64," + Convert.ToBase64String(bytes) } },
                    new JsonObject { ["type"] = "text", ["text"] = question }),
            }),
            ["temperature"] = 0,
            ["max_tokens"] = 120,
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var content = new StringContent(req.ToJsonString(), new UTF8Encoding(false), "application/json");
        using HttpResponseMessage r = await http.PostAsync(new Uri(server.BaseUri, "/v1/chat/completions"), content, ct);
        string body = await r.Content.ReadAsStringAsync(ct);
        if ((int)r.StatusCode != 200) return $"HTTP {(int)r.StatusCode}: {Head(body)}";
        string? answer = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        return $"HTTP 200: 「{Flat(answer ?? "(content なし)")}」";
    }

    private static string Flat(string s) => s.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Head(string s)
    {
        string one = Flat(s);
        return one.Length <= 300 ? one : one[..300] + "…";
    }
}
