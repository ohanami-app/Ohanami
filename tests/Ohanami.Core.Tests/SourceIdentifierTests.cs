using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 識別子の検査: src/ 配下のソースの識別子と文字列リテラルに、特定の配布元の名前を入れない。
/// コメントは検査しない(出自をコメントで説明するのは禁じない)。
/// 製品の系譜に、比較に使っただけの配布元を入れないため(ADR 0012)。
/// </summary>
public sealed class SourceIdentifierTests
{
    // 禁じる名前そのものをソースに書くと、検査の仕組みが自分自身に引っかかりうる。つなげて組み立てる。
    private static readonly string Forbidden = "imm" + "ich";

    [Fact]
    public void Source_identifiers_and_string_literals_do_not_contain_the_forbidden_name()
    {
        string src = Path.Combine(RepoPaths.Root, "src");
        var hits = new List<string>();
        int scanned = 0;
        foreach (string file in EnumerateSourceFiles(src))
        {
            scanned++;
            string text = File.ReadAllText(file);
            string stripped = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? CommentStripper.StripCSharp(text) : CommentStripper.StripXml(text);
            if (stripped.Contains(Forbidden, StringComparison.OrdinalIgnoreCase)) hits.Add(Path.GetRelativePath(RepoPaths.Root, file));
            // ファイル名・ディレクトリ名も識別子の一部
            if (Path.GetRelativePath(src, file).Contains(Forbidden, StringComparison.OrdinalIgnoreCase)) hits.Add(Path.GetRelativePath(RepoPaths.Root, file) + "(パス)");
        }
        Assert.True(scanned > 0, "検査の対象のファイルが 1 つも見つからない(src/ の場所が違う?)");
        Assert.True(hits.Count == 0, "配布元の名前が識別子か文字列リテラルに入っている: " + string.Join(", ", hits));
    }

    [Fact]
    public void Check_detects_the_name_in_identifiers_and_string_literals_but_not_in_comments()
    {
        Assert.Contains(Forbidden, CommentStripper.StripCSharp("var " + Forbidden + "Model = 1;"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Forbidden, CommentStripper.StripCSharp("var s = \"x-" + Forbidden + "\";"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Forbidden, CommentStripper.StripCSharp("// from " + Forbidden + "\nint a;"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Forbidden, CommentStripper.StripXml("<A><!-- " + Forbidden + " --></A>"), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        string[] patterns = ["*.cs", "*.csproj", "*.props", "*.targets", "*.xaml"];
        foreach (string pattern in patterns)
        {
            foreach (string f in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, f);
                string[] parts = rel.Split(Path.DirectorySeparatorChar);
                if (parts.Contains("bin") || parts.Contains("obj")) continue;
                yield return f;
            }
        }
    }
}
