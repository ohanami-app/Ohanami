using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>WPF の部品は STA のスレッドで動かす(ウィンドウ無し。レイアウトの大きさは 0 のまま)。</summary>
internal static class Sta
{
    public static Task RunAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception e) { tcs.SetException(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}

public sealed class GridControlTests
{
    private static ThumbnailGridControl NewGrid() => new(256, 144, 8, 2, _ => []);

    private static List<GridItem> Items(int n)
    {
        var list = new List<GridItem>();
        for (int i = 0; i < n; i++) list.Add(new GridItem(new string((char)('a' + i % 6), 64)));
        return list;
    }

    [Fact]
    public async Task Changes_request_a_frame_and_a_frame_clears_the_request_so_an_idle_grid_needs_none()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = NewGrid();
            int requests = 0;
            grid.FrameRequested = () => requests++;

            grid.SetItems(Items(10));
            Assert.True(grid.NeedsFrame);
            Assert.True(requests > 0);

            grid.Update();               // 毎フレームの更新で、要求が済む
            Assert.False(grid.NeedsFrame);

            int before = requests;
            grid.Update();               // 何も変わらなければ、要求は増えず、フレームも要らないまま
            Assert.False(grid.NeedsFrame);
            Assert.Equal(before, requests);

            grid.SetScale(1.5);          // 変化(DPI)があれば、また要求する
            Assert.True(grid.NeedsFrame);
            Assert.True(requests > before);
        });
    }

    [Fact]
    public async Task Select_all_and_selection_changes_request_a_frame()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = NewGrid();
            grid.SetItems(Items(5));
            grid.Update();
            Assert.False(grid.NeedsFrame);
            int selectionEvents = 0;
            grid.SelectionChanged = () => selectionEvents++;

            grid.SelectAll();

            Assert.True(grid.NeedsFrame);
            Assert.Equal(5, grid.SelectedCount);
            Assert.Equal(1, selectionEvents);
        });
    }

    // ---------------------------------------------------------------- 見ている位置と選択を保つ(走査のあとの自動の作り直しで、利用者が見ている最中に、一覧が先頭へ戻ったり、選択が消えたりしないため)
    // 800 × 600 に置くと、1 行 3 枚(256 + 8 の幅)、行の高さ 152(144 + 8)、画面に 4 行ほど。

    /// <summary>ハッシュが互いに違う項目(first から count 個。ハッシュは数字を 64 桁にしたもの)。</summary>
    private static List<GridItem> Unique(int first, int count) =>
        Enumerable.Range(first, count).Select(i => new GridItem(i.ToString("D64", System.Globalization.CultureInfo.InvariantCulture))).ToList();

    private static string Hash(int i) => i.ToString("D64", System.Globalization.CultureInfo.InvariantCulture);

    private static ThumbnailGridControl Arranged(IReadOnlyList<GridItem> items)
    {
        ThumbnailGridControl grid = NewGrid();
        grid.SetItems(items);
        grid.Measure(new System.Windows.Size(800, 600));
        grid.Arrange(new System.Windows.Rect(0, 0, 800, 600));
        grid.SetScale(1.0);          // 列数は、置いた大きさ(ActualWidth)が決まってから、SetScale で数え直す(1 回目の Arrange の中では、まだ 0)
        Assert.Equal(3, grid.Columns);
        Assert.Equal(152, grid.RowHeight);
        return grid;
    }

    [Fact]
    public async Task Replacing_the_items_the_usual_way_goes_back_to_the_top_and_drops_the_selection()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Arranged(Unique(1, 60));
            grid.Offset = 800;
            grid.SelectByHashes([Hash(20)]);

            grid.SetItems(Unique(100, 60));

            Assert.Equal(0, grid.Offset);
            Assert.Equal(0, grid.SelectedCount);
        });
    }

    [Fact]
    public async Task Keeping_the_view_after_a_full_row_is_added_on_top_moves_the_offset_down_one_row_so_the_viewed_images_stay_where_they_are()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Arranged(Unique(1, 60));
            grid.Offset = 5 * 152 + 40;                         // 行の途中まで(6 行目の途中)
            double before = grid.Offset;

            grid.SetItems([.. Unique(1001, 3), .. Unique(1, 57)], keepView: true);   // 先頭に 3 枚(1 行分)、末尾の 3 枚は上限で落ちた

            Assert.Equal(before + 152, grid.Offset);            // 見ていた画像が、画面の同じ位置(行の途中の位置もそのまま)
        });
    }

    [Theory]
    [InlineData(1, 0)]    // 1 枚足す: 見ていた行の先頭の画像は、同じ行の次の列へ動くだけ。行は同じ
    [InlineData(2, 0)]
    [InlineData(3, 152)]  // ちょうど 1 行
    [InlineData(4, 152)]  // 1 行と 1 枚: 画像は 1 行下の、次の列へ
    [InlineData(7, 304)]
    public async Task Keeping_the_view_moves_the_offset_by_the_number_of_rows_the_viewed_image_moved(int added, double expectedShift)
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Arranged(Unique(1, 60));
            grid.Offset = 5 * 152 + 40;
            double before = grid.Offset;

            grid.SetItems([.. Unique(1001, added), .. Unique(1, 60)], keepView: true);

            Assert.Equal(before + expectedShift, grid.Offset);
        });
    }

    [Fact]
    public async Task Keeping_the_view_at_the_very_top_stays_at_the_top_so_the_new_images_are_seen()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Arranged(Unique(1, 60));
            Assert.Equal(0, grid.Offset);

            grid.SetItems([.. Unique(1001, 3), .. Unique(1, 60)], keepView: true);

            Assert.Equal(0, grid.Offset);
        });
    }

    [Fact]
    public async Task Keeping_the_view_keeps_the_selection_of_the_images_that_are_still_listed_and_drops_the_ones_that_are_gone()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Arranged(Unique(1, 60));
            grid.SelectByHashes([Hash(16), Hash(41), Hash(60)]);
            Assert.Equal(3, grid.SelectedCount);
            int selectionEvents = 0;
            grid.SelectionChanged = () => selectionEvents++;

            // 先頭に 2 枚足し、末尾の 1 枚(60)は上限で落ちた。41 はまだある
            grid.SetItems([.. Unique(1001, 2), .. Unique(1, 59)], keepView: true);

            Assert.Equal([Hash(16), Hash(41)], grid.SelectedItems().Select(i => i.Hash).OrderBy(h => h, StringComparer.Ordinal).ToArray());
            Assert.Equal(2, grid.SelectedCount);
            Assert.Equal(1, selectionEvents);                    // 選択の変化は、1 回知らせる
            Assert.Contains(grid.SelectedItems(), i => i.Hash == Hash(16));
            // 選択は位置ではなくハッシュで追う: 16 は 2 つ後ろの位置(先頭に 2 枚足したので)にある
            Assert.Equal(Hash(16), grid.Items[17].Hash);
        });
    }

    [Fact]
    public async Task Keeping_the_view_when_the_viewed_images_are_gone_uses_the_next_one_that_is_still_listed_and_otherwise_the_same_offset_inside_the_new_range()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Arranged(Unique(1, 60));
            grid.Offset = 5 * 152 + 40;                          // 見えている行の先頭は 16・17・18 番目(ハッシュ 16〜18)
            double before = grid.Offset;

            // 見えていた行の先頭の 3 枚が消えた。次に残っている 19 番目は、新しい並びでは 3 つ前へ(1 行上へ)動く
            grid.SetItems(Unique(1, 60).Where(i => i.Hash != Hash(16) && i.Hash != Hash(17) && i.Hash != Hash(18)).ToList(), keepView: true);
            Assert.Equal(before - 152, grid.Offset);

            // 見えている範囲の画像が 1 枚も残っていない: 位置の値はそのまま
            grid.Offset = 5 * 152 + 40;
            before = grid.Offset;
            grid.SetItems(Unique(2000, 60), keepView: true);
            Assert.Equal(before, grid.Offset);

            // 新しい項目が少なくて、その位置が範囲の外: 範囲の中に収める
            grid.SetItems(Unique(3000, 12), keepView: true);
            Assert.Equal(grid.MaxOffset, grid.Offset);
            Assert.True(grid.Offset <= 8 + 4 * 152 - 600 + 0.5);
            grid.SetItems([], keepView: true);
            Assert.Equal(0, grid.Offset);
        });
    }

    [Fact]
    public async Task Selecting_by_hash_ignores_hashes_that_are_not_listed_and_tells_that_the_selection_changed()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = Arranged(Unique(1, 10));
            int events = 0;
            grid.SelectionChanged = () => events++;

            grid.SelectByHashes([Hash(2), Hash(99)]);

            Assert.Equal([Hash(2)], grid.SelectedItems().Select(i => i.Hash).ToArray());
            Assert.Equal(1, events);
        });
    }

    [Fact]
    public async Task Set_items_replaces_the_items_and_clears_the_selection()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = NewGrid();
            grid.SetItems(Items(5));
            grid.SelectAll();

            grid.SetItems(Items(3));

            Assert.Equal(0, grid.SelectedCount);
            Assert.Equal(3, grid.ItemCount);
        });
    }

    [Fact]
    public async Task Arranging_again_at_the_same_size_does_not_request_a_frame_so_a_redraw_cannot_loop_forever()
    {
        // InvalidateVisual は再配置も起こす。再配置が再描画を要求すると、毎フレームの購読を外せなくなる
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = NewGrid();
            grid.SetItems(Items(40));
            grid.Measure(new System.Windows.Size(800, 600));
            grid.Arrange(new System.Windows.Rect(0, 0, 800, 600));
            Assert.True(grid.NeedsFrame);       // 大きさが決まった
            grid.Update();
            Assert.False(grid.NeedsFrame);

            grid.InvalidateMeasure();
            grid.Measure(new System.Windows.Size(800, 600));
            grid.Arrange(new System.Windows.Rect(0, 0, 800, 600));   // 同じ大きさ
            Assert.False(grid.NeedsFrame);

            grid.Measure(new System.Windows.Size(900, 600));
            grid.Arrange(new System.Windows.Rect(0, 0, 900, 600));   // 大きさが変わった
            Assert.True(grid.NeedsFrame);
        });
    }

    [Fact]
    public async Task The_grid_disables_the_input_method_so_that_a_japanese_ime_cannot_swallow_single_key_shortcuts()
    {
        await Sta.RunAsync(() =>
        {
            ThumbnailGridControl grid = NewGrid();

            Assert.False(System.Windows.Input.InputMethod.GetIsInputMethodEnabled(grid));
        });
    }

    [Fact]
    public async Task The_grid_requires_a_paths_resolver()
    {
        // FrameworkElement の生成は STA のスレッドでしかできない(引数の検査は、その基底クラスの生成のあと)。STA で動かさないと、別の例外になる
        await Sta.RunAsync(() => Assert.Throws<ArgumentNullException>(() => new ThumbnailGridControl(256, 144, 8, 2, null!)));
    }

    // ---------------------------------------------------------------- 操作できなかったときの知らせ(目立つバナー)

    private static ResultsPanel NewPanel(System.Collections.Generic.List<string> notices) => new(new TestLog(), notices.Add, new FakeShell());

    [Fact]
    public async Task A_problem_is_shown_as_a_banner_over_the_list_and_also_in_the_status_line()
    {
        await Sta.RunAsync(() =>
        {
            var notices = new System.Collections.Generic.List<string>();
            ResultsPanel panel = NewPanel(notices);
            Assert.False(panel.BannerVisible);

            panel.ShowProblem("元のファイルが無い");

            Assert.True(panel.BannerVisible);
            Assert.Equal("元のファイルが無い", panel.BannerText);
            Assert.Equal(["元のファイルが無い"], notices);
        });
    }

    [Fact]
    public async Task The_grids_drag_notice_is_wired_to_the_banner()
    {
        // ドラッグできなかった(元のファイルが無い)知らせは、グリッドの Notice から来る。それが、状態の行の小さな文字でなく、バナーに出ること
        await Sta.RunAsync(() =>
        {
            var notices = new System.Collections.Generic.List<string>();
            ResultsPanel panel = NewPanel(notices);

            panel.ResultGrid.Notice!("元のファイルが無いので、ドラッグできない");

            Assert.True(panel.BannerVisible);
            Assert.Contains("ドラッグできない", panel.BannerText, System.StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Copy_and_reveal_of_files_that_are_gone_use_the_same_banner()
    {
        await Sta.RunAsync(() =>
        {
            var notices = new System.Collections.Generic.List<string>();
            ResultsPanel panel = NewPanel(notices);
            // 結果に出ている画像のパスが、DB の上では現存でも、実際には無い(ここでは、パスを引けない = 見つからない)
            panel.ResultGrid.SetItems(Items(2));
            panel.ResultGrid.SelectAll();

            panel.CopySelectedFiles();
            Assert.True(panel.BannerVisible);
            Assert.Contains("元のファイルがありません", panel.BannerText, System.StringComparison.Ordinal);

            panel.SetSearchEnabled(true); // 無関係(表示のリセット確認の代わりに、別の操作でも同じ知らせが出ること)
            panel.RevealSelected();
            Assert.Contains("元のファイルがありません", panel.BannerText, System.StringComparison.Ordinal);

            panel.CopySelectedImage();
            Assert.Contains("元のファイルがありません", panel.BannerText, System.StringComparison.Ordinal);
            Assert.Equal(3, notices.Count(n => n.Contains("元のファイルがありません", System.StringComparison.Ordinal))); // 状態の行にも出ている(ログにも残る)
        });
    }
}
