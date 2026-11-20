using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ohanami.RemoveData.Tests;

/// <summary>
/// 道具(.ps1)のソースを読んで、使わない言葉と、削除の呼び方を調べる(<see cref="SourceGuardTests"/> が使う)。
/// 文字をつないで名前を作るなど、文字列の組み立てで抜けられるので、これは「うっかり」を見つけるための見張りで、悪意を防ぐものではない。
/// 道具が、再帰の削除・任意の実行・ネットワーク・設定の変更をしない、という約束の裏付けとして、緩めない。
/// </summary>
internal static class ScriptGuard
{
    private static string Word(string word) => "(?<![A-Za-z0-9_])" + Regex.Escape(word) + "(?![A-Za-z0-9_])";

    private static string Sub(string text) => Regex.Escape(text);

    /// <summary>(見せる名前, 正規表現)。大文字小文字は区別しない。コメントも数える。</summary>
    private static readonly (string Name, string Pattern)[] Forbidden =
    [
        // 再帰の削除・ほかの削除(別名を含む)
        ("Remove-Item", Word("Remove-Item")), ("-Recurse", Word("-Recurse")), ("rmdir", Word("rmdir")), ("rd", Word("rd")), ("del", Word("del")),
        ("rm", Word("rm")), ("ri", Word("ri")), ("erase", Word("erase")), ("Clear-RecycleBin", Word("Clear-RecycleBin")),
        // 文字列を実行する・ほかのプロセス・ほかの実行
        ("Invoke-Expression", Word("Invoke-Expression")), ("iex", Word("iex")), ("Add-Type", Word("Add-Type")),
        ("Start-Process", Word("Start-Process")), ("Stop-Process", Word("Stop-Process")), ("taskkill", Word("taskkill")),
        ("Invoke-Command", Word("Invoke-Command")), ("icm", Word("icm")), ("[Process]", @"\[\s*(System\.Diagnostics\.)?Process\s*\]"),
        ("Diagnostics", Sub("Diagnostics")), ("EncodedCommand", Word("EncodedCommand")), ("FromBase64String", Word("FromBase64String")),
        ("GetMethod", Word("GetMethod")), ("InvokeMember", Word("InvokeMember")), ("ScriptBlock", Word("ScriptBlock")),
        // ファイルの書き込み(道具は、ファイルを消すだけで、書かない)
        ("Set-Content", Word("Set-Content")), ("Add-Content", Word("Add-Content")), ("Out-File", Word("Out-File")),
        // 設定の変更
        ("Set-ExecutionPolicy", Word("Set-ExecutionPolicy")), ("HKCU:", Word("HKCU:")), ("HKLM:", Word("HKLM:")), ("reg", Word("reg")),
        ("Set-ItemProperty", Word("Set-ItemProperty")),
        // ネットワーク
        ("Invoke-WebRequest", Word("Invoke-WebRequest")), ("Invoke-RestMethod", Word("Invoke-RestMethod")), ("iwr", Word("iwr")), ("irm", Word("irm")),
        ("curl", Word("curl")), ("wget", Word("wget")), ("System.Net", Sub("System.Net")), ("Net.WebClient", Sub("Net.WebClient")),
        ("Start-BitsTransfer", Word("Start-BitsTransfer")), ("Test-Connection", Word("Test-Connection")), ("Test-NetConnection", Word("Test-NetConnection")),
        ("Resolve-DnsName", Word("Resolve-DnsName")), ("http", Sub("http")),
    ];

    /// <summary>使わない言葉が当たった場所(「言葉(行 N)」)。</summary>
    public static IReadOnlyList<string> FindForbidden(string text)
    {
        var found = new List<string>();
        foreach ((string name, string pattern) in Forbidden)
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                found.Add($"{name}(行 {LineOf(text, m.Index)})");
        return found;
    }

    // 許す削除の呼び方は、この 2 つだけ: 引数が、変数(または変数のプロパティ)1 つ。
    // 引数が 2 つ以上(再帰の指定)・入れ子の括弧・ほかの綴り([IO.Directory]::Delete など)・メソッドの形(.Delete())は、すべて外れる。
    private const string Argument = @"\$[A-Za-z_][A-Za-z0-9_]*(:[A-Za-z_][A-Za-z0-9_]*)?(\.[A-Za-z_][A-Za-z0-9_]*)*";

    private static readonly Regex AllowedDelete = new(
        @"\[System\.IO\.(File|Directory)\]::Delete\(\s*" + Argument + @"\s*\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex AnyDelete = new(Word("Delete"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>許す形ではない Delete の呼び出し(「行 N」)。</summary>
    public static IReadOnlyList<string> FindBadDeletes(string text)
    {
        var allowedStarts = new HashSet<int>();
        foreach (Match m in AllowedDelete.Matches(text)) allowedStarts.Add(m.Index + m.Value.IndexOf("Delete", StringComparison.Ordinal));
        return [.. AnyDelete.Matches(text).Where(m => !allowedStarts.Contains(m.Index)).Select(m => $"行 {LineOf(text, m.Index)}")];
    }

    public static int CountAllowedDeletes(string text) => AllowedDelete.Matches(text).Count;

    private static int LineOf(string text, int index) => text[..index].Count(c => c == '\n') + 1;
}
