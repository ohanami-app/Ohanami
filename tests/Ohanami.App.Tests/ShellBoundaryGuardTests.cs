using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 「テストが、利用者の環境(本物のクリップボード・エクスプローラー・ダイアログ・他のアプリへのドラッグ・マウスとキーボード)に届かない」ことの見張り。
/// 偽物に替え忘れたテストは、本物のクリップボードを書き換える(ShellActions.CopyFiles → Clipboard.SetFileDropList)。新しいコードやテストが
/// そうならないよう、**ソースを読んで**次を確かめる(コメントは除く)。
/// <list type="number">
/// <item><b>製品のコード(src/Ohanami.App)</b>: 利用者の環境に届く API(Clipboard・シェル API・MessageBox・ファイルのダイアログ・ShowDialog・DoDragDrop・マウスの捕捉・
///   ウィンドウを前面に出す・実際の入力を送る API)を使ってよいファイルを、許可の表で決める。表に無いファイルが使ったら、このテストが落ちる
///   (使うなら、テストから届かない作りかを確かめて、表に足す)。</item>
/// <item><b>テストのコード(tests/Ohanami.App.Tests)</b>: これらの API を使わない。<c>ResultsPanel</c> は、必ず偽のシェル(<c>FakeShell</c>)を渡して作る
///   (2 つ引数の組み立ては、本物のシェルを使う)。マウス・キーのイベントを作って送らない(グリッドの CaptureMouse・DoDragDrop に届く)。
///   <c>TagSelectedAsync</c> を呼ぶテストは、名前を聞く画面(<c>PromptTag</c>)を偽物に替える。</item>
/// </list>
/// 見張れない範囲: 型の名前を書かない組み立て(<c>new(...)</c>)、リフレクション、別のアセンブリ経由の呼び出し。
/// </summary>
public sealed class ShellBoundaryGuardTests
{
    // ---------------------------------------------------------------- 製品のコードの許可の表

    /// <summary>(検出する式, その式を使ってよいファイル名)。表に無いファイルが使えば違反。</summary>
    private static readonly (string Pattern, string[] AllowedFiles, string Why)[] ProductRules =
    [
        (@"\bClipboard\.", ["ShellActions.cs"], "クリップボード(IShellActions の製品の実装だけ)"),
        (@"\bSHParseDisplayName\b|\bSHOpenFolderAndSelectItems\b", ["ShellActions.cs"], "エクスプローラーで表示(シェル API)"),
        (@"\bProcess\.Start\b|\bProcessStartInfo\b", [], "他のプロセスの起動"),
        (@"\bMessageBox\.Show\b", ["IndexPanel.cs", "Program.cs", "SetupWindow.cs"], "メッセージボックス(IndexPanel はボタンのハンドラの中だけ。Program は起動の失敗。SetupWindow は、取っている最中に閉じようとしたとき、1 つだけ)"),
        (@"\bOpenFolderDialog\b|\bOpenFileDialog\b|\bSaveFileDialog\b", ["IndexPanel.cs"], "ファイル・フォルダのダイアログ(ボタンのハンドラの中だけ)"),
        (@"\.ShowDialog\(", ["IndexPanel.cs", "TagPromptWindow.cs", "StartupGate.cs"], "モーダルの画面(StartupGate は、起動の関門のセットアップの画面。テストは画面を出さない口を渡す)"),
        (@"\bDragDrop\.DoDragDrop\b", ["IShellActions.cs", "ThumbnailGridControl.cs"], "OS のドラッグ(詳細画面は IShellActions 経由。一覧は実際のマウスの操作でしか届かない)"),
        (@"\bShellActions\.", ["IShellActions.cs"], "ShellActions の呼び出しは SystemShellActions だけ"),
        (@"\bSystemShellActions\b", ["IShellActions.cs", "ResultsPanel.cs", "IndexPanel.cs", "Program.cs"], "本物のシェルの実装を使うのは、ResultsPanel の 2 つ引数の組み立てと、IndexPanel の既定(失敗の一覧の「エクスプローラーで表示」の先。テストは偽のシェルを渡す)と、Program(起動の関門のセットアップの画面)だけ"),
        (@"\bTagPromptWindow\.Ask\b", ["ResultsPanel.cs"], "タグの名前を聞く本物の画面(PromptTag の既定。テストは差し替える)"),
        (@"\bCaptureMouse\(|\bMouse\.Capture\b", ["ThumbnailGridControl.cs", "DetailView.cs"], "マウスの捕捉(マウスのイベントのハンドラの中だけ)"),
        (@"\bActivate\(", ["MainWindow.cs", "IndexPanel.cs", "SetupWindow.cs"], "ウィンドウを前面に出す(MainWindow・SetupWindow は 2 つ目の起動のとき。IndexPanel は、開いたままの失敗の一覧の画面を前に出すとき)"),
        (@"\bSetCursorPos\b|\bmouse_event\b|\bkeybd_event\b|\bSendInput\b|\bSetForegroundWindow\b|\bSendKeys\b", [], "実際の入力を送る・前面を奪う API"),
        (@"\bnew ResultsPanel\(", ["MainWindow.cs"], "ResultsPanel を作る製品のコード(2 つ引数。本物のシェル)"),
    ];

