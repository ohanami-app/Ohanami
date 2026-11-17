using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// Fluent の皮(<see cref="AppTheme"/>)を「いつでも外せる皮」のままにする決まり(ADR 0040)の見張り。ソースを読んで(コメントは除く)、次を確かめる:
/// <list type="number">
/// <item><b>自前の色は <c>AppColors.cs</c> の 1 か所</b>: 色を作る・取る書き方(<c>Brushes.</c>・<c>Colors.</c>・<c>Color.FromRgb/FromArgb/FromScRgb</c>・<c>new SolidColorBrush</c> ほかのブラシ・
///   <c>SystemColors</c>・<c>BrushConverter/ColorConverter</c>、それらの <c>using static</c> と別名)は、AppColors.cs 以外に書かない。許可は**ファイル名でなく、src/Ohanami.App からの相対の道**で決める
///   (別のフォルダの同名ファイルは許可しない)。静的な SystemColors は ThemeMode に追従しない。面の色が出どころ別々になるのを防ぐ。</item>
/// <item><b>Fluent のリソースのキーを引かない</b>: <c>DynamicResource/StaticResource</c>(拡張の <c>…Extension</c> も)・<c>SetResourceReference</c>・<c>FindResource</c>・<c>TryFindResource</c>・
///   <c>ComponentResourceKey</c>・<c>…ResourceKey</c>・<c>Resources[</c>(修飾なしも)は、どこにも書かない(キーは .NET の版で変わりうる。消えると黙って既定値になる)。
///   <c>Resources</c>・<c>ResourceDictionary</c>・<c>MergedDictionaries</c> に触れるのは AppTheme.cs だけ(修飾なしの <c>Resources.MergedDictionaries.Add</c> を、FrameworkElement の派生クラスの中に書いても検出する)。</item>
/// <item><b>テーマの入口は AppTheme.cs の 1 か所</b>: Fluent の pack URI(<c>PresentationFramework.Fluent</c>)・<c>ThemeMode</c>(試験的な API。WPF0001)・Mica を切る switch の名前・DWM(dwmapi)は、AppTheme.cs 以外に書かない。
///   <c>WPF0001</c> は、ソースのどこにも(<c>#pragma</c>・属性の中も)書かず、csproj・props・targets・.editorconfig・.globalconfig のどれにも書かない(csproj 全体で、試験的な API の警告を黙らせない)。</item>
/// <item><b>窓の背景・タイトルバーの色は自分で決める</b>: 1 つのファイルの中の <c>: Window</c>(・<c>: NavigationWindow</c>)の画面の数と、<c>AppTheme.StyleWindow(this)</c> の呼び出しの数が同じ
///   (1 つのファイルに窓が 2 つあって、片方だけ呼ぶ、を検出する)。<c>new Window()</c> の直生成は書かない(Mica を切っても、タイトルバーは壁紙の色の Mica のまま残るため。背景が未指定だと、窓ごとに色が違いうる)。</item>
/// </list>
/// <b>見張れない範囲</b>(完全には塞げない。ここに書いておく): 文字列でキーや型名を組み立てる参照、リフレクション、別のアセンブリ経由の呼び出し、型の名前を書かない組み立て(<c>new(...)</c> で色のブラシを作る)、
/// <c>: Window</c> を継承した別の窓クラスの派生(基底の窓が StyleWindow を呼んでいれば足りる)、<c>Resources</c> という名前の別の物(誤検出する側に倒してある)、xaml(App に xaml は置かない=別の試験が見張る)、
/// 引数が <c>this</c> でない StyleWindow、コメントの中のコード(除く)、ビルドの外の設定(環境変数・コマンドラインの <c>-p:NoWarn</c> など)。
/// </summary>
public sealed class ThemeGuardTests
{
    private const string AppColorsFile = "AppColors.cs";
    private const string AppThemeFile = "AppTheme.cs";

    private const string Win = @"(?:System\s*\.\s*Windows\s*\.\s*)?";
    private const string Media = @"(?:System\s*\.\s*Windows\s*\.\s*Media\s*\.\s*)?";

