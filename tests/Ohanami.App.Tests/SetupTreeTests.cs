using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 一括の領域の下に字下げして並べた区画と、その左の木の線(<see cref="TreeRail"/>)の決まり。
/// 線は、区画の見出しの実際の位置から描き、区画の高さが変わる場面(取り終えて領域が空になる・片方だけ残った窓・スクロールが出る高さ)でも、ずれない。
/// </summary>
public sealed class SetupTreeTests
{
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject inner in Descendants(child)) yield return inner;
        }
    }

    /// <summary>窓の中身を、幅 820 の枠に置いて配置する(<paramref name="height"/> が null なら、中身の高さまで。あれば、その高さでスクロールさせて、<paramref name="offset"/> だけ下げる)。</summary>
    private static (FrameworkElement Host, Action Restore) Layout(SetupWindow w, double? height = null, double offset = 0)
    {
        var root = (UIElement)w.Content;
        w.Content = null;
        var host = new Border { Child = root, Width = 820 };
        if (height is { } h) host.Height = h;
        TestLook.Setup(host);
        host.Measure(new Size(820, height ?? double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, 820, height ?? Math.Ceiling(host.DesiredSize.Height)));
        host.UpdateLayout();
        if (offset != 0)
        {
            ((ScrollViewer)root).ScrollToVerticalOffset(offset);
            host.UpdateLayout();
        }
        return (host, () => { host.Child = null; w.Content = root; });
    }

    /// <summary>線を描く部品と、描いた高さ・いまの見出しの高さ・区画の上下の縁(部品の上の縁から)を調べて、描いた高さがいまの高さと同じで、各見出しが自分の区画の中にあることを確かめる。</summary>
    private static TreeRail AssertRailFollowsHeaders(FrameworkElement host)
    {
        TreeRail rail = Descendants(host).OfType<TreeRail>().Single();
        double[] now = rail.HeaderCentersForTests();
        Assert.Equal(2, now.Length);
        Assert.Equal(now, rail.DrawnCentersForTests);   // 描いたあとで区画の位置が替わったのに、描き直していない、ということが無い
        Assert.True(now[0] > 0 && now[1] > now[0]);
        var sections = Descendants(rail.Parent).OfType<Border>().Where(b => b.Parent == rail.Parent).ToArray();
        Assert.Equal(2, sections.Length);
        for (int i = 0; i < 2; i++)
        {
            double top = sections[i].TranslatePoint(new Point(0, 0), rail).Y;
            Assert.InRange(now[i], top, top + sections[i].ActualHeight);
            Assert.InRange(now[i], top, top + 40);   // 見出しは、区画の一番上の段(枠と余白のすぐ下)
        }
        return rail;
    }

    [Fact]
    public async Task Both_sections_are_indented_under_the_bulk_area_with_the_lines_following_the_headers()
    {
        await StaDispatcher.RunAsync(() =>
        {
            foreach (Func<SetupState> state in new Func<SetupState>[] { SetupWindowTests.BothMissing, SetupWindowTests.VlmOnlyMissing, SetupWindowTests.EmbeddingOnlyMissing, SetupWindowTests.WithMismatch })
            {
                SetupWindow w = SetupWindowTests.NewWindow(state(), downloads: new FakeDownloads());
                (FrameworkElement host, Action restore) = Layout(w);
                TreeRail rail = AssertRailFollowsHeaders(host);
                Assert.Equal(TreeRail.Indent, Descendants(rail.Parent).OfType<Border>().First(b => b.Parent == rail.Parent).TranslatePoint(new Point(0, 0), rail).X);   // 区画は、線の幅だけ右にずれている
                restore();
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_lines_are_not_drawn_when_no_bulk_area_is_there()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.AllReady(), downloads: new FakeDownloads());
            (FrameworkElement host, Action restore) = Layout(w);
            Assert.Empty(Descendants(host).OfType<TreeRail>());
            restore();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_lines_follow_the_headers_when_a_section_area_is_cleared_after_it_is_done()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);
            (FrameworkElement host, Action restore) = Layout(w);
            AssertRailFollowsHeaders(host);

            for (int i = 0; i < SetupDownloadBothTests.EmbeddingFiles.Length; i++)
                fake.Report(SetupDownloadBothTests.P(ModelDownloadPhase.Completed, SetupDownloadBothTests.EmbeddingFiles[i], i, 4_579_405_088, 4_579_405_088));
            host.UpdateLayout();
            AssertRailFollowsHeaders(host);   // 取り終えた区画の領域が空になっても、線は見出しの高さのまま

            fake.Fail(SetupDownloadBothTests.Fail(ModelDownloadFailure.Network, SetupDownloadBothTests.EmbeddingFiles[0]));
            await run;
            host.UpdateLayout();
            AssertRailFollowsHeaders(host);
            restore();
        });
    }

    [Fact]
    public async Task The_lines_follow_the_headers_when_the_content_scrolls()
    {
        await StaDispatcher.RunAsync(() =>
        {
            foreach (double offset in new[] { 0, 120, 300 })
            {
                SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads());
                (FrameworkElement host, Action restore) = Layout(w, height: 600, offset: offset);
                AssertRailFollowsHeaders(host);
                restore();
            }
            return Task.CompletedTask;
        });
    }
}
