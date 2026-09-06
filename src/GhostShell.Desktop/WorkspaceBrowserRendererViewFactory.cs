using GhostShell.App;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

internal sealed class WorkspaceBrowserRendererViewFactory(
    IBrowserRendererViewFactory inner,
    IWorkspaceNetworkConnector networkConnector) : IBrowserRendererViewFactory
{
    public BrowserRendererView Create() => inner.Create();

    public BrowserRendererView CreateIsolatedHtmlPreview() =>
        inner.CreateIsolatedHtmlPreview();

    public ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken) =>
        CreateAsync(
            connection,
            BrowserProfileBinding.Legacy(BrowserProfileKey.Global),
            cancellationToken);

    public ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileKey profile,
        CancellationToken cancellationToken) =>
        CreateAsync(
            connection,
            BrowserProfileBinding.Legacy(profile),
            cancellationToken);

    public ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileBinding profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return inner.CreateAsync(
            connection,
            profile,
            networkConnector,
            cancellationToken);
    }

    public ValueTask<BrowserRendererView> CreateThroughSocksProxyAsync(
        int explicitSocksProxyPort,
        string explicitRouteIdentity,
        BrowserProfileBinding profile,
        CancellationToken cancellationToken) =>
        inner.CreateThroughSocksProxyAsync(
            explicitSocksProxyPort,
            explicitRouteIdentity,
            profile,
            cancellationToken);
}
