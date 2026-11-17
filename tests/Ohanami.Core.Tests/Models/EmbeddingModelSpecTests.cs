using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ohanami.Core.Embedding;
using Ohanami.Core.Models;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>model_id の組み立て(正準文字列)と spec_json の往復・検証。ファイルは読まない(GPU もモデルも要らない)。</summary>
public sealed class EmbeddingModelSpecTests
{
    // 現在のモデルの正準文字列と model_id の「最終形」。書式や入力を変えたら、ここが落ちる(= model_id が変わる=全件が要再作成になる変更だと気づける)。
    private const string ExpectedCanonical =
        "ohanami-model-id/1\n" +
        "visual-onnx.sha256=c801892ee1737a913b729ac19f4b1ed69bbcc80755b568e7cd8266e9c8f32b6e\n" +
        "textual-onnx.sha256=785486a7ed6c5a0209e31740ed35e9fc806ef4a5aa1ac37bc0f3a5d7137f07df\n" +
        "textual-onnx-data.sha256=b04effd92d0ea4dd30063018b29b9811e46367e119f9fc5b51879e27f32290bc\n" +
        "tokenizer.sha256=220c63d496e0c14e63eb656c91e0215e926202e4c74b1f089e09f1920d779b04\n" +
        "image-preprocess=siglip2-squash384-bicubic-pillow-v1\n" +
        "text-clean=siglip2-textclean-v1\n" +
        "dim=1152\n" +
        "normalize=l2\n";

    private const string ExpectedModelId = "siglip2-so400m-p16-384:ab172dd56bbb";

    private static readonly string[] Shas =
    [
        new string('1', 64), new string('2', 64), new string('3', 64), new string('4', 64),
    ];

    private static List<ModelFile> Files(string[]? shas = null, long size = 100)
    {
        shas ??= Shas;
        return
        [
            new ModelFile(ModelRole.VisualOnnx, "visual/model.onnx", shas[0], size),
            new ModelFile(ModelRole.TextualOnnx, "textual/model.onnx", shas[1], size),
            new ModelFile(ModelRole.TextualExternalData, "textual/model.onnx_data", shas[2], size),
            new ModelFile(ModelRole.Tokenizer, "textual/tokenizer.json", shas[3], size),
        ];
    }

    private static EmbeddingModelSpec Spec(List<ModelFile>? files = null, string pre = "pre-v1", string clean = "clean-v1", int dim = 1152, string normalize = "l2", string family = "fam") =>
        new(family, files ?? Files(), pre, clean, dim, normalize);

    // ---------------------------------------------------------------- 現在のモデルの最終形

    [Fact]
    public void Current_model_has_the_expected_canonical_string_and_model_id()
    {
        Assert.Equal(ExpectedCanonical, ModelCatalog.Current.CanonicalString());
        Assert.Equal(ExpectedModelId, ModelCatalog.Current.ModelId);
        // 版の文字列は、評価で使った定数そのもの
        Assert.Equal(Siglip2ImagePreprocessor.SpecVersion, ModelCatalog.Current.ImagePreprocessVersion);
        Assert.Equal(TextCleaner.SpecVersion, ModelCatalog.Current.TextCleanVersion);
        Assert.Equal(1152, ModelCatalog.Current.Dim);
        Assert.Equal("l2", ModelCatalog.Current.Normalize);
    }

    [Fact]
    public void Current_model_spec_json_is_one_line_with_a_fixed_key_order()
    {
        string json = ModelCatalog.Current.ToJson();
        Assert.DoesNotContain('\n', json);
        Assert.StartsWith("{\"format\":1,\"family\":\"siglip2-so400m-p16-384\",\"files\":[{\"role\":\"visual-onnx\",\"path\":\"visual/model.onnx\",\"sha256\":\"c801892e", json);
        Assert.EndsWith("],\"image_preprocess\":\"siglip2-squash384-bicubic-pillow-v1\",\"text_clean\":\"siglip2-textclean-v1\",\"dim\":1152,\"normalize\":\"l2\"}", json);
    }

