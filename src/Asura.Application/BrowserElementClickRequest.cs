using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Requests activation of one opaque reference from an exact document
/// revision in a logical browser session.
/// </summary>
public sealed record BrowserElementClickRequest
{
    public BrowserElementClickRequest(
        SessionId sessionId,
        BrowserElementReferenceId reference,
        long documentRevision)
    {
        if (string.IsNullOrEmpty(reference.Value))
        {
            throw new ArgumentException(
                "A browser element click requires a valid reference ID.",
                nameof(reference));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(documentRevision);
        SessionId = sessionId;
        Reference = reference;
        DocumentRevision = documentRevision;
    }

    public SessionId SessionId { get; }

    public BrowserElementReferenceId Reference { get; }

    public long DocumentRevision { get; }
}
