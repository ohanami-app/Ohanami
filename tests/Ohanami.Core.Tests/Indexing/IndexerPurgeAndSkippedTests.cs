using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.FullText;
using Ohanami.Core.Indexing;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Scanning;
using Xunit;

namespace Ohanami.Core.Tests.Indexing;

/// <summary>
/// <see cref="Indexer"/> の、見つからない画像の記録を消す操作の状態の規則(ADR 0007: ジョブの実行中は拒む、待機中・一時停止中・Idle は可、
/// 消している間は待機から動き出さない・Start できない、走査中は可)と、飛ばした画像の理由の一覧(ランナーごとの集合と同じ寿命)。
/// </summary>
public sealed class IndexerPurgeAndSkippedTests : IDisposable
{
    private static readonly JobKind KindA = (JobKind)100;

    private static readonly string WithPath = new('1', 64);   // images.hash は 64 桁の 16 進(表の制約)
    private static readonly string NoPath = new('2', 64);

    private readonly ScanFixture _scan = new();
    private readonly TestLog _log = new();
    private readonly List<Indexer> _indexers = [];
    private readonly List<string> _removed = [];
    private Action? _afterWrite;

    public void Dispose()
    {
        foreach (Indexer i in _indexers) i.StopAsync().GetAwaiter().GetResult();
        _scan.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Indexer NewIndexer(bool withPurger, params JobRunner[] runners)
    {
        MissingImagePurger? purger = withPurger
            ? new MissingImagePurger(_scan.Db, new FtsIndex(_scan.Db, _log), hashes => { lock (_removed) _removed.AddRange(hashes); _afterWrite?.Invoke(); })
            : null;
        var indexer = new Indexer(_scan.Db, _scan.Scanner, runners, _scan.Time, "test-1.0", _log, purger: purger);
        _indexers.Add(indexer);
        return indexer;
    }

    private Task Exec(string sql) => _scan.Db.WriteAsync((c, tx) => TestDb.Exec(c, tx, sql), Ct);

    /// <summary>パスの行が 0 行の画像(消せる画像)を 1 枚作る。</summary>
    private Task AddMissingImageAsync(string hash) =>
        Exec($"INSERT INTO images(hash, size, format, first_seen_at) VALUES ('{hash}', 1, 'png', '2026-11-06T00:00:00.000Z')");

    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000) => await IndexingFixture.WaitUntilAsync(condition, timeoutMs);

    private int Count(string fragment) => _log.Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

    // ---------------------------------------------------------------- 消す操作の状態の規則

