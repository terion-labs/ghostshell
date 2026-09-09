using Asura.Application;
using Asura.Core;

namespace Asura.Browser.Tests;

public sealed class BrowserPanelSessionTests
{
    [Fact]
    public async Task FactoryCreatesADetachedSessionAtTheInitialAddress()
    {
        var factory = new BrowserPanelSessionFactory(
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var address = Address("https://example.test/start");
        await using var session = await factory.CreateAsync(
            new SessionId("browser-1"),
            address,
            CancellationToken.None);

        var snapshot = await session.SnapshotAsync(CancellationToken.None);
        var navigation = await session.NavigateAsync(
            Address("https://example.test/other"),
            CancellationToken.None);

        Assert.Equal(PanelKind.Browser, session.Kind);
        Assert.Equal(address, session.State.Address);
        Assert.Equal(SessionLifecycle.Starting, snapshot.Lifecycle);
        Assert.Equal(SessionHealth.Starting, snapshot.Health);
        Assert.False(navigation.IsSuccess);
        Assert.Equal(BrowserErrorCode.RendererUnavailable, navigation.Error?.Code);
        Assert.Equal(factory.Capabilities, session.Capabilities);
    }

    [Fact]
    public async Task AttachLoadsTheSessionAddressAndRejectsASecondRenderer()
    {
        var address = Address("https://example.test/session");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();

        await session.AttachRendererAsync(renderer, CancellationToken.None);
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        var snapshot = await session.SnapshotAsync(CancellationToken.None);

        Assert.Equal(1, renderer.NavigateCount);
        Assert.Equal(address, renderer.LastNavigatedAddress);
        Assert.Equal(BrowserLoadState.Loading, session.State.LoadState);
        Assert.False(snapshot.HasActiveWork);
        Assert.Equal("The browser is loading a page.", snapshot.StatusDetail);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.AttachRendererAsync(
                new RecordingBrowserRenderer(),
                CancellationToken.None));
    }

    [Fact]
    public async Task NetworkObservationLifetimeReachesTheAttachedRenderer()
    {
        await using var session = Session(
            Address("https://example.test/network"));
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);

        await session.BeginNetworkActivityObservationAsync(
            CancellationToken.None);
        await session.EndNetworkActivityObservationAsync(
            CancellationToken.None);