    private static readonly (string Pattern, string[] AllowedFiles, string Why)[] Rules =
    [
        // 1. 自前の色は AppColors.cs の 1 か所(「Brushes と . の間の空白・改行」「完全修飾」「using static・別名」「ほかのブラシ」「変換器」を含む)
        (@"\bBrushes\s*\.|\bColors\s*\.|\bSystemColors\b|\bColor\s*\.\s*From(?:Rgb|Argb|ScRgb)\b"
            + @"|\bnew\s+" + Media + @"(?:SolidColorBrush|LinearGradientBrush|RadialGradientBrush|ImageBrush|DrawingBrush|VisualBrush)\b"
            + @"|\b(?:BrushConverter|ColorConverter)\b"
            + @"|\busing\s+static\s+[\w.\s]*\b(?:Brushes|Colors|Color|SystemColors)\s*;"
            + @"|\busing\s+\w+\s*=\s*[\w.\s]*\b(?:Brushes|Colors|Color|SystemColors|SolidColorBrush|LinearGradientBrush|RadialGradientBrush|BrushConverter|ColorConverter)\s*;",
            [AppColorsFile], "自前の色は AppColors.cs に集める(直書き・静的な SystemColors・変換器・using static・別名を、ほかに書かない)"),

        // 2. Fluent のリソースのキーを引かない(どこでも不可。AppTheme でも不可)
        (@"\b(?:Dynamic|Static)Resource\w*|\bSetResourceReference\b|\bFindResource\b|\bTryFindResource\b|\bComponentResourceKey\b|\b\w*ResourceKey\b|\bResources(?:Property)?\s*\[|\bResourceReference\w*",
            [], "Fluent のリソースのキーを引かない"),

        // 2'. Resources・辞書に触れるのは AppTheme.cs だけ(修飾なしも含む)
        (@"\bResources(?:Property)?\b|\bResourceDictionary\b|\bMergedDictionaries\b", [AppThemeFile], "Resources・辞書に触れるのは AppTheme.cs だけ(テーマの入口)"),

        // 3. テーマの入口・Mica の switch・タイトルバーの色(DWM)は AppTheme.cs だけ
        (@"PresentationFramework\s*\.\s*Fluent|\bThemeMode\w*|DisableFluentThemeWindowBackdrop|\bdwmapi\b|\bDwmSetWindowAttribute\b",
            [AppThemeFile], "Fluent の読み込み・ThemeMode・Mica の switch・タイトルバーの色(DWM)は AppTheme.cs だけ"),

        // 3'. 試験的な API の警告(WPF0001)を、ソースのどこでも黙らせない(#pragma・SuppressMessage・属性を含む)
        (@"\bWPF0001\b", [], "試験的な API の警告(WPF0001)を、ソースのどこでも黙らせない"),

        // 4'. 窓の直生成は書かない(StyleWindow を呼べない。窓は、AppTheme.StyleWindow(this) を呼ぶ派生クラスにする)
        (@"\bnew\s+" + Win + @"(?:Window|NavigationWindow)\s*[({]", [], "窓を直に生成しない(Window のコンストラクタを呼ばない。AppTheme.StyleWindow(this) を呼ぶ派生クラスにする)"),
    ];

