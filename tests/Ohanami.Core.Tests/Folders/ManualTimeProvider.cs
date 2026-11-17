using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ohanami.Core.Tests.Folders;

/// <summary>
/// 手で進める TimeProvider。時刻だけでなく、<see cref="CreateTimer"/> の待ちも <see cref="Advance"/> で期限の順に起こす(まとめる時間・定期の走査・再試行を、実際に待たずに確かめる)。
/// <see cref="GetTimestamp"/> も時刻から決める(走査の所要時間を、テストが <see cref="Advance"/> で作れる)。
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override long GetTimestamp()
    {
        lock (_gate) return _now.UtcTicks;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>いま起きる予定の待ちの数(破棄していないもので、期限があるもの)。</summary>
    public int ArmedTimerCount
    {
        get
        {
            lock (_gate) return _timers.Count(t => t.Due is not null);
        }
    }

    /// <summary>時刻を進める。進める途中に期限が来た待ちは、期限の時刻に合わせて、期限の順に(このスレッドで)コールバックを呼ぶ。</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                if (next.Due!.Value > _now) _now = next.Due.Value;
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }
            return true;
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                if (Due is null) return;
                Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due.Value + _period;
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
