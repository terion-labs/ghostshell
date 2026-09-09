namespace Asura.Application;

/// <summary>
/// Ensures that the exact checkable element represented by a document-bound
/// reference is checked while containing any synchronously attributable
/// top-level navigation to one host-selected origin.
/// </summary>
public interface IOriginConstrainedBrowserElementCheck
{
    ValueTask<BrowserResult<BrowserCheckReceipt>> CheckWithinOriginAsync(
        BrowserElementReference reference,
        BrowserNavigationOrigin allowedOrigin,
        CancellationToken cancellationToken);
}
