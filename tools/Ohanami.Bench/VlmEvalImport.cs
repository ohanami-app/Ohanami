using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.Indexing;
using Ohanami.Core.Models;

namespace Ohanami.Bench;

/// <summary>VLM の評価の run と製品の構成を突き合わせるときの、製品側の値。Core の定数から作る(テストでは値を直接渡す)。</summary>
internal sealed record VlmEvalExpectations(
    string Prompt, string SchemaJson, string GgufName, long GgufSize, string GgufSha256, string MmprojName, long MmprojSize, string MmprojSha256,
    int ServerBuild, string ServerCommit, string LaunchFlags, int ImageMaxTokens, int ProductMaxTokens, string Temperature)
{
    public static VlmEvalExpectations FromCore() => new(
        VlmPrompt.Text, VlmSchema.Json,
        VlmCatalog.Current.Gguf.RelativePath, VlmCatalog.Current.Gguf.Size, VlmCatalog.Current.Gguf.Sha256,
        VlmCatalog.Current.Mmproj.RelativePath, VlmCatalog.Current.Mmproj.Size, VlmCatalog.Current.Mmproj.Sha256,
        VlmCatalog.LlamaServerBuild, VlmCatalog.LlamaServerCommit, VlmCatalog.LaunchFlags,
        VlmCatalog.ImageMaxTokens, 2048, VlmCatalog.Current.Temperature);
}

/// <summary>VLM の評価の run から読んだもの(DB に触らない)。</summary>
internal sealed record VlmEvalRunFiles(string PromptTxt, string SchemaJson, string RunInfoJson, string ServerVersion, IReadOnlyList<VlmEvalImage> Images);

