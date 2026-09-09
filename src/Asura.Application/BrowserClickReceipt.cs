namespace Asura.Application;

/// <summary>
/// Confirms that activation was dispatched from the exact source document.
/// The document may already have navigated by the time the receipt is returned.
/// </summary>
public sealed record BrowserClickReceipt
{
    public BrowserClickReceipt(BrowserDocumentBinding sourceDocument)
    {
        SourceDocument = sourceDocument
            ?? throw new ArgumentNullException(nameof(sourceDocument));
    }

    public BrowserDocumentBinding SourceDocument { get; }
}
