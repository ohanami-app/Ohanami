using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 「App のソースに、モデルの名前の書き写しが無い」ことの見張り(ADR 0038)。画面に出すモデルの名前は、Core が公開している表示名
/// (<c>OhanamiCore.ModelLabels</c>。定義のそばの定数)を差し込むだけにする。書き写すと、モデルを替えたとき画面の名前が嘘になる。
/// ソースを読んで確かめる(コメントは除く。<see cref="ShellBoundaryGuardTests"/> と同じ流儀)。
/// </summary>
public sealed class ModelNamesGuardTests
{
    /// <summary>モデルの名前(ファミリー名)。大文字・小文字を区別しない。</summary>
    private static readonly Regex Names = new(@"SigLIP|Qwen", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IEnumerable<string> Copies(string source) =>
        Names.Matches(ShellBoundaryGuardTests.StripComments(source)).Select(m => m.Value);

    [Fact]
    public void The_app_source_has_no_copied_model_names()
    {
        string src = Path.Combine(ShellBoundaryGuardTests.RepoRoot(), "src", "Ohanami.App");
        var found = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !ShellBoundaryGuardTests.IsGenerated(f)))
        {
            scanned++;
            foreach (string name in Copies(File.ReadAllText(file))) found.Add($"{Path.GetFileName(file)}: {name}");
        }

        Assert.True(scanned >= 20, $"ソースを読めていない(読んだのは {scanned} 個)");
        Assert.True(found.Count == 0, "モデルの名前が App のソースに書き写されている(OhanamiCore.ModelLabels を使う):\n" + string.Join("\n", found));
    }

    [Theory]
    [InlineData("var s = \"SigLIP2\";", 1)]
    [InlineData("var s = $\"モデル: Qwen3-VL-8B-Instruct\";", 1)]
    [InlineData("var s = \"siglip\" + \"qwen\";", 2)]            // 大文字・小文字を区別しない
    [InlineData("// SigLIP2 でベクトルにする(コメントは除く)", 0)]
    [InlineData("/* Qwen3 */ int a;", 0)]
    [InlineData("var s = OhanamiCore.ModelLabels.FeatureVector;", 0)]
    public void The_detector_finds_what_it_claims(string source, int expected)
    {
        Assert.Equal(expected, Copies(source).Count());
    }
}
