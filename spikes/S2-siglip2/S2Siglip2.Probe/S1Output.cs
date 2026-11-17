using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace S2Siglip2.Probe;

/// <summary>S1 の仕掛けの出力 1 画像分(ocr / description / tags)。tags が null = その実行に tags が無い。</summary>
public sealed record S1Doc(string File, string Ocr, string Description, IReadOnlyList<string>? Tags);

/// <summary>
/// S1 の run の出力ディレクトリ(1 画像 = 1 つの JSON)を読む。形(spikes/S1-vlm/RunCommand が書く):
/// <c>filename</c>(元の画像のファイル名)、<c>parsed</c>(S1 がパースした結果: ok / ocr / description / tags / error)、
/// <c>response_body</c>(llama-server の応答。JSON 文字列)→ choices[0].message.content(JSON 文字列)→ <c>ocr</c>、<c>description</c>、<c>tags</c>(文字列の配列。無くてもよい)。
/// parsed と応答本文の両方があれば、両方を読んで内容が一致することを確かめる(食い違いは例外にする。黙ってどちらかを選ばない)。片方だけなら、それを使う。
/// 打ち切り(finish_reason が stop でない)・parsed.ok が偽・JSON として読めない・必須の項目が無いものは、読めなかった(Failures)として理由つきで返す(黙って落とさない)。
/// run-info.json と schema.json 以外の JSON で filename が無いものは例外にする(別の形のファイルが混ざっている)。
/// </summary>
public static class S1Output
{
    /// <summary>読めなかった出力。Json = JSON のファイル名、File = 元の画像のファイル名(NFC)、Reason = 理由。</summary>
    public sealed record Failure(string Json, string File, string Reason)
    {
        public override string ToString() => $"{File}({Json}): {Reason}";
    }

    public sealed record Loaded(IReadOnlyDictionary<string, S1Doc> Docs, IReadOnlyList<Failure> Failures, int WithTags, int WithoutTags);

    private sealed record Vals(string Ocr, string Description, IReadOnlyList<string>? Tags);

    private static readonly string[] NonImageJson = ["run-info.json", "schema.json"];

