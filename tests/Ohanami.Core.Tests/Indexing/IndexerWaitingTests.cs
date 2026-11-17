using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Indexing;
using Ohanami.Core.Tests.Scanning;
using Xunit;

namespace Ohanami.Core.Tests.Indexing;

/// <summary>
/// 待ちを hash の集合で持つ偽のランナー(ADR 0027 のテスト用)。本体は台本(<paramref name="body"/>)で、待ちの集合を直接いじれる。
/// 待ち = <see cref="Pending"/> の hash。<see cref="CountPendingAmong"/> は、指定した hash のうち今も待ちのもの(本物の SQL と同じ意味)。
/// </summary>
internal sealed class HashRunner(JobKind kind, string name, List<string> log, Func<HashRunner, RunRecorder, Func<bool>, RunEnd> body) : JobRunner
{
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private int _runCount;

    public override JobKind Kind { get; } = kind;

    public override IReadOnlyList<Stage> Stages { get; } = [Stage.Embed];

    public override IReadOnlyDictionary<Stage, string> CurrentModelIds { get; } = new Dictionary<Stage, string> { [Stage.Embed] = "hash-runner:0" };

    public int RunCount => Volatile.Read(ref _runCount);

    public string[] Pending { get { lock (_gate) return _pending.ToArray(); } }

    public void Add(params string[] hashes) { lock (_gate) foreach (string h in hashes) _pending.Add(h); }

    public void Remove(params string[] hashes) { lock (_gate) foreach (string h in hashes) _pending.Remove(h); }

    public void Clear() { lock (_gate) _pending.Clear(); }

    public override Task<RunEnd> RunAsync(RunRecorder run, Func<bool> shouldYield, CancellationToken ct)
    {
        Interlocked.Increment(ref _runCount);
        lock (log) log.Add(name);
        return Task.FromResult(body(this, run, shouldYield));
    }

    /// <summary>資源を空けるとき(ReleaseAsync の中)に呼ぶ処理。「選べるランナーが無い」と判断したあと・待機に入る前の窓に、走査の完了を入れるため。</summary>
    public Func<Task>? OnRelease { get; set; }

    public override async Task ReleaseAsync()
    {
        lock (log) log.Add("release-" + name);
        if (OnRelease is { } hook) await hook().ConfigureAwait(false);
    }

    public override IReadOnlyList<StageSummary> Summarize() => [new StageSummary(Stage.Embed, Pending.Length, 0, 0, 0)];

    /// <summary>動かせない理由になる、無いファイルの名前(テストが設定する。空なら動かせる)。</summary>
    public IReadOnlyList<string> Missing { get; set; } = [];

    public override IReadOnlyList<string> UnavailableFiles() => Missing;

    public override int CountPendingImages() => Pending.Length;

    public override int CountPendingAmong(IReadOnlyCollection<string> hashes)
    {
        lock (_gate) return hashes.Count(_pending.Contains);
    }

    /// <summary>既定の台本: "bad" で始まる hash は読めずに飛ばし(待ちに残る)、他は処理して待ちから外す。Drained を返す。</summary>
    public static RunEnd DrainSkippingBad(HashRunner self, RunRecorder run, Func<bool> shouldYield)
    {
        foreach (string h in self.Pending)
        {
            if (h.StartsWith("bad", StringComparison.Ordinal)) run.ReportSkipped(h, "読めない(テスト)");
            else { self.Remove(h); run.ReportCompleted(); }
        }
        return RunEnd.Drained;
    }
}

/// <summary>
/// 「続けて待つ」(Waiting)と、切り替えの条件(新しく入った待ち ≥ 20 枚、または最初に入ってから 10 分。数え直しは 5 秒以上あけて)のテスト
/// (ADR 0027。偽のランナーで、実際の画像なしで確かめる)。
/// </summary>
public sealed class IndexerWaitingTests : IDisposable
{
    private static readonly JobKind KindA = (JobKind)100;
    private static readonly JobKind KindB = (JobKind)101;

    private readonly ScanFixture _scan = new();
    private readonly TestLog _log = new();
    private readonly List<Indexer> _indexers = [];

