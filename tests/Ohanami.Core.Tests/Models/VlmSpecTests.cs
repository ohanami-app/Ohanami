using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ohanami.Core.Models;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>VlmSpec の正準文字列・model_id・spec_json(ADR 0006)。GPU もモデルのファイルも要らない。</summary>
public sealed class VlmSpecTests
{
    private static readonly string S1 = new('1', 64);
    private static readonly string S2 = new('2', 64);
    private static readonly string S3 = new('3', 64);
    private static readonly string S4 = new('4', 64);
    private const string Rev = "f982a07559d4a2f6c8744d840bf6fccab30eea96";

    private static VlmSpec Spec(
        string family = "fam", string? gguf = null, string? mmproj = null, string? prompt = null, string? schema = null, string temperature = "0",
        string kwargs = "", int? min = null, int? max = 3584, string ggufPath = "m.gguf", long ggufSize = 100, string mmprojPath = "p.gguf", long mmprojSize = 50,
        string repo = "Qwen/Qwen3-VL-8B-Instruct-GGUF", string rev = Rev) =>
        new(family, new VlmFile(ggufPath, gguf ?? S1, ggufSize), new VlmFile(mmprojPath, mmproj ?? S2, mmprojSize), prompt ?? S3, schema ?? S4, temperature, kwargs, min, max, repo, rev);

    // ---------------------------------------------------------------- 正準文字列

    [Fact]
    public void Canonical_string_is_ten_fixed_lines_ending_with_a_line_feed()
    {
        string expected =
            "ohanami-model-id/1\n" +
            "kind=vlm\n" +
            $"gguf.sha256={S1}\n" +
            $"mmproj.sha256={S2}\n" +
            $"prompt.sha256={S3}\n" +
            $"schema.sha256={S4}\n" +
            "temperature=0\n" +
            "chat-template-kwargs=\n" +
            "image-min-tokens=default\n" +
            "image-max-tokens=3584\n";
        Assert.Equal(expected, Spec().CanonicalString());
        Assert.DoesNotContain('\r', Spec().CanonicalString());
    }

