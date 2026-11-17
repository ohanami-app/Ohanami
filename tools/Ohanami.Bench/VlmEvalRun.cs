using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ohanami.Bench;

/// <summary>
/// VLM の評価の run(spikes/S1-vlm の出力)のフォルダの、画像 1 枚ぶんの JSON(<c>&lt;名前の幹&gt;.json</c>)を読んだもの。
/// 成功(<paramref name="ParsedOk"/>)は finish_reason = stop で、応答の content を厳格にパースできたもの(ocr・description・tags だけ。型違い・余分なプロパティは失敗)。
/// </summary>
internal sealed record VlmEvalImage(
    string FileName, int? HttpStatus, string FinishReason, bool ParsedOk, string? Ocr, string? Description, IReadOnlyList<string>? Tags,
    int? PromptTokens, int? CompletionTokens, string? RequestText, string? RequestSchemaJson, double? Temperature, int? MaxTokens, string? Note);

/// <summary>
/// 1 つの確認の結果。<paramref name="Weak"/> は「弱い確認」(通っても、言えることが限られる)の印。
/// <paramref name="Unmeasured"/> は「測れなかった」(不合格ではなく、判定の材料が取れていない。<paramref name="Passed"/> は false)の印。
/// </summary>
internal sealed record CheckResult(string Name, bool Passed, string Detail, bool Weak = false, bool Unmeasured = false);

/// <summary>VLM の評価の run のフォルダの読み込み(画像ごとの JSON、run-info.json、prompt.txt など)。DB も GPU も使わない純粋な部分。</summary>
internal static class VlmEvalRun
{
    /// <summary>run のフォルダの中の、画像の結果ではない JSON。</summary>
    private static readonly string[] NonImageJson = ["run-info.json", "schema.json", "request-extra.json"];

    public static IReadOnlyList<VlmEvalImage> ReadImages(string runDir)
    {
        string[] files = Directory.GetFiles(runDir, "*.json").Where(f => !NonImageJson.Contains(Path.GetFileName(f), StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidDataException($"VLM の評価の run に画像の結果の JSON が無い: {runDir}");
        var list = new List<VlmEvalImage>(files.Length);
        foreach (string f in files)
        {
            try { list.Add(ParseImageJson(File.ReadAllText(f, new UTF8Encoding(false, true)))); }
            catch (Exception e) when (e is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                throw new InvalidDataException($"{Path.GetFileName(f)} を読めない: {e.Message}", e);
            }
        }
        var dup = list.GroupBy(i => i.FileName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidDataException($"VLM の評価の run に同じ画像「{dup.Key}」の結果が 2 つある");
        return list;
    }

    /// <summary>
    /// 画像 1 枚の JSON を読む。評価の <c>parsed</c> は、こちらの厳格なパースの結果と**突き合わせる**(食い違えば例外。評価のパースとここのパースが違うことを黙って通さない)。
    /// 失敗(finish_reason が stop でない、HTTP が 200 でない、content がスキーマどおりでない)は <see cref="VlmEvalImage.ParsedOk"/> = false。
    /// </summary>
    public static VlmEvalImage ParseImageJson(string json)
    {
        JsonObject root = (JsonNode.Parse(json) as JsonObject) ?? throw new InvalidDataException("JSON のルートがオブジェクトでない");
        string fileName = BenchInputs.Nfc(root["filename"]?.GetValue<string>() ?? throw new InvalidDataException("filename が無い"));
        int? http = root["http_status"] is JsonValue hv && hv.TryGetValue(out int h) ? h : null;
        string finish = root["finish_reason"]?.GetValue<string>() ?? "";

        JsonNode? body = root["request"]?["body_without_base64"];
        string? text = body?["messages"]?[0]?["content"]?[1]?["text"]?.GetValue<string>();
        string? schema = body?["response_format"]?["json_schema"]?["schema"]?.ToJsonString();
        double? temperature = body?["temperature"]?.GetValue<double>();
        int? maxTokens = body?["max_tokens"] is JsonValue mv && mv.TryGetValue(out int m) ? m : null;
        int? promptTokens = IntOf(root["usage"]?["prompt_tokens"]);
        int? completionTokens = IntOf(root["usage"]?["completion_tokens"]);

        string? ocr = null, description = null, note = null;
        List<string>? tags = null;
        bool ok = false;
        if (http == 200 && finish == "stop")
        {
            (ok, ocr, description, tags, note) = ParseContent(root["response_body"]?.GetValue<string>());
        }
        else
        {
            note = $"http_status={http?.ToString(CultureInfo.InvariantCulture) ?? "なし"}、finish_reason={finish}";
        }

        // 評価の parsed との突き合わせ
        JsonNode? evalParsed = root["parsed"];
        bool evalOk = evalParsed?["ok"]?.GetValue<bool>() ?? false;
        if (evalOk != ok)
            throw new InvalidDataException($"「{fileName}」: 評価の parsed.ok({evalOk})と、ここの厳格なパース({ok}。{note})が食い違う");
        if (ok)
        {
            if (evalParsed!["ocr"]?.GetValue<string>() != ocr || evalParsed["description"]?.GetValue<string>() != description)
                throw new InvalidDataException($"「{fileName}」: 評価の parsed の ocr / description と、応答の content から読んだ値が食い違う");
            string[] evalTags = evalParsed["tags"]?.AsArray().Select(t => t?.GetValue<string>() ?? throw new InvalidDataException("tags に null")).ToArray() ?? throw new InvalidDataException("評価の parsed に tags が無い");
            if (!evalTags.SequenceEqual(tags!, StringComparer.Ordinal))
                throw new InvalidDataException($"「{fileName}」: 評価の parsed の tags と、応答の content から読んだ値が食い違う");
        }
        return new VlmEvalImage(fileName, http, finish, ok, ocr, description, tags, promptTokens, completionTokens, text, schema, temperature, maxTokens, note);

        static int? IntOf(JsonNode? n) => n is JsonValue v && v.TryGetValue(out int i) ? i : null;
    }

    /// <summary>
    /// 応答の本文(<c>choices[0].message.content</c>)を厳格に読む(spikes/S1-vlm の ParseContent と同じ厳しさ): content は文字列で、JSON のオブジェクト。
    /// プロパティは ocr・description・tags だけ。ocr・description は文字列、tags は文字列の配列(空配列は許す)。違えば失敗(理由つき)。
    /// </summary>
    public static (bool Ok, string? Ocr, string? Description, List<string>? Tags, string? Error) ParseContent(string? responseBody)
    {
        if (responseBody is null) return Fail("response_body が無い");
        JsonNode? root;
        try { root = JsonNode.Parse(responseBody); }
        catch (JsonException e) { return Fail("response_body が JSON でない: " + e.Message); }
        if (root?["choices"] is not JsonArray { Count: > 0 } choices) return Fail("choices がない");
        if (choices[0]?["message"]?["content"] is not JsonValue cv || !cv.TryGetValue(out string? content)) return Fail("message.content が文字列でない");
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(content); }
        catch (JsonException e) { return Fail("content が JSON でない: " + e.Message); }
        if (parsed is not JsonObject po) return Fail("content の JSON がオブジェクトでない");
        foreach (KeyValuePair<string, JsonNode?> kv in po)
            if (kv.Key is not ("ocr" or "description" or "tags")) return Fail($"想定外のプロパティ '{kv.Key}'");
        if (!TryString(po, "ocr", out string? ocr, out string? e1)) return Fail(e1!);
        if (!TryString(po, "description", out string? desc, out string? e2)) return Fail(e2!);
        if (po["tags"] is not JsonArray arr) return Fail("'tags' が配列でない(または無い)");
        var tags = new List<string>(arr.Count);
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonValue tv && tv.TryGetValue(out string? t)) tags.Add(t);
            else return Fail($"'tags'[{i}] が文字列でない");
        }
        return (true, ocr, desc, tags, null);

        static (bool, string?, string?, List<string>?, string?) Fail(string error) => (false, null, null, null, error);

        static bool TryString(JsonObject o, string key, out string? value, out string? error)
        {
            value = null;
            error = null;
            if (!o.ContainsKey(key)) { error = $"'{key}' がない"; return false; }
            if (o[key] is JsonValue v && v.TryGetValue(out string? s)) { value = s; return true; }
            error = $"'{key}' が文字列でない";
            return false;
        }
    }