    public void Dispose()
    {
        foreach (Indexer i in _indexers) i.StopAsync().GetAwaiter().GetResult();
        _scan.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Indexer NewIndexer(params JobRunner[] runners)
    {
        var indexer = new Indexer(_scan.Db, _scan.Scanner, runners, _scan.Time, "test-1.0", _log);
        _indexers.Add(indexer);
        return indexer;
    }

    private static HashRunner NewRunner(JobKind kind, string name, List<string> log, Func<HashRunner, RunRecorder, Func<bool>, RunEnd>? body = null) =>
        new(kind, name, log, body ?? HashRunner.DrainSkippingBad);

    private int Count(string fragment) => _log.Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

    private static string[] Hashes(string prefix, int n) => Enumerable.Range(0, n).Select(i => $"{prefix}{i:00}").ToArray();

    private Task<bool> WaitingAsync(Indexer indexer) => IndexingFixture.WaitUntilAsync(() => indexer.State.Run == IndexRunState.Waiting);

    // ---------------------------------------------------------------- 起動時・待機に入る

    [Fact]
    public async Task A_new_indexer_is_idle_and_a_scan_does_not_start_anything()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        a.Add("x01");
        Indexer indexer = NewIndexer(a);

        Assert.Equal(IndexRunState.Idle, indexer.State.Run);   // 起動時は Idle(覚えない。起動して検索だけ使うとき GPU を取らない)
        await indexer.ScanAsync(null, Ct);

        Assert.Equal(IndexRunState.Idle, indexer.State.Run);
        Assert.Equal(0, a.RunCount);
        Assert.Equal(0, Count("待機から続ける"));
    }

    [Fact]
    public async Task When_the_queue_runs_dry_the_job_waits_instead_of_ending_and_start_returns()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        a.Add("x01", "x02");
        Indexer indexer = NewIndexer(a);

