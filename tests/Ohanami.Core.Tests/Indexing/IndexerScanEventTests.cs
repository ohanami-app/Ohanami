using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ohanami.Core.Tests.Scanning;
using Xunit;

namespace Ohanami.Core.Tests.Indexing;

/// <summary>走査のきっかけ・走査の始まりと終わりのログ・走査の完了のイベント(ADR 0009)。</summary>
public sealed class IndexerScanEventTests : IDisposable
{
    private readonly ScanFixture _scan = new();
    private readonly TestLog _log = new();
    private readonly Indexer _indexer;

    public IndexerScanEventTests()
    {
        _indexer = new Indexer(_scan.Db, _scan.Scanner, [], _scan.Time, "test-1.0", _log);
    }

    public void Dispose()
    {
        _indexer.StopAsync().GetAwaiter().GetResult();
        _scan.Dispose();
    }

    private static System.Threading.CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ScanTrigger.Startup, "起動")]
    [InlineData(ScanTrigger.Manual, "手動")]
    [InlineData(ScanTrigger.Watcher, "監視")]
    [InlineData(ScanTrigger.Periodic, "定期")]
    [InlineData(ScanTrigger.Resume, "復帰")]
    [InlineData(ScanTrigger.Missing, "見つからない")]
    [InlineData(ScanTrigger.Retry, "再試行")]
    public async Task The_scan_start_and_end_lines_carry_the_trigger_and_the_counts(ScanTrigger trigger, string label)
    {
        _scan.Write("a.png", ScanFixture.FakePng("a"), _scan.Time.GetUtcNow().UtcDateTime.AddMinutes(-5));
        await _scan.RegisterRootAsync();

        await _indexer.ScanAsync(null, trigger, Ct);

        Assert.Contains(_log.Messages, m => m == $"INFO 走査: 始める(きっかけ: {label}、フォルダ 1 個)");
        Assert.Contains(_log.Messages, m => m.StartsWith("INFO 走査: 終わった(新規 1、変更 0、消えた 0、所要 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scan_without_a_trigger_is_manual_and_names_the_enabled_folders()
    {
        _scan.Write("a.png", ScanFixture.FakePng("a"), _scan.Time.GetUtcNow().UtcDateTime.AddMinutes(-5));
        long folderId = await _scan.RegisterRootAsync();
        ScanCompletedInfo? seen = null;
        _indexer.ScanCompleted += info => seen = info;

        await _indexer.ScanAsync(null, Ct);

        Assert.NotNull(seen);
        Assert.Equal(ScanTrigger.Manual, seen!.Trigger);
        Assert.Equal([folderId], seen.FolderIds);                       // 指定が無ければ、有効なフォルダすべて
        Assert.Equal(1, seen.Summary.Added);
        Assert.Contains(_log.Messages, m => m.Contains("きっかけ: 手動", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_end_line_shows_the_transient_count_when_there_is_one()
    {
        _scan.Write("fresh.png", ScanFixture.FakePng("fresh"), _scan.Time.GetUtcNow().UtcDateTime.AddSeconds(-1));
        await _scan.RegisterRootAsync();

        ScanSummary s = await _indexer.ScanAsync(null, ScanTrigger.Watcher, Ct);

        Assert.Equal(1, s.TransientlyUnreadable);
        Assert.Contains(_log.Messages, m => m.StartsWith("INFO 走査: 終わった(", StringComparison.Ordinal) && m.Contains("一時的に読めない 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_scan_raises_no_completion_and_leaves_a_failure_line()
    {
        var seen = new List<ScanCompletedInfo>();
        _indexer.ScanCompleted += seen.Add;

        await Assert.ThrowsAsync<FolderRegistrationException>(() => _indexer.ScanAsync([9999L], ScanTrigger.Watcher, Ct));

        Assert.Empty(seen);
        Assert.DoesNotContain(_log.Messages, m => m.StartsWith("INFO 走査: 終わった", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_break_the_scan_and_is_logged()
    {
        _scan.Write("a.png", ScanFixture.FakePng("a"), _scan.Time.GetUtcNow().UtcDateTime.AddMinutes(-5));
        await _scan.RegisterRootAsync();
        _indexer.ScanCompleted += _ => throw new InvalidOperationException("購読者の失敗(テスト)");

        ScanSummary s = await _indexer.ScanAsync(null, ScanTrigger.Manual, Ct);

        Assert.Equal(1, s.Added);
        Assert.Contains(_log.Messages, m => m.StartsWith("ERROR ScanCompleted の購読者が例外を投げた", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_completion_is_raised_after_the_scan_lock_is_released_so_a_subscriber_can_scan_again()
    {
        await _scan.RegisterRootAsync();
        int calls = 0;
        _indexer.ScanCompleted += info =>
        {
            if (calls++ == 0) _indexer.ScanAsync(null, ScanTrigger.Retry, Ct).GetAwaiter().GetResult();   // 排他を握ったままなら、ここで固まる
        };

        await _indexer.ScanAsync(null, ScanTrigger.Watcher, Ct);

        Assert.Equal(2, calls);
    }
}
