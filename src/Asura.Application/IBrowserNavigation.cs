namespace Asura.Application;

/// <summary>
/// Typed human browser-chrome operations over one logical session.
/// </summary>
public interface IBrowserNavigation
{
    BrowserSessionState State { get; }

    ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
        BrowserAddress address,
        CancellationToken cancellationToken);

    ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
        CancellationToken cancellationToken);

    ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
        CancellationToken cancellationToken);

    ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
        CancellationToken cancellationToken);

    ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
        CancellationToken cancellationToken);
}
