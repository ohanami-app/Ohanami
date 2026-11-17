using System;
using System.Collections.Generic;
using System.IO;
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
/// 処理状態の窓を、**画面に出さずに**画像にする(測定・確認用)。
/// 窓は表示しない。窓の中身を取り出して、決めた大きさで Measure / Arrange し、RenderTargetBitmap で PNG に書く(%TEMP% の processing-window-shots)。合成のデータだけ。
/// 通常は Skip(手元で Skip を外して回す)。
/// </summary>
public sealed class ProcessingWindowRenderTests
{
    private static string H(char c) => new(c, 64);

    private static IndexState State(IndexRunState run, int embedFailed = 0, int vlmFailed = 0, int skipped = 0, int embedOutdated = 0, int vlmOutdated = 0) =>
        new(run, null,
            [new StageSummary(Stage.Embed, 0, 100, embedFailed, embedOutdated), new StageSummary(Stage.Ocr, 0, 100, vlmFailed, vlmOutdated), new StageSummary(Stage.Caption, 0, 100, vlmFailed, vlmOutdated)],
            EngineStatus.NotCreated, null, null, null, null, null, skipped);

    private static ProblemList ListOf(ProblemKind kind)
    {
        var at = new DateTimeOffset(2026, 11, 10, 3, 4, 5, TimeSpan.Zero);
        switch (kind)
        {
            case ProblemKind.EmbedFailures:
                return ProcessingText.FailureRows(JobKind.Embed, new FailureList(Stage.Embed,
                [
                    new FailureItem(H('a'), @"C:\shots\sample\Screenshot_0001.png", "画像をデコード・前処理できない: 形式が壊れている(合成のデータ)", 2, at, "m"),
                    new FailureItem(H('b'), @"C:\shots\sample\Screenshot_0002.png", "読めない(合成のデータ)", 1, at.AddMinutes(-20), "m"),
                    new FailureItem(H('c'), null, null, 3, at.AddHours(-3), "m"),
                ], 3), TimeZoneInfo.Utc);
            case ProblemKind.VlmFailures:
                return ProcessingText.FailureRows(JobKind.Vlm, new FailureList(Stage.Caption,
                    [new FailureItem(H('d'), @"C:\shots\sample\Screenshot_0003.png", "finish_reason=length(合成のデータ)", 1, at, "m")], 1), TimeZoneInfo.Utc);
            case ProblemKind.Skipped:
                return ProcessingText.SkippedRows(
                [
                    new SkippedItem(JobKind.Vlm, H('e'), @"C:\shots\sample\Screenshot_0004.png", "別のプロセスが使っている(合成のデータ)"),
                    new SkippedItem(JobKind.Embed, H('f'), null, "現存するパスが無い"),
                ]);
            case ProblemKind.Outdated:
                return ProcessingText.OutdatedList(new RequeuePlan(40, 40));
            default:
                return ProcessingText.MissingRows(new MissingImageList(
                [
                    new MissingImageItem(H('g'), @"C:\shots\old\Screenshot_0100.png", at, 2, true),
                    new MissingImageItem(H('h'), @"C:\shots\old\Screenshot_0101.png", at.AddDays(-1), 0, true),
                    new MissingImageItem(H('i'), null, null, 1, false),
                ], 3, 2), TimeZoneInfo.Utc);
        }
    }

    private static void Render(FailureListWindow window, string path, double width = 960, double height = 560)
    {
        var root = (UIElement)window.Content;
        window.Content = null;
        var host = new Border { Background = AppColors.WindowBackground, Child = root };
        TestLook.Setup(host);   // 実機と同じフォント・Fluent(ライト)の皮
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using FileStream fs = File.Create(path);
        enc.Save(fs);
    }

    [Fact(Skip = "測定用(処理状態の窓)。手元で Skip を外して回すと、%TEMP% の processing-window-shots に PNG を書く。通常の回帰テストには入れない")]
    public async Task Render_the_processing_window_offscreen_for_each_kind()
    {
        string dir = Path.Combine(Path.GetTempPath(), "processing-window-shots");
        Directory.CreateDirectory(dir);
        await StaDispatcher.RunAsync(async () =>
        {
            ProcessingSnapshot idle = ProcessingSnapshot.Build(State(IndexRunState.Idle, embedFailed: 3, vlmFailed: 1, skipped: 2, embedOutdated: 40, vlmOutdated: 40), 3, true);
            ProcessingSnapshot running = ProcessingSnapshot.Build(State(IndexRunState.Running, embedFailed: 3, vlmFailed: 1, skipped: 2, embedOutdated: 40, vlmOutdated: 40), 3, true);
            foreach ((string name, ProcessingSnapshot snap) in new[] { ("idle", idle), ("running", running) })
            {
                foreach (ProblemKind kind in Enum.GetValues<ProblemKind>())
                {
                    var window = new FailureListWindow(kind, (k, _) => Task.FromResult(ListOf(k)), new FakeShell(), snap, _ => Task.CompletedTask);
                    await window.ReloadAsync();
                    if (kind == ProblemKind.MissingImages) window.SelectRow(2);   // パスの記録が無い行を選んだ状態(エクスプローラーで表示の理由が出る)
                    Render(window, Path.Combine(dir, $"{name}-{(int)kind + 1}-{kind}.png"));
                    window.Close();
                }
            }
        });
        Assert.True(Directory.GetFiles(dir, "*.png").Length >= 10);
    }

    [Fact(Skip = "測定用(未取得)。手元で Skip を外して回すと、%TEMP% の ohanami-vlm-absent-shots に、テキスト検索のモデルが未取得の状態の「インデックスの状態」の窓の PNG(既定の大きさと最小の大きさ)と、1 行の幅(window-numbers.txt)を書く。通常の回帰テストには入れない")]
    public async Task Render_the_processing_window_with_the_vlm_model_not_fetched_offscreen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ohanami-vlm-absent-shots");
        Directory.CreateDirectory(dir);
        var numbers = new System.Text.StringBuilder();
        await StaDispatcher.RunAsync(async () =>
        {
            IndexState state = State(IndexRunState.Waiting, embedFailed: 3, skipped: 2) with { UnavailableJobs = [new UnavailableJob(JobKind.Vlm, ["synthetic-vlm-model.gguf", "synthetic-vlm-mmproj.gguf"])] };
            ProcessingSnapshot snap = ProcessingSnapshot.Build(state, 0, true);
            foreach ((string name, double width, double height) in new[] { ("default", 960.0, 560.0), ("min", 640.0, 360.0) })
            {
                var window = new FailureListWindow(ProblemKind.EmbedFailures, (k, _) => Task.FromResult(ListOf(k)), new FakeShell(), snap, _ => Task.CompletedTask);
                await window.ReloadAsync();
                numbers.AppendLine($"{name} {width}x{height}: 窓の上の 1 行 [{window.ModelNoteText}] ({window.ModelNoteText.Length} 文字)");
                Render(window, Path.Combine(dir, $"window-{name}.png"), width, height);
                window.Close();
            }
            return;
        });
        File.WriteAllText(Path.Combine(dir, "window-numbers.txt"), numbers.ToString(), new System.Text.UTF8Encoding(false));
        Assert.Equal(2, Directory.GetFiles(dir, "window-*.png").Length);
    }
}
