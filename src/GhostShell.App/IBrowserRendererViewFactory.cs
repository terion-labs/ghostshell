using Avalonia.Controls;
using GhostShell.App.Controls;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

/// <summary>
/// Presentation-owned bridge between the Avalonia panel and the browser
/// renderer selected by the desktop composition root.
/// </summary>
public interface IBrowserRendererViewFactory
{
    /// <summary>Creates a direct local browser for lightweight embedded previews.</summary>
    BrowserRendererView Create();

    /// <summary>
    /// Creates an ephemeral browser context for explicitly enabled live HTML
    /// previews. Its cookies, cache, and other origin state must not be shared
    /// with ordinary browser panels.
    /// </summary>
    BrowserRendererView CreateIsolatedHtmlPreview();

    ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken);

    ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileKey profile,
        CancellationToken cancellationToken) =>
        CreateAsync(connection, cancellationToken);

    ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileBinding profile,
        CancellationToken cancellationToken) =>
        CreateAsync(connection, profile.Selection.Partition, cancellationToken);

    /// <summary>
    /// Creates a browser whose local socket, including the SSH transport for a
    /// routed browser, is opened through one workspace's live network route.
    /// </summary>
    ValueTask<BrowserRendererView> CreateAsync(
        ConnectionProfile connection,
        BrowserProfileBinding profile,
        IWorkspaceNetworkConnector networkConnector,
        CancellationToken cancellationToken) =>
        connection.Endpoint is ConnectionEndpoint.Local
            ? CreateThroughSocksProxyAsync(
                networkConnector.LocalProxyEndpoint.Port,
                networkConnector.LocalProxyEndpoint.AbsoluteUri,
                profile,
                cancellationToken)
            : CreateAsync(connection, profile, cancellationToken);

    ValueTask<BrowserRendererView> CreateThroughSocksProxyAsync(
        int socksProxyPort,
        string routeIdentity,
        BrowserProfileBinding profile,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<BrowserRendererView>(new NotSupportedException(
            "This browser renderer cannot use an explicit SOCKS proxy."));
}

