using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 詳細画面を、状態の行を除く窓の全面に重ねて出す作り(ADR 0050)の約束:
/// 層が覆う範囲(左の列・検索ボックス・見出し・一覧のすべて。状態の行は覆わない)、赤い帯が詳細画面の前に見えること、
/// 開閉しても一覧の大きさ・列数・位置が変わらないこと、キーボードのフォーカスが詳細画面の外へ出ないこと。
/// 窓は作らない(ShellBoundaryGuardTests): <see cref="MainLayout"/>(MainWindow と同じ組み立て)を、外側の大きさを指定して置いて測る。
/// </summary>
public sealed class DetailOverlayTests
{
    private static readonly (double W, double H)[] Sizes = [(WindowLayout.MinWindowWidth, WindowLayout.MinWindowHeight), (1180, 760)];

    private static bool IsInside(DependencyObject ancestor, DependencyObject? node)
    {
        for (DependencyObject? n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    /// <summary>
    /// 窓の中の点に、マウスが当たる要素(いちばん前)。実際のマウスの当たり判定と同じに、見えない(Hidden・Collapsed)・当たらない(IsHitTestVisible が false)要素は、子ごと飛ばす
    /// (<see cref="VisualTreeHelper.HitTest(Visual, Point)"/> だけだと、隠した要素の子にも当たる)。
    /// </summary>
    private static DependencyObject? HitAt(MainLayoutHarness h, Point p)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(h.Root,
            d => d is UIElement { Visibility: not Visibility.Visible } or UIElement { IsHitTestVisible: false } ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            r => { hit = r.VisualHit; return HitTestResultBehavior.Stop; },
            new PointHitTestParameters(p));
        return hit;
    }

    // ---------------------------------------------------------------- 覆う範囲

    [Fact]
    public async Task The_detail_layer_covers_the_left_panel_the_search_box_and_the_list_but_not_the_status_row()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            foreach ((double w, double hgt) in Sizes)
            {
                MainLayoutHarness h = MainLayoutHarness.Create(w, hgt);
                Grid layer = h.Results.DetailLayer;

                // 閉じている間は、層は隠れていて、下の操作を邪魔しない
                Assert.Equal(Visibility.Collapsed, layer.Visibility);
                Assert.False(IsInside(layer, HitAt(h, new Point(50, 100))), "閉じているのに、左の列の上の点が、詳細画面に当たる");

                await h.Results.OpenDetailAsync(MainLayoutHarness.Hash(0));
                h.Layout();

                // 範囲: 窓の中の全面 − 状態の行(上下左右とも)
                double statusHeight = h.Status.ActualHeight;
                Assert.True(statusHeight > 0);
                Rect bounds = h.BoundsOf(layer);
                Assert.Equal(new Rect(0, 0, h.ClientWidth, h.ClientHeight - statusHeight), bounds);
                Assert.Equal(bounds.Bottom, h.BoundsOf(h.Status).Top);

                // 前後の順: 左の列 < 一覧 < 詳細画面の層(後に足したものが前面)
                var siblings = ((Panel)layer.Parent).Children.Cast<UIElement>().ToList();
                Assert.True(siblings.IndexOf(h.Index) < siblings.IndexOf(h.Results) && siblings.IndexOf(h.Results) < siblings.IndexOf(layer));

                // 当たり: 左の列・検索ボックス・見出しの位置・一覧の位置の点は、すべて詳細画面に当たる。状態の行の点は、状態の行に当たる
                Point[] covered = [new(50, 100), new(h.Index.ActualWidth + 40, 20), new(h.Index.ActualWidth + 100, 60), new(h.Index.ActualWidth + 100, 300), new(w / 2, bounds.Bottom - 5)];
                foreach (Point p in covered)
                    Assert.True(IsInside(layer, HitAt(h, p)), $"({p.X}, {p.Y}) の点が、詳細画面に当たらない(窓 {w}×{hgt})");
                Assert.True(IsInside(h.Status, HitAt(h, new Point(20, bounds.Bottom + statusHeight / 2))), "状態の行の点が、状態の行に当たらない");

                h.Results.CloseDetail();
                h.Layout();
                Assert.Equal(Visibility.Collapsed, layer.Visibility);
                Assert.False(IsInside(layer, HitAt(h, new Point(50, 100))), "閉じたあとも、左の列の上の点が、詳細画面に当たる");
            }
        });
    }

    // ---------------------------------------------------------------- 赤い帯

    [Fact]
    public async Task The_red_banner_is_drawn_in_front_of_the_detail_while_it_is_open()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            MainLayoutHarness h = MainLayoutHarness.Create(1180, 760);
            await h.Results.OpenDetailAsync(MainLayoutHarness.Hash(0));
            h.Results.ShowProblem("元のファイルが無いので、コピーできませんでした");
            h.Layout();

            Assert.True(h.Results.BannerVisible);
            Assert.True(h.Results.DetailBannerVisible);
            Assert.Equal(h.Results.BannerText, h.Results.DetailBannerText);

            // 層の中の順: 詳細画面 < 帯
            Border banner = h.Results.DetailLayer.Children.OfType<Border>().Single();
            Assert.True(h.Results.DetailLayer.Children.IndexOf(h.Results.DetailViewForTests) < h.Results.DetailLayer.Children.IndexOf(banner));

            // 画面に出る色: 帯のところの 1 点を描いて、帯の色(詳細画面の背景ではなく)であること
            Rect b = h.BoundsOf(banner);
            Assert.True(b.Width > 100 && b.Height > 20, "帯に大きさが無い");
            var bmp = new RenderTargetBitmap((int)h.ClientWidth, (int)h.ClientHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(h.Root);
            var pixel = new byte[4];
            bmp.CopyPixels(new Int32Rect((int)Math.Floor(b.Left) + 6, (int)Math.Floor(b.Top + b.Height / 2), 1, 1), pixel, 4, 0);
            Color expected = ((SolidColorBrush)AppColors.Warn).Color;
            Assert.Equal((expected.B, expected.G, expected.R), (pixel[0], pixel[1], pixel[2]));
        });
    }

    [Fact]
    public async Task The_banner_is_the_same_in_both_places_and_the_detail_layer_stays_hidden_when_it_is_closed()
    {
        await StaDispatcher.RunAsync(() =>
        {
            MainLayoutHarness h = MainLayoutHarness.Create(1180, 760);
            Assert.False(h.Results.BannerVisible);
            Assert.False(h.Results.DetailBannerVisible);

            h.Results.ShowProblem("ドラッグできません");
            Assert.True(h.Results.BannerVisible);
            Assert.True(h.Results.DetailBannerVisible);
            Assert.Equal("ドラッグできません", h.Results.BannerText);
            Assert.Equal("ドラッグできません", h.Results.DetailBannerText);
            Assert.Equal(Visibility.Collapsed, h.Results.DetailLayer.Visibility);   // 詳細画面が閉じていれば、層ごと隠れていて、帯は一覧の側だけに見える
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 一覧は動かない

    [Fact]
    public async Task Opening_and_closing_the_detail_leaves_the_list_size_column_count_and_position_unchanged()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            foreach ((double w, double hgt) in Sizes)
            {
                MainLayoutHarness h = MainLayoutHarness.Create(w, hgt, resultCount: 90);
                ThumbnailGridControl grid = h.Results.ResultGrid;
                grid.Offset = 500;   // 途中まで送っておく(閉じたあと、同じ位置に戻ること)
                h.Layout();

                (int Columns, double Offset, Rect Bounds) Snapshot() => (grid.Columns, grid.Offset, h.BoundsOf(grid));
                var before = Snapshot();
                Assert.True(before.Offset > 0, "試験の前提: 一覧が送られていない");
                Assert.True(before.Columns >= 2);

                await h.Results.OpenDetailAsync(MainLayoutHarness.Hash(40));
                h.Layout();
                Assert.Equal(before, Snapshot());

                h.Results.CloseDetail();
                h.Layout();
                Assert.Equal(before, Snapshot());
            }
        });
    }

    // ---------------------------------------------------------------- キーボード

    [Fact]
    public async Task The_detail_keeps_tab_ctrl_tab_and_arrow_focus_inside_itself()
    {
        await StaDispatcher.RunAsync(() =>
        {
            // 詳細画面は窓の全面を覆う。フォーカスが外へ出ると、見えない左の列・検索ボックス・一覧を操作してしまう(「一時停止」を含む)
            DetailView view = MainLayoutHarness.Create(1180, 760).Results.DetailViewForTests;
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(view));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetControlTabNavigation(view));
            Assert.Equal(KeyboardNavigationMode.Contained, KeyboardNavigation.GetDirectionalNavigation(view));
            return Task.CompletedTask;
        });
    }
}