/// <summary>
/// 「製品の VLM の構成が評価と同じである」ことの確認。1 つでも通らなければ取り込まない。
/// 通っても言えないこと(弱い確認、確認していない点)は、結果の <see cref="CheckResult.Weak"/> と <see cref="UnverifiedPoints"/> に出す。
/// </summary>
internal static class VlmEvalChecks
{
    public static IReadOnlyList<CheckResult> Evaluate(VlmEvalRunFiles run, VlmEvalExpectations e, IReadOnlyList<ManifestRow> manifest)
    {
        var results = new List<CheckResult>();
        VlmEvalRun.RunInfo info = VlmEvalRun.ReadRunInfo(run.RunInfoJson);
        IReadOnlyList<VlmEvalImage> images = run.Images;

        // 1. プロンプトとスキーマ(実際に送る文字列)
        string sentPrompt = run.PromptTxt.TrimEnd('\r', '\n');            // 評価(spikes/S1-vlm の RunCommand)が送った文字列と同じにする手順
        string sentSchema = Ohanami.Core.Models.VlmSpec.CanonicalizeSchema(run.SchemaJson);
        string promptHash = Ohanami.Core.Models.VlmSpec.PromptSha256Of(e.Prompt);
        int badText = images.Count(i => i.RequestText != e.Prompt);
        int badSchema = images.Count(i => i.RequestSchemaJson != e.SchemaJson);
        bool p1 = sentPrompt == e.Prompt && sentSchema == e.SchemaJson && badText == 0 && badSchema == 0;
        results.Add(new CheckResult("プロンプトとスキーマ(実際に送る文字列)", p1,
            p1 ? $"prompt.txt・schema.json と、{images.Count} 枚の実際のリクエストの文章・スキーマがすべて製品の定数と一致(prompt.sha256 {promptHash[..12]}…)"
               : $"prompt.txt 一致={sentPrompt == e.Prompt}、schema.json 一致={sentSchema == e.SchemaJson}、リクエストの文章が違う画像 {badText} 枚、スキーマが違う画像 {badSchema} 枚"));

        // 2. モデルのファイル(名前・サイズ + MANIFEST の sha256。VLM の評価の run には sha256 が無いので弱い確認)
        ManifestRow? gguf = manifest.FirstOrDefault(r => r.FileName == e.GgufName && r.Size == e.GgufSize);
        ManifestRow? mmproj = manifest.FirstOrDefault(r => r.FileName == e.MmprojName && r.Size == e.MmprojSize);
        bool p2 = info.ModelFileName == e.GgufName && info.ModelBytes == e.GgufSize && info.MmprojFileName == e.MmprojName && info.MmprojBytes == e.MmprojSize
                  && gguf is { Match: "OK" } && gguf.Sha256 == e.GgufSha256 && mmproj is { Match: "OK" } && mmproj.Sha256 == e.MmprojSha256;
        results.Add(new CheckResult("モデルのファイル(名前・サイズ・MANIFEST の sha256)", p2,
            p2 ? $"{info.ModelFileName}({info.ModelBytes:N0} バイト)と {info.MmprojFileName}({info.MmprojBytes:N0} バイト)が、製品の定数・MANIFEST と一致。" +
                 "**VLM の評価の run に sha256 は無く、同じバイト列だったことまでは言えない(サイズ・名前 + MANIFEST での代用)**"
               : $"run-info: {info.ModelFileName} {info.ModelBytes} / {info.MmprojFileName} {info.MmprojBytes}。期待: {e.GgufName} {e.GgufSize} / {e.MmprojName} {e.MmprojSize}。MANIFEST の行: gguf {(gguf is null ? "なし" : gguf.Match)}、mmproj {(mmproj is null ? "なし" : mmproj.Match)}",
            Weak: true));

        // 3. llama-server のビルド
        bool p3 = run.ServerVersion.Contains($"build {e.ServerBuild}", StringComparison.Ordinal) && run.ServerVersion.Contains($"commit {e.ServerCommit}", StringComparison.Ordinal);
        results.Add(new CheckResult("llama-server のビルド", p3,
            p3 ? $"build {e.ServerBuild}、commit {e.ServerCommit}(server-version.txt)" : "server-version.txt に build " + e.ServerBuild + " / commit " + e.ServerCommit + " が無い: " + run.ServerVersion.ReplaceLineEndings(" ").Trim()));

        // 4. 起動引数(パス・ポート・製品が足す --image-max-tokens / --no-webui を除く)
        string flags = VlmEvalRun.LaunchFlagsOf(info.ServerArgs);
        bool p4 = flags == e.LaunchFlags;
        results.Add(new CheckResult("起動引数", p4, p4 ? $"{flags}(VLM の評価の run の server_args と同じ。製品が足すのは --image-max-tokens と --no-webui だけ=評価には無い)" : $"評価: 「{flags}」、製品: 「{e.LaunchFlags}」"));

        // 5. temperature(run-info と全リクエスト)
        double expectedTemp = double.Parse(e.Temperature, CultureInfo.InvariantCulture);
        int badTemp = images.Count(i => i.Temperature != expectedTemp);
        bool p5 = info.Temperature == expectedTemp && badTemp == 0;
        results.Add(new CheckResult("temperature", p5, p5 ? $"{e.Temperature}(run-info と {images.Count} 枚のリクエストすべて)" : $"run-info {info.Temperature}、違うリクエスト {badTemp} 枚"));

        // 6. 失敗は length だけ。成功した画像の出力は max_tokens(評価は 4096、製品は 2048)に依らない
        var failures = images.Where(i => !i.ParsedOk).ToList();
        int notLength = failures.Count(i => i.FinishReason != "length" || i.HttpStatus != 200);
        int maxCompletion = images.Where(i => i.ParsedOk).Select(i => i.CompletionTokens ?? int.MaxValue).DefaultIfEmpty(0).Max();
        bool p6 = notLength == 0 && maxCompletion < e.ProductMaxTokens;
        results.Add(new CheckResult("失敗の種類と max_tokens", p6,
            p6 ? $"失敗 {failures.Count} 枚はすべて finish_reason=length。成功した画像の出力トークンの最大 {maxCompletion} < 製品の max_tokens {e.ProductMaxTokens}(温度 0 の出力は上限に依らない)。評価の max_tokens は {info.MaxTokens}"
               : $"length 以外の失敗 {notLength} 枚。成功した画像の出力トークンの最大 {maxCompletion}(製品の max_tokens {e.ProductMaxTokens})"));

        // 7. 画像トークンの上限が、どの画像にも効かなかった(prompt_n は文章のトークンを含むので、画像トークンより大きい=安全側の確認)
        int maxPrompt = images.Select(i => i.PromptTokens ?? 0).DefaultIfEmpty(0).Max();
        bool p7 = maxPrompt <= e.ImageMaxTokens;
        results.Add(new CheckResult("画像トークンの上限(--image-max-tokens)", p7,
            p7 ? $"prompt_n の最大 {maxPrompt} ≤ 上限 {e.ImageMaxTokens}。prompt_n は文章のトークンも含むので、画像トークンの最大はこれより小さく、上限は評価のどの画像にも効いていない(厳密な数字は vlm-check の tokens で測る)"
               : $"prompt_n の最大 {maxPrompt} > 上限 {e.ImageMaxTokens}。上限が効いた画像があるかもしれない(取り込めない)"));
        return results;
    }

