using System;
using System.Linq;
using System.Threading.Tasks;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// セットアップの画面の「2 つともダウンロード」。偽のダウンロードの口(<see cref="FakeDownloads"/>)で動かす(ネットワークには出ない)。確かめること:
/// 2 つの区画がどちらもそろっていないときだけ出る / 押すと、2 つの組を 1 回の呼び出しで渡す(区画ごとの 2 回ではない) /
/// 進み具合と「中止」は押した領域に出て、区画の表は「あり」に替わっていく / 中止のあと勝手に取り直さない /
/// 失敗・中止の文は、止まった時点で残っている組の領域に出る / 空きは、置き場のドライブごとに見る。
/// </summary>
public sealed class SetupDownloadBothTests
{
    private const ModelSets Both = SetupWindow.BothSets;

    internal static string[] EmbeddingFiles => [.. SetupWindowTests.BothMissing().Embedding.Files.Select(f => f.RelativePath)];

    internal static string[] VlmFiles => [.. SetupWindowTests.BothMissing().Vlm.Files.Select(f => f.RelativePath)];

    internal static ModelDownloadProgress P(ModelDownloadPhase phase, string file, int index, long done, long total = 10_770_000_000) =>
        new(phase, file, index, 6, done, total);

    internal static ModelDownloadException Fail(ModelDownloadFailure kind, string? file) =>
        new(kind, "Core のメッセージ(テスト)", file, 404, "cdn.example.test", null, null);

    /// <summary>失敗の文(「ダウンロードできませんでした: …」)の行。</summary>
    private static bool IsFailureLine(string text) => text.StartsWith(SetupWords.FailureHead, StringComparison.Ordinal);

    // ---------------------------------------------------------------- 押す前

