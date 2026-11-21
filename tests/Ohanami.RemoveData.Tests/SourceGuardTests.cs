using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ohanami.App;
using Ohanami.Core;
using Xunit;

namespace Ohanami.RemoveData.Tests;

/// <summary>
/// 道具(src/Ohanami.RemoveData/)のソースを読んで、決まりを見張る。動かして確かめられない決まり(文字コード・改行・使わない言葉・入口が 1 つだけ)と、
/// アプリの側の名前(Core の定数)と道具が写した名前が食い違っていないことを確かめる。
/// </summary>
public sealed class SourceGuardTests
{
    private static string DataDir => RepoRoot.Combine("src", "Ohanami.RemoveData");

    private static byte[] ReadBytes(string name) => File.ReadAllBytes(Path.Combine(DataDir, name));

    private static string ScriptText() => new UTF8Encoding(false).GetString(ReadBytes(ScriptEntrance.ScriptName)).TrimStart('\uFEFF');

    // ---------------------------------------------------------------- 文字コードと改行

    [Fact]
    public void The_ps1_begins_with_a_utf8_byte_order_mark()
    {
        // BOM の無い .ps1 を、Windows PowerShell 5.1 は ANSI として読み、日本語が化ける
        byte[] bytes = ReadBytes(ScriptEntrance.ScriptName);
        Assert.True(bytes.Length > 3);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    [Fact]
    public void The_cmd_is_exactly_the_five_fixed_lines_in_ascii_with_crlf_only()
    {
        byte[] bytes = ReadBytes(ScriptEntrance.CmdName);
        Assert.All(bytes, b => Assert.True(b <= 0x7F));
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0A) Assert.True(i > 0 && bytes[i - 1] == 0x0D, $"CR の無い LF: 位置 {i}");
            if (bytes[i] == 0x0D) Assert.True(i + 1 < bytes.Length && bytes[i + 1] == 0x0A, $"LF の無い CR: 位置 {i}");
        }

