namespace Asura.Application;

public sealed record ConnectionReconnectPolicy
{
    public ConnectionReconnectPolicy(
        int maximumAttempts,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        double multiplier)
    {
        if (maximumAttempts is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (initialDelay < TimeSpan.Zero || maximumDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        if (maximumDelay > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        }

        if (multiplier is < 1 or > 10 || double.IsNaN(multiplier))
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        }

        MaximumAttempts = maximumAttempts;
        InitialDelay = initialDelay;
        MaximumDelay = maximumDelay;
        Multiplier = multiplier;
    }

    public static ConnectionReconnectPolicy InteractiveDefault { get; } = new(
        maximumAttempts: 4,
        initialDelay: TimeSpan.FromSeconds(1),
        maximumDelay: TimeSpan.FromSeconds(8),
        multiplier: 2);

    public int MaximumAttempts { get; }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaximumDelay { get; }

    public double Multiplier { get; }

    public TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt is < 1 || attempt > MaximumAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        var milliseconds = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaximumDelay.TotalMilliseconds));
    }
}
