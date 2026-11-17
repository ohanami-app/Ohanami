using System.Windows.Input;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>ショートカットのキーの判定(純粋な関数)。どの要素にフォーカスがあるときに何が起きるかは、<see cref="KeyShortcuts"/> の説明。</summary>
public sealed class KeyShortcutsTests
{
    private static string Resolve(Key key, ModifierKeys mods, bool gridFocus = true, int selected = 1, Key ime = Key.None) =>
        KeyShortcuts.Resolve(key, ime, mods, gridFocus, selected).ToString();

    [Theory]
    [InlineData(Key.C, ModifierKeys.Control, "CopyImage")]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift, "CopyFiles")]
    [InlineData(Key.E, ModifierKeys.None, "RevealInExplorer")]
    public void On_the_grid_with_a_selection_the_shortcuts_work(Key key, ModifierKeys mods, string expected)
    {
        Assert.Equal(expected, Resolve(key, mods));
    }

    [Theory]
    [InlineData(Key.E, ModifierKeys.None)]
    [InlineData(Key.C, ModifierKeys.Control)]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift)]
    public void In_the_search_box_or_elsewhere_there_is_no_shortcut_so_e_is_just_a_letter_and_ctrl_c_copies_text(Key key, ModifierKeys mods)
    {
        Assert.Equal("None", Resolve(key, mods, gridFocus: false));
    }

    [Theory]
    [InlineData(Key.E, ModifierKeys.None)]
    [InlineData(Key.C, ModifierKeys.Control)]
    public void Without_a_selection_nothing_happens(Key key, ModifierKeys mods)
    {
        Assert.Equal("None", Resolve(key, mods, selected: 0));
    }

    [Theory]
    [InlineData(Key.E, ModifierKeys.Control)]                        // Ctrl+E は別のもの
    [InlineData(Key.E, ModifierKeys.Shift)]
    [InlineData(Key.E, ModifierKeys.Alt)]
    [InlineData(Key.C, ModifierKeys.None)]                           // C 単独は何でもない
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData(Key.C, ModifierKeys.Shift)]
    [InlineData(Key.A, ModifierKeys.Control)]                        // Ctrl+A は一覧自身が全選択する(ショートカットの判定には入れない)
    [InlineData(Key.Enter, ModifierKeys.None)]
    public void Other_keys_and_modifier_combinations_are_not_shortcuts(Key key, ModifierKeys mods)
    {
        Assert.Equal("None", Resolve(key, mods));
    }

    [Theory]
    [InlineData(Key.E, ModifierKeys.None, "RevealInExplorer")]
    [InlineData(Key.C, ModifierKeys.Control, "CopyImage")]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift, "CopyFiles")]
    [InlineData(Key.X, ModifierKeys.None, "None")]
    public void A_key_taken_by_the_ime_is_judged_by_the_key_the_ime_took(Key realKey, ModifierKeys mods, string expected)
    {
        // 日本語 IME がオンだと、E のような単体キーが Key.ImeProcessed で届く。本当のキーは ImeProcessedKey
        Assert.Equal(expected, Resolve(Key.ImeProcessed, mods, ime: realKey));
    }

    [Fact]
    public void An_ime_processed_key_with_no_real_key_is_nothing_and_the_grid_focus_rule_still_applies()
    {
        Assert.Equal("None", Resolve(Key.ImeProcessed, ModifierKeys.None, ime: Key.None));
        Assert.Equal("None", Resolve(Key.ImeProcessed, ModifierKeys.None, gridFocus: false, ime: Key.E)); // 検索欄で変換中の E は、ショートカットにしない
    }

    [Fact]
    public void The_effective_key_is_the_real_key_only_for_ime_processed_keys()
    {
        Assert.Equal(Key.E, KeyShortcuts.EffectiveKey(Key.ImeProcessed, Key.E));
        Assert.Equal(Key.C, KeyShortcuts.EffectiveKey(Key.C, Key.E));
        Assert.Equal(Key.None, KeyShortcuts.EffectiveKey(Key.ImeProcessed, Key.None));
    }
}
