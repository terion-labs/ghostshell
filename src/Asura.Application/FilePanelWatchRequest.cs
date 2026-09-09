namespace Asura.Application;

/// <summary>
/// A request to observe the file set below one location. The common implementation polls through
/// the provider boundary, so protocols without push notifications retain the same semantics.
/// </summary>
public sealed record FilePanelWatchRequest
{
    public FilePanelWatchRequest(
        FilePanelLocation location,
        FilePanelDiscoveryScope scope,
        bool showHidden,
        TimeSpan interval)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, null);
        }

        if (interval < TimeSpan.FromMilliseconds(50) || interval > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                interval,
                "A file watch interval must be between 50 milliseconds and 5 minutes.");
        }

        Scope = scope;
        ShowHidden = showHidden;
        Interval = interval;
    }

    public FilePanelLocation Location { get; }

    public FilePanelDiscoveryScope Scope { get; }

    public bool ShowHidden { get; }

    public TimeSpan Interval { get; }
}
