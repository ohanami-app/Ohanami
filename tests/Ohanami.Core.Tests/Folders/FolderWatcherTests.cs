using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Xunit;

namespace Ohanami.Core.Tests.Folders;

/// <summary>
/// <see cref="FolderWatcher"/> のテスト(ADR 0009): まとめ方(3 秒 / 30 秒)、走査の間隔の下限(前の所要の 2 倍)、Changed だけを拡張子で絞る、
/// 一時的に読めなかったファイルの 5 秒後の再走査(3 回)、Error での作り直し、15 分ごとと復帰の「全部作り直して全部走査」、「監視できない」、
/// フォルダの操作のあとの <see cref="FolderWatcher.Refresh"/>、走査の例外で落ちない。待ちは <see cref="ManualTimeProvider"/> で進める
/// (OS の監視は実物の一時フォルダ。イベントの到着だけは本物の時間で待つ)。
/// </summary>
public sealed class FolderWatcherTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly TestLog _log = new();
    private readonly TempDirectory _root = new();
    private readonly List<FolderInfo> _folders = [];
    private readonly List<FolderWatcher> _watchers = [];
    private readonly List<(long[] Ids, ScanTrigger Trigger)> _calls = [];
    private Func<int, ScanSummary> _result = _ => Summary();
    private Func<IReadOnlyList<long>, ScanTrigger, Task>? _during;

    public void Dispose()
    {
        foreach (FolderWatcher w in _watchers) w.StopAsync().GetAwaiter().GetResult();
        _root.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScanSummary Summary(int transient = 0) => new(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, transient);

    private int CallCount { get { lock (_calls) return _calls.Count; } }

    private (long[] Ids, ScanTrigger Trigger) Call(int i) { lock (_calls) return _calls[i]; }

    private string MakeFolder(string name)
    {
        string path = _root.File(name);
        Directory.CreateDirectory(path);
        return path;
    }

    private FolderInfo AddFolder(string name, bool enabled = true, bool create = true)
    {
        string path = create ? MakeFolder(name) : _root.File(name);
        var info = new FolderInfo(_folders.Count + 1, path, enabled, 0, null, null);
        _folders.Add(info);
        return info;
    }

    private FolderWatcher NewWatcher()
    {
        var w = new FolderWatcher(() => _folders.ToList(), async (ids, trigger, ct) =>
        {
            int index;
            lock (_calls)
            {
                _calls.Add((ids.ToArray(), trigger));
                index = _calls.Count - 1;
            }
            if (_during is { } hook) await hook(ids, trigger).ConfigureAwait(false);
            return _result(index);
        }, _time, _log);
        _watchers.Add(w);
        return w;
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(10, Ct);
        }
        return condition();
    }

    /// <summary>「走査が起きない」を確かめるための、実時間の小さな待ち(非同期の実行が動く余裕)。</summary>
    private static Task SettleAsync() => Task.Delay(200, Ct);

    // ---------------------------------------------------------------- まとめ方

    [Fact]
    public async Task Events_are_batched_until_three_quiet_seconds_then_scan_that_folder_once()
    {
        FolderInfo a = AddFolder("a");
        FolderInfo b = AddFolder("b");
        FolderWatcher w = NewWatcher();
        w.Refresh();

        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "x.png"));
        _time.Advance(TimeSpan.FromSeconds(1));
        w.NoteEvent(a.Id, WatchedChange.Renamed, Path.Combine(a.Path, "y.png"));     // 静かな時間の起算がやり直しになる
        _time.Advance(TimeSpan.FromSeconds(2.9));                                     // 最後のイベントから 2.9 秒
        await SettleAsync();
        Assert.Equal(0, CallCount);

        _time.Advance(TimeSpan.FromSeconds(0.1));                                     // 3 秒
        Assert.True(await UntilAsync(() => CallCount == 1));

        Assert.Equal([a.Id], Call(0).Ids);                                            // イベントのあったフォルダだけ
        Assert.Equal(ScanTrigger.Watcher, Call(0).Trigger);
        await SettleAsync();
        Assert.Equal(1, CallCount);
        Assert.NotEqual(b.Id, Call(0).Ids[0]);
    }

    [Fact]
    public async Task A_continuous_stream_of_events_scans_once_thirty_seconds_after_the_first()
    {
        FolderInfo a = AddFolder("a");
        FolderWatcher w = NewWatcher();
        w.Refresh();

        for (int i = 0; i < 29; i++)                                                  // 29 秒間、毎秒イベント(3 秒の静けさは来ない)
        {
            w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, $"f{i}.png"));
            _time.Advance(TimeSpan.FromSeconds(1));
        }
        await SettleAsync();
        Assert.Equal(0, CallCount);

        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "f29.png"));
        _time.Advance(TimeSpan.FromSeconds(1));                                       // 最初のイベントから 30 秒
        Assert.True(await UntilAsync(() => CallCount == 1));                          // 続いていても、一度走査する(終わった分から検索に出す)
    }

    [Fact]
    public async Task Only_changed_events_are_filtered_by_extension()
    {
        FolderInfo a = AddFolder("a");
        FolderWatcher w = NewWatcher();
        w.Refresh();

        w.NoteEvent(a.Id, WatchedChange.Changed, Path.Combine(a.Path, "recording.mp4"));   // 録画の書き込みで多発する Changed は数えない
        w.NoteEvent(a.Id, WatchedChange.Changed, Path.Combine(a.Path, "notes.txt"));
        Assert.False(w.HasPendingEvents(a.Id));

        w.NoteEvent(a.Id, WatchedChange.Changed, Path.Combine(a.Path, "shot.PNG"));        // 大文字小文字を区別しない
        Assert.True(w.HasPendingEvents(a.Id));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await UntilAsync(() => CallCount == 1));
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Deleted")]
    [InlineData("Renamed")]
    public void Created_deleted_and_renamed_are_counted_whatever_the_name_is(string changeName)
    {
        WatchedChange change = Enum.Parse<WatchedChange>(changeName);
        FolderInfo a = AddFolder("a");
        FolderWatcher w = NewWatcher();
        w.Refresh();

        w.NoteEvent(a.Id, change, Path.Combine(a.Path, "2026.11"));                        // 「2026.11」のようなフォルダの移動も拾う(拡張子では絞らない)

        Assert.True(w.HasPendingEvents(a.Id));
    }

    [Fact]
    public async Task The_next_scan_waits_for_twice_the_previous_scan_duration()
    {
        FolderInfo a = AddFolder("a");
        _during = (_, _) =>
        {
            if (CallCount == 1) _time.Advance(TimeSpan.FromSeconds(10));                   // 1 回目の走査に 10 秒かかった(列挙が遅い大きなフォルダ)
            return Task.CompletedTask;
        };
        FolderWatcher w = NewWatcher();
        w.Refresh();
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "x.png"));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await UntilAsync(() => CallCount == 1 && !w.IsBusy));

        // 1 回目の走査の終わりから 20 秒(所要 10 秒の 2 倍)あける
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "y.png"));
        _time.Advance(TimeSpan.FromSeconds(3));                                            // 静けさは来たが、間隔の下限はまだ
        _time.Advance(TimeSpan.FromSeconds(10));
        await SettleAsync();
        Assert.Equal(1, CallCount);

        _time.Advance(TimeSpan.FromSeconds(7));                                            // 走査の終わりから 20 秒
        Assert.True(await UntilAsync(() => CallCount == 2));
    }

    // ---------------------------------------------------------------- 一時的に読めなかったファイル

    [Fact]
    public async Task A_watcher_scan_with_transiently_unreadable_files_is_retried_after_five_seconds_up_to_three_times_in_a_row()
    {
        FolderInfo a = AddFolder("a");
        _result = _ => Summary(transient: 1);                                              // 書き込みの途中のファイルがずっと残る
        FolderWatcher w = NewWatcher();
        w.Refresh();
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "x.png"));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await UntilAsync(() => CallCount == 1 && !w.IsBusy));
        Assert.Equal(ScanTrigger.Watcher, Call(0).Trigger);

        _time.Advance(TimeSpan.FromSeconds(4.9));
        await SettleAsync();
        Assert.Equal(1, CallCount);                                                         // 5 秒より前には、再走査しない
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(await UntilAsync(() => CallCount == 2 && !w.IsBusy));
        Assert.Equal(ScanTrigger.Retry, Call(1).Trigger);

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await UntilAsync(() => CallCount == 3 && !w.IsBusy));
        Assert.Equal(ScanTrigger.Retry, Call(2).Trigger);

        _time.Advance(TimeSpan.FromMinutes(10));                                           // 3 回続けて 1 件以上 → 定期の走査に任せる(再走査はもう無い)
        await SettleAsync();
        Assert.Equal(3, CallCount);
        Assert.Contains(_log.Messages, m => m.Contains("定期の走査に任せる", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_retry_count_goes_back_when_a_scan_has_no_transient_files()
    {
        FolderInfo a = AddFolder("a");
        int[] transient = [1, 1, 0, 1, 1, 1];                                              // 3 回目で 0 になって回数が戻る。その後また 3 回続く
        _result = i => Summary(transient: transient[Math.Min(i, transient.Length - 1)]);
        FolderWatcher w = NewWatcher();
        w.Refresh();
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "x.png"));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await UntilAsync(() => CallCount == 1 && !w.IsBusy));
        for (int expected = 2; expected <= 3; expected++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            Assert.True(await UntilAsync(() => CallCount == expected && !w.IsBusy));
        }
        // 3 回目の走査は 0 件: 再走査は予約されない
        _time.Advance(TimeSpan.FromMinutes(1));
        await SettleAsync();
        Assert.Equal(3, CallCount);

        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "y.png"));          // 新しい監視の走査(回数は戻っている)
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await UntilAsync(() => CallCount == 4 && !w.IsBusy));
        for (int expected = 5; expected <= 6; expected++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            Assert.True(await UntilAsync(() => CallCount == expected && !w.IsBusy));
        }
        _time.Advance(TimeSpan.FromMinutes(1));
        await SettleAsync();
        Assert.Equal(6, CallCount);                                                         // 4 〜 6 回目が 3 回続けて 1 件以上 → そこで止まる
    }

    [Fact]
    public async Task Periodic_and_resume_scans_do_not_start_the_five_second_retry()
    {
        FolderInfo a = AddFolder("a");
        _result = _ => Summary(transient: 2);
        FolderWatcher w = NewWatcher();
        w.Refresh();

        w.OnPeriodic();
        Assert.True(await UntilAsync(() => CallCount == 1 && !w.IsBusy));
        _time.Advance(TimeSpan.FromSeconds(30));
        await SettleAsync();

        Assert.Equal(1, CallCount);                                                         // 定期の走査の結果で、5 秒後の再走査は予約しない(次の定期に任せる)
        Assert.Equal(ScanTrigger.Periodic, Call(0).Trigger);
        Assert.Equal([a.Id], Call(0).Ids);
    }

    // ---------------------------------------------------------------- 定期・復帰・Error

    [Fact]
    public async Task Every_fifteen_minutes_all_watchers_are_recreated_and_all_enabled_folders_are_scanned()
    {
        FolderInfo a = AddFolder("a");
        FolderInfo b = AddFolder("b");
        AddFolder("off", enabled: false);
        FolderWatcher w = NewWatcher();
        w.Refresh();
        Assert.True(w.IsWatching(a.Id));
        Assert.True(w.IsWatching(b.Id));

        _time.Advance(TimeSpan.FromMinutes(14.9));
        await SettleAsync();
        Assert.Equal(0, CallCount);
        _time.Advance(TimeSpan.FromMinutes(0.1));

        Assert.True(await UntilAsync(() => CallCount == 1));
        Assert.Equal(ScanTrigger.Periodic, Call(0).Trigger);
        Assert.Equal(new[] { a.Id, b.Id }, Call(0).Ids.OrderBy(x => x).ToArray());          // 有効なものだけ、まとめて 1 回
        Assert.True(w.IsWatching(a.Id));
        Assert.True(w.IsWatching(b.Id));
        Assert.False(w.IsWatching(3));
    }

    [Fact]
    public async Task Resume_recreates_everything_and_scans_all_enabled_folders()
    {
        FolderInfo a = AddFolder("a");
        FolderWatcher w = NewWatcher();
        w.Refresh();
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "x.png"));            // 復帰の前から待っていたイベント

        w.OnResumed();

        Assert.True(await UntilAsync(() => CallCount == 1));
        Assert.Equal(ScanTrigger.Resume, Call(0).Trigger);
        Assert.Equal([a.Id], Call(0).Ids);
        Assert.False(w.HasPendingEvents(a.Id));                                              // 全部の走査が見るので、まとめの待ちは消える
        Assert.True(w.IsWatching(a.Id));
        _time.Advance(TimeSpan.FromSeconds(10));
        await SettleAsync();
        Assert.Equal(1, CallCount);
    }

    [Fact]
    public async Task An_error_event_scans_at_once_without_batching_and_recreates_the_watcher()
    {
        FolderInfo a = AddFolder("a");
        FolderWatcher w = NewWatcher();
        w.Refresh();

        w.NoteEvent(a.Id, WatchedChange.Error, null);                                        // バッファのあふれなど(どのファイルが落ちたか分からない)
        _time.Advance(TimeSpan.Zero);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.True(await UntilAsync(() => CallCount == 1));
        Assert.Equal(ScanTrigger.Watcher, Call(0).Trigger);
        Assert.True(w.IsWatching(a.Id));                                                     // 作り直した
        Assert.Contains(_log.Messages, m => m.Contains("Error が来たので、監視を作り直して走査する", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 監視できない

    [Fact]
    public async Task A_folder_that_cannot_be_watched_is_reported_and_retried_at_the_periodic_scan()
    {
        FolderInfo gone = AddFolder("missing", create: false);                                // 取り外したドライブ・切れたネットワークの代わり
        var changes = new List<int>();
        FolderWatcher w = NewWatcher();
        w.StatusChanged += () => changes.Add(w.UnavailableFolders.Count);

        w.Refresh();

        Assert.True(w.UnavailableFolders.ContainsKey(gone.Id));
        Assert.False(w.IsWatching(gone.Id));
        Assert.Equal([1], changes);
        Assert.Contains(_log.Messages, m => m.StartsWith("WARN 監視: 監視できない", StringComparison.Ordinal));

        Directory.CreateDirectory(gone.Path);                                                 // 戻ってきた
        _time.Advance(TimeSpan.FromMinutes(15));                                              // 定期の走査が、監視を作り直す

        Assert.True(await UntilAsync(() => CallCount == 1 && !w.IsBusy));
        Assert.Equal(ScanTrigger.Periodic, Call(0).Trigger);                                  // 監視できなくても、走査はする(定期の走査だけが頼り)
        Assert.Empty(w.UnavailableFolders);
        Assert.True(w.IsWatching(gone.Id));
        Assert.Equal([1, 0], changes);
    }

    // ---------------------------------------------------------------- フォルダの操作

    [Fact]
    public void Refresh_follows_the_registered_folders_enabled_ones_only()
    {
        FolderInfo a = AddFolder("a");
        FolderInfo b = AddFolder("b", enabled: false);
        FolderWatcher w = NewWatcher();

        w.Refresh();
        Assert.True(w.IsWatching(a.Id));
        Assert.False(w.IsWatching(b.Id));                                                     // 無効なものは監視しない

        _folders[1] = b with { Enabled = true };                                              // 有効にした
        w.Refresh();
        Assert.True(w.IsWatching(b.Id));

        _folders.RemoveAt(0);                                                                 // 解除した
        w.Refresh();
        Assert.False(w.IsWatching(a.Id));
        Assert.True(w.IsWatching(b.Id));

        _folders[0] = b with { Enabled = false };                                             // 無効にした
        w.Refresh();
        Assert.False(w.IsWatching(b.Id));
    }

    [Fact]
    public async Task A_scan_request_for_a_folder_removed_in_the_meantime_is_logged_and_does_not_crash()
    {
        FolderInfo a = AddFolder("a");
        FolderWatcher w = NewWatcher();
        w.Refresh();
        _during = (_, _) => throw new FolderRegistrationException($"フォルダ id {a.Id} は登録されていない");   // Indexer が、外した直後の走査で投げるもの
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "x.png"));

        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.True(await UntilAsync(() => CallCount == 1 && !w.IsBusy));
        Assert.Contains(_log.Messages, m => m.StartsWith("INFO 監視: 走査しようとしたフォルダはもう登録されていない", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Messages, m => m.StartsWith("ERROR", StringComparison.Ordinal));
        _during = null;
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "y.png"));              // 落ちていない(次のイベントで、また走査する)
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await UntilAsync(() => CallCount == 2));
    }

    [Fact]
    public async Task An_unexpected_scan_failure_is_logged_and_the_next_event_still_scans()
    {
        FolderInfo a = AddFolder("a");
        FolderWatcher w = NewWatcher();
        w.Refresh();
        _during = (_, _) => throw new InvalidOperationException("走査の失敗(テスト)");
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "x.png"));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(await UntilAsync(() => CallCount == 1 && !w.IsBusy));
        Assert.Contains(_log.Messages, m => m.StartsWith("WARN 監視: 走査に失敗した", StringComparison.Ordinal));

        _during = null;
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "y.png"));
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await UntilAsync(() => CallCount == 2));
    }

    [Fact]
    public async Task After_stop_nothing_is_scanned_and_events_are_ignored()
    {
        FolderInfo a = AddFolder("a");
        FolderWatcher w = NewWatcher();
        w.Refresh();
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "x.png"));

        await w.StopAsync();
        _time.Advance(TimeSpan.FromHours(1));
        w.NoteEvent(a.Id, WatchedChange.Created, Path.Combine(a.Path, "y.png"));
        w.OnResumed();
        await SettleAsync();

        Assert.Equal(0, CallCount);
        Assert.Equal(0, _time.ArmedTimerCount);
    }
}
