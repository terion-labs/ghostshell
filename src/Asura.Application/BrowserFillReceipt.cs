namespace Asura.Application;

/// <summary>
/// Confirms that a value replacement was dispatched from the exact source
/// document. The document may already have navigated when this is returned.
/// </summary>
public sealed record BrowserFillReceipt
{
    public BrowserFillReceipt(BrowserDocumentBinding sourceDocument)
    {
        SourceDocument = sourceDocument
            ?? throw new ArgumentNullException(nameof(sourceDocument));
    }

    public BrowserDocumentBinding SourceDocument { get; }
}
