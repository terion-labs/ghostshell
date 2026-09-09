namespace Asura.Terminal.Tests;

internal sealed class ManualTimerTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TaskCompletionSource _timerChanged = NewTimerSignal();
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            ChangeUnsafe(timer, dueTime, period);
            SignalTimerChangedUnsafe();
        }

        return timer;
    }

    public async Task WaitForActiveTimerAsync(
        TimeSpan dueTime,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ManualTimer? candidate;
            Task timerChanged;
            lock (_gate)
            {
                candidate = _timers.FirstOrDefault(
                    timer => timer.HasRemaining(_timestamp, dueTime));
                timerChanged = _timerChanged.Task;
            }

            if (candidate is not null)
            {
                // Immediate reads create and synchronously dispose deadline
                // timers. Only the polling delay remains active after yielding.
                await Task.Yield();
                lock (_gate)
                {
                    if (candidate.HasRemaining(_timestamp, dueTime))
                    {
                        return;
                    }
                }

                continue;
            }

            await timerChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_gate)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
            foreach (var timer in _timers.Where(
                         timer => timer.IsDue(_timestamp)).ToArray())
            {
                callbacks.Add((timer.Callback, timer.State));
                timer.AdvanceAfterFire(_timestamp);
            }

            SignalTimerChangedUnsafe();
        }

        foreach (var (callback, state) in callbacks)
        {
            callback(state);
        }
    }

    private bool Change(
        ManualTimer timer,
        TimeSpan dueTime,
        TimeSpan period)
    {
        lock (_gate)
        {
            if (!_timers.Contains(timer) || timer.IsDisposed)
            {
                return false;
            }

            ChangeUnsafe(timer, dueTime, period);
            SignalTimerChangedUnsafe();
            return true;
        }
    }

    private void ChangeUnsafe(
        ManualTimer timer,
        TimeSpan dueTime,
        TimeSpan period) =>
        timer.Change(
            dueTime == Timeout.InfiniteTimeSpan
                ? null
                : _timestamp + dueTime.Ticks,
            period);

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            timer.MarkDisposed();
            _timers.Remove(timer);
            SignalTimerChangedUnsafe();
        }
    }

    private void SignalTimerChangedUnsafe()
    {
        var changed = _timerChanged;
        _timerChanged = NewTimerSignal();
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewTimerSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualTimer(
        ManualTimerTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private long? _dueTimestamp;
        private TimeSpan _period;

        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            owner.Change(this, dueTime, period);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public bool HasRemaining(long timestamp, TimeSpan remaining) =>
            !IsDisposed
            && _dueTimestamp is { } dueTimestamp
            && dueTimestamp - timestamp == remaining.Ticks;

        public bool IsDue(long timestamp) =>
            !IsDisposed && _dueTimestamp <= timestamp;

        public void AdvanceAfterFire(long timestamp)
        {
            _dueTimestamp = _period == Timeout.InfiniteTimeSpan
                ? null
                : timestamp + _period.Ticks;
        }

        public void Change(long? dueTimestamp, TimeSpan period)
        {
            _dueTimestamp = dueTimestamp;
            _period = period;
        }

        public void MarkDisposed()
        {
            IsDisposed = true;
            _dueTimestamp = null;
        }
    }
}
