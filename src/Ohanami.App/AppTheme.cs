using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Ohanami.App;

/// <summary>
/// Windows 11 の Fluent テーマ(ライト)を、**いつでも外せる皮**として読み込む、**唯一の入口**(ADR 0040)。
/// アプリ(<see cref="Program"/>)と試験(<see cref="ApplyTo"/>。テストは Application を持たないので、根の要素の Resources に同じ辞書を足す)が、同じ辞書をここから作る。
/// <list type="number">
/// <item><b>読み込み方</b>: Fluent のリソース辞書(<c>PresentationFramework.Fluent</c> の <c>Themes/Fluent.Light.xaml</c>)を足す。<c>Application.ThemeMode</c>(試験的な API。WPF0001)は
///   **呼ばない**(csproj 全体で WPF0001 を黙らせない)。.NET 10.0.14 で測ると、この辞書を足すと、内部で <c>Application.ThemeMode</c> が Light になる
///   (辞書を足しても ThemeMode を呼んでも、同じ寸法・同じ見た目になる)。</item>
/// <item><b>Fluent のリソースのキー(文字列)は、どこからも参照しない</b>(DynamicResource・SetResourceReference・FindResource などで Fluent のキーを引かない)。キーは .NET の版で変わりうる
///   (Microsoft の手引き)うえ、消えると黙って既定値になるため。自前の色は <see cref="AppColors"/> に直書きで集める。ThemeGuardTests が、この 2 つを見張る。</item>
/// <item><b>窓の背景の Mica は切る</b>: Fluent は、窓の背景を Mica(壁紙の色が混ざる素材)にする。壁紙で窓の色が変わると、面の色をそろえられないので、
///   <c>Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop</c>(WPF の AppContext の switch。.NET 10.0.14 の PresentationFramework.dll に在る)を true にし(<see cref="Install"/>)、
///   窓の背景の色とタイトルバーの色は <see cref="AppColors.WindowBackground"/> を窓ごとに明示する(<see cref="StyleWindow"/>)。
///   switch の置き場所は <see cref="Install"/> の中の <c>AppContext.SetSwitch</c>(最初の窓を作る前に呼ぶ。実測: Application を作った直後の呼び出しで、窓の中の既定の背景が、壁紙の色の Mica から固定の色に替わった。
///   runtimeconfig には書かない=入口を 1 か所に保つ。試験は窓を表示しないので、switch は要らない)。</item>
/// </list>
/// 外すとき: <see cref="Install"/> の 2 行と <see cref="ApplyTo"/> の 1 行を消せば、WPF 標準の見た目(Aero2)になる(自前の色・余白の指定は、どちらの見た目でも読める値にしてある)。
/// </summary>
internal static class AppTheme
{
    /// <summary>Fluent(ライト)のリソース辞書の pack URI。.NET の共有ランタイム(Microsoft.WindowsDesktop.App)の PresentationFramework.Fluent.dll の中にある。</summary>
    private const string FluentLightUri = "pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Light.xaml";

    /// <summary>窓の背景の Mica を切る AppContext の switch の名前(PresentationFramework.dll が読む)。</summary>
    internal const string DisableBackdropSwitch = "Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop";

    /// <summary>Fluent(ライト)の辞書を新しく作る。</summary>
    private static ResourceDictionary CreateDictionary() => new() { Source = new Uri(FluentLightUri, UriKind.Absolute) };

    /// <summary>アプリの起動で、1 回だけ呼ぶ(最初の窓を作る前)。Mica を切る switch を立て、Application に Fluent(ライト)の辞書を足す。</summary>
    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        AppContext.SetSwitch(DisableBackdropSwitch, true);
        application.Resources.MergedDictionaries.Add(CreateDictionary());
    }

    /// <summary>
    /// 窓ごとに 1 回、窓の構築で呼ぶ(<c>: Window</c> の画面は必ず呼ぶ。ThemeGuardTests が見張る)。窓の背景を <see cref="AppColors.WindowBackground"/> にし、タイトルバーの色も同じ色にする。
    /// Mica を切っても、Fluent は、タイトルバーを壁紙の色の Mica のまま残す(切る switch は、窓の中の背景だけに効く。実測では、窓の中は固定の色、タイトルバーは壁紙で変わる薄い青だった)。
    /// タイトルバーの色は DWM の <c>DWMWA_CAPTION_COLOR</c>(Windows 11 の属性)で、こちらの側から明示する。古い OS では効かない(戻り値は無視する。見た目だけの指定)。
    /// </summary>
    public static void StyleWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Background = AppColors.WindowBackground;
        window.SourceInitialized += (_, _) =>
        {
            Color c = AppColors.WindowBackgroundColor;
            int colorRef = c.R | (c.G << 8) | (c.B << 16);   // COLORREF = 0x00BBGGRR
            _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DwmwaCaptionColor, ref colorRef, sizeof(int));
        };
    }

    /// <summary>DWMWA_CAPTION_COLOR(Windows 11 22000 以降)。</summary>
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>試験用。Application が無い試験で、根の要素(と、その下)に、アプリと同じ Fluent(ライト)の辞書を足す(暗黙のスタイルが、要素の Resources から探される)。</summary>
    public static void ApplyTo(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Resources.MergedDictionaries.Add(CreateDictionary());
    }
}