        await indexer.StartAsync([KindA], Ct);

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(["A", "release-A"], order);                 // 待機に入るとき、資源は空ける
        Assert.Null(indexer.State.CurrentJob);
    }

    [Fact]
    public async Task Starting_with_nothing_to_do_also_waits()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        Indexer indexer = NewIndexer(a);

        await indexer.StartAsync([KindA], Ct);

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(0, a.RunCount);
    }

    // ---------------------------------------------------------------- 待機 → 実行(走査の完了。新しく入った待ち ≥ 1)

    [Fact]
    public async Task A_skipped_image_alone_does_not_wake_the_job_on_every_scan()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        a.Add("bad01", "ok01", "ok02");
        Indexer indexer = NewIndexer(a);
        await indexer.StartAsync([KindA], Ct);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(1, indexer.State.SkippedImages);
        Assert.Equal(["bad01"], a.Pending);                       // 飛ばした画像は待ちに残る(Pending に数え続ける)

        for (int i = 0; i < 3; i++) await indexer.ScanAsync(null, Ct);   // 走査のたびに往復しない

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(1, a.RunCount);
        Assert.Equal(0, Count("待機から続ける"));
    }

    [Fact]
    public async Task A_scan_that_adds_a_new_image_wakes_the_waiting_job_with_the_same_specification_and_keeps_the_skipped_set()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        a.Add("bad01", "ok01");
        Indexer indexer = NewIndexer(a);
        await indexer.StartAsync([KindA], Ct);

        a.Add("new01");   // 新着(走査が入れた)
        await indexer.ScanAsync(null, Ct);

        Assert.True(await WaitingAsync(indexer));
        Assert.True(await IndexingFixture.WaitUntilAsync(() => a.RunCount == 2));
        Assert.Equal(1, Count("待機から続ける"));
        Assert.Equal(["bad01"], a.Pending);                       // 新着は処理した。飛ばした画像は待ちのまま
        Assert.Equal(1, indexer.State.SkippedImages);              // 自動の再開は飛ばした集合を消さない(数えるのは 1 枚のまま)

        await indexer.ScanAsync(null, Ct);                        // もう新着は無い。再び動かない
        Assert.Equal(2, a.RunCount);
        Assert.Contains(_log.Messages, m => m == "INFO ジョブ: 待機から続ける(新しい待ち 100 1 枚。指定: 100)");
    }

    [Fact]
    public async Task The_continue_line_counts_the_new_pending_per_stage_and_not_as_a_sum()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        HashRunner b = NewRunner(KindB, "B", order);
        a.Add("ok01");
        b.Add("ok01");
        Indexer indexer = NewIndexer(a, b);
        await indexer.StartAsync([KindA, KindB], Ct);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);

        a.Add("new01");   // 1 枚の新着は、両方の段階の待ちに入る(走査が入れる)
        b.Add("new01");
        await indexer.ScanAsync(null, Ct);

        // 「新しい画像 2 枚」と、足した合計を出すと、1 枚の新着が 2 枚に読める。段階ごとに出す
        Assert.True(await IndexingFixture.WaitUntilAsync(() => Count("待機から続ける") == 1));
        Assert.Contains(_log.Messages, m => m == "INFO ジョブ: 待機から続ける(新しい待ち 100 1 枚、101 1 枚。指定: 100、101)");
        Assert.True(await IndexingFixture.WaitUntilAsync(() => a.RunCount == 2 && b.RunCount == 2));
        Assert.True(await WaitingAsync(indexer));
        // 2 回目の実行の通しの行も、段階ごとの内訳(最初の通しは 1 枚ずつ、2 回目も 1 枚ずつ)
        Assert.Contains(_log.Messages, m => m == "INFO ジョブ: 待機に入る(新しい画像が来たら続ける。処理 100 1 枚・101 1 枚、失敗 100 0・101 0、飛ばした(画像) 0 枚、所要 00:00:00)");
    }

    [Fact]
    public async Task A_vanished_skipped_image_and_a_new_image_in_the_same_scan_still_count_the_new_one()
    {
        // 数で持つと差し引き 0 になる場面(飛ばした画像が消える = 待ちが 1 減り、新着が 1 増える)。hash で引くので、新着は新着として数える
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        a.Add("bad01", "ok01");
        Indexer indexer = NewIndexer(a);
        await indexer.StartAsync([KindA], Ct);
        Assert.Equal(["bad01"], a.Pending);

        a.Remove("bad01");   // 飛ばした画像が消えた(missing になった)
        a.Add("new01");      // 同じ走査で新着
        await indexer.ScanAsync(null, Ct);

        Assert.True(await IndexingFixture.WaitUntilAsync(() => a.RunCount == 2));
        Assert.Equal(1, Count("待機から続ける"));
    }

    [Fact]
    public async Task A_scan_running_in_parallel_with_the_job_does_not_mix_its_new_images_into_the_baseline()
    {
        // ジョブの実行中(Running)に走査が新着を足しても、実行が終わったあとの待機の判定は「待ち − 飛ばした集合」。終わった瞬間の待ちの数が基準にならない
        var order = new List<string>();
        Indexer indexer = null!;
        HashRunner a = NewRunner(KindA, "A", order, (self, run, _) =>
        {
            if (self.RunCount == 1)
            {
                run.ReportSkipped("bad01", "読めない(テスト)");
                self.Remove("ok01");
                self.Add("new-from-parallel-scan");   // 実行中に並行の走査が足した新着(走査の完了のときは、まだ Running)
            }
            else
            {
                foreach (string h in self.Pending.Where(h => !h.StartsWith("bad", StringComparison.Ordinal))) self.Remove(h);
            }
            return RunEnd.Drained;
        });
        a.Add("bad01", "ok01");
        indexer = NewIndexer(a);

        await indexer.StartAsync([KindA], Ct);

        // 1 回目が終わった時点で、新着 1 枚が待ちに残っている(飛ばした集合にも無い)。待機に入ったが、次の走査の完了で動き出す
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Contains("new-from-parallel-scan", a.Pending);
        await indexer.ScanAsync(null, Ct);
        Assert.True(await IndexingFixture.WaitUntilAsync(() => a.RunCount == 2));
        Assert.True(await WaitingAsync(indexer));                  // 2 回目の実行が終わって待機に戻るまで待つ(RunCount は実行の頭で増える)
        Assert.Equal(["bad01"], a.Pending);
    }

    // ---------------------------------------------------------------- 一時停止・待機中の Start / Pause

    [Fact]
    public async Task Pause_while_waiting_goes_to_paused_and_a_scan_does_not_wake_it_but_resume_does()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        a.Add("ok01");
        Indexer indexer = NewIndexer(a);
        await indexer.StartAsync([KindA], Ct);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);

        await indexer.PauseAsync();

        Assert.Equal(IndexRunState.Paused, indexer.State.Run);
        a.Add("new01");
        await indexer.ScanAsync(null, Ct);                         // 一時停止中は、新着があっても自動では動かない
        Assert.Equal(IndexRunState.Paused, indexer.State.Run);
        Assert.Equal(1, a.RunCount);
        Assert.Contains(_log.Messages, m => m.Contains("ジョブ: 一時停止した(待機中から)", StringComparison.Ordinal));

        await indexer.ResumeAsync(Ct);                             // 再開(利用者の操作)は動く

        Assert.Equal(2, a.RunCount);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Empty(a.Pending);
    }

    [Fact]
    public async Task Start_while_waiting_starts_with_the_new_specification_and_forgets_the_skipped_set()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        HashRunner b = NewRunner(KindB, "B", order);
        a.Add("bad01");
        Indexer indexer = NewIndexer(a, b);
        await indexer.StartAsync([KindA], Ct);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(1, indexer.State.SkippedImages);
        b.Add("ok01");

        await indexer.StartAsync([KindB], Ct);                     // 待機中の Start は例外にしない(Paused と同じ)

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(1, b.RunCount);
        Assert.Equal(0, indexer.State.SkippedImages);              // 利用者の Start で、飛ばした集合は空になる
        a.Add("new01");
        await indexer.ScanAsync(null, Ct);                         // 指定は [B] に替わった。A の新着では動かない
        Assert.Equal(0, Count("待機から続ける"));
    }

    [Fact]
    public async Task Failed_and_requeue_operations_do_not_wake_a_waiting_job()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        a.Add("ok01");
        Indexer indexer = NewIndexer(a);
        await indexer.StartAsync([KindA], Ct);
        a.Add("new01");

        await indexer.RetryFailedAsync(KindA, Ct);
        await indexer.RequeueOutdatedAsync(KindA, Ct);

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);    // 走査の完了のときだけ判定する
        Assert.Equal(1, a.RunCount);
    }

    [Fact]
    public async Task A_caller_cancellation_ends_as_idle_not_waiting_and_a_scan_does_not_wake_it()
    {
        var order = new List<string>();
        using var cts = new CancellationTokenSource();
        HashRunner a = NewRunner(KindA, "A", order, (self, run, _) => { cts.Cancel(); return RunEnd.Cancelled; });
        a.Add("ok01");
        Indexer indexer = NewIndexer(a);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indexer.StartAsync([KindA], cts.Token));

        Assert.Equal(IndexRunState.Idle, indexer.State.Run);
        await indexer.ScanAsync(null, Ct);
        Assert.Equal(IndexRunState.Idle, indexer.State.Run);
    }

    // ---------------------------------------------------------------- 切り替えの条件(20 枚・10 分・5 秒)

    /// <summary>A(優先)・B の順で、B が動いている間に台本どおりに A の待ちを増やして、B の shouldYield の答えを集める。</summary>
    private async Task<(List<string> Order, List<bool> Answers, HashRunner A, HashRunner B)> RunSwitchScenarioAsync(
        Action<HashRunner, Indexer, List<bool>, Func<bool>> firstBRun)
    {
        var order = new List<string>();
        var answers = new List<bool>();
        Indexer indexer = null!;
        HashRunner a = null!;
        a = NewRunner(KindA, "A", order);
        HashRunner b = null!;
        b = NewRunner(KindB, "B", order, (self, run, shouldYield) =>
        {
            if (self.RunCount == 1)
            {
                firstBRun(a, indexer, answers, shouldYield);
                return answers[^1] ? RunEnd.Yielded : RunEnd.Drained;
            }
            self.Clear();
            return RunEnd.Drained;
        });
        b.Add("b01");
        indexer = NewIndexer(a, b);
        await indexer.StartAsync([KindA, KindB], Ct);
        return (order, answers, a, b);
    }

    [Fact]
    public async Task Nineteen_new_images_do_not_switch_but_twenty_do()
    {
        (List<string> order, List<bool> answers, HashRunner a, _) = await RunSwitchScenarioAsync((a, indexer, ans, shouldYield) =>
        {
            a.Add(Hashes("n", 19));
            indexer.ScanAsync(null, Ct).GetAwaiter().GetResult();   // 走査の完了で数える(19 枚)
            ans.Add(shouldYield());                                  // false(直後の数え直しは間引かれ、合図も無い)
            a.Add("n19");                                            // 20 枚目。走査はしない
            ans.Add(shouldYield());                                  // 前回の数え直しから 5 秒たっていない → まだ数え直さない
            _scan.Time.Advance(TimeSpan.FromSeconds(6));
            ans.Add(shouldYield());                                  // 5 秒たった → 数え直して 20 枚 → true
        });

        Assert.Equal([false, false, true], answers);
        string[] runs = order.Where(x => !x.StartsWith("release-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["B", "A", "B"], runs);                          // B → (合図) → A が 20 枚を処理 → B の続き
        Assert.Empty(a.Pending);
    }

    [Fact]
    public async Task A_scan_completion_with_twenty_new_images_switches_immediately()
    {
        (_, List<bool> answers, _, _) = await RunSwitchScenarioAsync((a, indexer, ans, shouldYield) =>
        {
            a.Add(Hashes("n", 20));
            indexer.ScanAsync(null, Ct).GetAwaiter().GetResult();   // 走査の完了で、すぐ数える
            ans.Add(shouldYield());                                  // 合図が立っている → true
        });

        Assert.Equal([true], answers);
    }

    [Fact]
    public async Task One_new_image_switches_only_after_ten_minutes_since_it_first_appeared()
    {
        (_, List<bool> answers, _, _) = await RunSwitchScenarioAsync((a, indexer, ans, shouldYield) =>
        {
            a.Add("n01");
            indexer.ScanAsync(null, Ct).GetAwaiter().GetResult();   // 最初に見えた時刻 = ここ
            ans.Add(shouldYield());                                  // false
            _scan.Time.Advance(TimeSpan.FromMinutes(9));
            ans.Add(shouldYield());                                  // 9 分: まだ
            _scan.Time.Advance(TimeSpan.FromMinutes(2));
            ans.Add(shouldYield());                                  // 11 分: 10 分を過ぎた → true
        });

        Assert.Equal([false, false, true], answers);
    }

    [Fact]
    public async Task The_ten_minute_clock_restarts_when_the_new_images_were_all_processed_in_between()
    {
        (_, List<bool> answers, _, _) = await RunSwitchScenarioAsync((a, indexer, ans, shouldYield) =>
        {
            a.Add("n01");
            indexer.ScanAsync(null, Ct).GetAwaiter().GetResult();   // 最初に見えた時刻 = t0
            _scan.Time.Advance(TimeSpan.FromMinutes(6));
            a.Clear();                                               // 新着が無くなった(別の経路で処理された、消えた、など)
            ans.Add(shouldYield());                                  // 数え直して 0 → 時刻の記録を消す。false
            a.Add("n02");
            _scan.Time.Advance(TimeSpan.FromMinutes(6));             // t0 から 12 分。ただし n02 が最初に見えるのは、この数え直し
            ans.Add(shouldYield());                                  // false(ここで初めて見えた)
            _scan.Time.Advance(TimeSpan.FromMinutes(11));
            ans.Add(shouldYield());                                  // n02 が見えてから 11 分 → true
        });

        Assert.Equal([false, false, true], answers);
    }

    [Fact]
    public async Task The_highest_priority_job_never_counts_anything_in_should_yield()
    {
        var order = new List<string>();
        var answers = new List<bool>();
        HashRunner b = NewRunner(KindB, "B", order);
        HashRunner a = null!;
        a = NewRunner(KindA, "A", order, (self, run, shouldYield) =>
        {
            b.Add(Hashes("n", 30));                                  // 後ろの順のジョブに新着が 30 枚。優先の A の実行中は、数えない(短絡)
            _scan.Time.Advance(TimeSpan.FromMinutes(30));
            answers.Add(shouldYield());
            self.Clear();
            return RunEnd.Drained;
        });
        a.Add("a01");
        Indexer indexer = NewIndexer(a, b);

        await indexer.StartAsync([KindA, KindB], Ct);

        Assert.Equal([false], answers);
    }

    // ---------------------------------------------------------------- 待ちを使い切ったあとの取り残し(設計に足した点)

    [Fact]
    public async Task New_images_below_the_switch_threshold_are_not_stranded_when_the_lower_job_finishes()
    {
        // A(優先)が先に Drained → B が動く → B の実行中に A へ新着 1 枚(20 枚・10 分に届かない)→ B が Drained。取り残さず、A を選び直す
        var order = new List<string>();
        HashRunner a = null!;
        a = NewRunner(KindA, "A", order);
        a.Add("a01");
        HashRunner b = NewRunner(KindB, "B", order, (self, run, shouldYield) =>
        {
            a.Add("late01");
            self.Clear();
            return RunEnd.Drained;
        });
        b.Add("b01");
        Indexer indexer = NewIndexer(a, b);

        await indexer.StartAsync([KindA, KindB], Ct);

        string[] runs = order.Where(x => !x.StartsWith("release-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["A", "B", "A"], runs);
        Assert.Empty(a.Pending);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
    }

    [Fact]
    public async Task A_drained_runner_whose_pending_did_not_shrink_is_not_chosen_again()
    {
        // 待ちが減らないランナー(飛ばした画像を報告しない偽物)で無限に回らない。Drained の時点の「新しく入った待ち」より増えたときだけ選び直す
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order, (self, run, _) => RunEnd.Drained);
        a.Add("stuck01", "stuck02");
        Indexer indexer = NewIndexer(a);

        await indexer.StartAsync([KindA], Ct);

        Assert.Equal(1, a.RunCount);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
    }

    // ---------------------------------------------------------------- 取り残しの窓(待機に入る直前の走査の完了)

    [Fact]
    public async Task A_scan_that_completes_while_resources_are_being_released_before_waiting_is_not_stranded()
    {
        // ループが「選べるランナーが無い」と判断したあと、資源を空ける間に走査が終わって新着が入った。待機に入ると、その新着は次の走査の完了まで見られない。
        // 走査の完了ごとに世代が増え、待機に入る(_stateLock の中)とき、判断の前に読んだ世代と違えば、もう一度回る
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        a.Add("a01");
        Indexer indexer = NewIndexer(a);
        bool injected = false;
        a.OnRelease = async () =>
        {
            if (injected) return;
            injected = true;
            a.Add("late01");                         // 窓の中で入った新着
            await indexer.ScanAsync(null, Ct);       // 走査の完了(状態はまだ Running)
        };

        await indexer.StartAsync([KindA], Ct);

        Assert.Equal(2, a.RunCount);                  // 窓の中の新着のために、もう一度回った
        Assert.Empty(a.Pending);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(0, Count("待機から続ける"));    // 待機に入ったあとの再開ではない(窓の中で拾った)
    }

    [Fact]
    public async Task A_scan_completion_in_the_window_that_adds_nothing_does_not_loop_forever()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        a.Add("a01");
        Indexer indexer = NewIndexer(a);
        int injections = 0;
        a.OnRelease = async () =>
        {
            if (injections++ == 0) await indexer.ScanAsync(null, Ct);   // 新着の無い走査の完了
        };

        await indexer.StartAsync([KindA], Ct);

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(1, a.RunCount);                  // 新着が無ければ、選び直しても動かすランナーは無く、そのまま待機に入る
        Assert.Equal(1, injections);                  // 2 周目は動かすランナーが無く、空ける資源も無い(ReleaseAsync は 1 回だけ)
    }

    // ---------------------------------------------------------------- ログ(4.3)

    [Fact]
    public async Task Job_end_waiting_and_pause_lines_carry_counts_and_the_elapsed_time()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order, (self, run, _) =>
        {
            run.ReportSkipped("bad01", "読めない(テスト)");
            run.ReportCompleted();
            run.ReportCompleted(failed: true);
            return RunEnd.Drained;
        });
        a.Add("bad01", "ok01", "ok02");
        Indexer indexer = NewIndexer(a);

        await indexer.StartAsync([KindA], Ct);

        // ランナーの行は、その段階の数。実行の通しの行(待機に入る・一時停止)は、段階ごとの内訳(ここでは段階の名前が「100」)。飛ばしたのは、どちらも画像の数
        Assert.Contains(_log.Messages, m => m == "INFO ジョブ: 100 が終わった(処理 2 枚、失敗 1、飛ばした(画像) 1 枚、所要 00:00:00)");
        Assert.Contains(_log.Messages, m => m == "INFO ジョブ: 待機に入る(新しい画像が来たら続ける。処理 100 2 枚、失敗 100 1、飛ばした(画像) 1 枚、所要 00:00:00)");
    }

    [Fact]
    public async Task Pause_in_the_middle_logs_the_stop_with_its_reason()
    {
        var order = new List<string>();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        HashRunner a = null!;
        a = NewRunner(KindA, "A", order, (self, run, shouldYield) =>
        {
            run.ReportCompleted();
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return RunEnd.Cancelled;   // 一時停止で ct が切れた
        });
        a.Add("ok01");
        Indexer indexer = NewIndexer(a);

        Task start = indexer.StartAsync([KindA], Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), Ct));
        Task pause = indexer.PauseAsync();
        release.Set();
        await pause;
        await start;

        Assert.Equal(IndexRunState.Paused, indexer.State.Run);
        Assert.Contains(_log.Messages, m => m == "INFO ジョブ: 100 を止めた(理由: 一時停止。処理 1 枚、失敗 0、飛ばした(画像) 0 枚、所要 00:00:00)");
        Assert.Contains(_log.Messages, m => m == "INFO ジョブ: 一時停止した(処理 100 1 枚、失敗 100 0、飛ばした(画像) 0 枚、所要 00:00:00)");
    }
}
