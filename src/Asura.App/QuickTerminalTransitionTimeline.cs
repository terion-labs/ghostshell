namespace Asura.App;

/// <summary>
/// Tracks Quick Terminal reveal progress and invalidates obsolete completion
/// callbacks. Timer scheduling and window animation remain controller-owned.
/// </summary>
internal sealed class QuickTerminalTransitionTimeline
{
    private long _startedAt;
    private double _from;
    private double _to;

    public QuickTerminalVisibilityState State { get; private set; } =
        QuickTerminalVisibilityState.Hidden;

    public double Progress { get; private set; }

    public int DurationMilliseconds { get; private set; }

    public long Generation { get; private set; }

    public long Begin(
        double from,
        double to,
        int durationMilliseconds,
        long startedAt)
    {
        _from = Math.Clamp(from, 0, 1);
        _to = Math.Clamp(to, 0, 1);
        Progress = _from;
        State = _to >= 1
            ? QuickTerminalVisibilityState.Showing
            : QuickTerminalVisibilityState.Hiding;
        var distance = Math.Abs(_to - _from);
        DurationMilliseconds = durationMilliseconds > 0
            && distance > double.Epsilon
                ? Math.Max(
                    1,
                    checked((int)Math.Round(durationMilliseconds * distance)))
                : 0;
        _startedAt = startedAt;
        return ++Generation;
    }

    public double Pause(long now)
    {
        Progress = CurrentProgress(now);
        Invalidate();
        return Progress;
    }

    public void Cancel() => Invalidate();

    public void Reset()
    {
        Invalidate();
        State = QuickTerminalVisibilityState.Hidden;
        Progress = 0;
    }

    public bool TryComplete(long generation)
    {
        if (generation != Generation)
        {
            return false;
        }

        Progress = _to;
        State = _to >= 1
            ? QuickTerminalVisibilityState.Visible
            : QuickTerminalVisibilityState.Hidden;
        Invalidate();
        return true;
    }

    private double CurrentProgress(long now)
    {
        if (DurationMilliseconds <= 0)
        {
            return Progress;
        }

        var elapsed = now - _startedAt;
        var timelineProgress = Math.Clamp(
            elapsed / (double)DurationMilliseconds,
            0,
            1);
        var offset = timelineProgress - 1;
        var eased = (offset * offset * offset) + 1;
        return _from + ((_to - _from) * eased);
    }

    private void Invalidate()
    {
        DurationMilliseconds = 0;
        Generation++;
    }
}

internal enum QuickTerminalVisibilityState
{
    Hidden,
    Showing,
    Visible,
    Hiding,
}