/// <summary>
/// A panel's browser visual, the renderer that drives it, and the session
/// attachment that feeds it.
///
/// The attachment lives here rather than in whichever control happens to be
/// drawing the panel, because it belongs to the panel and the panel outlives its
/// views. Rearranging panels rebuilds those views; if the attachment went with
/// them, every layout change would tear a live session off its renderer and put
/// a new one back — which is a session lifetime decided by where a panel sits on
/// screen, and there are runs with no screen at all.
/// </summary>
public sealed class BrowserRendererView(
    Control view,
    IBrowserRenderer renderer,
    IDisposable? lifetime = null,
    Action<bool>? agentActivityChanged = null,
    Func<bool>? developerToolsRequested = null) : IDisposable
{
    private readonly SemaphoreSlim _attachmentGate = new(1, 1);
    private readonly IDisposable? _lifetime = lifetime;
    private readonly Action<bool>? _agentActivityChanged = agentActivityChanged;
    private readonly Func<bool>? _developerToolsRequested = developerToolsRequested;
    private bool _isAgentActive;
    private bool _disposed;

    public Control View { get; } =
        view ?? throw new ArgumentNullException(nameof(view));

    public IBrowserRenderer Renderer { get; } =
        renderer ?? throw new ArgumentNullException(nameof(renderer));

    /// <summary>
    /// The session this renderer is attached to, once it is. A view that comes
    /// along later adopts this instead of attaching again.
    /// </summary>
    internal BrowserRendererAttachment? Attachment { get; set; }

    /// <summary>
    /// The presentation host currently drawing <see cref="View"/>. The panel
    /// owns the visual, so a replacement host adopts it without changing the
    /// renderer or session lifetime.
    /// </summary>
    internal BrowserPresentationHost? PresentationHost { get; set; }

    internal void SetAgentActivity(bool isActive)
    {
        if (_disposed || _isAgentActive == isActive)
        {
            return;
        }

        _isAgentActive = isActive;
        _agentActivityChanged?.Invoke(isActive);
    }

    internal bool OpenDeveloperTools() =>
        !_disposed && _developerToolsRequested?.Invoke() is true;

    /// <summary>
    /// Ensures the panel-owned renderer is linked to its hosted session. This
    /// is deliberately independent of presentation: inactive tabs and
    /// headless workspace runs still need a real renderer for browser tools.
    /// </summary>
    internal async ValueTask<BrowserRendererAttachment> EnsureAttachmentAsync(
        ISessionHostClient client,
        ClientId clientId,
        EnsureBrowserSessionRequest request,
        ViewportDescriptor viewport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(viewport);
        await _attachmentGate.WaitAsync(cancellationToken);
        AttachmentId? pendingAttachmentId = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Attachment is { } existing)
            {
                return existing.Matches(client, clientId, request.SessionId)
                    ? existing
                    : throw new InvalidOperationException(
                        "The browser renderer is already attached to a different panel session.");
            }

            var context = OperationContext.ForHuman(clientId);
            _ = RequireSuccess(await client.EnsureBrowserSessionAsync(
                request,
                context,
                cancellationToken));
            var attachment = await AttachInteractiveAsync(
                client,
                clientId,
                request.SessionId,
                viewport,
                cancellationToken);
            pendingAttachmentId = attachment.Attachment.Id;
            _ = RequireSuccess(await client.AttachBrowserRendererAsync(
                new AttachBrowserRendererRequest(
                    request.SessionId,
                    attachment.Attachment.Id,
                    Renderer),
                context,
                cancellationToken));
            ObjectDisposedException.ThrowIf(_disposed, this);

            var created = new BrowserRendererAttachment(
                client,
                clientId,
                request.SessionId,
                attachment.Attachment.Id);
            Attachment = created;
            pendingAttachmentId = null;
            return created;
        }
        finally
        {
            if (pendingAttachmentId is { } staleAttachmentId)
            {
                await DetachFailedAttachmentAsync(
                    client,
                    clientId,
                    request.SessionId,
                    staleAttachmentId);
            }

            _attachmentGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_isAgentActive)
        {
            _isAgentActive = false;
            _agentActivityChanged?.Invoke(false);
        }

        var attachment = Attachment;
        Attachment = null;
        attachment?.Release();
        var presentationHost = PresentationHost;
        PresentationHost = null;
        presentationHost?.ReleaseRendererVisual(this);
        _lifetime?.Dispose();
    }

    private async ValueTask<AttachmentResult> AttachInteractiveAsync(
        ISessionHostClient client,
        ClientId clientId,
        SessionId sessionId,
        ViewportDescriptor viewport,
        CancellationToken cancellationToken)
    {
        var capabilities = new CapabilitySet(
        [
            SessionCapabilities.AttachInteractive,
            .. Renderer.Capabilities.Values,
        ]);
        HostResult<AttachmentResult>? lastResult = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            lastResult = await client.AttachAsync(
                new AttachSessionRequest(
                    sessionId,
                    clientId,
                    AttachmentKind.Interactive,
                    viewport,
                    capabilities),
                OperationContext.ForHuman(clientId),
                cancellationToken);
            if (lastResult is HostResult<AttachmentResult>.Success success)
            {
                return success.Value;
            }

            if (lastResult is not HostResult<AttachmentResult>.Failure
                {
                    Error.Code: HostErrorCode.CapabilityNotSupported,
                })
            {
                return RequireSuccess(lastResult);
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(20 * (attempt + 1)),
                cancellationToken);
        }

        return RequireSuccess(lastResult
            ?? throw new InvalidOperationException(
                "The browser attachment did not start."));
    }

    private static T RequireSuccess<T>(HostResult<T> result) => result switch
    {
        HostResult<T>.Success success => success.Value,
        HostResult<T>.Failure failure => throw new InvalidOperationException(
            $"{failure.Error.StableCode}: {failure.Error.Message}"),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static async ValueTask DetachFailedAttachmentAsync(
        ISessionHostClient client,
        ClientId clientId,
        SessionId sessionId,
        AttachmentId attachmentId)
    {
        try
        {
            _ = await client.DetachAsync(
                new DetachSessionRequest(attachmentId, sessionId),
                OperationContext.ForHuman(clientId),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace(
                "browser.attachment.detach-failed",
                exception);
        }
    }
}
