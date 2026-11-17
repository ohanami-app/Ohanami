using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Data;
using Ohanami.Core.Models;

namespace Ohanami.Core.Vlm;

/// <summary>1 枚の処理の結果の種類。</summary>
internal enum VlmOutcome
{
    /// <summary>成功(<c>finish_reason = stop</c> でパースに成功)。</summary>
    Ok,

    /// <summary>その画像は失敗として記録する(再試行しても変わらない・試行を使い切った)。<see cref="VlmResult.Error"/> に理由。サーバは正常。</summary>
    Failed,

    /// <summary>llama-server が壊れたとみなす(HTTP 5xx、接続エラー、応答の読み取りの失敗、タイムアウト)。呼び出し側(VlmRunner)が kill → 起動し直し → 同じ画像をやり直す。</summary>
    ServerFailure,
}

/// <summary>
/// <see cref="VlmClient.CaptionAsync"/> の結果。<see cref="SettingsJson"/> は、この結果(成功なら captions.settings_json、失敗なら stage_status.settings_json)に残す設定と、
/// 試行ごとの記録(JSON のオブジェクト)。
/// </summary>
/// <param name="Outcome">結果の種類。</param>
/// <param name="Caption">成功のときだけ。</param>
/// <param name="Attempt">何回目の試行の結果か(成功なら成功した回、失敗なら試した回数。1 = ペナルティなし、2 = dry_multiplier、3 = repeat_penalty)。</param>
/// <param name="FinishReason">最後の応答の finish_reason(応答が無ければ null)。</param>
/// <param name="PromptTokens">最後の応答の usage.prompt_tokens(分かれば)。</param>
/// <param name="SettingsJson">設定と試行の記録。</param>
/// <param name="Error">失敗・サーバの異常のときの理由。</param>
internal sealed record VlmResult(VlmOutcome Outcome, VlmCaption? Caption, int Attempt, string? FinishReason, int? PromptTokens, string SettingsJson, string? Error);

/// <summary>llama-server が想定していない応答(HTTP 400 でも 5xx でもない 200 以外の状態)を返した。設定の誤りの兆候なので、画像ごとの失敗にせず、ジョブを止める。</summary>
internal sealed class VlmProtocolException : Exception
{
    public VlmProtocolException(string message) : base(message) { }
}

/// <summary>
/// llama-server の <c>/v1/chat/completions</c> を呼んで、画像 1 枚の ocr・説明文・tags を得る(ADR 0022)。
/// <list type="bullet">
/// <item>リクエストは評価で使った形と同じ: <c>messages</c>(画像の data URI + プロンプト)、<c>temperature</c> 0、<c>response_format</c> に json_schema(<c>name</c>/<c>strict</c> は付けない)、
/// <c>max_tokens</c> 2048、<c>cache_prompt</c> false(全試行。<see cref="CachePrompt"/>)。再試行のときだけ、<c>dry_multiplier</c> か <c>repeat_penalty</c> を足す。</item>
/// <item>試行 1 = ペナルティなし。<c>finish_reason = length</c> なら、試行 2 = <c>dry_multiplier = 0.8</c>、それでも length なら試行 3 = <c>repeat_penalty = 1.1</c>(2 つを同時には使わない)。
/// 3 つとも length なら、その画像は失敗(error = <c>finish_reason=length</c>)。成功は <c>stop</c> のときだけ。</item>
/// <item><b>HTTP 400</b>(大きすぎる画像など)は再試行せず、その画像の失敗。<b>HTTP 5xx・接続エラー・応答の読み取りの失敗・タイムアウト(300 秒)</b>は、サーバの異常
/// (<see cref="VlmOutcome.ServerFailure"/>。再起動とやり直しは呼び出し側)。それ以外の状態(401・404 など)は設定の誤りなので <see cref="VlmProtocolException"/>。</item>
/// <item>パースは厳格(<see cref="VlmResponse"/>)。<c>stop</c> なのにパースできない・想定外のプロパティ・思考の痕跡は、その画像の失敗(再試行しない)。</item>
/// <item>説明文が 120 字を超える・tags が 15 個を超えるのは、失敗にせず、設定の記録(warnings)に残す。</item>
/// </list>
/// 1 つの llama-server につき 1 つ(サーバを作り直したら作り直す)。キャンセルは <see cref="OperationCanceledException"/>(その 1 枚は未処理のまま)。
/// </summary>
internal sealed class VlmClient : IDisposable
{
    public const int MaxTokens = 2048;
    public const int MaxDescriptionChars = 120;
    public const int MaxTags = 15;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(300);