    // ---------------------------------------------------------------- run-info.json

    public sealed record RunInfo(string ModelFileName, long ModelBytes, string MmprojFileName, long MmprojBytes, string ServerArgs, double? Temperature, int? MaxTokens, string? Label);

    public static RunInfo ReadRunInfo(string json)
    {
        JsonObject root = (JsonNode.Parse(json) as JsonObject) ?? throw new InvalidDataException("run-info.json のルートがオブジェクトでない");
        static string Req(JsonNode? n, string what) => n?.GetValue<string>() ?? throw new InvalidDataException($"run-info.json に {what} が無い");
        return new RunInfo(
            Path.GetFileName(Req(root["model"]?["path"], "model.path")), root["model"]?["bytes"]?.GetValue<long>() ?? throw new InvalidDataException("run-info.json に model.bytes が無い"),
            Path.GetFileName(Req(root["mmproj"]?["path"], "mmproj.path")), root["mmproj"]?["bytes"]?.GetValue<long>() ?? throw new InvalidDataException("run-info.json に mmproj.bytes が無い"),
            Req(root["server_args"], "server_args"),
            root["temperature"] is JsonValue t && t.TryGetValue(out double td) ? td : null,
            root["max_tokens"] is JsonValue mt && mt.TryGetValue(out int mi) ? mi : null,
            root["label"]?.GetValue<string>());
    }

    /// <summary><c>server_args</c> から、パスとポート(<c>-m</c>、<c>--mmproj</c>、<c>--host</c>、<c>--port</c>)を除いた残りの引数(評価の起動フラグ)を取り出す。</summary>
    public static string LaunchFlagsOf(string serverArgs)
    {
        // 形: -m "<path>" --mmproj "<path>" --host 127.0.0.1 --port <N> <flags…>
        int at = serverArgs.IndexOf("--port ", StringComparison.Ordinal);
        if (at < 0) throw new InvalidDataException("server_args に --port が無い");
        string rest = serverArgs[(at + "--port ".Length)..].TrimStart();
        int space = rest.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? "" : rest[(space + 1)..].Trim();
    }
}

/// <summary>models/MANIFEST.tsv の 1 行(コミット済み)。</summary>
internal sealed record ManifestRow(string Repo, string Revision, string FileName, long Size, string Sha256, string Match)
{
    public static List<ManifestRow> Read(string path)
    {
        var rows = new List<ManifestRow>();
        foreach (string line in File.ReadLines(path, new UTF8Encoding(false, true)).Skip(1))
        {
            if (line.Length == 0) continue;
            string[] c = line.Split('\t');
            if (c.Length != 6) throw new InvalidDataException($"MANIFEST.tsv の列数が 6 でない: {line}");
            rows.Add(new ManifestRow(c[0], c[1], c[2], long.Parse(c[3], CultureInfo.InvariantCulture), c[4], c[5]));
        }
        return rows;
    }
}
