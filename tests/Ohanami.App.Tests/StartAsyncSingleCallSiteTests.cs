using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 「App が <c>Indexer.StartAsync</c> を呼ぶのは 1 か所だけで、指定はいつも <c>AllJobs</c>(= [埋め込み, VLM])」の見張り(ADR 0039)。
/// 呼ぶ場所が複数あって決め方が違うと、待機中に覚えている指定が、画面から見えない形で狭まる(ADR 0039 の罠)。指定が 1 通りなら見えない状態は無い。新しい呼び出しを足すと、この見張りが落ちる(足すなら、1 か所に寄せる)。
/// ソースを読んで確かめる(<see cref="ShellBoundaryGuardTests"/> と同じ流儀。コメントは除く)。
/// </summary>
public sealed class StartAsyncSingleCallSiteTests
{
    private static readonly Regex Call = new(@"\.StartAsync\(\s*([^()]*)\)", RegexOptions.CultureInvariant);

    /// <summary>ソース(コメントを除く)の中の <c>.StartAsync(...)</c> の呼び出し(引数の文字列)。</summary>
    internal static IEnumerable<string> StartCalls(string source) =>
        Call.Matches(ShellBoundaryGuardTests.StripComments(source)).Select(m => m.Groups[1].Value.Trim());

    [Fact]
    public void The_app_calls_StartAsync_in_exactly_one_place_and_always_with_the_one_specification()
    {
        string src = Path.Combine(ShellBoundaryGuardTests.RepoRoot(), "src", "Ohanami.App");
        var sites = new List<(string File, string Argument)>();
        int scanned = 0;
        foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !ShellBoundaryGuardTests.IsGenerated(f)))
        {
            scanned++;
            foreach (string argument in StartCalls(File.ReadAllText(file))) sites.Add((Path.GetFileName(file), argument));
        }

        Assert.True(scanned >= 20, $"ソースを読めていない(読んだのは {scanned} 個)");
        (string File, string Argument) site = Assert.Single(sites);
        Assert.Equal("IndexPanel.cs", site.File);
        Assert.Equal("AllJobs", site.Argument);                 // 指定はいつも、IndexPanel.AllJobs(= [埋め込み, VLM])
    }

    [Fact]
    public void The_old_ways_of_choosing_the_jobs_are_gone()
    {
        string src = Path.Combine(ShellBoundaryGuardTests.RepoRoot(), "src", "Ohanami.App");
        var found = new List<string>();
        foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !ShellBoundaryGuardTests.IsGenerated(f)))
        {
            string code = ShellBoundaryGuardTests.StripComments(File.ReadAllText(file));
            foreach (Match m in Regex.Matches(code, @"\bStartJobsFor\b|\.StartJobs\b|\bStartJobs\s*=>"))
                found.Add($"{Path.GetFileName(file)}: {m.Value}");
        }

        Assert.True(found.Count == 0, "ジョブの指定を決める古い道が残っている:\n" + string.Join("\n", found));
    }

    [Fact]
    public void The_all_jobs_specification_is_embedding_then_vlm()
    {
        Assert.Equal([Ohanami.Core.JobKind.Embed, Ohanami.Core.JobKind.Vlm], IndexPanel.AllJobs);
    }

    // ---------------------------------------------------------------- 見張り自身の確かめ(何も検出しないまま通ってしまわないように)

    [Theory]
    [InlineData("await core.Indexer.StartAsync(AllJobs);", 1, "AllJobs")]
    [InlineData("core.Indexer.StartAsync([JobKind.Embed]);", 1, "[JobKind.Embed]")]
    [InlineData("x.StartAsync(plan.StartJobs); y.StartAsync(AllJobs);", 2, "plan.StartJobs")]
    [InlineData("// core.Indexer.StartAsync([JobKind.Embed]);", 0, "")]
    [InlineData("/* x.StartAsync(a) */ int z;", 0, "")]
    [InlineData("var t = Task.Run(() => RunLoopAsync());", 0, "")]
    public void The_call_detector_finds_what_it_claims(string source, int expectedCount, string expectedFirst)
    {
        string[] calls = [.. StartCalls(source)];

        Assert.Equal(expectedCount, calls.Length);
        if (expectedCount > 0) Assert.Equal(expectedFirst, calls[0]);
    }
}