    /// <summary>temperature(model_id に入る <see cref="VlmCatalog.Temperature"/> と同じ値。0)。</summary>
    private static readonly double Temperature = double.Parse(VlmCatalog.Temperature, CultureInfo.InvariantCulture);

    /// <summary>
    /// リクエストの <c>cache_prompt</c>(**すべての試行で false**)。true(llama-server の既定)だと、同じ画像の 1 回目の直後の再試行で、プロンプト全体(画像のトークン込み)が
    /// スロットの KV キャッシュから再利用され(timings の prompt_n = 1)、再試行の出力が、評価の出力や「直前に何も処理していない」状態の出力と変わる(確かめた結果: 同じ画像・同じ設定で
    /// 3/3 枚が違う出力。cache_prompt=false を付けると評価の出力と文字列まで一致。ADR 0024)。出力が「直前にサーバーが何を処理したか」に左右されないようにする(結果はモデル・ビルド・設定だけで決まる)。
    /// 1 回目の出力は付けても付けなくても変わらない(10 枚 × 3 条件で一致)ので、既存の結果とは食い違わない。
    /// <b>model_id(<see cref="VlmCatalog"/>)には入れない</b>: 入れると既存の VLM の結果がすべて要再作成になるが、1 回目の出力は変わらず、再試行の出力もどちらも正しい出力なので、作り直す理由が無い。
    /// 古い記録(settings_json に cache_prompt が無い)は、既定(true)で作られた、と読む。
    /// </summary>
    public const bool CachePrompt = false;

    private const int MaxAttempts = 3;
    private const int ErrorBodyChars = 300;

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly JsonNode _schema = JsonNode.Parse(VlmSchema.Json) ?? throw new InvalidOperationException("スキーマが JSON でない");
    private readonly string _prompt = VlmPrompt.Text;

    public VlmClient(Uri baseUri, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        _endpoint = new Uri(baseUri, "/v1/chat/completions");
        _http = new HttpClient { Timeout = requestTimeout ?? DefaultRequestTimeout };
    }

