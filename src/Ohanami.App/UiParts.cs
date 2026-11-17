using System.Windows;
using System.Windows.Controls;

namespace Ohanami.App;

/// <summary>画面の部品の共通の作り方(見た目の揃え)。</summary>
internal static class UiParts
{
    public static TextBlock Header(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

    /// <summary>表の列の見出し(太字の小さな見出し)。</summary>
    public static TextBlock HeaderCell(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold };

    /// <summary>文字の一覧(ListBox)の行の内側の余白。Fluent の ListBoxItem は余白が大きい(12,12。1 行 43px)ので、行の高さを抑えるよう、明示する(最小の窓で、フォルダ一覧が 2 行以上見える約束のため)。</summary>
    public static readonly Thickness ListRowPadding = new(8, 2, 8, 2);

    /// <summary>
    /// 文字の一覧(ListBox)の見た目: 白い下地と枠で、窓の面との**区切りを出す**。Fluent(ライト)の ListBox は、背景が窓の灰色とほぼ同じで枠が無く、一覧の端が分からない。
    /// 色は <see cref="AppColors"/>(Fluent のキーは引かない)。
    /// </summary>
    public static ListBox TextList(ListBox list)
    {
        list.Background = AppColors.ListSurface;
        list.BorderBrush = AppColors.ListBorder;
        list.BorderThickness = new Thickness(1);
        return list;
    }

    public static Button Button(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6) };
        b.Click += onClick;
        return b;
    }
}