    /// <summary>通っても確認できていない点(取り込みの出力に必ず書く)。</summary>
    public static IReadOnlyList<string> UnverifiedPoints(bool allPng) =>
    [
        "モデルのファイルが評価のときと同じバイト列だったか(VLM の評価の run に sha256 が無い。サイズ・名前 + MANIFEST での代用)",
        allPng ? "JPEG・WebP の画像は評価に無い(評価の画像はすべて PNG)" : "評価の画像に PNG 以外が含まれる(形式ごとの送り方は vlm-check の formats で確かめる)",
        "製品が足す --no-webui の影響(評価には無い。出力に影響しない見込み=推測。vlm-check の compare で、評価と同じ画像を製品の起動で流して一致を数える)",
        "評価の再試行の run(dry_multiplier / repeat_penalty)は取り込まない(記録の数字は、失敗 16 枚を除いた文書で出したため)",
    ];
}

/// <summary>取り込みの設定。</summary>
internal sealed record ImportOptions(string RunDirectory, RunOptions Run);

/// <summary>
/// 評価の出力を、製品の <see cref="CaptionStore"/> に取り込む(<c>import-s1</c>)。VLM を回さずに、全文検索・融合・索引文書の組み立てを評価の記録の数字と比べるため。
/// 取り込む前に、構成の同一性を <see cref="VlmEvalChecks"/> で確認し、1 つでも違えば止まる。何回実行しても同じ結果(同じ内容は飛ばす)。
/// </summary>
internal static class VlmEvalImporter
{
    public static async Task<int> RunAsync(ImportOptions o, CancellationToken ct)
    {
        string runDir = o.RunDirectory;
        if (!Directory.Exists(runDir)) throw new DirectoryNotFoundException($"VLM の評価の run のフォルダが無い: {runDir}");
        var utf8 = new UTF8Encoding(false, true);
        var files = new VlmEvalRunFiles(
            File.ReadAllText(Path.Combine(runDir, "prompt.txt"), utf8), File.ReadAllText(Path.Combine(runDir, "schema.json"), utf8),
            File.ReadAllText(Path.Combine(runDir, "run-info.json"), utf8), File.ReadAllText(Path.Combine(runDir, "server-version.txt"), utf8),
            VlmEvalRun.ReadImages(runDir));
        Console.WriteLine($"VLM の評価の run: {runDir}({files.Images.Count} 枚: 成功 {files.Images.Count(i => i.ParsedOk)}、失敗 {files.Images.Count(i => !i.ParsedOk)})");

        string manifestPath = FindUpward("models", "MANIFEST.tsv");
        IReadOnlyList<CheckResult> checks = VlmEvalChecks.Evaluate(files, VlmEvalExpectations.FromCore(), ManifestRow.Read(manifestPath));
        Console.WriteLine("構成の確認(製品の VLM = 評価の最終構成):");
        foreach (CheckResult c in checks) Console.WriteLine($"  [{(c.Passed ? "通った" : "**違う**")}{(c.Weak && c.Passed ? "・弱い確認" : "")}] {c.Name}: {c.Detail}");
        if (checks.Any(c => !c.Passed))
            throw new InvalidDataException("製品の VLM の構成が VLM の評価の run と違う。取り込まない(上の「違う」を確かめる)。");

        await using OhanamiCore core = await BenchRunner.OpenCoreAsync(o.Run, ct);
        await BenchRunner.ScanLibraryAsync(core, o.Run, ct);
        OhanamiDatabase db = core.DatabaseForTests;

        // ファイル名 → ハッシュ(NFC。同じファイル名が複数あれば測れない)
        var hashOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var formats = new HashSet<string>(StringComparer.Ordinal);
        using (SqliteConnection read = db.CreateReadConnection())
        using (SqliteCommand cmd = read.CreateCommand())
        {
            cmd.CommandText = "SELECT p.path, p.hash, i.format FROM image_paths p JOIN images i ON i.hash = p.hash WHERE p.missing_since IS NULL";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string name = BenchInputs.Nfc(Path.GetFileName(r.GetString(0)));
                if (!hashOf.TryAdd(name, r.GetString(1))) throw new InvalidDataException($"同じファイル名「{name}」の画像が複数ある。この道具はファイル名で画像を指すので取り込めない。");
                formats.Add(r.GetString(2));
            }
        }
        string[] missing = files.Images.Select(i => i.FileName).Where(n => !hashOf.ContainsKey(n)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"VLM の評価の run の画像のうち、フォルダに無いものが {missing.Length} 枚ある: {string.Join("、", missing.Take(5))}。画像のフォルダが評価のときと同じか確かめる");