    [Fact]
    public void Model_id_is_the_family_and_the_first_twelve_hex_digits_of_the_canonical_strings_sha256()
    {
        VlmSpec spec = Spec();
        string hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(spec.CanonicalString())));
        Assert.Equal("fam:" + hex[..12], spec.ModelId);
        Assert.Equal(Spec().ModelId, spec.ModelId); // 決定的
    }

    [Fact]
    public void Default_image_tokens_are_written_as_the_word_default()
    {
        Assert.Contains("image-max-tokens=default\n", Spec(max: null).CanonicalString(), StringComparison.Ordinal);
        Assert.Contains("image-min-tokens=1024\n", Spec(min: 1024).CanonicalString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- id を変えるもの / 変えないもの

    [Fact]
    public void Every_element_that_defines_the_output_changes_the_model_id()
    {
        string baseId = Spec().ModelId;
        var changed = new (string What, string Id)[]
        {
            ("gguf", Spec(gguf: new string('9', 64)).ModelId),
            ("mmproj", Spec(mmproj: new string('9', 64)).ModelId),
            ("prompt", Spec(prompt: new string('9', 64)).ModelId),
            ("schema", Spec(schema: new string('9', 64)).ModelId),
            ("temperature", Spec(temperature: "0.1").ModelId),
            ("chat-template-kwargs", Spec(kwargs: "{\"enable_thinking\":false}").ModelId),
            ("image-min-tokens", Spec(min: 256).ModelId),
            ("image-max-tokens", Spec(max: 4096).ModelId),
            ("image-max-tokens を既定に", Spec(max: null).ModelId),
        };
        foreach ((string what, string id) in changed) Assert.NotEqual(baseId, id);
        Assert.Equal(changed.Length, changed.Select(c => c.Id).Distinct().Count()); // どれも別の id
        Assert.StartsWith("other:", Spec(family: "other").ModelId, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_sizes_and_the_hugging_face_pointer_do_not_change_the_model_id_but_change_the_json()
    {
        VlmSpec a = Spec();
        VlmSpec b = Spec(ggufPath: "sub/other.gguf", ggufSize: 999, mmprojPath: "sub/other-mmproj.gguf", mmprojSize: 777, repo: "someone/else", rev: new string('0', 40));
        Assert.Equal(a.CanonicalString(), b.CanonicalString());
        Assert.Equal(a.ModelId, b.ModelId);
        Assert.NotEqual(a.ToJson(), b.ToJson());
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Llama_server_build_max_tokens_retry_settings_and_memory_settings_are_not_in_the_identity()
    {
        // ビルドを上げるだけで全件が要再作成(約 36 時間)にならないこと。これらは runs.runtime_json と captions.settings_json に記録する
        string canonical = VlmCatalog.Current.CanonicalString();
        foreach (string forbidden in new[]
        {
            "build", "12110", VlmCatalog.LlamaServerCommit, VlmCatalog.LlamaServerExeSha256, "llama-server", "max_tokens", "max-tokens=2048", "dry", "repeat", "penalty",
            "ngl", "cram", "mmproj-offload", "-c ", "8192",
        })
            Assert.DoesNotContain(forbidden, canonical, StringComparison.OrdinalIgnoreCase);
        // 入っている行は、ちょうど決めた 10 行
        Assert.Equal(10, canonical.TrimEnd('\n').Split('\n').Length);
        string json = VlmCatalog.Current.ToJson();
        Assert.DoesNotContain("\"max_tokens\"", json, StringComparison.Ordinal); // image_max_tokens(画像トークンの上限)は入る。生成の max_tokens は入らない
        Assert.DoesNotContain(VlmCatalog.LlamaServerExeSha256, json, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- spec_json

    [Fact]
    public void Json_round_trips_to_an_equal_spec_with_the_same_model_id()
    {
        VlmSpec spec = Spec(min: 128, kwargs: "{\"a\":1}");
        VlmSpec back = VlmSpec.FromJson(spec.ToJson());
        Assert.True(back.Equals(spec));
        Assert.Equal(spec.ModelId, back.ModelId);
        Assert.Equal(spec.ToJson(), back.ToJson());
        VlmSpec defaults = VlmSpec.FromJson(Spec(min: null, max: null).ToJson());
        Assert.Null(defaults.ImageMinTokens);
        Assert.Null(defaults.ImageMaxTokens);
        Assert.DoesNotContain('\n', spec.ToJson()); // 1 行
    }

    [Fact]
    public void Json_has_a_fixed_key_order()
    {
        string json = Spec().ToJson();
        string[] keys = ["\"format\"", "\"family\"", "\"files\"", "\"prompt_sha256\"", "\"schema_sha256\"", "\"temperature\"", "\"chat_template_kwargs\"", "\"image_min_tokens\"", "\"image_max_tokens\"", "\"hf_repo\"", "\"hf_revision\""];
        int last = -1;
        foreach (string k in keys)
        {
            int at = json.IndexOf(k, StringComparison.Ordinal);
            Assert.True(at > last, $"{k} の位置が想定の順でない");
            last = at;
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Broken_json_is_a_model_mismatch(string json)
    {
        Assert.Throws<ModelMismatchException>(() => VlmSpec.FromJson(json));
    }

    [Fact]
    public void Json_with_a_wrong_format_version_a_missing_key_an_extra_key_or_a_wrong_type_is_rejected()
    {
        string good = Spec().ToJson();
        Assert.Throws<ModelMismatchException>(() => VlmSpec.FromJson(good.Replace("\"format\":1", "\"format\":2", StringComparison.Ordinal)));
        Assert.Throws<ModelMismatchException>(() => VlmSpec.FromJson(good.Replace("\"temperature\":\"0\",", "", StringComparison.Ordinal)));
        Assert.Throws<ModelMismatchException>(() => VlmSpec.FromJson(good.Replace("\"format\":1,", "\"format\":1,\"extra\":1,", StringComparison.Ordinal)));
        Assert.Throws<ModelMismatchException>(() => VlmSpec.FromJson(good.Replace("\"temperature\":\"0\"", "\"temperature\":0", StringComparison.Ordinal)));
        Assert.Throws<ModelMismatchException>(() => VlmSpec.FromJson(good.Replace("\"image_max_tokens\":3584", "\"image_max_tokens\":\"3584\"", StringComparison.Ordinal)));
        Assert.Throws<ModelMismatchException>(() => VlmSpec.FromJson(good.Replace("\"role\":\"gguf\"", "\"role\":\"weights\"", StringComparison.Ordinal)));
        Assert.Throws<ModelMismatchException>(() => VlmSpec.FromJson(good.Replace("\"role\":\"mmproj\"", "\"role\":\"gguf\"", StringComparison.Ordinal))); // gguf が 2 つ、mmproj が無い
        Assert.Throws<ModelMismatchException>(() => VlmSpec.FromJson(good.Replace("\"format\":1,\"family\":\"fam\",", "\"format\":1,\"family\":\"fam\",\"family\":\"again\",", StringComparison.Ordinal))); // キーの重複
    }

    // ---------------------------------------------------------------- 値の検査

    [Fact]
    public void Constructor_rejects_invalid_values_loudly()
    {
        Assert.Throws<ModelMismatchException>(() => Spec(family: "has space"));
        Assert.Throws<ModelMismatchException>(() => Spec(family: "has:colon"));
        Assert.Throws<ModelMismatchException>(() => Spec(gguf: "ABC"));
        Assert.Throws<ModelMismatchException>(() => Spec(prompt: new string('A', 64)));   // 大文字
        Assert.Throws<ModelMismatchException>(() => Spec(schema: "short"));
        Assert.Throws<ModelMismatchException>(() => Spec(temperature: ""));
        Assert.Throws<ModelMismatchException>(() => Spec(temperature: "-1"));
        Assert.Throws<ModelMismatchException>(() => Spec(temperature: "1."));
        Assert.Throws<ModelMismatchException>(() => Spec(temperature: "abc"));
        Assert.Throws<ModelMismatchException>(() => Spec(kwargs: "a\nb"));
        Assert.Throws<ModelMismatchException>(() => Spec(min: 0));
        Assert.Throws<ModelMismatchException>(() => Spec(max: -5));
        Assert.Throws<ModelMismatchException>(() => Spec(min: 2000, max: 1000));
        Assert.Throws<ModelMismatchException>(() => Spec(ggufPath: "..\\x"));
        Assert.Throws<ModelMismatchException>(() => Spec(ggufPath: "/abs"));
        Assert.Throws<ModelMismatchException>(() => Spec(ggufPath: "a/../b"));
        Assert.Throws<ModelMismatchException>(() => Spec(ggufSize: 0));
        Assert.Throws<ModelMismatchException>(() => Spec(repo: ""));
        Assert.Throws<ModelMismatchException>(() => Spec(rev: "main"));
        Assert.Throws<ModelMismatchException>(() => Spec(rev: new string('F', 40)));
        Assert.True(Spec(temperature: "0.7").Temperature == "0.7");
    }

    // ---------------------------------------------------------------- 「実際に送る文字列」のハッシュ

    [Fact]
    public void Prompt_hash_is_the_sha256_of_the_exact_text_and_a_trailing_newline_is_refused()
    {
        string text = "このスクリーンショットについて";
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), VlmSpec.PromptSha256Of(text));
        Assert.Throws<ArgumentException>(() => VlmSpec.PromptSha256Of(text + "\n"));
        Assert.Throws<ArgumentException>(() => VlmSpec.PromptSha256Of(text + "\r\n"));
        Assert.Throws<ArgumentException>(() => VlmSpec.PromptSha256Of(""));
        Assert.NotEqual(VlmSpec.PromptSha256Of(text), VlmSpec.PromptSha256Of(text + " ")); // 末尾の空白は文字列の一部
        Assert.NotEqual(VlmSpec.PromptSha256Of("a\nb"), VlmSpec.PromptSha256Of("a\r\nb")); // 途中の改行の種類も区別する(正規化しない)
    }

    [Fact]
    public void Schema_hash_is_taken_over_the_compact_canonical_json_and_a_pretty_printed_text_is_refused()
    {
        string pretty = "{\n  \"type\": \"object\",\n  \"required\": [ \"a\" ]\n}\n";
        string compact = VlmSpec.CanonicalizeSchema(pretty);
        Assert.Equal("{\"type\":\"object\",\"required\":[\"a\"]}", compact);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(compact))), VlmSpec.SchemaSha256Of(compact));
        Assert.Throws<ArgumentException>(() => VlmSpec.SchemaSha256Of(pretty));
        Assert.Throws<ArgumentException>(() => VlmSpec.CanonicalizeSchema("[1]"));
        Assert.Throws<ArgumentException>(() => VlmSpec.CanonicalizeSchema("null"));
        // キーの順は元のまま(並べ替えない)
        Assert.Equal("{\"b\":1,\"a\":2}", VlmSpec.CanonicalizeSchema("{ \"b\": 1, \"a\": 2 }"));
    }
}
