using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Ohanami.Core.Indexing;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Scanning;
using Xunit;

namespace Ohanami.Core.Tests.Indexing;

/// <summary>台本どおりに動く偽のランナー。ランナーの選び方(優先順・Drained・Yielded)を、実際の画像なしで確かめる。</summary>
internal sealed class ScriptedRunner : JobRunner
{
    private readonly List<string> _log;
    private readonly string _name;
    private readonly Func<ScriptedRunner, RunEnd> _script;

    public ScriptedRunner(JobKind kind, string name, List<string> log, int pending, Func<ScriptedRunner, RunEnd> script)
    {
        Kind = kind;
        _name = name;
        _log = log;
        Pending = pending;
        _script = script;
    }

    public int Pending { get; set; }

    public override JobKind Kind { get; }

    public override IReadOnlyList<Stage> Stages { get; } = [Stage.Embed];

    public override IReadOnlyDictionary<Stage, string> CurrentModelIds { get; } = new Dictionary<Stage, string> { [Stage.Embed] = "scripted:0" };

    public override Task<RunEnd> RunAsync(RunRecorder run, Func<bool> shouldYield, CancellationToken ct)
    {
        lock (_log) _log.Add(_name);
        return Task.FromResult(_script(this));
    }

    public override Task ReleaseAsync()
    {
        lock (_log) _log.Add("release-" + _name);
        return Task.CompletedTask;
    }

    public override IReadOnlyList<StageSummary> Summarize() => [new StageSummary(Stage.Embed, Pending, 0, 0, 0)];
}

/// <summary>Indexer が動かすランナーを選ぶ規則(優先順。待ちが無くなったランナーは選び直さない。合図で戻ったときは選び直す)。</summary>
public sealed class IndexerSelectionTests : IDisposable
{
    private static readonly JobKind KindA = (JobKind)100;
    private static readonly JobKind KindB = (JobKind)101;

    private readonly ScanFixture _scan = new();

    public void Dispose() => _scan.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Indexer NewIndexer(params JobRunner[] runners) => new(_scan.Db, _scan.Scanner, runners, _scan.Time, "test-1.0", new TestLog());

    [Fact]
    public async Task Yielded_puts_a_drained_higher_priority_runner_back_in_the_choice()
    {
        // 優先のランナー A が Drained になったあと、B の実行中に A に新しい仕事が出て(VLM の実行中に埋め込みの新着が出る場合に当たる)、B が合図で戻る。
        // Indexer は A を選び直さなければならない(Drained の印を、Yielded のたびに消す)。
        var log = new List<string>();
        Indexer? indexer = null;
        int bCalls = 0;
        ScriptedRunner a = null!, b = null!;
        a = new ScriptedRunner(KindA, "A", log, pending: 1, script: self => { self.Pending = 0; return RunEnd.Drained; });
        b = new ScriptedRunner(KindB, "B", log, pending: 5, script: self =>
        {
            if (++bCalls == 1)
            {
                a.Pending = 1;            // A に新着
                indexer!.RequestYield();  // 「選び直してほしい」の合図
                return RunEnd.Yielded;
            }
            self.Pending = 0;
            return RunEnd.Drained;
        });
        indexer = NewIndexer(a, b);

        await indexer.StartAsync([KindA, KindB], Ct);

        string[] runs = log.Where(x => !x.StartsWith("release-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["A", "B", "A", "B"], runs);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(0, indexer.State.Stages.Sum(s => s.Pending));
    }

    [Fact]
    public async Task A_yield_request_raised_during_the_last_stretch_of_a_run_that_returns_Drained_is_not_lost()
    {
        // 優先の B(埋め込み)が Drained になって exhausted に入ったあと、A(VLM)の実行中に走査が終わって B に新着が出て、合図が立つ。
        // A はその合図を見ないまま(最後に見たあとの数 ms の間の出来事)Drained を返す。合図を捨てると、B は選ばれず、新着は次の Start まで処理されない。
        var log = new List<string>();
        Indexer? indexer = null;
        ScriptedRunner a = null!, b = null!;
        b = new ScriptedRunner(KindB, "B", log, pending: 1, script: self => { self.Pending = 0; return RunEnd.Drained; });
        a = new ScriptedRunner(KindA, "A", log, pending: 3, script: self =>
        {
            b.Pending = 1;              // 新着
            indexer!.RequestYield();    // RunAsync の中で、shouldYield を見たあとに立つ合図
            self.Pending = 0;
            return RunEnd.Drained;
        });
        indexer = NewIndexer(a, b);

        await indexer.StartAsync([KindB, KindA], Ct);   // B が優先

        Assert.Equal(["B", "A", "B"], log.Where(x => !x.StartsWith("release-", StringComparison.Ordinal)).ToArray());
        Assert.Equal(0, indexer.State.Stages.Sum(s => s.Pending));
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
    }

    [Fact]
    public async Task Drained_runner_is_not_chosen_again_without_a_yield()
    {
        // 待ちが残っているように見えても、Drained を返したランナーを同じ Start の間に選び直さない(読めずに飛ばした画像などで無限に回らない)
        var log = new List<string>();
        ScriptedRunner a = new(KindA, "A", log, pending: 3, script: _ => RunEnd.Drained); // Pending は減らさない
        ScriptedRunner b = new(KindB, "B", log, pending: 2, script: self => { self.Pending = 0; return RunEnd.Drained; });
        Indexer indexer = NewIndexer(a, b);

        await indexer.StartAsync([KindA, KindB], Ct);

        Assert.Equal(["A", "B"], log.Where(x => !x.StartsWith("release-", StringComparison.Ordinal)).ToArray());
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
    }

    [Fact]
    public async Task Higher_priority_runner_is_chosen_first_and_resources_are_released_when_switching()
    {
        var log = new List<string>();
        ScriptedRunner a = new(KindA, "A", log, pending: 1, script: self => { self.Pending = 0; return RunEnd.Drained; });
        ScriptedRunner b = new(KindB, "B", log, pending: 1, script: self => { self.Pending = 0; return RunEnd.Drained; });
        Indexer indexer = NewIndexer(a, b);

        await indexer.StartAsync([KindB, KindA], Ct); // 指定の順が優先順

        Assert.Equal(["B", "release-B", "A", "release-A"], log.ToArray()); // 切り替えるとき・終わるときに、前のランナーの資源を空ける
    }

    [Fact]
    public async Task A_runner_without_pending_work_is_never_run()
    {
        var log = new List<string>();
        ScriptedRunner a = new(KindA, "A", log, pending: 0, script: _ => RunEnd.Drained);
        ScriptedRunner b = new(KindB, "B", log, pending: 1, script: self => { self.Pending = 0; return RunEnd.Drained; });
        Indexer indexer = NewIndexer(a, b);

        await indexer.StartAsync([KindA, KindB], Ct);

        Assert.Equal(["B", "release-B"], log.ToArray());
    }

    [Fact]
    public void Two_runners_of_the_same_kind_are_rejected()
    {
        var log = new List<string>();
        ScriptedRunner a1 = new(KindA, "A1", log, 0, _ => RunEnd.Drained);
        ScriptedRunner a2 = new(KindA, "A2", log, 0, _ => RunEnd.Drained);
        Assert.Throws<ArgumentException>(() => NewIndexer(a1, a2));
    }
}
