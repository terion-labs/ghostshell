namespace Asura.Application;

/// <summary>
/// Executes one top-level navigation while rejecting any initial request or
/// redirect outside the host-selected origin.
/// </summary>
public interface IOriginConstrainedBrowserNavigation
{
    ValueTask<BrowserResult<BrowserSessionState>> NavigateWithinOriginAsync(
        BrowserOriginConstrainedNavigationRequest request,
        BrowserNavigationOrigin allowedOrigin,
        BrowserNavigationStartBinding startBinding,
        CancellationToken cancellationToken);
}
