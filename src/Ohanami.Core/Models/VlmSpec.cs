using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ohanami.Core.Models;

/// <summary>VLM のモデルを構成するファイル 1 つ: {モデルのディレクトリからの相対パス(区切りは "/")、sha256(小文字 16 進 64 桁)、サイズ(バイト)}。</summary>
internal sealed record VlmFile(string RelativePath, string Sha256, long Size);

/// <summary>
/// VLM(models.kind = vlm)の仕様。1 つの model_id の中身は、結果の中身を決めるものだけから組み立てる(<see cref="CanonicalString"/>):
/// GGUF と mmproj の sha256、プロンプトとスキーマの sha256(**実際に送る文字列**のハッシュ)、temperature、chat template の追加引数、画像トークンの下限・上限。
/// <b>入れないもの</b>(runs.runtime_json と、結果ごとの captions.settings_json に記録する): llama-server のビルド・exe の sha256、max_tokens、再試行の設定
/// (dry_multiplier / repeat_penalty)、-ngl・-c・-cram・--no-mmproj-offload などのメモリ・速度の設定、パスとサイズ。入れると、ビルドを 1 つ上げるだけで
/// 全件が要再作成になる(VLM は 1 万枚で約 36 時間)。ビルド違いで結果が変わるかは別に確かめる事項で、変わらない限り同じモデルとして扱う。
/// models.spec_json には同じ内容に加えて名前・パス・サイズ・Hugging Face のリポジトリとリビジョン(取得の固定用。model_id の入力ではない)を保存し、
/// 行から読み戻して model_id を再計算できる。不正な値・壊れた JSON はすべて <see cref="ModelMismatchException"/>(黙って既定値にしない)。
/// </summary>
internal sealed class VlmSpec : IEquatable<VlmSpec>
{
    /// <summary>spec_json の書式の版。</summary>
    public const int SpecFormat = 1;

    /// <summary>
    /// 正準文字列の 1 行目(書式の版つき。kind は 2 行目で区別する)。値は埋め込みモデルと今は同じだが**共有しない**:
    /// VLM の書式だけを上げたときに、埋め込みの全行の model_id の再計算がずれて DB が開けなくなるのを防ぐ(書式を変えたら、自分の側の数字だけ上げる)。
    /// </summary>
    public const string CanonicalHeader = "ohanami-model-id/1";

    /// <summary>model_id の「:」のあとの桁数(正準文字列の SHA-256 の先頭の 16 進)。</summary>
    public const int IdDigits = EmbeddingModelSpec.IdDigits;

    /// <summary>画像トークンの既定(llama-server が GGUF から読む値に任せる)を表す、正準文字列の中の文字列。</summary>
    public const string Default = "default";

    public string Family { get; }

    public VlmFile Gguf { get; }

    public VlmFile Mmproj { get; }

    /// <summary>実際に送るプロンプトの UTF-8 の sha256(<see cref="PromptSha256Of"/>)。</summary>
    public string PromptSha256 { get; }

    /// <summary>実際に送るスキーマ(空白なしの正準 JSON)の UTF-8 の sha256(<see cref="SchemaSha256Of"/>)。</summary>
    public string SchemaSha256 { get; }

    /// <summary>temperature(10 進の数字の文字列。例: "0")。</summary>
    public string Temperature { get; }

    /// <summary>chat template の追加引数(JSON の文字列。無ければ空)。</summary>
    public string ChatTemplateKwargs { get; }

    /// <summary>画像トークンの下限。null = 既定(<c>--image-min-tokens</c> を付けない)。</summary>
    public int? ImageMinTokens { get; }

    /// <summary>画像トークンの上限。null = 既定(<c>--image-max-tokens</c> を付けない)。</summary>
    public int? ImageMaxTokens { get; }

    /// <summary>Hugging Face のリポジトリ(取得の固定用。model_id の入力ではない)。</summary>
    public string HfRepository { get; }

