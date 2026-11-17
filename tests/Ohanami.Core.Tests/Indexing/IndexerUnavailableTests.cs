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
/// モデルのファイルが無くて動かせないジョブは、失敗ではなく「未取得」の状態になる(偽のランナーで確かめる)。
/// 動かせるジョブは普通に進み、終わると待機に入る。ファイルがそろえば、次の開始、または待機中の次の走査の完了で動き出す。
/// </summary>
public sealed class IndexerUnavailableTests : IDisposable
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

    private static HashRunner NewRunner(JobKind kind, string name, List<string> order) => new(kind, name, order, HashRunner.DrainSkippingBad);

    private int Count(string fragment) => _log.Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public async Task A_job_whose_model_files_are_missing_is_not_run_and_the_run_waits_instead_of_failing()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        HashRunner b = NewRunner(KindB, "B", order);
        a.Add("a01", "a02");
        b.Add("b01", "b02", "b03");
        b.Missing = ["m/model.gguf"];
        Indexer indexer = NewIndexer(a, b);

        await indexer.StartAsync([KindA, KindB], Ct);

        IndexState state = indexer.State;
        Assert.Equal(IndexRunState.Waiting, state.Run);                 // Failed ではない。待機に入る
        Assert.Null(state.LastError);
        Assert.Equal(["A", "release-A"], order);                        // A は普通に進んで終わり、B は選ばれない
        Assert.Empty(a.Pending);
        Assert.Equal(["b01", "b02", "b03"], b.Pending.Order().ToArray());   // B の待ちは待ちのまま残る
        Assert.Equal(0, b.RunCount);
        UnavailableJob unavailable = Assert.Single(state.UnavailableJobs);
        Assert.Equal(KindB, unavailable.Job);
        Assert.Equal(["m/model.gguf"], unavailable.MissingFiles);
    }

    [Fact]
    public async Task The_reason_is_logged_once_per_run()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        HashRunner b = NewRunner(KindB, "B", order);
        a.Add("a01");
        b.Add("b01");
        b.Missing = ["m/model.gguf"];
        Indexer indexer = NewIndexer(a, b);

        await indexer.StartAsync([KindA, KindB], Ct);

        Assert.Equal(1, Count("モデルのファイルが無いので動かさない"));
        Assert.Contains(_log.Messages, m => m.Contains("未取得: m/model.gguf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_files_are_there_the_next_start_runs_the_job()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        HashRunner b = NewRunner(KindB, "B", order);
        a.Add("a01");
        b.Add("b01", "b02");
        b.Missing = ["m/model.gguf"];
        Indexer indexer = NewIndexer(a, b);
        await indexer.StartAsync([KindA, KindB], Ct);
        Assert.Equal(0, b.RunCount);

        b.Missing = [];   // 利用者がファイルをそろえた
        Assert.Empty(indexer.State.UnavailableJobs);
        await indexer.StartAsync([KindA, KindB], Ct);

        Assert.Equal(1, b.RunCount);
        Assert.Empty(b.Pending);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
    }

    [Fact]
    public async Task A_new_image_while_the_files_are_missing_does_not_wake_the_waiting_run()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        HashRunner b = NewRunner(KindB, "B", order);
        b.Add("b01");
        b.Missing = ["m/model.gguf"];
        Indexer indexer = NewIndexer(a, b);
        await indexer.StartAsync([KindA, KindB], Ct);
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);

        b.Add("b02");   // 走査が入れた新着。B は動かせないので、新しい待ちに数えない
        for (int i = 0; i < 3; i++) await indexer.ScanAsync(null, Ct);

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(0, b.RunCount);
        Assert.Equal(0, Count("待機から続ける"));
    }

    [Fact]
    public async Task When_the_files_become_available_the_next_scan_with_new_images_wakes_the_waiting_run()
    {
        var order = new List<string>();
        HashRunner a = NewRunner(KindA, "A", order);
        HashRunner b = NewRunner(KindB, "B", order);
        b.Add("b01");
        b.Missing = ["m/model.gguf"];
        Indexer indexer = NewIndexer(a, b);
        await indexer.StartAsync([KindA, KindB], Ct);

        b.Missing = [];
        await indexer.ScanAsync(null, Ct);   // 待ちの b01 が、いまは動かせるので、新しい待ちに数えられる

        Assert.True(await IndexingFixture.WaitUntilAsync(() => b.RunCount == 1));
        Assert.True(await IndexingFixture.WaitUntilAsync(() => indexer.State.Run == IndexRunState.Waiting));
        Assert.Empty(b.Pending);
    }

    [Fact]
    public async Task A_run_with_only_unavailable_jobs_waits_without_running_anything()
    {
        var order = new List<string>();
        HashRunner b = NewRunner(KindB, "B", order);
        b.Add("b01");
        b.Missing = ["m/model.gguf"];
        Indexer indexer = NewIndexer(b);

        await indexer.StartAsync([KindB], Ct);

        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(0, b.RunCount);
    }
}
