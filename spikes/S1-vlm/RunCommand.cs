using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace S1Vlm;

/// <summary>llama-server を子プロセスとして起動し、評価セットを1枚ずつ流して記録する。</summary>
public static class RunCommand
{
    public static readonly string[] ValueOptions =
    {
        "server", "model", "mmproj", "extra-args", "label", "samples", "images", "out", "only", "name-contains",
        "prompt", "schema", "request-extra", "max-tokens", "timeout-sec", "health-timeout-sec", "gpu-index",
    };
    public static readonly string[] FlagOptions = { "force", "check-only" };

    private sealed class ImageRun
    {
        public SampleRow Sample = null!;
        public int? HttpStatus;
        public bool ParseOk;
        public double? PromptN, PromptMs, PredictedN, PredictedMs;
        public double WallMs;
        public int? OcrLen, DescLen, TagsN;
        public int PeakVramMiB;
        public double PeakPrivateMiB;
        public double PeakWorkingSetMiB;
        public string? ThinkingTrace; // reasoning_content が空でない、または content に <think> がある場合の説明
    }

    public static async Task<int> ExecuteAsync(Args a)
    {
        // ---- 引数
        string serverExe = a.Require("server");
        string model = a.Require("model");
        string mmproj = a.Require("mmproj");
        string extraArgs = a.GetOr("extra-args", "").Trim();
        string label = a.Require("label");
        string samplesPath = a.GetOr("samples", "samples.tsv");
        string imagesDir = a.GetOr("images", Path.Combine("..", "images"));
        string promptPath = a.GetOr("prompt", "prompt.txt");
        string schemaPath = a.GetOr("schema", "schema.json");
        int maxTokens = a.GetInt("max-tokens", 4096);
        int timeoutSec = a.GetInt("timeout-sec", 1800);
        int healthTimeoutSec = a.GetInt("health-timeout-sec", 900);
        int gpuIndex = a.GetInt("gpu-index", 0);
        bool force = a.Flag("force");

        if (label.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || label.Length == 0)
            throw new ArgumentException($"--label にファイル名に使えない文字がある: '{label}'");
        string outDir = a.GetOr("out", Path.Combine("runs", label));

        foreach (var (name, path) in new[] { ("--server", serverExe), ("--model", model), ("--mmproj", mmproj), ("--prompt", promptPath), ("--schema", schemaPath), ("--samples", samplesPath) })
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"{name} のファイルがない: {path}");
        }
        if (!Directory.Exists(imagesDir)) throw new DirectoryNotFoundException($"--images のディレクトリがない: {imagesDir}");
        if (model.Contains('"') || mmproj.Contains('"') || serverExe.Contains('"'))
            throw new ArgumentException("パスに二重引用符を含められない");
        foreach (string forbidden in new[] { "--port", "--host", "--load-mode", "-lm " })
        {
            if (extraArgs.Contains(forbidden, StringComparison.Ordinal))
                throw new ArgumentException($"--extra-args に {forbidden.Trim()} は指定できない(この tool が決める。mmap を使わない `-lm none` を固定で付ける)");
        }
        if (maxTokens < 0 || timeoutSec <= 0 || healthTimeoutSec <= 0) throw new ArgumentException("--max-tokens / --timeout-sec / --health-timeout-sec の値が不正");

        // ---- 評価セット(絞り込み)と画像の事前検証
        List<SampleRow> all = SamplesTsv.Read(samplesPath);
        List<SampleRow> samples = FilterSamples(all, a.Get("only"), a.Get("name-contains"));
        foreach (SampleRow s in samples)
        {
            string p = Path.Combine(imagesDir, s.Filename);
            if (!File.Exists(p)) throw new FileNotFoundException($"画像がない: {p}");
            VerifyPngSignature(p);
        }

        // ---- プロンプトとスキーマ
        string prompt = File.ReadAllText(promptPath, RunFiles.Utf8NoBom).TrimEnd('\r', '\n');
        if (prompt.Length == 0) throw new InvalidDataException($"{promptPath} が空");
        JsonNode schemaNode = JsonNode.Parse(File.ReadAllText(schemaPath, RunFiles.Utf8NoBom))
            ?? throw new InvalidDataException($"{schemaPath} が null");
        if (schemaNode is not JsonObject) throw new InvalidDataException($"{schemaPath} がオブジェクトでない");

        // ---- リクエストに足す追加フィールド(--request-extra)
        string? requestExtraPath = a.Get("request-extra");
        JsonObject? requestExtra = null;
        if (requestExtraPath != null)
        {
            if (!File.Exists(requestExtraPath)) throw new FileNotFoundException($"--request-extra のファイルがない: {requestExtraPath}");
            requestExtra = LoadRequestExtra(requestExtraPath, maxTokens);
        }

        // ---- 検査だけ(--check-only): サンプル・画像・プロンプト・スキーマ・追加フィールドが全部そろっていることを確かめて終わる。
        // サーバは起動せず、出力ディレクトリも作らない。全件の実行前の点検用。
        if (a.Flag("check-only"))
        {
            long totalBytes = samples.Sum(s => new FileInfo(Path.Combine(imagesDir, s.Filename)).Length);
            Console.WriteLine($"検査 OK: 画像 {samples.Count} 枚(合計 {totalBytes / 1048576.0:0} MiB、全て存在し PNG シグネチャが正しい)/ 層 {samples.Select(s => s.Stratum).Distinct().Count()} 種 / " +
                              $"プロンプト {prompt.Length} 文字 / スキーマ OK / 出力先 {outDir}(未作成)");
            return 0;
        }

        // ---- 出力ディレクトリ
        if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any() && !force)
            throw new InvalidOperationException($"出力ディレクトリが空でない: {outDir}(別の --out を使うか、上書きしてよければ --force。古い画像の JSON は消えず残る)");
        Directory.CreateDirectory(outDir);
        File.Copy(promptPath, Path.Combine(outDir, "prompt.txt"), true);
        File.Copy(schemaPath, Path.Combine(outDir, "schema.json"), true);
        File.Copy(samplesPath, Path.Combine(outDir, "samples.tsv"), true);
        if (requestExtraPath != null) File.Copy(requestExtraPath, Path.Combine(outDir, "request-extra.json"), true);

        // ---- llama-server のバージョン(実行前に保存)
        string versionText = CaptureVersion(serverExe, out int versionExit);
        File.WriteAllText(Path.Combine(outDir, "server-version.txt"),
            $"# {serverExe} --version (exit code {versionExit})\n{versionText}\n", RunFiles.Utf8NoBom);
        if (versionExit != 0)
            throw new InvalidOperationException($"{serverExe} --version が終了コード {versionExit} を返した:\n{versionText}");

        // ---- 起動コマンドライン
        int port = FindFreePort();
        string serverArgs = $"-m \"{Path.GetFullPath(model)}\" --mmproj \"{Path.GetFullPath(mmproj)}\" --host 127.0.0.1 --port {port.ToString(CultureInfo.InvariantCulture)} -lm none";
        if (extraArgs.Length > 0) serverArgs += " " + extraArgs;
        string cmdline = $"\"{Path.GetFullPath(serverExe)}\" {serverArgs}";
        File.WriteAllText(Path.Combine(outDir, "server-cmdline.txt"), cmdline + "\n", RunFiles.Utf8NoBom);

        Console.WriteLine($"label={label} 画像={samples.Count}枚 出力={outDir}");
        Console.WriteLine("llama-server --version: " + versionText.Replace("\n", " | ").Trim());
        Console.WriteLine("起動コマンド: " + cmdline);

        // 実行中のスリープを抑止する(終了時・例外時は using で解除される)。
        using SleepGuard sleepGuard = SleepGuard.Start();
        Console.WriteLine("スリープ抑止: 有効(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)");

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); Console.Error.WriteLine("中断要求を受けた。サーバを止めて終わる..."); };
        Console.CancelKeyPress += onCancel;

        Process? server = null;
        StreamWriter? serverLog = null;
        var runs = new List<ImageRun>();
        int exitCode = 0;
        try
        {
            using var gpu = new GpuMonitor(gpuIndex);
            int vramBaseline = gpu.Current;
            Console.WriteLine($"VRAM ベースライン(llama-server 起動前、GPU全体): {vramBaseline} MiB");

            // ---- 起動
            serverLog = new StreamWriter(Path.Combine(outDir, "server.log"), false, RunFiles.Utf8NoBom) { AutoFlush = true };
            var logLock = new object();
            var tail = new Queue<string>();
            void AppendLog(string stream, string? line)
            {
                if (line == null) return;
                lock (logLock)
                {
                    try { serverLog!.WriteLine($"[{stream}] {line}"); } catch (ObjectDisposedException) { return; }
                    tail.Enqueue(line);
                    while (tail.Count > 40) tail.Dequeue();
                }
            }
            string TailText() { lock (logLock) return string.Join("\n", tail); }

            var psi = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(serverExe),
                Arguments = serverArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var sw0 = Stopwatch.StartNew();
            server = Process.Start(psi) ?? throw new InvalidOperationException("llama-server を起動できなかった");
            server.OutputDataReceived += (_, e) => AppendLog("out", e.Data);
            server.ErrorDataReceived += (_, e) => AppendLog("err", e.Data);
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            // 異常終了時にも子プロセスを残さない
            Process serverRef = server;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => KillQuietly(serverRef);

            using var mem = new PrivateMemoryMonitor(server);

            // ---- /health が 200 になるまで待つ(ロード中は 503)
            string baseUrl = $"http://127.0.0.1:{port}";
            await WaitHealthy(baseUrl, server, healthTimeoutSec, TailText, cts.Token);
            double loadSeconds = sw0.Elapsed.TotalSeconds;
            await Task.Delay(500, cts.Token);
            int vramAfterLoad = gpu.Current;
            Console.WriteLine($"起動完了 {loadSeconds:0.0}秒。VRAM(ロード後の待機時、GPU全体): {vramAfterLoad} MiB(ベースラインとの差 {vramAfterLoad - vramBaseline} MiB)");

            WriteRunInfoPartial(outDir, label, serverExe, model, mmproj, extraArgs, serverArgs, port, samples, a, maxTokens,
                vramBaseline, vramAfterLoad, loadSeconds, server.Id);

            // ---- 画像を流す
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
            string endpoint = baseUrl + "/v1/chat/completions";
            int i = 0;
            foreach (SampleRow s in samples)
            {
                cts.Token.ThrowIfCancellationRequested();
                i++;
                if (server.HasExited)
                    throw new InvalidOperationException($"llama-server が終了している(exit {server.ExitCode})。直近のログ:\n{TailText()}");

                ImageRun r = await RunOneAsync(http, endpoint, s, imagesDir, outDir, prompt, schemaNode, maxTokens, requestExtra, gpu, mem, vramBaseline, cts.Token);
                runs.Add(r);
                Console.WriteLine($"[{i}/{samples.Count}] {s.Stratum,-20} http={(r.HttpStatus?.ToString() ?? "-")} parse={(r.ParseOk ? "ok" : "FAIL")} " +
                                  $"wall={r.WallMs / 1000:0.0}s vram={r.PeakVramMiB}MiB private={r.PeakPrivateMiB:0}MiB {s.Filename}");
                if (!r.ParseOk || r.HttpStatus != 200) exitCode = 2;
                if (r.ThinkingTrace != null)
                    throw new InvalidOperationException($"thinking の痕跡を検出したので中止する({s.Filename}): {r.ThinkingTrace}。enable_thinking が効いていない可能性。この画像の JSON は書き出し済み");
            }

            Console.WriteLine($"全体のピーク: VRAM {gpu.AllPeak} MiB(GPU全体、ロード中を含む)/ llama-server private {mem.AllPeakPrivateMiB:0} MiB / working set {mem.AllPeakWorkingSetMiB:0} MiB(参考)");
            AppendRunInfoPeaks(outDir, gpu.AllPeak, mem.AllPeakPrivateMiB, mem.AllPeakWorkingSetMiB);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("中断した。ここまでの結果は書き出す。");
            exitCode = 130;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            if (server != null) KillQuietly(server);
            // 出力の読み取りが終わるのを少し待ってからログを閉じる
            await Task.Delay(300);
            if (serverLog != null) { try { serverLog.Dispose(); } catch (IOException) { /* ログのクローズ失敗は無視 */ } }
            WriteSummary(Path.Combine(outDir, "summary.tsv"), runs);
        }

        PrintRunSummary(runs);
        return exitCode;
    }

    // ------------------------------------------------------------------ 1枚ぶん

    private static async Task<ImageRun> RunOneAsync(HttpClient http, string endpoint, SampleRow s, string imagesDir, string outDir,
        string prompt, JsonNode schemaNode, int maxTokens, JsonObject? requestExtra, GpuMonitor gpu, PrivateMemoryMonitor mem, int vramBaseline, CancellationToken ct)
    {
        string imgPath = Path.Combine(imagesDir, s.Filename);
        byte[] bytes = await File.ReadAllBytesAsync(imgPath, ct);
        string dataUri = "data:image/png;base64," + Convert.ToBase64String(bytes);

        JsonObject BuildRequest(string imageUrl)
        {
            var req = new JsonObject
            {
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = imageUrl } },
                        new JsonObject { ["type"] = "text", ["text"] = prompt }),
                }),
                ["temperature"] = 0,
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["schema"] = schemaNode.DeepClone() },
                },
            };
            if (maxTokens > 0) req["max_tokens"] = maxTokens;
            if (requestExtra != null)
            {
                // 衝突するキーは LoadRequestExtra で弾いてある。temperature だけは上書きする。
                foreach (var kv in requestExtra) req[kv.Key] = kv.Value?.DeepClone();
            }
            return req;
        }

        string requestJson = BuildRequest(dataUri).ToJsonString();
        JsonObject requestRecord = BuildRequest($"data:image/png;base64,<省略: 元画像 {bytes.Length} バイト>");

        var run = new ImageRun { Sample = s };
        int? httpStatus = null;
        string? responseBody = null;
        string? transportError = null;

        gpu.BeginWindow();
        mem.BeginWindow();
        var sw = Stopwatch.StartNew();
        try
        {
            using var content = new StringContent(requestJson, new UTF8Encoding(false), "application/json");
            using HttpResponseMessage resp = await http.PostAsync(endpoint, content, ct);
            httpStatus = (int)resp.StatusCode;
            responseBody = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            transportError = $"タイムアウト({http.Timeout.TotalSeconds:0}秒)";
        }
        catch (HttpRequestException e)
        {
            transportError = "HttpRequestException: " + e.Message;
        }
        sw.Stop();
        int peakVram = gpu.EndWindow();
        var (peakPrivate, peakWs) = mem.EndWindow();

        // ---- 応答のパース
        string? finishReason = null;
        string? thinkingTrace = null;
        JsonNode? usage = null;
        JsonObject? timings = null;
        bool parseOk = false;
        string? ocr = null, desc = null, parseError = null;
        string[]? tags = null;
        if (transportError != null)
        {
            parseError = transportError;
        }
        else if (httpStatus != 200)
        {
            parseError = $"HTTP {httpStatus}";
        }
        else
        {
            try
            {
                JsonNode? root = JsonNode.Parse(responseBody!);
                if (root is not JsonObject ro) throw new FormatException("応答のルートがオブジェクトでない");
                finishReason = (ro["choices"] as JsonArray)?[0]?["finish_reason"]?.GetValue<string>();
                usage = ro["usage"]?.DeepClone();
                thinkingTrace = DetectThinking(ro);
                timings = ro["timings"]?.DeepClone() as JsonObject;
                (bool allowTags, bool requireTags) = SchemaTagsInfo(schemaNode);
                ContentParse cp = ParseContent(ro, allowTags, requireTags);
                (parseOk, ocr, desc, tags, parseError) = (cp.Ok, cp.Ocr, cp.Desc, cp.Tags, cp.Error);
            }
            catch (Exception e)
            {
                parseOk = false;
                parseError = "応答のパースに失敗: " + e.Message;
            }
        }
        if (parseOk && finishReason != "stop") parseError = $"(注意: finish_reason={finishReason})";

        run.HttpStatus = httpStatus;
        run.ThinkingTrace = thinkingTrace;
        run.ParseOk = parseOk;
        run.PromptN = Num(timings?["prompt_n"]);
        run.PromptMs = Num(timings?["prompt_ms"]);
        run.PredictedN = Num(timings?["predicted_n"]);
        run.PredictedMs = Num(timings?["predicted_ms"]);
        run.WallMs = sw.Elapsed.TotalMilliseconds;
        run.OcrLen = ocr is null ? null : ocr.EnumerateRunes().Count();
        run.DescLen = desc is null ? null : desc.EnumerateRunes().Count();
        run.TagsN = tags?.Length;
        run.PeakVramMiB = peakVram;
        run.PeakPrivateMiB = peakPrivate;
        run.PeakWorkingSetMiB = peakWs;

        var rec = new JsonObject
        {
            ["filename"] = s.Filename,
            ["stratum"] = s.Stratum,
            ["group"] = s.Group,
            ["width"] = s.Width,
            ["height"] = s.Height,
            ["request"] = new JsonObject { ["endpoint"] = endpoint, ["image_bytes"] = bytes.Length, ["body_without_base64"] = requestRecord },
            ["http_status"] = httpStatus,
            ["transport_error"] = transportError,
            ["response_body"] = responseBody,
            ["finish_reason"] = finishReason,
            ["thinking_trace"] = thinkingTrace,
            ["parsed"] = new JsonObject
            {
                ["ok"] = parseOk,
                ["ocr"] = ocr,
                ["description"] = desc,
                ["tags"] = tags is null ? null : new JsonArray(tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                ["error"] = parseError,
            },
            ["timings"] = timings,
            ["timings_missing"] = timings is null ? true : null,
            ["usage"] = usage,
            ["wall_ms"] = Math.Round(run.WallMs, 1),
            ["peak_vram_mib"] = peakVram,
            ["vram_baseline_mib"] = vramBaseline,
            ["peak_private_mib"] = Math.Round(peakPrivate, 1),
            ["peak_working_set_mib"] = Math.Round(peakWs, 1),
        };
        RunFiles.WriteJson(RunFiles.ImageJsonPath(outDir, s), rec);
        return run;
    }

    /// <summary>
    /// --request-extra の JSON(トップレベルのキーをリクエスト本文にそのまま足す)を読む。
    /// messages / response_format(と、--max-tokens を使うときの max_tokens)と同じキーは上書きせずエラー。temperature だけは上書き可。
    /// </summary>
    private static JsonObject LoadRequestExtra(string path, int maxTokens)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path, RunFiles.Utf8NoBom)); }
        catch (JsonException e) { throw new InvalidDataException($"{path} が JSON でない: {e.Message}"); }
        if (node is not JsonObject obj) throw new InvalidDataException($"{path} がオブジェクトでない");
        var reserved = new List<string> { "messages", "response_format" };
        if (maxTokens > 0) reserved.Add("max_tokens");
        foreach (string k in reserved)
        {
            if (obj.ContainsKey(k))
                throw new InvalidDataException($"{path} のキー '{k}' はこの tool が設定するので上書きできない" +
                    (k == "max_tokens" ? "(--max-tokens 0 にすればファイル側の値を使える)" : ""));
        }
        return obj;
    }

    /// <summary>thinking の痕跡: message.reasoning_content が空でない、または content に &lt;think&gt; / &lt;/think&gt; がある。なければ null。</summary>
    private static string? DetectThinking(JsonObject root)
    {
        JsonNode? msg = (root["choices"] as JsonArray)?[0]?["message"];
        var found = new List<string>();
        if (msg?["reasoning_content"] is JsonValue rv && rv.GetValueKind() == JsonValueKind.String && rv.GetValue<string>().Length > 0)
            found.Add($"reasoning_content が空でない({rv.GetValue<string>().Length} 文字)");
        if (msg?["content"] is JsonValue cv && cv.GetValueKind() == JsonValueKind.String)
        {
            string c = cv.GetValue<string>();
            if (c.Contains("<think>", StringComparison.Ordinal) || c.Contains("</think>", StringComparison.Ordinal))
                found.Add("content に <think> タグがある");
        }
        return found.Count == 0 ? null : string.Join(" / ", found);
    }

    /// <summary>応答の choices[0].message.content が {"ocr":string,"description":string} だけであることを確認する。</summary>
    private sealed record ContentParse(bool Ok, string? Ocr, string? Desc, string[]? Tags, string? Error)
    {
        public static ContentParse Fail(string error) => new(false, null, null, null, error);
    }

    /// <summary>
    /// 応答の choices[0].message.content を厳格にパースする。ocr と description は必須の文字列。
    /// tags(文字列の配列)は、スキーマに tags がある(allowTags)ときだけ許し、スキーマの required に入っている(requireTags)ときだけ必須にする。
    /// 上記以外のプロパティ、型違い、欠落は失敗として理由を返す。
    /// </summary>
    private static ContentParse ParseContent(JsonObject root, bool allowTags, bool requireTags)
    {
        if (root["choices"] is not JsonArray choices || choices.Count == 0) return ContentParse.Fail("choices がない");
        JsonNode? contentNode = choices[0]?["message"]?["content"];
        if (contentNode is not JsonValue cv || cv.GetValueKind() != JsonValueKind.String) return ContentParse.Fail("message.content が文字列でない");
        string content = cv.GetValue<string>();
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(content); }
        catch (JsonException e) { return ContentParse.Fail("content が JSON でない: " + e.Message); }
        if (parsed is not JsonObject po) return ContentParse.Fail("content の JSON がオブジェクトでない");
        foreach (var kv in po)
        {
            bool known = kv.Key == "ocr" || kv.Key == "description" || (allowTags && kv.Key == "tags");
            if (!known) return ContentParse.Fail($"想定外のプロパティ '{kv.Key}'");
        }
        string? ocr = StringProp(po, "ocr", out string? e1);
        if (e1 != null) return ContentParse.Fail(e1);
        string? desc = StringProp(po, "description", out string? e2);
        if (e2 != null) return ContentParse.Fail(e2);

        string[]? tags = null;
        if (po.ContainsKey("tags"))
        {
            if (po["tags"] is not JsonArray arr) return ContentParse.Fail("'tags' が配列でない");
            var list = new List<string>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is JsonValue tv && tv.GetValueKind() == JsonValueKind.String) list.Add(tv.GetValue<string>());
                else return ContentParse.Fail($"'tags'[{i}] が文字列でない");
            }
            tags = list.ToArray();
        }
        else if (requireTags)
        {
            return ContentParse.Fail("'tags' がない");
        }
        return new ContentParse(true, ocr, desc, tags, null);
    }

    /// <summary>スキーマの properties に tags があるか / required に tags が入っているか。</summary>
    private static (bool Allow, bool Require) SchemaTagsInfo(JsonNode schema)
    {
        bool allow = schema["properties"] is JsonObject props && props.ContainsKey("tags");
        bool require = false;
        if (schema["required"] is JsonArray req)
        {
            foreach (JsonNode? n in req)
            {
                if (n is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "tags") require = true;
            }
        }
        if (require && !allow) throw new InvalidDataException("スキーマの required に tags があるが properties にない");
        return (allow, require);
    }

    private static string? StringProp(JsonObject o, string key, out string? error)
    {
        error = null;
        if (!o.ContainsKey(key)) { error = $"'{key}' がない"; return null; }
        if (o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
        error = $"'{key}' が文字列でない";
        return null;
    }

    private static double? Num(JsonNode? n)
    {
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
        {
            if (v.TryGetValue(out double d)) return d;
        }
        return null;
    }

    // ------------------------------------------------------------------ サーバの起動・停止

    private static async Task WaitHealthy(string baseUrl, Process server, int timeoutSec, Func<string> tail, CancellationToken ct)
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var sw = Stopwatch.StartNew();
        string last = "(まだ応答なし)";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (server.HasExited)
                throw new InvalidOperationException($"llama-server が起動中に終了した(exit {server.ExitCode})。直近のログ:\n{tail()}");
            try
            {
                using HttpResponseMessage r = await hc.GetAsync(baseUrl + "/health", ct);
                int code = (int)r.StatusCode;
                if (code == 200) return;
                last = $"HTTP {code}";
                if (code != 503) Console.Error.WriteLine($"注意: /health が想定外の {code} を返した(503=ロード中 以外)。待ち続ける");
            }
            catch (HttpRequestException e) { last = "接続できない: " + e.Message; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { last = "/health が5秒でタイムアウト"; }
            if (sw.Elapsed.TotalSeconds > timeoutSec)
                throw new TimeoutException($"/health が {timeoutSec} 秒以内に 200 にならなかった。最後の状態: {last}\n直近のログ:\n{tail()}");
            await Task.Delay(500, ct);
        }
    }

    private static void KillQuietly(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(true);
                p.WaitForExit(10000);
            }
        }
        catch (InvalidOperationException) { /* 既に終了している */ }
        catch (System.ComponentModel.Win32Exception e) { Console.Error.WriteLine("llama-server を kill できなかった: " + e.Message); }
    }

    private static string CaptureVersion(string serverExe, out int exitCode)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(serverExe),
            Arguments = "--version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("llama-server --version を起動できなかった");
        Task<string> so = p.StandardOutput.ReadToEndAsync();
        Task<string> se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60000))
        {
            KillQuietly(p);
            throw new TimeoutException("llama-server --version が60秒で終わらない");
        }
        Task.WaitAll(so, se);
        exitCode = p.ExitCode;
        // version は stdout/stderr のどちらに出るかビルドによるので、両方を保存する
        return ("[stdout]\n" + so.Result.TrimEnd() + "\n[stderr]\n" + se.Result.TrimEnd());
    }

    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private static void VerifyPngSignature(string path)
    {
        byte[] sig = new byte[8];
        using var fs = File.OpenRead(path);
        int n = fs.Read(sig, 0, 8);
        byte[] expected = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (n != 8 || !sig.SequenceEqual(expected)) throw new InvalidDataException($"PNG ではない(シグネチャ不一致): {path}");
    }

    private static List<SampleRow> FilterSamples(List<SampleRow> all, string? only, string? nameContains)
    {
        IEnumerable<SampleRow> q = all;
        if (only != null)
        {
            string[] strata = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (strata.Length == 0) throw new ArgumentException("--only が空");
            var known = new HashSet<string>(all.Select(r => r.Stratum));
            foreach (string st in strata)
            {
                if (!known.Contains(st))
                    throw new ArgumentException($"--only に samples.tsv にない層 '{st}'(ある層: {string.Join(", ", known.OrderBy(x => x, StringComparer.Ordinal))})");
            }
            var set = new HashSet<string>(strata);
            q = q.Where(r => set.Contains(r.Stratum));
        }
        if (nameContains != null)
        {
            q = q.Where(r => r.Filename.Contains(nameContains, StringComparison.Ordinal));
        }
        List<SampleRow> list = q.ToList();
        if (list.Count == 0) throw new ArgumentException("--only / --name-contains の条件に合う画像が0枚");
        return list;
    }

    // ------------------------------------------------------------------ 出力

    private static void WriteRunInfoPartial(string outDir, string label, string serverExe, string model, string mmproj, string extraArgs,
        string serverArgs, int port, List<SampleRow> samples, Args a, int maxTokens, int vramBaseline, int vramAfterLoad, double loadSeconds, int pid)
    {
        FileInfo mi = new(model), pi = new(mmproj);
        var info = new JsonObject
        {
            ["label"] = label,
            ["started_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["server_exe"] = Path.GetFullPath(serverExe),
            ["model"] = new JsonObject { ["path"] = mi.FullName, ["bytes"] = mi.Length, ["last_write_utc"] = mi.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture) },
            ["mmproj"] = new JsonObject { ["path"] = pi.FullName, ["bytes"] = pi.Length, ["last_write_utc"] = pi.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture) },
            ["extra_args"] = extraArgs,
            ["server_args"] = serverArgs,
            ["port"] = port,
            ["server_pid"] = pid,
            ["image_count"] = samples.Count,
            ["only"] = a.Get("only"),
            ["name_contains"] = a.Get("name-contains"),
            ["max_tokens"] = maxTokens > 0 ? maxTokens : null,
            ["request_extra_file"] = a.Get("request-extra") is { } rx ? Path.GetFullPath(rx) : null,
            ["temperature"] = 0,
            ["vram_baseline_mib"] = vramBaseline,
            ["vram_after_load_mib"] = vramAfterLoad,
            ["load_seconds"] = Math.Round(loadSeconds, 1),
            ["note"] = "VRAM は GPU 全体の memory.used(nvidia-smi, 200ms 間隔)。RAM は llama-server の PrivateMemorySize64(200ms 間隔)。サンプリング間隔より短いピークは取れない。",
        };
        RunFiles.WriteJson(Path.Combine(outDir, "run-info.json"), info);
    }

    private static void AppendRunInfoPeaks(string outDir, int allPeakVram, double allPeakPrivate, double allPeakWs)
    {
        string path = Path.Combine(outDir, "run-info.json");
        JsonObject info = (JsonObject)JsonNode.Parse(File.ReadAllText(path, RunFiles.Utf8NoBom))!;
        info["overall_peak_vram_mib"] = allPeakVram;
        info["overall_peak_private_mib"] = Math.Round(allPeakPrivate, 1);
        info["overall_peak_working_set_mib"] = Math.Round(allPeakWs, 1);
        info["finished_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        RunFiles.WriteJson(path, info);
    }

    private static string D(double? v) => v is null ? "" : v.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void WriteSummary(string path, List<ImageRun> runs)
    {
        var sb = new StringBuilder();
        sb.Append("filename\tstratum\tgroup\thttp_status\tparse_ok\tprompt_n\tprompt_ms\tpredicted_n\tpredicted_ms\twall_ms\tocr_len\tdesc_len\tpeak_vram_mib\tpeak_private_mib\tpeak_working_set_mib\ttags_n\n");
        foreach (ImageRun r in runs)
        {
            sb.Append(r.Sample.Filename).Append('\t').Append(r.Sample.Stratum).Append('\t').Append(r.Sample.Group).Append('\t')
              .Append(r.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\t')
              .Append(r.ParseOk ? "true" : "false").Append('\t')
              .Append(D(r.PromptN)).Append('\t').Append(D(r.PromptMs)).Append('\t').Append(D(r.PredictedN)).Append('\t').Append(D(r.PredictedMs)).Append('\t')
              .Append(r.WallMs.ToString("0.#", CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.OcrLen?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\t')
              .Append(r.DescLen?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\t')
              .Append(r.PeakVramMiB.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.PeakPrivateMiB.ToString("0.#", CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.PeakWorkingSetMiB.ToString("0.#", CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.TagsN?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), RunFiles.Utf8NoBom);
    }

    private static void PrintRunSummary(List<ImageRun> runs)
    {
        if (runs.Count == 0) { Console.WriteLine("流した画像が0枚"); return; }
        int failed = runs.Count(r => !r.ParseOk || r.HttpStatus != 200);
        Console.WriteLine($"完了: {runs.Count}枚 / パース成功 {runs.Count(r => r.ParseOk)}枚 / 失敗 {failed}枚");
        Console.WriteLine($"画像ごとのピーク VRAM の最大: {runs.Max(r => r.PeakVramMiB)} MiB(GPU全体)/ private の最大: {runs.Max(r => r.PeakPrivateMiB):0} MiB");
        foreach (ImageRun r in runs.Where(r => !r.ParseOk || r.HttpStatus != 200))
            Console.WriteLine($"  失敗: {r.Sample.Filename} (http={r.HttpStatus?.ToString() ?? "-"})");
    }
}
