using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Ohanami.Core;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>VLM の評価の run の読み込みと、製品の構成との突き合わせ。合成の JSON で、DB も GPU も私物の run も使わない。</summary>
public sealed class VlmEvalRunTests
{
    internal const string Prompt = "このスクリーンショットについて JSON で出力してください。";
    internal const string Schema = "{\"type\":\"object\",\"properties\":{\"ocr\":{\"type\":\"string\"}},\"additionalProperties\":false}";

    /// <summary>評価の画像 1 枚の JSON(必要なところだけ)。</summary>
    internal static string ImageJson(
        string file = "a.png", string finish = "stop", string? content = "{\"ocr\":\"セリフ\",\"description\":\"説明\",\"tags\":[\"銀髪\",\"猫\"]}",
        bool? parsedOk = null, string? parsedOcr = "セリフ", string parsedDesc = "説明", string[]? parsedTags = null, int http = 200,
        int promptTokens = 1000, int completionTokens = 100, string prompt = Prompt, string schema = Schema, double temperature = 0, int maxTokens = 4096)
    {
        var responseBody = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = finish, ["message"] = new JsonObject { ["content"] = content } }),
        };
        var obj = new JsonObject
        {
            ["filename"] = file,
            ["http_status"] = http,
            ["finish_reason"] = finish,
            ["request"] = new JsonObject
            {
                ["body_without_base64"] = new JsonObject
                {
                    ["messages"] = new JsonArray(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "image_url" }, new JsonObject { ["type"] = "text", ["text"] = prompt }),
                    }),
                    ["temperature"] = temperature,
                    ["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["schema"] = JsonNode.Parse(schema) } },
                    ["max_tokens"] = maxTokens,
                },
            },
            ["response_body"] = responseBody.ToJsonString(),
            ["usage"] = new JsonObject { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens },
        };
        bool ok = parsedOk ?? (finish == "stop" && http == 200);
        obj["parsed"] = ok
            ? new JsonObject { ["ok"] = true, ["ocr"] = parsedOcr, ["description"] = parsedDesc, ["tags"] = new JsonArray((parsedTags ?? ["銀髪", "猫"]).Select(t => (JsonNode?)t).ToArray()), ["error"] = null }
            : new JsonObject { ["ok"] = false, ["ocr"] = null, ["description"] = null, ["tags"] = null, ["error"] = "x" };
        return obj.ToJsonString();
    }

    // ---------------------------------------------------------------- 画像 1 枚の JSON

    [Fact]
    public void A_successful_image_is_read_with_its_texts_tokens_and_request_settings()
    {
        VlmEvalImage i = VlmEvalRun.ParseImageJson(ImageJson());
        Assert.True(i.ParsedOk);
        Assert.Equal("a.png", i.FileName);
        Assert.Equal("セリフ", i.Ocr);
        Assert.Equal("説明", i.Description);
        Assert.Equal(["銀髪", "猫"], i.Tags);
        Assert.Equal(1000, i.PromptTokens);
        Assert.Equal(100, i.CompletionTokens);
        Assert.Equal(Prompt, i.RequestText);
        Assert.Equal(Schema, i.RequestSchemaJson);
        Assert.Equal(0.0, i.Temperature);
        Assert.Equal(4096, i.MaxTokens);
        Assert.Equal("stop", i.FinishReason);
    }

    [Fact]
    public void File_names_are_normalized_to_nfc()
    {
        string nfd = "ガ.png".Normalize(System.Text.NormalizationForm.FormD);
        Assert.Equal("ガ.png".Normalize(System.Text.NormalizationForm.FormC), VlmEvalRun.ParseImageJson(ImageJson(file: nfd)).FileName);
    }

    [Fact]
    public void A_length_finish_is_a_failure_even_when_the_content_happens_to_be_valid_json()
    {
        VlmEvalImage i = VlmEvalRun.ParseImageJson(ImageJson(finish: "length"));
        Assert.False(i.ParsedOk);
        Assert.Null(i.Ocr);
        Assert.Contains("finish_reason=length", i.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_200_status_is_a_failure()
    {
        VlmEvalImage i = VlmEvalRun.ParseImageJson(ImageJson(http: 500));
        Assert.False(i.ParsedOk);
        Assert.Equal(500, i.HttpStatus);
    }

    [Theory]
    [InlineData("{\"ocr\":\"a\",\"description\":\"b\"}")]                                            // tags が無い
    [InlineData("{\"ocr\":\"a\",\"description\":\"b\",\"tags\":\"x\"}")]                              // tags が配列でない
    [InlineData("{\"ocr\":\"a\",\"description\":\"b\",\"tags\":[1]}")]                                // tags の要素が文字列でない
    [InlineData("{\"ocr\":1,\"description\":\"b\",\"tags\":[]}")]                                    // ocr が文字列でない
    [InlineData("{\"ocr\":\"a\",\"tags\":[]}")]                                                      // description が無い
    [InlineData("{\"ocr\":\"a\",\"description\":\"b\",\"tags\":[],\"extra\":1}")]                    // 想定外のプロパティ
    [InlineData("[]")]                                                                                // オブジェクトでない
    [InlineData("not json")]
    public void A_content_that_is_not_exactly_the_schema_is_a_failure(string content)
    {
        VlmEvalImage i = VlmEvalRun.ParseImageJson(ImageJson(content: content, parsedOk: false));
        Assert.False(i.ParsedOk);
        Assert.NotNull(i.Note);
    }

    [Fact]
    public void An_empty_tags_array_and_an_empty_ocr_are_fine()
    {
        VlmEvalImage i = VlmEvalRun.ParseImageJson(ImageJson(content: "{\"ocr\":\"\",\"description\":\"d\",\"tags\":[]}", parsedOcr: "", parsedDesc: "d", parsedTags: []));
        Assert.True(i.ParsedOk);
        Assert.Equal("", i.Ocr);
        Assert.Empty(i.Tags!);
    }

    [Fact]
    public void A_disagreement_between_the_evaluation_parse_and_the_strict_parse_is_a_loud_error()
    {
        // 評価は成功としたが、こちらの厳格なパスでは失敗(余分なプロパティ)
        Assert.Throws<InvalidDataException>(() => VlmEvalRun.ParseImageJson(ImageJson(content: "{\"ocr\":\"a\",\"description\":\"b\",\"tags\":[],\"x\":1}", parsedOk: true)));
        // 値が違う
        Assert.Throws<InvalidDataException>(() => VlmEvalRun.ParseImageJson(ImageJson(parsedOcr: "別の文字")));
        Assert.Throws<InvalidDataException>(() => VlmEvalRun.ParseImageJson(ImageJson(parsedDesc: "別の説明")));
        Assert.Throws<InvalidDataException>(() => VlmEvalRun.ParseImageJson(ImageJson(parsedTags: ["猫", "銀髪"])));      // 順序も同じでなければならない
    }

    [Fact]
    public void Reading_a_folder_skips_the_non_image_json_and_rejects_a_duplicated_image()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bench-s1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), ImageJson(file: "a.png"));
            File.WriteAllText(Path.Combine(dir, "b.json"), ImageJson(file: "b.png", finish: "length"));
            File.WriteAllText(Path.Combine(dir, "run-info.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "schema.json"), "{\"type\":\"object\"}");
            IReadOnlyList<VlmEvalImage> images = VlmEvalRun.ReadImages(dir);
            Assert.Equal(["a.png", "b.png"], images.Select(i => i.FileName));
            Assert.Equal([true, false], images.Select(i => i.ParsedOk));

            File.WriteAllText(Path.Combine(dir, "c.json"), ImageJson(file: "a.png"));
            Assert.Throws<InvalidDataException>(() => VlmEvalRun.ReadImages(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_folder_without_image_results_is_an_error()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bench-s1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.Throws<InvalidDataException>(() => VlmEvalRun.ReadImages(dir)); }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------------------------------------------------------- run-info.json

    private const string ServerArgsTail = "-lm none -ngl 99 -c 8192 -np 1 -cram 0 --no-mmproj-offload";

    internal static string RunInfoJson(string args = ServerArgsTail, double temperature = 0, long modelBytes = 100, long mmprojBytes = 50, string model = "m.gguf", string mmproj = "p.gguf") =>
        new JsonObject
        {
            ["label"] = "x",
            ["model"] = new JsonObject { ["path"] = @"C:\models\Qwen\" + model, ["bytes"] = modelBytes },
            ["mmproj"] = new JsonObject { ["path"] = @"C:\models\Qwen\" + mmproj, ["bytes"] = mmprojBytes },
            ["server_args"] = $"-m \"C:\\models\\Qwen\\{model}\" --mmproj \"C:\\models\\Qwen\\{mmproj}\" --host 127.0.0.1 --port 61843 {args}",
            ["temperature"] = temperature,
            ["max_tokens"] = 4096,
        }.ToJsonString();

    [Fact]
    public void Run_info_gives_the_file_names_sizes_args_and_settings()
    {
        VlmEvalRun.RunInfo info = VlmEvalRun.ReadRunInfo(RunInfoJson());
        Assert.Equal("m.gguf", info.ModelFileName);
        Assert.Equal(100, info.ModelBytes);
        Assert.Equal("p.gguf", info.MmprojFileName);
        Assert.Equal(50, info.MmprojBytes);
        Assert.Equal(0.0, info.Temperature);
        Assert.Equal(4096, info.MaxTokens);
        Assert.Equal(ServerArgsTail, VlmEvalRun.LaunchFlagsOf(info.ServerArgs));
    }

    [Fact]
    public void Launch_flags_need_a_port_and_a_missing_piece_of_run_info_is_an_error()
    {
        Assert.Throws<InvalidDataException>(() => VlmEvalRun.LaunchFlagsOf("-m x --mmproj y"));
        Assert.Equal("", VlmEvalRun.LaunchFlagsOf("-m x --port 1234"));
        Assert.Throws<InvalidDataException>(() => VlmEvalRun.ReadRunInfo("{}"));
        Assert.Throws<InvalidDataException>(() => VlmEvalRun.ReadRunInfo("[]"));
    }
}

/// <summary>製品の VLM の構成が評価と同じことの確認。1 つでも違えば取り込まない。</summary>
public sealed class VlmEvalChecksTests
{
    private static VlmEvalExpectations Expect(int imageMaxTokens = 3584) =>
        new(VlmEvalRunTests.Prompt, VlmEvalRunTests.Schema, "m.gguf", 100, new string('1', 64), "p.gguf", 50, new string('2', 64), 12110, "cd959bae9",
            "-lm none -ngl 99 -c 8192 -np 1 -cram 0 --no-mmproj-offload", imageMaxTokens, 2048, "0");

    private static List<ManifestRow> Manifest(string ggufSha = "", string match = "OK") =>
    [
        new("Qwen/x", "rev", "m.gguf", 100, ggufSha.Length == 0 ? new string('1', 64) : ggufSha, match),
        new("Qwen/x", "rev", "p.gguf", 50, new string('2', 64), "OK"),
    ];

    private static VlmEvalRunFiles Run(IReadOnlyList<VlmEvalImage>? images = null, string? runInfo = null, string? prompt = null, string? schema = null, string? version = null) =>
        new(prompt ?? VlmEvalRunTests.Prompt + "\n", schema ?? "{\n  \"type\": \"object\",\n  \"properties\": { \"ocr\": { \"type\": \"string\" } },\n  \"additionalProperties\": false\n}\n",
            runInfo ?? VlmEvalRunTests.RunInfoJson(), version ?? "# llama-server --version\nversion: 0.9.0-dev (build 12110, commit cd959bae9)\n",
            images ?? [VlmEvalRun.ParseImageJson(VlmEvalRunTests.ImageJson("a.png")), VlmEvalRun.ParseImageJson(VlmEvalRunTests.ImageJson("b.png", finish: "length"))]);

    private static CheckResult Find(IReadOnlyList<CheckResult> results, string namePart) => results.Single(r => r.Name.Contains(namePart, StringComparison.Ordinal));

    [Fact]
    public void A_matching_run_passes_every_check_and_the_model_check_is_marked_weak()
    {
        IReadOnlyList<CheckResult> results = VlmEvalChecks.Evaluate(Run(), Expect(), Manifest());
        Assert.Equal(7, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, r.Name + ": " + r.Detail));
        Assert.True(Find(results, "モデルのファイル").Weak);
        Assert.Single(results, r => r.Weak);
    }

    [Fact]
    public void A_prompt_a_schema_or_a_request_that_differs_fails_the_first_check()
    {
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(prompt: "別のプロンプト"), Expect(), Manifest()), "プロンプトとスキーマ").Passed);
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(schema: "{\"type\":\"array\"}"), Expect(), Manifest()), "プロンプトとスキーマ").Passed);
        // ファイルは同じでも、実際に送ったリクエストの文章が違う画像がある
        VlmEvalImage[] images = [VlmEvalRun.ParseImageJson(VlmEvalRunTests.ImageJson("a.png", prompt: "違う文章"))];
        CheckResult c = Find(VlmEvalChecks.Evaluate(Run(images: images), Expect(), Manifest()), "プロンプトとスキーマ");
        Assert.False(c.Passed);
        Assert.Contains("1 枚", c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_file_with_another_size_name_or_manifest_hash_fails()
    {
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(runInfo: VlmEvalRunTests.RunInfoJson(modelBytes: 999)), Expect(), Manifest()), "モデルのファイル").Passed);
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(runInfo: VlmEvalRunTests.RunInfoJson(model: "other.gguf")), Expect(), Manifest()), "モデルのファイル").Passed);
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(), Expect(), Manifest(ggufSha: new string('9', 64))), "モデルのファイル").Passed);   // MANIFEST の sha256 が製品の定数と違う
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(), Expect(), Manifest(match: "NG")), "モデルのファイル").Passed);                    // MANIFEST の検証が OK でない
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(), Expect(), []), "モデルのファイル").Passed);                                        // MANIFEST に行が無い
    }

    [Fact]
    public void Another_build_other_launch_flags_or_another_temperature_fail()
    {
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(version: "version: 0.9.0-dev (build 12111, commit abcdef012)"), Expect(), Manifest()), "ビルド").Passed);
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(runInfo: VlmEvalRunTests.RunInfoJson(args: "-ngl 99 -c 4096")), Expect(), Manifest()), "起動引数").Passed);
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(runInfo: VlmEvalRunTests.RunInfoJson(temperature: 0.7)), Expect(), Manifest()), "temperature").Passed);
        VlmEvalImage[] hot = [VlmEvalRun.ParseImageJson(VlmEvalRunTests.ImageJson("a.png", temperature: 0.5))];
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(images: hot), Expect(), Manifest()), "temperature").Passed);
    }

    [Fact]
    public void A_failure_that_is_not_length_or_a_success_too_long_for_the_product_max_tokens_fails()
    {
        VlmEvalImage[] http = [VlmEvalRun.ParseImageJson(VlmEvalRunTests.ImageJson("a.png", http: 500, finish: "stop"))];
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(images: http), Expect(), Manifest()), "失敗の種類").Passed);
        VlmEvalImage[] stopReason = [VlmEvalRun.ParseImageJson(VlmEvalRunTests.ImageJson("a.png", finish: "content_filter", parsedOk: false))];
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(images: stopReason), Expect(), Manifest()), "失敗の種類").Passed);
        VlmEvalImage[] longOk = [VlmEvalRun.ParseImageJson(VlmEvalRunTests.ImageJson("a.png", completionTokens: 2048))];     // 成功したが、製品の max_tokens 2048 では打ち切られる長さ
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(images: longOk), Expect(), Manifest()), "失敗の種類").Passed);
        VlmEvalImage[] justUnder = [VlmEvalRun.ParseImageJson(VlmEvalRunTests.ImageJson("a.png", completionTokens: 2047))];
        Assert.True(Find(VlmEvalChecks.Evaluate(Run(images: justUnder), Expect(), Manifest()), "失敗の種類").Passed);
    }

    [Fact]
    public void An_image_token_cap_below_the_largest_prompt_fails_because_it_might_have_been_active()
    {
        VlmEvalImage[] big = [VlmEvalRun.ParseImageJson(VlmEvalRunTests.ImageJson("a.png", promptTokens: 3256))];
        Assert.True(Find(VlmEvalChecks.Evaluate(Run(images: big), Expect(3584), Manifest()), "画像トークン").Passed);
        Assert.True(Find(VlmEvalChecks.Evaluate(Run(images: big), Expect(3256), Manifest()), "画像トークン").Passed);       // ちょうど(prompt_n は文章を含むので、画像トークンはこれより小さい)
        Assert.False(Find(VlmEvalChecks.Evaluate(Run(images: big), Expect(3000), Manifest()), "画像トークン").Passed);
    }

    [Fact]
    public void The_unverified_points_always_mention_the_weak_model_check_and_the_image_formats()
    {
        string all = string.Join("\n", VlmEvalChecks.UnverifiedPoints(allPng: true));
        Assert.Contains("sha256", all, StringComparison.Ordinal);
        Assert.Contains("JPEG・WebP", all, StringComparison.Ordinal);
        Assert.Contains("--no-webui", all, StringComparison.Ordinal);
        Assert.Contains("再試行", all, StringComparison.Ordinal);
        Assert.Contains("PNG 以外", string.Join("\n", VlmEvalChecks.UnverifiedPoints(allPng: false)), StringComparison.Ordinal);
    }
}
