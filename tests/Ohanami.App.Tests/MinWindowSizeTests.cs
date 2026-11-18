using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 窓の最小の大きさ(<see cref="WindowLayout.MinWindowWidth"/>×<see cref="WindowLayout.MinWindowHeight"/>)で、見えるべきものが見えること(ADR 0039・0050)。
/// 詳細画面は状態の行を除く窓の全面に重なるので、画像に使える幅は「外側 − 枠 − 右の欄 − 内側の余白 − 画像の余白」で決まる。最小の幅でも、一覧のサムネイルの幅(256px)以上であること。
/// 同時に、「フォルダ一覧が 2 行以上見える」「一覧が最小の幅で 2 列・既定の幅で 3 列」を、最小の窓の大きさで固定する。
/// 窓は表示せず、MainWindow と同じ組み立て(<see cref="MainLayout"/>)で測る(論理単位 = DPI 96 のピクセル)。
/// </summary>
public sealed class MinWindowSizeTests
{
    /// <summary>MainWindow と同じフォントと、アプリと同じ Fluent の皮(TestLook)。</summary>
    private static void FontSetup(FrameworkElement e) => TestLook.Setup(e);

    /// <summary>外側の幅から、一覧(右パネル)の本体の幅を出す: 外側 − 枠 − 左の列 − 右パネルの余白。</summary>
    private static double BodyWidth(double windowWidth) => windowWidth - WindowLayout.ChromeWidth - WindowLayout.LeftColumnWidth - WindowLayout.RightPanelMarginWidth;

    /// <summary>外側の幅で詳細画面を開いたとき、画像に使える幅(画像の欄 − 画像の余白)を、本物の組み立てで測る。</summary>
    private static async Task<double> MeasureDetailImageWidthAsync(double outerWidth, double outerHeight)
    {
        MainLayoutHarness h = MainLayoutHarness.Create(outerWidth, outerHeight);
        await h.Results.OpenDetailAsync(MainLayoutHarness.Hash(0));
        h.Layout();
        return Math.Max(0, h.ImageHost().ActualWidth - WindowLayout.DetailImagePadding);
    }

    /// <summary>外側の幅から決まる、画像に使える幅(詳細画面の幅 = 外側 − 枠。そこから右の欄・内側の余白・画像の余白を引く)。</summary>
    private static double ExpectedImageWidth(double outerWidth) =>
        outerWidth - WindowLayout.ChromeWidth - WindowLayout.DetailSideWidth - WindowLayout.DetailInnerMarginWidth - WindowLayout.DetailImagePadding;

