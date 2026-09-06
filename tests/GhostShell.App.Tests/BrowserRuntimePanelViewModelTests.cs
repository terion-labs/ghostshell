using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using GhostShell.App;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class BrowserRuntimePanelViewModelTests
{
    [Fact]
    public void Blank_browser_address_starts_as_empty_editor_text()
    {
        var browser = new BrowserPresentationHost();

        Assert.Empty(browser.AddressText);
    }

    [Fact]
    public void RendererRecoveryWarnsAboutLostVolatileStateAndOffersOnlyReload()
    {
        var browser = new BrowserPresentationHost();
        var lostAddress = Address("https://example.test/unsaved-form");

        browser.ApplyProductEvent(
            new BrowserProductEvent.RendererRecovered(lostAddress));

        Assert.True(browser.IsProductNoticeVisible);
        Assert.Equal("Page process restarted", browser.ProductHeading);
        Assert.Contains("volatile page state were lost", browser.ProductMessage);
        Assert.DoesNotContain("restored", browser.ProductMessage);
        Assert.True(browser.HasProductAction);
        Assert.Equal("Reload page", browser.ProductActionLabel);
    }

    [Fact]
    public void DownloadAndPermissionEventsHaveDistinctVisibleStates()
    {
        var browser = new BrowserPresentationHost();

        browser.ApplyProductEvent(
            new BrowserProductEvent.PermissionDenied(
                "https://example.test",
                BrowserPermissionKind.Camera | BrowserPermissionKind.Microphone));

        Assert.Equal("Permission denied", browser.ProductHeading);
        Assert.Contains("camera", browser.ProductMessage);
        Assert.False(browser.HasDownloadProgress);

        browser.ApplyProductEvent(
            new BrowserProductEvent.DownloadProgressed(
                7,
                "report.pdf",
                ReceivedBytes: 524_288,
                TotalBytes: 1_048_576,
                PercentComplete: 50));

        Assert.Equal("Downloading report.pdf", browser.ProductHeading);
        Assert.True(browser.HasDownloadProgress);
        Assert.False(browser.IsDownloadProgressIndeterminate);
        Assert.Equal(50, browser.DownloadProgress);
    }

    [Fact]
    public void FindResultsStayInTheFindChromeInsteadOfReplacingProductNotices()
    {
        var browser = new BrowserPresentationHost();
        browser.ApplyProductEvent(
            new BrowserProductEvent.CertificateRejected(
                Address("https://example.test"),
                BrowserCertificateErrorKind.UntrustedAuthority,
                "example.test",
                "Unknown"));

        browser.ApplyProductEvent(
            new BrowserProductEvent.FindUpdated(
                MatchCount: 4,
                ActiveMatchOrdinal: 2,
                IsFinal: true));

        Assert.Equal("2 of 4", browser.FindResultText);
        Assert.Equal("Certificate rejected", browser.ProductHeading);
    }

    [Theory]
    [InlineData("https://example.test/sign-in")]
    [InlineData("http://localhost:8080/callback")]
    public void System_browser_handoff_accepts_only_the_presented_http_page(
        string value)
    {
        var presented = new BrowserAddress(new Uri(value));

        Assert.True(BrowserPresentationHost.TryGetSystemBrowserAddress(
            presented,
            out var address));
        Assert.Equal(presented.Value, address);
    }

    [Fact]
    public void System_browser_handoff_rejects_blank_document_and_local_file_addresses()
    {
        var localPath = Path.Combine(Path.GetTempPath(), "ghostshell-browser-handoff.html");

        Assert.False(BrowserPresentationHost.TryGetSystemBrowserAddress(
            BrowserAddress.Blank,
            out _));
        Assert.False(BrowserPresentationHost.TryGetSystemBrowserAddress(
            BrowserAddress.ForDocument("<html></html>"),
            out _));
        Assert.False(BrowserPresentationHost.TryGetSystemBrowserAddress(
            BrowserAddress.ForLocalFile(localPath),
            out _));
    }

    /// <summary>
    /// Hiding a native surface must never give it up.
    ///
    /// A native control does not survive leaving the visual tree — the framework
    /// destroys it and builds a fresh one on the way back — so a surface that was
    /// removed whenever its panel went off screen lost the page it was showing.
    /// That is what adding a panel beside a browser used to do: rearranging
    /// panels rebuilds the views that draw them, and the document went with the
    /// view. Concealing keeps the surface parented; only the panel's own end
    /// releases it.
    /// </summary>
    [Fact]
    public void Concealing_a_native_surface_keeps_it_parented_and_only_release_gives_it_up()
    {
        var layer = new NativeSurfaceLayer();
        var surface = new Border();

        layer.Present(surface, new Rect(10, 20, 300, 200));
        Assert.Contains(surface, layer.Children);
        Assert.True(surface.IsVisible);
        Assert.Equal(10, Canvas.GetLeft(surface));
        Assert.Equal(20, Canvas.GetTop(surface));
        Assert.Equal(300, surface.Width);
        Assert.Equal(200, surface.Height);

        layer.Conceal(surface);
        Assert.Contains(surface, layer.Children);
        Assert.False(surface.IsVisible);

        // And showing it again is a move, not a rebuild.
        layer.Present(surface, new Rect(0, 0, 120, 90));
        Assert.Single(layer.Children);
        Assert.True(surface.IsVisible);

        layer.Release(surface);
        Assert.DoesNotContain(surface, layer.Children);
    }

    [Fact]
    public void Browser_visual_is_hosted_as_normal_content()
    {
        var visual = new Border();
        var rendererView = new BrowserRendererView(
            visual,
            new RecordingBrowserRenderer());
        var host = new BrowserPresentationHost { RendererView = rendererView };

        Assert.Same(visual, host.Content);
        Assert.Same(host, rendererView.PresentationHost);
    }

    [Fact]
    public void Browser_panel_activity_controls_the_panel_owned_renderer_indicator()
    {
        var activityStates = new List<bool>();
        var rendererView = new BrowserRendererView(
            new Border(),
            new RecordingBrowserRenderer(),
            agentActivityChanged: activityStates.Add);
        var host = new BrowserPresentationHost
        {
            RendererView = rendererView,
            IsAgentActive = true,
        };

        Assert.Equal([true], activityStates);

        host.IsAgentActive = false;

        Assert.Equal([true, false], activityStates);
    }

    [Fact]
    public void Browser_panel_opens_developer_tools_through_its_owned_renderer()
    {
        var openCount = 0;
        var rendererView = new BrowserRendererView(
            new Border(),
            new RecordingBrowserRenderer(),
            developerToolsRequested: () =>
            {
                openCount++;
                return true;
            });
        var host = new BrowserPresentationHost { RendererView = rendererView };

        host.OpenDeveloperTools();

        Assert.Equal(1, openCount);
        Assert.Empty(host.StatusMessage);
    }

    [Fact]
    public void Browser_panel_explains_when_developer_tools_are_not_ready()
    {
        var host = new BrowserPresentationHost();

        host.OpenDeveloperTools();

        Assert.Equal(
            "Developer tools are unavailable until the browser is ready.",
            host.StatusMessage);
    }

    /// <summary>
    /// A panel floated into a window of its own takes its surface with it, and
    /// brings it back. By the time it comes back the window it was in has closed
    /// — so the layer that still holds the surface is not one of the layers any
    /// more, and looking for it among the living found nothing. Adding a control
    /// that still has a parent throws, and it took the shell down with it.
    /// </summary>
    [Fact]
    public void A_surface_moves_between_layers_even_when_the_one_it_left_is_gone()
    {
        // Any parent at all, not only a layer the shell still knows about — a
        // plain panel stands in for the layer of a window that has closed, which
        // is exactly the parent nothing was looking for.
        var left = new Panel();
        var arrived = new NativeSurfaceLayer();
        var surface = new Border();

        left.Children.Add(surface);

        arrived.Present(surface, new Rect(5, 6, 400, 300));

        Assert.DoesNotContain(surface, left.Children);
        Assert.Contains(surface, arrived.Children);
        Assert.True(surface.IsVisible);
        Assert.Equal(5, Canvas.GetLeft(surface));
        Assert.Equal(400, surface.Width);
    }

    /// <summary>
    /// Dock may bind the arriving host before it unbinds the departing one. The
    /// visual follows the panel to the new host, while its renderer lifetime
    /// remains owned by the panel.
    /// </summary>
    [Fact]
    public void Browser_visual_moves_between_hosts_without_disposing_its_renderer()
    {
        var lifetime = new RecordingLifetime();
        var visual = new Border();
        var rendererView = new BrowserRendererView(
            visual,
            new RecordingBrowserRenderer(),
            lifetime);
        var attachment = new BrowserRendererAttachment(
            DispatchProxy.Create<ISessionHostClient, NoopSessionClient>(),
            new ClientId("client"),
            SessionId.New(),
            AttachmentId.New());
        rendererView.Attachment = attachment;
        var departing = new BrowserPresentationHost { RendererView = rendererView };
        var arriving = new BrowserPresentationHost { RendererView = rendererView };

        Assert.Null(departing.Content);
        Assert.Same(visual, arriving.Content);
        Assert.Same(arriving, rendererView.PresentationHost);
        Assert.Same(attachment, rendererView.Attachment);
        Assert.Equal(0, lifetime.DisposeCount);

        departing.RendererView = null;

        Assert.Same(visual, arriving.Content);
        Assert.Same(attachment, rendererView.Attachment);
        Assert.Equal(0, lifetime.DisposeCount);

        arriving.RendererView = null;

        Assert.Null(arriving.Content);
        Assert.Null(rendererView.PresentationHost);
        Assert.Same(attachment, rendererView.Attachment);
        Assert.Equal(0, lifetime.DisposeCount);
    }

    /// <summary>
    /// A native view is composited above everything the shell draws, so the only
    /// way to show something over it is for it to leave. Suspending is that, and
    /// it must be exactly reversible: the dock's placement targets are unreachable
    /// under an operating-system view, and a drag that ended with the surface
    /// still gone would be a worse bug than the one it fixed.
    /// </summary>
    [Fact]
    public void Suspending_takes_every_surface_off_screen_and_ending_it_puts_them_back()
    {
        var layer = new NativeSurfaceLayer();
        var shown = new Border();
        var concealed = new Border();

        layer.Present(shown, new Rect(0, 0, 100, 100));
        layer.Present(concealed, new Rect(0, 0, 100, 100));
        layer.Conceal(concealed);

        var outer = NativeSurfaceLayer.Suspend();
        var inner = NativeSurfaceLayer.Suspend();
        Assert.False(shown.IsVisible);

        // A surface presented mid-drag stays off screen until the drag ends.
        var late = new Border();
        layer.Present(late, new Rect(0, 0, 100, 100));
        Assert.False(late.IsVisible);

        inner.Dispose();
        Assert.False(shown.IsVisible);

        outer.Dispose();
        Assert.True(shown.IsVisible);
        Assert.True(late.IsVisible);
        // And a surface its panel had already hidden stays hidden.
        Assert.False(concealed.IsVisible);
    }

    /// <summary>
    /// The panel owns the visual and attachment, so ending the panel is what ends
    /// both — not whichever control happened to be drawing it.
    /// </summary>
    [Fact]
    public void Disposing_the_renderer_view_releases_it_from_the_presentation_host()
    {
        var lifetime = new RecordingLifetime();
        var view = new Border();
        var rendererView = new BrowserRendererView(
            view,
            new RecordingBrowserRenderer(),
            lifetime);
        var host = new BrowserPresentationHost { RendererView = rendererView };

        rendererView.Dispose();

        Assert.Null(host.Content);
        Assert.Null(rendererView.PresentationHost);
        Assert.Null(rendererView.Attachment);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task BrowserStateFlowsIntoRecoveryAndRendererLifetimeIsReleasedOnce()
    {
        var lifetime = new RecordingLifetime();
        var renderer = new RecordingBrowserRenderer();
        var rendererView = new BrowserRendererView(new Border(), renderer, lifetime);
        var panel = new BrowserRuntimePanelViewModel(
            new PanelInstanceId("browser-panel"),
            "Documentation",
            new SessionOwner(
                HostMode.Desktop,
                new WindowInstanceId("window"),
                new WorkspaceInstanceId("workspace"),
                new TabInstanceId("tab"),
                new PanelInstanceId("browser-panel")),
            BrowserAddress.Blank,
            DispatchProxy.Create<ISessionHostClient, NoopSessionClient>(),
            new ClientId("client"),
            BuiltInConnections.Local,
            new RecordingBrowserRendererViewFactory(rendererView));
        await panel.StartInitialization();
        var address = Address("https://docs.example.test/guide");
        panel.ApplyBrowserState(new BrowserSessionState(
            address,
            "Guide",
            BrowserLoadState.Ready,
            canGoBack: true,
            canGoForward: false,
            documentRevision: 7));
        var tab = new RuntimeTabViewModel(
            new TabInstanceId("tab"),
            "Docs",
            "TEST");
        tab.AddPanel(panel);
        var workspace = new RuntimeWorkspaceViewModel(
            new WorkspaceInstanceId("workspace"),
            "Workspace",
            "#123456",
            []);
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;

        var json = RuntimeWorkspaceRecoveryCodec.Serialize(workspace);
        var deserialized = RuntimeWorkspaceRecoveryCodec.TryDeserialize(
            new RuntimeRecoverySnapshot(
                "run",
                RuntimeWorkspaceRecoveryCodec.SnapshotKey,
                RuntimeWorkspaceRecoveryCodec.SchemaVersion,
                json,
                DateTimeOffset.UnixEpoch),
            out var recovery,
            out var error);

        Assert.True(deserialized, error);
        var recoveredPanel = Assert.Single(
            Assert.Single(recovery!.Workspace!.Tabs).Panels);
        Assert.Equal(RuntimePanelRecoveryKind.Browser, recoveredPanel.Kind);
        Assert.Equal(address.ToString(), recoveredPanel.StartupLocation);
        Assert.Equal(BuiltInConnections.Local.Id.Value, recoveredPanel.ConnectionId);
        Assert.Null(recoveredPanel.FileLocation);

        panel.Dispose();
        panel.Dispose();
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task ProfileAndPopupRequestsFlowAcrossThePanelBoundary()
    {
        var renderer = new RecordingBrowserRenderer();
        var rendererView = new BrowserRendererView(new Border(), renderer);
        var factory = new RecordingBrowserRendererViewFactory(rendererView);
        var profile = BrowserProfileKey.ForWorkspace("workspace-saved");
        var panel = new BrowserRuntimePanelViewModel(
            new PanelInstanceId("browser-panel"),
            "Documentation",
            new SessionOwner(
                HostMode.Desktop,
                new WindowInstanceId("window"),
                new WorkspaceInstanceId("workspace"),
                new TabInstanceId("tab"),
                new PanelInstanceId("browser-panel")),
            BrowserAddress.Blank,
            DispatchProxy.Create<ISessionHostClient, NoopSessionClient>(),
            new ClientId("client"),
            BuiltInConnections.Local,
            profile,
            factory);
        BrowserNewTabRequestedEventArgs? requested = null;
        panel.NewTabRequested += (_, args) => requested = args;

        await panel.StartInitialization();
        var popup = Address("https://docs.example.test/popup");
        renderer.RaiseNewTabRequested(popup);

        Assert.Equal(profile, factory.LastProfile);
        Assert.Equal(popup, requested?.Address);

        panel.Dispose();
        renderer.RaiseNewTabRequested(Address("https://docs.example.test/late"));
        Assert.Equal(popup, requested?.Address);
    }

    [Fact]
    public void WorkspaceProfileFooterNamesTheIsolatedPartition()
    {
        var panelId = new PanelInstanceId("browser-panel");
        using var panel = new BrowserRuntimePanelViewModel(
            panelId,
            "Documentation",
            new SessionOwner(
                HostMode.Desktop,
                new WindowInstanceId("window"),
                new WorkspaceInstanceId("workspace"),
                new TabInstanceId("tab"),
                panelId),
            BrowserAddress.Blank,
            DispatchProxy.Create<ISessionHostClient, NoopSessionClient>(),
            new ClientId("client"),
            BuiltInConnections.Local,
            new BrowserProfileBinding(
                new BrowserProfileSelection(
                    BuiltInBrowserProfiles.Default.Id,
                    BrowserProfileKey.ForWorkspace("workspace-saved")),
                BuiltInBrowserProfiles.Default,
                revision: 1),
            new RecordingBrowserRendererViewFactory(
                new BrowserRendererView(
                    new Border(),
                    new RecordingBrowserRenderer())));

        Assert.Equal(
            "Default browser · Isolated workspace",
            panel.BrowserProfileDisplayName);
    }

    private static BrowserAddress Address(string value)
    {
        Assert.True(BrowserAddress.TryParse(value, out var address));
        return address;
    }

    private sealed class RecordingLifetime : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingBrowserRendererViewFactory(
        BrowserRendererView rendererView) : IBrowserRendererViewFactory
    {
        public BrowserProfileKey? LastProfile { get; private set; }

        public BrowserRendererView Create() => rendererView;

        public BrowserRendererView CreateIsolatedHtmlPreview() => rendererView;

        public ValueTask<BrowserRendererView> CreateAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(rendererView);
        }

        public ValueTask<BrowserRendererView> CreateAsync(
            ConnectionProfile connection,
            BrowserProfileKey profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastProfile = profile;
            return ValueTask.FromResult(rendererView);
        }
    }

    private sealed class RecordingBrowserRenderer :
        IBrowserRenderer,
        IBrowserPhysicalInputBarrier,
        IBrowserNewTabRequestSource
    {
        public BrowserSessionState State { get; } =
            BrowserSessionState.Initial(BrowserAddress.Blank);

        public CapabilitySet Capabilities { get; } = new(
        [
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserAgentInputBarrier,
        ]);

        public event EventHandler<BrowserStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<BrowserNewTabRequestedEventArgs>? NewTabRequested;

        public void RaiseNewTabRequested(BrowserAddress address) =>
            NewTabRequested?.Invoke(
                this,
                new BrowserNewTabRequestedEventArgs(address, userGesture: true));

        public void BindPhysicalInputGate(
            Func<NativeRendererPhysicalInput, bool>? physicalInputGate)
        {
            _ = physicalInputGate;
        }

        public ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
            BrowserAddress address,
            CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
            CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
            CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
            CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
            CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserSessionState>>
            NavigateWithinOriginAsync(
                BrowserOriginConstrainedNavigationRequest request,
                BrowserNavigationOrigin allowedOrigin,
                BrowserNavigationStartBinding startBinding,
                CancellationToken cancellationToken) =>
            Success(cancellationToken);

        public ValueTask<BrowserResult<BrowserDocumentSnapshot>>
            CaptureSnapshotAsync(
                BrowserDocumentBinding document,
                CancellationToken cancellationToken,
                BrowserSnapshotQuery? query = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Success(
                    new BrowserDocumentSnapshot(
                        document,
                        [new BrowserSnapshotNode(
                            0,
                            "document",
                            string.Empty)],
                        DateTimeOffset.UnixEpoch)));
        }

        public ValueTask<BrowserResult<BrowserClickReceipt>>
            ClickWithinOriginAsync(
                BrowserElementReference reference,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserClickReceipt>.Success(
                    new BrowserClickReceipt(reference.Document)));
        }

        public ValueTask<BrowserResult<BrowserFillReceipt>>
            FillWithinOriginAsync(
                BrowserElementReference reference,
                string text,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserFillReceipt>.Success(
                    new BrowserFillReceipt(reference.Document)));
        }

        public ValueTask<BrowserResult<BrowserCheckReceipt>>
            CheckWithinOriginAsync(
                BrowserElementReference reference,
                BrowserNavigationOrigin allowedOrigin,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserCheckReceipt>.Success(
                    new BrowserCheckReceipt(reference.Document)));
        }

        private ValueTask<BrowserResult<BrowserSessionState>> Success(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                BrowserResult<BrowserSessionState>.Success(State));
        }
    }

    public class NoopSessionClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }
}