    [Fact]
    public async Task Purging_is_refused_while_a_job_is_running_and_allowed_once_it_waits()
    {
        var order = new List<string>();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        HashRunner a = new(KindA, "A", order, (self, run, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            self.Clear();
            return RunEnd.Drained;
        });
        a.Add("x01");
        Indexer indexer = NewIndexer(withPurger: true, a);
        await AddMissingImageAsync(new string('a', 64));

        Task start = indexer.StartAsync([KindA], Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), Ct));

        InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.PurgeMissingAsync(Ct));
        Assert.Contains("ジョブの実行中", e.Message, StringComparison.Ordinal);
        Assert.Equal(1, _scan.Long("SELECT count(*) FROM images WHERE hash = '" + new string('a', 64) + "'"));   // 消えていない

        release.Set();
        await start;
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);

        Assert.Equal(1, await indexer.PurgeMissingAsync(Ct));                                      // 待機中は可
        Assert.Equal(0, _scan.Long("SELECT count(*) FROM images"));
    }

    [Fact]
    public async Task Counting_works_in_any_state_and_matches_what_purging_removes()
    {
        Indexer indexer = NewIndexer(withPurger: true);
        await AddMissingImageAsync(new string('a', 64));
        await AddMissingImageAsync(new string('b', 64));

        Assert.Equal(2, await indexer.CountMissingImagesAsync(Ct));
        Assert.Equal(2, await indexer.PurgeMissingAsync(Ct));                                      // Idle は可
        Assert.Equal(0, await indexer.CountMissingImagesAsync(Ct));
        Assert.Contains(_log.Messages, m => m == "INFO 見つからない画像の記録を消した(2 枚)");
    }

    [Fact]
    public async Task Purging_is_allowed_while_paused()
    {
        var order = new List<string>();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        HashRunner a = new(KindA, "A", order, (self, run, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return RunEnd.Cancelled;
        });
        a.Add("x01");
        Indexer indexer = NewIndexer(withPurger: true, a);
        await AddMissingImageAsync(new string('a', 64));
        Task start = indexer.StartAsync([KindA], Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), Ct));
        Task pause = indexer.PauseAsync();
        release.Set();
        await pause;
        await start;
        Assert.Equal(IndexRunState.Paused, indexer.State.Run);

        Assert.Equal(1, await indexer.PurgeMissingAsync(Ct));
        Assert.Equal(IndexRunState.Paused, indexer.State.Run);                                      // 状態は変えない
    }

    [Fact]
    public async Task While_purging_a_waiting_job_does_not_wake_and_start_is_refused_but_scanning_works_and_the_wake_comes_after()
    {
        var order = new List<string>();
        HashRunner a = new(KindA, "A", order, HashRunner.DrainSkippingBad);
        a.Add("x01");
        Indexer indexer = NewIndexer(withPurger: true, a);
        await AddMissingImageAsync(new string('a', 64));
        await indexer.StartAsync([KindA], Ct);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);

        // 書き込みが済んだあと(まだ「消している最中」)で、走査の完了と Start を試す
        var inside = new ManualResetEventSlim();
        var proceed = new ManualResetEventSlim();
        _afterWrite = () =>
        {
            inside.Set();
            proceed.Wait(TimeSpan.FromSeconds(10));
        };
        Task<int> purge = Task.Run(() => indexer.PurgeMissingAsync(Ct), Ct);
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10), Ct));

        a.Add("new01");                                                                             // 消している間に、新着が入った
        await indexer.ScanAsync(null, Ct);                                                          // 走査は可。待機からは動き出さない
        Assert.Equal(0, Count("待機から続ける"));
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.StartAsync([KindA], Ct));
        Assert.Contains("消している最中", e.Message, StringComparison.Ordinal);
        Assert.Equal(1, a.RunCount);

        proceed.Set();
        Assert.Equal(1, await purge);

        // 消し終わったあと、止められていた動き出しを 1 度判定する(新しく入った待ちがあるので、同じ指定で動き出す)
        Assert.True(await UntilAsync(() => a.RunCount == 2));
        Assert.True(await UntilAsync(() => indexer.State.Run == IndexRunState.Waiting));
        Assert.Equal(1, Count("待機から続ける"));
        Assert.DoesNotContain("new01", a.Pending);
    }

    [Fact]
    public async Task Purging_is_allowed_while_a_scan_is_running_and_the_scan_finishes_normally_afterwards()
    {
        // 消すトランザクションと走査のバッチは、同じ書き込みの接続で直列になる(走査の排他は消す操作に関係しない)
        Indexer indexer = NewIndexer(withPurger: true);
        string path = _scan.Write("fresh.png", ScanFixture.FakePng("fresh"), _scan.Time.GetUtcNow().UtcDateTime.AddHours(-1));
        await _scan.RegisterRootAsync();
        await AddMissingImageAsync(new string('a', 64));
        var inside = new ManualResetEventSlim();
        var proceed = new ManualResetEventSlim();
        _scan.Scanner.BeforeOpenForTests = p =>
        {
            if (p != path) return;
            inside.Set();
            proceed.Wait(TimeSpan.FromSeconds(10));
        };

        Task<ScanSummary> scanning = Task.Run(() => indexer.ScanAsync(null, Ct), Ct);
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10), Ct));
        Assert.NotNull(indexer.State.Scan);                                                          // 走査の最中

        int removed = await indexer.PurgeMissingAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);   // 固まらない

        Assert.Equal(1, removed);
        proceed.Set();
        ScanSummary summary = await scanning;
        Assert.Equal(1, summary.Added);
        Assert.Equal(1, _scan.Long("SELECT count(*) FROM images"));                                  // 走査が登録した画像は、消されない
    }

    [Fact]
    public async Task A_second_purge_while_one_is_in_progress_is_refused()
    {
        Indexer indexer = NewIndexer(withPurger: true);
        await AddMissingImageAsync(new string('a', 64));
        var inside = new ManualResetEventSlim();
        var proceed = new ManualResetEventSlim();
        _afterWrite = () =>
        {
            inside.Set();
            proceed.Wait(TimeSpan.FromSeconds(10));
        };
        Task<int> first = Task.Run(() => indexer.PurgeMissingAsync(Ct), Ct);
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10), Ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.PurgeMissingAsync(Ct));

        proceed.Set();
        Assert.Equal(1, await first);
    }

    [Fact]
    public async Task An_indexer_built_without_the_purger_says_so_loudly()
    {
        Indexer indexer = NewIndexer(withPurger: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.PurgeMissingAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.CountMissingImagesAsync(Ct));
    }

    // ---------------------------------------------------------------- 飛ばした画像の理由

    [Fact]
    public async Task Skipped_items_carry_the_reason_and_the_current_path_and_live_like_the_skipped_set()
    {
        var order = new List<string>();
        HashRunner a = new(KindA, "A", order, (self, run, _) =>
        {
            foreach (string h in self.Pending)
            {
                if (h == WithPath) run.ReportSkipped(h, "C:\\lib\\a.png: 別のプロセスが使っている");
                else if (h == NoPath) run.ReportSkipped(h, "現存するパスが無い");
                else { self.Remove(h); run.ReportCompleted(); }
            }
            return RunEnd.Drained;
        });
        a.Add(WithPath, NoPath, "ok01");
        Indexer indexer = NewIndexer(withPurger: false, a);
        await Exec("INSERT INTO folders(id, path, enabled, added_at) VALUES (1, 'C:\\lib', 1, '2026-11-06T00:00:00.000Z')");
        await Exec("INSERT INTO images(hash, size, format, first_seen_at) VALUES ('" + WithPath + "', 1, 'png', '2026-11-06T00:00:00.000Z')");
        await Exec("INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size, missing_since) VALUES ('C:\\lib\\z.png', '" + WithPath + "', 1, 1, 1, '2026-11-06T01:00:00.000Z'), ('C:\\lib\\a.png', '" + WithPath + "', 1, 1, 1, NULL)");

        await indexer.StartAsync([KindA], Ct);

        IReadOnlyList<SkippedItem> items = await indexer.ListSkippedAsync(Ct);
        Assert.Equal(2, items.Count);
        SkippedItem withPath = items.Single(i => i.Hash == WithPath);
        Assert.Equal(KindA, withPath.Job);
        Assert.Equal("C:\\lib\\a.png", withPath.Path);                                               // 現存するパス(見つからない印のパスは使わない)
        Assert.Equal("C:\\lib\\a.png: 別のプロセスが使っている", withPath.Reason);
        SkippedItem noPath = items.Single(i => i.Hash == NoPath);
        Assert.Null(noPath.Path);
        Assert.Equal("現存するパスが無い", noPath.Reason);
        Assert.Equal(2, indexer.State.SkippedImages);                                                // 枚数の表示と同じ集合

        a.Remove(WithPath, NoPath);                                                    // 次の実行では、飛ばすものが無くなった(読めるようになった)
        await indexer.StartAsync([KindA], Ct);                                                       // 利用者の Start で空になる

        Assert.Empty(await indexer.ListSkippedAsync(Ct));
        Assert.Equal(0, indexer.State.SkippedImages);
    }

    [Fact]
    public async Task The_running_job_s_skipped_images_are_listed_before_it_finishes()
    {
        var order = new List<string>();
        var reported = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        HashRunner a = new(KindA, "A", order, (self, run, _) =>
        {
            run.ReportSkipped("bad01", "読めない(実行中)");
            reported.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return RunEnd.Drained;
        });
        a.Add("bad01");
        Indexer indexer = NewIndexer(withPurger: false, a);

        Task start = indexer.StartAsync([KindA], Ct);
        Assert.True(reported.Wait(TimeSpan.FromSeconds(10), Ct));

        IReadOnlyList<SkippedItem> during = await indexer.ListSkippedAsync(Ct);
        Assert.Equal("読めない(実行中)", during.Single().Reason);
        Assert.Equal(KindA, during.Single().Job);

        release.Set();
        await start;
        Assert.Single(await indexer.ListSkippedAsync(Ct));                                           // 待ちを使い切った実行の集合に置き換わる(同じ内容)
    }

    [Fact]
    public async Task A_run_that_was_cut_short_keeps_what_it_skipped_in_the_list()
    {
        var order = new List<string>();
        HashRunner a = new(KindA, "A", order, (self, run, _) =>
        {
            run.ReportSkipped(self.RunCount == 1 ? "bad01" : "bad02", self.RunCount == 1 ? "1 回目の理由" : "2 回目の理由");
            return self.RunCount == 1 ? RunEnd.Cancelled : RunEnd.Drained;
        });
        a.Add("bad01", "bad02");
        Indexer indexer = NewIndexer(withPurger: false, a);

        await indexer.StartAsync([KindA], Ct);                                                       // 1 回目は中断(Cancelled)で終わる扱い
        Assert.Contains("1 回目の理由", (await indexer.ListSkippedAsync(Ct)).Select(i => i.Reason));
    }
}
