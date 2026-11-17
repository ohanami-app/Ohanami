using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.Models;
using Ohanami.Core.Vlm;
using SkiaSharp;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>VlmClient: リクエストの形、再試行の順序(length → dry → rep)、HTTP 400 / 5xx / 接続エラー / タイムアウトの扱い、厳格なパース。実物の HTTP で、台本どおりに答える偽サーバに対して確かめる。</summary>
public sealed class VlmClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = ThumbnailTestImages.Gradient(64, 48);

    /// <summary>結果の SettingsJson が、CaptionStore が検証する形(最上位がオブジェクト、attempts が 1 つ以上、各要素は attempt ≧ 1)であること。成功・失敗・サーバの異常のどれでも同じ形。</summary>
    private static JsonObject AssertSettingsShape(VlmResult r)
    {
        CaptionStore.CheckFailureSettings(r.SettingsJson, nameof(r));   // 形が違えば ArgumentException
        JsonObject settings = (JsonObject)JsonNode.Parse(r.SettingsJson)!;
        Assert.Equal(r.Attempt, (int)settings["attempt"]!);
        Assert.Equal(Enumerable.Range(1, r.Attempt).ToArray(), settings["attempts"]!.AsArray().Select(a => (int)a!["attempt"]!).ToArray()); // 試行の順
        return settings;
    }

    private static ScriptedResponse Length() => FakeCompletionServer.Completion("length", "{\"ocr\":\"途中で切れ", completionTokens: 2048);

    // ---------------------------------------------------------------- リクエストの形

    [Fact]
    public async Task The_request_has_the_evaluation_shape_and_a_stop_response_is_a_success_on_the_first_attempt()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(FakeCompletionServer.Ok(ocr: "セリフ", description: "説明", tags: ["海", "夜"], promptTokens: 1500));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Ok, r.Outcome);
        Assert.Equal(1, r.Attempt);
        Assert.Equal("stop", r.FinishReason);
        Assert.Equal(1500, r.PromptTokens);
        Assert.Null(r.Error);
        Assert.Equal(("セリフ", "説明"), (r.Caption!.Ocr, r.Caption.Description));
        Assert.Equal(["海", "夜"], r.Caption.Tags);

        Assert.Equal(["/v1/chat/completions"], server.Paths);
        JsonObject req = server.BodyAsJson(0);
        Assert.Equal(["messages", "temperature", "response_format", "max_tokens", "cache_prompt"], req.Select(kv => kv.Key).ToArray()); // 評価のコードと同じ並び + cache_prompt(追加のキーはこれだけ。ADR 0024)
        Assert.False((bool)req["cache_prompt"]!);
        JsonArray content = req["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("user", (string)req["messages"]![0]!["role"]!);
        Assert.Equal("image_url", (string)content[0]!["type"]!);
        string url = (string)content[0]!["image_url"]!["url"]!;
        Assert.StartsWith("data:image/png;base64,", url, StringComparison.Ordinal);
        Assert.Equal(Png, Convert.FromBase64String(url["data:image/png;base64,".Length..]));
        Assert.Equal("text", (string)content[1]!["type"]!);
        Assert.Equal(VlmPrompt.Text, (string)content[1]!["text"]!);
        Assert.Equal(0, (int)req["temperature"]!);
        Assert.Equal(2048, (int)req["max_tokens"]!);
        JsonNode format = req["response_format"]!;
        Assert.Equal("json_schema", (string)format["type"]!);
        Assert.Equal(new[] { "schema" }, format["json_schema"]!.AsObject().Select(kv => kv.Key).ToArray()); // name / strict は付けない(評価のコードと同じ)
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(VlmSchema.Json), format["json_schema"]!["schema"]));
    }

    [Fact]
    public async Task The_settings_record_the_attempt_and_the_timings_and_the_image()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(FakeCompletionServer.Ok());
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        JsonObject settings = AssertSettingsShape(r);
        Assert.Equal(2048, (int)settings["max_tokens"]!);
        Assert.Equal(0, (double)settings["temperature"]!);
        Assert.False((bool)settings["cache_prompt"]!);               // 結果ごとの設定にも記録する(古い記録には無い = 既定の true で作られた)
        Assert.Null(settings["dry_multiplier"]);
        Assert.Equal("image/png", (string)settings["image"]!["mime"]!);
        Assert.Equal(Png.Length, (int)settings["image"]!["bytes"]!);
        Assert.Null(settings["image"]!["converted_from"]);   // 形式の変換は無い(PNG・JPEG・WebP とも、そのまま送る)
        Assert.Equal(1234, (int)settings["timings"]!["prompt_n"]!);
        Assert.Equal(80, (int)settings["timings"]!["predicted_n"]!);
        JsonArray attempts = settings["attempts"]!.AsArray();
        Assert.Single(attempts);
        Assert.Equal("stop", (string)attempts[0]!["finish_reason"]!);
        Assert.Equal(200, (int)attempts[0]!["http_status"]!);
        Assert.Equal(1, (int)attempts[0]!["attempt"]!);
        Assert.Equal(2048, (int)attempts[0]!["max_tokens"]!);       // その試行のリクエストの設定は、llama-server のリクエストと同じ名前
        Assert.False((bool)attempts[0]!["cache_prompt"]!);
        Assert.Null(attempts[0]!["error"]);
    }

    [Fact]
    public async Task Jpeg_is_sent_as_is_with_its_own_mime_type()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(FakeCompletionServer.Ok());
        using var client = new VlmClient(server.BaseUri);
        byte[] jpeg = ThumbnailTestImages.Gradient(64, 48, SKEncodedImageFormat.Jpeg, 90);

        VlmResult r = await client.CaptionAsync(jpeg, ImageFormat.Jpeg, Ct);

        Assert.Equal(VlmOutcome.Ok, r.Outcome);
        string url = (string)server.BodyAsJson(0)["messages"]![0]!["content"]![0]!["image_url"]!["url"]!;
        Assert.StartsWith("data:image/jpeg;base64,", url, StringComparison.Ordinal);
        Assert.Equal(jpeg, Convert.FromBase64String(url["data:image/jpeg;base64,".Length..]));
    }

    [Theory]
    [InlineData(true)]    // 非可逆
    [InlineData(false)]   // 可逆
    public async Task Webp_is_sent_as_is_with_its_own_mime_type_like_png_and_jpeg(bool lossy)
    {
        // 実物の確認(ADR 0025): llama-server b12110 は WebP をそのまま読める。Skia で PNG にすると、完全に透明な画素の RGB が 0(黒)になり、PNG をそのまま送る場合と食い違う
        using var server = new FakeCompletionServer();
        server.Enqueue(FakeCompletionServer.Ok());
        using var client = new VlmClient(server.BaseUri);
        byte[] webp = ThumbnailTestImages.Gradient(64, 48, SKEncodedImageFormat.Webp, lossy ? 90 : 100);

        VlmResult r = await client.CaptionAsync(webp, ImageFormat.Webp, Ct);

        Assert.Equal(VlmOutcome.Ok, r.Outcome);
        string url = (string)server.BodyAsJson(0)["messages"]![0]!["content"]![0]!["image_url"]!["url"]!;
        Assert.StartsWith("data:image/webp;base64,", url, StringComparison.Ordinal);
        Assert.Equal(webp, Convert.FromBase64String(url["data:image/webp;base64,".Length..]));   // バイト列は一切変えない
        Assert.Equal("image/webp", (string)JsonNode.Parse(r.SettingsJson)!["image"]!["mime"]!);
    }

    [Fact]
    public void A_webp_with_fully_transparent_pixels_keeps_its_bytes_so_the_hidden_rgb_is_not_zeroed_by_a_reencode()
    {
        // 完全に透明な画素(アルファ 0)の RGB は、PNG をそのまま送るときは保たれる。WebP も同じにする(Skia の事前乗算の変換は、これを 0 にしてしまう)
        using var bitmap = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(new SKColor(200, 100, 50, 0));
        using var image = SKImage.FromBitmap(bitmap);
        byte[] webp = image.Encode(SKEncodedImageFormat.Webp, 100).ToArray();

        VlmImagePayload payload = VlmImage.Prepare(webp, ImageFormat.Webp);

        Assert.Same(webp, payload.Bytes);   // 同じバイト列(コピーも変換もしない)
        Assert.Equal("image/webp", payload.Mime);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_empty_file_or_a_broken_webp_fails_the_image_without_calling_the_server(bool undecodableWebp)
    {
        using var server = new FakeCompletionServer();
        using var client = new VlmClient(server.BaseUri);
        ImageFormat format = undecodableWebp ? ImageFormat.Webp : ImageFormat.Png;
        byte[] bytes = undecodableWebp ? new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } : [];

        VlmResult r = await client.CaptionAsync(bytes, format, Ct);

        Assert.Equal(VlmOutcome.Failed, r.Outcome);
        Assert.Contains("画像を送れない", r.Error, StringComparison.Ordinal);
        Assert.Equal(0, server.RequestCount);
    }

    // ---------------------------------------------------------------- 再試行(length → dry → rep)

    [Fact]
    public async Task A_length_response_is_retried_with_dry_multiplier_and_the_result_records_attempt_2()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(Length());
        server.Enqueue(FakeCompletionServer.Ok(ocr: "二度目"));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Ok, r.Outcome);
        Assert.Equal(2, r.Attempt);
        Assert.Equal("二度目", r.Caption!.Ocr);
        Assert.Equal(2, server.RequestCount);
        Assert.Null(server.BodyAsJson(0)["dry_multiplier"]);
        Assert.Equal(0.8, (double)server.BodyAsJson(1)["dry_multiplier"]!);
        Assert.Null(server.BodyAsJson(1)["repeat_penalty"]);
        JsonObject settings = AssertSettingsShape(r);
        Assert.Equal(0.8, (double)settings["dry_multiplier"]!);
        JsonArray attempts = settings["attempts"]!.AsArray();
        Assert.Equal(["length", "stop"], attempts.Select(a => (string)a!["finish_reason"]!).ToArray());
        Assert.Equal(2048, (int)attempts[0]!["completion_tokens"]!);
        Assert.Null(attempts[0]!["dry_multiplier"]);             // 試行ごとのリクエストの設定: 1 回目は無し、2 回目は dry_multiplier
        Assert.Equal(0.8, (double)attempts[1]!["dry_multiplier"]!);
    }

    [Fact]
    public async Task A_second_length_response_is_retried_with_repeat_penalty_alone()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(Length());
        server.Enqueue(Length());
        server.Enqueue(FakeCompletionServer.Ok(ocr: "三度目"));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Ok, r.Outcome);
        Assert.Equal(3, r.Attempt);
        Assert.Null(server.BodyAsJson(2)["dry_multiplier"]); // 2 つを同時には使わない
        Assert.Equal(1.1, (double)server.BodyAsJson(2)["repeat_penalty"]!);
        JsonObject settings = AssertSettingsShape(r);
        Assert.Equal(1.1, (double)settings["repeat_penalty"]!);
        Assert.Null(settings["dry_multiplier"]);
        Assert.Equal(1.1, (double)settings["attempts"]![2]!["repeat_penalty"]!);
    }

    [Fact]
    public async Task Three_length_responses_fail_the_image_and_record_all_three_attempts()
    {
        using var server = new FakeCompletionServer();
        for (int i = 0; i < 3; i++) server.Enqueue(Length());
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Failed, r.Outcome);
        Assert.Equal(3, r.Attempt);
        Assert.Equal("finish_reason=length", r.Error);
        Assert.Equal("length", r.FinishReason);
        Assert.Null(r.Caption);
        Assert.Equal(3, server.RequestCount); // 4 回目は無い
        Assert.Equal(3, AssertSettingsShape(r)["attempts"]!.AsArray().Count);
    }

    [Fact]
    public async Task Every_attempt_sends_cache_prompt_false_and_records_it_in_success_and_failure()
    {
        // ADR 0024: 同じ画像の 1 回目の直後の再試行でプロンプト全体がキャッシュから再利用されると、出力が変わる。全試行で cache_prompt=false にして、直前の処理に左右されないようにする
        using var server = new FakeCompletionServer();
        server.Enqueue(Length());
        server.Enqueue(Length());
        server.Enqueue(FakeCompletionServer.Ok(ocr: "三度目"));
        for (int i = 0; i < 3; i++) server.Enqueue(Length());
        server.Enqueue(new ScriptedResponse(400, "{\"error\":\"too large\"}"));
        using var client = new VlmClient(server.BaseUri);

        VlmResult success = await client.CaptionAsync(Png, ImageFormat.Png, Ct);
        VlmResult failure = await client.CaptionAsync(Png, ImageFormat.Png, Ct);
        VlmResult bad = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(7, server.RequestCount);
        for (int i = 0; i < 7; i++)
        {
            JsonObject req = server.BodyAsJson(i);
            Assert.True(req.ContainsKey("cache_prompt"), $"リクエスト {i} に cache_prompt が無い");
            Assert.False((bool)req["cache_prompt"]!, $"リクエスト {i} の cache_prompt が false でない");
        }
        foreach (VlmResult r in new[] { success, failure, bad })
        {
            JsonObject settings = AssertSettingsShape(r);
            Assert.False((bool)settings["cache_prompt"]!);
            foreach (JsonNode? a in settings["attempts"]!.AsArray()) Assert.False((bool)a!["cache_prompt"]!);
        }
        Assert.Equal(VlmOutcome.Ok, success.Outcome);
        Assert.Equal(VlmOutcome.Failed, failure.Outcome);
        Assert.Equal(VlmOutcome.Failed, bad.Outcome);
    }

    [Fact]
    public void Old_settings_without_cache_prompt_are_still_accepted_by_the_caption_store_checks()
    {
        // cache_prompt を送る前の記録(cache_prompt が無い。既定の true で作られた)も、CaptionStore の検証を通る(検証は attempts の attempt だけを見る)
        CaptionStore.CheckFailureSettings("{\"attempts\":[{\"attempt\":1,\"max_tokens\":2048,\"temperature\":0,\"finish_reason\":\"length\"}]}", "json");
        CaptionStore.CheckFailureSettings("{\"max_tokens\":2048,\"temperature\":0,\"attempt\":2,\"attempts\":[{\"attempt\":1},{\"attempt\":2}]}", "json");
    }

    [Fact]
    public async Task A_length_response_is_never_a_success_even_if_its_content_happens_to_be_valid_json()
    {
        using var server = new FakeCompletionServer();
        for (int i = 0; i < 3; i++) server.Enqueue(FakeCompletionServer.Ok(finish: "length"));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Failed, r.Outcome);
        Assert.Equal("finish_reason=length", r.Error);
    }

    // ---------------------------------------------------------------- HTTP の状態

    [Fact]
    public async Task Http_400_fails_the_image_without_any_retry()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(new ScriptedResponse(400, "{\"error\":{\"message\":\"image too large for context\",\"code\":400}}"));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Failed, r.Outcome);
        Assert.Contains("HTTP 400", r.Error, StringComparison.Ordinal);
        Assert.Contains("image too large", r.Error, StringComparison.Ordinal);
        Assert.Equal(1, server.RequestCount);
        JsonNode attempt0 = AssertSettingsShape(r)["attempts"]![0]!;
        Assert.Equal(400, (int)attempt0["http_status"]!);
        Assert.Contains("HTTP 400", (string)attempt0["error"]!, StringComparison.Ordinal);   // 応答が成功しなかった理由は error
    }

    [Fact]
    public async Task Http_400_after_a_length_retry_is_still_a_failure_on_that_attempt()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(Length());
        server.Enqueue(new ScriptedResponse(400, "{\"error\":\"bad\"}"));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Failed, r.Outcome);
        Assert.Equal(2, r.Attempt);
        Assert.Equal(2, server.RequestCount);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Http_5xx_means_the_server_is_broken(int status)
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(new ScriptedResponse(status, "{\"error\":\"boom\"}"));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.ServerFailure, r.Outcome);
        Assert.Contains("HTTP " + status, r.Error, StringComparison.Ordinal);
        Assert.Equal(1, server.RequestCount); // 再起動とやり直しは呼び出し側(クライアントは再試行しない)
        Assert.Contains("HTTP " + status, (string)AssertSettingsShape(r)["attempts"]![0]!["error"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dropped_connection_a_dead_server_and_a_broken_body_are_server_failures()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(new ScriptedResponse(200, "", Abort: true));              // 応答を返さず切る
        server.Enqueue(new ScriptedResponse(200, "this is not json"));           // 200 だが JSON でない
        using var client = new VlmClient(server.BaseUri);

        VlmResult dropped = await client.CaptionAsync(Png, ImageFormat.Png, Ct);
        VlmResult broken = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.ServerFailure, dropped.Outcome);
        Assert.Equal(VlmOutcome.ServerFailure, broken.Outcome);
        Assert.Contains("JSON でない", broken.Error, StringComparison.Ordinal);

        VlmClient deadClient;
        using (var gone = new FakeCompletionServer()) { deadClient = new VlmClient(gone.BaseUri); }  // 止めたサーバ(接続できない)
        using (deadClient)
        {
            VlmResult dead = await deadClient.CaptionAsync(Png, ImageFormat.Png, Ct);
            Assert.Equal(VlmOutcome.ServerFailure, dead.Outcome);
        }
    }

    [Fact]
    public async Task A_request_that_takes_longer_than_the_timeout_is_a_server_failure_not_a_cancellation()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(new ScriptedResponse(200, "{}", DelayMs: 5000));
        using var client = new VlmClient(server.BaseUri, TimeSpan.FromMilliseconds(500));

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.ServerFailure, r.Outcome);
        Assert.Contains("タイムアウト", r.Error, StringComparison.Ordinal);
        Assert.Contains("タイムアウト", (string)AssertSettingsShape(r)["attempts"]![0]!["error"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_aborts_the_request_with_an_operation_canceled_exception()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(new ScriptedResponse(200, "{}", DelayMs: 10_000));
        using var client = new VlmClient(server.BaseUri);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(300);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CaptionAsync(Png, ImageFormat.Png, cts.Token));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    public async Task Other_statuses_are_a_protocol_error_that_stops_the_job_instead_of_failing_every_image(int status)
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(new ScriptedResponse(status, "{\"error\":\"x\"}"));
        using var client = new VlmClient(server.BaseUri);

        VlmProtocolException ex = await Assert.ThrowsAsync<VlmProtocolException>(() => client.CaptionAsync(Png, ImageFormat.Png, Ct));

        Assert.Contains("HTTP " + status, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 厳格なパース

    [Theory]
    [InlineData("not json at all", "content が JSON でない")]
    [InlineData("[]", "オブジェクトでない")]
    [InlineData("{\"ocr\":\"a\",\"description\":\"b\",\"tags\":[],\"extra\":1}", "想定外のプロパティ 'extra'")]
    [InlineData("{\"description\":\"b\",\"tags\":[]}", "'ocr' がない")]
    [InlineData("{\"ocr\":\"a\",\"tags\":[]}", "'description' がない")]
    [InlineData("{\"ocr\":\"a\",\"description\":\"b\"}", "'tags' がない")]
    [InlineData("{\"ocr\":1,\"description\":\"b\",\"tags\":[]}", "'ocr' が文字列でない")]
    [InlineData("{\"ocr\":\"a\",\"description\":null,\"tags\":[]}", "'description' が文字列でない")]
    [InlineData("{\"ocr\":\"a\",\"description\":\"b\",\"tags\":\"x\"}", "'tags' が配列でない")]
    [InlineData("{\"ocr\":\"a\",\"description\":\"b\",\"tags\":[\"x\",2]}", "'tags'[1] が文字列でない")]
    public async Task A_stop_response_that_does_not_parse_strictly_fails_the_image_without_retry(string content, string expectedError)
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(FakeCompletionServer.Completion("stop", content));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Failed, r.Outcome);
        Assert.Contains("応答のパースに失敗", r.Error, StringComparison.Ordinal);
        Assert.Contains(expectedError, r.Error, StringComparison.Ordinal);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task A_thinking_trace_fails_the_image_because_it_signals_a_misconfiguration()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(FakeCompletionServer.Completion("stop", "{\"ocr\":\"\",\"description\":\"d\",\"tags\":[]}", messageExtra: new JsonObject { ["reasoning_content"] = "考え中…" }));
        server.Enqueue(FakeCompletionServer.Completion("stop", "<think>…</think>{\"ocr\":\"\",\"description\":\"d\",\"tags\":[]}"));
        using var client = new VlmClient(server.BaseUri);

        VlmResult viaField = await client.CaptionAsync(Png, ImageFormat.Png, Ct);
        VlmResult viaTag = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Failed, viaField.Outcome);
        Assert.Contains("思考の痕跡", viaField.Error, StringComparison.Ordinal);
        Assert.Contains("reasoning_content", viaField.Error, StringComparison.Ordinal);
        Assert.Equal(VlmOutcome.Failed, viaTag.Outcome);
        Assert.Contains("<think>", viaTag.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tool_calls")]
    [InlineData("content_filter")]
    public async Task A_finish_reason_other_than_stop_or_length_fails_the_image(string finish)
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(FakeCompletionServer.Ok(finish: finish));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Failed, r.Outcome);
        Assert.Contains("finish_reason=" + finish, r.Error, StringComparison.Ordinal);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task A_response_without_choices_fails_the_image()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(ScriptedResponse.Json(new JsonObject { ["usage"] = new JsonObject() }));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Failed, r.Outcome);
        Assert.Contains("choices がない", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strings_are_kept_exactly_and_empty_values_are_valid()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(FakeCompletionServer.Ok(ocr: "  先頭と末尾の空白\n改行 ＡＢＣ\u0000制御  ", description: "", tags: []));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Ok, r.Outcome);
        Assert.Equal("  先頭と末尾の空白\n改行 ＡＢＣ\u0000制御  ", r.Caption!.Ocr); // trim も正規化もしない
        Assert.Equal("", r.Caption.Description);
        Assert.Empty(r.Caption.Tags);
    }

    [Fact]
    public async Task A_long_description_and_many_tags_are_kept_and_only_noted_as_warnings()
    {
        using var server = new FakeCompletionServer();
        string[] tags = Enumerable.Range(1, 16).Select(i => "タグ" + i).ToArray();
        server.Enqueue(FakeCompletionServer.Ok(description: new string('あ', 130), tags: tags));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Ok, r.Outcome);
        Assert.Equal(130, r.Caption!.Description.Length);
        Assert.Equal(16, r.Caption.Tags.Count);
        string[] warnings = JsonNode.Parse(r.SettingsJson)!["warnings"]!.AsArray().Select(w => (string)w!).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains("130 字", warnings[0], StringComparison.Ordinal);
        Assert.Contains("16 個", warnings[1], StringComparison.Ordinal);
    }


    // ---------------------------------------------------------------- 孤立したサロゲート

    [Theory]
    [InlineData("ocr")]
    [InlineData("description")]
    [InlineData("tags")]
    public async Task A_lone_surrogate_in_the_model_output_fails_the_image_as_a_response_parse_failure_that_can_be_counted(string where)
    {
        // VLM の出力(content の JSON)に、孤立したサロゲートのエスケープ(バックスラッシュ + ud800)が入った。System.Text.Json は、それを GetString すると例外にする
        string ocr = where == "ocr" ? "a\\ud800b" : "a";
        string desc = where == "description" ? "x\\ud800" : "x";
        string tag = where == "tags" ? "\\udfff" : "t";   // 孤立した低位サロゲート
        string content = "{\"ocr\":\"" + ocr + "\",\"description\":\"" + desc + "\",\"tags\":[\"" + tag + "\"]}";
        using var server = new FakeCompletionServer();
        server.Enqueue(FakeCompletionServer.Completion("stop", content));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Failed, r.Outcome);
        Assert.Contains(VlmResponse.SurrogateErrorPrefix, r.Error, StringComparison.Ordinal); // 固定の文字列なので、集計で数えられる
        Assert.Equal(1, server.RequestCount);                                                  // 再試行しない(同じ出力になる)
        AssertSettingsShape(r);
    }

    [Fact]
    public async Task A_lone_surrogate_directly_in_the_response_body_is_also_a_countable_parse_failure()
    {
        // 応答の content そのものが、孤立したサロゲートのエスケープを含む(JSON の本文としては正しい)
        string raw = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"\\ud800\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":1}}";
        using var server = new FakeCompletionServer();
        server.Enqueue(new ScriptedResponse(200, raw));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Failed, r.Outcome);
        Assert.Contains(VlmResponse.SurrogateErrorPrefix, r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_valid_surrogate_pair_such_as_an_emoji_is_kept()
    {
        using var server = new FakeCompletionServer();
        server.Enqueue(FakeCompletionServer.Ok(ocr: "笑顔😀", description: "𠮷野家", tags: ["😀"]));
        using var client = new VlmClient(server.BaseUri);

        VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

        Assert.Equal(VlmOutcome.Ok, r.Outcome);
        Assert.Equal(("笑顔😀", "𠮷野家"), (r.Caption!.Ocr, r.Caption.Description));
        Assert.Equal(["😀"], r.Caption.Tags);
    }

    // ---------------------------------------------------------------- 応答の解釈(単体)

    [Fact]
    public void Response_parsing_reads_usage_timings_and_rejects_a_broken_body()
    {
        string body = FakeCompletionServer.Ok(promptTokens: 777, completionTokens: 55).Body;

        VlmResponseInfo info = VlmResponse.Parse(body);

        Assert.Equal("stop", info.FinishReason);
        Assert.Equal((777, 55), (info.PromptTokens, info.CompletionTokens));
        Assert.Equal(777, (int)info.Timings!["prompt_n"]!);
        Assert.NotNull(info.Caption);
        Assert.ThrowsAny<JsonException>(() => VlmResponse.Parse("{not json"));
        Assert.ThrowsAny<JsonException>(() => VlmResponse.Parse("[]"));
    }
}