    [Fact]
    public async Task Before_pressing_the_bulk_area_sits_above_the_sections_and_says_the_total_and_the_order()
    {
        await StaDispatcher.RunAsync(() =>
        {
            var fake = new FakeDownloads { Free = 312_400_000_000 };
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            string[] area = w.AreaTextsForTests(Both);

            Assert.Empty(fake.Calls);   // 窓を作っても、押すまでは取らない
            Assert.Contains(area, t => t.StartsWith("押すと、Hugging Face から 6 ファイル (合計 10.77 GB) を取って、置き場に置きます。置き場のドライブ (", StringComparison.Ordinal));
            Assert.Contains($"{ProcessingWords.StageName(JobKind.Embed)}のモデルから先に取ります", area);
            Assert.Equal(["2 つともダウンロード (10.77 GB)"], w.AreaButtonsForTests(Both));
            Assert.Equal([true], w.AreaButtonsEnabledForTests(Both));

            // 画面の上から順に: 導入の文 → 「2 つとも」の領域 → 特徴ベクトル検索のモデルの区画 → テキスト検索のモデルの区画
            string[] texts = w.TextsForTests();
            int lead = Array.IndexOf(texts, SetupWords.LeadMissing);
            int bulk = Array.FindIndex(texts, t => t.StartsWith("押すと、Hugging Face から 6 ファイル", StringComparison.Ordinal));
            int embed = Array.IndexOf(texts, SetupWords.SectionTitle(JobKind.Embed));
            int vlm = Array.IndexOf(texts, SetupWords.SectionTitle(JobKind.Vlm));
            Assert.True(0 <= lead && lead < bulk && bulk < embed && embed < vlm, $"順: 導入 {lead}、2 つとも {bulk}、特徴ベクトル {embed}、テキスト {vlm}");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_bulk_area_is_only_there_when_both_models_are_incomplete()
    {
        await StaDispatcher.RunAsync(() =>
        {
            Assert.True(SetupWindowTests.NewWindow(SetupWindowTests.BothMissing()).HasAreaForTests(Both));
            Assert.True(SetupWindowTests.NewWindow(SetupWindowTests.WithMismatch()).HasAreaForTests(Both));
            Assert.False(SetupWindowTests.NewWindow(SetupWindowTests.VlmOnlyMissing()).HasAreaForTests(Both));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_part_left_from_the_last_time_makes_the_bulk_button_say_continue()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: new FakeDownloads { Partial = 1_000_000_000 });

            Assert.Equal(["2 つとも続きからダウンロード (9.77 GB)"], w.AreaButtonsForTests(Both));
            Assert.Contains(w.AreaTextsForTests(Both), t => t.Contains("前回の続きがあるので、取るのは残りの 9.77 GB です", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 空き(置き場のドライブごと)

    private static SetupState OnDrives(string embeddingDirectory, string vlmDirectory)
    {
        SetupState s = SetupWindowTests.BothMissing();
        return new SetupState(s.Embedding with { Directory = embeddingDirectory }, s.Vlm with { Directory = vlmDirectory });
    }

    [Fact]
    public async Task On_one_drive_the_bulk_button_needs_the_sum_of_both_even_when_each_model_fits_alone()
    {
        await StaDispatcher.RunAsync(() =>
        {
            // 特徴ベクトル検索のモデル(約 4.65 GB)にも、テキスト検索のモデル(約 6.26 GB)にも足りるが、合わせた約 10.84 GB には足りない空き
            var fake = new FakeDownloads { Free = 8_000_000_000 };
            SetupWindow w = SetupWindowTests.NewWindow(OnDrives(@"C:\ohanami-test\a", @"C:\ohanami-test\b"), downloads: fake);

            Assert.Equal(2, w.EnabledButtonTextsForTests().Count(b => b.StartsWith(SetupWords.DownloadLabel, StringComparison.Ordinal)));
            Assert.Equal([false], w.AreaButtonsEnabledForTests(Both));
            long needed = fake.Plan(@"C:\ohanami-test\a", @"C:\ohanami-test\b", Both).RequiredFreeBytes;
            Assert.Contains(SetupWords.NoSpaceReason(needed, 8_000_000_000, @"C:\"), w.AreaTextsForTests(Both));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task On_two_drives_each_drive_is_checked_for_its_own_model_and_both_are_named_in_the_plan()
    {
        await StaDispatcher.RunAsync(() =>
        {
            // C: には特徴ベクトル検索のモデルのぶん、Z: にはテキスト検索のモデルのぶんが、それぞれ足りる(合計には足りない)
            long? Free(string dir) => dir.StartsWith("Z:", StringComparison.OrdinalIgnoreCase) ? 7_000_000_000 : 5_000_000_000;
            var fake = new FakeDownloads { FreeOf = Free };
            SetupWindow w = SetupWindowTests.NewWindow(OnDrives(@"C:\ohanami-test\a", @"Z:\ohanami-test\b"), downloads: fake);

            Assert.Equal([true], w.AreaButtonsEnabledForTests(Both));
            Assert.Contains(w.AreaTextsForTests(Both), t => t.Contains(@"置き場のドライブの空きは、C:\ が 5.00 GB、Z:\ が 7.00 GB です", StringComparison.Ordinal));

            // Z: が足りなければ、足りないドライブを名指しして、押せない
            fake.FreeOf = dir => dir.StartsWith("Z:", StringComparison.OrdinalIgnoreCase) ? 6_000_000_000 : 5_000_000_000;
            w = SetupWindowTests.NewWindow(OnDrives(@"C:\ohanami-test\a", @"Z:\ohanami-test\b"), downloads: fake);
            long vlmNeeded = fake.Plan(@"C:\ohanami-test\a", @"Z:\ohanami-test\b", ModelSets.Vlm).RequiredFreeBytes;
            Assert.Equal([false], w.AreaButtonsEnabledForTests(Both));
            Assert.Contains(SetupWords.NoSpaceReason(vlmNeeded, 6_000_000_000, @"Z:\"), w.AreaTextsForTests(Both));
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 押す

    [Fact]
    public async Task Pressing_the_bulk_button_makes_one_call_for_both_sets_and_the_progress_and_cancel_are_in_that_area()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);

            SetupWindowTests.Click(SetupWindowTests.ButtonOf(w, "2 つともダウンロード (10.77 GB)"));

            Assert.Equal([Both], fake.Calls);   // 区画ごとの 2 回ではなく、組を足した 1 回
            Assert.True(w.IsDownloading);
            Assert.Contains(SetupWords.Starting, w.AreaTextsForTests(Both));
            Assert.Equal([SetupWords.CancelLabel], w.AreaButtonsForTests(Both));
            // 区画ごとの領域は、押せないだけ(進み具合は出ない)
            Assert.Equal([SetupWords.BusyReason], w.AreaTextsForTests(ModelSets.FeatureVector));
            Assert.Equal([SetupWords.BusyReason], w.AreaTextsForTests(ModelSets.Vlm));
            Assert.Equal([false], w.AreaButtonsEnabledForTests(ModelSets.FeatureVector));
            Assert.Equal([false], w.AreaButtonsEnabledForTests(ModelSets.Vlm));
            Assert.DoesNotContain(w.EnabledButtonTextsForTests(), b => b.StartsWith(SetupWords.DownloadLabel, StringComparison.Ordinal));
            Assert.DoesNotContain(SetupWords.RecheckLabel, w.EnabledButtonTextsForTests());

            fake.Fail(Fail(ModelDownloadFailure.Network, EmbeddingFiles[0]));   // 後始末
            while (w.IsDownloading) await Task.Delay(10);
        });
    }

    [Fact]
    public async Task While_both_are_downloading_each_table_moves_to_present_and_a_finished_section_clears_its_download_area()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            Task run = w.StartDownloadAsync(Both);

            string[] embedding = EmbeddingFiles, vlm = VlmFiles;
            fake.Report(P(ModelDownloadPhase.Downloading, embedding[0], 0, 1_000_000_000));
            Assert.Equal(SetupWords.FileStateWhileDownloading(ModelDownloadPhase.Downloading), w.FileStatesForTests(ModelSets.FeatureVector)[0]);

            for (int i = 0; i < embedding.Length; i++) fake.Report(P(ModelDownloadPhase.Completed, embedding[i], i, 4_579_405_088));
            Assert.Equal(SetupWords.SetComplete, w.SetStateForTests(ModelSets.FeatureVector));      // 特徴ベクトル検索のモデルは、準備完了
            Assert.NotEqual(SetupWords.SetComplete, w.SetStateForTests(ModelSets.Vlm));
            Assert.Empty(w.AreaTextsForTests(ModelSets.FeatureVector));                             // そろった区画の、押せない「ダウンロード」は残さない
            Assert.Empty(w.AreaButtonsForTests(ModelSets.FeatureVector));
            Assert.Equal([SetupWords.BusyReason], w.AreaTextsForTests(ModelSets.Vlm));              // まだの区画は、そのまま
            Assert.Equal([SetupWords.CancelLabel], w.AreaButtonsForTests(Both));                    // 「2 つとも」の領域は、取っている間は進み具合と「中止」

            fake.Report(P(ModelDownloadPhase.Downloading, vlm[0], 4, 1_000_000_000));
            Assert.Equal(SetupWords.FileStateWhileDownloading(ModelDownloadPhase.Downloading), w.FileStatesForTests(ModelSets.Vlm)[0]);
            Assert.Contains("(5 / 6 ファイル)", w.AreaTextsForTests(Both)[0]);   // 5 番目のファイル(Core の番号は 0 始まり)

            fake.Fail(Fail(ModelDownloadFailure.Network, vlm[0]));   // 後始末
            await run;
        });
    }

    [Fact]
    public async Task A_second_press_while_both_are_downloading_does_nothing()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);

            Task first = w.StartDownloadAsync(Both);
            await w.StartDownloadAsync(ModelSets.FeatureVector);
            await w.StartDownloadAsync(Both);

            Assert.Equal([Both], fake.Calls);
            fake.Fail(Fail(ModelDownloadFailure.Network, EmbeddingFiles[0]));
            await first;
        });
    }

    [Fact]
    public async Task While_one_model_is_downloading_the_bulk_button_cannot_be_pressed()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);

            Task run = w.StartDownloadAsync(ModelSets.Vlm);

            Assert.Equal([SetupWords.BusyReason], w.AreaTextsForTests(Both));
            Assert.Single(w.AreaButtonsForTests(Both), SetupWindowTests.IsBothButton);
            Assert.Equal([false], w.AreaButtonsEnabledForTests(Both));
            fake.Fail(Fail(ModelDownloadFailure.Network, VlmFiles[0]));
            await run;
        });
    }

    [Fact]
    public async Task When_the_one_model_being_downloaded_is_done_the_bulk_area_is_cleared_because_it_can_no_longer_be_pressed()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            Task run = w.StartDownloadAsync(ModelSets.FeatureVector);
            string[] embedding = EmbeddingFiles;

            for (int i = 0; i < embedding.Length - 1; i++) fake.Report(P(ModelDownloadPhase.Completed, embedding[i], i, 4_000_000_000, 4_579_405_088));
            Assert.Equal([SetupWords.BusyReason], w.AreaTextsForTests(Both));   // まだ 1 つ残っている間は、押せないだけ
            fake.Report(P(ModelDownloadPhase.Completed, embedding[^1], embedding.Length - 1, 4_579_405_088, 4_579_405_088));

            Assert.Empty(w.AreaTextsForTests(Both));
            Assert.Empty(w.AreaButtonsForTests(Both));
            fake.Fail(Fail(ModelDownloadFailure.Network, embedding[0]));   // 後始末
            await run;
        });
    }

    // ---------------------------------------------------------------- 中止

    [Fact]
    public async Task Cancelling_both_says_so_in_the_bulk_area_and_does_not_start_again_and_the_next_press_continues()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            Task run = w.StartDownloadAsync(Both);
            fake.Report(P(ModelDownloadPhase.Downloading, EmbeddingFiles[0], 0, 1_000_000_000));

            SetupWindowTests.Click(SetupWindowTests.ButtonOf(w, SetupWords.CancelLabel));
            Assert.True(fake.LastToken.IsCancellationRequested);
            await run;

            Assert.False(w.IsDownloading);
            Assert.Contains(SetupWords.Cancelled, w.AreaTextsForTests(Both));
            Assert.DoesNotContain(SetupWords.Cancelled, w.AreaTextsForTests(ModelSets.FeatureVector));
            Assert.DoesNotContain(SetupWords.Cancelled, w.AreaTextsForTests(ModelSets.Vlm));
            Assert.Equal([Both], fake.Calls);   // 中止のあと、勝手に(2 つ目を、あるいは取り直しを)始めない
            Assert.Equal([true], w.AreaButtonsEnabledForTests(Both));

            fake.Partial = 1_000_000_000;   // 次の見積もりには、途中までのファイルが入る
            SetupWindow again = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            Assert.Equal(["2 つとも続きからダウンロード (9.77 GB)"], again.AreaButtonsForTests(Both));
            Task second = w.StartDownloadAsync(Both);
            Assert.Equal([Both, Both], fake.Calls);
            fake.Fail(Fail(ModelDownloadFailure.Network, EmbeddingFiles[0]));
            await second;
        });
    }

    // ---------------------------------------------------------------- 失敗

    [Fact]
    public async Task A_failure_in_the_first_model_is_shown_in_the_bulk_area_and_the_button_comes_back()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            Task run = w.StartDownloadAsync(Both);

            fake.Fail(Fail(ModelDownloadFailure.HttpStatus, EmbeddingFiles[1]));
            await run;

            Assert.Contains(w.AreaTextsForTests(Both), IsFailureLine);
            Assert.Contains(SetupWords.ManualGuidance, w.AreaTextsForTests(Both));
            Assert.DoesNotContain(w.AreaTextsForTests(ModelSets.FeatureVector), IsFailureLine);
            Assert.DoesNotContain(w.AreaTextsForTests(ModelSets.Vlm), IsFailureLine);
            Assert.Equal(1, w.TextsForTests().Count(IsFailureLine));
            Assert.Equal([true], w.AreaButtonsEnabledForTests(Both));   // 押し直せる
            Assert.Equal([Both], fake.Calls);                           // 1 つ目の失敗のあと、2 つ目は始まらない
        });
    }

    [Fact]
    public async Task A_failure_midway_through_the_text_search_model_is_shown_in_that_section_because_the_bulk_area_is_gone()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            Task run = w.StartDownloadAsync(Both);
            string[] embedding = EmbeddingFiles, vlm = VlmFiles;
            for (int i = 0; i < embedding.Length; i++) fake.Report(P(ModelDownloadPhase.Completed, embedding[i], i, 4_579_405_088));
            fake.Report(P(ModelDownloadPhase.Downloading, vlm[0], 4, 5_000_000_000));

            current = SetupWindowTests.VlmOnlyMissing();   // 置き場には、特徴ベクトル検索のモデルが置かれている
            fake.Fail(Fail(ModelDownloadFailure.HttpStatus, vlm[0]));
            await run;

            Assert.False(w.HasAreaForTests(Both));   // 確かめ直した画面に、「2 つとも」の領域は無い
            Assert.Contains(w.AreaTextsForTests(ModelSets.Vlm), IsFailureLine);
            Assert.Contains(w.AreaTextsForTests(ModelSets.Vlm), t => t.Contains(vlm[0], StringComparison.Ordinal));
            Assert.Contains(SetupWords.ManualGuidance, w.AreaTextsForTests(ModelSets.Vlm));
            Assert.Equal(1, w.TextsForTests().Count(IsFailureLine));
            Assert.Equal([Both], fake.Calls);
            Assert.Single(w.EnabledButtonTextsForTests(), b => b.StartsWith(SetupWords.DownloadLabel, StringComparison.Ordinal));   // 残りの区画から、押し直せる
        });
    }

    [Fact]
    public async Task A_cancel_midway_through_the_text_search_model_is_said_in_that_section()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupState current = SetupWindowTests.BothMissing();
            SetupWindow w = SetupWindowTests.NewWindow(current, () => current, downloads: fake);
            Task run = w.StartDownloadAsync(Both);
            string[] embedding = EmbeddingFiles, vlm = VlmFiles;
            for (int i = 0; i < embedding.Length; i++) fake.Report(P(ModelDownloadPhase.Completed, embedding[i], i, 4_579_405_088));
            fake.Report(P(ModelDownloadPhase.Downloading, vlm[0], 4, 5_000_000_000));

            current = SetupWindowTests.VlmOnlyMissing();
            SetupWindowTests.Click(SetupWindowTests.ButtonOf(w, SetupWords.CancelLabel));
            await run;

            Assert.Contains(SetupWords.Cancelled, w.AreaTextsForTests(ModelSets.Vlm));
            Assert.Equal([Both], fake.Calls);
        });
    }

    // ---------------------------------------------------------------- 閉じるとき

    [Fact]
    public async Task Exit_while_both_are_downloading_asks_first_and_closes_only_after_the_cancel_is_done()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var fake = new FakeDownloads();
            SetupWindow w = SetupWindowTests.NewWindow(SetupWindowTests.BothMissing(), downloads: fake);
            int closed = 0, asked = 0;
            w.CloseWindow = () => closed++;
            w.ConfirmAbort = () => { asked++; return true; };
            Task run = w.StartDownloadAsync(Both);

            w.RequestExit();

            Assert.Equal((1, 0), (asked, closed));
            Assert.True(fake.LastToken.IsCancellationRequested);
            await run;
            Assert.Equal(1, closed);
            Assert.Equal([Both], fake.Calls);
        });
    }

    // ---------------------------------------------------------------- カタログ

    [Fact]
    public void The_bulk_label_and_the_order_line_use_the_words_of_the_screen()
    {
        Assert.Equal("特徴ベクトル検索のモデルから先に取ります", SetupWords.BothOrder);
        Assert.Equal("ダウンロードしている間は、押せません", SetupWords.BusyReason);
    }
}
