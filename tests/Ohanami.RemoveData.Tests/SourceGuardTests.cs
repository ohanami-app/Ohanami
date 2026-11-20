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

    /// <summary>(言葉, 部分一致にするか)。部分一致でない言葉は、前後が英数字・下線でないときだけ当たる(短い言葉が、別の語の中に当たらないように)。</summary>
    private static readonly (string Word, bool Substring)[] Forbidden =
    [
        // 再帰の削除・ほかの削除
        ("Remove-Item", false), ("-Recurse", false), ("rmdir", false), ("rd", false), ("del", false), ("Clear-RecycleBin", false),
        // 文字列を実行する・ほかのプロセス
        ("Invoke-Expression", false), ("iex", false), ("Add-Type", false), ("Start-Process", false), ("Stop-Process", false), ("taskkill", false),
        ("EncodedCommand", false), ("FromBase64String", false),
        // 設定の変更
        ("Set-ExecutionPolicy", false), ("HKCU:", false), ("HKLM:", false), ("reg", false), ("Set-ItemProperty", false),
        // ネットワーク
        ("Invoke-WebRequest", false), ("Invoke-RestMethod", false), ("iwr", false), ("irm", false), ("curl", false), ("wget", false),
        ("System.Net", true), ("Net.WebClient", true), ("Start-BitsTransfer", false), ("Test-Connection", false), ("Test-NetConnection", false),
        ("Resolve-DnsName", false), ("http", true),
    ];

    [Fact]
    public void The_ps1_uses_none_of_the_words_for_recursive_removal_other_processes_settings_or_the_network()
    {
        string text = ScriptText();
        var found = new List<string>();
        foreach ((string word, bool substring) in Forbidden)
        {
            string pattern = substring
                ? Regex.Escape(word)
                : "(?<![A-Za-z0-9_])" + Regex.Escape(word) + "(?![A-Za-z0-9_])";
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                found.Add($"{word}(行 {text[..m.Index].Count(c => c == '\n') + 1})");
        }
        Assert.True(found.Count == 0, "使わない言葉が道具にある(コメントも数える): " + string.Join(", ", found));
    }

    [Fact]
    public void The_ps1_deletes_only_with_single_item_calls()
    {
        string text = ScriptText();
        Assert.Contains("[System.IO.File]::Delete(", text, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Directory]::Delete(", text, StringComparison.Ordinal);
        // フォルダの削除は、引数が 1 つ(再帰の指定が無い = 空のときだけ消える)
        foreach (Match m in Regex.Matches(text, @"\[System\.IO\.Directory\]::Delete\(([^)]*)\)"))
            Assert.DoesNotContain(",", m.Groups[1].Value, StringComparison.Ordinal);
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