    public static Loaded Load(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"S1 の出力ディレクトリが無い: {dir}");
        var docs = new Dictionary<string, S1Doc>(StringComparer.Ordinal);
        var failures = new List<Failure>();
        int withTags = 0, withoutTags = 0;
        foreach (string path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (NonImageJson.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("filename", out var fn) || fn.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"{path}: filename が無い(S1 の出力の形ではない)");
            string file = fn.GetString()!.Normalize(NormalizationForm.FormC);
            string? why = TryRead(root, out Vals? v, path);
            if (why is not null) { failures.Add(new Failure(name, file, why)); continue; }
            if (!docs.TryAdd(file, new S1Doc(file, v!.Ocr, v.Description, v.Tags))) throw new InvalidDataException($"{path}: 画像「{file}」の出力が2つある");
            if (v.Tags is null) withoutTags++; else withTags++;
        }
        return new Loaded(docs, failures, withTags, withoutTags);
    }

    /// <summary>読めなければ理由(読めたら null)。parsed と応答本文が食い違えば例外。</summary>
    private static string? TryRead(JsonElement root, out Vals? vals, string path)
    {
        vals = null;
        if (root.TryGetProperty("transport_error", out var te) && te.ValueKind == JsonValueKind.String) return "通信エラー: " + te.GetString();
        if (root.TryGetProperty("finish_reason", out var topFr) && topFr.ValueKind == JsonValueKind.String && topFr.GetString() != "stop")
            return $"finish_reason = {topFr.GetString()}(打ち切り・ループ。失敗として数える)";
        bool hasParsed = root.TryGetProperty("parsed", out var pe) && pe.ValueKind == JsonValueKind.Object;
        bool hasBody = root.TryGetProperty("response_body", out var rb) && rb.ValueKind == JsonValueKind.String;
        if (!hasParsed && !hasBody) return "parsed も response_body も無い";
        Vals? fromParsed = null, fromBody = null;
        string? whyParsed = hasParsed ? ReadParsed(pe, out fromParsed) : null;
        string? whyBody = hasBody ? ReadBody(rb.GetString()!, out fromBody) : null;
        if (whyParsed is not null) return whyParsed;
        if (whyBody is not null) return whyBody;
        if (fromParsed is not null && fromBody is not null && !Same(fromParsed, fromBody))
            throw new InvalidDataException($"{path}: parsed と応答本文(content)の ocr / description / tags が食い違う(S1 の書き出しか、このコードの読みが違う)");
        vals = fromParsed ?? fromBody;
        return null;
    }

    private static bool Same(Vals a, Vals b) =>
        a.Ocr == b.Ocr && a.Description == b.Description && (a.Tags is null ? b.Tags is null : b.Tags is not null && a.Tags.SequenceEqual(b.Tags));

    /// <summary>S1 の parsed(ok / ocr / description / tags / error)。ok が偽なら失敗(理由は error)。</summary>
    private static string? ReadParsed(JsonElement p, out Vals? vals)
    {
        vals = null;
        if (!p.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "parsed.ok が真偽値でない";
        if (ok.ValueKind == JsonValueKind.False)
            return "parsed.ok = false: " + (p.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() : "(理由なし)");
        return ReadFields(p, "parsed", out vals);
    }

    /// <summary>応答本文: choices[0].message.content(JSON 文字列)。finish_reason が stop でなければ失敗。</summary>
    private static string? ReadBody(string responseBody, out Vals? vals)
    {
        vals = null;
        JsonDocument body;
        try { body = JsonDocument.Parse(responseBody); } catch (JsonException) { return "response_body が JSON として読めない"; }
        using (body)
        {
            if (!body.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return "choices が無い";
            JsonElement c0 = choices[0];
            if (c0.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String && fr.GetString() != "stop") return $"finish_reason = {fr.GetString()}(打ち切り・ループ。失敗として数える)";
            if (!c0.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return "message.content が無い";
            JsonDocument inner;
            try { inner = JsonDocument.Parse(content.GetString()!); } catch (JsonException) { return "message.content が JSON として読めない"; }
            using (inner)
            {
                if (inner.RootElement.ValueKind != JsonValueKind.Object) return "message.content が JSON のオブジェクトでない";
                return ReadFields(inner.RootElement, "content", out vals);
            }
        }
    }

    private static string? ReadFields(JsonElement r, string where, out Vals? vals)
    {
        vals = null;
        if (!r.TryGetProperty("ocr", out var o) || o.ValueKind != JsonValueKind.String) return $"{where}.ocr が無い(文字列でない)";
        if (!r.TryGetProperty("description", out var d) || d.ValueKind != JsonValueKind.String) return $"{where}.description が無い(文字列でない)";
        IReadOnlyList<string>? tags = null;
        if (r.TryGetProperty("tags", out var t) && t.ValueKind != JsonValueKind.Null)
        {
            if (t.ValueKind != JsonValueKind.Array) return $"{where}.tags が配列でない";
            var list = new List<string>();
            foreach (var e in t.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) return $"{where}.tags に文字列でない要素がある";
                list.Add(e.GetString()!);
            }
            tags = list;
        }
        vals = new Vals(o.GetString()!, d.GetString()!, tags);
        return null;
    }

    /// <summary>タグの区切り。S3 が区切りとみなす記号(日本語の文字でも英数字でもない)。空白や改行は S3 の正規化で日本語の文字の間から除かれ、タグどうしの bigram がつながるので使わない。</summary>
    public const string TagSeparator = "、";

    /// <summary>欄の区切り。同じく S3 の区切り。</summary>
    public const string FieldSeparator = "|";

    /// <summary>
    /// 全文検索に入れる 1 文書のテキスト。「tags なし」は ocr|description、「tags あり」は ocr|description|tag1、tag2、…。
    /// 欄の区切りは「|」、タグの区切りは「、」(どちらも S3 の bigram では必ず区切りになる)。
    /// 改行や空白で区切ると、S3 の正規化が日本語の文字の間の空白・改行を除去するので、ocr の末尾と description の先頭、
    /// またはタグどうしの bigram がつながってしまう(「黒髪」+「ロングヘア」が「黒髪ロング」に当たる、など)。
    /// </summary>
    public static string IndexText(S1Doc d, bool withTags)
    {
        string text = d.Ocr + FieldSeparator + d.Description;
        if (!withTags) return text;
        if (d.Tags is null) throw new InvalidOperationException($"{d.File}: tags が無い(tags なしの run で「tags あり」の索引は作れない)");
        return text + FieldSeparator + string.Join(TagSeparator, d.Tags);
    }
}
