using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 左パネル(IndexPanel)の大きさの測定(測るだけ。製品の振る舞いは見ない)。
/// 窓は作らない: MainWindow と同じ割り付け(左 300 固定・右は残り・下に状態のバー)を Grid で組み、Measure / Arrange で、内容が全部見えるのに要る高さと幅・ボタンの折り返し・切れる所を数える。
/// 結果は %TEMP% の left-panel-measure.txt に書く(数字だけ。画像のファイル名・パスは合成の名前)。測定の結果は断言しない(測るだけ。壊れていないことだけを断言する)。
/// </summary>
public sealed class LeftPanelMeasureTests
{
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d)
                foreach (DependencyObject x in Walk(d)) yield return x;
    }

    private static IndexState State(
        IndexRunState run, JobKind? job, IReadOnlyList<StageSummary> stages, EngineStatus? engine = null, ScanProgress? scan = null,
        string? path = null, double? ips = null, TimeSpan? remaining = null, string? error = null, int skipped = 0) =>
        new(run, job, stages, engine ?? EngineStatus.NotCreated, scan, path, ips, remaining, error, skipped);

    private static StageSummary Stg(Stage stage, int pending = 0, int done = 0, int failed = 0, int outdated = 0) => new(stage, pending, done, failed, outdated);

    private sealed record Scenario(string Name, IndexState State, int? Missing);

    private static readonly string LongName = "Screenshot_2026-11-10_synthetic-long-file-name-for-layout-measurement_0000012345678.png";
    private static readonly string LongReasonA = "モデルのファイルが無い: ViT-SO400M-16-SigLIP2-384__ohanami/model.onnx(探した場所: C:\\Users\\synthetic\\AppData\\Local\\Ohanami\\models\\ViT-SO400M-16-SigLIP2-384__ohanami\\model.onnx)。期待サイズ 1234567890 バイト。";
    private static readonly string LongReasonB = "VLM の処理プログラムが 3 枚続けて異常になった。GPU が他で使われているか、ドライバの問題かもしれない(場所: C:\\Users\\synthetic\\AppData\\Local\\Ohanami\\runtime\\llama.cpp\\b12110)。";

    private static IReadOnlyList<Scenario> Scenarios() =>
    [
        new("(a1) 止まっている・全部済み(条件つきのボタンは全部押せない)",
            State(IndexRunState.Idle, null, [Stg(Stage.Embed, done: 1030), Stg(Stage.Ocr, done: 1030), Stg(Stage.Caption, done: 1030)]), null),
        new("(a2) 止まっている・未作成あり(条件つきのボタンは全部押せない)",
            State(IndexRunState.Idle, null, [Stg(Stage.Embed, pending: 1030), Stg(Stage.Ocr, pending: 1030), Stg(Stage.Caption, pending: 1030)]), null),
        new("(b0) VLM の実行中・ふつうの形(段の 2 行 + 実行中 + 短めのファイル名)",
            State(IndexRunState.Running, JobKind.Vlm, [Stg(Stage.Embed, done: 1030), Stg(Stage.Ocr, pending: 800, done: 230), Stg(Stage.Caption, pending: 800, done: 230)],
                null, null, @"C:\synthetic\Screenshot_2026-11-10_123456.png", 0.08, TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5)), null),
        new("(b1) VLM の実行中・最も長い形(走査 + 準備中 + 速さと残り + 長いファイル名 + 飛ばした)",
            State(IndexRunState.Running, JobKind.Vlm,
                [Stg(Stage.Embed, pending: 1234, done: 8000, failed: 123, outdated: 4567), Stg(Stage.Ocr, pending: 1234, done: 7000, failed: 123, outdated: 4567), Stg(Stage.Caption, pending: 1234, done: 7000, failed: 123, outdated: 4567)],
                new EngineStatus(EnginePhase.Checking, null, null), new ScanProgress(1, 12345, 1234, 123, 45, 0, 0, 0, 0),
                LongName, 0.08, TimeSpan.FromHours(12) + TimeSpan.FromMinutes(34), null, 12), null),
        new("(b2) 停止中 (失敗で止まりました)・長い理由が 2 つ(準備できなかった + 止まった原因)",
            State(IndexRunState.Failed, JobKind.Vlm, [Stg(Stage.Embed, done: 1030), Stg(Stage.Ocr, pending: 1030), Stg(Stage.Caption, pending: 1030)],
                new EngineStatus(EnginePhase.Failed, LongReasonA, null), null, null, null, null, LongReasonB, 12), null),
        new("(c) 失敗・要再作成・フォルダに無い画像(3 桁)がある(止まっている。「インデックスの状態…」の隣の 1 行が最も長い形)",
            State(IndexRunState.Idle, null,
                [Stg(Stage.Embed, pending: 12, done: 900, failed: 12, outdated: 123), Stg(Stage.Ocr, pending: 12, done: 800, failed: 34, outdated: 123), Stg(Stage.Caption, pending: 12, done: 800, failed: 34, outdated: 123)], null, null, null, null, null, null, 2),
            123),
    ];

    private static Button? FindButton(IndexPanel panel, Func<string, bool> match) =>
        Walk(panel).OfType<Button>().FirstOrDefault(b => b.Content is string s && match(s));

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    [Fact(Skip = "測定用。手元で Skip を外して回すと %TEMP% の left-panel-measure.txt に書く。通常の回帰テストには入れない")]
    public async Task Measure_the_left_panel_per_state_and_the_detail_image_width()
    {
        var sb = new StringBuilder();
        await StaDispatcher.RunAsync(() =>
        {
            Thickness frame = SystemParameters.WindowNonClientFrameThickness;
            sb.AppendLine(CultureInfo.InvariantCulture, $"窓の枠(WindowNonClientFrameThickness、DIP): 左 {Fmt(frame.Left)} 上 {Fmt(frame.Top)} 右 {Fmt(frame.Right)} 下 {Fmt(frame.Bottom)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"画面のスケール: {VisualTreeHelper.GetDpi(new Border()).DpiScaleX}(DIP の測定なので、スケールにはよらない)");

            // 状態のバーの高さ(通常の 1 行)
            var status = new StatusBarView();
            status.ShowCoreStatus(new CoreStatus(CorePhase.Ready, null, true, 1030));
            FontSetup(status);
            status.Measure(new Size(1000, double.PositiveInfinity));
            double statusHeight = status.DesiredSize.Height;
            sb.AppendLine(CultureInfo.InvariantCulture, $"状態のバーの高さ: {Fmt(statusHeight)}");

            foreach ((string name, double w, double h) in new[] { ("既定 1180×760", 1180.0, 760.0), ($"最小 {WindowLayout.MinWindowWidth}×{WindowLayout.MinWindowHeight}", WindowLayout.MinWindowWidth, WindowLayout.MinWindowHeight) })
            {
                double clientW = w - frame.Left - frame.Right;
                double clientH = h - frame.Top - frame.Bottom;
                sb.AppendLine();
                sb.AppendLine("==================================================================");
                sb.AppendLine(CultureInfo.InvariantCulture, $"{name}: 外側 {Fmt(w)}×{Fmt(h)} → 中(クライアント)の推定 {Fmt(clientW)}×{Fmt(clientH)}(枠は上のとおり。実機の枠は未確認)、左パネルに使える高さ {Fmt(clientH - statusHeight)}");
                foreach (Scenario sc in Scenarios()) MeasureScenario(sb, sc, clientW, clientH);
            }

            MeasureDetail(sb, frame);
            return Task.CompletedTask;
        });
        string path = Path.Combine(Path.GetTempPath(), "left-panel-measure.txt");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Assert.True(sb.Length > 0);
    }

    /// <summary>MainWindow と同じフォントと、アプリと同じ Fluent の皮(TestLook)。</summary>
    private static void FontSetup(FrameworkElement e) => TestLook.Setup(e);

    private static void MeasureScenario(StringBuilder sb, Scenario sc, double clientW, double clientH)
    {
        var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
        panel.ShowForTests(sc.State, sc.Missing);

        // MainWindow と同じ割り付け: 行 0 = 左 300 | 右、行 1 = 状態のバー
        var right = new Border();
        var status = new StatusBarView();
        status.ShowCoreStatus(new CoreStatus(CorePhase.Ready, null, true, 1030));
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(panel, 0);
        Grid.SetColumn(right, 1);
        main.Children.Add(panel);
        main.Children.Add(right);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(main, 0);
        Grid.SetRow(status, 1);
        root.Children.Add(main);
        root.Children.Add(status);
        FontSetup(root);

        // (1) 高さに制限が無いとき: 要る高さ・幅(一覧は最低の高さ)
        var free = new Grid();
        free.Children.Add(CloneFor(sc, out IndexPanel freePanel));
        FontSetup(free);
        freePanel.Measure(new Size(300, double.PositiveInfinity));
        Size need = freePanel.DesiredSize;
        freePanel.Arrange(new Rect(0, 0, 300, need.Height));
        freePanel.UpdateLayout();

        sb.AppendLine();
        sb.AppendLine("--- " + sc.Name);
        sb.AppendLine(CultureInfo.InvariantCulture, $"  要る高さ(余白の上下 24 を含む、一覧は最低の 120): {Fmt(need.Height)}、幅 {Fmt(need.Width)}(300 の列に収まる幅は余白の左右 18 を引いた 282)");
        Report(sb, freePanel);

        // (2) 使える高さに置いたとき: 切れる所
        root.Measure(new Size(clientW, clientH));
        root.Arrange(new Rect(0, 0, clientW, clientH));
        root.UpdateLayout();
        double avail = main.ActualHeight;
        double listH = Walk(panel).OfType<ListBox>().First().ActualHeight;
        sb.AppendLine(CultureInfo.InvariantCulture, $"  使える高さ {Fmt(avail)} → 足りない高さ {Fmt(Math.Max(0, need.Height - avail))}。このとき一覧の高さ {Fmt(listH)}(最低 120 {(listH < 120 ? "を割る" : listH <= 120.5 ? "ちょうど" : "を上回る")})");
        // 置かれた位置(main の左上が原点。パネルの余白 12 を含む)。DockPanel は、下に積む部分を「下端に寄せて」置くので、足りないときは一覧や上の見出しに重なる
        TextBlock[] hs = Walk(panel).OfType<TextBlock>().Where(t => t.Text is "フォルダ" or "インデックス" or "処理状態").OrderBy(t => t.TranslatePoint(new Point(0, 0), main).Y).ToArray();
        Button firstBottom = Walk(panel).OfType<Button>().First(b => (string)b.Content == "追加…");
        Button lastBottom = Walk(panel).OfType<Button>().First(b => (string)b.Content == ProcessingWords.ProcessingStateLabel);
        ListBox listBox = Walk(panel).OfType<ListBox>().First();
        double Ym(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), main).Y;
        sb.AppendLine($"  置かれた位置(main 原点): パネル高さ {Fmt(panel.ActualHeight)}(desired {Fmt(panel.DesiredSize.Height)}) / " + string.Join(" ", hs.Select(h => $"見出し「{h.Text}」y={Fmt(Ym(h))}"))
            + $" / 一覧 y={Fmt(Ym(listBox))}〜{Fmt(Ym(listBox) + listBox.ActualHeight)} / [追加…] y={Fmt(Ym(firstBottom))} / [インデックスの状態…] 下端={Fmt(Ym(lastBottom) + lastBottom.ActualHeight)}");
        // 一覧は MinHeight 120 なので、置き場(スロット)が 120 より小さいと、120 の高さで作られて置き場に切り取られる(レイアウトのクリップ)。見える高さ = スロットの高さ
        Rect slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(listBox);
        sb.AppendLine($"  フォルダ一覧の置き場(スロット)の高さ = 見える高さ: {Fmt(slot.Height)}(部品としては 120 の高さで作られる。{(slot.Height < 119.5 ? "最低の 120 を割る" : "最低の 120 以上")})");
        Point lc = listBox.TranslatePoint(new Point(listBox.ActualWidth / 2, Math.Min(listBox.ActualHeight, Math.Max(1, slot.Height)) / 2), root);
        DependencyObject? lhit = VisualTreeHelper.HitTest(root, lc)?.VisualHit;
        bool listHit = false;
        for (DependencyObject? d = lhit; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : null) if (ReferenceEquals(d, listBox)) { listHit = true; break; }
        sb.AppendLine($"  一覧の見える部分の中心を押すと一覧に当たるか: {(slot.Height < 0.5 ? "(見える高さ 0 なので対象なし)" : listHit ? "当たる" : "当たらない")}");
        // 重なり・切れ: 下の積みの部品(ボタン・見出し・進捗の文章・進捗バー・区切り線)ごとに。一覧は Children の最後(= 一番手前)に足してあるので、重なる分は一覧が上に描かれる
        double lt = Ym(listBox), lb = lt + listBox.ActualHeight;
        var covered = new List<string>();
        var partly = new List<string>();
        var cut = new List<string>();
        var notHit = new List<string>();
        foreach (FrameworkElement el in Walk(panel).OfType<FrameworkElement>().Where(e => (e is Button or TextBlock or ProgressBar or Separator) && !ReferenceEquals(e, listBox) && !IsInside(e, listBox)))
        {
            double top = Ym(el), bottom = top + el.ActualHeight;
            string label = el switch { Button b => "[" + b.Content + "]", TextBlock t => "「" + Short(t.Text) + "」", ProgressBar => "進捗バー", _ => "区切り線" };
            if (el.ActualHeight <= 0) continue;
            if (top >= avail) cut.Add(label);
            else if (bottom > avail + 0.5) cut.Add(label + "(下の " + Fmt(bottom - avail) + "px)");
            double overlapH = Math.Min(bottom, lb) - Math.Max(top, lt);
            if (overlapH >= el.ActualHeight - 0.5) covered.Add(label);
            else if (overlapH > 0.5) partly.Add(label + "(" + Fmt(overlapH) + "px)");
            if (el is Button btn && top < avail)
            {
                Point c = btn.TranslatePoint(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2), root);
                DependencyObject? hit = VisualTreeHelper.HitTest(root, c)?.VisualHit;
                bool ok = false;
                for (DependencyObject? d = hit; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null) if (ReferenceEquals(d, btn)) { ok = true; break; }
                if (!ok) notHit.Add(label);
            }
        }
        sb.AppendLine("  使える高さの外へ出て切れる部品: " + (cut.Count == 0 ? "なし" : string.Join("、", cut)));
        sb.AppendLine("  ボタンの中心を押したとき、そのボタンに当たらない(使える高さの中で): " + (notHit.Count == 0 ? "なし" : string.Join("、", notHit)));
    }

    private static bool IsInside(DependencyObject e, DependencyObject ancestor)
    {
        for (DependencyObject? d = e; d is not null; d = LogicalTreeHelper.GetParent(d)) if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private static string Short(string s) => s.Replace("\n", "⏎", StringComparison.Ordinal) is { Length: > 18 } x ? x[..18] + "…" : s.Replace("\n", "⏎", StringComparison.Ordinal);

    private static IndexPanel CloneFor(Scenario sc, out IndexPanel panel)
    {
        panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
        panel.ShowForTests(sc.State, sc.Missing);
        return panel;
    }

    private static void Report(StringBuilder sb, IndexPanel panel)
    {
        TextBlock[] headers = Walk(panel).OfType<TextBlock>().Where(t => t.Text is "フォルダ" or "インデックス" or "処理状態").ToArray();
        ListBox list = Walk(panel).OfType<ListBox>().First();
        TextBlock state = panel.IndexRowsForTests.Rows[4];      // 状態の行(「インデックス」欄は固定の 7 行。ADR 0039。IndexSectionHeightTests が、高さの不変を固定する)
        ProgressBar bar = Walk(panel).OfType<ProgressBar>().First();
        TextBlock oneLine = new() { Text = "あ" };
        oneLine.SetValue(TextElement.FontFamilyProperty, new FontFamily("Yu Gothic UI, Segoe UI"));
        oneLine.SetValue(TextElement.FontSizeProperty, 13.0);
        oneLine.Measure(new Size(1000, 1000));
        double lineH = oneLine.DesiredSize.Height;

        double Y(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), panel).Y;
        sb.AppendLine($"  縦の並び(上端 y、高さ。パネルの左上が原点): " +
            string.Join(" / ", headers.OrderBy(Y).Select(h => $"見出し「{h.Text}」y={Fmt(Y(h))} h={Fmt(h.ActualHeight)}")) +
            $" / 一覧 y={Fmt(Y(list))} h={Fmt(list.ActualHeight)} / 進捗の文章 y={Fmt(Y(state))} h={Fmt(state.ActualHeight)}(約 {Fmt(state.ActualHeight / lineH)} 行。1 行 {Fmt(lineH)}) / 進捗バー y={Fmt(Y(bar))} h={Fmt(bar.ActualHeight)}");

        foreach (WrapPanel wrap in Walk(panel).OfType<WrapPanel>())
        {
            var rows = wrap.Children.OfType<Button>()
                .GroupBy(b => Math.Round(b.TranslatePoint(new Point(0, 0), panel).Y))
                .OrderBy(g => g.Key).ToArray();
            string first = (string)wrap.Children.OfType<Button>().First().Content;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ボタンの段({first}… の組): {rows.Length} 段、組の高さ {Fmt(wrap.ActualHeight)}(WrapPanel の上下の余白 6 を含まない)");
            int r = 1;
            foreach (var g in rows)
            {
                sb.AppendLine("    " + r++.ToString(CultureInfo.InvariantCulture) + " 段目: " + string.Join(" ", g.Select(b => $"[{b.Content}]({Fmt(b.ActualWidth)}px)")));
            }
            foreach (Button b in wrap.Children.OfType<Button>())
            {
                b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double natural = b.DesiredSize.Width;
                if (natural > 282.5) sb.AppendLine(CultureInfo.InvariantCulture, $"    ★はみ出す: [{b.Content}] の文字が要る幅 {Fmt(natural)}px > 282(幅 300 から余白 18 を引いた分)");
            }
        }
        sb.AppendLine("  進捗の文章の中身(行ごと):");
        foreach (string line in state.Text.Split('\n')) sb.AppendLine("    | " + line);
    }

    private static void MeasureDetail(StringBuilder sb, Thickness frame)
    {
        sb.AppendLine();
        sb.AppendLine("==================================================================");
        sb.AppendLine($"詳細画面を開いたときの画像の幅(左 {WindowLayout.LeftColumnWidth} + 右パネルの余白 6・12 + 詳細画面の右の欄 {WindowLayout.DetailSideWidth} 固定 + 詳細の内側の余白 {WindowLayout.DetailInnerMargin}×2 + 画像の余白 6×2)");
        foreach ((string name, double w) in new[] { ("既定 1180", 1180.0), ($"最小 {WindowLayout.MinWindowWidth}", WindowLayout.MinWindowWidth), ("1280", 1280.0), ("1920", 1920.0) })
        {
            double clientW = w - frame.Left - frame.Right;
            // 一覧(右パネル)の body と同じ幅 = DetailView に渡す幅: クライアント幅 − 左の列 − 右パネルの余白(左 6・右 12)。
            // 詳細の内側の余白(DetailInnerMargin×2)は、DetailView が自分の中で引くので、ここでは引かない(引くと二重になる)
            double detailW = Math.Max(0, clientW - WindowLayout.LeftColumnWidth - WindowLayout.RightPanelMarginWidth);
            var view = new DetailView { Visibility = Visibility.Visible };
            FontSetup(view);
            view.Measure(new Size(detailW, 600));
            view.Arrange(new Rect(0, 0, detailW, 600));
            view.UpdateLayout();
            Border host = Walk(view).OfType<Border>().FirstOrDefault(b => b.Background is SolidColorBrush c && c.Color == Color.FromRgb(0x1E, 0x1E, 0x1E))
                ?? (Border)VisualWalk(view).First(x => x is Border b && b.Background is SolidColorBrush c && c.Color == Color.FromRgb(0x1E, 0x1E, 0x1E));
            Image image = VisualWalk(host).OfType<Image>().First();
            ScrollViewer side = VisualWalk(view).OfType<ScrollViewer>().First();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {name}: クライアント幅の推定 {Fmt(clientW)} → 詳細画面の幅 {Fmt(detailW)} → 画像の欄(暗い背景)の幅 {Fmt(host.ActualWidth)} → 画像に使える幅 {Fmt(Math.Max(0, host.ActualWidth - 12))}(画像の余白 6×2 を引く)、右の欄(スクロール)の幅 {Fmt(side.ActualWidth)}(うち内容は余白 12 + 8 を引く)");
        }
    }

    private static IEnumerable<DependencyObject> VisualWalk(DependencyObject root)
    {
        yield return root;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            foreach (DependencyObject x in VisualWalk(VisualTreeHelper.GetChild(root, i))) yield return x;
    }
}
