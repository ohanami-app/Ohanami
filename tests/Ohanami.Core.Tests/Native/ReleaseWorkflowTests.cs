using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 配布の zip を作る GitHub Actions のワークフロー(.github/workflows/)の決まりを、YAML を文字列で読んで見張る(YAML のパーサは使わない)。
/// ワークフローそのものは、ランナーで回すまで動きを確かめられない。ここで見張るのは、書き方の決まりだけ:
/// きっかけ・使う部品・部品の固定・ランナーの画像・キャッシュ・権限・秘密・版と文の下書き・SDK の固定・行末の固定。決定は docs/adr/0048-release-built-by-actions.md。
/// </summary>
public sealed class ReleaseWorkflowTests
{
    private static readonly string[] WorkflowNames = ["release.yml", "release-publish.yml"];

    private static string Repo(params string[] parts) => Path.Combine([RepoPaths.Root, .. parts]);

    private static string WorkflowPath(string name) => Repo(".github", "workflows", name);

    /// <summary>コメントだけの行と空行を除いた行。行の中の末尾のコメントは残す(uses の横の版のコメントを読むため)。</summary>
    private static List<string> CodeLines(string name) =>
        File.ReadAllLines(WorkflowPath(name))
            .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
            .ToList();

    private static readonly Regex UsesLine = new(@"^\s*(?:-\s+)?uses:\s*(?<ref>\S+)(?<rest>.*)$");

