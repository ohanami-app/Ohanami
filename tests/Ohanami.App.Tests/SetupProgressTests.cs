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
/// セットアップの画面の、ダウンロード中の表示の 2 つの決まり。偽のダウンロードの口(<see cref="FakeDownloads"/>)で動かす(ネットワークには出ない)。
/// <list type="number">
/// <item>ファイルごとに、取り終えて置いた時点で、その行を「あり」に替え、区画の見出しの数を減らす。いま取っている行は「取得中」「確かめ中」。</item>
/// <item>ダウンロードの領域の高さが、場面(押す前・取っている最中・失敗・中止・空きが足りない・もう一方を取っている)によらず一定で、窓の高さが変わらない。</item>
/// </list>
/// </summary>
public sealed class SetupProgressTests
{
    private const long EmbeddingTotal = 4_579_405_088;

    private static ModelDownloadProgress P(ModelDownloadPhase phase, string file, int index, long done, int count = 4, long total = EmbeddingTotal) =>
        new(phase, file, index, count, done, total);

    private static readonly string[] EmbeddingFiles = ["visual/model.onnx", "textual/model.onnx", "textual/model.onnx_data", "textual/tokenizer.json"];

    /// <summary>置き場に、指定のファイルだけがある状態(特徴ベクトル検索のモデルのうち)。</summary>
    private static SetupState WithEmbeddingPresent(params string[] present)
    {
        SetupState both = SetupWindowTests.BothMissing();
        ModelSetStatus e = both.Embedding with
        {
            Files = [.. both.Embedding.Files.Select(f => present.Contains(f.RelativePath) ? f with { State = ModelFileState.Present, ActualSize = f.ExpectedSize } : f)],
        };
        return both with { Embedding = e };
    }

    // ---------------------------------------------------------------- 1. ファイルごとに「あり」に替わる

