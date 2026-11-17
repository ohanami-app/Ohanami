using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

public sealed class ThumbnailLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Hash(char c) => new(c, 64);

    private static ThumbnailResult OkResult(int w, int h, byte fill = 7)
    {
        var bgra = new byte[w * h * 4];
        Array.Fill(bgra, fill);
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return ThumbnailResult.Ok(new ThumbnailPixels(w, h, bgra), fromCache: false);
    }

    /// <summary>呼ばれた順を記録する偽の作り手。</summary>
    private sealed class FakeFetch
    {
        private readonly object _gate = new();
        private readonly List<string> _calls = [];
        public Func<string, CancellationToken, Task<ThumbnailResult>> Behavior { get; set; } = (_, _) => Task.FromResult(OkResult(8, 4));

        public IReadOnlyList<string> Calls { get { lock (_gate) return _calls.ToArray(); } }

        public async Task<ThumbnailResult> Invoke(string hash, int w, int h, CancellationToken ct)
        {
            lock (_gate) _calls.Add(hash);
            return await Behavior(hash, ct);
        }
    }

    // ---------------------------------------------------------------- Core の画素 → BitmapSource

    [Fact]
    public void Ok_pixels_become_a_frozen_bgr32_bitmap_with_the_same_size_and_bytes()
    {
        var bgra = new byte[3 * 2 * 4];
        for (int i = 0; i < bgra.Length; i++) bgra[i] = (byte)(i * 3 + 1);
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;

        LoadedThumb thumb = ThumbnailLoader.ToThumb(Hash('a'), ThumbnailResult.Ok(new ThumbnailPixels(3, 2, bgra), true));

        Assert.Equal(ThumbnailStatus.Ok, thumb.Status);
        BitmapSource bmp = Assert.IsAssignableFrom<BitmapSource>(thumb.Bitmap);
        Assert.True(bmp.IsFrozen); // UI スレッド以外で作るので、固定していないと使えない
        Assert.Equal((3, 2), (bmp.PixelWidth, bmp.PixelHeight));
        Assert.Equal(PixelFormats.Bgr32, bmp.Format);
        var back = new byte[bgra.Length];
        bmp.CopyPixels(back, 3 * 4, 0);
        Assert.Equal(bgra, back);
    }

    [Theory]
    [InlineData(ThumbnailStatus.SourceUnavailable)]
    [InlineData(ThumbnailStatus.SourceUndecodable)]
    public void Failed_results_keep_the_status_and_message_and_have_no_bitmap(ThumbnailStatus status)
    {
        LoadedThumb thumb = ThumbnailLoader.ToThumb(Hash('b'), ThumbnailResult.Failure(status, "理由"));

        Assert.Equal(status, thumb.Status);
        Assert.Equal("理由", thumb.Message);
        Assert.Null(thumb.Bitmap);
    }

    [Fact]
    public void An_ok_result_without_pixels_is_a_bug_and_fails_loudly()
    {
        Assert.Throws<InvalidOperationException>(() => ThumbnailLoader.ToThumb(Hash('c'), new ThumbnailResult(ThumbnailStatus.Ok, null, false, null)));
    }

    // ---------------------------------------------------------------- ローダー

    [Fact]
    public async Task Wanted_hashes_are_made_in_priority_order_and_drained_into_the_lru()
    {
        var fetch = new FakeFetch();
        using var loader = new ThumbnailLoader(fetch.Invoke, new TestLog(), 64, 36, capacity: 100, workers: 1);
        loader.SetProtected([Hash('a'), Hash('b')]);

        loader.SetWanted([Hash('a'), Hash('b'), Hash('c')]);
        Assert.True(await Wait.UntilAsync(() => fetch.Calls.Count == 3));
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 3));

        Assert.Equal([Hash('a'), Hash('b'), Hash('c')], fetch.Calls); // 1 本のワーカーなら、渡した順
        Assert.True(loader.Drain());                                    // 見えているもの(a, b)が入った
        Assert.Equal(3, loader.Count);
        Assert.True(loader.TryGet(Hash('a'), out LoadedThumb? a));
        Assert.Equal(ThumbnailStatus.Ok, a!.Status);
        Assert.False(loader.Drain());                                   // もう何も無い
        Assert.Equal(0, loader.InFlightOrQueued);
    }

    [Fact]
    public async Task A_hash_is_made_once_even_if_it_is_requested_again_and_again()
    {
        var fetch = new FakeFetch();
        using var loader = new ThumbnailLoader(fetch.Invoke, new TestLog(), 64, 36, capacity: 100, workers: 3);

        for (int i = 0; i < 5; i++) loader.SetWanted([Hash('a'), Hash('b')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 2));
        await Task.Delay(100, Ct); // 余計な呼び出しが後から来ないことの確認

        Assert.Equal(2, fetch.Calls.Count);
    }

    [Fact]
    public async Task Drain_reports_a_change_only_when_a_visible_thumbnail_arrived()
    {
        var fetch = new FakeFetch();
        using var loader = new ThumbnailLoader(fetch.Invoke, new TestLog(), 64, 36, capacity: 100, workers: 1);
        loader.SetProtected([Hash('a')]);

        loader.SetWanted([Hash('z')]); // 見えていない先読み
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 1));
        Assert.False(loader.Drain());
        Assert.True(loader.Contains(Hash('z')));

        loader.SetWanted([Hash('a')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 2));
        Assert.True(loader.Drain());
    }

    [Fact]
    public async Task Over_capacity_the_oldest_invisible_thumbnails_go_first_and_visible_ones_stay()
    {
        var fetch = new FakeFetch();
        using var loader = new ThumbnailLoader(fetch.Invoke, new TestLog(), 64, 36, capacity: 2, workers: 1);
        loader.SetProtected([Hash('a')]);

        loader.SetWanted([Hash('a'), Hash('b'), Hash('c'), Hash('d')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 4));
        loader.Drain();

        Assert.Equal(2, loader.Count);
        Assert.True(loader.Contains(Hash('a')));   // 見えているものは、一番古くても残る
        Assert.True(loader.Contains(Hash('d')));   // 残りは新しいほう
        Assert.False(loader.Contains(Hash('b')));
        Assert.False(loader.Contains(Hash('c')));
    }

    [Fact]
    public async Task An_evicted_hash_can_be_made_again_when_it_is_wanted_again()
    {
        var fetch = new FakeFetch();
        using var loader = new ThumbnailLoader(fetch.Invoke, new TestLog(), 64, 36, capacity: 1, workers: 1);
        loader.SetWanted([Hash('a'), Hash('b')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 2));
        loader.Drain();
        Assert.False(loader.Contains(Hash('a')));

        loader.SetWanted([Hash('a')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 3));

        Assert.Equal(3, fetch.Calls.Count);
        Assert.Equal(Hash('a'), fetch.Calls[2]);
    }

    [Fact]
    public async Task Failed_thumbnails_are_kept_as_failed_cells_and_not_retried_every_frame()
    {
        var fetch = new FakeFetch { Behavior = (_, _) => Task.FromResult(ThumbnailResult.Failure(ThumbnailStatus.SourceUnavailable, "元のファイルが無い")) };
        using var loader = new ThumbnailLoader(fetch.Invoke, new TestLog(), 64, 36, capacity: 100, workers: 1);

        loader.SetWanted([Hash('a')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 1));
        loader.Drain();
        loader.SetWanted([Hash('a')]);
        await Task.Delay(100, Ct);

        Assert.True(loader.TryGet(Hash('a'), out LoadedThumb? t));
        Assert.Equal(ThumbnailStatus.SourceUnavailable, t!.Status);
        Assert.Null(t.Bitmap);
        Assert.Single(fetch.Calls);
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_a_failed_cell_is_logged_and_the_worker_keeps_going()
    {
        var log = new TestLog();
        var fetch = new FakeFetch
        {
            Behavior = (hash, _) => hash == Hash('a') ? throw new InvalidOperationException("想定外") : Task.FromResult(OkResult(8, 4)),
        };
        using var loader = new ThumbnailLoader(fetch.Invoke, log, 64, 36, capacity: 100, workers: 1);

        loader.SetWanted([Hash('a'), Hash('b')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 2));
        loader.Drain();

        Assert.True(loader.TryGet(Hash('a'), out LoadedThumb? a));
        Assert.Equal(ThumbnailStatus.SourceUndecodable, a!.Status);
        Assert.Contains("想定外", a.Message, StringComparison.Ordinal);
        Assert.True(loader.TryGet(Hash('b'), out LoadedThumb? b));
        Assert.Equal(ThumbnailStatus.Ok, b!.Status); // 1 枚の失敗で、一覧の残りは止まらない
        Assert.Contains(log.Messages, m => m.StartsWith("ERROR", StringComparison.Ordinal) && m.Contains(Hash('a'), StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_box_size_is_passed_to_the_maker_unchanged()
    {
        (int W, int H)? seen = null;
        ThumbnailFetch fetch = (hash, w, h, ct) =>
        {
            seen = (w, h);
            return Task.FromResult(OkResult(4, 4));
        };
        using var loader = new ThumbnailLoader(fetch, new TestLog(), 320, 180, capacity: 10, workers: 1);

        loader.SetWanted([Hash('a')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 1));

        Assert.Equal((320, 180), seen);
        Assert.Equal((320, 180), (loader.BoxWidthPx, loader.BoxHeightPx));
    }

    [Fact]
    public async Task Dispose_cancels_running_work_and_stops_taking_new_requests()
    {
        var started = new TaskCompletionSource();
        CancellationToken seen = default;
        var fetch = new FakeFetch
        {
            Behavior = async (_, ct) =>
            {
                seen = ct;
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // キャンセルされるまで終わらない
                return OkResult(1, 1);
            },
        };
        var loader = new ThumbnailLoader(fetch.Invoke, new TestLog(), 64, 36, capacity: 10, workers: 1);
        loader.SetWanted([Hash('a'), Hash('b')]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        loader.Dispose();
        loader.Dispose(); // 2 度目は何もしない

        Assert.True(await Wait.UntilAsync(() => seen.IsCancellationRequested));
        await Task.Delay(100, Ct);
        Assert.Single(fetch.Calls); // 2 枚目には着手しない
        Assert.Equal(0, loader.CompletedTotal);
    }

    [Fact]
    public async Task Result_ready_is_signalled_once_until_the_ui_drains_and_not_again_for_each_result()
    {
        var fetch = new FakeFetch();
        int signals = 0;
        using var loader = new ThumbnailLoader(fetch.Invoke, new TestLog(), 64, 36, capacity: 100, workers: 2) { ResultReady = () => Interlocked.Increment(ref signals) };

        loader.SetWanted([Hash('a'), Hash('b'), Hash('c')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 3));
        await Task.Delay(50, Ct);
        Assert.Equal(1, Volatile.Read(ref signals)); // 3 つ届いても、UI への知らせは 1 回(UI が取り込むまで)

        loader.Drain();
        loader.SetWanted([Hash('d')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 4));
        Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref signals) == 2)); // 取り込んだあとに届いたものは、また知らせる
    }

    [Fact]
    public async Task Idle_workers_sleep_and_a_new_request_wakes_them()
    {
        var fetch = new FakeFetch();
        using var loader = new ThumbnailLoader(fetch.Invoke, new TestLog(), 64, 36, capacity: 100, workers: 3);
        await Task.Delay(150, Ct); // 待ちが無いあいだ、ワーカーは眠っている

        loader.SetWanted([Hash('a')]);

        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 1, timeoutMs: 2000));
        await Task.Delay(100, Ct);
        loader.SetWanted([Hash('b'), Hash('c')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 3, timeoutMs: 2000));
    }

    [Fact]
    public async Task Forget_unavailable_lets_original_missing_cells_be_tried_again_but_keeps_undecodable_ones()
    {
        int calls = 0;
        var fetch = new FakeFetch
        {
            Behavior = (hash, _) =>
            {
                if (hash == Hash('a')) return Task.FromResult(Interlocked.Increment(ref calls) == 1 ? ThumbnailResult.Failure(ThumbnailStatus.SourceUnavailable, "無い") : OkResult(8, 4));
                if (hash == Hash('b')) return Task.FromResult(ThumbnailResult.Failure(ThumbnailStatus.SourceUndecodable, "壊れている"));
                return Task.FromResult(OkResult(8, 4));
            },
        };
        using var loader = new ThumbnailLoader(fetch.Invoke, new TestLog(), 64, 36, capacity: 100, workers: 1);
        loader.SetWanted([Hash('a'), Hash('b'), Hash('c')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 3));
        loader.Drain();

        loader.ForgetUnavailable();

        Assert.False(loader.Contains(Hash('a')));
        Assert.True(loader.Contains(Hash('b')));
        Assert.True(loader.Contains(Hash('c')));
        loader.SetWanted([Hash('a'), Hash('b'), Hash('c')]);
        Assert.True(await Wait.UntilAsync(() => loader.CompletedTotal == 4));
        loader.Drain();
        Assert.True(loader.TryGet(Hash('a'), out LoadedThumb? a));
        Assert.Equal(ThumbnailStatus.Ok, a!.Status);
        Assert.Equal(4, fetch.Calls.Count); // b と c は作り直していない
    }

    [Fact]
    public void Constructor_rejects_bad_arguments()
    {
        ThumbnailFetch ok = (_, _, _, _) => Task.FromResult(OkResult(1, 1));
        Assert.Throws<ArgumentNullException>(() => new ThumbnailLoader(null!, new TestLog(), 1, 1, 1, 1));
        Assert.Throws<ArgumentNullException>(() => new ThumbnailLoader(ok, null!, 1, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThumbnailLoader(ok, new TestLog(), 0, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThumbnailLoader(ok, new TestLog(), 1, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThumbnailLoader(ok, new TestLog(), 1, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThumbnailLoader(ok, new TestLog(), 1, 1, 1, 0));
    }
}
