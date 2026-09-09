using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Requests that one checkable element from an exact browser document revision
/// be placed in its checked state.
/// </summary>
public sealed record BrowserElementCheckRequest
{
    public BrowserElementCheckRequest(
        SessionId sessionId,
        BrowserElementReferenceId reference,
        long documentRevision)
    {
        if (string.IsNullOrEmpty(sessionId.Value))
        {
            throw new ArgumentException(
                "A browser element check requires a valid session ID.",
                nameof(sessionId));
        }

        if (string.IsNullOrEmpty(reference.Value))
        {
            throw new ArgumentException(
                "A browser element check requires a valid reference ID.",
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
