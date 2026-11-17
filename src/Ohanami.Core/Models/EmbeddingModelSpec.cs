using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ohanami.Core.Models;

/// <summary>
/// 埋め込みモデル(models.kind = image-embedding)の仕様。SigLIP2 の画像側・テキスト側を 1 つの model_id にまとめた形で、
/// 4 つの役割(画像側 ONNX、テキスト側 ONNX、外部データ、tokenizer)を過不足なく要求し、出力の正規化は l2 だけを受ける。
/// 他の kind(VLM など)は別の型の仕様を持つ(<see cref="ModelRegistry"/> が kind で読み方を分ける)。
/// 1 つのモデル(= 1 つの model_id)の中身: 構成するファイル(役割・パス・sha256・サイズ)、前処理の版、テキスト整理の版、次元、出力の正規化。
/// model_id は、このうち結果の数値を決めるものだけから組み立てる(<see cref="CanonicalString"/>)。ORT の版は入れない(ADR 0006)。
/// models.spec_json には同じ内容を JSON で保存し(<see cref="ToJson"/>)、行から読み戻して model_id を再計算できる(<see cref="FromJson"/>)。
/// 不正な値・壊れた JSON はすべて <see cref="ModelMismatchException"/>(黙って既定値にしない)。
/// </summary>
internal sealed class EmbeddingModelSpec : IEquatable<EmbeddingModelSpec>
{
    /// <summary>spec_json の書式の版。</summary>
    public const int SpecFormat = 1;

    /// <summary>正準文字列の 1 行目(書式の版つき)。書式を変えたら数字を上げる。</summary>
    public const string CanonicalHeader = "ohanami-model-id/1";

    /// <summary>出力の正規化の名前。L2 正規化(ノルム 1)。扱うのはこれだけ。</summary>
    public const string NormalizeL2 = "l2";

    /// <summary>model_id の「:」のあとの桁数(正準文字列の SHA-256 の先頭の 16 進)。</summary>
    public const int IdDigits = 12;

    private readonly ModelFile[] _files;
    private string? _canonical;
    private string? _json;

    /// <summary>モデルの種類を表す接頭辞(model_id の「:」の前)。例: siglip2-so400m-p16-384。</summary>
    public string Family { get; }

    /// <summary>構成するファイル。<see cref="ModelRoles.All"/> の順(固定)。</summary>
    public IReadOnlyList<ModelFile> Files => _files;

    public string ImagePreprocessVersion { get; }

    public string TextCleanVersion { get; }

    /// <summary>埋め込みの次元。</summary>
    public int Dim { get; }

    /// <summary>出力の正規化(<see cref="NormalizeL2"/>)。</summary>
    public string Normalize { get; }

    public EmbeddingModelSpec(string family, IEnumerable<ModelFile> files, string imagePreprocessVersion, string textCleanVersion, int dim, string normalize)
    {
        ArgumentNullException.ThrowIfNull(files);
        Family = CheckToken(family, "family");
        ImagePreprocessVersion = CheckToken(imagePreprocessVersion, "image_preprocess");
        TextCleanVersion = CheckToken(textCleanVersion, "text_clean");
        if (dim <= 0) throw Bad($"dim は 1 以上でなければならない(実際 {dim})", "dim", "1 以上", dim.ToString(CultureInfo.InvariantCulture));
        Dim = dim;
        if (normalize != NormalizeL2) throw Bad($"normalize は「{NormalizeL2}」だけを扱う(実際「{normalize}」)", "normalize", NormalizeL2, normalize);
        Normalize = normalize;

        var byRole = new Dictionary<ModelRole, ModelFile>();
        foreach (ModelFile f in files)
        {
            ArgumentNullException.ThrowIfNull(f);
            if (!byRole.TryAdd(f.Role, CheckFile(f)))
                throw Bad($"モデルの役割「{ModelRoles.ToText(f.Role)}」のファイルが 2 つある", ModelRoles.ToText(f.Role), "1 つ", "2 つ以上");
        }
        _files = new ModelFile[ModelRoles.All.Length];
        for (int i = 0; i < ModelRoles.All.Length; i++)
        {
            ModelRole role = ModelRoles.All[i];
            if (!byRole.TryGetValue(role, out ModelFile? f))
                throw Bad($"モデルの役割「{ModelRoles.ToText(role)}」のファイルが無い", ModelRoles.ToText(role), "1 つ", "無し");
            _files[i] = f;
        }
        if (byRole.Count != _files.Length) throw Bad("想定外の役割のファイルがある", null, null, null);
    }

    public ModelFile File(ModelRole role) => _files[Array.IndexOf(ModelRoles.All, role)];

    public IEnumerable<ModelFile> FilesOf(ModelSide side) => _files.Where(f => f.Side == side);

