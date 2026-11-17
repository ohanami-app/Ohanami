using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// <see cref="TestLook"/> が、Fluent の皮を**本当に当てている**ことの固定(ADR 0040)。
/// 寸法の試験の部品は、明示の Padding などで、テーマあり・なしで同じ寸法になる。そのため、TestLook の AppTheme.ApplyTo を抜いても、寸法の試験は通ってしまい、
/// 「テーマつきで測っている」という前提が、黙って崩れる(ApplyTo を抜いても、寸法の試験は 1 つも落ちない)。ここでは、**明示の指定が無い素の部品**(テーマの影響を直に受ける)の寸法で、皮の有無を判定する。
/// 目安(実測。.NET 10.0.14): TextBox 19→32px、ListBoxItem 21→43px(昔 → Fluent ライト)。しきい値は、その中間(28px・40px)。
/// </summary>
public sealed class TestLookTests
{
    private static (double TextBoxHeight, double ListBoxItemHeight) Measure(bool withTheme)
    {
        var root = new StackPanel();
        // TestLook.Setup と同じフォント・丸め。テーマなしの対照は、ApplyTo だけを抜いた形
        if (withTheme) TestLook.Setup(root);
        else
        {
            root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, new System.Windows.Media.FontFamily("Yu Gothic UI, Segoe UI"));
            root.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 13.0);
            root.UseLayoutRounding = true;
        }
        var box = new TextBox();                                                              // 素の TextBox(Padding などの明示なし)
        var item = new ListBoxItem { Content = new TextBlock { Text = "あ" } };               // 素の ListBoxItem
        root.Children.Add(box);
        root.Children.Add(item);
        root.Measure(new Size(400, 400));
        root.Arrange(new Rect(0, 0, 400, 400));
        root.UpdateLayout();
        return (box.ActualHeight, item.ActualHeight);
    }

    [Fact]
    public async Task TestLook_applies_the_fluent_skin_so_bare_controls_get_the_fluent_sizes()
    {
        await StaDispatcher.RunAsync(() =>
        {
            (double textBox, double listItem) = Measure(withTheme: true);
            Assert.True(textBox >= 28, $"素の TextBox の高さ {textBox}px が 28px 未満(Fluent の皮が当たっていない。TestLook.Setup が AppTheme.ApplyTo を呼んでいるか)");
            Assert.True(listItem >= 40, $"素の ListBoxItem の高さ {listItem}px が 40px 未満(Fluent の皮が当たっていない。TestLook.Setup が AppTheme.ApplyTo を呼んでいるか)");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Without_the_skin_the_same_bare_controls_are_below_the_thresholds_so_the_thresholds_tell_the_two_apart()
    {
        await StaDispatcher.RunAsync(() =>
        {
            (double textBox, double listItem) = Measure(withTheme: false);
            Assert.True(textBox < 28, $"テーマなしでも素の TextBox が {textBox}px(しきい値 28px で、皮の有無を見分けられない。.NET の版で寸法が変わった可能性)");
            Assert.True(listItem < 40, $"テーマなしでも素の ListBoxItem が {listItem}px(しきい値 40px で、皮の有無を見分けられない。.NET の版で寸法が変わった可能性)");
            return Task.CompletedTask;
        });
    }
}