    /// <summary>Hugging Face のコミットリビジョン(40 桁の小文字 16 進。model_id の入力ではない)。</summary>
    public string HfRevision { get; }

    private string? _canonical;
    private string? _json;

    public VlmSpec(
        string family, VlmFile gguf, VlmFile mmproj, string promptSha256, string schemaSha256, string temperature, string chatTemplateKwargs,
        int? imageMinTokens, int? imageMaxTokens, string hfRepository, string hfRevision)
    {
        Family = CheckToken(family, "family");
        Gguf = CheckFile(gguf, "gguf");
        Mmproj = CheckFile(mmproj, "mmproj");
        PromptSha256 = CheckSha(promptSha256, "prompt.sha256");
        SchemaSha256 = CheckSha(schemaSha256, "schema.sha256");
        if (temperature is null || temperature.Length == 0 || !IsDecimalNumber(temperature))
            throw Bad($"temperature は 10 進の数字でなければならない(実際「{temperature}」)", "temperature", "10 進の数字", temperature);
        Temperature = temperature;
        if (chatTemplateKwargs is null || chatTemplateKwargs.Contains('\n') || chatTemplateKwargs.Contains('\r'))
            throw Bad("chat-template-kwargs は 1 行の文字列でなければならない(無ければ空)", "chat-template-kwargs", "1 行", chatTemplateKwargs);
        ChatTemplateKwargs = chatTemplateKwargs;
        ImageMinTokens = CheckTokens(imageMinTokens, "image-min-tokens");
        ImageMaxTokens = CheckTokens(imageMaxTokens, "image-max-tokens");
        if (ImageMinTokens is { } lo && ImageMaxTokens is { } hi && lo > hi)
            throw Bad($"画像トークンの下限 {lo} が上限 {hi} より大きい", "image-tokens", "下限 ≤ 上限", $"{lo} > {hi}");
        if (string.IsNullOrEmpty(hfRepository) || !hfRepository.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_' or '/'))
            throw Bad($"Hugging Face のリポジトリ名が不正(実際「{hfRepository}」)", "hf_repo", "[A-Za-z0-9._/-]+", hfRepository);
        HfRepository = hfRepository;
        if (hfRevision is null || hfRevision.Length != 40 || !hfRevision.All(IsLowerHex))
            throw Bad($"Hugging Face のリビジョンは 40 桁の小文字 16 進でなければならない(実際「{hfRevision}」)", "hf_revision", "40 桁の小文字 16 進", hfRevision);
        HfRevision = hfRevision;
    }

    // ---------------------------------------------------------------- 「実際に送る文字列」のハッシュ

    /// <summary>
    /// プロンプトの sha256(UTF-8、小文字 16 進)。引数は**実際に送る文字列**で、末尾に CR・LF が付いていてはならない(評価では、ファイルを読んで
    /// 末尾の CR・LF を除いたものを送った。ここで除かず、付いていたら例外にして、送る文字列とハッシュの食い違いを防ぐ)。空も不可。
    /// </summary>
    public static string PromptSha256Of(string sentPrompt)
    {
        ArgumentNullException.ThrowIfNull(sentPrompt);
        if (sentPrompt.Length == 0) throw new ArgumentException("プロンプトが空", nameof(sentPrompt));
        if (sentPrompt.EndsWith('\n') || sentPrompt.EndsWith('\r'))
            throw new ArgumentException("プロンプトの末尾に CR か LF がある(送る文字列は末尾の改行を除いたもの)", nameof(sentPrompt));
        return Sha256Hex(sentPrompt);
    }

    /// <summary>
    /// スキーマの sha256。引数は**実際に送る形**の、空白なしの正準 JSON(JSON を読み込んで <see cref="JsonNode.ToJsonString(JsonSerializerOptions?)"/> で書き出した文字列。
    /// 評価のリクエストに入った形)。正準でなければ例外(整形つきのファイルの内容をそのまま渡させない)。
    /// </summary>
    public static string SchemaSha256Of(string canonicalSchemaJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalSchemaJson);
        string canonical = CanonicalizeSchema(canonicalSchemaJson);
        if (!string.Equals(canonical, canonicalSchemaJson, StringComparison.Ordinal))
            throw new ArgumentException("スキーマが空白なしの正準 JSON でない(CanonicalizeSchema を通した文字列を渡す)", nameof(canonicalSchemaJson));
        return Sha256Hex(canonicalSchemaJson);
    }

