using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Models;
using Ohanami.Core.Vlm;

namespace Ohanami.KanaYardstick;

/// <summary>流し方の指定。</summary>
/// <param name="Work">作業フォルダ(manifest.json と images/ がある。results.jsonl などをここに書く)。</param>
/// <param name="LlamaServer">llama-server.exe。</param>
/// <param name="Model">GGUF。</param>
/// <param name="Mmproj">mmproj の GGUF。</param>
/// <param name="Condition">条件の名前(results.jsonl の cond。例: c1)。</param>
/// <param name="ImageMinTokens"><c>--image-min-tokens</c>。null なら付けない(モデルの既定)。</param>
/// <param name="ImageMaxTokens"><c>--image-max-tokens</c>。</param>
/// <param name="ExtraArguments">製品の引数のあとに足す引数(空白で区切る。候補のモデルが別の引数を要るとき)。</param>
/// <param name="Only">画像の id にこの文字列を含むものだけ流す(試し用)。空なら全部。</param>
internal sealed record RunOptions(string Work, string LlamaServer, string Model, string Mmproj, string Condition, int? ImageMinTokens, int ImageMaxTokens, string ExtraArguments, string Only);

/// <summary>
/// 製品と同じ引数で llama-server を起動し(<see cref="LlamaServerArguments.Build"/>。ADR 0019)、製品の <c>VlmClient.BuildRequest</c> と同じ要求
/// (プロンプトとスキーマは <see cref="VlmPrompt"/>・<see cref="VlmSchema"/>)で、画像を 1 枚ずつ流す。再試行(finish_reason = length のときだけ、
/// dry_multiplier 0.8、次に repeat_penalty 1.1。同時には使わない)も製品と同じ。
/// 1 回の実行で 1 つの条件を流す。結果は results.jsonl に 1 枚 1 行で足す(同じ条件・同じ id が済みなら流さない。途中から続けられる)。
/// </summary>
internal static class Runner
{
    private const int MaxTokens = 2048;

