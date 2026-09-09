using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Identifies one durable Chromium state archive. Definition revisions are not
/// part of the identity: renaming a profile or rotating its bounded HTTP-auth
/// credential must not sign the user out. Network routes remain isolated
/// because a routed and a local browser must never inherit one another's state.
/// </summary>
public readonly record struct BrowserProfileStateKey
{
    public const int MaximumRouteLength = 512;

    public BrowserProfileStateKey(
        BrowserProfileSelection selection,
        string route)
    {
        var normalizedRoute = NormalizeRoute(route);
        Selection = selection;
        Route = normalizedRoute;
    }

    public static string NormalizeRoute(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var normalizedRoute = route.Trim();
        if (normalizedRoute.Length > MaximumRouteLength
            || normalizedRoute.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A browser profile route must be bounded printable text.",
                nameof(route));
        }

        return normalizedRoute;
    }

    public BrowserProfileSelection Selection { get; }

    public string Route { get; }
}

public sealed record BrowserProfileStoredState
{
    public BrowserProfileStoredState(bool exists, long contentBytes)
    {
        if (contentBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentBytes));
        }

        Exists = exists;
        ContentBytes = contentBytes;
    }

    public bool Exists { get; }

    public long ContentBytes { get; }
}

/// <summary>
/// Encrypted durable storage for complete Chromium request-context trees.
/// Implementations restore only into an empty owner-private directory and
/// publish a replacement archive only after the complete tree was written.
/// </summary>
public interface IBrowserProfileStateStore
{
    /// <summary>
    /// False only after the user deliberately disabled application encryption;
    /// callers then discard runtime state instead of ever writing it plaintext.
    /// </summary>
    bool IsRetentionEnabled { get; }

    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    BrowserProfileStoredState Inspect(BrowserProfileSelection selection);

    IReadOnlyList<BrowserProfileStateKey> ListKeys(
        BrowserProfileSelection selection);

    void Restore(BrowserProfileStateKey key, string destinationDirectory);

    long Seal(BrowserProfileStateKey key, string sourceDirectory);

    long Delete(BrowserProfileSelection selection);
}