    private static readonly Regex WindowClass = new(@"\bclass\s+\w+(?:\s*<[^>]*>)?\s*:\s*" + Win + @"(?:Window|NavigationWindow)\b", RegexOptions.Compiled);
    private static readonly Regex StyleWindowCall = new(@"\bAppTheme\s*\.\s*StyleWindow\s*\(\s*this\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// 1 つのファイル(src/Ohanami.App からの相対の道。区切りは /)とソースについて、決まりに反するものを返す(コメントは除く)。
    /// 許可のあるファイルは、**相対の道が完全に一致するとき**だけ(別のフォルダの同名ファイルは許可しない)。
    /// </summary>
    internal static IEnumerable<string> Violations(string relativePath, string source)
    {
        string code = ShellBoundaryGuardTests.StripComments(source);
        foreach ((string pattern, string[] allowed, string why) in Rules)
        {
            if (allowed.Contains(relativePath, StringComparer.Ordinal)) continue;
            foreach (Match m in Regex.Matches(code, pattern))
                yield return $"{relativePath}: 「{Regex.Replace(m.Value, @"\s+", " ")}」 — {why}";
        }
        // 窓の数(: Window・: NavigationWindow)と、AppTheme.StyleWindow(this) の呼び出しの数が同じ
        int windows = WindowClass.Matches(code).Count, calls = StyleWindowCall.Matches(code).Count;
        if (windows != calls)
            yield return $"{relativePath}: 窓のクラスが {windows} 個、AppTheme.StyleWindow(this) の呼び出しが {calls} 個(窓ごとに 1 回ずつ呼ぶ。Mica を切ってあるので、窓の背景・タイトルバーの色は窓ごとに自分で決める)";
    }

    /// <summary>ビルドの設定のファイルに、WPF0001 の抑止が無いか(csproj・props・targets・.editorconfig・.globalconfig・.ruleset)。</summary>
    internal static IEnumerable<string> ConfigViolations(string relativePath, string content)
    {
        foreach (Match m in Regex.Matches(content, @"\bWPF0001\b"))
            yield return $"{relativePath}: 「{m.Value}」 — 試験的な API の警告を、ビルドの設定で黙らせない(csproj 全体・props・targets・.editorconfig・.globalconfig のどこにも書かない)";
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Ohanami.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("リポジトリのルート(Ohanami.sln のあるフォルダ)が見つからない: " + AppContext.BaseDirectory);
    }

    private static bool IsGenerated(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsConfigFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ruleset", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_app_source_keeps_the_fluent_skin_removable()
    {
        string src = Path.Combine(RepoRoot(), "src", "Ohanami.App");
        var violations = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !IsGenerated(f)))
        {
            scanned++;
            string relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            violations.AddRange(Violations(relative, File.ReadAllText(file)));
        }
        Assert.True(scanned >= 20, $"製品のソースを読めていない(読んだのは {scanned} 個)");
        Assert.True(violations.Count == 0, "Fluent の皮を外せなくする書き方がある:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void The_app_has_no_xaml_and_no_wpf0001_suppression_in_any_build_setting()
    {
        string root = RepoRoot();
        string appDir = Path.Combine(root, "src", "Ohanami.App");
        Assert.False(Directory.GetFiles(appDir, "*.xaml", SearchOption.AllDirectories).Any(f => !IsGenerated(f)), "xaml がある(App.xaml も使わない)");

        // ビルドの設定: リポジトリのルート(直下)・build・App・App のテスト(下の全部。bin・obj は除く)
        var files = new List<string>();
        files.AddRange(Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly).Where(IsConfigFile));
        foreach (string dir in new[] { Path.Combine(root, "build"), appDir, Path.Combine(root, "tests", "Ohanami.App.Tests") })
            if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !IsGenerated(f) && IsConfigFile(f)));
        Assert.True(files.Any(f => Path.GetFileName(f) == "Ohanami.App.csproj"), "設定のファイルを読めていない(Ohanami.App.csproj が見つからない)");

