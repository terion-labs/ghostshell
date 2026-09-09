namespace Asura.Application;

public sealed record RecentSessionRetentionPolicy
{
    public const int DefaultMaximumEntries = 100;
    public const int MaximumSupportedEntries = 1_000;

    public static TimeSpan DefaultMaximumAge { get; } = TimeSpan.FromDays(30);

    public static TimeSpan MaximumSupportedAge { get; } = TimeSpan.FromDays(365);

    public static RecentSessionRetentionPolicy Default { get; } = new(
        DefaultMaximumEntries,
        DefaultMaximumAge);

    public RecentSessionRetentionPolicy(int maximumEntries, TimeSpan maximumAge)
    {
        if (maximumEntries is < 0 or > MaximumSupportedEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                $"Recent-session retention must be between 0 and {MaximumSupportedEntries} records.");
        }

        if (maximumAge <= TimeSpan.Zero || maximumAge > MaximumSupportedAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAge),
                $"Recent-session retention must be positive and no longer than {MaximumSupportedAge.TotalDays} days.");
        }

        MaximumEntries = maximumEntries;
        MaximumAge = maximumAge;
    }

    public int MaximumEntries { get; }

    public TimeSpan MaximumAge { get; }

    public bool IsEnabled => MaximumEntries > 0;
}
