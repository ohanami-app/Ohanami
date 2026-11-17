using System;
using System.Threading;

namespace Ohanami.Core.Indexing;

/// <summary>
/// 状態の通知の間引き(1 秒に 10 回まで=最短 100ms 間隔)。先頭は即時、間隔内の変化は 1 回にまとめて、間隔が空いたあとに末尾の 1 回を必ず送る
/// (最後の状態が間引きで失われない)。時刻とタイマーは <see cref="TimeProvider"/> から(テストでは手で進められる)。
/// <see cref="PublishNow"/> は、実行の状態の切り替え(Running → Paused など)のように、遅らせたくない通知用(間隔の起点も更新する)。
/// </summary>
internal sealed class StateThrottle : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _minInterval;
    private readonly Action _publish;
    private readonly object _gate = new();
    private ITimer? _timer;
    private bool _hasLast;
    private long _lastTimestamp;
    private bool _disposed;

    public StateThrottle(TimeProvider time, TimeSpan minInterval, Action publish)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(publish);
        if (minInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minInterval));
        _time = time;
        _minInterval = minInterval;
        _publish = publish;
    }

    /// <summary>状態が変わった。間引いて通知する。</summary>
    public void Request()
    {
        bool publishNow = false;
        lock (_gate)
        {
            if (_disposed || _timer is not null) return; // 末尾の通知が予約済み(それが最新の状態を送る)
            long now = _time.GetTimestamp();
            TimeSpan sinceLast = _hasLast ? _time.GetElapsedTime(_lastTimestamp, now) : TimeSpan.MaxValue;
            if (sinceLast >= _minInterval)
            {
                _hasLast = true;
                _lastTimestamp = now;
                publishNow = true;
            }
            else
            {
                _timer = _time.CreateTimer(OnTimer, null, _minInterval - sinceLast, Timeout.InfiniteTimeSpan);
            }
        }
        if (publishNow) _publish();
    }

    /// <summary>間引かずに今すぐ通知する(予約済みの末尾の通知は取り消す。これが最新を送るため)。</summary>
    public void PublishNow()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer?.Dispose();
            _timer = null;
            _hasLast = true;
            _lastTimestamp = _time.GetTimestamp();
        }
        _publish();
    }

    private void OnTimer(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _timer is null) return;
            _timer.Dispose();
            _timer = null;
            _hasLast = true;
            _lastTimestamp = _time.GetTimestamp();
        }
        _publish();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
