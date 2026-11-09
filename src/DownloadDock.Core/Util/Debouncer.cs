namespace DownloadDock.Core.Util;

/// <summary>
/// Coalesces bursts of calls into one invocation that runs after a quiet period.
/// A maximum delay guarantees the action still runs during a continuous burst
/// (e.g. a large file being written for minutes).
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly Action _action;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _maxDelay;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private DateTime? _firstPendingUtc;
    private bool _disposed;

    public Debouncer(Action action, TimeSpan quietPeriod, TimeSpan? maxDelay = null)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _quietPeriod = quietPeriod;
        _maxDelay = maxDelay ?? TimeSpan.FromTicks(quietPeriod.Ticks * 8);
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Trigger()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = DateTime.UtcNow;
            _firstPendingUtc ??= now;
            var deadline = _firstPendingUtc.Value + _maxDelay;
            var due = now + _quietPeriod;
            if (due > deadline) due = deadline;
            var delay = due - now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs the pending action now (if any) instead of waiting.</summary>
    public void Flush()
    {
        bool pending;
        lock (_gate) pending = _firstPendingUtc != null && !_disposed;
        if (pending) Fire();
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed || _firstPendingUtc == null) return;
            _firstPendingUtc = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        _action();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