    [Fact]
    public async Task At_the_minimum_window_width_the_detail_image_is_at_least_as_wide_as_a_list_thumbnail()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            double atMin = await MeasureDetailImageWidthAsync(WindowLayout.MinWindowWidth, WindowLayout.MinWindowHeight);
            Assert.True(atMin >= WindowLayout.ThumbnailWidth, $"最小の幅 {WindowLayout.MinWindowWidth} で、詳細画面の画像に使える幅 {atMin} が、一覧のサムネイルの幅 {WindowLayout.ThumbnailWidth} に足りない");
        });
    }

    [Fact]
    public async Task The_detail_image_width_follows_from_the_outer_width_minus_the_frame()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            foreach ((double w, double h) in new[] { (WindowLayout.MinWindowWidth, WindowLayout.MinWindowHeight), (1180.0, 760.0), (1920.0, 1080.0) })
                Assert.Equal(ExpectedImageWidth(w), await MeasureDetailImageWidthAsync(w, h));
        });
    }

    [Fact]
    public async Task At_the_minimum_window_size_the_detail_image_and_two_rows_of_the_folder_list_are_both_visible()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            // 幅: 詳細の画像
            Assert.True(await MeasureDetailImageWidthAsync(WindowLayout.MinWindowWidth, WindowLayout.MinWindowHeight) >= WindowLayout.ThumbnailWidth);

            // 高さ: フォルダ一覧が、1 行 3 行分(長いパスが 2 行に折り返す行 + 枚数)の見本の 2 行以上
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
            double usableHeight = WindowLayout.MinWindowHeight - WindowLayout.ChromeHeight - 28;      // 28 = 状態のバーの高さ(1 行)
            var root = new Grid { Width = WindowLayout.LeftColumnWidth, Height = usableHeight };
            root.Children.Add(panel);
            FontSetup(root);
            ListBox list = LogicalTreeHelper.GetChildren(panel).OfType<ListBox>().First();
            var sample = new ListBoxItem { Padding = UiParts.ListRowPadding, Content = new TextBlock { Text = "synthetic-path-first-line\nsynthetic-path-second-line\n24 枚", TextWrapping = TextWrapping.Wrap } };
            list.Items.Add(sample);
            root.Measure(new Size(root.Width, usableHeight));
            root.Arrange(new Rect(0, 0, root.Width, usableHeight));
            root.UpdateLayout();
            double visible = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(list).Height - list.BorderThickness.Top - list.BorderThickness.Bottom;   // 枠の内側(行が見える高さ。一覧に 1px の枠がある)
            Assert.True(visible >= 2 * sample.ActualHeight, $"最小の窓で、フォルダ一覧の見える高さ {visible}px が、2 行分({2 * sample.ActualHeight}px)に足りない");
        });
    }

    [Fact]
    public async Task The_thumbnail_list_has_two_columns_at_the_minimum_width_and_three_at_the_default()
    {
        await StaDispatcher.RunAsync(() =>
        {
            Assert.Equal(2, ColumnsAt(WindowLayout.MinWindowWidth));        // 最小の幅: (708 − 余白 8) ÷ (256 + 8) = 2.65 → 2 列
            Assert.Equal(3, ColumnsAt(1180));                                // 既定の幅: (846 − 8) ÷ 264 = 3.17 → 3 列
            return Task.CompletedTask;
        });
    }

    /// <summary>その外側の幅で、一覧(本物の <see cref="ThumbnailGridControl"/>)に並ぶ列数。</summary>
    internal static int ColumnsAt(double windowWidth)
    {
        double w = BodyWidth(windowWidth);
        var grid = new ThumbnailGridControl(WindowLayout.ThumbnailWidth, 144, 8, prefetchScreens: 2, pathsOf: _ => []);
        grid.Measure(new Size(w, 400));
        grid.Arrange(new Rect(0, 0, w, 400));
        grid.SetScale(1.0);          // 列数は、置いた大きさが決まってから、SetScale で数え直す(GridControlTests と同じ)
        return grid.Columns;
    }

    // ---------------------------------------------------------------- 測定用(最小の窓の見た目の画像)

    [Fact(Skip = "測定用(最小の幅)。手元で Skip を外して回すと、%TEMP% の min-window-shots に、最小・既定・1920×1080 の窓の外観(詳細画面を開いたところ。画像の欄は暗い四角)の PNG と、数字(numbers.txt)を書く。通常の回帰テストには入れない")]
    public async Task Render_the_minimum_and_default_window_with_the_detail_view_offscreen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "min-window-shots");
        Directory.CreateDirectory(dir);
        var numbers = new System.Text.StringBuilder();
        await StaDispatcher.RunAsync(async () =>
        {
            foreach ((string name, double w, double h) in new[] { ("min", WindowLayout.MinWindowWidth, WindowLayout.MinWindowHeight), ("default", 1180.0, 760.0), ("fullhd", 1920.0, 1080.0) })
            {
                MainLayoutHarness layout = MainLayoutHarness.Create(w, h);
                layout.Root.Background = Brushes.White;
                await layout.Results.OpenDetailAsync(MainLayoutHarness.Hash(0));
                layout.Layout();
                Border host = layout.ImageHost();
                numbers.AppendLine($"{name}: 外側 {w}×{h} / 中 {layout.ClientWidth}×{layout.ClientHeight} / 画像の欄 {host.ActualWidth}×{host.ActualHeight} / 画像に使える幅 {host.ActualWidth - WindowLayout.DetailImagePadding} / 詳細画面の層 {layout.BoundsOf(layout.Results.DetailLayer)} / 一覧の欄(詳細を開く前の画像の欄の位置) {layout.BoundsOf(layout.Results.ResultGrid)} / 一覧の列数 {ColumnsAt(w)}");
                var bmp = new RenderTargetBitmap((int)layout.ClientWidth, (int)layout.ClientHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(layout.Root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using FileStream fs = File.Create(Path.Combine(dir, name + ".png"));
                enc.Save(fs);
            }
        });
        File.WriteAllText(Path.Combine(dir, "numbers.txt"), numbers.ToString(), new System.Text.UTF8Encoding(false));
        Assert.True(File.Exists(Path.Combine(dir, "min.png")));
    }
}
