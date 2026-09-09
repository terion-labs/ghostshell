namespace Asura.Application;

/// <summary>
/// Identifies the committed browser document from which a governed navigation
/// was authorized. The renderer rechecks this binding immediately before
/// dispatch so a policy decision cannot be reused after the document changes.
/// </summary>
public sealed record BrowserNavigationStartBinding
{
    public BrowserNavigationStartBinding(
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

    public static BrowserNavigationStartBinding FromState(
        BrowserSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BrowserNavigationStartBinding(
            state.Address,
            state.DocumentRevision);
    }
}