    /// <summary>スキーマの JSON(整形つきでもよい)を、空白なしの正準 JSON(リクエストに入る形。キーの順は元のまま)にする。<c>JsonNode.ToJsonString()</c> と同じ。</summary>
    public static string CanonicalizeSchema(string schemaJson)
    {
        ArgumentNullException.ThrowIfNull(schemaJson);
        JsonNode node = JsonNode.Parse(schemaJson) ?? throw new ArgumentException("スキーマが null", nameof(schemaJson));
        if (node is not JsonObject) throw new ArgumentException("スキーマが JSON のオブジェクトでない", nameof(schemaJson));
        return node.ToJsonString();
    }

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---------------------------------------------------------------- 正準文字列と model_id

    /// <summary>
    /// 正準文字列。固定の順・固定の書式(UTF-8、改行は LF、末尾も LF)。
    /// <code>
    /// ohanami-model-id/1
    /// kind=vlm
    /// gguf.sha256=&lt;64 桁&gt;
    /// mmproj.sha256=&lt;64 桁&gt;
    /// prompt.sha256=&lt;64 桁&gt;
    /// schema.sha256=&lt;64 桁&gt;
    /// temperature=0
    /// chat-template-kwargs=&lt;JSON または空&gt;
    /// image-min-tokens=&lt;数字 または default&gt;
    /// image-max-tokens=&lt;数字 または default&gt;
    /// </code>
    /// </summary>
    public string CanonicalString()
    {
        if (_canonical is not null) return _canonical;
        var sb = new StringBuilder();
        sb.Append(CanonicalHeader).Append('\n');
        sb.Append("kind=vlm\n");
        sb.Append("gguf.sha256=").Append(Gguf.Sha256).Append('\n');
        sb.Append("mmproj.sha256=").Append(Mmproj.Sha256).Append('\n');
        sb.Append("prompt.sha256=").Append(PromptSha256).Append('\n');
        sb.Append("schema.sha256=").Append(SchemaSha256).Append('\n');
        sb.Append("temperature=").Append(Temperature).Append('\n');
        sb.Append("chat-template-kwargs=").Append(ChatTemplateKwargs).Append('\n');
        sb.Append("image-min-tokens=").Append(TokensText(ImageMinTokens)).Append('\n');
        sb.Append("image-max-tokens=").Append(TokensText(ImageMaxTokens)).Append('\n');
        return _canonical = sb.ToString();
    }

