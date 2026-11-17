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
/// 窓の最小の大きさ(<see cref="WindowLayout.MinWindowWidth"/>×<see cref="WindowLayout.MinWindowHeight"/>)で、見えるべきものが見えること。
/// 最小の幅は、画像が最低限見える幅にする(ADR 0039)。「最低限」= 詳細画面の画像に使える幅が、一覧のサムネイルの幅(256px)以上
/// (画像の見え方が一覧より小さくならない)。同時に、「フォルダ一覧が 2 行以上見える」も、最小の窓の大きさで固定する。
/// 窓は表示せず、MainWindow と同じ割り付け(左 300 固定・右は残り)で測る(論理単位 = DPI 96 のピクセル)。
/// </summary>
public sealed class MinWindowSizeTests
{
    /// <summary>MainWindow と同じフォントと、アプリと同じ Fluent の皮(TestLook)。</summary>
    private static void FontSetup(FrameworkElement e) => TestLook.Setup(e);

    /// <summary>外側の幅から、一覧(右パネル)の本体と同じ幅(= 詳細画面の幅)を出す: 外側 − 枠 − 左の列 − 右パネルの余白。</summary>
    private static double BodyWidth(double windowWidth) => windowWidth - WindowLayout.ChromeWidth - WindowLayout.LeftColumnWidth - WindowLayout.RightPanelMarginWidth;

    /// <summary>詳細画面(本物の <see cref="DetailView"/>)を、その幅で配置し、画像に使える幅(画像の欄 − 画像の余白)を測る。</summary>
    private static (double HostWidth, double ImageWidth) MeasureDetailImage(double bodyWidth)
    {
        var view = new DetailView { Visibility = Visibility.Visible };
        FontSetup(view);
        view.Measure(new Size(bodyWidth, 600));
        view.Arrange(new Rect(0, 0, bodyWidth, 600));
        view.UpdateLayout();
        Border host = VisualWalk(view).OfType<Border>().First(b => b.Background is SolidColorBrush c && c.Color == Color.FromRgb(0x1E, 0x1E, 0x1E));
        return (host.ActualWidth, Math.Max(0, host.ActualWidth - WindowLayout.DetailImagePadding));
    }

    [Fact]
    public async Task At_the_minimum_window_width_the_detail_image_is_at_least_as_wide_as_a_list_thumbnail_and_the_minimum_is_tight()
    {
        await StaDispatcher.RunAsync(() =>
        {
            double atMin = MeasureDetailImage(BodyWidth(WindowLayout.MinWindowWidth)).ImageWidth;
            Assert.True(atMin >= WindowLayout.ThumbnailWidth, $"最小の幅 {WindowLayout.MinWindowWidth} で、詳細画面の画像に使える幅 {atMin} が、一覧のサムネイルの幅 {WindowLayout.ThumbnailWidth} に足りない");

            // 最小の幅は、ぎりぎり(1px 狭いと足りない)。必要以上に上げていない
            double oneNarrower = MeasureDetailImage(BodyWidth(WindowLayout.MinWindowWidth - 1)).ImageWidth;
            Assert.True(oneNarrower < WindowLayout.ThumbnailWidth, $"最小の幅より 1px 狭くても、画像に使える幅 {oneNarrower} が足りている(最小の幅が大きすぎる)");

            // 既定の窓(1180)でも、画像は一覧のサムネイルより大きい
            Assert.True(MeasureDetailImage(BodyWidth(1180)).ImageWidth > WindowLayout.ThumbnailWidth);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task At_the_minimum_window_size_the_detail_image_and_two_rows_of_the_folder_list_are_both_visible()
    {
        await StaDispatcher.RunAsync(() =>
        {
            // 幅: 詳細の画像
            Assert.True(MeasureDetailImage(BodyWidth(WindowLayout.MinWindowWidth)).ImageWidth >= WindowLayout.ThumbnailWidth);

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
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void The_minimum_width_is_the_sum_of_its_parts()
    {
        // 最小の幅 = 枠 + 左の列 + 右パネルの余白 + 詳細の右の欄 + 詳細の内側の余白 + 画像の余白 + サムネイルの幅(定数どうしの整合。どれかを替えたら、最小の幅も替える)
        double sum = WindowLayout.ChromeWidth + WindowLayout.LeftColumnWidth + WindowLayout.RightPanelMarginWidth + WindowLayout.DetailSideWidth + WindowLayout.DetailInnerMarginWidth + WindowLayout.DetailImagePadding + WindowLayout.ThumbnailWidth;
        Assert.Equal(WindowLayout.MinWindowWidth, sum);
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

    private static System.Collections.Generic.IEnumerable<DependencyObject> VisualWalk(DependencyObject root)
    {
        yield return root;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (DependencyObject x in VisualWalk(VisualTreeHelper.GetChild(root, i))) yield return x;
    }

    // ---------------------------------------------------------------- 測定用(最小の窓の見た目の画像)

    [Fact(Skip = "測定用(最小の幅)。手元で Skip を外して回すと、%TEMP% の min-window-shots に、最小の窓と既定の窓の外観(左パネル + 詳細画面。画像の欄は暗い四角)の PNG と、数字(numbers.txt)を書く。通常の回帰テストには入れない")]
    public async Task Render_the_minimum_and_default_window_with_the_detail_view_offscreen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "min-window-shots");
        Directory.CreateDirectory(dir);
        var numbers = new System.Text.StringBuilder();
        await StaDispatcher.RunAsync(() =>
        {
            foreach ((string name, double w, double h) in new[] { ("min", WindowLayout.MinWindowWidth, WindowLayout.MinWindowHeight), ("default", 1180.0, 760.0) })
            {
                double clientW = w - WindowLayout.ChromeWidth, clientH = h - WindowLayout.ChromeHeight;
                var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
                var detail = new DetailView { Visibility = Visibility.Visible, Margin = new Thickness(6, 12, 12, 12) };
                var status = new StatusBarView();
                status.ShowCoreStatus(new Ohanami.Core.CoreStatus(Ohanami.Core.CorePhase.Ready, null, true, 1030));
                var main = new Grid();
                main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WindowLayout.LeftColumnWidth) });
                main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(detail, 1);
                main.Children.Add(panel);
                main.Children.Add(detail);
                var root = new Grid { Background = Brushes.White };
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(status, 1);
                root.Children.Add(main);
                root.Children.Add(status);
                FontSetup(root);
                root.Measure(new Size(clientW, clientH));
                root.Arrange(new Rect(0, 0, clientW, clientH));
                root.UpdateLayout();
                Border host = VisualWalk(detail).OfType<Border>().First(b => b.Background is SolidColorBrush c && c.Color == Color.FromRgb(0x1E, 0x1E, 0x1E));
                numbers.AppendLine($"{name}: 外側 {w}×{h} / 中 {clientW}×{clientH} / 画像の欄 {host.ActualWidth} / 画像に使える幅 {host.ActualWidth - WindowLayout.DetailImagePadding} / 一覧の列数 {ColumnsAt(w)}");
                var bmp = new RenderTargetBitmap((int)clientW, (int)clientH, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using FileStream fs = File.Create(Path.Combine(dir, name + ".png"));
                enc.Save(fs);
            }
            return Task.CompletedTask;
        });
        File.WriteAllText(Path.Combine(dir, "numbers.txt"), numbers.ToString(), new System.Text.UTF8Encoding(false));
        Assert.True(File.Exists(Path.Combine(dir, "min.png")));
    }
}
