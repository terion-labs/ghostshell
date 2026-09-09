namespace Asura.Application;

/// <summary>
/// An opaque, short-lived reference to an element in one exact browser
/// document. The renderer owns its meaning and expiry; callers cannot derive a
/// selector or native handle from this value.
/// </summary>
public sealed record BrowserElementReference
{
    public const int MaximumValueBytes =
        BrowserElementReferenceId.MaximumValueBytes;

    public BrowserElementReference(
        string value,
        BrowserDocumentBinding document)
        : this(new BrowserElementReferenceId(value), document)
    {
    }

    public BrowserElementReference(
        BrowserElementReferenceId id,
        BrowserDocumentBinding document)
    {
        if (string.IsNullOrEmpty(id.Value))
        {
            throw new ArgumentException(
                "A browser element reference requires a valid ID.",
                nameof(id));
        }

        Id = id;
        Document = document
            ?? throw new ArgumentNullException(nameof(document));
    }

    public BrowserElementReferenceId Id { get; }

    public string Value => Id.Value;

    public BrowserDocumentBinding Document { get; }
}
