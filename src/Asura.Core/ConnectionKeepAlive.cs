namespace Asura.Core;

public sealed record ConnectionKeepAlive
{
    public ConnectionKeepAlive(bool enabled, TimeSpan interval, int maximumFailures)
    {
        if (enabled && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "An enabled keepalive interval must be positive.");
        }

        if (enabled && maximumFailures < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFailures),
                maximumFailures,
                "An enabled keepalive must allow at least one failure.");
        }

        if (!enabled && interval != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A disabled keepalive cannot retain an active interval.",
                nameof(interval));
        }

        if (!enabled && maximumFailures != 0)
        {
            throw new ArgumentException(
                "A disabled keepalive cannot retain a failure allowance.",
                nameof(maximumFailures));
        }

        Enabled = enabled;
        Interval = interval;
        MaximumFailures = maximumFailures;
    }

    public static ConnectionKeepAlive Disabled { get; } = new(false, TimeSpan.Zero, 0);

    public bool Enabled { get; }

    public TimeSpan Interval { get; }

    public int MaximumFailures { get; }

    public static ConnectionKeepAlive EnabledEvery(TimeSpan interval, int maximumFailures = 3) =>
        new(true, interval, maximumFailures);
}
