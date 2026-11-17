using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 一覧のクリックによる選択(Shift+クリックの範囲選択)。規則は純粋な関数(<see cref="GridSelectionRules"/>)で確かめ、
/// グリッドの組み込み(起点の持ち方・keepView での起点の追い方)は試験用の入口(<c>ApplyClick</c>)で確かめる(ShellBoundaryGuardTests が、マウスのイベントの合成を禁じている)。
/// </summary>
public sealed class GridSelectionTests
{
    private static IReadOnlySet<int> Set(params int[] xs) => new HashSet<int>(xs);

    private static int[] Sorted(IReadOnlySet<int> s) => [.. s.OrderBy(x => x)];

    // ---------------------------------------------------------------- 規則(純粋な関数)

    [Fact]
    public void A_plain_click_selects_only_that_item_and_makes_it_the_anchor()
    {
        ClickOutcome o = GridSelectionRules.Click(Set(1, 2), anchor: 1, index: 5, ctrl: false, shift: false);
        Assert.Equal([5], Sorted(o.Selected));
        Assert.Equal(5, o.Anchor);
        Assert.True(o.Changed);
        Assert.False(o.PendingSingleSelect);
    }

    [Fact]
    public void A_plain_click_on_one_of_several_selected_items_keeps_the_group_until_release_and_moves_the_anchor()
    {
        ClickOutcome o = GridSelectionRules.Click(Set(1, 2, 3), anchor: 1, index: 2, ctrl: false, shift: false);
        Assert.Equal([1, 2, 3], Sorted(o.Selected));
        Assert.True(o.PendingSingleSelect);   // 離したとき単独選択にする(ドラッグなら群ごと運ぶ)
        Assert.False(o.Changed);
        Assert.Equal(2, o.Anchor);

        // 1 つだけ選んでいる項目を押し直しても、変わらない(待ちにもしない)
        ClickOutcome single = GridSelectionRules.Click(Set(2), anchor: 2, index: 2, ctrl: false, shift: false);
        Assert.False(single.PendingSingleSelect);
        Assert.False(single.Changed);
    }

    [Fact]
    public void Ctrl_click_toggles_one_item_and_the_anchor_moves_to_it_even_when_it_is_removed()
    {
        ClickOutcome add = GridSelectionRules.Click(Set(1), anchor: 1, index: 4, ctrl: true, shift: false);
        Assert.Equal([1, 4], Sorted(add.Selected));
        Assert.Equal(4, add.Anchor);

        ClickOutcome remove = GridSelectionRules.Click(Set(1, 4), anchor: 4, index: 1, ctrl: true, shift: false);
        Assert.Equal([4], Sorted(remove.Selected));
        Assert.Equal(1, remove.Anchor);
    }

    [Theory]
    [InlineData(2, 6, new[] { 2, 3, 4, 5, 6 })]    // 起点より後ろ
    [InlineData(6, 2, new[] { 2, 3, 4, 5, 6 })]    // 起点より前(添字の小さい方から大きい方まで)
    [InlineData(4, 4, new[] { 4 })]                // 起点と同じ項目
    public void Shift_click_selects_the_range_from_the_anchor_and_drops_the_old_selection(int anchor, int clicked, int[] expected)
    {
        ClickOutcome o = GridSelectionRules.Click(Set(0, 9, anchor), anchor, clicked, ctrl: false, shift: true);
        Assert.Equal(expected, Sorted(o.Selected));
        Assert.Equal(anchor, o.Anchor);             // Shift+クリックでは起点は動かない
        Assert.False(o.PendingSingleSelect);
    }

    [Fact]
    public void Shift_clicking_again_changes_the_range_from_the_same_anchor()
    {
        ClickOutcome first = GridSelectionRules.Click(Set(3), anchor: 3, index: 7, ctrl: false, shift: true);
        ClickOutcome second = GridSelectionRules.Click(first.Selected, first.Anchor, index: 1, ctrl: false, shift: true);
        Assert.Equal([1, 2, 3], Sorted(second.Selected));   // 3 から 7 ではなく、3 から 1
        Assert.Equal(3, second.Anchor);
    }

    [Fact]
    public void Ctrl_shift_click_adds_the_range_to_the_current_selection_and_keeps_the_anchor()
    {
        ClickOutcome o = GridSelectionRules.Click(Set(0, 10), anchor: 3, index: 5, ctrl: true, shift: true);
        Assert.Equal([0, 3, 4, 5, 10], Sorted(o.Selected));   // 今の選択(0・10)は残る
        Assert.Equal(3, o.Anchor);
    }

    [Fact]
    public void Shift_click_without_an_anchor_acts_like_a_plain_click_and_ctrl_shift_just_adds_the_item()
    {
        ClickOutcome plain = GridSelectionRules.Click(Set(1, 2), anchor: -1, index: 5, ctrl: false, shift: true);
        Assert.Equal([5], Sorted(plain.Selected));
        Assert.Equal(5, plain.Anchor);                           // その項目が新しい起点

        ClickOutcome add = GridSelectionRules.Click(Set(1, 2), anchor: -1, index: 5, ctrl: true, shift: true);
        Assert.Equal([1, 2, 5], Sorted(add.Selected));
        Assert.Equal(5, add.Anchor);
    }

