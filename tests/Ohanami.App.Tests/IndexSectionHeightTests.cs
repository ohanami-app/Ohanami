using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 左パネルの「インデックス」欄の高さが、**どの状態でも同じ**であること(ADR 0039。進み具合の文の行数が状態で変わり、上のフォルダ一覧が伸び縮みしていた)。
/// 状態は、LeftPanelMeasureTests で測るものの全部と、ほかの実行の状態。窓は表示せず、MainWindow と同じ割り付け(左 300 固定)で測る。
/// 固定した高さ・行の中身・「切れずに読める」件数の行を、テストで固める。画像(状態ごとの見た目)は、%TEMP% の index-section-shots に書く(通常は Skip の測定用)。
/// </summary>
public sealed class IndexSectionHeightTests
{
    private static IndexState State(
        IndexRunState run, JobKind? job, IReadOnlyList<StageSummary> stages, EngineStatus? engine = null, ScanProgress? scan = null,
        string? path = null, double? ips = null, TimeSpan? remaining = null, string? error = null, int skipped = 0) =>
        new(run, job, stages, engine ?? EngineStatus.NotCreated, scan, path, ips, remaining, error, skipped);

    private static StageSummary Stg(Stage stage, int pending = 0, int done = 0, int failed = 0, int outdated = 0) => new(stage, pending, done, failed, outdated);

    private static IReadOnlyList<StageSummary> Done(int n) => [Stg(Stage.Embed, done: n), Stg(Stage.Ocr, done: n), Stg(Stage.Caption, done: n)];

    private static IReadOnlyList<StageSummary> Mixed(int embedDone, int vlmDone, int pending, int failed, int outdated) =>
        [Stg(Stage.Embed, pending: pending, done: embedDone, failed: failed, outdated: outdated), Stg(Stage.Ocr, pending: pending, done: vlmDone, failed: failed, outdated: outdated), Stg(Stage.Caption, pending: pending, done: vlmDone, failed: failed, outdated: outdated)];

    private static readonly string LongName = "Screenshot_2026-11-10_synthetic-long-file-name-for-layout-measurement_0000012345678.png";
    private static readonly string LongReasonA = "モデルのファイルが無い: ViT-SO400M-16-SigLIP2-384__ohanami/model.onnx(探した場所: C:\\Users\\synthetic\\AppData\\Local\\Ohanami\\models\\ViT-SO400M-16-SigLIP2-384__ohanami\\model.onnx)。期待サイズ 1234567890 バイト。";
    private static readonly string LongReasonB = "llama-server が 3 枚続けて異常になった。GPU が他で使われているか、ドライバの問題かもしれない(場所: C:\\Users\\synthetic\\AppData\\Local\\Ohanami\\runtime\\llama.cpp\\b12110)。";

    /// <summary>テキスト検索のモデルのファイルが無いことを、Core が伝えている形(足りないファイルの名前は作った名前)。</summary>
    private static IndexState NotFetched(IndexRunState run, IReadOnlyList<StageSummary> stages) =>
        State(run, null, stages) with { UnavailableJobs = [new UnavailableJob(JobKind.Vlm, ["synthetic-vlm-model.gguf", "synthetic-vlm-mmproj.gguf"])] };

    internal sealed record Scenario(string Name, IndexState? State, int? Missing);

