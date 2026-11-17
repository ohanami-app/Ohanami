using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace S1Vlm;

/// <summary>run が出力する画像ごとの JSON の読み書きに共通の部分。</summary>
public static class RunFiles
{
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 日本語をエスケープせず、人が開いて読めるようにする。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string ImageJsonPath(string runDir, SampleRow row) => Path.Combine(runDir, row.Stem + ".json");

    /// <summary>run の出力ディレクトリにあるが、画像名のファイルでない JSON(画像ごとの出力ではないもの)。</summary>
    private static readonly string[] NonImageJson = { "run-info.json", "schema.json", "request-extra.json" };

    /// <summary>
    /// run の出力ディレクトリにある画像ごとの JSON のうち、samples.tsv にない画像のもの(ファイル名)。
    /// 評価セットから画像を外したあとに、古い run の出力が残っている場合などに使う。集計には混ぜず、件数と名前を表示して無視する。
    /// </summary>
    public static List<string> FindExtraOutputs(string runDir, IEnumerable<SampleRow> samples)
    {
        var stems = new HashSet<string>(samples.Select(s => s.Stem), StringComparer.Ordinal);
        var extra = new List<string>();
        foreach (string f in Directory.EnumerateFiles(runDir, "*.json"))
        {
            string name = Path.GetFileName(f);
            if (NonImageJson.Contains(name, StringComparer.Ordinal)) continue;
            if (!stems.Contains(Path.GetFileNameWithoutExtension(name))) extra.Add(name);
        }
        extra.Sort(StringComparer.Ordinal);
        return extra;
    }

    public static void WriteJson(string path, JsonNode node)
    {
        File.WriteAllText(path, node.ToJsonString(WriteOptions) + "\n", Utf8NoBom);
    }

    public sealed record ParsedOutput(bool Ok, string? Ocr, string? Description, string? Error, string[]? Tags = null);

    /// <summary>画像ごとの JSON から、パース結果(parsed)を読む。形が想定と違えば例外。</summary>
    public static ParsedOutput LoadParsed(string path)
    {
        JsonNode? root = JsonNode.Parse(File.ReadAllText(path, Utf8NoBom));
        if (root is not JsonObject obj) throw new InvalidDataException($"{path}: ルートがオブジェクトでない");
        if (obj["parsed"] is not JsonObject p) throw new InvalidDataException($"{path}: 'parsed' がない");
        if (p["ok"] is not JsonValue okv || okv.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"{path}: parsed.ok が真偽値でない");
        bool ok = okv.GetValue<bool>();
        string? ocr = GetStringOrNull(p, "ocr", path);
        string? desc = GetStringOrNull(p, "description", path);
        string? err = GetStringOrNull(p, "error", path);
        if (ok && (ocr is null || desc is null)) throw new InvalidDataException($"{path}: parsed.ok=true なのに ocr/description がない");
        string[]? tags = null;
        if (p["tags"] is JsonNode tn)
        {
            if (tn is not JsonArray arr) throw new InvalidDataException($"{path}: parsed.tags が配列でない");
            var list = new List<string>(arr.Count);
            foreach (JsonNode? e in arr)
            {
                if (e is JsonValue ev && ev.GetValueKind() == JsonValueKind.String) list.Add(ev.GetValue<string>());
                else throw new InvalidDataException($"{path}: parsed.tags に文字列でない要素がある");
            }
            tags = list.ToArray();
        }
        return new ParsedOutput(ok, ocr, desc, err, tags);
    }

    private static string? GetStringOrNull(JsonObject o, string key, string path)
    {
        JsonNode? n = o[key];
        if (n is null) return null;
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
        throw new InvalidDataException($"{path}: parsed.{key} が文字列でない");
    }
}
