using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// セットアップの画面の、ダウンロード。偽のダウンロードの口(<see cref="FakeDownloads"/>)で動かす(ネットワークには出ない)。確かめること:
/// 押す前は何も取らず、何を・どこから・何 GB・空きを言う / 押すと 1 つずつ取る(進み具合・中止・ほかのボタンは押せない) / 終わったら自動で確かめ直す /
/// 失敗は種類ごとの文と手で置く方法 / 中止は途中までを残す / 取っている最中に閉じようとすると確かめる。
/// </summary>
public sealed class SetupDownloadTests
{
    private static ModelDownloadProgress Progress(ModelDownloadPhase phase, long done, long total = 4_579_405_088, int index = 2, int count = 4, string file = "textual/model.onnx_data") =>
        new(phase, file, index, count, done, total);

    private static ModelDownloadException Fail(ModelDownloadFailure kind, string? file = "textual/model.onnx_data", int? status = null, string? host = null, long? needed = null, long? available = null) =>
        new(kind, "Core のメッセージ(テスト)", file, status, host, needed, available);

    // ---------------------------------------------------------------- 押す前

    [Fact]
    public async Task Before_the_button_is_pressed_nothing_is_downloaded_and_the_plan_is_in_one_line()
    {
        await StaDispatcher.RunAsync(() =>
        {
            var fake = new FakeDownloads { Free = 312_400_000_000 };
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            string[] texts = w.TextsForTests();

            Assert.Empty(fake.Calls);   // 窓を作っても、押すまでは、取る口を呼ばない(通信 0)
            Assert.Contains(texts, t => t.StartsWith("押すと、Hugging Face から 4 ファイル (合計 4.58 GB) を取って、置き場に置きます。置き場のドライブ (C:", StringComparison.Ordinal) && t.Contains("の空きは 312.40 GB です", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.StartsWith("押すと、Hugging Face から 2 ファイル (合計 6.19 GB)", StringComparison.Ordinal));
            Assert.Contains("ダウンロード (4.58 GB)", w.EnabledButtonTextsForTests());
            Assert.Contains("ダウンロード (6.19 GB)", w.EnabledButtonTextsForTests());
            Assert.Contains(SetupWords.Promise, texts);   // 約束の文は、ダウンロードのボタンの近くに出る
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_part_left_from_the_last_time_makes_the_button_say_continue_and_the_plan_says_how_much_is_left()
    {
        await StaDispatcher.RunAsync(() =>
        {
            var fake = new FakeDownloads { Partial = 1_000_000_000 };
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.VlmOnlyMissing(), downloads: fake);

            Assert.Contains(w.TextsForTests(), t => t.Contains("前回の続きがあるので、取るのは残りの 5.19 GB です", StringComparison.Ordinal));
            Assert.Contains("続きからダウンロード (5.19 GB)", w.EnabledButtonTextsForTests());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Not_enough_free_space_is_said_before_pressing_and_the_button_cannot_be_pressed()
    {
        await StaDispatcher.RunAsync(() =>
        {
            var fake = new FakeDownloads { Free = 2_000_000_000 };
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);

            Assert.Contains(w.TextsForTests(), t => t.StartsWith("空きが足りません: 要るのは 4.65 GB、置き場のドライブ (C:", StringComparison.Ordinal));
            Assert.DoesNotContain(w.EnabledButtonTextsForTests(), b => b.StartsWith(SetupWords.DownloadLabel, StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_complete_model_has_no_download_area()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.VlmOnlyMissing(), downloads: new FakeDownloads());

            Assert.Single(w.ButtonTextsForTests(), b => b.StartsWith(SetupWords.DownloadLabel, StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_lead_names_both_ways_to_get_the_files_download_or_put_them_by_hand()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads());

            Assert.Contains(w.TextsForTests(), t => t.Contains("「ダウンロード」を押すか、下の置き場に表と同じ名前・同じ大きさのファイルを置いてから「確かめ直す」を押してください", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 取っている最中

    [Fact]
    public async Task Pressing_downloads_one_set_at_a_time_and_shows_the_progress_with_a_cancel_button()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            DateTime t = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
            w.Now = () => t;

            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);

            Assert.Equal([ModelSets.FeatureVector], fake.Calls);
            Assert.True(w.IsDownloading);
            Assert.Contains(SetupWords.Starting, w.TextsForTests());
            // 取っている間は、もう一方のボタン・確かめ直す・このまま始めるは押せない。中止と終了は押せる
            Assert.DoesNotContain(w.EnabledButtonTextsForTests(), b => b.StartsWith(SetupWords.DownloadLabel, StringComparison.Ordinal));
            Assert.DoesNotContain(SetupWords.RecheckLabel, w.EnabledButtonTextsForTests());
            Assert.Contains(SetupWords.CancelLabel, w.EnabledButtonTextsForTests());
            Assert.Contains(SetupWords.ExitLabel, w.EnabledButtonTextsForTests());
            Assert.Contains(SetupWords.BusyReason, w.TextsForTests());

            fake.Report(Progress(ModelDownloadPhase.Connecting, 0));
            Assert.Contains("接続中… textual/model.onnx_data (3 / 4 ファイル)", w.TextsForTests());

            fake.Report(Progress(ModelDownloadPhase.Downloading, 1_000_000_000));
            t = t.AddSeconds(2);
            fake.Report(Progress(ModelDownloadPhase.Downloading, 1_100_000_000));
            Assert.Contains("取得中: textual/model.onnx_data (3 / 4 ファイル)", w.TextsForTests());
            Assert.Contains(w.TextsForTests(), s => s.StartsWith("1.10 GB / 4.58 GB (24%)、50.0 MB/秒、残り約 ", StringComparison.Ordinal));
            Assert.InRange(w.ProgressFraction!.Value, 0.24, 0.25);

            fake.Report(Progress(ModelDownloadPhase.Verifying, 4_579_405_088));
            Assert.Contains(w.TextsForTests(), s => s.StartsWith("確かめ中: textual/model.onnx_data (3 / 4 ファイル。取ったファイルの中身", StringComparison.Ordinal));
            Assert.Contains("4.58 GB / 4.58 GB (100%)", w.TextsForTests());   // 確かめている間は、速さを出さない

            fake.Fail(Fail(ModelDownloadFailure.Network));   // 後始末
            await run;
        });
    }

    [Fact]
    public async Task A_second_press_while_downloading_does_nothing()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);

            Task first = w.StartDownloadAsync(ModelSets.FeatureVector);
            await w.StartDownloadAsync(ModelSets.Vlm);

            Assert.Equal([ModelSets.FeatureVector], fake.Calls);
            fake.Fail(Fail(ModelDownloadFailure.Network));
            await first;
        });
    }

    [Fact]
    public async Task An_automatic_check_while_downloading_is_ignored()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            int inspected = 0;
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => { inspected++; return current; }, downloads: fake);
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);

            w.Recheck(manual: false);

            Assert.Equal(0, inspected);
            fake.Fail(Fail(ModelDownloadFailure.Network));
            await run;
        });
    }

    // ---------------------------------------------------------------- 終わったら

    [Fact]
    public async Task When_the_download_finishes_and_everything_is_there_the_window_closes_with_ready()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            bool closed = false;
            w.CloseWindow = () => closed = true;
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector | ModelSets.Vlm);

            current = SetupWindowTests.AllReady();
            fake.Complete();
            await run;

            Assert.True(closed);
            Assert.Equal(SetupChoice.Ready, w.Choice);
            Assert.False(w.IsDownloading);
        });
    }

    [Fact]
    public async Task When_only_the_first_model_is_done_the_window_stays_and_offers_the_rest()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            bool closed = false;
            w.CloseWindow = () => closed = true;
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);

            current = SetupWindowTests.VlmOnlyMissing();
            fake.Complete();
            await run;

            Assert.False(closed);
            Assert.Contains(SetupWords.HeadingVlmMissing, w.TextsForTests());
            Assert.Contains(SetupWords.ContinueLabel, w.EnabledButtonTextsForTests());
            Assert.Contains("ダウンロード (6.19 GB)", w.EnabledButtonTextsForTests());
            Assert.Contains(SetupWords.RecheckLabel, w.EnabledButtonTextsForTests());
        });
    }

    // ---------------------------------------------------------------- 失敗・中止

    [Fact]
    public async Task A_failure_is_shown_in_its_section_with_the_status_code_the_host_and_the_manual_way_and_the_button_comes_back()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);

            fake.Fail(Fail(ModelDownloadFailure.HttpStatus, status: 404, host: "cdn.example.test"));
            await run;

            string[] texts = w.TextsForTests();
            Assert.Contains("ダウンロードできませんでした: textual/model.onnx_data: サーバーが状態コード 404 を返しました (接続先: cdn.example.test)。取得元で、このファイルを取れない状態です", texts);
            Assert.Contains(SetupWords.ManualGuidance, texts);
            Assert.Contains(w.HintsForTests(), t => t.StartsWith("ダウンロードできませんでした: ", StringComparison.Ordinal) && t.Contains("\n詳細: Core のメッセージ", StringComparison.Ordinal));   // 詳細は、失敗の行のヒントで読む
            Assert.False(w.IsDownloading);
            Assert.Equal(2, w.EnabledButtonTextsForTests().Count(b => b.StartsWith(SetupWords.DownloadLabel, StringComparison.Ordinal)));   // 押し直せる
        });
    }

    [Fact]
    public async Task Each_kind_of_failure_has_its_own_flat_sentence_with_what_the_user_needs()
    {
        await StaDispatcher.RunAsync(() =>
        {
            string Sentence(ModelDownloadException e) => SetupWords.FailureSummary(e);

            Assert.Contains("状態コード 503", Sentence(Fail(ModelDownloadFailure.HttpStatus, status: 503, host: "h.test")));
            Assert.Contains("しばらくしてから", Sentence(Fail(ModelDownloadFailure.HttpStatus, status: 429)));
            Assert.Contains("取得元で、このファイルを取れない", Sentence(Fail(ModelDownloadFailure.HttpStatus, status: 403)));
            Assert.Contains("(接続先: h.test)", Sentence(Fail(ModelDownloadFailure.UnexpectedResponse, host: "h.test")));
            Assert.Contains("大きさが、定義と違いました", Sentence(Fail(ModelDownloadFailure.SizeMismatch)));
            Assert.Contains("textual/model.onnx_data: 取ったファイルの中身が、定義と違いました (SHA-256 が一致しません)", Sentence(Fail(ModelDownloadFailure.HashMismatch)));
            Assert.Contains("データが届かなくなりました", Sentence(Fail(ModelDownloadFailure.Stalled)));
            Assert.Contains("サーバーに接続できませんでした (接続先: h.test)。回線やファイアウォール", Sentence(Fail(ModelDownloadFailure.Network, host: "h.test")));
            Assert.Equal("置き場の空きが足りません (要るのは 4.65 GB、空きは 2.10 GB)。空きを作ってから、もう一度試してください",
                Sentence(Fail(ModelDownloadFailure.InsufficientSpace, file: null, needed: 4_646_000_000, available: 2_100_000_000)));
            Assert.Contains("置き場に書けませんでした", Sentence(Fail(ModelDownloadFailure.Io)));
            Assert.StartsWith("ファイル: ", Sentence(Fail(ModelDownloadFailure.Network, file: null)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Cancelling_keeps_the_part_and_the_next_press_continues()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);
            fake.Report(Progress(ModelDownloadPhase.Downloading, 1_000_000_000));

            // 「中止」を押す(ボタンのハンドラ。中止の合図が出る)
            System.Windows.Controls.Button cancel = FindCancel(w);
            cancel.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(fake.LastToken.IsCancellationRequested);
            Assert.Equal(SetupWords.Cancelling, (string)cancel.Content);
            await run;

            Assert.False(w.IsDownloading);
            Assert.Contains(SetupWords.Cancelled, w.TextsForTests());
            Assert.Equal([ModelSets.FeatureVector], fake.Calls);   // 中止のあと、勝手に取り直さない

            fake.Partial = 1_000_000_000;   // 次の見積もりには、途中までのファイルが入る
            Task second = w.StartDownloadAsync(ModelSets.FeatureVector);
            Assert.Equal(2, fake.Calls.Count);
            fake.Fail(Fail(ModelDownloadFailure.Network));
            await second;
        });
    }

    private static System.Windows.Controls.Button FindCancel(SetupWindow w)
    {
        System.Windows.Controls.Button? found = null;
        void Walk(System.Windows.DependencyObject d)
        {
            foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(d))
            {
                if (child is System.Windows.Controls.Button b && b.Content is string s && s == SetupWords.CancelLabel) found = b;
                if (child is System.Windows.DependencyObject o) Walk(o);
            }
        }
        Walk((System.Windows.DependencyObject)w.Content);
        return found ?? throw new InvalidOperationException("中止のボタンが無い");
    }

    // ---------------------------------------------------------------- 閉じるとき

    [Fact]
    public async Task Exit_while_downloading_asks_first_and_closes_only_after_the_cancel_is_done()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            int closed = 0;
            w.CloseWindow = () => closed++;
            int asked = 0;
            bool answer = false;
            w.ConfirmAbort = () => { asked++; return answer; };
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);

            w.RequestExit();   // 「いいえ」: 続ける
            Assert.Equal((1, 0), (asked, closed));
            Assert.False(fake.LastToken.IsCancellationRequested);

            answer = true;
            w.RequestExit();   // 「はい」: 中止を始める。閉じるのは、中止が済んでから
            Assert.Equal((2, 0), (asked, closed));
            Assert.True(fake.LastToken.IsCancellationRequested);
            await run;

            Assert.Equal(1, closed);
            Assert.Equal(SetupChoice.Exit, w.Choice);
        });
    }

    [Fact]
    public async Task Answering_yes_after_the_download_has_already_ended_closes_at_once_and_leaves_no_pending_close()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            int closed = 0;
            w.CloseWindow = () => closed++;
            // 確かめの窓を出している間に、取り終わる(窓の裏でも取るのは進む)。窓の裏の動きは、メッセージを回して進める
            w.ConfirmAbort = () =>
            {
                fake.Complete();
                w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                return true;
            };
            Task first = w.StartDownloadAsync(ModelSets.FeatureVector);

            w.RequestExit();   // 「はい」を押したときには、もう取っていない: 中止するものが無いので、そのまま閉じる

            await first;
            Assert.Equal(1, closed);
            Assert.Equal(SetupChoice.Exit, w.Choice);

            // 旗が残っていないこと: 次にもう一方を取り終えても、閉じる処理は走らない
            w.ConfirmAbort = () => throw new InvalidOperationException("聞かない");
            Task second = w.StartDownloadAsync(ModelSets.Vlm);
            fake.Complete();
            await second;
            Assert.Equal(1, closed);
        });
    }

    [Fact]
    public async Task Exit_without_a_download_closes_at_once_without_asking()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing());
            int closed = 0;
            w.CloseWindow = () => closed++;
            w.ConfirmAbort = () => throw new InvalidOperationException("聞かない");

            w.RequestExit();

            Assert.Equal(1, closed);
            Assert.Equal(SetupChoice.Exit, w.Choice);
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 速さ・語

    [Fact]
    public void The_rate_is_the_average_over_the_last_seconds_and_unknown_until_a_second_has_passed()
    {
        var rate = new TransferRate();
        DateTime t = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

        rate.Add(t, 0);
        Assert.Null(rate.BytesPerSecond);
        rate.Add(t.AddSeconds(0.5), 10_000_000);
        Assert.Null(rate.BytesPerSecond);   // 1 秒に満たない
        rate.Add(t.AddSeconds(2), 40_000_000);
        Assert.Equal(20_000_000, rate.BytesPerSecond!.Value, 1);

        for (int i = 3; i <= 20; i++) rate.Add(t.AddSeconds(i), 40_000_000 + (i - 2) * 100_000_000L);   // 速くなった: 古い記録は捨てて、直近の平均になる
        Assert.Equal(100_000_000, rate.BytesPerSecond!.Value, 1);

        rate.Reset();
        Assert.Null(rate.BytesPerSecond);
    }

    [Fact]
    public void The_amount_line_leaves_out_the_speed_when_it_is_not_known_or_not_downloading()
    {
        ModelDownloadProgress p = Progress(ModelDownloadPhase.Downloading, 2_289_702_544);

        Assert.Equal("2.29 GB / 4.58 GB (50%)", SetupWords.AmountLine(p, null));
        Assert.StartsWith("2.29 GB / 4.58 GB (50%)、100.0 MB/秒、残り約 ", SetupWords.AmountLine(p, 100_000_000), StringComparison.Ordinal);
        Assert.Equal("2.29 GB / 4.58 GB (50%)", SetupWords.AmountLine(Progress(ModelDownloadPhase.Verifying, 2_289_702_544), 100_000_000));
    }

    [Fact]
    public async Task Building_the_windows_never_calls_the_download_even_for_every_state()
    {
        await StaDispatcher.RunAsync(() =>
        {
            var fake = new FakeDownloads();
            foreach (SetupState state in new[] { SetupWindowTests.BothMissing(), SetupWindowTests.VlmOnlyMissing(), SetupWindowTests.WithMismatch() })
            {
                SetupWindow w = SetupWindowTests.NewWindow(state, downloads: fake);
                w.Recheck(manual: true);
                w.Close();
            }

            Assert.Empty(fake.Calls);
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 測定用(ダウンロードの各状態の画像)

    [Fact(Skip = "測定用(ダウンロードの画面)。手元で Skip を外して回すと、%TEMP% の ohanami-download-shots に、押す前・取得中・確かめ中・失敗(404・空き不足・ハッシュ違い)・途中までの 1 つ目が済んだ状態の PNG を書く。通常の回帰テストには入れない")]
    public async Task Render_the_download_states_offscreen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ohanami-download-shots");
        Directory.CreateDirectory(dir);
        await StaDispatcher.RunAsync(async () =>
        {
            // 押す前(ふつう / 前回の続きがあって、空きが足りない)
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads { Free = 312_400_000_000 });
            SetupWindowTests.Save(w, Path.Combine(dir, "1-before.png"));
            w.Close();
            w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads { Free = 2_000_000_000, Partial = 1_000_000_000 });
            SetupWindowTests.Save(w, Path.Combine(dir, "2-before-no-space.png"));
            w.Close();

            // 取得中・確かめ中
            var fake = new FakeDownloads { Free = 312_400_000_000 };
            SetupState current = SetupWindowTests.BothMissing();
            w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            DateTime t = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
            w.Now = () => t;
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);
            fake.Report(Progress(ModelDownloadPhase.Downloading, 1_800_000_000));
            t = t.AddSeconds(3);
            fake.Report(Progress(ModelDownloadPhase.Downloading, 2_100_000_000));
            SetupWindowTests.Save(w, Path.Combine(dir, "3-downloading.png"));
            fake.Report(Progress(ModelDownloadPhase.Verifying, 4_579_405_088L, index: 3, file: "textual/tokenizer.json"));
            SetupWindowTests.Save(w, Path.Combine(dir, "4-verifying.png"));

            // 1 つ目が済んで、VLM だけ残った状態
            current = SetupWindowTests.VlmOnlyMissing();
            fake.Complete();
            await run;
            SetupWindowTests.Save(w, Path.Combine(dir, "5-first-done.png"));
            w.Close();

            // 失敗 3 通り
            foreach ((string name, ModelDownloadException e) in new[]
            {
                ("6-fail-404", Fail(ModelDownloadFailure.HttpStatus, file: "visual/model.onnx", status: 404, host: "cdn.example.test")),
                ("7-fail-space", Fail(ModelDownloadFailure.InsufficientSpace, file: null, needed: 4_646_000_000, available: 2_100_000_000)),
                ("8-fail-hash", Fail(ModelDownloadFailure.HashMismatch, file: "textual/model.onnx_data")),
            })
            {
                var f = new FakeDownloads { Free = 312_400_000_000 };
                w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: f);
                Task r = w.StartDownloadAsync(ModelSets.FeatureVector);
                f.Fail(e);
                await r;
                SetupWindowTests.Save(w, Path.Combine(dir, name + ".png"));
                w.Close();
            }
        });
        Assert.Equal(8, Directory.GetFiles(dir, "*.png").Length);
    }
}