        Assert.Equal(1, renderer.BeginNetworkActivityObservationCount);
        Assert.Equal(1, renderer.EndNetworkActivityObservationCount);
    }

    [Fact]
    public async Task NetworkObservationCleanupReturnsToItsOriginalRenderer()
    {
        await using var session = Session(
            Address("https://example.test/network-replacement"));
        var original = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(original, CancellationToken.None);
        await session.BeginNetworkActivityObservationAsync(
            CancellationToken.None);

        await session.DetachRendererAsync(CancellationToken.None);
        var replacement = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(replacement, CancellationToken.None);
        await session.EndNetworkActivityObservationAsync(
            CancellationToken.None);

        Assert.Equal(1, original.BeginNetworkActivityObservationCount);
        Assert.Equal(1, original.EndNetworkActivityObservationCount);
        Assert.Equal(0, replacement.EndNetworkActivityObservationCount);
    }

    [Fact]
    public async Task RendererStateUpdatesTheLogicalStateAndPanelSnapshot()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);

        renderer.Complete(address, canGoBack: true);
        var snapshot = await session.SnapshotAsync(CancellationToken.None);

        Assert.Equal(BrowserLoadState.Ready, session.State.LoadState);
        Assert.Equal(1, session.State.DocumentRevision);
        Assert.True(session.State.CanGoBack);
        Assert.Equal(SessionLifecycle.Active, snapshot.Lifecycle);
        Assert.Equal(SessionHealth.Healthy, snapshot.Health);
        Assert.False(snapshot.HasActiveWork);
    }

    [Fact]
    public async Task DetachKeepsTheSessionOpenAndReattachPreservesRevisionMonotonicity()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var first = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(first, CancellationToken.None);
        first.Complete(address);

        await session.DetachRendererAsync(CancellationToken.None);
        var detachedSnapshot = await session.SnapshotAsync(CancellationToken.None);
        var detachedNavigation = await session.ReloadAsync(CancellationToken.None);
        var second = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(second, CancellationToken.None);
        second.Complete(address);

        Assert.Equal(SessionLifecycle.Active, detachedSnapshot.Lifecycle);
        Assert.Equal(SessionHealth.Unavailable, detachedSnapshot.Health);
        Assert.False(detachedNavigation.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            detachedNavigation.Error?.Code);
        Assert.Equal(2, session.State.DocumentRevision);
    }

    [Fact]
    public async Task GovernedNavigationTranslatesLogicalRevisionForAReplacementRenderer()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var first = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(first, CancellationToken.None);
        first.Complete(address);
        await session.DetachRendererAsync(CancellationToken.None);
        var replacement = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(
            replacement,
            CancellationToken.None);
        replacement.Complete(address);
        var logicalBinding = BrowserNavigationStartBinding.FromState(
            session.State);
        var rendererBinding = BrowserNavigationStartBinding.FromState(
            replacement.State);

        var result = await session.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Reload(),
            BrowserNavigationOrigin.FromAddress(address),
            logicalBinding,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, logicalBinding.DocumentRevision);
        Assert.Equal(1, rendererBinding.DocumentRevision);
        Assert.Equal(rendererBinding, replacement.LastStartBinding);
    }

    [Fact]
    public async Task SnapshotTranslatesReplacementRendererDocumentBinding()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var first = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(first, CancellationToken.None);
        first.Complete(address);
        await session.DetachRendererAsync(CancellationToken.None);
        var replacement = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(
            replacement,
            CancellationToken.None);
        replacement.Complete(address);
        var logicalDocument =
            BrowserDocumentBinding.FromState(session.State);
        var rendererDocument =
            BrowserDocumentBinding.FromState(replacement.State);

        var result = await session.CaptureSnapshotAsync(
            logicalDocument,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(rendererDocument, replacement.LastSnapshotBinding);
        Assert.Equal(logicalDocument, result.Value?.Document);
        Assert.Equal(
            logicalDocument,
            result.Value?.Nodes[1].Reference?.Document);
        Assert.Equal(2, logicalDocument.DocumentRevision);
        Assert.Equal(1, rendererDocument.DocumentRevision);
    }

    [Fact]
    public async Task ClickTranslatesLogicalReferenceAndKeepsSourceReceipt()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var first = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(first, CancellationToken.None);
        first.Complete(address);
        await session.DetachRendererAsync(CancellationToken.None);
        var replacement = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(
            replacement,
            CancellationToken.None);
        replacement.Complete(address);
        var snapshot = await session.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(session.State),
            CancellationToken.None);
        var logicalReference =
            snapshot.Value!.Nodes[1].Reference!;
        var rendererSource =
            BrowserDocumentBinding.FromState(replacement.State);
        replacement.StateAfterClick = new BrowserSessionState(
            address,
            string.Empty,
            BrowserLoadState.Ready,
            false,
            false,
            rendererSource.DocumentRevision + 1);

        var result = await session.ClickWithinOriginAsync(
            logicalReference,
            BrowserNavigationOrigin.FromAddress(address),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            logicalReference.Document,
            result.Value?.SourceDocument);
        Assert.Equal(
            rendererSource,
            replacement.LastClickReference?.Document);
        Assert.Equal(
            logicalReference.Id,
            replacement.LastClickReference?.Id);
        Assert.Equal(1, replacement.ClickCount);
        Assert.Equal(
            logicalReference.Document.DocumentRevision + 1,
            session.State.DocumentRevision);
    }

    [Fact]
    public async Task ClickRejectsStaleLogicalDocumentBeforeRenderer()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.Complete(address);
        var snapshot = await session.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(session.State),
            CancellationToken.None);
        var staleReference =
            snapshot.Value!.Nodes[1].Reference!;
        renderer.Complete(address);

        var result = await session.ClickWithinOriginAsync(
            staleReference,
            BrowserNavigationOrigin.FromAddress(address),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationStateChanged,
            result.Error?.Code);
        Assert.Equal(0, renderer.ClickCount);
    }

    [Fact]
    public async Task FillTranslatesLogicalReferenceTextAndKeepsSourceReceipt()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var first = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(first, CancellationToken.None);
        first.Complete(address);
        await session.DetachRendererAsync(CancellationToken.None);
        var replacement = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(
            replacement,
            CancellationToken.None);
        replacement.Complete(address);
        var snapshot = await session.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(session.State),
            CancellationToken.None);
        var logicalReference =
            snapshot.Value!.Nodes[1].Reference!;
        var rendererSource =
            BrowserDocumentBinding.FromState(replacement.State);
        replacement.StateAfterFill = new BrowserSessionState(
            address,
            string.Empty,
            BrowserLoadState.Ready,
            false,
            false,
            rendererSource.DocumentRevision + 1);

        var result = await session.FillWithinOriginAsync(
            logicalReference,
            "Ada Lovelace",
            BrowserNavigationOrigin.FromAddress(address),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            logicalReference.Document,
            result.Value?.SourceDocument);
        Assert.Equal(
            rendererSource,
            replacement.LastFillReference?.Document);
        Assert.Equal(
            logicalReference.Id,
            replacement.LastFillReference?.Id);
        Assert.Equal("Ada Lovelace", replacement.LastFillText);
        Assert.Equal(1, replacement.FillCount);
        Assert.Equal(
            logicalReference.Document.DocumentRevision + 1,
            session.State.DocumentRevision);
        Assert.DoesNotContain(
            "Ada Lovelace",
            result.Value?.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FillRejectsStaleLogicalDocumentBeforeRenderer()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.Complete(address);
        var snapshot = await session.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(session.State),
            CancellationToken.None);
        var staleReference =
            snapshot.Value!.Nodes[1].Reference!;
        renderer.Complete(address);

        var result = await session.FillWithinOriginAsync(
            staleReference,
            "stale",
            BrowserNavigationOrigin.FromAddress(address),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.NavigationStateChanged,
            result.Error?.Code);
        Assert.Equal(0, renderer.FillCount);
    }

    [Fact]
    public async Task CheckTranslatesLogicalReferenceAndKeepsSourceReceipt()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var first = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(first, CancellationToken.None);
        first.Complete(address);
        await session.DetachRendererAsync(CancellationToken.None);
        var replacement = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(
            replacement,
            CancellationToken.None);
        replacement.Complete(address);
        var snapshot = await session.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(session.State),
            CancellationToken.None);
        var logicalReference =
            snapshot.Value!.Nodes[1].Reference!;
        var rendererSource =
            BrowserDocumentBinding.FromState(replacement.State);
        replacement.StateAfterCheck = new BrowserSessionState(
            address,
            string.Empty,
            BrowserLoadState.Ready,
            false,
            false,
            rendererSource.DocumentRevision + 1);

        var result = await session.CheckWithinOriginAsync(
            logicalReference,
            BrowserNavigationOrigin.FromAddress(address),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            logicalReference.Document,
            result.Value?.SourceDocument);
        Assert.Equal(
            rendererSource,
            replacement.LastCheckReference?.Document);
        Assert.Equal(
            logicalReference.Id,
            replacement.LastCheckReference?.Id);
        Assert.Equal(
            BrowserNavigationOrigin.FromAddress(address),
            replacement.LastCheckOrigin);
        Assert.Equal(1, replacement.CheckCount);
        Assert.Equal(
            logicalReference.Document.DocumentRevision + 1,
            session.State.DocumentRevision);
    }

    [Fact]
    public async Task CheckRejectsStaleLogicalDocumentBeforeRenderer()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.Complete(address);
        var snapshot = await session.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(session.State),
            CancellationToken.None);
        var staleReference =
            snapshot.Value!.Nodes[1].Reference!;
        renderer.Complete(address);

        var result = await session.CheckWithinOriginAsync(
            staleReference,
            BrowserNavigationOrigin.FromAddress(address),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.NavigationStateChanged,
            result.Error?.Code);
        Assert.Equal(0, renderer.CheckCount);
    }

    [Fact]
    public async Task CheckRejectsRendererReceiptForAnotherDocument()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.Complete(address);
        var snapshot = await session.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(session.State),
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;
        renderer.CheckResult =
            BrowserResult<BrowserCheckReceipt>.Success(
                new BrowserCheckReceipt(
                    new BrowserDocumentBinding(
                        address,
                        renderer.State.DocumentRevision + 1)));

        var result = await session.CheckWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(address),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.InteractionOutcomeUnknown,
            result.Error?.Code);
        Assert.Equal(1, renderer.CheckCount);
    }

    [Fact]
    public async Task ReattachingTheRetainedRendererDoesNotReloadOrDuplicateHistory()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.Complete(address, canGoBack: true);

        await session.DetachRendererAsync(CancellationToken.None);
        await session.AttachRendererAsync(renderer, CancellationToken.None);

        Assert.Equal(1, renderer.NavigateCount);
        Assert.Equal(address, session.State.Address);
        Assert.Equal(BrowserLoadState.Ready, session.State.LoadState);
        Assert.True(session.State.CanGoBack);
        Assert.Equal(1, session.State.DocumentRevision);
    }

    [Fact]
    public async Task ReattachingRetainedRendererPreservesDetachedDocumentAdvance()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.Complete(address);

        await session.DetachRendererAsync(CancellationToken.None);
        _ = await renderer.ReloadAsync(CancellationToken.None);
        renderer.Complete(address);
        await session.AttachRendererAsync(renderer, CancellationToken.None);

        Assert.Equal(1, renderer.NavigateCount);
        Assert.Equal(address, session.State.Address);
        Assert.Equal(BrowserLoadState.Ready, session.State.LoadState);
        Assert.Equal(2, session.State.DocumentRevision);
    }

    [Fact]
    public async Task RendererRevisionRegressionFailsClosedBeforeSnapshot()
    {
        var address = Address("https://example.test/document");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.Complete(address);
        await session.DetachRendererAsync(CancellationToken.None);
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        var logicalDocument =
            BrowserDocumentBinding.FromState(session.State);

        renderer.SetDocumentRevisionForTest(0);
        var result = await session.CaptureSnapshotAsync(
            logicalDocument,
            CancellationToken.None);

        Assert.Equal(BrowserLoadState.Failed, session.State.LoadState);
        Assert.Equal(
            logicalDocument.DocumentRevision,
            session.State.DocumentRevision);
        Assert.Equal(
            BrowserErrorCode.EngineFailed,
            session.State.Failure?.Code);
        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationStateChanged,
            result.Error?.Code);
        Assert.Null(renderer.LastSnapshotBinding);
    }

    [Fact]
    public async Task DetachInvalidatesSurfaceElementReferences()
    {
        await using var session = Session(BrowserAddress.Blank);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = NativeBrowserSnapshotResult.Success(
                new NativeBrowserSnapshot(
                    [
                        new NativeBrowserSnapshotNode(
                            0,
                            "document",
                            "Example",
                            BrowserSnapshotNodeState.None,
                            Handle: null),
                        new NativeBrowserSnapshotNode(
                            1,
                            "button",
                            "Continue",
                            BrowserSnapshotNodeState.None,
                            Handle: new NativeBrowserElementHandle(
                                "snapshot_test",
                                "element_0",
                                0)),
                    ],
                    IsTruncated: false)),
        };
        var renderer = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        nativeView.RaiseNavigationCompleted(
            BrowserAddress.Blank,
            isSuccess: true);
        var snapshot = await session.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(session.State),
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;
        Assert.True(renderer.TryResolveElementReference(
            new BrowserElementReference(
                reference.Value,
                BrowserDocumentBinding.FromState(renderer.State)),
            out _));

        await session.DetachRendererAsync(CancellationToken.None);

        Assert.False(renderer.TryResolveElementReference(
            new BrowserElementReference(
                reference.Value,
                BrowserDocumentBinding.FromState(renderer.State)),
            out _));
    }

    [Fact]
    public async Task CloseRejectsNavigationAndDisposeIsIdempotent()
    {
        var session = Session(Address("https://example.test"));
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);

        var close = await session.CloseAsync(
            PanelCloseMode.Graceful,
            CancellationToken.None);
        var secondClose = await session.CloseAsync(
            PanelCloseMode.Force,
            CancellationToken.None);
        var navigation = await session.GoBackAsync(CancellationToken.None);
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(PanelCloseOutcome.GracefullyClosed, close);
        Assert.Equal(PanelCloseOutcome.AlreadyClosed, secondClose);
        Assert.False(navigation.IsSuccess);
        Assert.Equal(BrowserErrorCode.SessionClosed, navigation.Error?.Code);

        var rendererResult = await renderer.NavigateAsync(
            Address("https://example.test/still-owned-by-caller"),
            CancellationToken.None);
        Assert.True(rendererResult.IsSuccess);
    }

    [Fact]
    public async Task AttachRejectsAnIncompleteRendererCapabilitySet()
    {
        await using var session = Session(Address("https://example.test"));
        var renderer = new RecordingBrowserRenderer(
            new CapabilitySet([SessionCapabilities.BrowserReadState]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.AttachRendererAsync(
                renderer,
                CancellationToken.None));
    }

    [Fact]
    public async Task AttachRejectsCapabilitiesBeyondTheFixedSessionProfile()
    {
        await using var session = new BrowserPanelSession(
            new SessionId("browser-production"),
            BrowserAddress.Blank,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var renderer = new RecordingBrowserRenderer(
            new CapabilitySet(
            [
                .. BrowserCapabilityProfile.Production.Capabilities.Values,
                "browser.unsupported-test-capability",
            ]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await session.AttachRendererAsync(
                renderer,
                CancellationToken.None));
    }

    [Fact]
    public async Task ProductionSessionDispatchesSemanticSnapshots()
    {
        await using var session = new BrowserPanelSession(
            new SessionId("browser-production"),
            BrowserAddress.Blank,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var renderer = new RecordingBrowserRenderer(
            BrowserCapabilityProfile.Production.Capabilities);
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        var document = BrowserDocumentBinding.FromState(session.State);

        var snapshot = await session.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(document, renderer.LastSnapshotBinding);
    }

    [Fact]
    public async Task StopWaitsForAnOrdinaryOperationBeforeCallingTheRenderer()
    {
        var address = Address("https://example.test/ordinary");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.Complete(address);
        renderer.PauseNextReload();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var reload = session.ReloadAsync(timeout.Token).AsTask();
        await renderer.WaitForPausedReloadAsync(timeout.Token);
        var stop = session.StopAsync(timeout.Token).AsTask();

        Assert.False(stop.IsCompleted);
        Assert.Equal(0, renderer.StopCount);

        renderer.ResumeReload();
        var reloadResult = await reload;
        var stopResult = await stop;

        Assert.True(reloadResult.IsSuccess);
        Assert.True(stopResult.IsSuccess);
        Assert.Equal(1, renderer.StopCount);
    }

    [Fact]
    public async Task StopBeforeRendererDispatchCancelsGovernedNavigation()
    {
        var address = Address("https://example.test/governed");
        await using var session = Session(address);
        var renderer = new RecordingBrowserRenderer();
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        renderer.Complete(address);
        renderer.PauseNextGovernedNavigation();
        renderer.RejectStopAsUnavailable = true;
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var governed = session.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Reload(),
            BrowserNavigationOrigin.FromAddress(address),
            BrowserNavigationStartBinding.FromState(session.State),
            timeout.Token).AsTask();
        await renderer.WaitForPausedGovernedNavigationAsync(timeout.Token);

        var stop = await session.StopAsync(timeout.Token);
        var navigation = await governed;

        Assert.True(stop.IsSuccess);
        Assert.False(navigation.IsSuccess);
        Assert.Equal(BrowserErrorCode.Cancelled, navigation.Error?.Code);
        Assert.Null(renderer.LastStartBinding);
        Assert.Equal(1, renderer.StopCount);
    }

    [Fact]
    public async Task StopInterruptsGovernedNavigationWaitingForNativeCompletion()
    {
        await using var session = Session(BrowserAddress.Blank);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            AcceptStop = true,
        };
        var renderer = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        await session.AttachRendererAsync(renderer, CancellationToken.None);
        nativeView.RaiseNavigationCompleted(
            BrowserAddress.Blank,
            isSuccess: true);
        var address = Address("https://example.test/governed");
        var governed = session.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Navigate(address),
            BrowserNavigationOrigin.FromAddress(address),
            BrowserNavigationStartBinding.FromState(session.State),
            CancellationToken.None).AsTask();
        nativeView.RaiseNavigationStarted(address);

        var stop = await session.StopAsync(CancellationToken.None);
        var navigation = await governed;

        Assert.True(stop.IsSuccess);
        Assert.False(navigation.IsSuccess);
        Assert.Equal(BrowserErrorCode.Cancelled, navigation.Error?.Code);
        Assert.Equal(BrowserAddress.Blank, session.State.Address);
        Assert.Equal(1, session.State.DocumentRevision);
    }

    private static BrowserPanelSession Session(BrowserAddress address) =>
        new(
            new SessionId("browser-session"),
            address,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch),
            BrowserCapabilityProfile.FullAutomationCandidate);

    private static BrowserAddress Address(string value)
    {
        Assert.True(BrowserAddress.TryParse(value, out var address));
        return address;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
