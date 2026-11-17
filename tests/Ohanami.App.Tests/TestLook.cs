using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ohanami.App.Tests;

/// <summary>
/// 寸法を測る試験が、**実機と同じ見た目の前提**(MainWindow が窓に付けるフォントと、アプリが読み込む Fluent の皮)で部品を置くための 1 か所。
/// 試験は Application を持たないので、テーマは根の要素の Resources に足す(<see cref="AppTheme.ApplyTo"/>。アプリは <see cref="AppTheme.Install"/>。辞書は同じ)。
/// 3 つの寸法の試験(MinWindowSizeTests・IndexSectionHeightTests・LeftPanelMeasureTests)が、フォントの設定をここで共有する
/// (ADR 0040: テーマなしで測ると、最小の窓の約束が、黙って無検証になる)。
/// </summary>
internal static class TestLook
{
    /// <summary>MainWindow と同じフォント・丸め + アプリと同じ Fluent(ライト)。根の要素に 1 回だけ呼ぶ。</summary>
    public static void Setup(FrameworkElement root)
    {
        root.SetValue(TextElement.FontFamilyProperty, new FontFamily("Yu Gothic UI, Segoe UI"));
        root.SetValue(TextElement.FontSizeProperty, 13.0);
        root.UseLayoutRounding = true;
        AppTheme.ApplyTo(root);
    }
}