        // 規約の確認(索引)、モデルの登録、実行の記録
        await core.FullTextIndex.EnsureAsync(ct);
        VlmSpec spec = VlmCatalog.Current;
        await ModelRegistry.EnsureVlmModelRegisteredAsync(db, VlmCatalog.Name, spec, DateTimeOffset.UtcNow, ct);
        var recorder = new RunRecorder(db, JobKind.Vlm, "bench-import-s1", TimeProvider.System);
        string runName = Path.GetFileName(runDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        await recorder.StartAsync(RuntimeJson(runName, files), ct);
        var store = new CaptionStore(db, core.FullTextIndex);

        int imported = 0, unchanged = 0, failedRecorded = 0, failedUnchanged = 0, withoutIndex = 0;
        foreach (VlmEvalImage image in files.Images)
        {
            string hash = hashOf[image.FileName];
            if (image.ParsedOk)
            {
                CaptionRow? existing;
                using (SqliteConnection read = db.CreateReadConnection()) existing = CaptionStore.Find(read, hash, spec.ModelId);
                if (existing is not null && existing.Ocr == image.Ocr && existing.Description == image.Description && existing.Tags.SequenceEqual(image.Tags!, StringComparer.Ordinal))
                {
                    unchanged++;
                    continue;
                }
                var result = new CaptionResult(hash, spec.ModelId, image.Ocr!, image.Description!, image.Tags!, "stop", 1, SettingsJson(runName, image), image.PromptTokens);
                CaptionSaveOutcome outcome = await store.SaveAsync(recorder.RunId, result, DateTimeOffset.UtcNow, ct);
                if (outcome == CaptionSaveOutcome.Saved) imported++; else withoutIndex++;
            }
            else
            {
                bool already;
                using (SqliteConnection read = db.CreateReadConnection())
                using (SqliteCommand cmd = read.CreateCommand())
                {
                    cmd.CommandText = "SELECT count(*) FROM stage_status WHERE hash = $h AND stage = 'caption' AND status = 'failed' AND model_id = $m";
                    cmd.Parameters.AddWithValue("$h", hash);
                    cmd.Parameters.AddWithValue("$m", spec.ModelId);
                    already = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
                }
                if (already) { failedUnchanged++; continue; }
                await store.SaveFailureAsync(recorder.RunId, hash, spec.ModelId, $"finish_reason={image.FinishReason}(評価の取り込み。{image.Note})", FailureSettingsJson(runName, image), DateTimeOffset.UtcNow, ct);
                failedRecorded++;
            }
        }
        await recorder.EndAsync(ct);

        long captions = Scalar(db, "SELECT count(*) FROM captions WHERE model_id = '" + spec.ModelId + "'");
        long indexed = Scalar(db, "SELECT count(*) FROM search_text");
        Console.WriteLine($"取り込み: 成功の結果 {imported} 枚を書き、同じ内容で飛ばしたもの {unchanged} 枚、失敗として記録 {failedRecorded} 枚(記録済み {failedUnchanged} 枚)、captions は書いたが索引に入れなかった {withoutIndex} 枚");
        Console.WriteLine($"DB: captions {captions} 件(model_id {spec.ModelId})、索引 {indexed} 文書");
        Console.WriteLine("確認していない点:");
        foreach (string p in VlmEvalChecks.UnverifiedPoints(formats.All(f => f == "png"))) Console.WriteLine("  ・" + p);
        return 0;
    }