    [Fact]
    public void Both_workflows_exist_and_the_directory_has_no_others()
    {
        Assert.True(Directory.Exists(Repo(".github", "workflows")));
        string[] files = Directory.GetFiles(Repo(".github", "workflows")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(WorkflowNames.Order(StringComparer.Ordinal), files);
    }

    [Theory]
    [InlineData("release.yml")]
    [InlineData("release-publish.yml")]
    public void The_only_trigger_is_workflow_dispatch(string name)
    {
        List<string> lines = CodeLines(name);
        int on = lines.FindIndex(l => l == "on:");
        Assert.True(on >= 0, "トップレベルの on: が無い");
        var triggers = new List<string>();
        for (int i = on + 1; i < lines.Count && lines[i].StartsWith(' '); i++)
        {
            Match m = Regex.Match(lines[i], @"^  (?<key>[A-Za-z_]+):");
            if (m.Success) triggers.Add(m.Groups["key"].Value);
        }
        Assert.Equal(["workflow_dispatch"], triggers);
    }

    [Theory]
    [InlineData("release.yml")]
    [InlineData("release-publish.yml")]
    public void Every_action_is_pinned_to_a_40_digit_commit_sha_with_a_version_comment(string name)
    {
        List<string> uses = CodeLines(name).Where(l => UsesLine.IsMatch(l)).ToList();
        Assert.NotEmpty(uses);
        foreach (string line in uses)
        {
            Match m = UsesLine.Match(line);
            Assert.Matches(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[0-9a-f]{40}$", m.Groups["ref"].Value);
            Assert.Matches(@"^\s+#\s*v\d+\.\d+\.\d+\s*$", m.Groups["rest"].Value);   // 横に、タグの版をコメントで書く
        }
    }

    [Theory]
    [InlineData("release.yml")]
    [InlineData("release-publish.yml")]
    public void The_only_actions_are_checkout_and_setup_dotnet(string name)
    {
        string[] allowed = ["actions/checkout", "actions/setup-dotnet"];
        foreach (string line in CodeLines(name).Where(l => UsesLine.IsMatch(l)))
        {
            string action = UsesLine.Match(line).Groups["ref"].Value.Split('@')[0];
            Assert.Contains(action, allowed);
        }
        // 2 つとも使っている(使わない部品を残さない)
        string[] used = CodeLines(name).Where(l => UsesLine.IsMatch(l)).Select(l => UsesLine.Match(l).Groups["ref"].Value.Split('@')[0]).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(allowed.Order(StringComparer.Ordinal), used);
    }

    [Theory]
    [InlineData("release.yml")]
    [InlineData("release-publish.yml")]
    public void Runs_only_on_the_windows_2025_image(string name)
    {
        string[] runsOn = CodeLines(name)
            .Select(l => Regex.Match(l, @"^\s*runs-on:\s*(?<v>\S+)\s*$"))
            .Where(m => m.Success).Select(m => m.Groups["v"].Value).ToArray();
        Assert.NotEmpty(runsOn);
        Assert.All(runsOn, v => Assert.Equal("windows-2025", v));
    }

    [Theory]
    [InlineData("release.yml")]
    [InlineData("release-publish.yml")]
    public void Nothing_is_cached(string name)
    {
        foreach (string line in CodeLines(name))
        {
            Assert.DoesNotContain("actions/cache", line, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"^\s*(?:cache|cache-dependency-path|cache-hit)\s*:", line);   // setup-dotnet の cache の入力も使わない
        }
    }

    [Theory]
    [InlineData("release.yml")]
    [InlineData("release-publish.yml")]
    public void The_only_secret_is_github_token(string name)
    {
        string text = string.Join('\n', CodeLines(name));
        List<string> secrets = Regex.Matches(text, @"secrets\.([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(["GITHUB_TOKEN"], secrets);
        Assert.DoesNotContain("secrets[", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets:", text, StringComparison.Ordinal);   // 再利用ワークフローへ秘密を渡さない
    }

    [Theory]
    [InlineData("release.yml")]
    [InlineData("release-publish.yml")]
    public void Runs_only_from_main_and_does_not_leave_credentials_in_the_checkout(string name)
    {
        string text = string.Join('\n', CodeLines(name));
        Assert.Contains("refs/heads/main", text, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_workflow_reads_by_default_and_publish_workflow_only_flips_the_draft()
    {
        string release = string.Join('\n', CodeLines("release.yml"));
        string publish = string.Join('\n', CodeLines("release-publish.yml"));

        // release.yml: トップレベルは contents: read。write は、ジョブの中にだけある
        Match top = Regex.Match(release, @"^permissions:\n  contents: (?<v>\w+)\n", RegexOptions.Multiline);
        Assert.True(top.Success && top.Groups["v"].Value == "read", "release.yml のトップレベルの permissions が contents: read でない");
        Assert.Contains("    permissions:\n      contents: write", release, StringComparison.Ordinal);
        Assert.DoesNotContain("--generate-notes", release, StringComparison.Ordinal);
        Assert.Contains("--draft", release, StringComparison.Ordinal);
        Assert.Contains("--target", release, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch", release, StringComparison.Ordinal);

        // release-publish.yml: permissions は contents: write だけ。やるのは下書きの公開だけ
        Match pTop = Regex.Match(publish, @"^permissions:\n  contents: (?<v>\w+)\n(?!  )", RegexOptions.Multiline);
        Assert.True(pTop.Success && pTop.Groups["v"].Value == "write", "release-publish.yml の permissions が contents: write だけでない");
        Assert.Contains("gh release edit", publish, StringComparison.Ordinal);
        Assert.Contains("--draft=false", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release create", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("Publish-Release.ps1", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_version_has_a_release_notes_draft_without_mentions()
    {
        XElement props = XDocument.Load(Repo("src", "Directory.Build.props")).Root!;
        string version = props.Descendants("Version").Single().Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);   // ワークフローの「版を読む」が通す形
        string notes = Repo("tools", "release", "notes", version + ".md");
        Assert.True(File.Exists(notes), $"版 {version} の Release の文の下書きが無い: {notes}");
        string text = File.ReadAllText(notes);
        Assert.NotEmpty(text.Trim());
        Assert.False(text.Contains('@'), "Release の文に @ がある(誰かに通知が行かないように、@ は書かない)");
    }

    [Fact]
    public void The_sdk_is_pinned_to_one_version_with_roll_forward_disabled()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Repo("global.json")));
        JsonElement sdk = doc.RootElement.GetProperty("sdk");
        Assert.Matches(@"^10\.0\.\d+$", sdk.GetProperty("version").GetString()!);
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.Equal("Microsoft.Testing.Platform", doc.RootElement.GetProperty("test").GetProperty("runner").GetString());
    }

    [Fact]
    public void Line_endings_are_fixed_to_lf_for_every_checkout()
    {
        string[] lines = File.ReadAllLines(Repo(".gitattributes")).Select(l => l.Trim()).ToArray();
        Assert.Contains("* text=auto eol=lf", lines);
        Assert.Contains("tools/convert-siglip2/*.py text eol=lf", lines);
        Assert.Contains("tools/convert-siglip2/requirements.txt text eol=lf", lines);
    }
}
