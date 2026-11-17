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
/// 左のパネルのフォルダ操作(外す・有効/無効)のあとに、待ちの件数の表示も更新されること(外したあとも件数が古いまま残らない)と、
/// 一時停止を押したことが診断ログに残ること。Core は本物(偽のモデル・VLM なし。GPU は使わない)。
/// </summary>
public sealed class IndexPanelFolderTests
{
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
        {
            // 中身が違う(ハッシュが違う)画像。走査は先頭を読むだけで、デコードしない
            AgedFile.Write(Path.Combine(dir, $"{name}-{i}.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, (byte)i, (byte)name[0], 3, 4, 5, 6, 7, 8]);
        }
        return dir;
    }

    /// <summary>2 つのフォルダ(A に 2 枚、B に 1 枚)を登録して走査した Core と、それを見せたパネル。</summary>
    private static async Task<(OhanamiCore Core, IndexPanel Panel, FolderInfo A, FolderInfo B)> OpenWithTwoFoldersAsync(TempDirectory temp)
    {
        OhanamiCore core = await OpenCoreAsync(temp);
        FolderInfo a = await core.Folders.AddAsync(MakeFolder(temp, "a", 2));
        FolderInfo b = await core.Folders.AddAsync(MakeFolder(temp, "b", 1));
        await core.Indexer.ScanAsync();
        var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
        panel.Attach(core);
        return (core, panel, a, b);
    }

    [Fact]
    public async Task Removing_a_folder_refreshes_the_pending_counts_at_once()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            (OhanamiCore core, IndexPanel panel, FolderInfo a, _) = await OpenWithTwoFoldersAsync(temp);
            await using (core)
            {
                Assert.Contains("特徴ベクトル検索: 0 / 3 枚", panel.StatusText, StringComparison.Ordinal);       // 3 枚が埋め込み待ち

                await panel.RemoveFolderAsync(a);

                // 外したフォルダの 2 枚は待ちから消える。表示が古い 3 のままなら、イベントを待たずに更新していない
                Assert.Contains("特徴ベクトル検索: 0 / 1 枚", panel.StatusText, StringComparison.Ordinal);
                Assert.DoesNotContain("0 / 3", panel.StatusText, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public async Task Disabling_and_enabling_a_folder_also_refreshes_the_displayed_counts()
    {
        // 無効にしても待ちの件数は変わらない(無効は走査の対象から外すだけ)。表示を取り直したことは、古い値を見せておいて、切り替えのあとに本当の値に戻ることで確かめる
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            (OhanamiCore core, IndexPanel panel, FolderInfo a, _) = await OpenWithTwoFoldersAsync(temp);
            await using (core)
            {
                IndexState stale = new(IndexRunState.Idle, null, [new StageSummary(Stage.Embed, 99, 0, 0, 0)], EngineStatus.NotCreated, null, null, null, null, null, 0);

                panel.ShowForTests(stale);
                Assert.Contains("特徴ベクトル検索: 0 / 99 枚", panel.StatusText, StringComparison.Ordinal);
                await panel.ToggleFolderAsync(a);                                        // 無効にする
                Assert.False(core.Folders.List().Single(f => f.Id == a.Id).Enabled);
                Assert.Contains("特徴ベクトル検索: 0 / 3 枚", panel.StatusText, StringComparison.Ordinal);

                panel.ShowForTests(stale);
                await panel.ToggleFolderAsync(core.Folders.List().Single(f => f.Id == a.Id));   // 有効に戻す
                Assert.True(core.Folders.List().Single(f => f.Id == a.Id).Enabled);
                Assert.Contains("特徴ベクトル検索: 0 / 3 枚", panel.StatusText, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public async Task A_failed_removal_still_leaves_the_display_consistent_and_says_why()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false);
            panel.Attach(core);

            await panel.RemoveFolderAsync(new FolderInfo(9999, @"C:\nowhere", true, 0, null, null));   // 登録されていないフォルダ

            Assert.Contains(notices, n => n.StartsWith("フォルダを外せませんでした", StringComparison.Ordinal));
            Assert.Contains("特徴ベクトル検索: 0 / 0 枚", panel.StatusText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Pressing_pause_while_running_leaves_a_job_log_line_like_start_and_resume()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var log = new TestLog();
            var panel = new IndexPanel(log, _ => { }, () => false);
            panel.Attach(core);
            // 表示を「実行中」にする(本物のジョブは GPU が要るので動かさない)。Indexer は動いていないので、PauseAsync は何もしないで返る
            panel.ShowForTests(new IndexState(IndexRunState.Running, JobKind.Vlm, [], EngineStatus.NotCreated, null, null, null, null, null, 0));

            await panel.ToggleRunAsync();

            Assert.Contains("INFO ジョブ: 一時停止", log.Messages);
        });
    }

    [Theory]
    [InlineData(IndexRunState.Idle, true, "インデックス作成を開始")]
    [InlineData(IndexRunState.Running, true, "一時停止")]
    [InlineData(IndexRunState.Pausing, false, "一時停止")]        // 一時停止の途中は押せない(状態の文が「一時停止の途中…」と言う)
    [InlineData(IndexRunState.Paused, true, "再開")]
    [InlineData(IndexRunState.Failed, true, "インデックス作成を開始")]
    [InlineData(IndexRunState.Waiting, true, "一時停止")]        // 待機中: 一時停止もできる(Core の PauseAsync の意味と同じ)
    public async Task The_one_button_is_start_pause_or_resume_following_the_run_state_including_waiting(IndexRunState run, bool canRun, string text)
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            panel.Attach(core);

            panel.ShowForTests(new IndexState(run, null, [], EngineStatus.NotCreated, null, null, null, null, null, 0));

            Assert.Equal(canRun, panel.CanRun);
            Assert.Equal(text, panel.RunButtonText);
        });
    }

    [Fact]
    public async Task Pressing_pause_while_waiting_goes_through_the_same_pause_path_and_logs_it()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var log = new TestLog();
            var panel = new IndexPanel(log, _ => { }, () => false);
            panel.Attach(core);
            panel.ShowForTests(new IndexState(IndexRunState.Waiting, null, [], EngineStatus.NotCreated, null, null, null, null, null, 0));
            Assert.Contains("待機中 (新しい画像が来たら続けます)", panel.StatusText, StringComparison.Ordinal);

            await panel.ToggleRunAsync();

            Assert.Contains("INFO ジョブ: 一時停止", log.Messages);
        });
    }

    [Fact]
    public async Task Pressing_the_one_button_while_idle_starts_the_processing_and_logs_it()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var log = new TestLog();
            var panel = new IndexPanel(log, _ => { }, () => false);
            panel.Attach(core);

            await panel.ToggleRunAsync();

            Assert.Contains("INFO ジョブ: インデックス作成を開始", log.Messages);                       // 止まっているときは、押すと始まる(この Core には VLM が無いので、失敗の通知になる)
            Assert.Contains(log.Messages, m => m.StartsWith("ERROR ジョブに失敗: インデックス作成を開始", StringComparison.Ordinal));
        });
    }
}