    private static long Scalar(OhanamiDatabase db, string sql)
    {
        using SqliteConnection read = db.CreateReadConnection();
        using SqliteCommand cmd = read.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    /// <summary>runs.runtime_json: 取り込みであること、取り込み元の run、評価の記録(ビルド・起動引数・max_tokens)。</summary>
    private static string RuntimeJson(string runName, VlmEvalRunFiles files)
    {
        VlmEvalRun.RunInfo info = VlmEvalRun.ReadRunInfo(files.RunInfoJson);
        return $"{{\"source\":\"s1-import\",\"run\":{Json(runName)},\"server_version\":{Json(files.ServerVersion.ReplaceLineEndings(" ").Trim())},\"server_args\":{Json(info.ServerArgs)},\"max_tokens\":{(info.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? "null")}}}";
    }

    /// <summary>captions.settings_json: 取り込みの印と、評価のときの設定(max_tokens・temperature)。</summary>
    private static string SettingsJson(string runName, VlmEvalImage i) =>
        $"{{\"source\":\"s1-import\",\"run\":{Json(runName)},\"max_tokens\":{(i.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? "null")},\"temperature\":{(i.Temperature ?? 0).ToString("0.###", CultureInfo.InvariantCulture)},\"completion_tokens\":{(i.CompletionTokens?.ToString(CultureInfo.InvariantCulture) ?? "null")}}}";

    /// <summary>失敗の stage_status.settings_json: 試した設定の一覧(評価は 1 回だけ試した)。</summary>
    private static string FailureSettingsJson(string runName, VlmEvalImage i) =>
        $"{{\"source\":\"s1-import\",\"run\":{Json(runName)},\"attempts\":[{{\"attempt\":1,\"finish_reason\":{Json(i.FinishReason)},\"max_tokens\":{(i.MaxTokens?.ToString(CultureInfo.InvariantCulture) ?? "null")},\"completion_tokens\":{(i.CompletionTokens?.ToString(CultureInfo.InvariantCulture) ?? "null")}}}]}}";

    /// <summary>実行ファイルから親をたどって、<c>dir/file</c> がある場所を探す(リポジトリの models/MANIFEST.tsv など)。</summary>
    private static string FindUpward(string dir, string file)
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, dir, file);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{dir}/{file} がリポジトリに見つからない(コミット済みのファイル)");
    }
}
