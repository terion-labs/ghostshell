namespace Asura.Application;

/// <summary>
/// Trusted browser-document identity projected by SessionHost. This is
/// comparison evidence for agent context and never reusable authority.
/// </summary>
public sealed record BrowserSessionMetadata
{
    public BrowserSessionMetadata(
        BrowserNavigationOrigin origin,
        long documentRevision,
        BrowserViewportState? viewport = null,
        long viewportRevision = 0,
        long inputEpoch = 0,
        BrowserAddress? address = null)
    {
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        ArgumentOutOfRangeException.ThrowIfNegative(documentRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(viewportRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(inputEpoch);
        DocumentRevision = documentRevision;
        Viewport = viewport ?? BrowserViewportState.Empty;
        ViewportRevision = viewportRevision;
        InputEpoch = inputEpoch;
        Address = address;
    }

    public BrowserNavigationOrigin Origin { get; }

    public long DocumentRevision { get; }

    public BrowserViewportState Viewport { get; }

    public long ViewportRevision { get; }

    public long InputEpoch { get; }

    public BrowserAddress? Address { get; }

    public static BrowserSessionMetadata FromState(BrowserSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BrowserSessionMetadata(
            BrowserNavigationOrigin.FromAddress(state.Address),
            state.DocumentRevision,
            state.Viewport,
            state.ViewportRevision,
            state.InputEpoch,
            state.Address);
    }
}
