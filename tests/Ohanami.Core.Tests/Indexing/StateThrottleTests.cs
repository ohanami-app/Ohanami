using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Indexing;
using Xunit;

namespace Ohanami.Core.Tests.Indexing;

/// <summary>手で進められる TimeProvider(時刻とタイマー)。間引きの検査用。</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _nowMs;

    public override long TimestampFrequency => 1000; // 1 刻み = 1ms

    public override long GetTimestamp() { lock (_gate) return _nowMs; }

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, GetTimestamp() + (long)dueTime.TotalMilliseconds);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    public int PendingTimers { get { lock (_gate) return _timers.Count; } }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _nowMs += (long)by.TotalMilliseconds;
            due = _timers.FindAll(t => t.DueMs <= _nowMs);
            foreach (ManualTimer t in due) _timers.Remove(t);
        }
        foreach (ManualTimer t in due) t.Fire();
    }

    private void Remove(ManualTimer timer) { lock (_gate) _timers.Remove(timer); }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, long dueMs) : ITimer
    {
        public long DueMs { get; } = dueMs;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>状態の通知の間引き(1 秒に 10 回まで)。</summary>
public sealed class StateThrottleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void First_request_publishes_at_once_and_a_burst_is_coalesced_into_one_trailing_publish()
    {
        var time = new ManualTimeProvider();
        int published = 0;
        var throttle = new StateThrottle(time, Interval, () => published++);

        throttle.Request();
        Assert.Equal(1, published); // 先頭は即時

        for (int i = 0; i < 50; i++) { throttle.Request(); time.Advance(TimeSpan.FromMilliseconds(1)); }
        Assert.Equal(1, published); // 50ms の間は送らない
        Assert.Equal(1, time.PendingTimers); // 末尾の通知の予約は 1 つだけ

        time.Advance(TimeSpan.FromMilliseconds(60)); // 100ms を超えた
        Assert.Equal(2, published); // 末尾の 1 回が送られる(最後の状態が失われない)
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public void Publishing_is_limited_to_ten_per_second_even_with_constant_updates()
    {
        var time = new ManualTimeProvider();
        int published = 0;
        var throttle = new StateThrottle(time, Interval, () => published++);

        for (int ms = 0; ms < 1000; ms++) { throttle.Request(); time.Advance(TimeSpan.FromMilliseconds(1)); }

        Assert.InRange(published, 9, 11); // 1 秒に約 10 回
    }

    [Fact]
    public void Requests_spaced_at_least_the_interval_apart_each_publish_at_once()
    {
        var time = new ManualTimeProvider();
        int published = 0;
        var throttle = new StateThrottle(time, Interval, () => published++);

        for (int i = 0; i < 5; i++)
        {
            throttle.Request();
            time.Advance(Interval);
        }
        Assert.Equal(5, published);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public void Publish_now_is_immediate_cancels_the_pending_trailing_publish_and_restarts_the_interval()
    {
        var time = new ManualTimeProvider();
        int published = 0;
        var throttle = new StateThrottle(time, Interval, () => published++);

        throttle.Request();                           // 1(先頭)
        time.Advance(TimeSpan.FromMilliseconds(10));
        throttle.Request();                           // 末尾を予約
        Assert.Equal(1, time.PendingTimers);

        throttle.PublishNow();                        // 2(即時。予約は取り消す)
        Assert.Equal(2, published);
        Assert.Equal(0, time.PendingTimers);

        time.Advance(TimeSpan.FromMilliseconds(500)); // 取り消したので、余分には送られない
        Assert.Equal(2, published);

        throttle.Request();                           // 間隔が空いているので即時
        Assert.Equal(3, published);
    }

    [Fact]
    public void Request_right_after_publish_now_waits_for_the_interval()
    {
        var time = new ManualTimeProvider();
        int published = 0;
        var throttle = new StateThrottle(time, Interval, () => published++);

        throttle.PublishNow();
        time.Advance(TimeSpan.FromMilliseconds(20));
        throttle.Request();
        Assert.Equal(1, published);
        time.Advance(TimeSpan.FromMilliseconds(80));
        Assert.Equal(2, published); // PublishNow から 100ms 後
    }

    [Fact]
    public void Disposed_throttle_publishes_nothing()
    {
        var time = new ManualTimeProvider();
        int published = 0;
        var throttle = new StateThrottle(time, Interval, () => published++);
        throttle.Request();
        throttle.Request();
        throttle.Dispose();
        time.Advance(TimeSpan.FromSeconds(1));
        throttle.Request();
        throttle.PublishNow();
        Assert.Equal(1, published);
    }

    [Fact]
    public void Non_positive_interval_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StateThrottle(TimeProvider.System, TimeSpan.Zero, () => { }));
    }
}