    // ---------------------------------------------------------------- model_id の性質

    [Fact]
    public void Model_id_has_the_family_prefix_and_12_lowercase_hex_digits()
    {
        Assert.Matches("^fam:[0-9a-f]{12}$", Spec().ModelId);
    }

    [Fact]
    public void Same_input_gives_the_same_model_id_regardless_of_the_order_the_files_are_given()
    {
        var shuffled = Files().AsEnumerable().Reverse().ToList();
        Assert.Equal(Spec().ModelId, Spec().ModelId);
        Assert.Equal(Spec().ModelId, Spec(shuffled).ModelId);
        Assert.Equal(Spec().CanonicalString(), Spec(shuffled).CanonicalString());
        Assert.Equal(Spec().ToJson(), Spec(shuffled).ToJson());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Changing_the_sha256_of_any_file_changes_the_model_id(int index)
    {
        string[] changed = (string[])Shas.Clone();
        changed[index] = new string('a', 64);
        Assert.NotEqual(Spec().ModelId, Spec(Files(changed)).ModelId);
    }

    [Fact]
    public void Changing_a_version_string_the_dimension_or_the_family_changes_the_model_id()
    {
        string baseId = Spec().ModelId;
        Assert.NotEqual(baseId, Spec(pre: "pre-v2").ModelId);
        Assert.NotEqual(baseId, Spec(clean: "clean-v2").ModelId);
        Assert.NotEqual(baseId, Spec(dim: 768).ModelId);
        // 接頭辞が違えば id が違う(正準文字列は同じ)
        Assert.NotEqual(baseId, Spec(family: "other").ModelId);
        Assert.Equal(Spec().CanonicalString(), Spec(family: "other").CanonicalString());
    }

    [Fact]
    public void Swapping_the_hashes_between_two_roles_changes_the_model_id()
    {
        string[] swapped = [Shas[1], Shas[0], Shas[2], Shas[3]];
        Assert.NotEqual(Spec().ModelId, Spec(Files(swapped)).ModelId);
    }

    [Fact]
    public void Sizes_are_not_part_of_the_model_id_but_are_part_of_the_spec_json()
    {
        // 内容(sha256)が同じなら同じモデル。サイズは spec_json に記録するだけ(model_id の入力ではない)
        EmbeddingModelSpec a = Spec(Files(size: 100));
        EmbeddingModelSpec b = Spec(Files(size: 200));
        Assert.Equal(a.ModelId, b.ModelId);
        Assert.NotEqual(a.ToJson(), b.ToJson());
        Assert.False(a.Equals(b));
    }

    // ---------------------------------------------------------------- spec_json の往復

    [Fact]
    public void Spec_json_round_trips_to_an_equal_spec_with_the_same_model_id()
    {
        EmbeddingModelSpec original = Spec();
        EmbeddingModelSpec back = EmbeddingModelSpec.FromJson(original.ToJson());
        Assert.Equal(original.ToJson(), back.ToJson());
        Assert.Equal(original.ModelId, back.ModelId);
        Assert.True(original.Equals(back));
        Assert.Equal(original.Files, back.Files);
        // 現在のモデルも
        Assert.Equal(ModelCatalog.Current.ModelId, EmbeddingModelSpec.FromJson(ModelCatalog.Current.ToJson()).ModelId);
    }

    [Fact]
    public void Spec_json_with_the_files_in_another_order_reads_into_the_same_spec()
    {
        string json = Spec().ToJson();
        const string StartMarker = "\"files\":[";
        const string EndMarker = "],\"image_preprocess\"";
        int start = json.IndexOf(StartMarker, StringComparison.Ordinal) + StartMarker.Length;
        int end = json.IndexOf(EndMarker, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(json);
        string[] files = doc.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetRawText()).Reverse().ToArray();
        string reordered = json[..start] + string.Join(",", files) + json[end..];
        Assert.NotEqual(json, reordered);
        Assert.Equal(Spec().ModelId, EmbeddingModelSpec.FromJson(reordered).ModelId);
        Assert.Equal(json, EmbeddingModelSpec.FromJson(reordered).ToJson());
    }

    public static IEnumerable<object[]> BrokenJson()
    {
        string good = Spec().ToJson();
        yield return ["", "空文字列"];
        yield return ["not json", "JSON でない"];
        yield return ["[]", "配列"];
        yield return ["{}", "キーが無い"];
        yield return [Replaced(good, "\"format\":1", "\"format\":2"), "知らない書式の版"];
        yield return [Replaced(good, "\"format\":1", "\"format\":\"1\""), "format が文字列"];
        yield return [Replaced(good, "{\"format\":1,", "{\"format\":1,\"extra\":1,"), "想定外のキー"];
        yield return [Replaced(good, "\"normalize\":\"l2\"", "\"normalize\":\"l2\",\"dim\":1152"), "キーの重複"];
        yield return [Replaced(good, "\"dim\":1152", "\"dim\":\"1152\""), "dim が文字列"];
        yield return [Replaced(good, "\"dim\":1152", "\"dim\":0"), "dim が 0"];
        yield return [Replaced(good, "\"normalize\":\"l2\"", "\"normalize\":\"none\""), "知らない正規化"];
        yield return [Replaced(good, "\"role\":\"tokenizer\"", "\"role\":\"unknown\""), "知らない役割"];
        yield return [Replaced(good, "\"role\":\"tokenizer\"", "\"role\":\"visual-onnx\""), "役割の重複(tokenizer が無くなる)"];
        yield return [Replaced(good, new string('4', 64), new string('4', 63)), "sha256 が 63 桁"];
        yield return [Replaced(good, new string('4', 64), new string('g', 64)), "sha256 が 16 進でない"];
        yield return [Replaced(good, new string('4', 64), new string('A', 64)), "sha256 が大文字"];
        yield return [Replaced(good, "\"size\":100", "\"size\":0"), "サイズが 0"];
        yield return [Replaced(good, "\"path\":\"visual/model.onnx\"", "\"path\":\"../visual/model.onnx\""), "パスに .."];
        yield return [Replaced(good, "\"path\":\"visual/model.onnx\"", "\"path\":\"visual\\\\model.onnx\""), "パスに \\"];
        yield return [Replaced(good, "\"family\":\"fam\"", "\"family\":\"a:b\""), "family に :"];
        yield return [Replaced(good, "\"image_preprocess\":\"pre-v1\"", "\"image_preprocess\":\"\""), "版が空"];
        yield return [good[..^1], "閉じていない"];
    }

    /// <summary>置き換えが実際に起きたことを確かめてから返す(テストの入力の作り間違いで、壊れていない JSON を「壊れた」として渡さないため)。</summary>
    private static string Replaced(string text, string from, string to)
    {
        Assert.Contains(from, text, StringComparison.Ordinal);
        return text.Replace(from, to, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BrokenJson))]
    public void Broken_spec_json_throws_ModelMismatchException(string json, string what)
    {
        Exception e = Assert.ThrowsAny<Exception>(() => EmbeddingModelSpec.FromJson(json));
        Assert.True(e is ModelMismatchException, $"{what}: ModelMismatchException が期待だが {e.GetType().Name}: {e.Message}");
    }

    [Fact]
    public void A_spec_must_have_each_role_exactly_once()
    {
        List<ModelFile> files = Files();
        Assert.Throws<ModelMismatchException>(() => Spec(files.Take(3).ToList()));
        Assert.Throws<ModelMismatchException>(() => Spec([.. files, files[0]]));
    }

    [Fact]
    public void Only_l2_normalization_is_accepted()
    {
        Assert.Throws<ModelMismatchException>(() => Spec(normalize: "none"));
    }
}
