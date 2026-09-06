using GhostShell.App;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

internal sealed class IsolatedBrowserRendererViewFactory :
    IBrowserRendererViewFactory
{
    private readonly IBrowserRendererViewFactory _inner;
    private readonly WorkspaceIsolationSocksProxy _workspaceProxy;

    public IsolatedBrowserRendererViewFactory(
        IBrowserRendererViewFactory inner,
        WorkspaceIsolationSocksProxy workspaceProxy)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _workspaceProxy = workspaceProxy ?? throw new ArgumentNullException(nameof(workspaceProxy));
    }

    public BrowserRendererView Create() => _inner.Create();

    public BrowserRendererView CreateIsolatedHtmlPreview() =>
        _inner.CreateIsolatedHtmlPreview();

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
        return _inner.CreateAsync(
            connection,
            profile,
            _workspaceProxy,
            cancellationToken);
    }
}