    /// <summary>試行ごとの追加フィールド(試行 1 は無し)。</summary>
    private static (string Name, double Value)? ExtraOf(int attempt) => attempt switch
    {
        1 => null,
        2 => ("dry_multiplier", 0.8),
        3 => ("repeat_penalty", 1.1),
        _ => throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "試行は 1〜3"),
    };

    /// <summary>
    /// 画像 1 枚を処理する。<paramref name="imageBytes"/> はファイルの内容(DB のハッシュと照合済みのもの)、<paramref name="format"/> は <c>images.format</c>。
    /// </summary>
    public async Task<VlmResult> CaptionAsync(byte[] imageBytes, ImageFormat format, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ct.ThrowIfCancellationRequested();

        VlmImagePayload image;
        try
        {
            image = VlmImage.Prepare(imageBytes, format);
        }
        catch (VlmImageException e)
        {
            // 失敗の settings_json は attempts が 1 つ以上(CaptionStore の検証)。送れなかった試行も 1 つ記録する
            string error = "画像を送れない: " + e.Message;
            return new VlmResult(VlmOutcome.Failed, null, 1, null, null, SettingsJson(null, [AttemptRecord(1, null, Stopwatch.StartNew(), null, null, null, null, error)], 1, null), error);
        }
        string dataUri = "data:" + image.Mime + ";base64," + Convert.ToBase64String(image.Bytes);

        var attempts = new List<JsonObject>();
        VlmResponseInfo? last = null;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            (string Name, double Value)? extra = ExtraOf(attempt);
            string requestJson = BuildRequest(dataUri, extra);

            var sw = Stopwatch.StartNew();
            int status;
            string body;
            try
            {
                using var content = new StringContent(requestJson, new UTF8Encoding(false), "application/json");
                using HttpResponseMessage response = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
                status = (int)response.StatusCode;
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Server(attempt, attempts, image, $"タイムアウト({_http.Timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} 秒)", extra, sw);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                return Server(attempt, attempts, image, "接続・読み取りの失敗: " + e.Message, extra, sw);
            }

            if (status >= 500) return Server(attempt, attempts, image, $"HTTP {status.ToString(CultureInfo.InvariantCulture)}: {Head(body)}", extra, sw);
            if (status == 400)
            {
                string error400 = $"HTTP 400(再試行しない): {Head(body)}";
                attempts.Add(AttemptRecord(attempt, extra, sw, status, null, null, null, error400));
                return new VlmResult(VlmOutcome.Failed, null, attempt, null, null, SettingsJson(image, attempts, attempt, null), error400);
            }
            if (status != 200)
            {
                throw new VlmProtocolException($"llama-server が想定外の HTTP {status.ToString(CultureInfo.InvariantCulture)} を返した(200・400・5xx 以外): {Head(body)}");
            }

            try
            {
                last = VlmResponse.Parse(body);
            }
            catch (JsonException e)
            {
                return Server(attempt, attempts, image, "応答を読めない(JSON でない): " + e.Message, extra, sw);
            }
            attempts.Add(AttemptRecord(attempt, extra, sw, status, last.FinishReason, last.PromptTokens, last.CompletionTokens));

            if (last.ThinkingTrace is not null)
            {
                return new VlmResult(VlmOutcome.Failed, null, attempt, last.FinishReason, last.PromptTokens, SettingsJson(image, attempts, attempt, last.Timings),
                    "思考の痕跡がある(構成の誤りの兆候): " + last.ThinkingTrace);
            }
            if (string.Equals(last.FinishReason, "length", StringComparison.Ordinal))
            {
                if (attempt < MaxAttempts) continue; // ペナルティを足して、もう一度
                return new VlmResult(VlmOutcome.Failed, null, attempt, last.FinishReason, last.PromptTokens, SettingsJson(image, attempts, attempt, last.Timings),
                    "finish_reason=length");
            }
            if (!string.Equals(last.FinishReason, "stop", StringComparison.Ordinal))
            {
                return new VlmResult(VlmOutcome.Failed, null, attempt, last.FinishReason, last.PromptTokens, SettingsJson(image, attempts, attempt, last.Timings),
                    $"finish_reason={last.FinishReason ?? "(無し)"}(stop でも length でもない)" + (last.FinishReason is null && last.ParseError is not null ? ": " + last.ParseError : ""));
            }
            if (last.Caption is null)
            {
                return new VlmResult(VlmOutcome.Failed, null, attempt, last.FinishReason, last.PromptTokens, SettingsJson(image, attempts, attempt, last.Timings),
                    "応答のパースに失敗: " + last.ParseError);
            }

            return new VlmResult(VlmOutcome.Ok, last.Caption, attempt, last.FinishReason, last.PromptTokens,
                SettingsJson(image, attempts, attempt, last.Timings, Warnings(last.Caption)), null);
        }

        throw new InvalidOperationException("到達しない(試行のループは必ず戻る)");
    }

    // ------------------------------------------------------------------ リクエスト

    private string BuildRequest(string imageUrl, (string Name, double Value)? extra)
    {
        var req = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = imageUrl } },
                    new JsonObject { ["type"] = "text", ["text"] = _prompt }),
            }),
            ["temperature"] = Temperature,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["schema"] = _schema.DeepClone() },
            },
            ["max_tokens"] = MaxTokens,
            ["cache_prompt"] = CachePrompt,
        };
        if (extra is { } e) req[e.Name] = e.Value;
        return req.ToJsonString();
    }

    // ------------------------------------------------------------------ 結果の記録

    private static VlmResult Server(int attempt, List<JsonObject> attempts, VlmImagePayload image, string error, (string Name, double Value)? extra, Stopwatch sw)
    {
        attempts.Add(AttemptRecord(attempt, extra, sw, null, null, null, null, error));
        return new VlmResult(VlmOutcome.ServerFailure, null, attempt, null, null, SettingsJson(image, attempts, attempt, null), error);
    }

    /// <summary>
    /// 試行 1 回ぶんの記録。形は <c>CaptionStore.CheckFailureSettings</c> が検証する(<c>attempt</c> は 1 以上の整数で必須)。そのほかは、応答があれば <c>finish_reason</c>・トークン数、
    /// 応答が無かった(または 400 など成功しなかった)理由は <c>error</c>、その試行のリクエストの設定は llama-server のリクエストと同じ名前(<c>max_tokens</c>・<c>temperature</c>・
    /// <c>dry_multiplier</c>・<c>repeat_penalty</c>)。
    /// </summary>
    private static JsonObject AttemptRecord(int attempt, (string Name, double Value)? extra, Stopwatch sw, int? status, string? finishReason, int? promptTokens, int? completionTokens, string? error = null)
    {
        var o = new JsonObject
        {
            ["attempt"] = attempt,
            ["max_tokens"] = MaxTokens,
            ["temperature"] = Temperature,
            ["cache_prompt"] = CachePrompt,
        };
        if (extra is { } e) o[e.Name] = e.Value;
        if (status is { } s) o["http_status"] = s;
        if (finishReason is not null) o["finish_reason"] = finishReason;
        if (promptTokens is { } p) o["prompt_tokens"] = p;
        if (completionTokens is { } c) o["completion_tokens"] = c;
        o["wall_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        if (error is not null) o["error"] = error;
        return o;
    }

    private static List<string> Warnings(VlmCaption c)
    {
        var w = new List<string>();
        int descChars = c.Description.EnumerateRunes().Count();
        if (descChars > MaxDescriptionChars) w.Add($"description が {descChars.ToString(CultureInfo.InvariantCulture)} 字({MaxDescriptionChars.ToString(CultureInfo.InvariantCulture)} 字を超えた)");
        if (c.Tags.Count > MaxTags) w.Add($"tags が {c.Tags.Count.ToString(CultureInfo.InvariantCulture)} 個({MaxTags.ToString(CultureInfo.InvariantCulture)} 個を超えた)");
        return w;
    }

    /// <summary>設定と試行の記録(JSON のオブジェクト)。成功の captions.settings_json と、失敗の stage_status.settings_json に入れる。</summary>
    private static string SettingsJson(VlmImagePayload? image, List<JsonObject> attempts, int attempt, JsonObject? timings, List<string>? warnings = null)
    {
        var o = new JsonObject
        {
            ["max_tokens"] = MaxTokens,
            ["temperature"] = Temperature,
            ["cache_prompt"] = CachePrompt,
            ["attempt"] = attempt,
        };
        if (ExtraOf(attempt) is { } e) o[e.Name] = e.Value;
        if (image is not null)
        {
            o["image"] = new JsonObject { ["mime"] = image.Mime, ["bytes"] = image.Bytes.Length };
        }
        if (timings is not null)
        {
            var t = new JsonObject();
            foreach (string key in new[] { "prompt_n", "prompt_ms", "predicted_n", "predicted_ms" })
            {
                if (timings[key] is JsonValue v) t[key] = v.DeepClone();
            }
            o["timings"] = t;
        }
        o["attempts"] = new JsonArray(attempts.Select(a => (JsonNode?)a.DeepClone()).ToArray());
        if (warnings is { Count: > 0 }) o["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());
        return o.ToJsonString();
    }

    private static string Head(string body)
    {
        string oneLine = body.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return oneLine.Length <= ErrorBodyChars ? oneLine : oneLine[..ErrorBodyChars] + "…";
    }

    public void Dispose() => _http.Dispose();
}
