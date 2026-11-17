using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 左パネル(ADR 0039): 開始 / 一時停止 / 再開の 1 つのボタンと二度押しの鍵、状態の文(止まっている・待機中・一時停止中)、
/// 「インデックスの状態…」の隣の 1 行(0 のものは書かない)、フォルダの行の右クリック(取り込みを止める / 再開する)、段階の名前を 1 か所から作ること。
/// </summary>
public sealed class LeftPanelTests
{
    private static IndexState State(
        IndexRunState run, int embedFailed = 0, int vlmFailed = 0, int skipped = 0, int embedOutdated = 0, int vlmOutdated = 0) =>
        new(run, null,
            [new StageSummary(Stage.Embed, 0, 5, embedFailed, embedOutdated), new StageSummary(Stage.Ocr, 0, 5, vlmFailed, vlmOutdated), new StageSummary(Stage.Caption, 0, 5, vlmFailed, vlmOutdated)],
            EngineStatus.NotCreated, null, null, null, null, null, skipped);

    private static Task<OhanamiCore> OpenCoreAsync(TempDirectory temp)
    {
        string models = temp.File("no-models");
        Directory.CreateDirectory(models);
        var options = new CoreOptions(temp.File("data/ohanami.db"), temp.File("thumbs"), models, AppContext.BaseDirectory, new TestLog(), null, WatchFolders: false);
        return OhanamiCore.OpenAsync(options, configureNative: false, time: null, CancellationToken.None);
    }

