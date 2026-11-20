using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ohanami.Core.Models;
using Ohanami.Core.Native;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 配布物の置き方(build/*.targets が exe の隣にコピーするもの)と、配布物の許可の表(tools/release/allowed-files.tsv)と、
/// 実行時の検査(NativeSelfCheck.RequiredFiles・VlmCatalog)が、同じ集合を指していること。ファイルの中身(DLL)は読まない(publish を回さない)。
/// publish の出力そのものとの突き合わせは、tools/release/Publish-Release.ps1 が行う。
/// </summary>
public sealed class DistributionLayoutTests
{
    private sealed record Row(string Path, string Sha256, string Source);

    private static string Repo(params string[] parts) => Path.Combine([RepoPaths.Root, .. parts]);

    private static List<Row> ReadTable()
    {
        string[] lines = File.ReadAllLines(Repo("tools", "release", "allowed-files.tsv"));
        Assert.Equal("path\tsha256\tsource", lines[0]);
        var rows = new List<Row>();
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            string[] c = line.Split('\t');
            Assert.True(c.Length == 3, $"列数が 3 でない: {line}");
            rows.Add(new Row(c[0], c[1], c[2]));
        }
        return rows;
    }

    /// <summary>targets の指定の項目(例: OhanamiCudaFile)の Include から、ファイル名だけを取り出す(「$(変数)名前」の形)。</summary>
    private static List<string> TargetsFileNames(string targetsFile, string itemName)
    {
        XElement project = XDocument.Load(Repo("build", targetsFile)).Root!;
        return project.Descendants(itemName)
            .Select(e => (string?)e.Attribute("Include") ?? throw new InvalidDataException($"{itemName} に Include が無い"))
            .Select(include => Regex.Match(include, @"^\$\([A-Za-z]+\)(?<name>[^\\/$]+)$") is { Success: true } m
                ? m.Groups["name"].Value
                : throw new InvalidDataException($"想定外の形の Include: {include}"))
            .ToList();
    }

    [Fact]
    public void The_allowed_table_is_well_formed_and_lists_only_the_files_a_release_may_contain()
    {
        List<Row> rows = ReadTable();
        Assert.NotEmpty(rows);
        Assert.Equal(rows.Count, rows.Select(r => r.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(rows.Select(r => r.Path).Order(StringComparer.Ordinal), rows.Select(r => r.Path));   // 並びは決まっている(差分が読める)

        foreach (Row r in rows)
        {
            Assert.DoesNotContain('\\', r.Path);
            Assert.False(r.Path.StartsWith('/'), r.Path);
            Assert.DoesNotMatch(@"\.(pdb|lib)$", r.Path);                                        // 開発機の絶対パスが入る・リンク用
            Assert.DoesNotContain("tensorrt", r.Path, StringComparison.OrdinalIgnoreCase);       // TensorRT は使わない
            if (r.Source is "ohanami" or "repository") Assert.Equal("", r.Sha256);                // ビルドのたびに変わる物・リポジトリの文書(同じパスのファイルと突き合わせる)には、SHA-256 を書かない
            else Assert.Matches("^[0-9a-f]{64}$", r.Sha256);
        }
        Assert.Equal(5, rows.Count(r => r.Source == "ohanami"));
    }

    [Fact]
    public void The_repository_rows_in_the_table_are_exactly_the_notice_documents_and_the_remove_data_scripts_and_the_publish_targets_copy_them()
    {
        // 表の repository の行 = リポジトリの LICENSE・THIRD-PARTY-NOTICES.md・licenses/*.txt と、配布物の直下に置くデータ削除の道具の 2 つ(増減したら、表も直す)
        string[] removeDataScripts = ["Ohanami-RemoveData.cmd", "Ohanami-RemoveData.ps1"];
        List<string> expected =
        [
            "LICENSE",
            "THIRD-PARTY-NOTICES.md",
            .. Directory.GetFiles(Repo("licenses"), "*.txt").Select(f => "licenses/" + System.IO.Path.GetFileName(f)),
            .. removeDataScripts,
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), ReadTable().Where(r => r.Source == "repository").Select(r => r.Path).Order(StringComparer.Ordinal));
        Assert.All(Directory.GetFiles(Repo("licenses")), f => Assert.EndsWith(".txt", f, StringComparison.Ordinal));   // txt 以外は、publish が運ばない
        Assert.All(removeDataScripts, s => Assert.True(File.Exists(Repo("src", "Ohanami.RemoveData", s)), s));          // 配布物の直下の道具は、src/Ohanami.RemoveData/ の同名のファイル

        // publish の出力に入れる指定(LICENSE・THIRD-PARTY-NOTICES.md・licenses/*.txt・データ削除の道具。削除の道具は、リンクで配布物の直下に置く)
        XElement project = XDocument.Load(Repo("build", "OhanamiPublish.targets")).Root!;
        List<string> includes = [.. project.Descendants("None").Select(e => (string?)e.Attribute("Include") ?? "")];
        Assert.Contains(includes, i => i.EndsWith(@"..\LICENSE", StringComparison.Ordinal));
        Assert.Contains(includes, i => i.EndsWith(@"..\THIRD-PARTY-NOTICES.md", StringComparison.Ordinal));
        Assert.Contains(includes, i => i.EndsWith(@"..\licenses\*.txt", StringComparison.Ordinal));
        foreach (string s in removeDataScripts)
        {
            XElement item = Assert.Single(project.Descendants("None"), e => ((string?)e.Attribute("Include") ?? "").EndsWith(@"..\src\Ohanami.RemoveData\" + s, StringComparison.Ordinal));
            Assert.Equal(s, (string?)item.Attribute("Link"));
        }
        Assert.All(project.Descendants("None"), e => Assert.Equal("PreserveNewest", (string?)e.Attribute("CopyToPublishDirectory")));
    }

    [Fact]
    public void The_notices_name_every_license_file_and_leave_no_open_decision_behind()
    {
        string notices = File.ReadAllText(Repo("THIRD-PARTY-NOTICES.md"));
        string[] files = [.. Directory.GetFiles(Repo("licenses")).Select(f => System.IO.Path.GetFileName(f))];
        foreach (string f in files) Assert.Contains($"(licenses/{f})", notices);                 // 全文のファイルは、すべて一覧からリンクされている
        foreach (Match m in Regex.Matches(notices, @"\(licenses/([^)]+)\)"))                     // リンクの先は、すべてある
            Assert.Contains(m.Groups[1].Value, files);

        Assert.DoesNotContain("【", notices);                                                    // 未決の印を残さない
        string license = File.ReadAllText(Repo("LICENSE"));
        Assert.StartsWith("MIT License", license, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2026 Ohanami contributors", license, StringComparison.Ordinal);

        // 第三者から写したコードのソースの冒頭に、元の表示とライセンス全文の場所がある
        string embedding = Repo("src", "Ohanami.Core", "Embedding");
        Assert.Contains("licenses/Pillow-MIT-CMU.txt", File.ReadAllText(System.IO.Path.Combine(embedding, "ImagePreprocessing.cs")), StringComparison.Ordinal);
        Assert.Contains("licenses/Apache-2.0.txt", File.ReadAllText(System.IO.Path.Combine(embedding, "Siglip2Tokenizer.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_publish_trim_list_names_nothing_the_table_allows()
    {
        XElement project = XDocument.Load(Repo("build", "OhanamiPublish.targets")).Root!;
        string list = project.Descendants("OhanamiNotPublished").Single().Value;
        string[] names = list.Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(names);

        HashSet<string> allowed = ReadTable().Select(r => System.IO.Path.GetFileName(r.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names) Assert.DoesNotContain(name, allowed);
    }

    [Fact]
    public void Every_file_the_build_copies_next_to_the_exe_is_in_the_allowed_table_and_the_sources_agree()
    {
        List<Row> rows = ReadTable();
        HashSet<string> cuda = TargetsFileNames("OhanamiCudaRuntime.targets", "OhanamiCudaFile").ToHashSet(StringComparer.Ordinal);
        HashSet<string> llama = TargetsFileNames("OhanamiLlamaServer.targets", "OhanamiLlamaFile").ToHashSet(StringComparer.Ordinal);
        HashSet<string> vc = TargetsFileNames("OhanamiVcRuntime.targets", "OhanamiVcFile").ToHashSet(StringComparer.Ordinal);

        Assert.Empty(cuda.Intersect(llama));                                                     // cuBLAS・cudart は ORT と llama-server で 1 組(2 回数えない)
        Assert.Empty(vc.Intersect(cuda));
        Assert.Empty(vc.Intersect(llama));
        HashSet<string> copied = [.. cuda, .. llama, .. vc];
        HashSet<string> topLevel = rows.Where(r => !r.Path.Contains('/')).Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(copied.Except(topLevel));                                                   // 表に無いものは、配布物に入れられない

        // 表の側の同梱物(第三者のバイナリのうち、runtime/ から来るもの)は、ビルドが名前でコピーするものと同じ集合
        Assert.Equal(cuda.Where(n => n.StartsWith("cudnn", StringComparison.Ordinal)).Order(StringComparer.Ordinal),
                     rows.Where(r => r.Source == "cudnn").Select(r => r.Path).Order(StringComparer.Ordinal));
        Assert.Equal(cuda.Where(n => !n.StartsWith("cudnn", StringComparison.Ordinal)).Order(StringComparer.Ordinal),
                     rows.Where(r => r.Source.StartsWith("cuda", StringComparison.Ordinal)).Select(r => r.Path).Order(StringComparer.Ordinal));
        Assert.Equal(llama.Order(StringComparer.Ordinal), rows.Where(r => r.Source == "llama.cpp").Select(r => r.Path).Order(StringComparer.Ordinal));
        Assert.Equal(vc.Order(StringComparer.Ordinal), rows.Where(r => r.Source.StartsWith("vc++", StringComparison.Ordinal)).Select(r => r.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_vc_runtime_set_is_the_five_dlls_that_onnx_runtime_and_llama_server_import()
    {
        // ORT(onnxruntime*.dll)と llama-server の一式の import を読んで決めた集合(docs/adr/0044-vc-runtime-bundled-app-local.md)。
        // UCRT(api-ms-win-crt-*)は Windows 10 以降の OS の部品なので入れない。デバッグ版(*d.dll)と onecore の変種は再配布できない。
        string[] expected = ["msvcp140.dll", "msvcp140_1.dll", "msvcp140_atomic_wait.dll", "vcruntime140.dll", "vcruntime140_1.dll"];
        List<string> vc = TargetsFileNames("OhanamiVcRuntime.targets", "OhanamiVcFile");
        Assert.Equal(expected.Order(StringComparer.Ordinal), vc.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(vc, n => Regex.IsMatch(n, @"d\.dll$|d_atomic_wait"));              // デバッグ版は配らない
    }

    [Fact]
    public void The_cudnn_files_are_the_seven_the_startup_check_requires()
    {
        HashSet<string> cuda = TargetsFileNames("OhanamiCudaRuntime.targets", "OhanamiCudaFile").ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] cudnnInBuild = [.. cuda.Where(n => n.StartsWith("cudnn", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];
        string[] cudnnRequired = [.. NativeSelfCheck.RequiredFiles.Where(n => n.StartsWith("cudnn", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];
        Assert.Equal(7, cudnnInBuild.Length);
        Assert.Equal(cudnnRequired, cudnnInBuild);

        // 起動の検査が要求する CUDA 系(cuBLAS・cuBLASLt)も、ビルドがコピーする
        foreach (string n in NativeSelfCheck.RequiredFiles.Where(n => n.StartsWith("cublas", StringComparison.OrdinalIgnoreCase)))
            Assert.Contains(n, cuda);
    }

    [Fact]
    public void The_llama_server_set_has_the_server_and_all_fourteen_cpu_backends_and_its_exe_hash_is_the_one_checked_at_runtime()
    {
        List<string> llama = TargetsFileNames("OhanamiLlamaServer.targets", "OhanamiLlamaFile");
        Assert.Contains("llama-server.exe", llama);
        Assert.Equal(14, llama.Count(n => Regex.IsMatch(n, @"^ggml-cpu-[a-z0-9]+\.dll$")));       // どれが読まれるかは利用者の CPU で決まるので、全部

        Row exe = ReadTable().Single(r => r.Path == "llama-server.exe");
        Assert.Equal(VlmCatalog.LlamaServerExeSha256, exe.Sha256);                                // 表と、実行時に確かめる定数は、同じ exe を指す
    }
}
