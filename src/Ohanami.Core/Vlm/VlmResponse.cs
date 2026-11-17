using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ohanami.Core.Vlm;

/// <summary>VLM の出力 1 枚ぶん(ocr、説明文、tags)。加工せずに持つ(trim も正規化もしない)。</summary>
internal sealed record VlmCaption(string Ocr, string Description, IReadOnlyList<string> Tags);

/// <summary>応答の本文の解釈の結果。</summary>
internal sealed record VlmResponseInfo(
    string? FinishReason, int? PromptTokens, int? CompletionTokens, JsonObject? Timings, string? ThinkingTrace, VlmCaption? Caption, string? ParseError);

/// <summary>
/// llama-server の <c>/v1/chat/completions</c> の応答(HTTP 200 の本文)を、厳格に解釈する(ADR 0022)。
/// <list type="bullet">
/// <item><c>choices[0].message.content</c> は文字列で、JSON のオブジェクト。プロパティは <c>ocr</c>・<c>description</c>・<c>tags</c> だけ(想定外のプロパティは失敗)、
/// <c>ocr</c> と <c>description</c> は必須の文字列、<c>tags</c> は必須の文字列の配列(空配列は許す)。型違い・欠落は失敗(理由つき)。</item>
/// <item>思考の痕跡(<c>reasoning_content</c> が空でない、または content に <c>&lt;think&gt;</c>)があれば、その画像を失敗にする(構成の誤りの兆候)。</item>
/// <item><c>finish_reason</c>・<c>usage.prompt_tokens</c>・<c>usage.completion_tokens</c>・<c>timings</c>(prompt_n / predicted_n など)を読む。</item>
/// <item><b>孤立したサロゲート</b>(JSON の文字列エスケープの片割れ。例: 高位サロゲートだけの <c>\uD800</c>)を含む文字列は、System.Text.Json が <c>GetString</c> で
/// <see cref="InvalidOperationException"/> にする(VLM が壊れた出力を返したとき。実際に起きた)。これは、応答を読む段階のパース失敗として、理由を
/// <see cref="SurrogateErrorPrefix"/> で始めて返す(その画像は失敗。集計で数えられるよう、先頭は固定の文字列)。</item>
/// </list>
/// 本文が JSON として壊れているときは <see cref="JsonException"/>(呼び出し側が「応答の読み取りの失敗」として扱う)。
/// </summary>
internal static class VlmResponse
{
    /// <summary>応答のパースの失敗の理由の先頭(孤立したサロゲートなどで、文字列を読めなかったとき)。集計で数えられるよう、固定の文字列にする。</summary>
    public const string SurrogateErrorPrefix = "不正なサロゲートを含む文字列で、応答を読めない";

    public static VlmResponseInfo Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        JsonNode? node = JsonNode.Parse(body);
        if (node is not JsonObject root) throw new JsonException("応答のルートが JSON のオブジェクトでない");

        JsonNode? choice0 = (root["choices"] as JsonArray) is { Count: > 0 } choices ? choices[0] : null;
        string? finish = SafeString(choice0?["finish_reason"]);
        int? promptTokens = IntOrNull(root["usage"]?["prompt_tokens"]);
        int? completionTokens = IntOrNull(root["usage"]?["completion_tokens"]);
        JsonObject? timings = root["timings"]?.DeepClone() as JsonObject;

        try
        {
            string? thinking = DetectThinking(choice0?["message"]);
            if (choice0 is null) return new VlmResponseInfo(finish, promptTokens, completionTokens, timings, thinking, null, "choices がない");
            (VlmCaption? caption, string? error) = ParseContent(choice0["message"]?["content"]);
            return new VlmResponseInfo(finish, promptTokens, completionTokens, timings, thinking, caption, error);
        }
        catch (InvalidOperationException e)
        {
            // 孤立したサロゲートを含む文字列(content そのもの、または content の JSON の中の ocr / description / tags)
            return new VlmResponseInfo(finish, promptTokens, completionTokens, timings, null, null, SurrogateErrorPrefix + ": " + e.Message);
        }
    }

    /// <summary>思考の痕跡。message.reasoning_content が空でない、または content に &lt;think&gt; / &lt;/think&gt; がある。なければ null。</summary>
    internal static string? DetectThinking(JsonNode? message)
    {
        var found = new List<string>();
        if (message?["reasoning_content"] is JsonValue rv && rv.GetValueKind() == JsonValueKind.String && rv.GetValue<string>().Length > 0)
            found.Add($"reasoning_content が空でない({rv.GetValue<string>().Length.ToString(CultureInfo.InvariantCulture)} 文字)");
        if (message?["content"] is JsonValue cv && cv.GetValueKind() == JsonValueKind.String)
        {
            string c = cv.GetValue<string>();
            if (c.Contains("<think>", StringComparison.Ordinal) || c.Contains("</think>", StringComparison.Ordinal))
                found.Add("content に <think> タグがある");
        }
        return found.Count == 0 ? null : string.Join(" / ", found);
    }

    private static (VlmCaption? Caption, string? Error) ParseContent(JsonNode? contentNode)
    {
        if (contentNode is not JsonValue cv || cv.GetValueKind() != JsonValueKind.String) return (null, "message.content が文字列でない");
        string content = cv.GetValue<string>();
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(content); }
        catch (JsonException e) { return (null, "content が JSON でない: " + e.Message); }
        if (parsed is not JsonObject po) return (null, "content の JSON がオブジェクトでない");

        foreach (KeyValuePair<string, JsonNode?> kv in po)
        {
            if (kv.Key is not ("ocr" or "description" or "tags")) return (null, $"想定外のプロパティ '{kv.Key}'");
        }
        string? ocr = StringProp(po, "ocr", out string? e1);
        if (e1 is not null) return (null, e1);
        string? desc = StringProp(po, "description", out string? e2);
        if (e2 is not null) return (null, e2);

        if (!po.ContainsKey("tags")) return (null, "'tags' がない");
        if (po["tags"] is not JsonArray arr) return (null, "'tags' が配列でない");
        var tags = new List<string>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonValue tv && tv.GetValueKind() == JsonValueKind.String) tags.Add(tv.GetValue<string>());
            else return (null, $"'tags'[{i.ToString(CultureInfo.InvariantCulture)}] が文字列でない");
        }
        return (new VlmCaption(ocr!, desc!, tags), null);
    }

    private static string? StringProp(JsonObject o, string key, out string? error)
    {
        error = null;
        if (!o.ContainsKey(key)) { error = $"'{key}' がない"; return null; }
        if (o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
        error = $"'{key}' が文字列でない";
        return null;
    }

    private static string? SafeString(JsonNode? n)
    {
        try { return n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static int? IntOrNull(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out int i) ? i : null;
}
