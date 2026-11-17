using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Tests.Indexing;
using Ohanami.Core.Tests.Scanning;
using Xunit;

namespace Ohanami.Core.Tests.Folders;

/// <summary>
/// 実物の一時フォルダと OS の監視(<see cref="System.IO.FileSystemWatcher"/>)で、製品の <see cref="FolderWatcher"/> + <see cref="Indexer"/> + <c>Scanner</c> を通す
/// (ADR 0009。大量のコピー、一時ファイル → 名前の変更、フォルダごとの移動、チャンクごとの追記、
/// 監視しているフォルダの削除と作り直し)。待ちの時間は <see cref="ManualTimeProvider"/>(まとめる 3 秒・定期の 15 分は進めて起こす)。
/// OS のイベントが届くのだけは、本物の時間で待つ。走査・登録の時計(<see cref="TestTimeProvider"/>)は 2026-11-05 で、書いたばかりのファイルの更新時刻は
/// それより未来なので、「更新から 2 秒以内」には掛からない(その規則は ScannerWriteInProgressTests)。
/// </summary>
public sealed class FolderWatcherRealFileSystemTests : IDisposable
{
    private readonly ScanFixture _fx = new();
    private readonly ManualTimeProvider _time = new();
    private readonly TestLog _log = new();
    private readonly Indexer _indexer;
    private readonly FolderWatcher _watcher;
    private readonly List<ScanCompletedInfo> _completed = [];
    private long _folderId;

    public FolderWatcherRealFileSystemTests()
    {
        _indexer = new Indexer(_fx.Db, _fx.Scanner, [], _fx.Time, "test-1.0", _log);
        _indexer.ScanCompleted += info => { lock (_completed) _completed.Add(info); };
        _watcher = new FolderWatcher(() => _fx.Folders.List(), (ids, trigger, ct) => _indexer.ScanAsync(ids, trigger, ct), _time, _log);
    }

