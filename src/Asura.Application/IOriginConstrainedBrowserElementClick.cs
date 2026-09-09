namespace Asura.Application;

/// <summary>
/// Activates the exact element represented by a document-bound reference while
/// containing any synchronously attributable top-level navigation to one
/// host-selected origin.
/// </summary>
public interface IOriginConstrainedBrowserElementClick
{
    ValueTask<BrowserResult<BrowserClickReceipt>> ClickWithinOriginAsync(
        BrowserElementReference reference,
        BrowserNavigationOrigin allowedOrigin,
        CancellationToken cancellationToken);
}