        var violations = files.SelectMany(f => ConfigViolations(Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f))).ToList();
        Assert.True(violations.Count == 0, "試験的な API の警告を、ビルドの設定で黙らせている:\n" + string.Join("\n", violations));
    }

    // ---------------------------------------------------------------- 見張り自身の確かめ(何も検出しないまま通ってしまわないように。塞いだ抜け道は、見逃さない例として足してある)

    [Theory]
    // 色: 基本
    [InlineData("Foo.cs", "var b = Brushes.Red;", true)]
    [InlineData("AppColors.cs", "var b = Brushes.Red;", false)]                              // 許可のあるファイル
    [InlineData("Sub/AppColors.cs", "var b = Brushes.Red;", true)]                           // 別のフォルダの同名ファイルは許可しない
    [InlineData("Foo.cs", "var b = new SolidColorBrush(Color.FromRgb(1, 2, 3));", true)]
    [InlineData("Foo.cs", "Background = SystemColors.WindowBrush;", true)]
    [InlineData("Foo.cs", "// Brushes.Red は使わない", false)]                              // コメントは除く
    // 色: 塞いだ抜け道
    [InlineData("Foo.cs", "using static System.Windows.Media.Brushes;", true)]
    [InlineData("Foo.cs", "using B = System.Windows.Media.Brushes;", true)]
    [InlineData("Foo.cs", "using static System.Windows.Media.Colors;", true)]
    [InlineData("Foo.cs", "using static System.Windows.Media.Color;", true)]
    [InlineData("Foo.cs", "using static System.Windows.SystemColors;", true)]
    [InlineData("Foo.cs", "using SC = System.Windows.SystemColors;", true)]
    [InlineData("Foo.cs", "var b = Brushes\n    .Red;", true)]                              // Brushes と . の間の改行
    [InlineData("Foo.cs", "var b = Brushes  .Red;", true)]                                  // Brushes と . の間の空白
    [InlineData("Foo.cs", "var c = Color\n.FromRgb(1, 2, 3);", true)]
    [InlineData("Foo.cs", "var c = Color.FromScRgb(1f, 0f, 0f, 0f);", true)]
    [InlineData("Foo.cs", "var b = new System.Windows.Media.SolidColorBrush(c);", true)]    // 完全修飾
    [InlineData("Foo.cs", "var b = new LinearGradientBrush(a, b, 45);", true)]
    [InlineData("Foo.cs", "var b = new RadialGradientBrush();", true)]
    [InlineData("Foo.cs", "var b = (Brush)new BrushConverter().ConvertFromString(\"#fff\");", true)]
    [InlineData("Foo.cs", "var c = (Color)ColorConverter.ConvertFromString(\"#fff\");", true)]
    [InlineData("Foo.cs", "var s = AppColors.Warn;", false)]                                // AppColors を取るのは正しい書き方(AppColors の Colors. に誤検出しない)
    [InlineData("Foo.cs", "Color c = AppColors.WindowBackgroundColor;", false)]              // Color 型の変数・取り出しは、色を作らない
    // キー: 基本
    [InlineData("Foo.cs", "x.SetResourceReference(Control.BackgroundProperty, \"k\");", true)]
    [InlineData("Foo.cs", "var r = FindResource(\"k\");", true)]
    [InlineData("Foo.cs", "var b = (Brush)Application.Current.Resources[\"k\"];", true)]
    [InlineData("AppTheme.cs", "var b = Application.Current.Resources[\"k\"];", true)]       // キーを引くのは、AppTheme でも不可
    [InlineData("AppTheme.cs", "application.Resources.MergedDictionaries.Add(d);", false)]   // 辞書を足すのは AppTheme だけ
    [InlineData("Foo.cs", "application.Resources.MergedDictionaries.Add(d);", true)]
    // キー: 塞いだ抜け道(e・d)
    [InlineData("Foo.cs", "internal sealed class V : Grid { void M() { var b = Resources[\"k\"]; } }", true)]                    // 修飾なしの Resources["k"]
    [InlineData("Foo.cs", "internal sealed class V : Grid { void M() { Resources.MergedDictionaries.Add(d); } }", true)]         // 修飾なしの Resources.MergedDictionaries.Add
    [InlineData("AppTheme.cs", "internal sealed class V : Grid { void M() { var b = Resources[\"k\"]; } }", true)]               // AppTheme でも、キーを引くのは不可(修飾なし)
    [InlineData("Foo.cs", "var e = new DynamicResourceExtension(\"k\");", true)]
    [InlineData("Foo.cs", "var e = new StaticResourceExtension(\"k\");", true)]
    [InlineData("Foo.cs", "var k = new ComponentResourceKey(typeof(Foo), \"k\");", true)]
    [InlineData("Foo.cs", "var k = SystemColors.WindowBrushKey;", true)]
    [InlineData("Foo.cs", "var s = CurrentResourceKey;", true)]
    // テーマの入口・WPF0001: 基本と、塞いだ抜け道(f)
    [InlineData("Foo.cs", "application.ThemeMode = ThemeMode.Light;", true)]
    [InlineData("Foo.cs", "var u = \"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Light.xaml\";", true)]
    [InlineData("Foo.cs", "#pragma warning disable WPF0001\nint a;", true)]                  // ThemeMode を書かなくても、WPF0001 の抑止は検出する
    [InlineData("Foo.cs", "[SuppressMessage(\"Usage\", \"WPF0001\")] void M() { }", true)]
    [InlineData("Foo.cs", "[System.Diagnostics.CodeAnalysis.Experimental(\"WPF0001\")] void M() { }", true)]
    [InlineData("Foo.cs", "var s = \"Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop\";", true)]
    [InlineData("Foo.cs", "[DllImport(\"dwmapi.dll\")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);", true)]
    [InlineData("AppTheme.cs", "[DllImport(\"dwmapi.dll\")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);", false)]
    // 窓: 基本と、塞いだ抜け道(g)
    [InlineData("Foo.cs", "internal sealed class W : Window { W() { AppTheme.StyleWindow(this); } }", false)]
    [InlineData("Foo.cs", "internal sealed class W : Window { W() { } }", true)]            // 窓の背景・タイトルバーの色を決めていない
    [InlineData("Foo.cs", "internal sealed class W : Window { W() { Background = AppColors.WindowBackground; } }", true)]   // 背景だけでは足りない(タイトルバーが壁紙の色のまま)
    [InlineData("Foo.cs", "class A : Window { A() { AppTheme.StyleWindow(this); } } class B : Window { B() { } }", true)]      // 1 ファイルに窓が 2 つで、片方だけ
    [InlineData("Foo.cs", "class A : Window { A() { AppTheme.StyleWindow(this); } } class B : Window { B() { AppTheme.StyleWindow(this); } }", false)]
    [InlineData("Foo.cs", "class A : NavigationWindow { A() { } }", true)]                   // Window 以外の窓
    [InlineData("Foo.cs", "class A : System.Windows.Window { A() { } }", true)]             // 完全修飾の基底
    [InlineData("Foo.cs", "class A<T> : Window { A() { } }", true)]                          // ジェネリック
    [InlineData("Foo.cs", "class A { void M() { var w = new " + "Window(); } }", true)]       // 窓の直生成(ShellBoundaryGuardTests が試験のソースの「new Window」を検出するので、文字列を分けて書く)
    [InlineData("Foo.cs", "class A { void M() { var w = new System.Windows." + "Window { Title = \"x\" }; } }", true)]
    [InlineData("Foo.cs", "class A { void M() { AppTheme.StyleWindow(this); } }", true)]    // 窓でないクラスが呼ぶのは、数が合わない
    [InlineData("Foo.cs", "class A : Grid { }", false)]                                       // 窓でなければ、呼ばなくてよい
    public void The_rules_detect_what_they_claim(string relativePath, string source, bool expectViolation)
    {
        Assert.Equal(expectViolation, Violations(relativePath, source).Any());
    }

    [Theory]
    [InlineData("src/Ohanami.App/Ohanami.App.csproj", "<NoWarn>$(NoWarn);WPF0001</NoWarn>", true)]
    [InlineData("Directory.Build.props", "<PropertyGroup><NoWarn>WPF0001</NoWarn></PropertyGroup>", true)]
    [InlineData("Directory.Build.targets", "<NoWarn>$(NoWarn);WPF0001</NoWarn>", true)]
    [InlineData(".editorconfig", "[*.cs]\ndotnet_diagnostic.WPF0001.severity = none", true)]
    [InlineData("src/.globalconfig", "is_global = true\ndotnet_diagnostic.WPF0001.severity = none", true)]
    [InlineData("src/Ohanami.App/Ohanami.App.csproj", "<NoWarn>$(NoWarn);CS1591</NoWarn>", false)]
    public void The_build_setting_rules_detect_what_they_claim(string relativePath, string content, bool expectViolation)
    {
        Assert.Equal(expectViolation, ConfigViolations(relativePath, content).Any());
    }
}
