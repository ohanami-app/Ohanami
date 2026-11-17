using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 画面は、長い文を省略記号で切る(状態バーの左の列)。起動の失敗の文は、すべきことを先に言うので、最も狭い窓でも、その部分が切れずに見えることを、
/// 実際の部品の幅で確かめる(部品を実機と同じフォント・皮で、窓と同じ割り付けに置き、先頭の部分の幅が、左の列の幅に収まるかを測る)。
/// 左パネルの補足の 1 行(幅 約 264)は、失敗の理由の頭の「…を準備できませんでした: 」(幅 約 190〜261)を外して、理由の文から始める。理由の最初の文が、その行に収まることを確かめる。
/// </summary>
public sealed class NativeFailureVisibilityTests
{
    /// <summary>起動できなかったときの、すべきことの部分(文の先頭。<c>NativeFailure.FilesMissingMessage</c> の括弧の前)。</summary>
    private const string FilesAction = "アプリのファイルが足りないか、壊れています。zip を展開し直してください";

    private static double TextWidth(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap };
        var host = new Grid();
        host.Children.Add(tb);
        TestLook.Setup(host);
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize.Width;
    }

    [Fact]
    public async Task The_action_of_a_startup_failure_stays_visible_in_the_status_bar_at_the_minimum_window_width()
    {
        await StaDispatcher.RunAsync(() =>
        {
            Thickness frame = SystemParameters.WindowNonClientFrameThickness;
            double clientWidth = WindowLayout.MinWindowWidth - frame.Left - frame.Right;
            string reason = FilesAction + " (onnxruntime.dll を読み込めませんでした。足りないファイル: msvcp140.dll、msvcp140_1.dll、msvcp140_atomic_wait.dll、vcruntime140.dll、vcruntime140_1.dll)";

            var status = new StatusBarView();
            status.ShowCoreStatus(new CoreStatus(CorePhase.Failed, reason, false, 0));
            var host = new Grid { Width = clientWidth };
            host.Children.Add(status);
            TestLook.Setup(host);
            host.Measure(new Size(clientWidth, 100));
            host.Arrange(new Rect(0, 0, clientWidth, 100));
            host.UpdateLayout();

            TextBlock core = Walk(status).OfType<TextBlock>().First();
            Assert.StartsWith("起動できませんでした: " + FilesAction, core.Text, StringComparison.Ordinal);
            double needed = TextWidth("起動できませんでした: " + FilesAction);
            Assert.True(needed <= core.ActualWidth, $"すべきことの部分が、状態バーの左の列に収まらない: 必要 {needed:0}、列 {core.ActualWidth:0}");
            Assert.Equal(core.Text, core.ToolTip);   // 全文は、ヒントで読める
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("NVIDIA の GPU を使えません。", "NVIDIA の GPU を使えません。NVIDIA のドライバと GPU を確かめてください (CUDA を初期化できませんでした)")]
    [InlineData("アプリのファイルが足りないか、壊れています。", "アプリのファイルが足りないか、壊れています。zip を展開し直してください (onnxruntime.dll を読み込めませんでした)")]
    public async Task The_first_sentence_of_a_preparation_failure_is_visible_in_the_left_panel_line(string firstSentence, string reason)
    {
        await StaDispatcher.RunAsync(() =>
        {
            foreach (JobKind? job in new JobKind?[] { JobKind.Embed, JobKind.Vlm, null })
            {
                var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
                var state = new IndexState(IndexRunState.Failed, job, [], new EngineStatus(EnginePhase.Failed, reason, null), null, null, null, null, reason, 0);
                panel.ShowForTests(state, null);
                var host = new Grid { Width = WindowLayout.LeftColumnWidth, Height = 700 };
                host.Children.Add(panel);
                TestLook.Setup(host);
                host.Measure(new Size(WindowLayout.LeftColumnWidth, 700));
                host.Arrange(new Rect(0, 0, WindowLayout.LeftColumnWidth, 700));
                host.UpdateLayout();

                TextBlock note = Walk(panel).OfType<IndexRowsView>().First().Rows[6];
                Assert.StartsWith(firstSentence, note.Text, StringComparison.Ordinal);   // 頭の「…を準備できませんでした: 」が無く、理由から始まる
                double needed = SecondaryWidth(firstSentence);
                Assert.True(needed <= note.ActualWidth, $"理由の最初の文が、左パネルの 1 行に収まらない: 必要 {needed:0}、行 {note.ActualWidth:0}");
                Assert.Contains(ProcessingWords.PrepareFailedPrefix(job) + reason, note.ToolTip as string ?? "", StringComparison.Ordinal);   // ヒントは、全文(段階の名前つき)
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>補足の行と同じ大きさ(12)の文字の幅。</summary>
    private static double SecondaryWidth(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        var host = new Grid();
        host.Children.Add(tb);
        TestLook.Setup(host);
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return tb.DesiredSize.Width;
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d)
                foreach (DependencyObject x in Walk(d)) yield return x;
    }
}
