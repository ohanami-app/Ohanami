using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Vlm;

namespace Ohanami.FakeLlamaServer;

/// <summary>
/// 偽の llama-server。本物と同じ引数(<c>--port N</c> など)で起動して、<c>GET /health</c> に答える。動きは環境変数で指定する(子プロセスに渡す):
/// <list type="bullet">
/// <item><c>FAKE_LLAMA_START_DELAY_MS</c>: この時間のあいだ /health は 503(読み込み中)、そのあと 200。既定 0。</item>
/// <item><c>FAKE_LLAMA_NEVER_HEALTHY=1</c>: ずっと 503。</item>
/// <item><c>FAKE_LLAMA_HEALTH_CODE=500</c>: /health が常にこの状態(想定外の状態)。</item>
/// <item><c>FAKE_LLAMA_EXIT_AFTER_MS</c>: この時間のあと、終了コード 3 で終わる(異常終了)。</item>
/// <item><c>FAKE_LLAMA_EXIT_FIRST_MARKER=パス</c>: このファイルが無ければ、作って即座に終了コード 1 で終わる(最初の起動だけ失敗 = ポートの競合の模擬)。</item>
/// <item><c>FAKE_LLAMA_COMPLETIONS</c>: <c>POST /v1/chat/completions</c> の答え方(カンマ区切りで、リクエストごとに 1 つずつ。最後は繰り返す)。ok / length / 400 / 500 / badjson / extra / think / surrogate / hang / crash(<see cref="Complete"/>)。既定は ok。</item>
/// <item><c>FAKE_LLAMA_SLOW_MS</c>(slow の待ち。既定 800)、<c>FAKE_LLAMA_MARKER</c>(crashonce / hangonce が、済んだことを覚えるファイル)。</item>
/// <item><c>FAKE_LLAMA_FAIL_ALWAYS=1</c>: 起動のたびに即座に終了コード 1 で終わる。</item>
/// <item><c>FAKE_LLAMA_VERSION_HANG_MS</c>: <c>--version</c> がこの時間止まってから答える(タイムアウトの確認)。</item>
/// <item><c>FAKE_LLAMA_PRINT_LINES=N</c>: 起動時に標準出力と標準エラーへ N 行ずつ出す。</item>
/// <item><c>FAKE_LLAMA_GRANDCHILD=1</c>: 起動時に、出力のパイプを引き継いだ眠る孫(<c>--sleep</c>)を作り、<c>grandchild pid N</c> を標準出力に出す(親が終わっても、読む側は EOF を受け取れない)。</item>
/// <item><c>FAKE_LLAMA_VERSION_LINE</c>: <c>--version</c> が標準エラーに出す行(既定は本物の形)。<c>FAKE_LLAMA_VERSION_EXIT</c>: その終了コード。</item>
/// </list>
/// 別のモード: <c>--sleep</c>(ずっと眠る)、<c>--guard-parent [--no-job] [--exec 実行ファイル -- 引数...]</c>(ジョブオブジェクトを作って子 <c>--sleep</c> を入れ、<c>child pid</c> を標準出力に出して眠る。
/// テストが親を強制終了して、子が消えること(<c>--no-job</c> では残ること)を確かめる)。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--version")
        {
            int hang = EnvInt("FAKE_LLAMA_VERSION_HANG_MS", 0);
            if (hang > 0) Thread.Sleep(hang);
            Console.Error.WriteLine(Env("FAKE_LLAMA_VERSION_LINE") ?? "version: 0.9.0-dev (build 12110, commit cd959bae9)");
            Console.Error.WriteLine("built with fake for Windows x86_64");
            return int.Parse(Env("FAKE_LLAMA_VERSION_EXIT") ?? "0", CultureInfo.InvariantCulture);
        }
        if (args.Length > 0 && args[0] == "--sleep")
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--guard-parent") return GuardParent(args);

        return Serve(args);
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Env(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static int GuardParent(string[] args)
    {
        bool useJob = Array.IndexOf(args, "--no-job") < 0;
        // --exec <exe> -- <引数...> があれば、眠る子の代わりにそのプログラムを子にする(実物の llama-server で、親の強制終了を確かめる手元のテスト用)
        string childExe = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath が無い");
        var childArgs = new System.Collections.Generic.List<string> { "--sleep" };
        int exec = Array.IndexOf(args, "--exec");
        if (exec >= 0)
        {
            childExe = args[exec + 1];
            int dashes = Array.IndexOf(args, "--");
            childArgs = dashes >= 0 ? new System.Collections.Generic.List<string>(args[(dashes + 1)..]) : [];
        }

        using JobObject? job = useJob ? JobObject.CreateKillOnClose() : null;
        var psi = new ProcessStartInfo(childExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in childArgs) psi.ArgumentList.Add(a);
        using Process child = Process.Start(psi) ?? throw new InvalidOperationException("子を起動できなかった");
        job?.Assign(child);
        Console.WriteLine("child " + child.Id.ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    /// <summary>
    /// <c>POST /v1/chat/completions</c> への答え。<c>FAKE_LLAMA_COMPLETIONS</c>(カンマ区切り)の n 番目のモードで答え、最後のモードは繰り返す。
    /// モード: ok(成功。description に、受け取ったリクエストの追加キー [dry_multiplier / repeat_penalty] を入れる)、length(finish_reason = length)、
    /// 400、500、badjson(200 だが JSON でない)、extra(想定外のプロパティ)、think(思考の痕跡)、surrogate(孤立したサロゲート)、
    /// hang(30 秒止まる)、crash(リクエストの途中でプロセスが終了コード 9 で落ちる)、404、slow(<c>FAKE_LLAMA_SLOW_MS</c> 待ってから ok)、
    /// crashonce / hangonce(<c>FAKE_LLAMA_MARKER</c> のファイルが無いときだけ、1 回落ちる・止まる。再起動したプロセスでは ok)。
    /// </summary>
    private static (int Code, string Body) Complete(string mode, string request, int n, HttpListenerContext ctx)
    {
        string extras = string.Join('+', new[] { "dry_multiplier", "repeat_penalty" }.Where(k => request.Contains("\"" + k + "\":", StringComparison.Ordinal)));
        string Content(string ocr, string desc) => "{\"ocr\":\"" + ocr + "\",\"description\":\"" + desc + "\",\"tags\":[\"a\",\"b\"]}";
        string Wrap(string finish, string content, string extraMessage = "") =>
            "{\"choices\":[{\"index\":0,\"finish_reason\":\"" + finish + "\",\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(content) + extraMessage + "}}]," +
            "\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":50},\"timings\":{\"prompt_n\":1000,\"prompt_ms\":10.5,\"predicted_n\":50,\"predicted_ms\":99.5}}";
        switch (mode)
        {
            case "ok": return (200, Wrap("stop", Content("fake-ocr-" + n.ToString(CultureInfo.InvariantCulture), "extras=" + extras)));
            case "length": return (200, Wrap("length", "{\"ocr\":\"途中で切れ"));
            case "400": return (400, "{\"error\":{\"message\":\"fake: image too large\",\"code\":400}}");
            case "500": return (500, "{\"error\":\"fake boom\"}");
            case "badjson": return (200, "this is not json");
            case "extra": return (200, Wrap("stop", "{\"ocr\":\"a\",\"description\":\"b\",\"tags\":[],\"extra\":1}"));
            case "think": return (200, Wrap("stop", Content("a", "b"), ",\"reasoning_content\":\"考え中\""));
            case "surrogate": return (200, Wrap("stop", "{\"ocr\":\"a\\ud800b\",\"description\":\"d\",\"tags\":[]}"));
            case "hang":
                Thread.Sleep(30_000);
                return (200, Wrap("stop", Content("late", "late")));
            case "404": return (404, "{\"error\":\"fake not found\"}");
            case "slow":
                Thread.Sleep(EnvInt("FAKE_LLAMA_SLOW_MS", 800));
                return (200, Wrap("stop", Content("fake-ocr-" + n.ToString(CultureInfo.InvariantCulture), "extras=" + extras)));
            case "crashonce":   // 最初の 1 回だけ(プロセスをまたいで、マーカーのファイルで覚える)落ちる。再起動のあとは ok
            case "hangonce":    // 最初の 1 回だけ止まる(30 秒)。そのあとは ok
                {
                    string? marker = Env("FAKE_LLAMA_MARKER");
                    if (marker is null) throw new InvalidOperationException("FAKE_LLAMA_MARKER が無い");
                    if (!File.Exists(marker))
                    {
                        File.WriteAllText(marker, mode);
                        if (mode == "crashonce")
                        {
                            Console.Error.WriteLine("fake crash during request (once)");
                            Environment.Exit(9);
                        }
                        Thread.Sleep(30_000);
                    }
                    return (200, Wrap("stop", Content("fake-ocr-" + n.ToString(CultureInfo.InvariantCulture), "extras=" + extras)));
                }
            case "crash":
                Console.Error.WriteLine("fake crash during request");
                Environment.Exit(9);
                return (500, "");
            default: throw new InvalidOperationException("未知の FAKE_LLAMA_COMPLETIONS のモード: " + mode);
        }
    }

    private static int Serve(string[] args)
    {
        string? marker = Env("FAKE_LLAMA_EXIT_FIRST_MARKER");
        if (marker is not null && !File.Exists(marker))
        {
            File.WriteAllText(marker, "first");
            Console.Error.WriteLine("couldn't bind: address already in use (fake first-start failure)");
            return 1;
        }

        // DLL を読めない起動では、何も出力せずに、Windows のローダーの終了コード(0xC0000135 など)で終わる。終了コードは符号つき 10 進で渡す(-1073741515 = 0xC0000135)
        if (Env("FAKE_LLAMA_EXIT_CODE") is { } exitCode) return int.Parse(exitCode, CultureInfo.InvariantCulture);

        if (Env("FAKE_LLAMA_FAIL_ALWAYS") == "1")
        {
            Console.Error.WriteLine("couldn't bind: address already in use (fake permanent failure)");
            return 1;
        }

        int port = 0;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--port") port = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
        }
        if (port == 0)
        {
            Console.Error.WriteLine("--port が無い");
            return 2;
        }

        // CUDA を初期化できない環境の llama-server は、この行を出して、CPU で起動を続ける
        if (Env("FAKE_LLAMA_CUDA_INIT_FAILED") == "1") Console.Error.WriteLine("0.00.120.833 E ggml_cuda_init: failed to initialize CUDA: (null)");

        int lines = EnvInt("FAKE_LLAMA_PRINT_LINES", 0);
        for (int i = 1; i <= lines; i++)
        {
            Console.Out.WriteLine("fake-out-" + i.ToString(CultureInfo.InvariantCulture));
            Console.Error.WriteLine("fake-err-" + i.ToString(CultureInfo.InvariantCulture));
        }
        Console.Out.WriteLine("pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Console.Out.WriteLine("args: " + string.Join(' ', args));
        if (Env("FAKE_LLAMA_GRANDCHILD") == "1")
        {
            // 出力のパイプ(標準出力・標準エラー)を引き継いだまま、親が終わっても眠り続ける孫を作る(リダイレクトしないので、親の標準ハンドルを引き継ぐ)。
            // 親が終わっても、パイプの書き込み側が閉じないので、読む側(製品)は EOF を受け取れない(LogTailAfterExitAsync の上限の確認)
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            psi.ArgumentList.Add("--sleep");
            Process grandchild = Process.Start(psi)!;
            Console.Out.WriteLine("grandchild pid " + grandchild.Id.ToString(CultureInfo.InvariantCulture));
        }
        Console.Out.Flush();

        int startDelay = EnvInt("FAKE_LLAMA_START_DELAY_MS", 0);
        bool never = Env("FAKE_LLAMA_NEVER_HEALTHY") == "1";
        int fixedCode = EnvInt("FAKE_LLAMA_HEALTH_CODE", 0);
        int exitAfter = EnvInt("FAKE_LLAMA_EXIT_AFTER_MS", -1);
        var clock = Stopwatch.StartNew();
        string[] completionModes = (Env("FAKE_LLAMA_COMPLETIONS") ?? "ok").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int completionCount = 0;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
        listener.Start();

        if (exitAfter >= 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(exitAfter);
                Console.Error.WriteLine("fake crash");
                Environment.Exit(3);
            });
        }

        while (true)
        {
            HttpListenerContext ctx = listener.GetContext();
            try
            {
                string path = ctx.Request.Url?.AbsolutePath ?? "";
                int code;
                string body;
                if (path == "/health")
                {
                    if (fixedCode != 0) { code = fixedCode; body = "{\"error\":\"fixed\"}"; }
                    else if (never || clock.ElapsedMilliseconds < startDelay) { code = 503; body = "{\"error\":{\"message\":\"Loading model\",\"code\":503}}"; }
                    else { code = 200; body = "{\"status\":\"ok\"}"; }
                }
                else if (path == "/v1/chat/completions" && ctx.Request.HttpMethod == "POST")
                {
                    string request;
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) request = reader.ReadToEnd();
                    string mode = completionModes[Math.Min(completionCount, completionModes.Length - 1)];
                    completionCount++;
                    (code, body) = Complete(mode, request, completionCount, ctx);
                }
                else
                {
                    code = 404;
                    body = "{}";
                }
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                ctx.Response.Close();
            }
        }
    }
}
