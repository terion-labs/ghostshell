namespace Asura.Application;

/// <summary>
/// Confirms that the exact element was checked in the source document. The
/// document may already have navigated by the time the receipt is returned.
/// </summary>
public sealed record BrowserCheckReceipt
{
    public BrowserCheckReceipt(BrowserDocumentBinding sourceDocument)
    {
        SourceDocument = sourceDocument
            ?? throw new ArgumentNullException(nameof(sourceDocument));
    }

    public BrowserDocumentBinding SourceDocument { get; }
}
