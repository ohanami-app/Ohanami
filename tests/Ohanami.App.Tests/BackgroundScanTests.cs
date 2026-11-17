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
/// Core の中で起きた走査(監視・定期・復帰・再試行)の完了を、App が走査の完了のイベントで受けること(走査をやり直す合図としての監視は ADR 0009)と、
/// 「監視できない」フォルダの表示。画面のスレッドへの渡しは本物の Dispatcher(StaDispatcher)。Core は本物(偽のモデルなし。GPU は使わない)。
/// </summary>
public sealed class BackgroundScanTests
{
    private static Task<OhanamiCore> OpenCoreAsync(TempDirectory temp, bool watch)
    {
        string models = temp.File("no-models");
        Directory.CreateDirectory(models);
        var options = new CoreOptions(temp.File("data/ohanami.db"), temp.File("thumbs"), models, AppContext.BaseDirectory, new TestLog(), null, WatchFolders: watch);
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

    private static ScanSummary Summary(int added = 0, int changed = 0, int missing = 0) => new(added, changed, missing, 0, 0, 0, 0, TimeSpan.FromSeconds(0.2));

    [Fact]
    public async Task A_background_scan_refreshes_the_folder_list_tells_the_search_to_redo_and_notes_only_real_changes()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp, watch: false);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 2));
            var notices = new List<string>();
            var finished = new List<ScanSummary>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false);
            panel.ScanFinished += finished.Add;
            panel.Attach(core);
            Assert.Contains("0 枚", panel.FolderTexts.Single(), StringComparison.Ordinal);
            await core.Indexer.ScanAsync([folder.Id], ScanTrigger.Manual);   // 一覧の件数だけ先に動かす(このパネルは受けない)
            finished.Clear();
            notices.Clear();

            panel.HandleBackgroundScan(new ScanCompletedInfo(ScanTrigger.Watcher, [folder.Id], Summary(added: 2)));

            Assert.Contains("2 枚", panel.FolderTexts.Single(), StringComparison.Ordinal);          // フォルダの一覧を取り直した
            Assert.Single(finished);                                                                  // 検索のやり直しの合図(MainWindow が今の検索をやり直す)
            Assert.Contains(notices, n => n.StartsWith("自動のスキャンが終わりました (フォルダの変化、", StringComparison.Ordinal) && n.Contains("新規 2", StringComparison.Ordinal));

            notices.Clear();
            panel.HandleBackgroundScan(new ScanCompletedInfo(ScanTrigger.Periodic, [folder.Id], Summary()));   // 何も変わらなかった定期の走査
            Assert.Single(finished);                                                                  // 何も変わらなければ、検索をやり直さない(15 分ごとに結果の並びが動かないように)
            Assert.Empty(notices);                                                                    // 通知も出さない(毎回出すと、状態の行が埋まる)

            panel.HandleBackgroundScan(new ScanCompletedInfo(ScanTrigger.Watcher, [folder.Id], Summary(missing: 1)));   // 消えた画像だけでも、結果から外すためにやり直す
            Assert.Equal(2, finished.Count);
        });
    }

    [Fact]
    public async Task A_scan_that_core_ran_by_itself_reaches_the_panel_through_the_completion_event()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp, watch: false);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 3));
            var finished = new List<ScanSummary>();
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            panel.ScanFinished += finished.Add;
            panel.Attach(core);

            await core.Indexer.ScanAsync([folder.Id], ScanTrigger.Watcher);                         // Core の中の走査(監視のきっかけ)

            Assert.True(await Wait.UntilAsync(() => finished.Count == 1));
            Assert.Equal(3, finished[0].Added);
            Assert.True(await Wait.UntilAsync(() => panel.FolderTexts.Single().Contains("3 枚", StringComparison.Ordinal)));
        });
    }

    [Theory]
    [InlineData(ScanTrigger.Startup)]
    [InlineData(ScanTrigger.Manual)]
    [InlineData(ScanTrigger.Missing)]
    public async Task Scans_the_panel_asked_for_itself_are_not_handled_twice(ScanTrigger trigger)
    {
        // このパネルが頼んだ走査(起動・手動・見つからない)は ScanAsync の続きで処理するので、完了のイベントでは、もう一度処理しない
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp, watch: false);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 1));
            var finished = new List<ScanSummary>();
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            panel.ScanFinished += finished.Add;
            panel.Attach(core);

            await core.Indexer.ScanAsync([folder.Id], trigger);
            await Task.Delay(300, TestContext.Current.CancellationToken);                            // イベントが届くなら、この間に届く

            Assert.Empty(finished);
        });
    }

    [Fact]
    public async Task A_background_scan_after_the_panel_was_detached_touches_nothing()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp, watch: false);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 1));
            var finished = new List<ScanSummary>();
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            panel.ScanFinished += finished.Add;
            panel.Attach(core);
            panel.Detach();

            await core.Indexer.ScanAsync([folder.Id], ScanTrigger.Watcher);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            Assert.Empty(finished);
        });
    }

    [Fact]
    public async Task A_folder_that_cannot_be_watched_shows_the_notice_in_the_list_and_the_tooltip()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp, watch: true);
            string dir = MakeFolder(temp, "a", 1);
            FolderInfo folder = await core.Folders.AddAsync(dir);
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            panel.Attach(core);
            Assert.DoesNotContain("監視できません", panel.FolderTexts.Single(), StringComparison.Ordinal);   // ふつうは出ない

            Directory.Delete(dir, true);                                                              // 取り外したドライブ・切れたネットワークの代わり
            await core.Folders.SetEnabledAsync(folder.Id, false);
            await core.Folders.SetEnabledAsync(folder.Id, true);                                      // 操作のあとの Refresh で、監視を作り直す(作れない)

            Assert.True(core.UnwatchedFolders.ContainsKey(folder.Id));
            Assert.True(await Wait.UntilAsync(() => panel.FolderTexts.Single().Contains("監視できません (定期のスキャンだけです)", StringComparison.Ordinal)));
            Assert.Contains("監視できません: ", panel.FolderTooltips.Single(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_disabled_folder_does_not_show_the_unwatched_notice()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp, watch: true);
            string dir = MakeFolder(temp, "a", 1);
            FolderInfo folder = await core.Folders.AddAsync(dir);
            Directory.Delete(dir, true);
            await core.Folders.SetEnabledAsync(folder.Id, false);                                     // 無効にしたフォルダは、監視しない(「監視できない」ではない)
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);

            panel.Attach(core);

            Assert.DoesNotContain("監視できません", panel.FolderTexts.Single(), StringComparison.Ordinal);
            Assert.Empty(core.UnwatchedFolders);
        });
    }
}