    [Fact]
    public void Clicking_the_empty_area_clears_only_when_no_modifier_is_held_and_never_moves_the_anchor()
    {
        ClickOutcome clear = GridSelectionRules.Click(Set(1, 2), anchor: 2, index: -1, ctrl: false, shift: false);
        Assert.Empty(clear.Selected);
        Assert.True(clear.Changed);
        Assert.Equal(2, clear.Anchor);

        foreach ((bool ctrl, bool shift) in new[] { (true, false), (false, true), (true, true) })
        {
            ClickOutcome keep = GridSelectionRules.Click(Set(1, 2), anchor: 2, index: -1, ctrl, shift);
            Assert.Equal([1, 2], Sorted(keep.Selected));         // うっかり選択を失わない
            Assert.False(keep.Changed);
        }
    }

    [Fact]
    public void A_shift_click_that_gives_the_same_selection_is_not_reported_as_a_change()
    {
        ClickOutcome o = GridSelectionRules.Click(Set(2, 3, 4), anchor: 2, index: 4, ctrl: false, shift: true);
        Assert.False(o.Changed);   // 選択の変化の知らせ(再描画・ボタンの更新)を無駄に出さない
    }

    // ---------------------------------------------------------------- グリッドへの組み込み

    private static string Hash(int i) => i.ToString("D64", System.Globalization.CultureInfo.InvariantCulture);

    private static List<GridItem> Unique(int first, int count) => Enumerable.Range(first, count).Select(i => new GridItem(Hash(i))).ToList();

    private static ThumbnailGridControl Grid(IReadOnlyList<GridItem> items)
    {
        var grid = new ThumbnailGridControl(256, 144, 8, 2, _ => []);
        grid.SetItems(items);
        grid.Measure(new System.Windows.Size(800, 600));
        grid.Arrange(new System.Windows.Rect(0, 0, 800, 600));
        grid.SetScale(1.0);
        return grid;
    }

    private static string[] Selected(ThumbnailGridControl grid) => grid.SelectedItems().Select(i => i.Hash).ToArray();

    [Fact]
    public async Task The_grid_applies_shift_and_ctrl_shift_clicks_from_the_last_plain_or_ctrl_click_and_reports_each_change_once()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Grid(Unique(1, 20));
            int changes = 0;
            grid.SelectionChanged = () => changes++;

            grid.ApplyClick(2, ctrl: false, shift: false);                       // 起点 = 項目 2(ハッシュ 3)
            grid.ApplyClick(6, ctrl: false, shift: true);
            Assert.Equal([Hash(3), Hash(4), Hash(5), Hash(6), Hash(7)], Selected(grid));
            Assert.Equal(Hash(3), grid.AnchorHashForTests);

            grid.ApplyClick(10, ctrl: true, shift: false);                       // Ctrl+クリックで足す。起点は 10 に移る
            grid.ApplyClick(12, ctrl: true, shift: true);                        // 今の選択に、起点(10)から 12 までを足す
            Assert.Equal([Hash(3), Hash(4), Hash(5), Hash(6), Hash(7), Hash(11), Hash(12), Hash(13)], Selected(grid));
            Assert.Equal(Hash(11), grid.AnchorHashForTests);

            Assert.Equal(4, changes);
        });
    }

    [Fact]
    public async Task Replacing_the_items_the_usual_way_drops_the_anchor_and_so_does_a_selection_that_is_not_a_click()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Grid(Unique(1, 20));
            grid.ApplyClick(3, false, false);
            Assert.Equal(Hash(4), grid.AnchorHashForTests);

            grid.SetItems(Unique(1, 20));                                        // 検索のたび(keepView なし): 選択も起点も捨てる
            Assert.Null(grid.AnchorHashForTests);
            grid.ApplyClick(5, ctrl: false, shift: true);                        // 起点なしの Shift+クリックは、普通のクリックと同じ
            Assert.Equal([Hash(6)], Selected(grid));

            grid.SelectByHashes([Hash(2)]);                                      // クリックでない選択の入口は、起点を持たない
            Assert.Null(grid.AnchorHashForTests);
        });
    }

    [Fact]
    public async Task Keeping_the_view_follows_the_anchor_by_hash_and_has_no_anchor_when_that_image_is_gone()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Grid(Unique(1, 30));
            grid.ApplyClick(9, false, false);                                    // 起点 = ハッシュ 10
            grid.ApplyClick(12, ctrl: true, shift: true);
            Assert.Equal(Hash(10), grid.AnchorHashForTests);

            // 先頭に 4 枚足した(位置は 4 つ後ろにずれる)。起点の画像はまだある: ハッシュで追う
            grid.SetItems([.. Unique(1001, 4), .. Unique(1, 30)], keepView: true);
            Assert.Equal(Hash(10), grid.AnchorHashForTests);
            int eight = grid.Items.ToList().FindIndex(i => i.Hash == Hash(8));
            grid.ApplyClick(eight, ctrl: false, shift: true);                    // 起点(10)から 8 まで
            Assert.Equal([Hash(8), Hash(9), Hash(10)], Selected(grid).OrderBy(h => h, StringComparer.Ordinal).ToArray());

            // 起点の画像が一覧から消えた(上限で落ちた・走査で消えた): 起点なし。Shift+クリックは普通のクリックになる
            grid.SetItems([.. Unique(1001, 4), .. Unique(11, 20)], keepView: true);
            Assert.Null(grid.AnchorHashForTests);
            grid.ApplyClick(2, ctrl: false, shift: true);
            Assert.Single(Selected(grid));
            Assert.Equal(grid.Items[2].Hash, grid.AnchorHashForTests);           // その項目が新しい起点
        });
    }

    [Fact]
    public async Task The_grid_rejects_a_click_on_an_item_outside_the_list()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Grid(Unique(1, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.ApplyClick(3, false, false));
        });
    }
}