    /// <summary>状態の全部(LeftPanelMeasureTests で測る状態 + 停止中・待機中・一時停止の途中・一時停止中・実行中(2 つの段階)・準備中・止まった・スキャン中・飛ばした・まとめが最も長い形)。最初の 1 つは、まだ状態が届いていない形。</summary>
    internal static IReadOnlyList<Scenario> Scenarios() =>
    [
        new("00 まだ状態が届いていない", null, null),
        new("01 停止中・全部済み", State(IndexRunState.Idle, null, Done(1030)), null),
        new("02 停止中・未作成あり", State(IndexRunState.Idle, null, [Stg(Stage.Embed, pending: 1030), Stg(Stage.Ocr, pending: 1030), Stg(Stage.Caption, pending: 1030)]), null),
        new("03 待機中", State(IndexRunState.Waiting, null, Done(1030)), 0),
        new("04 一時停止の途中", State(IndexRunState.Pausing, JobKind.Vlm, Mixed(1030, 230, 800, 0, 0)), 0),
        new("05 一時停止中", State(IndexRunState.Paused, JobKind.Vlm, Mixed(1030, 230, 800, 0, 0), null, null, null, null, null, null, 0), 0),
        new("06 実行中・特徴ベクトル検索・ふつう", State(IndexRunState.Running, JobKind.Embed, [Stg(Stage.Embed, pending: 200, done: 830), Stg(Stage.Ocr, pending: 1030), Stg(Stage.Caption, pending: 1030)],
            null, null, @"C:\synthetic\Screenshot_2026-11-10_123456.png", 9.5, TimeSpan.FromSeconds(21)), 0),
        new("07 実行中・テキスト検索・ふつう", State(IndexRunState.Running, JobKind.Vlm, Mixed(1030, 230, 800, 0, 0),
            null, null, @"C:\synthetic\Screenshot_2026-11-10_123456.png", 0.08, TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5)), 0),
        new("08 実行中・テキスト検索・長いファイル名と長い残り", State(IndexRunState.Running, JobKind.Vlm, Mixed(1030, 230, 800, 0, 0),
            null, null, @"C:\synthetic\" + LongName, 0.08, TimeSpan.FromHours(34) + TimeSpan.FromMinutes(30)), 0),
        new("09 準備中(特徴ベクトル検索)", State(IndexRunState.Running, JobKind.Embed, Mixed(0, 0, 1030, 0, 0), new EngineStatus(EnginePhase.Checking, null, null)), 0),
        new("10 準備中(テキスト検索)", State(IndexRunState.Running, JobKind.Vlm, Mixed(1030, 0, 1030, 0, 0), new EngineStatus(EnginePhase.Checking, null, null)), 0),
        new("11 止まった(失敗)・長い理由 2 つ", State(IndexRunState.Failed, JobKind.Vlm, [Stg(Stage.Embed, done: 1030), Stg(Stage.Ocr, pending: 1030), Stg(Stage.Caption, pending: 1030)],
            new EngineStatus(EnginePhase.Failed, LongReasonA, null), null, null, null, null, LongReasonB, 12), 0),
        new("12 止まった(失敗)・準備の理由なし・止まった原因だけ", State(IndexRunState.Failed, JobKind.Vlm, Done(1030), null, null, null, null, null, LongReasonB), 0),
        new("13 スキャン中(停止中)", State(IndexRunState.Idle, null, Done(1030), null, new ScanProgress(1, 12345, 1234, 123, 45, 0, 0, 0, 0)), 0),
        new("14 飛ばした画像あり(停止中)", State(IndexRunState.Idle, null, Done(1030), null, null, null, null, null, null, 12), 0),
        new("15 最も長い形(実行中 + スキャン + 準備中 + 長いファイル名 + 飛ばした + 4 桁の内訳)", State(IndexRunState.Running, JobKind.Vlm, Mixed(8000, 7000, 1234, 1234, 4567),
            new EngineStatus(EnginePhase.Checking, null, null), new ScanProgress(1, 12345, 1234, 123, 45, 0, 0, 0, 0), LongName, 0.08, TimeSpan.FromHours(12) + TimeSpan.FromMinutes(34), null, 12), 1234),
        new("16 まとめの行が最も長い(失敗・飛ばした・要再作成・フォルダに無い 3〜4 桁)", State(IndexRunState.Idle, null, Mixed(900, 800, 12, 1234, 1234), null, null, null, null, null, null, 2), 1234),
        new("17 4 桁の件数(全部)", State(IndexRunState.Idle, null, [Stg(Stage.Embed, pending: 1234, done: 8888, failed: 1234, outdated: 1234), Stg(Stage.Ocr, pending: 1234, done: 8888, failed: 1234, outdated: 1234), Stg(Stage.Caption, pending: 1234, done: 8888, failed: 1234, outdated: 1234)]), 0),
        new("18 テキスト検索のモデルが未取得・待機中", NotFetched(IndexRunState.Waiting, [Stg(Stage.Embed, done: 1030), Stg(Stage.Ocr, pending: 1030), Stg(Stage.Caption, pending: 1030)]), 0),
        new("19 テキスト検索のモデルが未取得・停止中・4 桁の未作成と要再作成", NotFetched(IndexRunState.Idle, [Stg(Stage.Embed, done: 8888), Stg(Stage.Ocr, pending: 1234, done: 8888, outdated: 1234), Stg(Stage.Caption, pending: 1234, done: 8888, outdated: 1234)]), 0),
    ];

    /// <summary>窓の枠の高さ(実機の測定: 上 31 + 下 8)と、状態のバーの高さ(1 行)。左パネルに使える高さ = 窓の高さ − この 2 つ。</summary>
    private const double ChromeHeight = 39;
    private const double StatusBarHeight = 28;

    /// <summary>MainWindow と同じフォントと、アプリと同じ Fluent の皮(TestLook)。</summary>
    private static void FontSetup(FrameworkElement e) => TestLook.Setup(e);

    /// <summary>MainWindow と同じ割り付け(左 300 固定の列、行の高さは <paramref name="height"/>)に置いて、配置まで済ませる。</summary>
    private static (IndexPanel Panel, Grid Root) Layout(Scenario sc, double height)
    {
        var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
        if (sc.State is not null) panel.ShowForTests(sc.State, sc.Missing);
        var root = new Grid { Width = 300, Height = height };
        root.Children.Add(panel);
        FontSetup(root);
        root.Measure(new Size(300, height));
        root.Arrange(new Rect(0, 0, 300, height));
        root.UpdateLayout();
        return (panel, root);
    }

    [Fact]
    public async Task The_index_section_has_the_same_height_in_every_state_and_the_folder_list_does_not_move()
    {
        await StaDispatcher.RunAsync(() =>
        {
            foreach (double height in new[] { 760.0 - ChromeHeight - StatusBarHeight, WindowLayout.MinWindowHeight - ChromeHeight - StatusBarHeight })      // 既定(1180×760)と最小の、左パネルに使える高さ(枠・状態のバーを引いた概算)
            {
                double? sectionHeight = null, listTop = null, listHeight = null;
                foreach (Scenario sc in Scenarios())
                {
                    (IndexPanel panel, Grid root) = Layout(sc, height);
                    double section = panel.IndexSectionForTests.ActualHeight;
                    ListBox list = Walk(panel).OfType<ListBox>().First();
                    double top = list.TranslatePoint(new Point(0, 0), root).Y;
                    sectionHeight ??= section;
                    listTop ??= top;
                    listHeight ??= list.ActualHeight;
                    Assert.True(Math.Abs(section - sectionHeight.Value) < 0.001, $"「{sc.Name}」の欄の高さ {section} が、最初の状態の {sectionHeight} と違う(窓の高さ {height})");
                    Assert.True(Math.Abs(top - listTop.Value) < 0.001, $"「{sc.Name}」で、フォルダ一覧の上端 {top} が {listTop} から動いた(窓の高さ {height})");
                    Assert.True(Math.Abs(list.ActualHeight - listHeight.Value) < 0.001, $"「{sc.Name}」で、フォルダ一覧の高さ {list.ActualHeight} が {listHeight} から変わった(窓の高さ {height})");
                }
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_state_row_is_never_cut_in_any_state_or_job()
    {
        // 状態の行は、どの状態でも 1 行に収まる(「…」が付かない)。長い説明は、行のヒントに
        await StaDispatcher.RunAsync(() =>
        {
            foreach (IndexRunState run in Enum.GetValues<IndexRunState>())
                foreach (JobKind? job in new JobKind?[] { null, JobKind.Embed, JobKind.Vlm })
                {
                    var sc = new Scenario(run + "/" + job, State(run, job, Done(1030), error: run == IndexRunState.Failed ? "理由" : null), 0);
                    (IndexPanel panel, _) = Layout(sc, 700);
                    TextBlock state = panel.IndexRowsForTests.Rows[4];
                    state.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Assert.True(state.DesiredSize.Width <= 282, $"状態の行「{state.Text}」({sc.Name})が要る幅 {state.DesiredSize.Width} が 282 を超える(切れる)");
                    Assert.False(string.IsNullOrEmpty(state.Text));
                }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void The_short_state_sentences_keep_the_long_explanation_in_the_hint()
    {
        Assert.Equal("停止中 (開始を押すまで作りません)", ProcessingWords.StateIdle);
        Assert.Equal("待機中 (新しい画像が来たら続けます)", ProcessingWords.StateWaiting);
        Assert.Equal("一時停止の途中…", ProcessingWords.StatePausing);
        Assert.Equal("一時停止中 (再開を押すまで作りません)", ProcessingWords.StatePaused);
        Assert.Equal("停止中 (失敗で止まりました)", ProcessingWords.StateFailed);
        Assert.Contains("新しい画像が来ても、開始を押すまでインデックスは作りません", ProcessingWords.StateHint(IndexRunState.Idle), StringComparison.Ordinal);
        Assert.Contains("新しい画像が来たら、続けてインデックスを作ります", ProcessingWords.StateHint(IndexRunState.Waiting), StringComparison.Ordinal);
        Assert.Contains("新しい画像が来ても、再開を押すまでインデックスは作りません", ProcessingWords.StateHint(IndexRunState.Paused), StringComparison.Ordinal);
        Assert.Null(ProcessingWords.StateHint(IndexRunState.Running));          // 実行中は、行の文字が全文
    }

    [Fact]
    public async Task At_the_minimum_window_height_the_folder_list_shows_at_least_two_rows_of_long_paths()
    {
        // 窓の最小の高さは、これが成り立つ高さに上げてある(WindowLayout.MinWindowHeight。ADR 0039)。長いパスが 2 行に折り返す行(+ 枚数の行 = 3 行。実機で 1 行 56px 前後)が、2 行以上見えること
        await StaDispatcher.RunAsync(() =>
        {
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
            double usable = WindowLayout.MinWindowHeight - ChromeHeight - StatusBarHeight;
            var root = new Grid { Width = 300, Height = usable };
            root.Children.Add(panel);
            FontSetup(root);
            root.Measure(new Size(300, usable));
            root.Arrange(new Rect(0, 0, 300, usable));
            root.UpdateLayout();
            ListBox list = Walk(panel).OfType<ListBox>().First();

            // 見本の 1 行(3 行の文字。IndexPanel のフォルダの行と同じ作り: ListBoxItem の中の折り返す TextBlock)を、一覧に入れて測る
            var sample = new ListBoxItem { Padding = UiParts.ListRowPadding, Content = new TextBlock { Text = "synthetic-path-first-line\nsynthetic-path-second-line\n24 枚", TextWrapping = TextWrapping.Wrap } };
            list.Items.Add(sample);
            root.Measure(new Size(300, usable));
            root.Arrange(new Rect(0, 0, 300, usable));
            root.UpdateLayout();
            double row = sample.ActualHeight;
            double visible = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(list).Height - list.BorderThickness.Top - list.BorderThickness.Bottom;   // 枠の内側(行が見える高さ。一覧に 1px の枠がある)
            Assert.InRange(row, 3 * 17, 3 * 20 + 8);                           // 3 行分(見本が 3 行になっている)
            Assert.True(visible >= 2 * row, $"最小の窓(高さ {WindowLayout.MinWindowHeight})で、フォルダ一覧の見える高さ {visible}px が、2 行分({2 * row}px。1 行 {row}px)に足りない");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Every_row_is_one_fixed_line_high_and_never_wraps()
    {
        await StaDispatcher.RunAsync(() =>
        {
            foreach (Scenario sc in Scenarios())
            {
                (IndexPanel panel, _) = Layout(sc, 700);
                foreach (TextBlock row in panel.IndexRowsForTests.Rows)
                {
                    Assert.True(row.ActualHeight is IndexRowsView.LineHeightPx or IndexRowsView.SecondaryLineHeightPx, $"「{sc.Name}」の行「{row.Text}」の高さ {row.ActualHeight}(固定の {IndexRowsView.LineHeightPx}・{IndexRowsView.SecondaryLineHeightPx} でない)");
                    Assert.Equal(row.Height, row.ActualHeight);
                    Assert.Equal(TextWrapping.NoWrap, row.TextWrapping);
                    Assert.Equal(TextTrimming.CharacterEllipsis, row.TextTrimming);
                }
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_count_rows_and_the_breakdown_rows_are_never_cut_even_with_four_digit_counts()
    {
        // 件数は、切れずに読めることを優先する(件数の行・内訳の行は、幅 282 に収まる。切れてよいのは、状態・速さ・補足の長い文)
        await StaDispatcher.RunAsync(() =>
        {
            foreach (Scenario sc in Scenarios())
            {
                (IndexPanel panel, _) = Layout(sc, 700);
                IReadOnlyList<TextBlock> rows = panel.IndexRowsForTests.Rows;
                for (int i = 0; i < 4; i++)
                {
                    TextBlock row = rows[i];
                    row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double available = 282 - row.Margin.Left;
                    Assert.True(row.DesiredSize.Width <= available, $"「{sc.Name}」の行「{row.Text}」が要る幅 {row.DesiredSize.Width} が、使える幅 {available} を超える(件数が切れる)");
                }
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_not_fetched_note_fits_in_one_line_without_being_cut()
    {
        await StaDispatcher.RunAsync(() =>
        {
            foreach (Scenario sc in Scenarios().Where(s => s.State is { UnavailableJobs.Count: > 0 }))
            {
                (IndexPanel panel, _) = Layout(sc, 700);
                TextBlock note = panel.IndexRowsForTests.Rows[6];
                note.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.Equal(ProcessingWords.ModelNotFetchedNote(JobKind.Vlm), note.Text);
                Assert.True(note.DesiredSize.Width <= 282, $"「{sc.Name}」の補足の行「{note.Text}」が要る幅 {note.DesiredSize.Width} が 282 を超える(切れる)");
                Assert.Contains("足りないファイル", (string)note.ToolTip, StringComparison.Ordinal);   // 全文(足りないファイル)はヒントで読める
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_rows_hold_the_same_kind_of_information_in_the_same_row_and_empty_rows_stay_empty()
    {
        await StaDispatcher.RunAsync(() =>
        {
            IReadOnlyList<Scenario> all = Scenarios();
            IReadOnlyList<string> idle = Rows(all.Single(s => s.Name.StartsWith("01", StringComparison.Ordinal)));
            Assert.Equal("特徴ベクトル検索: 1030 / 1030 枚", idle[0]);
            Assert.Equal("", idle[1]);                                    // 内訳が無ければ、空のまま
            Assert.Equal("テキスト検索: 1030 / 1030 枚", idle[2]);
            Assert.Equal("", idle[3]);
            Assert.Equal(ProcessingWords.StateIdle, idle[4]);
            Assert.Equal("", idle[5]);                                    // 速さと残りは、実行中だけ
            Assert.Equal("", idle[6]);

            IReadOnlyList<string> pending = Rows(all.Single(s => s.Name.StartsWith("02", StringComparison.Ordinal)));
            Assert.Equal("特徴ベクトル検索: 0 / 1030 枚", pending[0]);
            Assert.Equal("未作成 1030", pending[1]);                       // 内訳は、件数の下の行(同じ種類は同じ行)

            IReadOnlyList<string> running = Rows(all.Single(s => s.Name.StartsWith("08", StringComparison.Ordinal)));
            Assert.Equal(ProcessingWords.RunningLine(JobKind.Vlm), running[4]);
            Assert.Equal("0.1 枚/秒、残り約 34 時間 30 分", running[5]);
            Assert.Equal(LongName, running[6]);                           // 補足(いま処理している画像)

            // 補足の優先順: 止まった原因・準備の失敗 > 準備中 > スキャン中 > ファイル名 > 飛ばした。先頭だけを出し、全部は NoteFull(ヒント)にある
            IndexRows longest = IndexStatusText.BuildRows(all.Single(s => s.Name.StartsWith("15", StringComparison.Ordinal)).State!);
            Assert.StartsWith("テキスト検索のインデックス作成の準備中", longest.Note, StringComparison.Ordinal);   // 準備の失敗・止まった原因は無いので、準備中が先頭
            string[] full = longest.NoteFull.Split('\n');
            Assert.Equal(4, full.Length);
            Assert.StartsWith("テキスト検索のインデックス作成の準備中", full[0], StringComparison.Ordinal);
            Assert.StartsWith("スキャン中: ", full[1], StringComparison.Ordinal);
            Assert.Equal(LongName, full[2]);
            Assert.Equal(ProcessingWords.SkippedImagesLine(12), full[3]);

            IndexRows failed = IndexStatusText.BuildRows(all.Single(s => s.Name.StartsWith("11", StringComparison.Ordinal)).State!);
            Assert.StartsWith(UiText.Plain(LongReasonA), failed.Note, StringComparison.Ordinal);                             // 準備の失敗が先頭。短い行は、頭の「…を準備できませんでした: 」を外して、理由から始める
            Assert.StartsWith(ProcessingWords.PrepareFailedPrefix(JobKind.Vlm), failed.NoteFull, StringComparison.Ordinal);   // ヒントの全文は、段階の名前つき
            Assert.Contains(ProcessingWords.StopReasonPrefix, failed.NoteFull, StringComparison.Ordinal);                   // 止まった原因も、ヒントにある
            Assert.Contains(ProcessingWords.SkippedImagesLine(12), failed.NoteFull, StringComparison.Ordinal);              // 飛ばした枚数も(まとめの行にも出る)
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_row_that_is_cut_keeps_its_full_sentence_in_the_tooltip()
    {
        await StaDispatcher.RunAsync(() =>
        {
            (IndexPanel panel, _) = Layout(Scenarios().Single(s => s.Name.StartsWith("11", StringComparison.Ordinal)), 700);
            TextBlock note = panel.IndexRowsForTests.Rows[6];
            Assert.Contains(UiText.Plain(LongReasonB), (string)note.ToolTip, StringComparison.Ordinal);     // 切れても、全文はヒントで読める
            TextBlock state = panel.IndexRowsForTests.Rows[4];
            Assert.Equal(ProcessingWords.StateHint(IndexRunState.Failed), (string)state.ToolTip);   // 状態の行は短く、全文はヒント
            Assert.Null(panel.IndexRowsForTests.Rows[5].ToolTip);                                           // 空の行にヒントは出さない
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_summary_next_to_the_state_button_is_one_trimmed_line_with_the_full_text_in_the_tooltip()
    {
        await StaDispatcher.RunAsync(() =>
        {
            (IndexPanel panel, _) = Layout(Scenarios().Single(s => s.Name.StartsWith("16", StringComparison.Ordinal)), 700);
            TextBlock summary = Walk(panel).OfType<TextBlock>().First(t => t.Text == panel.SummaryText && t.Text.Length > 0);
            Assert.Equal(TextWrapping.NoWrap, summary.TextWrapping);
            Assert.Equal(TextTrimming.CharacterEllipsis, summary.TextTrimming);
            Assert.Equal(panel.SummaryText, (string)summary.ToolTip);
            Assert.Contains("フォルダに無い 1234", panel.SummaryText, StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    private static IReadOnlyList<string> Rows(Scenario sc) => IndexStatusText.BuildRows(sc.State!) is { } r
        ? [r.Stage1Count, r.Stage1Extra, r.Stage2Count, r.Stage2Extra, r.State, r.Progress, r.Note]
        : [];

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d)
                foreach (DependencyObject x in Walk(d)) yield return x;
    }

    // ---------------------------------------------------------------- 測定用(状態ごとの見た目の画像)

    [Fact(Skip = "測定用。手元で Skip を外して回すと、%TEMP% の index-section-shots に、状態ごとの左パネルの PNG と、高さの一覧(heights.txt)を書く。通常の回帰テストには入れない")]
    public async Task Render_the_left_panel_for_every_state_offscreen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "index-section-shots");
        Directory.CreateDirectory(dir);
        var heights = new System.Text.StringBuilder();
        await StaDispatcher.RunAsync(() =>
        {
            foreach (double height in new[] { 693.0, 413.0 })
            {
                heights.AppendLine($"窓の高さ(左パネルに使える高さ) {height}");
                foreach (Scenario sc in Scenarios())
                {
                    (IndexPanel panel, Grid root) = Layout(sc, height);
                    heights.AppendLine($"  {sc.Name}: 「インデックス」欄 {panel.IndexSectionForTests.ActualHeight}、一覧の上端 {Walk(panel).OfType<ListBox>().First().TranslatePoint(new Point(0, 0), root).Y}");
                    var host = new Border { Background = Brushes.White, Child = null };
                    root.Children.Remove(panel);
                    host.Child = panel;
                    var holder = new Grid { Width = 300, Height = height };
                    holder.Children.Add(host);
                    FontSetup(holder);
                    holder.Measure(new Size(300, height));
                    holder.Arrange(new Rect(0, 0, 300, height));
                    holder.UpdateLayout();
                    var bmp = new RenderTargetBitmap(300, (int)height, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(holder);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    string file = Path.Combine(dir, $"{(int)height}-{sc.Name.Split(' ')[0]}.png");
                    using FileStream fs = File.Create(file);
                    enc.Save(fs);
                }
            }
            return Task.CompletedTask;
        });
        File.WriteAllText(Path.Combine(dir, "heights.txt"), heights.ToString(), new System.Text.UTF8Encoding(false));
        Assert.True(Directory.GetFiles(dir, "*.png").Length >= 30);
    }

    [Fact(Skip = "測定用(未取得)。手元で Skip を外して回すと、%TEMP% の ohanami-vlm-absent-shots に、テキスト検索のモデルが未取得の状態の左パネルの PNG(最小の窓と既定の窓の高さ)と、行ごとの幅(numbers.txt)を書く。通常の回帰テストには入れない")]
    public async Task Render_the_not_fetched_state_of_the_left_panel_offscreen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ohanami-vlm-absent-shots");
        Directory.CreateDirectory(dir);
        var numbers = new System.Text.StringBuilder();
        await StaDispatcher.RunAsync(() =>
        {
            foreach (double height in new[] { 760.0 - ChromeHeight - StatusBarHeight, WindowLayout.MinWindowHeight - ChromeHeight - StatusBarHeight })
                foreach (Scenario sc in Scenarios().Where(s => s.Name.StartsWith("18", StringComparison.Ordinal) || s.Name.StartsWith("19", StringComparison.Ordinal)))
                {
                    (IndexPanel panel, Grid root) = Layout(sc, height);
                    numbers.AppendLine($"窓の高さ(左パネルに使える高さ) {height} / {sc.Name}");
                    foreach (TextBlock row in panel.IndexRowsForTests.Rows)
                    {
                        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        double available = 282 - row.Margin.Left;
                        string cut = row.Text.Length > 0 && row.DesiredSize.Width > available ? "切れる(末尾が「…」。全文はヒント)" : "収まる";
                        numbers.AppendLine($"  [{row.Text}] 要る幅 {row.DesiredSize.Width:0.0} / 使える幅 {available:0.0} → {cut}" + (row.ToolTip is string tip && tip.Length > 0 ? $" ヒント: {tip.Replace("\n", " / ")}" : ""));
                    }
                    root.Children.Remove(panel);
                    var holder = new Grid { Width = 300, Height = height, Background = Brushes.White };
                    holder.Children.Add(panel);
                    FontSetup(holder);
                    holder.Measure(new Size(300, height));
                    holder.Arrange(new Rect(0, 0, 300, height));
                    holder.UpdateLayout();
                    var bmp = new RenderTargetBitmap(300, (int)height, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(holder);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using FileStream fs = File.Create(Path.Combine(dir, $"left-{(int)height}-{sc.Name.Split(' ')[0]}.png"));
                    enc.Save(fs);
                }
            return Task.CompletedTask;
        });
        File.WriteAllText(Path.Combine(dir, "numbers.txt"), numbers.ToString(), new System.Text.UTF8Encoding(false));
        Assert.Equal(4, Directory.GetFiles(dir, "left-*.png").Length);
    }
}