    [Fact]
    public async Task The_file_rows_turn_to_present_one_by_one_and_the_header_count_goes_down()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            Assert.Equal(["未取得", "未取得", "未取得", "未取得"], w.FileStatesForTests(ModelSets.FeatureVector));
            Assert.Equal("未取得 4 ファイル", w.SetStateForTests(ModelSets.FeatureVector));

            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);
            Assert.Equal(["未取得", "未取得", "未取得", "未取得"], w.FileStatesForTests(ModelSets.FeatureVector));   // 始めたばかりは、どの行も印が無い

            // 1 つ目: 接続中・取得中は「取得中」、確かめている間は「確かめ中」、置いたら「あり」
            fake.Report(P(ModelDownloadPhase.Connecting, EmbeddingFiles[0], 0, 0));
            Assert.Equal(["取得中", "未取得", "未取得", "未取得"], w.FileStatesForTests(ModelSets.FeatureVector));
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[0], 0, 1_000_000_000));
            Assert.Equal(["取得中", "未取得", "未取得", "未取得"], w.FileStatesForTests(ModelSets.FeatureVector));
            fake.Report(P(ModelDownloadPhase.Verifying, EmbeddingFiles[0], 0, 1_713_350_137));
            Assert.Equal(["確かめ中", "未取得", "未取得", "未取得"], w.FileStatesForTests(ModelSets.FeatureVector));
            Assert.Equal("未取得 4 ファイル", w.SetStateForTests(ModelSets.FeatureVector));                     // 確かめ中は、まだ数に入る
            fake.Report(P(ModelDownloadPhase.Completed, EmbeddingFiles[0], 0, 1_713_350_137));
            Assert.Equal(["あり", "未取得", "未取得", "未取得"], w.FileStatesForTests(ModelSets.FeatureVector));
            Assert.Equal("未取得 3 ファイル", w.SetStateForTests(ModelSets.FeatureVector));

            // 2 つ目に移る。1 つ目は「あり」のまま
            fake.Report(P(ModelDownloadPhase.Connecting, EmbeddingFiles[1], 1, 1_713_350_137));
            Assert.Equal(["あり", "取得中", "未取得", "未取得"], w.FileStatesForTests(ModelSets.FeatureVector));
            fake.Report(P(ModelDownloadPhase.Completed, EmbeddingFiles[1], 1, 1_713_910_619));
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[2], 2, 2_000_000_000));
            Assert.Equal(["あり", "あり", "取得中", "未取得"], w.FileStatesForTests(ModelSets.FeatureVector));
            Assert.Equal("未取得 2 ファイル", w.SetStateForTests(ModelSets.FeatureVector));

            // もう一方の組は、取っていないので、そのまま
            Assert.Equal(["未取得", "未取得"], w.FileStatesForTests(ModelSets.Vlm));
            Assert.Equal("未取得 2 ファイル", w.SetStateForTests(ModelSets.Vlm));

            fake.Fail(new ModelDownloadException(ModelDownloadFailure.Network, "切れた(テスト)", EmbeddingFiles[2]));   // 後始末
            await run;
        });
    }

    [Fact]
    public async Task When_every_file_is_in_place_the_header_says_prepared_while_the_download_is_still_finishing()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);

            for (int i = 0; i < 4; i++)
            {
                fake.Report(P(ModelDownloadPhase.Connecting, EmbeddingFiles[i], i, 0));
                fake.Report(P(ModelDownloadPhase.Completed, EmbeddingFiles[i], i, 0));
            }

            Assert.Equal(["あり", "あり", "あり", "あり"], w.FileStatesForTests(ModelSets.FeatureVector));
            Assert.Equal(SetupWords.SetComplete, w.SetStateForTests(ModelSets.FeatureVector));
            Assert.True(w.IsDownloading);                                                       // 取り終えた通知のあとも、返事が来るまでは「取っている最中」
            Assert.Contains(SetupWords.CancelLabel, w.EnabledButtonTextsForTests());

            current = SetupWindowTests.VlmOnlyMissing();
            fake.Complete();
            await run;
            Assert.Equal(SetupWords.SetComplete, w.SetStateForTests(ModelSets.FeatureVector));     // 確かめ直したあとも同じ
        });
    }

    [Fact]
    public async Task After_a_failure_the_files_put_so_far_stay_present_and_only_the_rest_count()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);
            fake.Report(P(ModelDownloadPhase.Completed, EmbeddingFiles[0], 0, 1_713_350_137));
            fake.Report(P(ModelDownloadPhase.Completed, EmbeddingFiles[1], 1, 1_713_910_619));
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[2], 2, 2_000_000_000));

            current = WithEmbeddingPresent(EmbeddingFiles[0], EmbeddingFiles[1]);              // 置き場の実際(2 つ置いてある)
            fake.Fail(new ModelDownloadException(ModelDownloadFailure.Network, "切れた(テスト)", EmbeddingFiles[2], host: "cdn.example.test"));
            await run;

            Assert.False(w.IsDownloading);
            Assert.Equal(["あり", "あり", "未取得", "未取得"], w.FileStatesForTests(ModelSets.FeatureVector));    // 「取得中」は残らない
            Assert.Equal("未取得 2 ファイル", w.SetStateForTests(ModelSets.FeatureVector));
        });
    }

    [Fact]
    public async Task After_a_cancel_the_in_flight_mark_is_gone_and_the_finished_files_stay_present()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);
            fake.Report(P(ModelDownloadPhase.Completed, EmbeddingFiles[0], 0, 1_713_350_137));
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[1], 1, 1_713_400_000));

            current = WithEmbeddingPresent(EmbeddingFiles[0]);
            ButtonOf(w, SetupWords.CancelLabel).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await run;

            Assert.Equal(["あり", "未取得", "未取得", "未取得"], w.FileStatesForTests(ModelSets.FeatureVector));
            Assert.Contains(SetupWords.Cancelled, w.TextsForTests());
        });
    }

    private static Button ButtonOf(SetupWindow w, string text)
    {
        Button? found = null;
        void Walk(DependencyObject d)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(d))
            {
                if (child is Button b && b.Content is string s && s == text) found = b;
                if (child is DependencyObject o) Walk(o);
            }
        }
        Walk((DependencyObject)w.Content);
        return found ?? throw new InvalidOperationException("ボタンが無い: " + text);
    }

    [Fact]
    public void The_in_flight_words_are_the_same_words_as_the_progress_line()
    {
        Assert.Equal("取得中", SetupWords.FileStateWhileDownloading(ModelDownloadPhase.Connecting));
        Assert.Equal("取得中", SetupWords.FileStateWhileDownloading(ModelDownloadPhase.Downloading));
        Assert.Equal("確かめ中", SetupWords.FileStateWhileDownloading(ModelDownloadPhase.Verifying));
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupWords.FileStateWhileDownloading(ModelDownloadPhase.Completed));
        Assert.StartsWith(SetupWords.StateDownloading, SetupWords.ProgressLine(P(ModelDownloadPhase.Downloading, "a/b.bin", 0, 1)), StringComparison.Ordinal);
        Assert.StartsWith(SetupWords.StateVerifying, SetupWords.ProgressLine(P(ModelDownloadPhase.Verifying, "a/b.bin", 0, 1)), StringComparison.Ordinal);
        Assert.StartsWith("置き場に置きました: a/b.bin (1 / 4 ファイル)", SetupWords.ProgressLine(P(ModelDownloadPhase.Completed, "a/b.bin", 0, 1)), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 2. 高さが場面によらず一定

    /// <summary>窓の中身の高さ(幅 820 で測る。窓は表示しない)。窓が変えるのは、この高さ(SizeToContent.Height)。</summary>
    internal static double ContentHeight(SetupWindow w)
    {
        var root = (UIElement)w.Content;
        w.Content = null;
        var host = new Border { Child = root, Width = 820 };
        TestLook.Setup(host);
        host.Measure(new Size(820, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, 820, Math.Ceiling(host.DesiredSize.Height)));
        host.UpdateLayout();
        double height = host.DesiredSize.Height;
        host.Child = null;
        w.Content = root;
        return height;
    }

    private static ModelDownloadException Fail(ModelDownloadFailure kind, string? file = "textual/model.onnx_data", int? status = null, string? host = null) =>
        new(kind, "Core のメッセージ(テスト)。" + new string('長', 80), file, status, host, 4_646_000_000, 2_100_000_000);

    /// <summary>場面の全部(組・押す前・取っている最中の各段階・失敗の全種類・中止・空きが足りない・続き)の窓の高さを集める。名前は、どの場面かを言う。</summary>
    private static async Task<List<(string Name, double Height)>> HeightsAsync(Func<SetupState> state, params ModelSets[] keys)
    {
        var heights = new List<(string, double)>();
        void Add(string name, SetupWindow w) => heights.Add((name, ContentHeight(w)));

        foreach (ModelSets sets in keys)
        {
            string tag = sets + ": ";

            // 押す前(ふつう / 続きがある / 空きが足りない / 両方)
            foreach ((string name, FakeDownloads f) in new (string, FakeDownloads)[]
            {
                ("押す前", new FakeDownloads()),
                ("押す前・続きあり", new FakeDownloads { Partial = 1_000_000_000 }),
                ("押す前・空きが足りない", new FakeDownloads { Free = 1_000_000_000 }),
                ("押す前・空きが足りない・続きあり", new FakeDownloads { Free = 1_000_000_000, Partial = 1_000_000_000 }),
                ("押す前・空きが分からない", new FakeDownloads { Free = null }),
            })
            {
                Add(tag + name, SetupWindowTests.NewWindow(state(), downloads: f));
            }

            // 取っている最中: 各段階 × ファイル(長いファイル名)
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(state(), state, downloads: fake);
            Task run = w.StartDownloadAsync(sets);
            Add(tag + "取っている最中・始めたばかり", w);
            foreach (ModelDownloadPhase phase in Enum.GetValues<ModelDownloadPhase>())
                foreach (string file in new[] { "visual/model.onnx", "textual/model.onnx_data", "a-very-long-file-name-for-layout-measurement/with/deep/folders/model-00001-of-00009.gguf" })
                {
                    fake.Report(new ModelDownloadProgress(phase, file, 1, 4, phase == ModelDownloadPhase.Connecting ? 0 : 1_234_567_890, EmbeddingTotal));
                    Add(tag + $"取っている最中・{phase}・{file.Length} 文字", w);
                }
            fake.Report(new ModelDownloadProgress(ModelDownloadPhase.Completed, "visual/model.onnx", 0, 4, EmbeddingTotal, EmbeddingTotal));
            Add(tag + "取っている最中・1 つ置いた", w);
            fake.Fail(Fail(ModelDownloadFailure.Network));
            await run;

            // 失敗の全種類(長い文・接続先つき・状態コードつき)
            foreach (ModelDownloadFailure kind in Enum.GetValues<ModelDownloadFailure>())
                foreach ((string label, string? file, int? status, string? host) in new (string, string?, int?, string?)[]
                {
                    ("短い", null, null, null),
                    ("長い", "a-very-long-file-name-for-layout-measurement/with/deep/folders/model-00001-of-00009.gguf", 503, "cdn-lfs-eu-1-with-a-very-long-host-name.example.test"),
                })
                {
                    var f = new FakeDownloads();
                    SetupWindow fw = SetupWindowTests.NewWindow(state(), state, downloads: f);
                    Task r = fw.StartDownloadAsync(sets);
                    f.Fail(Fail(kind, file, status, host));
                    await r;
                    Add(tag + $"失敗・{kind}・{label}", fw);
                }

            // 中止
            var cf = new FakeDownloads();
            SetupWindow cw = SetupWindowTests.NewWindow(state(), state, downloads: cf);
            Task cr = cw.StartDownloadAsync(sets);
            cf.Report(new ModelDownloadProgress(ModelDownloadPhase.Downloading, "visual/model.onnx", 0, 4, 1, EmbeddingTotal));
            ButtonOf(cw, SetupWords.CancelLabel).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Add(tag + "中止している最中", cw);
            await cr;
            Add(tag + "中止したあと", cw);
        }
        return heights;
    }

    private static void AssertSameHeight(List<(string Name, double Height)> heights, string state)
    {
        double first = heights[0].Height;
        Assert.True(heights.Count > 20, $"場面が少ない({heights.Count})");
        foreach ((string name, double h) in heights)
            Assert.True(Math.Abs(h - first) < 0.001, $"{state}: 「{name}」の窓の高さ {h} が、最初の場面「{heights[0].Name}」の {first} と違う");
    }

    [Fact]
    public async Task The_window_height_is_the_same_in_every_scene_when_both_models_are_missing()
    {
        await StaDispatcher.RunAsync(async () =>
            AssertSameHeight(await HeightsAsync(SetupWindowTests.BothMissing, ModelSets.FeatureVector, ModelSets.Vlm, SetupWindow.BothSets), "両方が未取得"));
    }

    [Fact]
    public async Task The_window_height_is_the_same_in_every_scene_when_only_the_text_search_model_is_missing()
    {
        await StaDispatcher.RunAsync(async () =>
            AssertSameHeight(await HeightsAsync(SetupWindowTests.VlmOnlyMissing, ModelSets.Vlm), "テキスト検索のモデルだけが未取得"));
    }

    [Fact]
    public async Task The_window_height_is_the_same_in_every_scene_when_a_file_has_the_wrong_size()
    {
        await StaDispatcher.RunAsync(async () =>
            AssertSameHeight(await HeightsAsync(SetupWindowTests.WithMismatch, ModelSets.FeatureVector, ModelSets.Vlm, SetupWindow.BothSets), "大きさの違うファイルがある"));
    }

    [Fact]
    public async Task Every_download_area_has_the_fixed_height_and_the_rows_above_do_not_move()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            double[] three = [SetupWindow.BothAreaHeight, SetupWindow.DownloadAreaHeight, SetupWindow.DownloadAreaHeight];   // 「2 つとも」+ 区画ごとの 2 つ
            Assert.Equal(three, AreaHeights(w));

            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[0], 0, 1_000_000_000));
            Assert.Equal(three, AreaHeights(w));
            fake.Fail(Fail(ModelDownloadFailure.HttpStatus, status: 404, host: "cdn.example.test"));
            await run;
            Assert.Equal(three, AreaHeights(w));

            // 「2 つとも」を取っているとき・中止のあと(文の出る領域が「2 つとも」)
            Task both = w.StartDownloadAsync(SetupWindow.BothSets);
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[0], 0, 1_000_000_000));
            Assert.Equal(three, AreaHeights(w));
            ButtonOf(w, SetupWords.CancelLabel).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await both;
            Assert.Equal(three, AreaHeights(w));

            // VLM だけ残った窓(特徴ベクトル検索のモデルがそろった)では、領域は 1 つ
            current = SetupWindowTests.VlmOnlyMissing();
            w.Recheck(manual: false);
            Assert.Equal([SetupWindow.DownloadAreaHeight], AreaHeights(w));
        });
    }

    /// <summary>ダウンロードの領域(固定の高さの StackPanel)の、描いたあとの高さ。</summary>
    private static double[] AreaHeights(SetupWindow w)
    {
        var root = (UIElement)w.Content;
        w.Content = null;
        var host = new Border { Child = root, Width = 820 };
        TestLook.Setup(host);
        host.Measure(new Size(820, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, 820, Math.Ceiling(host.DesiredSize.Height)));
        host.UpdateLayout();
        double[] result = [.. Descendants(root).OfType<FrameworkElement>()
            .Where(p => (p is StackPanel && p.Height == SetupWindow.DownloadAreaHeight) || (p is Grid && p.Height == SetupWindow.BothAreaHeight)).Select(p => p.ActualHeight)];
        host.Child = null;
        w.Content = root;
        return result;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (DependencyObject inner in Descendants(child)) yield return inner;
        }
    }

    [Fact]
    public async Task On_a_screen_shorter_than_the_content_the_window_is_capped_at_the_work_area_and_scrolls()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads());
            Assert.Equal(SystemParameters.WorkArea.Height, w.MaxHeight);
            var scroll = Assert.IsType<ScrollViewer>(w.Content);
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);

            // 画面が低い(600)として測ると、中身が届かない分をスクロールで見る(はみ出さない)
            scroll.Measure(new Size(820, 600));
            scroll.Arrange(new Rect(0, 0, 820, 600));
            scroll.UpdateLayout();
            Assert.Equal(600, scroll.ActualHeight);
            Assert.True(scroll.ExtentHeight > scroll.ViewportHeight, $"中身 {scroll.ExtentHeight} が 表示 {scroll.ViewportHeight} に収まっているのでスクロールの検査にならない");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_failure_that_does_not_fit_is_cut_and_its_full_text_and_detail_are_in_the_hint()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);
            var error = Fail(ModelDownloadFailure.Network, "a-very-long-file-name-for-layout-measurement/with/deep/folders/model-00001-of-00009.gguf", host: "cdn-lfs-eu-1-with-a-very-long-host-name.example.test");
            fake.Fail(error);
            await run;

            string hint = Assert.Single(w.HintsForTests(), t => t.StartsWith("ダウンロードできませんでした: ", StringComparison.Ordinal));
            Assert.Contains(SetupWords.FailureSummary(error), hint, StringComparison.Ordinal);       // 全文
            Assert.Contains(SetupWords.FailureDetail(error), hint, StringComparison.Ordinal);        // 詳細
            Assert.Contains(SetupWords.ManualGuidance, w.TextsForTests());                            // 手で置く方法の案内は、行に出ている
        });
    }

    [Fact]
    public async Task The_worst_plan_sentence_fits_in_its_two_lines_without_being_cut()
    {
        // 見積もりの文が一番長い形(前回の続きがあり、空きが分かる)。2 行に収まる(切れない)
        await StaDispatcher.RunAsync(() =>
        {
            var fake = new FakeDownloads { Partial = 1_000_000_000, Free = 312_400_000_000 };
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            var root = (UIElement)w.Content;
            w.Content = null;
            var host = new Border { Child = root, Width = 820 };
            TestLook.Setup(host);
            host.Measure(new Size(820, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 820, Math.Ceiling(host.DesiredSize.Height)));
            host.UpdateLayout();
            // 区画の領域(全幅)と、「2 つとも」の領域(ボタンの左の、せまい幅)の、すべての見積もりの文
            TextBlock[] plans = [.. Descendants(root).OfType<TextBlock>().Where(t => t.Text.StartsWith("押すと、", StringComparison.Ordinal) && t.Text.Contains("前回の続き", StringComparison.Ordinal))];
            var probes = plans.Select(plan =>
            {
                var probe = new TextBlock { Text = plan.Text, TextWrapping = TextWrapping.Wrap, FontFamily = plan.FontFamily, FontSize = plan.FontSize, Width = plan.ActualWidth };
                probe.Measure(new Size(plan.ActualWidth, double.PositiveInfinity));
                return (plan.ActualWidth, probe.DesiredSize.Height);
            }).ToArray();
            host.Child = null;
            w.Content = root;

            Assert.Equal(3, plans.Length);   // 「2 つとも」+ 区画ごとの 2 つ
            foreach ((double width, double height) in probes)
            {
                Assert.True(width > 450, $"見積もりの文の幅 {width}");
                Assert.True(height <= 2 * SetupWindow.AreaLineHeight, $"見積もりの文が {height}px 要る(2 行 = {2 * SetupWindow.AreaLineHeight}px に収まらず、切れる)");
            }
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 測定用(画像)

    [Fact]
    public async Task Render_the_progress_states_offscreen()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OHANAMI_SETUP_SHOTS") == "1", "環境変数 OHANAMI_SETUP_SHOTS=1 が無い(%TEMP% の ohanami-setup-progress-shots に PNG を書く手元専用の測定)");
        string dir = Path.Combine(Path.GetTempPath(), "ohanami-setup-progress-shots");
        Directory.CreateDirectory(dir);
        var heights = new List<string>();
        await StaDispatcher.RunAsync(async () =>
        {
            void Shot(SetupWindow w, string name)
            {
                SetupWindowTests.Save(w, Path.Combine(dir, name + ".png"));
                heights.Add($"{name}\t{ContentHeight(w):0.0}");
            }

            // 押す前
            SetupWindow w0 = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads { Free = 312_400_000_000 });
            Shot(w0, "1-before");
            w0.Close();

            // 取っている最中: 1 つ目を置いて、2 つ目を取っている / 確かめている / 3 つ目を取っている
            var fake = new FakeDownloads { Free = 312_400_000_000 };
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            DateTime t = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
            w.Now = () => t;
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);
            fake.Report(P(ModelDownloadPhase.Connecting, EmbeddingFiles[0], 0, 0));
            Shot(w, "2-connecting-first");
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[0], 0, 900_000_000));
            t = t.AddSeconds(2);
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[0], 0, 1_000_000_000));
            Shot(w, "3-downloading-first");
            fake.Report(P(ModelDownloadPhase.Verifying, EmbeddingFiles[0], 0, 1_713_350_137));
            Shot(w, "4-verifying-first");
            fake.Report(P(ModelDownloadPhase.Completed, EmbeddingFiles[0], 0, 1_713_350_137));
            fake.Report(P(ModelDownloadPhase.Completed, EmbeddingFiles[1], 1, 1_713_910_619));
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[2], 2, 2_100_000_000));
            t = t.AddSeconds(2);
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[2], 2, 2_200_000_000));
            Shot(w, "5-two-done-third-downloading");
            fake.Report(P(ModelDownloadPhase.Completed, EmbeddingFiles[2], 2, 4_545_042_221));
            fake.Report(P(ModelDownloadPhase.Verifying, EmbeddingFiles[3], 3, EmbeddingTotal));
            Shot(w, "6-three-done-last-verifying");

            // 失敗(4 つ目で 404)。置き場には 3 つ置いてある
            current = WithEmbeddingPresent(EmbeddingFiles[0], EmbeddingFiles[1], EmbeddingFiles[2]);
            fake.Fail(new ModelDownloadException(ModelDownloadFailure.HttpStatus, "Core のメッセージ(テスト)", EmbeddingFiles[3], 404, "cdn.example.test"));
            await run;
            Shot(w, "7-failed-after-three");
            w.Close();

            // 失敗・長い文
            var f2 = new FakeDownloads { Free = 312_400_000_000 };
            SetupWindow w2 = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: f2);
            Task r2 = w2.StartDownloadAsync(ModelSets.Vlm);
            f2.Fail(Fail(ModelDownloadFailure.Network, "a-very-long-file-name-for-layout-measurement/with/deep/folders/model-00001-of-00009.gguf", host: "cdn-lfs-eu-1-with-a-very-long-host-name.example.test"));
            await r2;
            Shot(w2, "8-failed-long");
            w2.Close();

            // 空きが足りない・続きあり
            SetupWindow w3 = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads { Free = 2_000_000_000, Partial = 1_000_000_000 });
            Shot(w3, "9-no-space-with-part");
            w3.Close();

            // もう一方の組を取っている間の、取っていない側
            var f4 = new FakeDownloads { Free = 312_400_000_000 };
            SetupWindow w4 = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: f4);
            Task r4 = w4.StartDownloadAsync(ModelSets.Vlm);
            f4.Report(new ModelDownloadProgress(ModelDownloadPhase.Downloading, "Qwen3-VL-8B-Instruct-Q8_0.gguf", 0, 2, 2_000_000_000, 6_190_000_000));
            Shot(w4, "10-vlm-downloading-other-side");
            f4.Fail(Fail(ModelDownloadFailure.Network));
            await r4;
            w4.Close();
        });
        File.WriteAllLines(Path.Combine(dir, "heights.tsv"), heights);
        Assert.True(Directory.GetFiles(dir, "*.png").Length >= 10);
        double first = double.Parse(heights[0].Split('\t')[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.All(heights, h => Assert.Equal(first, double.Parse(h.Split('\t')[1], System.Globalization.CultureInfo.InvariantCulture), 3));
    }
}
