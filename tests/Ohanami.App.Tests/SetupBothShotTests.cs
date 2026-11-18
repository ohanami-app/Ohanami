using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 測定用: 「2 つともダウンロード」の各場面の画面を、画面に出さずに描いて PNG にする(<c>OHANAMI_SETUP_SHOTS=1</c> のときだけ動く。通常の回帰テストには入れない)。
/// %TEMP% の setup-both-shots に PNG と、場面ごとの窓の高さと領域の文(heights.tsv)を書く。
/// </summary>
public sealed class SetupBothShotTests
{
    private const ModelSets Both = SetupWindow.BothSets;

    /// <summary>窓の中身の高さ(幅 820 で組んだとき)。</summary>
    private static double ContentHeight(SetupWindow w)
    {
        var root = (UIElement)w.Content;
        w.Content = null;
        var host = new Border { Child = root, Width = 820 };
        TestLook.Setup(host);
        host.Measure(new Size(820, double.PositiveInfinity));
        double height = host.DesiredSize.Height;
        host.Child = null;
        w.Content = root;
        return height;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (DependencyObject inner in Descendants(child)) yield return inner;
        }
    }

    /// <summary>約束の文を、導入の文の直後へ移す(案の絵を作るためだけ。画面の作りは変えない)。</summary>
    private static void MovePromiseUnderLead(SetupWindow w)
    {
        StackPanel body = Descendants((DependencyObject)w.Content).OfType<StackPanel>()
            .First(p => p.Children.OfType<TextBlock>().Any(t => t.Text == SetupWords.Promise));
        TextBlock promise = body.Children.OfType<TextBlock>().First(t => t.Text == SetupWords.Promise);
        body.Children.Remove(promise);
        promise.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Insert(2, promise);   // 見出し・導入の文の次
    }

    [Fact]
    public async Task Render_the_bulk_download_states_offscreen()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OHANAMI_SETUP_SHOTS") == "1", "環境変数 OHANAMI_SETUP_SHOTS=1 が無い(%TEMP% の setup-both-shots に、「2 つともダウンロード」の各場面の PNG と、窓の高さ(heights.tsv)を書く手元専用の測定)");
        string dir = Path.Combine(Path.GetTempPath(), "setup-both-shots");
        Directory.CreateDirectory(dir);
        // 別の案の絵を並べて比べるための、名前の接尾辞(例: -compact)
        string suffix = Environment.GetEnvironmentVariable("OHANAMI_SETUP_SHOTS_SUFFIX") ?? "";
        var lines = new List<string>();
        await StaDispatcher.RunAsync(async () =>
        {
            void Shot(SetupWindow w, string name)
            {
                SetupWindowTests.Save(w, Path.Combine(dir, name + suffix + ".png"));
                string area = string.Join(" | ", new[] { Both, ModelSets.FeatureVector, ModelSets.Vlm }
                    .Select(k => $"[{k}] " + string.Join(" / ", w.AreaTextsForTests(k).Concat(w.AreaButtonsForTests(k)))));
                lines.Add($"{name}\t{ContentHeight(w):0.0}\t{area}");
            }
            string[] embedding = SetupDownloadBothTests.EmbeddingFiles, vlm = SetupDownloadBothTests.VlmFiles;
            ModelDownloadProgress P(ModelDownloadPhase phase, string file, int index, long done, long total = 10_770_000_000) => SetupDownloadBothTests.P(phase, file, index, done, total);
            ModelDownloadException Fail(ModelDownloadFailure kind, string? file) => SetupDownloadBothTests.Fail(kind, file);
            DateTime t0 = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

            // 1: 押す前(約束の文は、今の位置 / 導入の文の下)
            SetupWindow w1 = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads { Free = 312_400_000_000 });
            Shot(w1, "1-before");
            MovePromiseUnderLead(w1);
            Shot(w1, "1b-before-promise-under-lead");
            w1.Close();

            // 2・3・5: 「2 つとも」を取っている最中(1 つ目の途中 / 2 つ目の途中)。2 つ目の途中で失敗
            var fake = new FakeDownloads { Free = 312_400_000_000 };
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            DateTime t = t0;
            w.Now = () => t;
            Task run = w.StartDownloadAsync(Both);
            fake.Report(P(ModelDownloadPhase.Downloading, embedding[0], 0, 900_000_000));
            t = t.AddSeconds(2);
            fake.Report(P(ModelDownloadPhase.Downloading, embedding[0], 0, 1_000_000_000));
            Shot(w, "2-bulk-first-model");
            for (int i = 0; i < embedding.Length; i++) fake.Report(P(ModelDownloadPhase.Completed, embedding[i], i, 4_579_405_088));
            fake.Report(P(ModelDownloadPhase.Downloading, vlm[0], 4, 5_000_000_000));
            t = t.AddSeconds(2);
            fake.Report(P(ModelDownloadPhase.Downloading, vlm[0], 4, 5_100_000_000));
            Shot(w, "3-bulk-second-model");
            current = SetupWindowTests.VlmOnlyMissing();   // 置き場には、1 つ目のモデルが置いてある(確かめ直した画面)
            fake.Fail(Fail(ModelDownloadFailure.HttpStatus, vlm[0]));
            await run;
            Shot(w, "5-after-failure-midway-second");
            w.Close();

            // 4: 1 つ目の途中で中止
            var fc = new FakeDownloads { Free = 312_400_000_000 };
            SetupWindow wc = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fc);
            Task rc = wc.StartDownloadAsync(Both);
            fc.Report(P(ModelDownloadPhase.Downloading, embedding[0], 0, 1_000_000_000));
            SetupWindowTests.Click(SetupWindowTests.ButtonOf(wc, SetupWords.CancelLabel));
            await rc;
            Shot(wc, "4-after-cancel");
            wc.Close();

            // 5a: 1 つ目で失敗
            var ff = new FakeDownloads { Free = 312_400_000_000 };
            SetupWindow wf = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: ff);
            Task rf = wf.StartDownloadAsync(Both);
            ff.Fail(Fail(ModelDownloadFailure.HttpStatus, embedding[1]));
            await rf;
            Shot(wf, "5a-after-failure-first");
            wf.Close();

            // 5c: 長い失敗の文(長いファイル名・長い接続先)
            var fl = new FakeDownloads { Free = 312_400_000_000 };
            SetupWindow wl = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fl);
            Task rl = wl.StartDownloadAsync(Both);
            fl.Fail(new ModelDownloadException(ModelDownloadFailure.HttpStatus, "Core のメッセージ(テスト)", embedding[2], 503, "download-mirror-01.eu-central.example.test", null, null));
            await rl;
            Shot(wl, "5c-after-failure-long");
            wl.Close();

            // 6c: 置き場が別のドライブで、片方が足りない(ドライブごとの空きを言う長い文)
            SetupWindow wd = SetupWindowTests.NewWindow(SetupDownloadBothTests.OnDrives(@"C:\ohanami-test\a", @"Z:\ohanami-test\b"),
                downloads: new FakeDownloads { FreeOf = d => d.StartsWith("Z:", StringComparison.OrdinalIgnoreCase) ? 6_000_000_000 : 5_000_000_000 });
            Shot(wd, "6c-no-space-two-drives");
            wd.Close();

            // 6: 空きが足りない(合わせた量に足りない。それぞれには足りる)
            SetupWindow wn = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads { Free = 8_000_000_000 });
            Shot(wn, "6-no-space-for-both");
            wn.Close();

            // 6b: 前回の続きがある
            SetupWindow wp = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads { Free = 312_400_000_000, Partial = 1_000_000_000 });
            Shot(wp, "6b-resume");
            wp.Close();

            // 7: 片方だけそろった(「2 つとも」の領域は無い)
            SetupWindow w7 = SetupWindowTests.NewWindow(SetupWindowTests.VlmOnlyMissing(), downloads: new FakeDownloads { Free = 312_400_000_000 });
            Shot(w7, "7-one-done");
            w7.Close();

            // 8: 区画の「ダウンロード」を押している間の、「2 つとも」の領域
            var f8 = new FakeDownloads { Free = 312_400_000_000 };
            SetupWindow w8 = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: f8);
            Task r8 = w8.StartDownloadAsync(ModelSets.Vlm);
            f8.Report(new ModelDownloadProgress(ModelDownloadPhase.Downloading, vlm[0], 0, 2, 2_000_000_000, 6_190_000_000));
            Shot(w8, "8-one-model-downloading-bulk-disabled");
            f8.Fail(Fail(ModelDownloadFailure.Network, vlm[0]));
            await r8;
            w8.Close();
        });
        File.WriteAllLines(Path.Combine(dir, $"heights{suffix}.tsv"), lines);
        Assert.True(Directory.GetFiles(dir, "*.png").Length >= 10);
    }
}