    private static string MakeFolder(TempDirectory temp, string name, int count)
    {
        string dir = temp.File(name);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < count; i++)
            AgedFile.Write(Path.Combine(dir, $"{name}-{i}.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, (byte)i, (byte)name[0], 3, 4, 5, 6, 7, 8]);
        return dir;
    }

    // ---------------------------------------------------------------- 1 つのボタンと二度押しの鍵

    [Fact]
    public async Task After_a_press_the_one_button_stays_locked_until_a_different_state_arrives()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            panel.Attach(core);
            panel.ShowForTests(State(IndexRunState.Idle));
            Assert.True(panel.CanRun);
            Assert.Equal(ProcessingWords.StartLabel, panel.RunButtonText);

            panel.LockRunButtonForTests(IndexRunState.Idle);                           // 押した直後(開始を押した)
            Assert.False(panel.CanRun);
            panel.ShowForTests(State(IndexRunState.Idle));                               // まだ同じ状態: 押せない(開始 → すぐ一時停止、にならない)
            Assert.False(panel.CanRun);

            panel.ShowForTests(State(IndexRunState.Running));                            // 次の状態が届いた: 押せる(いまは「一時停止」)
            Assert.True(panel.CanRun);
            Assert.Equal(ProcessingWords.PauseLabel, panel.RunButtonText);
        });
    }

    [Fact]
    public async Task A_second_press_while_locked_does_nothing()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var log = new TestLog();
            var panel = new IndexPanel(log, _ => { }, () => false);
            panel.Attach(core);
            panel.ShowForTests(State(IndexRunState.Running));
            panel.LockRunButtonForTests(IndexRunState.Running);

            await panel.ToggleRunAsync();

            Assert.DoesNotContain(log.Messages, m => m.Contains("ジョブ:", StringComparison.Ordinal));      // 鍵がかかっている間は、何も起こらない
        });
    }

    [Fact]
    public async Task A_press_that_fails_without_changing_the_state_does_not_leave_the_button_locked()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false);
            panel.Attach(core);
            panel.ShowForTests(State(IndexRunState.Idle));

            await panel.ToggleRunAsync();                                               // この Core には VLM が無いので、開始は失敗する(状態は止まったまま)

            Assert.Contains(notices, n => n.StartsWith("インデックス作成を開始できませんでした: ", StringComparison.Ordinal));
            Assert.True(panel.CanRun);                                                  // 失敗しても、押せないまま残らない
        });
    }

    // ---------------------------------------------------------------- 状態の文

    [Theory]
    [InlineData(IndexRunState.Idle, "停止中 (開始を押すまで作りません)")]
    [InlineData(IndexRunState.Waiting, "待機中 (新しい画像が来たら続けます)")]
    [InlineData(IndexRunState.Pausing, "一時停止の途中…")]
    [InlineData(IndexRunState.Paused, "一時停止中 (再開を押すまで作りません)")]
    [InlineData(IndexRunState.Failed, "停止中 (失敗で止まりました)")]
    public void The_state_sentence_says_what_the_job_is_doing_including_stopped(IndexRunState run, string sentence)
    {
        string text = IndexStatusText.Build(State(run));

        Assert.EndsWith(sentence, text, StringComparison.Ordinal);                     // 段階の件数の行のあとに、状態の文
        Assert.StartsWith("特徴ベクトル検索: 5 / 5 枚", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_running_line_names_the_stage_by_the_search_it_makes()
    {
        var running = new IndexState(IndexRunState.Running, JobKind.Vlm, [], EngineStatus.NotCreated, null, null, 0.5, null, null, 0);

        Assert.Contains("実行中: テキスト検索のインデックス作成 (0.5 枚/秒)", IndexStatusText.Build(running), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 「インデックスの状態…」の隣の 1 行

    [Fact]
    public void The_summary_line_leaves_out_what_is_zero_and_is_empty_when_everything_is_zero()
    {
        ProcessingSnapshot Of(IndexState st, int? missing) => ProcessingSnapshot.Build(st, missing, open: true);

        Assert.Equal("", ProcessingWords.LeftSummary(Of(State(IndexRunState.Idle), 0)));
        Assert.Equal("", ProcessingWords.LeftSummary(Of(State(IndexRunState.Idle), null)));              // まだ数えていない
        Assert.Equal("失敗 3", ProcessingWords.LeftSummary(Of(State(IndexRunState.Idle, embedFailed: 2, vlmFailed: 1), 0)));                // 2 つの段階の合計
        Assert.Equal("失敗 3・フォルダに無い 5", ProcessingWords.LeftSummary(Of(State(IndexRunState.Idle, embedFailed: 3), 5)));
        Assert.Equal("失敗 1・飛ばした 2・要再作成 7・フォルダに無い 5", ProcessingWords.LeftSummary(Of(State(IndexRunState.Idle, vlmFailed: 1, skipped: 2, embedOutdated: 3, vlmOutdated: 4), 5)));
    }

    [Fact]
    public async Task The_summary_line_is_shown_only_when_something_is_not_zero_and_the_state_button_is_for_the_open_core()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            Assert.False(panel.CanOpenProcessingState);                                  // Core を開く前は押せない
            panel.Attach(core);
            Assert.True(panel.CanOpenProcessingState);
            Assert.Equal("", panel.SummaryText);

            panel.ShowForTests(State(IndexRunState.Idle, embedFailed: 3), missing: 5);

            Assert.Equal("失敗 3・フォルダに無い 5", panel.SummaryText);
            panel.ShowForTests(State(IndexRunState.Idle), missing: 0);
            Assert.Equal("", panel.SummaryText);
        });
    }

    // ---------------------------------------------------------------- フォルダの行の右クリック

    [Fact]
    public async Task The_folder_row_menu_says_stop_or_resume_the_import_and_a_stopped_folder_is_marked()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 2));
            await core.Indexer.ScanAsync([folder.Id]);
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            panel.Attach(core);
            Assert.Equal([ProcessingWords.FolderStopImport], panel.FolderMenuHeaders);
            Assert.DoesNotContain(ProcessingWords.FolderStoppedMark, panel.FolderTexts.Single(), StringComparison.Ordinal);

            await panel.ToggleFolderAsync(core.Folders.List().Single());                  // メニューの「取り込みを止める」

            Assert.Equal([ProcessingWords.FolderResumeImport], panel.FolderMenuHeaders);
            Assert.StartsWith(ProcessingWords.FolderStoppedMark, panel.FolderTexts.Single(), StringComparison.Ordinal);
            await panel.ToggleFolderAsync(core.Folders.List().Single());                  // 「取り込みを再開する」
            Assert.Equal([ProcessingWords.FolderStopImport], panel.FolderMenuHeaders);
        });
    }

    [Fact]
    public async Task The_check_now_item_scans_only_that_folder_and_a_stopped_folder_is_not_scanned()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            await core.Folders.AddAsync(MakeFolder(temp, "a", 2));
            await core.Folders.AddAsync(MakeFolder(temp, "b", 3));
            var finished = new List<ScanSummary>();
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            panel.ScanFinished += finished.Add;
            panel.Attach(core);

            IReadOnlyList<System.Windows.Controls.MenuItem> items = panel.FolderCheckNowItems();
            Assert.Equal([ProcessingWords.FolderCheckNow, ProcessingWords.FolderCheckNow], items.Select(i => (string)i.Header));
            Assert.All(items, i => Assert.True(i.IsEnabled));
            Assert.All(items, i => Assert.Equal(ProcessingWords.FolderCheckNowHint, i.ToolTip));

            await panel.ScanFolderNowAsync(core.Folders.List()[0]);                         // 「今すぐ変化を確かめる」(1 つ目のフォルダだけ)
            Assert.Equal(2, Assert.Single(finished).Added);                                   // 2 つ目(3 枚)は走査していない

            FolderInfo second = core.Folders.List()[1];
            await panel.ToggleFolderAsync(second);                                            // 2 つ目の取り込みを止める
            items = panel.FolderCheckNowItems();
            Assert.True(items[0].IsEnabled);
            Assert.False(items[1].IsEnabled);                                                 // 止めているフォルダは押せない(Core は、指定されれば止めていても走査してしまうため)
            Assert.Equal(ProcessingWords.FolderCheckNowStoppedReason, items[1].ToolTip);
            finished.Clear();

            await panel.ScanFolderNowAsync(core.Folders.List()[1]);                         // 呼ばれても、止めているフォルダは走査しない
            Assert.Empty(finished);
        });
    }

    // ---------------------------------------------------------------- 段階の名前は 1 か所から

    [Fact]
    public void The_stage_names_are_made_in_one_place_and_every_other_stage_wording_uses_them()
    {
        Assert.Equal("特徴ベクトル検索", ProcessingWords.StageName(JobKind.Embed));
        Assert.Equal("テキスト検索", ProcessingWords.StageName(JobKind.Vlm));
        Assert.Equal(ProcessingWords.StageName(JobKind.Embed), ProcessingText.JobLabel(JobKind.Embed));
        Assert.Equal(ProcessingWords.StageName(JobKind.Vlm), ProcessingText.Subject(JobKind.Vlm));
        Assert.Equal(ProcessingWords.StageName(JobKind.Vlm), IndexStatusText.JobLabel(JobKind.Vlm));
        Assert.Equal("インデックス作成", IndexStatusText.JobLabel(null));
        Assert.StartsWith(ProcessingWords.StageName(JobKind.Embed), ProcessingWords.KindLabel(ProblemKind.EmbedFailures), StringComparison.Ordinal);
        Assert.StartsWith(ProcessingWords.StageName(JobKind.Vlm), ProcessingWords.KindLabel(ProblemKind.VlmFailures), StringComparison.Ordinal);
    }

    [Fact]
    public void The_stage_hint_adds_the_exact_technical_names_and_takes_the_model_names_from_the_core_labels()
    {
        string embed = ProcessingWords.StageHint(JobKind.Embed);
        string vlm = ProcessingWords.StageHint(JobKind.Vlm);

        Assert.Contains("特徴ベクトル化", embed, StringComparison.Ordinal);
        Assert.Contains(OhanamiCore.ModelLabels.FeatureVector, embed, StringComparison.Ordinal);
        Assert.Contains("OCR", vlm, StringComparison.Ordinal);
        Assert.Contains("VLM", vlm, StringComparison.Ordinal);
        Assert.Contains(OhanamiCore.ModelLabels.Vlm, vlm, StringComparison.Ordinal);
        Assert.Equal("特徴ベクトル検索 (" + OhanamiCore.ModelLabels.FeatureVector + ")", ProcessingWords.StageNameWithModel(JobKind.Embed));
        Assert.Equal("テキスト検索 (" + OhanamiCore.ModelLabels.Vlm + ")", ProcessingWords.StageNameWithModel(JobKind.Vlm));
    }
}
