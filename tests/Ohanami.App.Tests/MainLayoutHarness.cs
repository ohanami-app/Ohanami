using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ohanami.Core;

namespace Ohanami.App.Tests;

/// <summary>
/// 窓の中身(<see cref="MainLayout"/>。MainWindow が使うものと同じ組み立て)を、窓を作らずに、外側の大きさを指定して置く。
/// 寸法の試験(MinWindowSizeTests)と、詳細画面を窓の全面に重ねる試験(DetailOverlayTests)が共有する。
/// 外側の大きさ − 枠(<see cref="WindowLayout.ChromeWidth"/>・<see cref="WindowLayout.ChromeHeight"/>)= 中の大きさ。
/// </summary>
internal sealed class MainLayoutHarness
{
    public required IndexPanel Index { get; init; }
    public required ResultsPanel Results { get; init; }
    public required StatusBarView Status { get; init; }
    public required Grid Root { get; init; }
    public required double ClientWidth { get; init; }
    public required double ClientHeight { get; init; }

    /// <summary>検索の結果に入れる画像 <paramref name="i"/> 番目(0 始まり)のハッシュ(合成。64 桁)。</summary>
    public static string Hash(int i) => i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>外側が <paramref name="outerWidth"/>×<paramref name="outerHeight"/> の窓の中身を組み、配置する(一覧には <paramref name="resultCount"/> 枚の結果が入っている)。</summary>
    public static MainLayoutHarness Create(double outerWidth, double outerHeight, int resultCount = 3)
    {
        var results = new ResultsPanel(new TestLog(), _ => { }, new FakeShell())
        {
            DetailFunc = (_, _) => Task.FromResult<ImageDetail?>(null),
            ImageFunc = (_, _) => Task.FromResult(ThumbnailResult.Ok(new ThumbnailPixels(4, 2, new byte[4 * 2 * 4]), fromCache: false)),
            DisplayZone = TimeZoneInfo.Utc,
        };
        results.SetResults([.. Enumerable.Range(0, resultCount).Select(i => new SearchResult(Hash(i), [$@"C:\no-such-dir\{i}.png"], i + 1, 0.01, []))]);
        var index = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
        var status = new StatusBarView();
        status.ShowCoreStatus(new CoreStatus(CorePhase.Ready, null, true, 1030));
        Grid root = MainLayout.Build(index, results, status);
        TestLook.Setup(root);
        var h = new MainLayoutHarness
        {
            Index = index, Results = results, Status = status, Root = root,
            ClientWidth = outerWidth - WindowLayout.ChromeWidth,
            ClientHeight = outerHeight - WindowLayout.ChromeHeight,
        };
        h.Layout();
        results.ResultGrid.SetScale(1.0);   // 列数は、置いた大きさが決まってから、SetScale で数え直す(GridControlTests と同じ)
        h.Layout();
        return h;
    }

    /// <summary>配置し直す(開く・閉じる・内容を替えたあとに呼ぶ)。</summary>
    public void Layout()
    {
        Root.Measure(new Size(ClientWidth, ClientHeight));
        Root.Arrange(new Rect(0, 0, ClientWidth, ClientHeight));
        Root.UpdateLayout();
    }

    /// <summary>詳細画面の画像の欄(暗い四角)。</summary>
    public Border ImageHost() => VisualWalk(Results.DetailViewForTests).OfType<Border>().First(b => ReferenceEquals(b.Background, AppColors.ImageHost));

    /// <summary><paramref name="element"/> の、窓の中(根)での位置と大きさ。</summary>
    public Rect BoundsOf(FrameworkElement element) => new(element.TranslatePoint(new Point(0, 0), Root), new Size(element.ActualWidth, element.ActualHeight));

    public static IEnumerable<DependencyObject> VisualWalk(DependencyObject root)
    {
        yield return root;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (DependencyObject x in VisualWalk(VisualTreeHelper.GetChild(root, i))) yield return x;
    }
}