    public void Dispose()
    {
        _watcher.StopAsync().GetAwaiter().GetResult();
        _indexer.StopAsync().GetAwaiter().GetResult();
        _fx.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int CompletedCount { get { lock (_completed) return _completed.Count; } }

    private async Task StartAsync()
    {
        _folderId = await _fx.RegisterRootAsync();
        _watcher.Refresh();
        Assert.True(_watcher.IsWatching(_folderId));
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

    /// <summary>
    /// OS のイベントが届いてまとめの待ちに入り、届き終わる(数えたイベントの数が 0.4 秒変わらない)のを待ってから、3 秒進めて、走査が終わるのを待つ。
    /// 届き終わるのを待つのは、1 つの操作のイベント(一時ファイルの Created と Renamed など)が、時間を進めたあとに届くと、静かな時間の起算がやり直しになって走査が起きないため
    /// (この時計は止まっているので、本物の製品では 3 秒の間に届く)。
    /// </summary>
    private async Task SettleScanAsync(int expectedCompleted)
    {
        await EventSettle.WaitUntilQuietAsync(_watcher, _folderId);
        _time.Advance(FolderWatcher.QuietPeriod);
        Assert.True(await UntilAsync(() => CompletedCount >= expectedCompleted && !_watcher.IsBusy), "走査が終わらなかった");
    }

    private long ImageCount => _fx.Long("SELECT count(*) FROM image_paths WHERE missing_since IS NULL");

    [Fact]
    public async Task A_mass_copy_is_picked_up_by_one_scan_after_the_events_settle()
    {
        await StartAsync();
        for (int i = 0; i < 300; i++) _fx.Write($"copy{i:000}.png", ScanFixture.FakePng("copy-" + i));

        await SettleScanAsync(1);

        Assert.Equal(300, ImageCount);
        Assert.Equal(ScanTrigger.Watcher, _completed[0].Trigger);
        Assert.Equal(1, CompletedCount);
    }

    [Fact]
    public async Task A_temporary_file_renamed_to_an_image_name_is_picked_up_although_no_created_event_comes_for_the_final_name()
    {
        await StartAsync();
        string tmp = _fx.Write("shot.png.tmp", ScanFixture.FakePng("shot"));
        File.Move(tmp, Path.Combine(_fx.Root, "shot.png"));                    // Renamed だけが来る(x.png の Created は来ない)

        await SettleScanAsync(1);

        Assert.Equal(1, ImageCount);
        Assert.EndsWith("shot.png", _fx.Text("SELECT path FROM image_paths")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_folder_moved_in_as_a_whole_is_picked_up_although_no_event_comes_for_its_contents()
    {
        await StartAsync();
        string outside = Path.GetFullPath(Path.Combine(_fx.Root, "..", Path.GetFileName(_fx.Root) + "-outside"));
        string sub = Path.Combine(outside, "2026.11");                          // 拡張子のようなものが付いた名前(Created は拡張子を問わず数える)
        Directory.CreateDirectory(sub);
        try
        {
            for (int i = 0; i < 5; i++) File.WriteAllBytes(Path.Combine(sub, $"in{i}.png"), ScanFixture.FakePng("moved-" + i));
            Directory.Move(sub, Path.Combine(_fx.Root, "2026.11"));             // Created <フォルダ> の 1 件だけ(中身のイベントは来ない)

            await SettleScanAsync(1);

            Assert.Equal(5, ImageCount);
        }
        finally
        {
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task A_file_written_in_chunks_that_is_opened_and_closed_each_time_is_registered_with_its_final_content()
    {
        await StartAsync();
        string path = Path.Combine(_fx.Root, "chunked.png");
        byte[] png = ScanFixture.FakePng("chunked-content");
        for (int offset = 0; offset < png.Length; offset += 16)
        {
            using var chunk = new FileStream(path, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read);
            chunk.Write(png, offset, Math.Min(16, png.Length - offset));
        }

        await SettleScanAsync(1);

        Assert.Equal(1, ImageCount);
        Assert.Equal(png.Length, _fx.Long("SELECT size FROM image_paths"));      // 書き終わったあとの走査が、最後の内容を登録する
        Assert.Equal(ScanFixture.Sha256Hex(File.ReadAllBytes(path)), _fx.Text("SELECT hash FROM image_paths"));
    }

    [Fact]
    public async Task A_deleted_file_is_marked_missing_by_the_next_watcher_scan_and_the_completion_tells_the_ui()
    {
        await StartAsync();
        string path = _fx.Write("gone.png", ScanFixture.FakePng("gone"));
        await SettleScanAsync(1);
        Assert.Equal(1, ImageCount);

        File.Delete(path);
        _time.Advance(TimeSpan.FromMinutes(1));                                  // 前の走査との間隔の下限(所要 × 2)を過ぎる
        await SettleScanAsync(2);

        Assert.Equal(0, ImageCount);
        Assert.Equal(1, _fx.Long("SELECT count(*) FROM image_paths WHERE missing_since IS NOT NULL"));
        Assert.Equal(1, _completed[1].Summary.Missing);                          // App はこの完了で、今の検索をやり直して結果から外す
    }

    [Fact]
    public async Task Deleting_and_recreating_the_watched_folder_is_repaired_by_the_periodic_recreation()
    {
        await StartAsync();
        _fx.Write("before.png", ScanFixture.FakePng("before"));
        await SettleScanAsync(1);
        Assert.Equal(1, ImageCount);

        Directory.Delete(_fx.Root, true);                                        // 監視しているフォルダ自体を消すと、監視は死ぬ(EnableRaisingEvents は true のまま)
        _time.Advance(FolderWatcher.PeriodicInterval);                           // 15 分: 全部作り直して全部走査する。フォルダが無いので「監視できない」
        Assert.True(await UntilAsync(() => !_watcher.IsBusy && _watcher.UnavailableFolders.ContainsKey(_folderId)));

        Directory.CreateDirectory(_fx.Root);                                     // 作り直した
        _fx.Write("after.png", ScanFixture.FakePng("after"));
        _time.Advance(FolderWatcher.PeriodicInterval);                           // 次の定期の走査が、監視を作り直し、新しいファイルも拾う
        Assert.True(await UntilAsync(() => !_watcher.IsBusy && _watcher.IsWatching(_folderId) && ImageCount == 1));
        Assert.Empty(_watcher.UnavailableFolders);

        int before = CompletedCount;
        _fx.Write("later.png", ScanFixture.FakePng("later"));                    // 作り直した監視が、イベントを届ける
        await SettleScanAsync(before + 1);

        Assert.Equal(2, ImageCount);
    }
}
