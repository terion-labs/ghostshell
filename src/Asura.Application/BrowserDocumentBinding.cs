namespace Asura.Application;

/// <summary>
/// Identifies one committed browser document. Snapshot capture rechecks this
/// binding at the renderer boundary so page data cannot be attributed to a
/// document that changed while the request was in flight.
/// </summary>
public sealed record BrowserDocumentBinding
{
    public BrowserDocumentBinding(
        BrowserAddress address,
        long documentRevision)
    {
        Address = address
            ?? throw new ArgumentNullException(nameof(address));
        ArgumentOutOfRangeException.ThrowIfNegative(documentRevision);
        DocumentRevision = documentRevision;
    }

    public BrowserAddress Address { get; }

    public long DocumentRevision { get; }

    public bool Matches(BrowserSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Address == state.Address
            && DocumentRevision == state.DocumentRevision;
    }

    public static BrowserDocumentBinding FromState(
        BrowserSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BrowserDocumentBinding(
            state.Address,
            state.DocumentRevision);
    }
}
