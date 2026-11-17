using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Power;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Ohanami.Core.Tests.Scanning;
using Xunit;

namespace Ohanami.Core.Tests.Folders;

/// <summary>
/// <see cref="OhanamiCore"/> に組み込んだフォルダの監視(ADR 0009): 起動時に監視を作る、フォルダの登録・解除・有効/無効のあとの <c>Refresh</c>、
/// スリープからの復帰の通知は VLM の設定が無くても届く(<c>PowerEvents</c> を VLM の外へ)、監視 → 走査 → 完了のイベント、破棄で止まる。
/// Core は本物(モデルは無いので「準備に失敗」になるが、フォルダと走査は動く。GPU は使わない)。時計は <see cref="ManualTimeProvider"/>。
/// </summary>
public sealed class CoreFolderWatchingTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly TestLog _log = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CoreOptions Options(bool watch = true)
    {
        string models = _dir.File("no-models");
        Directory.CreateDirectory(models);
        return new CoreOptions(_dir.File("data/ohanami.db"), _dir.File("thumbs"), models, AppContext.BaseDirectory, _log, null, WatchFolders: watch);
    }

    private Task<OhanamiCore> OpenAsync(bool watch = true) => OhanamiCore.OpenAsync(Options(watch), configureNative: false, time: _time, Ct);

    private string MakeLibrary(string name)
    {
        string path = _dir.File(name);
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteImage(string path, string seed)
    {
        File.WriteAllBytes(path, ScanFixture.FakePng(seed));
        File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime.AddHours(-1));
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20, Ct);
        }
        return condition();
    }

    private static long Count(OhanamiCore core, string sql)
    {
        using Microsoft.Data.Sqlite.SqliteConnection conn = core.DatabaseForTests.CreateReadConnection();
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Opening_the_core_watches_the_registered_enabled_folders_before_any_scan()
    {
        long enabledId, disabledId;
        await using (OhanamiCore first = await OpenAsync(watch: false))
        {
            enabledId = (await first.Folders.AddAsync(MakeLibrary("on"), Ct)).Id;
            disabledId = (await first.Folders.AddAsync(MakeLibrary("off"), Ct)).Id;
            await first.Folders.SetEnabledAsync(disabledId, false, Ct);
        }

        await using OhanamiCore core = await OpenAsync();

        FolderWatcher watcher = core.FolderWatcherForTests!;
        Assert.True(watcher.IsWatching(enabledId));
        Assert.False(watcher.IsWatching(disabledId));
        Assert.DoesNotContain(_log.Messages, m => m.StartsWith("INFO 走査: 始める", StringComparison.Ordinal));   // 監視を先に作る。走査は App の起動時の走査(まだ呼んでいない)
    }

    [Fact]
    public async Task A_core_without_watching_has_no_watcher_and_no_unwatched_folders()
    {
        await using OhanamiCore core = await OpenAsync(watch: false);
        await core.Folders.AddAsync(MakeLibrary("a"), Ct);

        Assert.Null(core.FolderWatcherForTests);
        Assert.Empty(core.UnwatchedFolders);
    }

    [Fact]
    public async Task Registering_removing_and_toggling_a_folder_refreshes_the_watchers()
    {
        await using OhanamiCore core = await OpenAsync();
        FolderWatcher watcher = core.FolderWatcherForTests!;

        FolderInfo a = await core.Folders.AddAsync(MakeLibrary("a"), Ct);
        Assert.True(watcher.IsWatching(a.Id));                                        // 登録のあと

        await core.Folders.SetEnabledAsync(a.Id, false, Ct);
        Assert.False(watcher.IsWatching(a.Id));                                       // 無効にしたあと

        await core.Folders.SetEnabledAsync(a.Id, true, Ct);
        Assert.True(watcher.IsWatching(a.Id));                                        // 有効にしたあと

        await core.Folders.RemoveAsync(a.Id, Ct);
        Assert.False(watcher.IsWatching(a.Id));                                       // 解除したあと
    }

    [Fact]
    public async Task A_rejected_registration_does_not_touch_the_watchers()
    {
        await using OhanamiCore core = await OpenAsync();
        string lib = MakeLibrary("a");
        FolderInfo a = await core.Folders.AddAsync(lib, Ct);

        await Assert.ThrowsAsync<FolderRegistrationException>(() => core.Folders.AddAsync(lib, Ct));   // 登録済み

        Assert.True(core.FolderWatcherForTests!.IsWatching(a.Id));
    }

    [Fact]
    public async Task A_folder_that_cannot_be_watched_is_reported_through_the_core_and_its_event()
    {
        await using OhanamiCore core = await OpenAsync();
        string lib = MakeLibrary("a");
        FolderInfo a = await core.Folders.AddAsync(lib, Ct);
        int events = 0;
        core.FolderWatchChanged += () => Interlocked.Increment(ref events);

        Directory.Delete(lib);
        await core.Folders.SetEnabledAsync(a.Id, false, Ct);
        await core.Folders.SetEnabledAsync(a.Id, true, Ct);                           // 操作のあとの Refresh で作れない

        Assert.True(core.UnwatchedFolders.ContainsKey(a.Id));
        Assert.True(await UntilAsync(() => Volatile.Read(ref events) >= 1));
    }

    [Fact]
    public async Task The_resume_notification_scans_everything_even_when_no_vlm_is_configured()
    {
        // PowerEvents は VLM の設定の中でだけ作られていた。外に出したので、VLM が無くても、復帰で全部作り直して全部走査する
        await using OhanamiCore core = await OpenAsync();
        await core.Folders.AddAsync(MakeLibrary("a"), Ct);
        Assert.NotNull(core.PowerEventsForTests);

        core.PowerEventsForTests!.Handle(PowerEvents.ResumeAutomatic);

        Assert.True(await UntilAsync(() => _log.Messages.Any(m => m == "INFO 走査: 始める(きっかけ: 復帰、フォルダ 1 個)")));
    }

    [Fact]
    public async Task A_new_image_in_a_watched_folder_is_scanned_after_the_quiet_time_and_the_completion_event_fires()
    {
        await using OhanamiCore core = await OpenAsync();
        string lib = MakeLibrary("a");
        FolderInfo a = await core.Folders.AddAsync(lib, Ct);
        var completed = new List<ScanCompletedInfo>();
        core.Indexer.ScanCompleted += info => { lock (completed) completed.Add(info); };

        WriteImage(Path.Combine(lib, "new.png"), "new");
        await EventSettle.WaitUntilQuietAsync(core.FolderWatcherForTests!, a.Id);   // 1 つの操作の OS のイベントが届き終わってから、時計を進める
        _time.Advance(FolderWatcher.QuietPeriod);

        Assert.True(await UntilAsync(() => { lock (completed) return completed.Count == 1; }));
        Assert.Equal(ScanTrigger.Watcher, completed[0].Trigger);
        Assert.Equal([a.Id], completed[0].FolderIds);
        Assert.Equal(1, completed[0].Summary.Added);
        Assert.Equal(1, Count(core, "SELECT count(*) FROM image_paths WHERE missing_since IS NULL"));
    }

    [Fact]
    public async Task Dispose_stops_the_watcher_and_nothing_scans_afterwards()
    {
        OhanamiCore core = await OpenAsync();
        string lib = MakeLibrary("a");
        FolderInfo a = await core.Folders.AddAsync(lib, Ct);
        FolderWatcher watcher = core.FolderWatcherForTests!;
        WriteImage(Path.Combine(lib, "x.png"), "x");
        Assert.True(await UntilAsync(() => watcher.HasPendingEvents(a.Id)));

        await core.DisposeAsync();
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(200, Ct);

        Assert.False(watcher.IsWatching(a.Id));
        Assert.DoesNotContain(_log.Messages, m => m.StartsWith("ERROR", StringComparison.Ordinal) && m.Contains("監視", StringComparison.Ordinal));   // (モデルが無いので、起動の失敗の ERROR は別にある)
        Assert.DoesNotContain(_log.Messages, m => m.StartsWith("INFO 走査: 始める", StringComparison.Ordinal));   // 破棄のあと、まとめの待ちが発火しても走査しない
        Assert.Equal(0, _time.ArmedTimerCount);                                       // 待ちも止まっている
    }
}
