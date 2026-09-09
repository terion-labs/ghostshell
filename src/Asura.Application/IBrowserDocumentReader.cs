namespace Asura.Application;

/// <summary>
/// Captures bounded, engine-neutral page semantics for one exact committed
/// browser document. Raw script and vendor objects never cross this port.
/// </summary>
public interface IBrowserDocumentReader
{
    ValueTask<BrowserResult<BrowserDocumentSnapshot>> CaptureSnapshotAsync(
        BrowserDocumentBinding document,
        CancellationToken cancellationToken,
        BrowserSnapshotQuery? query = null);
}