    private static string TokensText(int? v) => v is null ? Default : v.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>model_id = family + ":" + 正準文字列の SHA-256 の先頭 <see cref="IdDigits"/> 桁(小文字 16 進)。</summary>
    public string ModelId
    {
        get
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalString()));
            return Family + ":" + Convert.ToHexStringLower(hash)[..IdDigits];
        }
    }

    // ---------------------------------------------------------------- spec_json

    private static readonly string[] RootKeys =
        ["format", "family", "files", "prompt_sha256", "schema_sha256", "temperature", "chat_template_kwargs", "image_min_tokens", "image_max_tokens", "hf_repo", "hf_revision"];
    private static readonly string[] FileKeys = ["role", "path", "sha256", "size"];

    /// <summary>models.spec_json に保存する JSON(1 行、キーの順は固定)。パス・サイズ・Hugging Face の情報も含む(model_id の入力ではないが、構成の記録として)。</summary>
    public string ToJson()
    {
        if (_json is not null) return _json;
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("format", SpecFormat);
            w.WriteString("family", Family);
            w.WriteStartArray("files");
            foreach ((string role, VlmFile f) in new[] { ("gguf", Gguf), ("mmproj", Mmproj) })
            {
                w.WriteStartObject();
                w.WriteString("role", role);
                w.WriteString("path", f.RelativePath);
                w.WriteString("sha256", f.Sha256);
                w.WriteNumber("size", f.Size);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("prompt_sha256", PromptSha256);
            w.WriteString("schema_sha256", SchemaSha256);
            w.WriteString("temperature", Temperature);
            w.WriteString("chat_template_kwargs", ChatTemplateKwargs);
            WriteNullableInt(w, "image_min_tokens", ImageMinTokens);
            WriteNullableInt(w, "image_max_tokens", ImageMaxTokens);
            w.WriteString("hf_repo", HfRepository);
            w.WriteString("hf_revision", HfRevision);
            w.WriteEndObject();
        }
        return _json = Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNullableInt(Utf8JsonWriter w, string key, int? v)
    {
        if (v is null) w.WriteNull(key); else w.WriteNumber(key, v.Value);
    }

    /// <summary>spec_json を読む。書式の版が違う・キーが足りない/余る/重複する・型が違う・値が不正なものは、すべて <see cref="ModelMismatchException"/>。</summary>
    public static VlmSpec FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad("spec_json は JSON のオブジェクトでなければならない", "spec_json", "object", root.ValueKind.ToString());
            Dictionary<string, JsonElement> props = ReadKeys(root, RootKeys, "spec_json");

            int format = GetInt(props["format"], "format");
            if (format != SpecFormat) throw Bad($"spec_json の書式の版 {format} を知らない(この版のコードは {SpecFormat})", "format", SpecFormat.ToString(CultureInfo.InvariantCulture), format.ToString(CultureInfo.InvariantCulture));

            JsonElement filesEl = props["files"];
            if (filesEl.ValueKind != JsonValueKind.Array) throw Bad("spec_json の files は配列でなければならない", "files", "array", filesEl.ValueKind.ToString());
            var files = new Dictionary<string, VlmFile>(StringComparer.Ordinal);
            foreach (JsonElement item in filesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Bad("spec_json の files の要素はオブジェクトでなければならない", "files", "object", item.ValueKind.ToString());
                Dictionary<string, JsonElement> fp = ReadKeys(item, FileKeys, "files の要素");
                string role = GetString(fp["role"], "role");
                if (role is not ("gguf" or "mmproj")) throw Bad($"VLM のファイルの役割「{role}」を知らない", role, "gguf / mmproj", role);
                if (!files.TryAdd(role, new VlmFile(GetString(fp["path"], "path"), GetString(fp["sha256"], "sha256"), GetLong(fp["size"], "size"))))
                    throw Bad($"VLM のファイルの役割「{role}」が 2 つある", role, "1 つ", "2 つ以上");
            }
            if (!files.TryGetValue("gguf", out VlmFile? gguf)) throw Bad("spec_json の files に gguf が無い", "gguf", "有り", "無し");
            if (!files.TryGetValue("mmproj", out VlmFile? mmproj)) throw Bad("spec_json の files に mmproj が無い", "mmproj", "有り", "無し");

            return new VlmSpec(
                GetString(props["family"], "family"), gguf, mmproj,
                GetString(props["prompt_sha256"], "prompt_sha256"), GetString(props["schema_sha256"], "schema_sha256"),
                GetString(props["temperature"], "temperature"), GetString(props["chat_template_kwargs"], "chat_template_kwargs"),
                GetNullableInt(props["image_min_tokens"], "image_min_tokens"), GetNullableInt(props["image_max_tokens"], "image_max_tokens"),
                GetString(props["hf_repo"], "hf_repo"), GetString(props["hf_revision"], "hf_revision"));
        }
        catch (JsonException e)
        {
            throw new ModelMismatchException("spec_json が JSON として読めない: " + e.Message, e);
        }
    }

    private static Dictionary<string, JsonElement> ReadKeys(JsonElement obj, string[] expected, string where)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (Array.IndexOf(expected, p.Name) < 0) throw Bad($"{where} に想定外のキー「{p.Name}」がある", p.Name, string.Join(",", expected), "想定外のキー");
            if (!map.TryAdd(p.Name, p.Value)) throw Bad($"{where} のキー「{p.Name}」が重複している", p.Name, "1 回", "2 回以上");
        }
        foreach (string k in expected)
        {
            if (!map.ContainsKey(k)) throw Bad($"{where} にキー「{k}」が無い", k, "有り", "無し");
        }
        return map;
    }

    private static string GetString(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.String) throw Bad($"spec_json の {key} は文字列でなければならない", key, "string", e.ValueKind.ToString());
        return e.GetString()!;
    }

    private static int GetInt(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int v)) throw Bad($"spec_json の {key} は整数でなければならない", key, "int", e.ValueKind.ToString());
        return v;
    }

    private static int? GetNullableInt(JsonElement e, string key) => e.ValueKind == JsonValueKind.Null ? null : GetInt(e, key);

    private static long GetLong(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out long v)) throw Bad($"spec_json の {key} は整数でなければならない", key, "long", e.ValueKind.ToString());
        return v;
    }

    // ---------------------------------------------------------------- 検査

    private static bool IsLowerHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');

    private static bool IsDecimalNumber(string s)
    {
        int dot = s.IndexOf('.');
        string whole = dot < 0 ? s : s[..dot];
        string? frac = dot < 0 ? null : s[(dot + 1)..];
        return whole.Length > 0 && whole.All(char.IsAsciiDigit) && (frac is null || (frac.Length > 0 && frac.All(char.IsAsciiDigit)));
    }

    private static string CheckToken(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || !value.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_'))
            throw Bad($"{name} は英数字と . - _ だけの空でない文字列でなければならない(実際「{value}」)", name, "[A-Za-z0-9._-]+", value);
        return value;
    }

    private static string CheckSha(string value, string name)
    {
        if (value is null || value.Length != 64 || !value.All(IsLowerHex))
            throw Bad($"{name} は小文字 16 進 64 桁でなければならない(実際「{value}」)", name, "64 桁の小文字 16 進", value);
        return value;
    }

    private static int? CheckTokens(int? v, string name)
    {
        if (v is < 1) throw Bad($"{name} は 1 以上でなければならない(実際 {v})", name, "1 以上", v?.ToString(CultureInfo.InvariantCulture));
        return v;
    }

    private static VlmFile CheckFile(VlmFile f, string role)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (string.IsNullOrEmpty(f.RelativePath) || f.RelativePath.Contains('\\') || f.RelativePath.StartsWith('/') || f.RelativePath.Contains(':')
            || f.RelativePath.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
            throw Bad($"{role} のパス「{f.RelativePath}」は、モデルのディレクトリからの相対パス(区切りは / )でなければならない", role, "相対パス", f.RelativePath);
        CheckSha(f.Sha256, role + " の sha256");
        if (f.Size <= 0) throw Bad($"{role} のサイズは 1 以上でなければならない(実際 {f.Size})", role, "1 以上", f.Size.ToString(CultureInfo.InvariantCulture));
        return f;
    }

    private static ModelMismatchException Bad(string message, string? subject, string? expected, string? actual) =>
        new("VLM の構成が不正: " + message, subject, expected, actual);

    // ---------------------------------------------------------------- 等価性(spec_json が同じなら同じ)

    public bool Equals(VlmSpec? other) => other is not null && string.Equals(ToJson(), other.ToJson(), StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as VlmSpec);

    public override int GetHashCode() => ToJson().GetHashCode(StringComparison.Ordinal);
}