    /// <summary>
    /// 正準文字列。固定の順・固定の書式(UTF-8、改行は LF、末尾も LF)。
    /// <code>
    /// ohanami-model-id/1
    /// visual-onnx.sha256=&lt;64 桁&gt;
    /// textual-onnx.sha256=&lt;64 桁&gt;
    /// textual-onnx-data.sha256=&lt;64 桁&gt;
    /// tokenizer.sha256=&lt;64 桁&gt;
    /// image-preprocess=&lt;前処理の版&gt;
    /// text-clean=&lt;テキスト整理の版&gt;
    /// dim=&lt;次元&gt;
    /// normalize=l2
    /// </code>
    /// パスとサイズは入れない(内容の sha256 が決めるため。パスの変更で model_id が変わると全件が再処理になる)。
    /// </summary>
    public string CanonicalString()
    {
        if (_canonical is not null) return _canonical;
        var sb = new StringBuilder();
        sb.Append(CanonicalHeader).Append('\n');
        foreach (ModelFile f in _files) sb.Append(ModelRoles.ToText(f.Role)).Append(".sha256=").Append(f.Sha256).Append('\n');
        sb.Append("image-preprocess=").Append(ImagePreprocessVersion).Append('\n');
        sb.Append("text-clean=").Append(TextCleanVersion).Append('\n');
        sb.Append("dim=").Append(Dim.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("normalize=").Append(Normalize).Append('\n');
        return _canonical = sb.ToString();
    }

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

    private static readonly string[] RootKeys = ["format", "family", "files", "image_preprocess", "text_clean", "dim", "normalize"];
    private static readonly string[] FileKeys = ["role", "path", "sha256", "size"];

    /// <summary>
    /// models.spec_json に保存する JSON(1 行、キーの順は固定)。サイズとパスも含む(model_id の入力ではないが、構成の記録として)。
    /// </summary>
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
            foreach (ModelFile f in _files)
            {
                w.WriteStartObject();
                w.WriteString("role", ModelRoles.ToText(f.Role));
                w.WriteString("path", f.RelativePath);
                w.WriteString("sha256", f.Sha256);
                w.WriteNumber("size", f.Size);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("image_preprocess", ImagePreprocessVersion);
            w.WriteString("text_clean", TextCleanVersion);
            w.WriteNumber("dim", Dim);
            w.WriteString("normalize", Normalize);
            w.WriteEndObject();
        }
        return _json = Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// spec_json を読む。書式の版が違う・キーが足りない/余る/重複する・型が違う・値が不正なものは、すべて <see cref="ModelMismatchException"/>。
    /// </summary>
    public static EmbeddingModelSpec FromJson(string json)
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
            var files = new List<ModelFile>();
            foreach (JsonElement item in filesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Bad("spec_json の files の要素はオブジェクトでなければならない", "files", "object", item.ValueKind.ToString());
                Dictionary<string, JsonElement> fp = ReadKeys(item, FileKeys, "files の要素");
                files.Add(new ModelFile(ModelRoles.FromText(GetString(fp["role"], "role")), GetString(fp["path"], "path"), GetString(fp["sha256"], "sha256"), GetLong(fp["size"], "size")));
            }
            return new EmbeddingModelSpec(GetString(props["family"], "family"), files, GetString(props["image_preprocess"], "image_preprocess"),
                GetString(props["text_clean"], "text_clean"), GetInt(props["dim"], "dim"), GetString(props["normalize"], "normalize"));
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

    private static long GetLong(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out long v)) throw Bad($"spec_json の {key} は整数でなければならない", key, "long", e.ValueKind.ToString());
        return v;
    }

    // ---------------------------------------------------------------- 検査

    private static string CheckToken(string value, string name)
    {
        // 正準文字列の区切り(改行・"=")と model_id の区切り(":")を含まない、安全な文字だけ
        if (string.IsNullOrEmpty(value) || !value.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_'))
            throw Bad($"{name} は英数字と . - _ だけの空でない文字列でなければならない(実際「{value}」)", name, "[A-Za-z0-9._-]+", value);
        return value;
    }

    private static ModelFile CheckFile(ModelFile f)
    {
        string role = ModelRoles.ToText(f.Role);
        if (string.IsNullOrEmpty(f.RelativePath) || f.RelativePath.Contains('\\') || f.RelativePath.StartsWith('/') || f.RelativePath.Contains(':')
            || f.RelativePath.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
            throw Bad($"{role} のパス「{f.RelativePath}」は、モデルのディレクトリからの相対パス(区切りは / )でなければならない", role, "相対パス", f.RelativePath);
        if (f.Sha256 is null || f.Sha256.Length != 64 || !f.Sha256.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw Bad($"{role} の sha256 は小文字 16 進 64 桁でなければならない(実際「{f.Sha256}」)", role, "64 桁の小文字 16 進", f.Sha256);
        if (f.Size <= 0) throw Bad($"{role} のサイズは 1 以上でなければならない(実際 {f.Size})", role, "1 以上", f.Size.ToString(CultureInfo.InvariantCulture));
        return f;
    }

    private static ModelMismatchException Bad(string message, string? subject, string? expected, string? actual) =>
        new("モデルの構成が不正: " + message, subject, expected, actual);

    // ---------------------------------------------------------------- 等価性(spec_json が同じなら同じ)

    public bool Equals(EmbeddingModelSpec? other) => other is not null && string.Equals(ToJson(), other.ToJson(), StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as EmbeddingModelSpec);

    public override int GetHashCode() => ToJson().GetHashCode(StringComparison.Ordinal);
}
