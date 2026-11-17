using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Ohanami.Core.Models;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// VlmCatalog の値が models/MANIFEST.tsv(コミット済み)の行と同じこと、送るプロンプトとスキーマの「作り方」が VLM の評価のコード(spikes/ の RunCommand)と同じこと
/// (評価のコミット済みのファイルと、評価の run があれば実際に送ったリクエストとの突き合わせ)。ファイルの中身(GGUF)は読まない。
/// </summary>
public sealed class VlmCatalogTests
{
    private sealed record ManifestRow(string Repo, string Revision, string FileName, long Size, string Sha256, string Match);

    private static List<ManifestRow> ReadManifest()
    {
        string path = Path.Combine(RepoPaths.Root, "models", "MANIFEST.tsv");
        Assert.True(File.Exists(path), $"MANIFEST.tsv が無い: {path}");
        var rows = new List<ManifestRow>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            if (line.Length == 0) continue;
            string[] c = line.Split('\t');
            Assert.True(c.Length == 6, $"MANIFEST.tsv の列数が 6 でない: {line}");
            rows.Add(new ManifestRow(c[0], c[1], c[2], long.Parse(c[3], System.Globalization.CultureInfo.InvariantCulture), c[4], c[5]));
        }
        return rows;
    }

    private static string SpikeFilePath(string file) => Path.Combine(RepoPaths.Root, "spikes", "S1-vlm", file);

    // ---------------------------------------------------------------- MANIFEST との突き合わせ

    [Fact]
    public void Catalog_files_and_the_hugging_face_pointer_match_the_manifest_rows()
    {
        List<ManifestRow> rows = ReadManifest().Where(r => r.Repo == VlmCatalog.HfRepository).ToList();
        Assert.NotEmpty(rows);
        VlmSpec spec = VlmCatalog.Current;
        foreach ((string label, VlmFile file) in new[] { ("gguf", spec.Gguf), ("mmproj", spec.Mmproj) })
        {
            ManifestRow row = Assert.Single(rows, r => r.FileName == file.RelativePath);
            Assert.Equal("OK", row.Match);
            Assert.Equal(row.Size, file.Size);
            Assert.Equal(row.Sha256, file.Sha256);
            Assert.Equal(VlmCatalog.HfRevision, row.Revision);
            Assert.True(label.Length > 0);
        }
        Assert.Equal(VlmCatalog.HfRepository, spec.HfRepository);
        Assert.Equal(VlmCatalog.HfRevision, spec.HfRevision);
    }

    [Fact]
    public void Catalog_names_and_the_stage_one_final_configuration_are_stable()
    {
        Assert.Equal("Qwen3-VL-8B-Instruct-GGUF", VlmCatalog.DirectoryName);
        Assert.Equal("Qwen3VL-8B-Instruct-Q4_K_M.gguf", VlmCatalog.Current.Gguf.RelativePath);
        Assert.Equal("mmproj-Qwen3VL-8B-Instruct-F16.gguf", VlmCatalog.Current.Mmproj.RelativePath);
        Assert.StartsWith(VlmCatalog.Family + ":", VlmCatalog.Current.ModelId, StringComparison.Ordinal);
        Assert.Equal("0", VlmCatalog.Current.Temperature);         // 評価で決めた最終構成: temperature 0。ADR 0018
        Assert.Equal("", VlmCatalog.Current.ChatTemplateKwargs);   // thinking の指定は使わない(Qwen3-VL Instruct)
        Assert.Null(VlmCatalog.Current.ImageMinTokens);            // --image-min-tokens は既定
        Assert.Equal(12, VlmCatalog.Current.ModelId.Split(':')[1].Length);
    }

    [Fact]
    public void The_image_max_tokens_constant_is_the_decided_value()
    {
        // 評価で決めた値(ADR 0019)。変えるときは、この値と ADR を同時に更新する
        Assert.Equal(3584, VlmCatalog.ImageMaxTokens);
        Assert.Equal(VlmCatalog.ImageMaxTokens, VlmCatalog.Current.ImageMaxTokens);
        // 文章 約 500(推定)+ 画像 + 出力 max_tokens 2048 が、-c 8192 に収まる
        Assert.True(500 + VlmCatalog.ImageMaxTokens + 2048 <= 8192);
    }

    [Fact]
    public void The_llama_server_constants_are_well_formed_and_outside_the_identity()
    {
        Assert.Equal(12110, VlmCatalog.LlamaServerBuild);
        Assert.Equal("cd959bae9", VlmCatalog.LlamaServerCommit);
        Assert.Equal(64, VlmCatalog.LlamaServerExeSha256.Length);
        Assert.True(VlmCatalog.LlamaServerExeSha256.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
        string spec = VlmCatalog.Current.CanonicalString() + VlmCatalog.Current.ToJson();
        Assert.DoesNotContain(VlmCatalog.LlamaServerExeSha256, spec, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 「送る文字列」の作り方(評価のコードの RunCommand と同じ)

    /// <summary>評価のコードの RunCommand: <c>File.ReadAllText(path, UTF-8 BOM なし).TrimEnd('\r', '\n')</c>。</summary>
    private static string ReadSentPrompt(string path) => File.ReadAllText(path, new UTF8Encoding(false)).TrimEnd('\r', '\n');

    [Fact]
    public void The_prompt_constant_equals_what_the_spike_runner_reads_from_the_committed_prompt_file()
    {
        string path = SpikeFilePath("prompt-tags.txt");
        Assert.True(File.Exists(path), $"評価のプロンプトのファイルが無い: {path}");
        string sent = ReadSentPrompt(path);
        Assert.Equal(VlmPrompt.Text, sent);
        Assert.False(VlmPrompt.Text.EndsWith('\n') || VlmPrompt.Text.EndsWith('\r'));
        Assert.DoesNotContain('\r', VlmPrompt.Text);
        Assert.DoesNotContain('\n', VlmPrompt.Text);   // 評価のプロンプトは 1 行
        Assert.Equal(VlmSpec.PromptSha256Of(sent), VlmCatalog.Current.PromptSha256);
    }

    [Fact]
    public void The_schema_constant_equals_the_compact_json_the_spike_runner_puts_in_the_request()
    {
        string path = SpikeFilePath("schema-tags.json");
        Assert.True(File.Exists(path), $"評価のスキーマのファイルが無い: {path}");
        // 評価のコードは JsonNode.Parse(ファイルの内容) をそのままリクエストに入れて ToJsonString() した(整形つきのファイルとは文字列が違う)
        string sent = VlmSpec.CanonicalizeSchema(File.ReadAllText(path, new UTF8Encoding(false)));
        Assert.Equal(VlmSchema.Json, sent);
        Assert.DoesNotContain(' ', VlmSchema.Json);
        Assert.NotEqual(VlmSchema.Json, File.ReadAllText(path, new UTF8Encoding(false)).Trim()); // ファイルそのものではない
        Assert.Equal(VlmSpec.SchemaSha256Of(sent), VlmCatalog.Current.SchemaSha256);
        // フィールドの順は ocr → description → tags(書き起こしてから要約させる)
        JsonObject props = JsonNode.Parse(VlmSchema.Json)!["properties"]!.AsObject();
        Assert.Equal(["ocr", "description", "tags"], props.Select(p => p.Key).ToArray());
        Assert.Equal(["ocr", "description", "tags"], JsonNode.Parse(VlmSchema.Json)!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.False(JsonNode.Parse(VlmSchema.Json)!["additionalProperties"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------- 私物の評価の run があるときだけ(実際に送ったリクエストとの一致)

    [Fact]
    public void The_constants_equal_the_request_the_spike_actually_sent_when_the_evaluation_run_is_available()
    {
        string? run = Environment.GetEnvironmentVariable("OHANAMI_S1_RUN");
        Assert.SkipUnless(!string.IsNullOrEmpty(run),
            "環境変数 OHANAMI_S1_RUN(VLM の評価の run のフォルダ。spikes/ の下にある)が無い(手元の run との突き合わせ。通常は skip)");
        Assert.True(Directory.Exists(run), $"OHANAMI_S1_RUN のフォルダが無い: {run}");

        // run にコピーされたファイルを、評価のコードと同じ手順で読んだもの
        Assert.Equal(VlmPrompt.Text, ReadSentPrompt(Path.Combine(run!, "prompt.txt")));
        Assert.Equal(VlmSchema.Json, VlmSpec.CanonicalizeSchema(File.ReadAllText(Path.Combine(run!, "schema.json"), new UTF8Encoding(false))));

        // 画像ごとの JSON の request.body_without_base64 は、実際にサーバへ送った本文(画像のバイト列だけ省略)
        string[] files = Directory.GetFiles(run!, "*.json").Where(f => Path.GetFileName(f) is not ("run-info.json" or "schema.json")).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        foreach (string file in files) // 画像ごとの JSON をすべて(1030 枚の run なら 1030 件が、同じ本文の形)
        {
            JsonNode body = JsonNode.Parse(File.ReadAllText(file, new UTF8Encoding(false)))!["request"]!["body_without_base64"]!;
            JsonNode content = body["messages"]![0]!["content"]!;
            Assert.Equal("image_url", content[0]!["type"]!.GetValue<string>());
            Assert.Equal("text", content[1]!["type"]!.GetValue<string>());
            Assert.Equal(VlmPrompt.Text, content[1]!["text"]!.GetValue<string>());                                  // 送った文章 = 定数(ハッシュも同じ)
            Assert.Equal(VlmSchema.Json, body["response_format"]!["json_schema"]!["schema"]!.ToJsonString());     // 送ったスキーマ = 定数
            Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
            Assert.Equal(0, body["temperature"]!.GetValue<double>());                                               // temperature 0
            Assert.Equal("0", VlmCatalog.Current.Temperature);
        }
    }
}
