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
/// 「覚えているジョブの指定」が、再開の操作で狭まる罠と、その直し方の確認(ADR 0039)。
/// 罠: 「VLM を始める」(指定 = [Embed, Vlm])→ 待機中 → 埋め込みの失敗だけがある → 「失敗をやり直す」が、**埋め込みだけの指定** <c>StartAsync([Embed])</c> を呼んでいた。
/// Core の <c>StartCore</c> は指定を置き換える(Indexer.cs の <c>_jobs = jobs.Distinct().ToList()</c>。<c>IndexerWaitingTests.Start_while_waiting_starts_with_the_new_specification…</c> が、
/// 利用者が明示した新しい指定への置き換えとして確かめている)ので、待機に戻ったあとの自動の再開は [Embed] だけになり、新しい画像に VLM が自動でかからなかった
/// (埋め込みだけの指定で呼ぶと 2 ケースとも VLM が自動で続かないことは、このテストの指定を [Embed] にすると確かめられる)。
/// <para>
/// **直し方(ADR 0039)**: Core は変えず、App が <c>StartAsync</c> に渡す指定を、いつも [Embed, Vlm] の 1 通りにした(<c>IndexPanel.AllJobs</c>。
/// 呼ぶのは 1 か所で、App.Tests の <c>StartAsyncSingleCallSiteTests</c> がソースを読んで見張る)。ここは、その指定で戻して始めたあとも、覚えている指定が [Embed, Vlm] のままで、
/// 待機からの自動の再開で VLM もかかることを、Core の偽のランナーで確かめる。
/// </para>
/// </summary>
public sealed class IndexerSpecNarrowingTests : IDisposable
{
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

    /// <summary>「VLM を始める」のあとの待機中: 指定 [Embed, Vlm]、待ちは両方 0。</summary>
    private async Task<(Indexer Indexer, HashRunner Embed, HashRunner Vlm)> WaitingWithEmbedAndVlmAsync()
    {
        var order = new List<string>();
        var embed = new HashRunner(JobKind.Embed, "embed", order, HashRunner.DrainSkippingBad);
        var vlm = new HashRunner(JobKind.Vlm, "vlm", order, HashRunner.DrainSkippingBad);
        embed.Add("ok01");
        vlm.Add("ok01");
        Indexer indexer = NewIndexer(embed, vlm);
        await indexer.StartAsync([JobKind.Embed, JobKind.Vlm], Ct);          // 「VLM を始める」(IndexPanel.StartVlmAsync)
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(1, embed.RunCount);
        Assert.Equal(1, vlm.RunCount);
        return (indexer, embed, vlm);
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("requeue")]
    public async Task After_a_retry_or_requeue_the_start_with_the_one_specification_keeps_the_vlm_in_the_auto_resume(string operation)
    {
        (Indexer indexer, HashRunner embed, HashRunner vlm) = await WaitingWithEmbedAndVlmAsync();

        // 埋め込みの失敗だけがある(VLM の失敗は無い)→ 「失敗をやり直す」/「要再作成を始める」: 待ちに戻して、いつもの指定 [Embed, Vlm] で始める(IndexPanel.AllJobs)
        embed.Add("retry01");
        if (operation == "retry") await indexer.RetryFailedAsync(JobKind.Embed, Ct);
        else await indexer.RequeueOutdatedAsync(JobKind.Embed, Ct);
        await indexer.StartAsync([JobKind.Embed, JobKind.Vlm], Ct);          // App の唯一の呼び出し(IndexPanel.StartProcessingAsync)と同じ指定
        Assert.Equal(IndexRunState.Waiting, indexer.State.Run);
        Assert.Equal(2, embed.RunCount);                                     // やり直した
        Assert.Equal(1, vlm.RunCount);                                       // VLM は動かさない(望ましい: 失敗が埋め込みだけなので)

        // 新しい画像が来る(走査が見つける。1 枚の新着は、埋め込みと VLM の両方の待ちに入る)
        embed.Add("new01");
        vlm.Add("new01");
        await indexer.ScanAsync(null, Ct);

        // 覚えている指定は [Embed, Vlm] のまま: 待機から自動で続けて、新着に VLM もかかる
        Assert.True(await IndexingFixture.WaitUntilAsync(() => embed.RunCount == 3), "埋め込みは自動で続く");
        Assert.True(await IndexingFixture.WaitUntilAsync(() => vlm.RunCount == 2), "VLM も自動で続く(指定が狭まっていないので)");
        Assert.Empty(vlm.Pending);
    }
}