        string[] lines =
        [
            "@echo off",
            "\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -ExecutionPolicy Bypass -File \"%~dpn0.ps1\" %*",
            "set \"OHANAMI_EXIT=%ERRORLEVEL%\"",
            "pause",
            "exit /b %OHANAMI_EXIT%",
        ];
        Assert.Equal(string.Concat(lines.Select(l => l + "\r\n")), Encoding.ASCII.GetString(bytes));
    }

    // ---------------------------------------------------------------- 使わない言葉(コメントも数える)

    [Fact]
    public void The_ps1_uses_none_of_the_words_for_recursive_removal_other_processes_creating_writing_moving_or_copying_files_settings_or_the_network()
    {
        IReadOnlyList<string> found = ScriptGuard.FindForbidden(ScriptText());
        Assert.True(found.Count == 0, "使わない言葉が道具にある(コメントも数える): " + string.Join(", ", found));
    }

    [Fact]
    public void The_ps1_deletes_only_with_the_two_single_item_forms()
    {
        string text = ScriptText();
        IReadOnlyList<string> bad = ScriptGuard.FindBadDeletes(text);
        Assert.True(bad.Count == 0, "許す形ではない Delete がある: " + string.Join(", ", bad));
        Assert.True(ScriptGuard.CountAllowedDeletes(text) >= 3, "ファイルの削除・フォルダの削除の呼び出しが見つからない");
        Assert.Contains("[System.IO.File]::Delete(", text, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Directory]::Delete(", text, StringComparison.Ordinal);
    }

    // 見張りそのものの確かめ(見逃さないこと、通すべき物を通すこと)
    [Theory]
    [InlineData("Remove-Item $p")]
    [InlineData("remove-item $p -Recurse")]
    [InlineData("rm $p")]
    [InlineData("ri $p")]
    [InlineData("del $p")]
    [InlineData("erase $p")]
    [InlineData("rd $p")]
    [InlineData("rmdir $p")]
    [InlineData("Set-Content $p 'x'")]
    [InlineData("Add-Content $p 'x'")]
    [InlineData("'x' | Out-File $p")]
    [InlineData("Invoke-Command { 1 }")]
    [InlineData("[Process]::Start('x')")]
    [InlineData("[System." + "Diagnostics.Process]::Start('x')")]
    [InlineData("[Diagnostics.Process]::Start('x')")]
    [InlineData("# コメントの中でも当たる: Remove-Item")]
    [InlineData("New-Item -ItemType File $p")]
    [InlineData("Copy-Item $a $b")]
    [InlineData("Move-Item $a $b")]
    [InlineData("Rename-Item $a 'b'")]
    [InlineData("Clear-Content $p")]
    [InlineData("Get-ChildItem | Tee-Object -FilePath $p")]
    [InlineData("Get-Process | Export-Csv $p")]
    [InlineData("Start-Transcript -Path $p")]
    [InlineData("Start-Job { 1 }")]
    [InlineData("Invoke-Item $p")]
    [InlineData("Register-ScheduledTask -TaskName x")]
    [InlineData("Remove-ItemProperty -Path $p -Name x")]
    [InlineData("ni $p")]
    [InlineData("copy $a $b")]
    [InlineData("mv $a $b")]
    [InlineData("[System.IO.File]::WriteAllText($p, 'x')")]
    [InlineData("[IO.File]::AppendAllLines($p, $l)")]
    [InlineData("[System.IO.File]::Create($p)")]
    [InlineData("[System.IO.File]::Move($a, $b)")]
    [InlineData("[System.IO.File]::Copy($a, $b)")]
    [InlineData("[System.IO.File]::Replace($a, $b, $c)")]
    [InlineData("[System.IO.File]::OpenWrite($p)")]
    [InlineData("[System.IO.File]::SetAttributes($p, 'Normal')")]
    [InlineData("[System.IO.Directory]::CreateDirectory($p)")]
    [InlineData("[System.IO.Directory]::Move($a, $b)")]
    [InlineData("New-Object System.IO.StreamWriter($p)")]
    [InlineData("[System.IO.FileStream]::new($p, [System.IO.FileMode]::Create)")]
    [InlineData("[System.IO.File]::Open($p, [System.IO.FileMode]::OpenOrCreate)")]
    [InlineData("'x' > $p")]
    [InlineData("Get-Date >> $p")]
    [InlineData("Get-Date >$p")]
    [InlineData("Write-Output 1 > 'out.txt'")]
    public void The_forbidden_word_check_catches_the_spelling(string source) =>
        Assert.NotEmpty(ScriptGuard.FindForbidden(source));

    [Theory]
    [InlineData("[System.IO.File]::Delete($item.Path)")]
    [InlineData("try { [System.IO.Directory]::Delete($dir.Path) } catch { }")]
    [InlineData("[System.IO.Directory]::Delete($script:RootPath)")]
    [InlineData("$deleted = 1; $result.Deleted.Add($x)")]
    [InlineData("[System.IO.File]::Exists($p) -and [System.IO.File]::GetAttributes($p)")]
    [InlineData("[System.IO.File]::Open($p, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)")]
    [InlineData("[System.IO.Directory]::GetFiles($p)")]
    [InlineData("$sha = [System.Security.Cryptography.SHA256]::Create()")]
    [InlineData("if ($a -gt 1 -and $b -ge 2) { $x = 'a->b' }")]
    [InlineData("# 矢印の -> と、ブロックコメントの終わりの #> と、ストリームの 2>&1 は当たらない")]
    [InlineData("Write-Host 'a=>b' 2>&1")]
    [InlineData("$stream = $null")]
    [InlineData("# 消せない物は、削除の呼び出しの例外から分かる")]
    [InlineData("Write-Host ('これは、ひらがなの「り」と、カタカナの「リ」')")]
    public void The_forbidden_word_check_lets_the_allowed_spellings_through(string source)
    {
        Assert.Empty(ScriptGuard.FindForbidden(source));
        Assert.Empty(ScriptGuard.FindBadDeletes(source));
    }

    [Theory]
    [InlineData("[System.IO.Directory]::Delete($p, $true)")]
    [InlineData("[System.IO.Directory]::Delete($p,$true)")]
    [InlineData("[IO.Directory]::Delete($p, $true)")]
    [InlineData("[IO.Directory]::Delete($p)")]
    [InlineData("[System.IO.Directory]::Delete((Join-Path $a $b), $true)")]
    [InlineData("[System.IO.Directory]::Delete((Join-Path $a $b))")]
    [InlineData("[System.IO.Directory]::Delete($p -replace 'x','y')")]
    [InlineData("[System.IO.Directory]::Delete('C:\\x')")]
    [InlineData("[System.IO.Directory]::Delete()")]
    [InlineData("[System.IO.File]::Delete($a, $b)")]
    [InlineData("$info.Delete()")]
    [InlineData("$info.Delete($true)")]
    [InlineData("$d.delete($true)")]
    [InlineData("[System.IO.Directory]::DELETE($p, $true)")]
    public void The_delete_check_fails_every_form_but_the_two_single_item_ones(string source) =>
        Assert.NotEmpty(ScriptGuard.FindBadDeletes(source));

    [Fact]
    public void The_ps1_has_the_byte_order_mark_exactly_once()
    {
        byte[] bytes = ReadBytes(ScriptEntrance.ScriptName);
        Assert.False(HasDoubledBom(bytes), "BOM が二重になっている");
        Assert.DoesNotContain('\uFEFF', ScriptText());                      // 本文の途中にも無い
    }

    [Fact]
    public void A_doubled_byte_order_mark_is_told_apart_by_the_check_the_test_uses()
    {
        Assert.True(HasDoubledBom([0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, 0x23]));
        Assert.False(HasDoubledBom([0xEF, 0xBB, 0xBF, 0x23, 0x20, 0x20]));
    }

    private static bool HasDoubledBom(byte[] bytes) => bytes.Length >= 6 && bytes[3] == 0xEF && bytes[4] == 0xBB && bytes[5] == 0xBF;

    // ---------------------------------------------------------------- 画面の文の括弧(ADR 0038: 括弧は半角。半角の「(」の前に、半角の空白を 1 つ入れる)

    // 例外は、行や文の頭・空白の直後・開きの括弧やかぎ括弧(「(」「「」「『」「[」)の直後だけ。書式の差し込み({0} など)は、差し込んだあとの文で数えるので、「{0}(」は違反。
    // 文字列の中の正規表現(名前の型を作る Add-FileRule・Add-DirRule の行)は、画面に出ないので、見ない。
    [Fact]
    public void The_text_the_ps1_prints_has_a_half_width_space_before_every_half_width_open_bracket_and_no_full_width_brackets()
    {
        Assert.Empty(BracketViolations(ScriptText()));
    }

    [Theory]
    [InlineData("Write-Host 'モデル(約 1 KB)'", 1)]
    [InlineData("Write-Host 'モデル (約 1 KB)'", 0)]
    [InlineData("Write-Host 'モデル（約 1 KB）'", 1)]
    [InlineData("Write-Host ('{0} ファイル({1})' -f $a, $b)", 1)]
    [InlineData("Write-Host ('{0}({1})' -f $a, $b)", 1)]                       // 差し込みの直後(差し込んだあとは、文字の直後)
    [InlineData("Write-Host ('ファイル ({1})' -f $a, $b)", 0)]
    [InlineData("Write-Host 'file(x)'", 1)]                                    // 英数字の直後
    [InlineData("Write-Host 'パス ' + $x + '(' + $y + ')'", 1)]                // 文字列の頭の「(」でも、前に連結される式(パスなど)があれば、直後
    [InlineData("Write-Host 'パス ' + $x + ' (' + $y + ')'", 0)]
    [InlineData("$kept = $_ + '(リンク)'", 1)]
    [InlineData("$kept = $_ + ' (リンク)'", 0)]
    [InlineData("Write-Host '(先頭の括弧)'", 0)]                                // 文の頭
    [InlineData("Write-Host '「(x)」'", 0)]                                    // 開きのかぎ括弧の直後
    [InlineData("Write-Host '[(x)]'", 0)]                                      // 開きの角括弧の直後
    [InlineData("Write-Host '((x))'", 0)]                                      // 開きの括弧の直後
    [InlineData("Add-FileRule $spec ('^' + [regex]::Escape($p) + '(\\.part)?$') 'Models'", 0)]   // 名前の型(正規表現)は、画面に出ない
    [InlineData("# 説明(コメントは数えない)", 0)]
    [InlineData("return $true   # 開けなかった(別の所有者)", 0)]
    public void The_bracket_check_finds_the_forms_it_is_meant_to_find(string line, int expected)
    {
        Assert.Equal(expected, BracketViolations(line).Count);
    }

    /// <summary>
    /// コメント(<c>&lt;# #&gt;</c> の説明・行頭の <c>#</c>・行末の <c># </c>)と、名前の型の行を除いた行から、全角の括弧と、
    /// 空白・文の頭・開きの括弧の直後ではない半角の「(」(文字列の中。連結の <c>+ '(</c> を含む)を探す。
    /// </summary>
    private static List<string> BracketViolations(string text)
    {
        var found = new List<string>();
        bool inHelp = false;
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.StartsWith("<#", StringComparison.Ordinal)) inHelp = true;
            if (inHelp)
            {
                if (line.Contains("#>", StringComparison.Ordinal)) inHelp = false;
                continue;
            }
            if (line.TrimStart().StartsWith('#')) continue;
            Match comment = Regex.Match(line, @"\s+#\s");
            string code = comment.Success ? line[..comment.Index] : line;
            if (Regex.IsMatch(code, "[（）]")) { found.Add($"{i + 1}: {line.Trim()}"); continue; }
            string trimmed = code.TrimStart();
            if (trimmed.StartsWith("Add-FileRule", StringComparison.Ordinal) || trimmed.StartsWith("Add-DirRule", StringComparison.Ordinal)) continue;
            bool bad = Regex.IsMatch(code, @"\+\s*['""]\(");                                  // 連結で、前の式に続く「(」から始まる文字列
            foreach (Match literal in Regex.Matches(code, @"'(?:[^']|'')*'|""(?:[^""`]|`.)*"""))
            {
                string t = literal.Value;
                for (int k = 2; k < t.Length; k++)                                              // 先頭の引用符と、その次の文字(文の頭)は、数えない
                    if (t[k] == '(' && !" 「『[(\t".Contains(t[k - 1])) bad = true;
            }
            if (bad) found.Add($"{i + 1}: {line.Trim()}");
        }
        return found;
    }

    // ---------------------------------------------------------------- アプリの側の名前との一致

    [Fact]
    public void The_default_data_folder_is_decided_the_same_way_the_app_decides_it()
    {
        string text = ScriptText();
        Assert.Matches(@"\[Environment\]::GetFolderPath\('LocalApplicationData'\)\)\s+'Ohanami'", text);
        string appPaths = File.ReadAllText(RepoRoot.Combine("src", "Ohanami.App", "AppPaths.cs"));
        Assert.Contains("Path.Combine(localAppData, \"Ohanami\")", appPaths, StringComparison.Ordinal);
        Assert.Contains("Environment.SpecialFolder.LocalApplicationData", appPaths, StringComparison.Ordinal);
    }

    [Fact]
    public void The_environment_variable_names_are_the_apps_names()
    {
        string text = ScriptText();
        Assert.Contains("$DataDirectoryVariable = '" + AppPaths.DataDirectoryVariable + "'", text, StringComparison.Ordinal);
        Assert.Contains("$ModelsDirectoryVariable = '" + AppPaths.ModelsDirectoryVariable + "'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_model_folder_and_file_names_are_the_ones_the_app_inspects()
    {
        string text = ScriptText();
        Assert.Equal(OhanamiCore.ModelDirectoryName, ScriptString(text, "EmbeddingDirectoryName"));
        Assert.Equal(VlmOptions.ModelDirectoryName, ScriptString(text, "VlmDirectoryName"));
        Assert.Equal(
            ModelFiles.InspectEmbedding(@"C:\x").Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal),
            ScriptList(text, "EmbeddingFiles").Order(StringComparer.Ordinal));
        Assert.Equal(
            ModelFiles.InspectVlm(@"C:\x").Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal),
            ScriptList(text, "VlmFiles").Order(StringComparer.Ordinal));
    }

    private static string ScriptString(string text, string variable)
    {
        Match m = Regex.Match(text, @"^\$" + variable + @" = '([^']*)'\s*$", RegexOptions.Multiline);
        Assert.True(m.Success, "道具に $" + variable + " の定義が無い");
        return m.Groups[1].Value;
    }

    private static List<string> ScriptList(string text, string variable)
    {
        Match m = Regex.Match(text, @"^\$" + variable + @" = @\(([^)]*)\)\s*$", RegexOptions.Multiline);
        Assert.True(m.Success, "道具に $" + variable + " の定義が無い");
        return [.. Regex.Matches(m.Groups[1].Value, "'([^']*)'").Select(x => x.Groups[1].Value)];
    }

    // ---------------------------------------------------------------- 入口が 1 つだけ

    [Fact]
    public void Only_the_entrance_file_starts_processes()
    {
        string startInfo = "Process" + "StartInfo";            // このファイル自身が、見張る言葉を書かないように
        string diagnostics = "System." + "Diagnostics";
        string projectDirectory = RepoRoot.Combine("tests", "Ohanami.RemoveData.Tests");
        string[] sources = [.. Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))];

        string[] withStartInfo = [.. sources.Where(f => File.ReadAllText(f).Contains(startInfo, StringComparison.Ordinal)).Select(Path.GetFileName)!];
        Assert.Equal(["ScriptEntrance.cs"], withStartInfo);
        string[] withDiagnostics = [.. sources.Where(f => File.ReadAllText(f).Contains(diagnostics, StringComparison.Ordinal)).Select(Path.GetFileName)!];
        Assert.Equal(["ScriptEntrance.cs"], withDiagnostics);
    }
}
