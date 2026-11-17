using System;
using System.Windows.Media;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// AppColors の値の整合。static の初期化は「書いた順」なので、ある色を別の色の元にすると、順番を間違えたとき、元の値が既定値(黒)のまま読まれる
/// (順番を間違えると、窓の背景が黒になる。画面を撮るまで気づかない)。画面に出る面の色が、意図した値であることを、値で確かめる。
/// </summary>
public sealed class AppColorsTests
{
    private static Color ColorOf(Brush b) => ((SolidColorBrush)b).Color;

    [Fact]
    public void The_window_background_brush_has_the_window_background_color_and_is_not_the_default_black()
    {
        Assert.Equal(AppColors.WindowBackgroundColor, ColorOf(AppColors.WindowBackground));
        Assert.Equal(Color.FromRgb(0xF3, 0xF3, 0xF3), AppColors.WindowBackgroundColor);
        Assert.True(AppColors.WindowBackground.IsFrozen);
    }

    /// <summary>WCAG の相対輝度とコントラスト比。</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void The_success_green_reads_as_text_on_the_list_surface_and_on_the_window_background()
    {
        Color green = ColorOf(AppColors.Success);

        Assert.True(Contrast(green, ColorOf(AppColors.ListSurface)) >= 4.5, "白の上のコントラスト比が足りない");
        Assert.True(Contrast(green, AppColors.WindowBackgroundColor) >= 4.5, "窓の背景の上のコントラスト比が足りない");
        Assert.True(green.G > green.R && green.G > green.B, "緑に見えない");
    }

    [Fact]
    public void The_surfaces_are_the_intended_light_colors_so_text_on_them_stays_readable()
    {
        foreach ((string name, Brush b) in new[]
        {
            ("WindowBackground", AppColors.WindowBackground), ("ListSurface", AppColors.ListSurface), ("StatusBarBackground", AppColors.StatusBarBackground), ("ChipBackground", AppColors.ChipBackground),
        })
        {
            Color c = ColorOf(b);
            Assert.True(c.A == 0xFF && c.R >= 0xF0 && c.G >= 0xF0 && c.B >= 0xF0, $"{name} が明るい色でない({c})。static の初期化の順を確かめる");
        }
        foreach ((string name, Brush b) in new[] { ("ImageHost", AppColors.ImageHost), ("GridBackground", AppColors.GridBackground) })
        {
            Color c = ColorOf(b);
            Assert.True(c.A == 0xFF && c.R <= 0x30 && c.G <= 0x30 && c.B <= 0x30 && (c.R | c.G | c.B) != 0, $"{name} が暗い灰色(黒でない)でない({c})");
        }
        Assert.Equal(8, AppColors.GridPlaceholders.Length);
        Assert.All(AppColors.GridPlaceholders, b => Assert.NotEqual(Color.FromRgb(0, 0, 0), ColorOf(b)));
    }
}
