using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// tools/runtime/Get-Runtime.ps1(runtime/ と tokenizer.json を取って置く。動かさずに、書いてあることだけを読む)が、
/// ビルドの置き方(build/*.targets)・許可の表・models/MANIFEST.tsv と同じものを指していること、
/// 取る先が一次の配布元だけであること。ネットワークに出るのは、このスクリプトだけ(製品のコードの見張りは NetworkBoundaryGuardTests)。
/// </summary>
public sealed class GetRuntimeScriptTests
{
    private static string Repo(params string[] parts) => Path.Combine([RepoPaths.Root, .. parts]);

    private static string Script() => File.ReadAllText(Repo("tools", "runtime", "Get-Runtime.ps1"));

    private static string TargetsProperty(string targetsFile, string property) =>
        XDocument.Load(Repo("build", targetsFile)).Root!.Descendants(property).First().Value;

    [Fact]
    public void The_destination_folders_are_the_ones_the_build_copies_from()
    {
        string script = Script();
        string llama = Regex.Match(TargetsProperty("OhanamiCudaRuntime.targets", "OhanamiLlamaDir"), @"llama\.cpp\\[^\\]+").Value;   // llama.cpp\b12110
        string cudnn = "cudnn\\" + TargetsProperty("OhanamiCudaRuntime.targets", "OhanamiCudnnVersionDir");
        string vc = "vcruntime\\" + TargetsProperty("OhanamiVcRuntime.targets", "OhanamiVcVersionDir");

        Assert.Equal(2, Regex.Matches(script, Regex.Escape($"Dest = '{llama}'")).Count);          // llama.cpp の 2 つの zip は、同じ場所に置く
        Assert.Contains($"Dest = '{cudnn}'", script, StringComparison.Ordinal);
        Assert.Contains($"$vcDest = '{vc}'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_script_places_exactly_the_table_rows_of_its_sources()
    {
        string script = Script();
        List<string> labels = [.. File.ReadAllLines(Repo("tools", "release", "allowed-files.tsv")).Skip(1).Select(l => l.Split('\t')[2]).Distinct()];

        foreach (string label in new[] { "llama.cpp", "cuda (llama.cpp の同梱物)", "cudnn" })
        {
            Assert.Contains(label, labels);
            Assert.Contains($"Label = '{label}'", script, StringComparison.Ordinal);
        }
        Assert.Contains(labels, l => l.StartsWith("vc++", StringComparison.Ordinal));
        Assert.Contains("$vcLabelPrefix = 'vc++'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tokenizer_constant_is_the_one_in_the_manifest()
    {
        string script = Script();
        string sha = Regex.Match(script, @"\$tokenizer = @\{[^}]*Sha256 = '([0-9a-f]{64})'", RegexOptions.Singleline).Groups[1].Value;
        Assert.Equal(64, sha.Length);

        string[] row = File.ReadAllLines(Repo("models", "MANIFEST.tsv"))
            .Select(l => l.Split('\t')).Single(c => c.Length >= 6 && c[0].EndsWith("/ViT-SO400M-16-SigLIP2-384__ohanami", StringComparison.Ordinal) && c[2] == "textual/tokenizer.json");
        Assert.Equal(row[4], sha);
        Assert.Contains("ViT-SO400M-16-SigLIP2-384__ohanami\\textual\\tokenizer.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_url_in_the_script_points_at_a_primary_distribution_source_and_every_zip_has_a_sha256()
    {
        string script = Script();
        string[] allowedPrefixes =
        [
            "https://github.com/ggml-org/llama.cpp/releases/download/",
            "https://developer.download.nvidia.com/compute/cudnn/redist/cudnn/windows-x86_64/",
            "https://huggingface.co/timm/ViT-SO400M-16-SigLIP2-384/resolve/",
        ];
        List<string> urls = [.. Regex.Matches(script, @"https?://[^\s'""`)]+").Select(m => m.Value)];
        Assert.NotEmpty(urls);
        foreach (string url in urls)
        {
            Assert.StartsWith("https://", url, StringComparison.Ordinal);                          // TLS だけ
            Assert.Contains(allowedPrefixes, p => url.StartsWith(p, StringComparison.Ordinal));
        }

        // zip(3 つ)ごとに、SHA-256 の定数がある。tokenizer と合わせて 4 つ
        Assert.Equal(4, Regex.Matches(script, @"Sha256 = '[0-9a-f]{64}'").Count);
    }
}