    // ---------------------------------------------------------------- テストのコードの禁止

    private static readonly (string Pattern, string Why)[] TestRules =
    [
        (@"\bClipboard\.", "本物のクリップボード"),
        (@"\bProcess\.Start\b|\bProcessStartInfo\b", "他のプロセスの起動(エクスプローラーを含む)"),
        (@"\bMessageBox\b", "メッセージボックス"),
        (@"\bOpenFolderDialog\b|\bOpenFileDialog\b|\bSaveFileDialog\b", "ファイル・フォルダのダイアログ"),
        (@"\.ShowDialog\(", "モーダルの画面"),
        (@"\bDoDragDrop\b|\bDragDrop\.", "OS のドラッグ"),
        (@"\bCaptureMouse\b|\bMouse\.Capture\b", "マウスの捕捉"),
        (@"\bSetCursorPos\b|\bmouse_event\b|\bkeybd_event\b|\bSendInput\b|\bSetForegroundWindow\b|\bSendKeys\b", "実際の入力を送る・前面を奪う API"),
        (@"\bActivate\(", "ウィンドウを前面に出す"),
        (@"\bnew Window\b|\bnew MainWindow\b|\bMainWindow\b|\.Show\(\)|\bHwndSource\b", "ウィンドウを作る・表示する"),
        (@"\bShellActions\b|\bSystemShellActions\b|\bTagPromptWindow\b", "本物のシェル・本物の名前を聞く画面(偽物を渡す)"),
        (@"\bMouseButtonEventArgs\b|\bMouseEventArgs\b|\bMouseWheelEventArgs\b|\bOnMouse\w+\(", "マウスのイベントを作って送る(グリッドの CaptureMouse・DoDragDrop に届く)"),
    ];

    // ---------------------------------------------------------------- 本体

