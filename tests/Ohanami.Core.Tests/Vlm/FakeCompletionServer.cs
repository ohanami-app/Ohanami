using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>偽の llama-server が返す応答 1 つ。<see cref="Abort"/> は、応答を返さずに接続を切る。<see cref="DelayMs"/> は、返すまでの待ち(タイムアウトの確認)。</summary>
internal sealed record ScriptedResponse(int Status, string Body, int DelayMs = 0, bool Abort = false)
{
    public static ScriptedResponse Json(JsonNode body, int status = 200) => new(status, body.ToJsonString());
}

/// <summary>
/// テストの中で動く、台本どおりに答える偽の <c>/v1/chat/completions</c>(<see cref="HttpListener"/>。実物の HTTP で VlmClient を確かめる)。
/// 来たリクエストの本文とパスを記録し、台本(<see cref="Enqueue"/> した順)の応答を 1 つずつ返す。台本が尽きたら HTTP 500 を返す。
/// </summary>
internal sealed class FakeCompletionServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<ScriptedResponse> _script = new();
    private readonly object _gate = new();
    private readonly List<string> _bodies = [];
    private readonly List<string> _paths = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeCompletionServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public int Port { get; }

    public Uri BaseUri => new($"http://127.0.0.1:{Port}");

    /// <summary>来たリクエストの本文(順)。</summary>
    public IReadOnlyList<string> Bodies { get { lock (_gate) return _bodies.ToArray(); } }

    public IReadOnlyList<string> Paths { get { lock (_gate) return _paths.ToArray(); } }

    public int RequestCount { get { lock (_gate) return _bodies.Count; } }

    public void Enqueue(ScriptedResponse response) => _script.Enqueue(response);

    public JsonObject BodyAsJson(int index) => (JsonObject)JsonNode.Parse(Bodies[index])!;

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _bodies.Add(body);
                _paths.Add(ctx.Request.Url?.AbsolutePath ?? "");
            }

            if (!_script.TryDequeue(out ScriptedResponse? response))
                response = new ScriptedResponse(500, "{\"error\":\"台本が尽きた\"}");
            if (response.DelayMs > 0)
            {
                try { await Task.Delay(response.DelayMs, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { ctx.Response.Abort(); return; }
            }
            if (response.Abort)
            {
                ctx.Response.Abort();
                continue;
            }
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(response.Body);
                ctx.Response.StatusCode = response.Status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                ctx.Response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
            {
                // クライアントが先に切った(タイムアウト・キャンセル)
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Close(); } catch (ObjectDisposedException) { /* もう閉じている */ }
        try { _loop.Wait(2000); } catch (AggregateException) { /* 終了の競合 */ }
        _stop.Dispose();
    }

    // ------------------------------------------------------------------ 応答の組み立て

    /// <summary>成功の応答(finish_reason = stop)。content は、画像 1 枚の出力 {ocr, description, tags}。</summary>
    public static ScriptedResponse Ok(string ocr = "こんにちは", string description = "教室の場面", string[]? tags = null, int promptTokens = 1234, int completionTokens = 80, string finish = "stop") =>
        Completion(finish, new JsonObject
        {
            ["ocr"] = ocr,
            ["description"] = description,
            ["tags"] = new JsonArray((tags ?? ["制服", "教室"]).Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
        }.ToJsonString(), promptTokens, completionTokens);

    /// <summary>content に任意の文字列を入れた応答。</summary>
    public static ScriptedResponse Completion(string finish, string content, int promptTokens = 1234, int completionTokens = 80, JsonObject? messageExtra = null)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (messageExtra is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in messageExtra) message[kv.Key] = kv.Value?.DeepClone();
        }
        var root = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["finish_reason"] = finish, ["message"] = message }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens, ["total_tokens"] = promptTokens + completionTokens },
            ["timings"] = new JsonObject { ["prompt_n"] = promptTokens, ["prompt_ms"] = 321.5, ["predicted_n"] = completionTokens, ["predicted_ms"] = 4567.8 },
        };
        return ScriptedResponse.Json(root);
    }
}