    private static readonly JsonSerializerOptions JsonLine = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> RunAsync(RunOptions o, CancellationToken ct)
    {
        string work = Path.GetFullPath(o.Work);
        string prompt = VlmPrompt.Text;
        JsonNode schema = JsonNode.Parse(VlmSchema.Json) ?? throw new InvalidOperationException("スキーマが JSON でない");
        foreach (string f in new[] { o.LlamaServer, o.Model, o.Mmproj })
            if (!File.Exists(f)) throw new FileNotFoundException("ファイルが無い: " + f);

        List<Spec> specs = JsonSerializer.Deserialize<List<Spec>>(File.ReadAllText(Path.Combine(work, "manifest.json"))) ?? throw new InvalidDataException("manifest.json が空");
        if (o.Only.Length > 0) specs = specs.Where(s => s.Id.Contains(o.Only, StringComparison.Ordinal)).ToList();
        string resultsPath = Path.Combine(work, "results.jsonl");
        var done = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(resultsPath))
            foreach (string line in File.ReadLines(resultsPath))
                if (line.Length > 0)
                {
                    JsonNode n = JsonNode.Parse(line) ?? throw new InvalidDataException("results.jsonl の行が JSON でない");
                    done.Add((string)n["cond"]! + "|" + (string)n["id"]!);
                }
        List<Spec> todo = specs.Where(s => !done.Contains(o.Condition + "|" + s.Id)).ToList();
        Console.WriteLine($"[{o.Condition}] 流す枚数 {todo.Count}(済み {specs.Count - todo.Count})");
        if (todo.Count == 0) return 0;

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string logPath = Path.Combine(work, $"server-{o.Condition}-{stamp}.log");
        int port = FreePort();
        var launch = new LlamaServerLaunch(o.LlamaServer, o.Model, o.Mmproj, o.ImageMinTokens, o.ImageMaxTokens, null, work, LlamaServerLaunch.DefaultHealthTimeout);
        List<string> args = LlamaServerArguments.Build(launch, port).ToList();
        args.AddRange(o.ExtraArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // 何で流したかの記録(結果と一緒に残す)。モデルのファイルは大きさだけ(SHA-256 は、候補を比べるときに別に取る)
        File.WriteAllText(Path.Combine(work, $"run-info-{o.Condition}-{stamp}.json"), new JsonObject
        {
            ["condition"] = o.Condition,
            ["started"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["llama_server"] = o.LlamaServer,
            ["llama_server_sha256"] = Sha256Of(o.LlamaServer),
            ["model"] = o.Model,
            ["model_bytes"] = new FileInfo(o.Model).Length,
            ["mmproj"] = o.Mmproj,
            ["mmproj_bytes"] = new FileInfo(o.Mmproj).Length,
            ["arguments"] = new JsonArray(args.Select(a => (JsonNode?)a).ToArray()),
            ["prompt_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))),
            ["max_tokens"] = MaxTokens,
        }.ToJsonString(new JsonSerializerOptions(JsonLine) { WriteIndented = true }), new UTF8Encoding(false));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        Process? server = null;
        try
        {
            server = StartServer(o.LlamaServer, args, logPath);
            var sw0 = Stopwatch.StartNew();
            await WaitHealth(http, port, server, ct);
            Console.WriteLine($"[{o.Condition}] llama-server 起動 {sw0.Elapsed.TotalSeconds:0.0} 秒、ポート {port}、pid {server.Id}、min-tokens={o.ImageMinTokens?.ToString(CultureInfo.InvariantCulture) ?? "既定"}、GPU 全体の使用 {GpuUsed()} MiB");
            var endpoint = new Uri($"http://127.0.0.1:{port}/v1/chat/completions");

            int i = 0;
            foreach (Spec s in todo)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                byte[] bytes = File.ReadAllBytes(Path.Combine(work, "images", s.Id + ".png"));
                JsonObject rec = await OneImage(http, endpoint, bytes, prompt, schema, ct);
                rec["id"] = s.Id;
                rec["cond"] = o.Condition;
                rec["min_tokens"] = o.ImageMinTokens;
                rec["image_bytes"] = bytes.Length;
                rec["vram_used_mib_after"] = GpuUsed();
                File.AppendAllText(resultsPath, rec.ToJsonString(JsonLine) + "\n", new UTF8Encoding(false));
                Console.WriteLine($"[{o.Condition} {i}/{todo.Count}] {s.Id} {(string)rec["status"]!} attempts={rec["attempt_count"]} {((double)rec["wall_ms"]! / 1000).ToString("0.0", CultureInfo.InvariantCulture)}s prompt_tokens={rec["prompt_tokens"]?.ToString() ?? "-"}");
                if (server.HasExited) throw new InvalidOperationException("llama-server が終了した(終了コード " + server.ExitCode + ")");
            }
        }
        finally
        {
            if (server is not null && !server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                server.WaitForExit(10000);
            }
            Console.WriteLine($"[{o.Condition}] llama-server を止めた");
        }
        return 0;
    }

    private static async Task<JsonObject> OneImage(HttpClient http, Uri endpoint, byte[] png, string prompt, JsonNode schema, CancellationToken ct)
    {
        string dataUri = "data:image/png;base64," + Convert.ToBase64String(png);
        var rec = new JsonObject();
        var total = Stopwatch.StartNew();
        var attempts = new JsonArray();
        string status = "failed";
        string? error = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var req = new JsonObject
            {
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUri } },
                        new JsonObject { ["type"] = "text", ["text"] = prompt }),
                }),
                ["temperature"] = 0.0,
                ["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["schema"] = schema.DeepClone() } },
                ["max_tokens"] = MaxTokens,
                ["cache_prompt"] = false,
            };
            if (attempt == 2) req["dry_multiplier"] = 0.8;
            if (attempt == 3) req["repeat_penalty"] = 1.1;

            var sw = Stopwatch.StartNew();
            int code;
            string body;
            try
            {
                using var content = new StringContent(req.ToJsonString(), new UTF8Encoding(false), "application/json");
                using HttpResponseMessage resp = await http.PostAsync(endpoint, content, ct);
                code = (int)resp.StatusCode;
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                ct.ThrowIfCancellationRequested();
                status = "server_failure";
                error = e.GetType().Name + ": " + e.Message;
                attempts.Add(new JsonObject { ["attempt"] = attempt, ["error"] = error, ["wall_ms"] = sw.Elapsed.TotalMilliseconds });
                break;
            }
            var a = new JsonObject { ["attempt"] = attempt, ["http_status"] = code, ["wall_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1) };
            attempts.Add(a);
            if (code != 200)
            {
                status = code >= 500 ? "server_failure" : "failed";
                error = $"HTTP {code}: {(body.Length > 300 ? body[..300] : body)}";
                a["error"] = error;
                break;
            }
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            JsonElement choice = root.GetProperty("choices")[0];
            string? finish = choice.TryGetProperty("finish_reason", out JsonElement fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;
            JsonElement msg = choice.GetProperty("message");
            string? contentText = msg.TryGetProperty("content", out JsonElement ce) && ce.ValueKind == JsonValueKind.String ? ce.GetString() : null;
            bool thinking = msg.TryGetProperty("reasoning_content", out JsonElement rc) && rc.ValueKind == JsonValueKind.String && rc.GetString()!.Length > 0;
            int? promptTokens = root.TryGetProperty("usage", out JsonElement u) && u.TryGetProperty("prompt_tokens", out JsonElement pt) ? pt.GetInt32() : null;
            int? completionTokens = root.TryGetProperty("usage", out JsonElement u2) && u2.TryGetProperty("completion_tokens", out JsonElement ct2) ? ct2.GetInt32() : null;
            a["finish_reason"] = finish;
            a["prompt_tokens"] = promptTokens;
            a["completion_tokens"] = completionTokens;
            if (root.TryGetProperty("timings", out JsonElement tm)) a["timings"] = JsonNode.Parse(tm.GetRawText());
            rec["finish_reason"] = finish;
            rec["prompt_tokens"] = promptTokens;
            rec["completion_tokens"] = completionTokens;
            if (root.TryGetProperty("timings", out JsonElement tm2)) rec["timings"] = JsonNode.Parse(tm2.GetRawText());
            if (thinking) { status = "failed"; error = "思考の痕跡がある"; break; }
            if (finish == "length")
            {
                a["partial_content"] = contentText;   // 再試行の前の出力(繰り返しの疑い)。採点には使わず、別に数える
                if (attempt < 3) continue;
                status = "failed"; error = "finish_reason=length"; break;
            }
            if (finish != "stop") { status = "failed"; error = "finish_reason=" + finish; break; }
            try
            {
                JsonNode? n = JsonNode.Parse(contentText ?? "");
                if (n is not JsonObject obj) throw new FormatException("JSON のオブジェクトでない");
                foreach (var kv in obj)
                    if (kv.Key is not ("ocr" or "description" or "tags")) throw new FormatException("想定外のプロパティ " + kv.Key);
                rec["ocr"] = (string?)obj["ocr"] ?? throw new FormatException("ocr が無い");
                rec["description"] = (string?)obj["description"] ?? throw new FormatException("description が無い");
                rec["tags"] = obj["tags"]?.DeepClone();
                status = "ok";
            }
            catch (Exception e) when (e is FormatException or JsonException or InvalidCastException)
            {
                status = "failed";
                error = "パース失敗: " + e.Message;
                rec["raw_content"] = contentText;
            }
            break;
        }
        rec["status"] = status;
        if (error is not null) rec["error"] = error;
        rec["attempt_count"] = attempts.Count;
        rec["attempts"] = attempts;
        rec["wall_ms"] = Math.Round(total.Elapsed.TotalMilliseconds, 1);
        return rec;
    }

    private static Process StartServer(string exe, IReadOnlyList<string> args, string logPath)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        var log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        File.WriteAllText(logPath + ".cmdline.txt", exe + " " + string.Join(" ", args.Select(x => x.Contains(' ') ? "\"" + x + "\"" : x)) + "\n", new UTF8Encoding(false));
        Process p = Process.Start(psi) ?? throw new InvalidOperationException("llama-server を起動できなかった");
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static async Task WaitHealth(HttpClient http, int port, Process server, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < LlamaServerLaunch.DefaultHealthTimeout)
        {
            ct.ThrowIfCancellationRequested();
            if (server.HasExited) throw new InvalidOperationException("llama-server が起動中に終了した(終了コード " + server.ExitCode + ")");
            try
            {
                using HttpResponseMessage r = await http.GetAsync($"http://127.0.0.1:{port}/health", ct);
                if (r.StatusCode == HttpStatusCode.OK) return;
                if (r.StatusCode != HttpStatusCode.ServiceUnavailable) throw new InvalidOperationException("/health が " + (int)r.StatusCode);
            }
            catch (HttpRequestException) { }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("llama-server の起動待ちがタイムアウト");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>GPU 全体の使用量(MiB)。nvidia-smi が無ければ "?"。ほかのプロセスの分も入る(目安)。</summary>
    private static string GpuUsed()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using Process p = Process.Start(psi)!;
            string s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return s;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return "?"; }
    }

    private static string Sha256Of(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }
}