    [Fact]
    public void Product_code_uses_the_apis_that_reach_the_users_environment_only_in_the_allowed_files()
    {
        string src = Path.Combine(RepoRoot(), "src", "Ohanami.App");
        var violations = new List<string>();
        foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !IsGenerated(f)))
        {
            string name = Path.GetFileName(file);
            violations.AddRange(ProductViolations(name, File.ReadAllText(file)));
        }
        Assert.True(violations.Count == 0, "許可の表に無いファイルが、利用者の環境に届く API を使っている:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Test_code_never_touches_the_real_clipboard_explorer_dialogs_drag_windows_or_input_and_always_passes_a_fake_shell()
    {
        string tests = Path.Combine(RepoRoot(), "tests", "Ohanami.App.Tests");
        var violations = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.GetFiles(tests, "*.cs", SearchOption.AllDirectories).Where(f => !IsGenerated(f)))
        {
            if (Path.GetFileName(file) == nameof(ShellBoundaryGuardTests) + ".cs") continue;   // この見張り自身は、検出する式を文字列で持っている
            scanned++;
            violations.AddRange(TestViolations(Path.GetFileName(file), File.ReadAllText(file)));
        }
        Assert.True(scanned >= 10, $"テストのファイルを読めていない(読んだのは {scanned} 個)");
        Assert.True(violations.Count == 0, "テストが、利用者の環境に届きうる使い方をしている:\n" + string.Join("\n", violations));
    }

    // ---------------------------------------------------------------- 見張り自身の確かめ(何も検出しないまま通ってしまわないように)

    [Theory]
    [InlineData("Foo.cs", "Clipboard.SetText(x);", true)]
    [InlineData("ShellActions.cs", "Clipboard.SetText(x);", false)]                       // 許可のあるファイル
    [InlineData("Foo.cs", "// Clipboard.SetText(x);", false)]                             // コメントは除く
    [InlineData("Foo.cs", "var s = \"Clipboard.SetText\";", true)]                        // 文字列の中は、検出しても構わない(式を書かないための安全側)
    [InlineData("Foo.cs", "Process.Start(\"explorer\");", true)]
    [InlineData("Foo.cs", "var r = new ResultsPanel(log, n);", true)]
    [InlineData("MainWindow.cs", "var r = new ResultsPanel(log, n);", false)]
    [InlineData("Foo.cs", "SendKeys.SendWait(\"a\");", true)]
    [InlineData("Foo.cs", "/* MessageBox.Show(x) */ int a;", false)]
    public void The_product_rules_detect_what_they_claim(string file, string source, bool expectViolation)
    {
        Assert.Equal(expectViolation, ProductViolations(file, source).Any());
    }

    [Theory]
    [InlineData("var p = new ResultsPanel(log, n);", true)]                              // 偽のシェルを渡していない
    [InlineData("var p = new ResultsPanel(log, n, new FakeShell());", false)]
    [InlineData("var p = new ResultsPanel(log, n, shell)\n{ A = 1 };", false)]
    [InlineData("Clipboard.GetText();", true)]
    [InlineData("var e = new MouseButtonEventArgs(a, 1, MouseButton.Left);", true)]
    [InlineData("await panel.TagSelectedAsync();", true)]                                // PromptTag を替えていない
    [InlineData("PromptTag = (c, d) => null; await panel.TagSelectedAsync();", false)]
    [InlineData("// Clipboard.GetText();", false)]
    public void The_test_rules_detect_what_they_claim(string source, bool expectViolation)
    {
        Assert.Equal(expectViolation, TestViolations("SomeTests.cs", source).Any());
    }

    [Fact]
    public void The_comment_stripper_keeps_code_and_strings_and_drops_comments()
    {
        string stripped = StripComments("int a = 1; // x\nstring s = \"//not a comment\"; /* y\nz */ int b = @\"a\"\"//b\";\nvar c = \"\"\"raw // text\"\"\";");
        Assert.Contains("int a = 1;", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("// x", stripped, StringComparison.Ordinal);
        Assert.Contains("\"//not a comment\"", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("y\nz", stripped, StringComparison.Ordinal);
        Assert.Contains("int b =", stripped, StringComparison.Ordinal);
        Assert.Contains("@\"a\"\"//b\"", stripped, StringComparison.Ordinal);
        Assert.Contains("raw // text", stripped, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 実装

    private static IEnumerable<string> ProductViolations(string fileName, string source)
    {
        string code = StripComments(source);
        foreach ((string pattern, string[] allowed, string why) in ProductRules)
        {
            if (allowed.Contains(fileName, StringComparer.Ordinal)) continue;
            foreach (Match m in Regex.Matches(code, pattern))
                yield return $"{fileName}: 「{m.Value}」({why}。許可: {(allowed.Length == 0 ? "どこにも無い" : string.Join("、", allowed))})";
        }
    }

    private static IEnumerable<string> TestViolations(string fileName, string source)
    {
        string code = StripComments(source);
        foreach ((string pattern, string why) in TestRules)
        {
            foreach (Match m in Regex.Matches(code, pattern))
                yield return $"{fileName}: 「{m.Value}」({why})";
        }
        // ResultsPanel は、偽のシェルを渡して作る: 呼び出しの括弧の中(対応する閉じ括弧まで)に、shell か Shell が入っていること
        foreach (Match m in Regex.Matches(code, @"\bnew ResultsPanel\("))
        {
            int open = m.Index + m.Length - 1, depth = 0, end = code.Length;
            for (int k = open; k < code.Length; k++)
            {
                if (code[k] == '(') depth++;
                else if (code[k] == ')' && --depth == 0) { end = k + 1; break; }
            }
            string call = code[m.Index..end];
            if (!call.Contains("Shell", StringComparison.OrdinalIgnoreCase))
                yield return $"{fileName}: 「{call.Trim().ReplaceLineEndings(" ")}」(ResultsPanel を、偽のシェル(FakeShell)を渡さずに作っている)";
        }
        // 一覧の「タグを付ける」を呼ぶなら、名前を聞く本物の画面を、偽物に替えていること
        if (Regex.IsMatch(code, @"\bTagSelectedAsync\(") && !Regex.IsMatch(code, @"\bPromptTag\s*="))
            yield return $"{fileName}: TagSelectedAsync を呼ぶのに、PromptTag を偽物に替えていない(本物の名前を聞く画面が開く)";
    }

    internal static bool IsGenerated(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    internal static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Ohanami.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("リポジトリのルート(Ohanami.sln のあるフォルダ)が見つからない: " + AppContext.BaseDirectory);
    }

    /// <summary>C# のソースから、コメント(// と /* */)を除く。文字列(通常・逐語的 @"…"・生 """…""")と文字リテラルの中は、そのまま残す。</summary>
    internal static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            char n = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && n == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
            }
            else if (c == '/' && n == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
                sb.Append(' ');
            }
            else if (c == '"' && s.AsSpan(i).StartsWith("\"\"\""))
            {
                int quotes = 0;
                while (i + quotes < s.Length && s[i + quotes] == '"') quotes++;
                string closing = new('"', quotes);
                int end = s.IndexOf(closing, i + quotes, StringComparison.Ordinal);
                int stop = end < 0 ? s.Length : end + quotes;
                sb.Append(s, i, stop - i);
                i = stop;
            }
            else if (c == '@' && n == '"' || (c == '$' && n == '@' && i + 2 < s.Length && s[i + 2] == '"') || (c == '@' && n == '$' && i + 2 < s.Length && s[i + 2] == '"'))
            {
                int start = s.IndexOf('"', i);
                sb.Append(s, i, start - i + 1);
                i = start + 1;
                while (i < s.Length)
                {
                    sb.Append(s[i]);
                    if (s[i] == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
            }
            else if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < s.Length && s[i] != '\n')
                {
                    sb.Append(s[i]);
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                    if (s[i] == '"') { i++; break; }
                    i++;
                }
            }
            else if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < s.Length && s[i] != '\n')
                {
                    sb.Append(s[i]);
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                    if (s[i] == '\'') { i++; break; }
                    i++;
                }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }
}
