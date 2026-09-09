using System.Net;
using Asura.Application;

namespace Asura.Browser.Tests;

public sealed class BrowserSurfaceTests
{
    [Fact]
    public void CreatesTheRealNativeSurfaceWithoutVisualHosting()
    {
        var surface = new BrowserSurface();

        Assert.NotNull(surface.Content);
        Assert.IsAssignableFrom<IBrowserPhysicalInputBarrier>(surface);
        Assert.Same(
            BrowserCapabilityProfile.Production,
            surface.CapabilityProfile);
        Assert.Equal(
            BrowserCapabilityProfile.Production.Capabilities.Values,
            surface.Capabilities.Values);
    }

    [Fact]
    public void StartsBlankWithTheBoundedBrowserCapabilities()
    {
        var surface = Surface(new RecordingEmbeddedBrowserView());

        Assert.Equal(BrowserSessionState.Initial(BrowserAddress.Blank), surface.State);
        Assert.Equal(
        [
            SessionCapabilities.BrowserAgentInputBarrier,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserEvaluate,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserKey,
            SessionCapabilities.BrowserMouse,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserScroll,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserWait,
        ],
            surface.Capabilities.Values);
    }

    [Fact]
    public void AgentActivityReachesTheNativeBrowserView()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);

        surface.SetAgentActivity(isActive: true);
        Assert.True(nativeView.IsAgentActive);

        surface.SetAgentActivity(isActive: false);
        Assert.False(nativeView.IsAgentActive);
    }

    [Fact]
    public void DeveloperToolsRequestReachesOnlyTheNativeBrowserView()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);

        Assert.True(surface.OpenDeveloperTools());

        Assert.Equal(1, nativeView.DeveloperToolsOpenCount);
    }

    [Fact]
    public void NativePopupRequestIsPromotedToANewShellTabRequest()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var address = Address("https://docs.example.test/popup");
        BrowserNewTabRequestedEventArgs? requested = null;
        surface.NewTabRequested += (_, args) => requested = args;

        nativeView.RaiseNewTabRequested(address, userGesture: true);

        Assert.NotNull(requested);
        Assert.Equal(address, requested!.Address);
        Assert.True(requested.UserGesture);
    }

    [Fact]
    public async Task NetworkObservationLifetimeReachesTheNativeBrowserView()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);

        await surface.BeginNetworkActivityObservationAsync(
            CancellationToken.None);
        await surface.EndNetworkActivityObservationAsync(
            CancellationToken.None);

        Assert.Equal(1, nativeView.BeginNetworkActivityObservationCount);
        Assert.Equal(1, nativeView.EndNetworkActivityObservationCount);
    }

    [Fact]
    public async Task ProductionProfileDispatchesNativeSemanticSnapshots()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("production"),
        };
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance);
        var document = BrowserDocumentBinding.FromState(surface.State);

        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.NotNull(snapshot.Value?.Nodes[1].Reference);
        Assert.Equal(1, nativeView.SnapshotCount);
    }

    [Fact]
    public async Task SnapshotNarrowingQueryReachesTheNativeProjectionBoundary()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var query = new BrowserSnapshotQuery(
            interactiveOnly: true,
            filter: "result",
            maximumDepth: 5);

        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None,
            query);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(query, nativeView.LastSnapshotQuery);
    }

    [Fact]
    public async Task SnapshotCapturesBoundedNodesAndOpaqueReferences()
    {
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
                            BrowserSnapshotNodeState.Required,
                            Handle: NativeHandle("0_2")),
                    ],
                    IsTruncated: false)),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);

        var result = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(document, result.Value?.Document);
        Assert.Equal(2, result.Value?.Nodes.Count);
        var button = result.Value!.Nodes[1];
        Assert.Equal("button", button.Role);
        Assert.Equal("Continue", button.Name);
        Assert.Equal(
            BrowserSnapshotNodeState.Required,
            button.States);
        Assert.NotNull(button.Reference);
        Assert.True(surface.TryResolveElementReference(
            button.Reference!,
            out var handle));
        Assert.Equal(NativeHandle("0_2"), handle);
    }

    [Fact]
    public async Task NextSnapshotAndReferenceExpiryInvalidateOldReferences()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("0"),
        };
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var surface = Surface(nativeView, time);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var first = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var firstReference = first.Value!.Nodes[1].Reference!;

        var second = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.False(surface.TryResolveElementReference(
            firstReference,
            out _));
        var secondReference = second.Value!.Nodes[1].Reference!;
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(surface.TryResolveElementReference(
            secondReference,
            out _));
    }

    [Fact]
    public async Task ClickConsumesExactHandleAndAllSnapshotReferences()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("original"),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;

        var result = await surface.ClickWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(document, result.Value?.SourceDocument);
        Assert.Equal(1, nativeView.ClickCount);
        Assert.Equal(NativeHandle("original"), nativeView.LastClickHandle);
        Assert.False(surface.TryResolveElementReference(
            reference,
            out _));

        var repeated = await surface.ClickWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.False(repeated.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.ElementReferenceStale,
            repeated.Error?.Code);
        Assert.Equal(1, nativeView.ClickCount);
    }

    [Fact]
    public async Task ForgedAndWrongDocumentReferencesNeverReachNativeClick()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("original"),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var forged = new BrowserElementReference(
            "be_forged",
            document);

        var forgedResult = await surface.ClickWithinOriginAsync(
            forged,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var wrongDocument = new BrowserElementReference(
            "be_forged",
            new BrowserDocumentBinding(
                Address("https://example.test/other"),
                document.DocumentRevision));
        var wrongResult = await surface.ClickWithinOriginAsync(
            wrongDocument,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.ElementReferenceStale,
            forgedResult.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.NavigationStateChanged,
            wrongResult.Error?.Code);
        Assert.Equal(0, nativeView.ClickCount);
    }

    [Fact]
    public async Task CancellationAfterClickCommitCannotOverwriteActivation()
    {
        var pendingClick =
            new TaskCompletionSource<NativeBrowserClickResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("original"),
            PendingClick = pendingClick,
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;
        using var cancellation = new CancellationTokenSource();

        var click = surface.ClickWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            cancellation.Token).AsTask();
        cancellation.Cancel();
        await Task.Yield();

        Assert.False(click.IsCompleted);
        Assert.Equal(1, nativeView.ClickCount);
        pendingClick.SetResult(
            NativeBrowserClickResult.Activated());

        var result = await click;
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ClickSerializesSnapshotNavigationAndConcurrentClick()
    {
        var pendingClick =
            new TaskCompletionSource<NativeBrowserClickResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("original"),
            PendingClick = pendingClick,
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;
        var click = surface.ClickWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None).AsTask();

        var concurrentClick = await surface.ClickWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var concurrentSnapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var concurrentNavigation = await surface.NavigateAsync(
            Address("https://example.test/next"),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            concurrentClick.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            concurrentSnapshot.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            concurrentNavigation.Error?.Code);
        Assert.Equal(1, nativeView.ClickCount);
        Assert.Equal(0, nativeView.NavigateCount);

        pendingClick.SetResult(
            NativeBrowserClickResult.NotInteractable());
        var result = await click;
        Assert.Equal(
            BrowserErrorCode.ElementNotInteractable,
            result.Error?.Code);
    }

    [Fact]
    public async Task ClickWaitsForSameOriginNavigationTerminalState()
    {
        var pendingClick =
            new TaskCompletionSource<NativeBrowserClickResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("original"),
            PendingClick = pendingClick,
        };
        var surface = Surface(nativeView);
        var source = Address("https://example.test/start");
        _ = await surface.NavigateAsync(
            source,
            CancellationToken.None);
        nativeView.RaiseNavigationCompleted(
            source,
            isSuccess: true);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;
        var destination = Address("https://example.test/next");
        var click = surface.ClickWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(destination),
            CancellationToken.None).AsTask();

        Assert.False(nativeView.RaiseNavigationStarted(destination));
        pendingClick.SetResult(
            NativeBrowserClickResult.Activated());
        await Task.Yield();

        Assert.False(click.IsCompleted);
        Assert.Equal(BrowserLoadState.Loading, surface.State.LoadState);

        nativeView.RaiseNavigationCompleted(
            destination,
            isSuccess: true);
        var result = await click;

        Assert.True(result.IsSuccess);
        Assert.Equal(destination, surface.State.Address);
        Assert.Equal(BrowserLoadState.Ready, surface.State.LoadState);
        Assert.Equal(2, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task CrossOriginClickNavigationFailsAndQuarantinesAdapter()
    {
        var pendingClick =
            new TaskCompletionSource<NativeBrowserClickResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("original"),
            PendingClick = pendingClick,
        };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = SurfaceWithReplacement(nativeView, replacement);
        var source = Address("https://example.test/start");
        _ = await surface.NavigateAsync(
            source,
            CancellationToken.None);
        nativeView.RaiseNavigationCompleted(
            source,
            isSuccess: true);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;
        var click = surface.ClickWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(
                source),
            CancellationToken.None).AsTask();

        Assert.True(nativeView.RaiseNavigationStarted(
            Address("https://other.test/escape")));
        pendingClick.SetResult(
            NativeBrowserClickResult.Activated());
        var result = await click;

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            result.Error?.Code);
        Assert.False(result.Error?.Retryable);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(2, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task ClickDeadlineIsOutcomeUnknownAndFencesLateCompletion()
    {
        var pendingClick =
            new TaskCompletionSource<NativeBrowserClickResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("original"),
            PendingClick = pendingClick,
        };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            () => replacement,
            static _ => { },
            nativeSnapshotDeadline: TimeSpan.FromMilliseconds(25),
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;

        var result = await surface.ClickWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.InteractionOutcomeUnknown,
            result.Error?.Code);
        Assert.False(result.Error?.Retryable);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);

        pendingClick.SetResult(
            NativeBrowserClickResult.Activated());
        await Task.Yield();
        await Task.Yield();

        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task FillConsumesExactHandleTextAndAllSnapshotReferences()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;

        var result = await surface.FillWithinOriginAsync(
            reference,
            "Ada\nLovelace",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(document, result.Value?.SourceDocument);
        Assert.Equal(1, nativeView.FillCount);
        Assert.Equal(NativeHandle("original"), nativeView.LastFillHandle);
        Assert.Equal("Ada\nLovelace", nativeView.LastFillText);
        Assert.False(surface.TryResolveElementReference(reference, out _));

        var repeated = await surface.FillWithinOriginAsync(
            reference,
            "second attempt",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.ElementReferenceStale,
            repeated.Error?.Code);
        Assert.Equal(1, nativeView.FillCount);
    }

    [Fact]
    public async Task InvalidFillReferencesAndPreDispatchCancellationStayNativeFree()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var forged = new BrowserElementReference("be_forged", document);

        var forgedResult = await surface.FillWithinOriginAsync(
            forged,
            "forged",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var wrongDocument = new BrowserElementReference(
            "be_forged",
            new BrowserDocumentBinding(
                Address("https://example.test/other"),
                document.DocumentRevision));
        var wrongResult = await surface.FillWithinOriginAsync(
            wrongDocument,
            "wrong",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await surface.FillWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            "cancelled",
            BrowserNavigationOrigin.FromAddress(document.Address),
            cancellation.Token);

        Assert.Equal(
            BrowserErrorCode.ElementReferenceStale,
            forgedResult.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.NavigationStateChanged,
            wrongResult.Error?.Code);
        Assert.Equal(BrowserErrorCode.Cancelled, cancelled.Error?.Code);
        Assert.Equal(0, nativeView.FillCount);
    }

    [Fact]
    public async Task CancellationAfterFillCommitCannotOverwriteKnownResult()
    {
        var pendingFill =
            new TaskCompletionSource<NativeBrowserFillResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
            PendingFill = pendingFill,
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var fill = surface.FillWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            "committed",
            BrowserNavigationOrigin.FromAddress(document.Address),
            cancellation.Token).AsTask();
        cancellation.Cancel();
        await Task.Yield();

        Assert.False(fill.IsCompleted);
        Assert.Equal(1, nativeView.FillCount);
        pendingFill.SetResult(NativeBrowserFillResult.Filled());

        Assert.True((await fill).IsSuccess);
    }

    [Fact]
    public async Task FillSerializesClickSnapshotNavigationAndAnotherFill()
    {
        var pendingFill =
            new TaskCompletionSource<NativeBrowserFillResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
            PendingFill = pendingFill,
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;
        var fill = surface.FillWithinOriginAsync(
            reference,
            "pending",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None).AsTask();

        var concurrentFill = await surface.FillWithinOriginAsync(
            reference,
            "other",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var concurrentClick = await surface.ClickWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var concurrentSnapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var concurrentNavigation = await surface.NavigateAsync(
            Address("https://example.test/next"),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            concurrentFill.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            concurrentClick.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            concurrentSnapshot.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            concurrentNavigation.Error?.Code);
        Assert.Equal(1, nativeView.FillCount);
        Assert.Equal(0, nativeView.ClickCount);
        Assert.Equal(0, nativeView.NavigateCount);

        pendingFill.SetResult(NativeBrowserFillResult.NotFillable());
        var result = await fill;
        Assert.Equal(
            BrowserErrorCode.ElementNotFillable,
            result.Error?.Code);
    }

    [Theory]
    [InlineData(
        2,
        BrowserErrorCode.ElementNotInteractable)]
    [InlineData(
        3,
        BrowserErrorCode.ElementNotFillable)]
    [InlineData(
        5,
        BrowserErrorCode.FillValueNotSupported)]
    [InlineData(
        1,
        BrowserErrorCode.ElementReferenceStale)]
    public async Task FillMapsClosedPreCommitFailures(
        int nativeStatusValue,
        BrowserErrorCode expectedError)
    {
        var nativeStatus =
            (NativeBrowserFillStatus)nativeStatusValue;
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
            FillResult = nativeStatus switch
            {
                NativeBrowserFillStatus.NotInteractable =>
                    NativeBrowserFillResult.NotInteractable(),
                NativeBrowserFillStatus.NotFillable =>
                    NativeBrowserFillResult.NotFillable(),
                NativeBrowserFillStatus.ValueNotSupported =>
                    NativeBrowserFillResult.ValueNotSupported(),
                NativeBrowserFillStatus.Stale =>
                    NativeBrowserFillResult.Stale(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(nativeStatus)),
            },
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        var result = await surface.FillWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            "value",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.Equal(expectedError, result.Error?.Code);
    }

    [Fact]
    public async Task FillWaitsForSameOriginNavigationTerminalState()
    {
        var pendingFill =
            new TaskCompletionSource<NativeBrowserFillResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
            PendingFill = pendingFill,
        };
        var surface = Surface(nativeView);
        var source = Address("https://example.test/start");
        _ = await surface.NavigateAsync(source, CancellationToken.None);
        nativeView.RaiseNavigationCompleted(source, isSuccess: true);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var destination = Address("https://example.test/next");
        var fill = surface.FillWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            "navigate",
            BrowserNavigationOrigin.FromAddress(destination),
            CancellationToken.None).AsTask();

        Assert.False(nativeView.RaiseNavigationStarted(destination));
        pendingFill.SetResult(NativeBrowserFillResult.Filled());
        await Task.Yield();

        Assert.False(fill.IsCompleted);
        Assert.Equal(BrowserLoadState.Loading, surface.State.LoadState);

        nativeView.RaiseNavigationCompleted(destination, isSuccess: true);
        var result = await fill;
        Assert.True(result.IsSuccess);
        Assert.Equal(destination, surface.State.Address);
        Assert.Equal(2, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task CrossOriginFillNavigationFailsAndQuarantinesAdapter()
    {
        var pendingFill =
            new TaskCompletionSource<NativeBrowserFillResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
            PendingFill = pendingFill,
        };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = SurfaceWithReplacement(nativeView, replacement);
        var source = Address("https://example.test/start");
        _ = await surface.NavigateAsync(source, CancellationToken.None);
        nativeView.RaiseNavigationCompleted(source, isSuccess: true);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var fill = surface.FillWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            "escape",
            BrowserNavigationOrigin.FromAddress(source),
            CancellationToken.None).AsTask();

        Assert.True(nativeView.RaiseNavigationStarted(
            Address("https://other.test/escape")));
        pendingFill.SetResult(NativeBrowserFillResult.Filled());
        var result = await fill;

        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            result.Error?.Code);
        Assert.False(result.Error?.Retryable);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(2, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task FillNavigationRejectionIsPolicyFailure()
    {
        var pendingFill =
            new TaskCompletionSource<NativeBrowserFillResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
            PendingFill = pendingFill,
        };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = SurfaceWithReplacement(nativeView, replacement);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var fill = surface.FillWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            "rejected",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None).AsTask();

        nativeView.RaiseNavigationRejected();
        pendingFill.SetResult(NativeBrowserFillResult.Filled());
        var result = await fill;

        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            result.Error?.Code);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task FillDeadlineIsOutcomeUnknownAndFencesLateCompletion()
    {
        var pendingFill =
            new TaskCompletionSource<NativeBrowserFillResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
            PendingFill = pendingFill,
        };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            () => replacement,
            static _ => { },
            nativeSnapshotDeadline: TimeSpan.FromMilliseconds(25),
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        var result = await surface.FillWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            "timeout",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.InteractionOutcomeUnknown,
            result.Error?.Code);
        Assert.False(result.Error?.Retryable);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);

        pendingFill.SetResult(NativeBrowserFillResult.Filled());
        await Task.Yield();
        await Task.Yield();

        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task SynchronousFillFailureWithoutReplacementBlocksFurtherUse()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
            ThrowOnFill = true,
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        var fill = await surface.FillWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            "ambiguous",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var navigation = await surface.NavigateAsync(
            Address("https://example.test/next"),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.InteractionOutcomeUnknown,
            fill.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            navigation.Error?.Code);
        Assert.Equal(0, nativeView.NavigateCount);
    }

    [Fact]
    public async Task ControlFillTextIsRejectedBeforeLeaseConsumption()
    {
        await AssertFillTextRejectedWithoutConsumingLeaseAsync("\0");
    }

    [Fact]
    public async Task UnpairedSurrogateFillTextIsRejectedBeforeLeaseConsumption()
    {
        await AssertFillTextRejectedWithoutConsumingLeaseAsync(
            new string('\ud800', 1));
    }

    private static async Task
        AssertFillTextRejectedWithoutConsumingLeaseAsync(
            string invalidText)
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;

        await Assert.ThrowsAsync<ArgumentException>(
            () => surface.FillWithinOriginAsync(
                    reference,
                    invalidText,
                    BrowserNavigationOrigin.FromAddress(document.Address),
                    CancellationToken.None)
                .AsTask());
        var valid = await surface.FillWithinOriginAsync(
            reference,
            "valid",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.True(valid.IsSuccess);
        Assert.Equal(1, nativeView.FillCount);
        Assert.Equal("valid", nativeView.LastFillText);
    }

    [Fact]
    public async Task OversizedFillTextIsRejectedBeforeNativeDispatch()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => surface.FillWithinOriginAsync(
                    snapshot.Value!.Nodes[1].Reference!,
                    new string(
                        'x',
                        BrowserElementFillRequest.MaximumTextBytes + 1),
                    BrowserNavigationOrigin.FromAddress(document.Address),
                    CancellationToken.None)
                .AsTask());

        Assert.Equal(0, nativeView.FillCount);
    }

    [Fact]
    public async Task FillDispatcherFailureReturnsUnknownAndPermanentlyFencesAdapter()
    {
        var pendingFill =
            new TaskCompletionSource<NativeBrowserFillResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = FillableSnapshot("original"),
            PendingFill = pendingFill,
        };
        var dispatcher = new FailingBrowserUiDispatcher();
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            dispatcher,
            () => new RecordingEmbeddedBrowserView(),
            static _ => { },
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var fill = surface.FillWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            "ambiguous",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None).AsTask();

        dispatcher.FailMarshalling();
        pendingFill.SetResult(NativeBrowserFillResult.Filled());
        var result = await fill.WaitAsync(TimeSpan.FromSeconds(1));
        dispatcher.RestoreAccess();
        var navigation = await surface.NavigateAsync(
            Address("https://example.test/next"),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.InteractionOutcomeUnknown,
            result.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            navigation.Error?.Code);
        Assert.Equal(0, nativeView.NavigateCount);
    }

    [Fact]
    public async Task CheckConsumesExactHandleAndAllSnapshotReferences()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;
        var siblingReference = snapshot.Value.Nodes[2].Reference!;

        var result = await surface.CheckWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(document, result.Value?.SourceDocument);
        Assert.Equal(1, nativeView.CheckCount);
        Assert.Equal(NativeHandle("original"), nativeView.LastCheckHandle);
        Assert.False(surface.TryResolveElementReference(
            reference,
            out _));
        Assert.False(surface.TryResolveElementReference(
            siblingReference,
            out _));

        var repeated = await surface.CheckWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.ElementReferenceStale,
            repeated.Error?.Code);
        Assert.Equal(1, nativeView.CheckCount);
    }

    [Fact]
    public async Task InvalidCheckReferencesAndPreDispatchCancellationStayNativeFree()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var forged = new BrowserElementReference("be_forged", document);

        var forgedResult = await surface.CheckWithinOriginAsync(
            forged,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var wrongDocument = new BrowserElementReference(
            "be_forged",
            new BrowserDocumentBinding(
                Address("https://example.test/other"),
                document.DocumentRevision));
        var wrongResult = await surface.CheckWithinOriginAsync(
            wrongDocument,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await surface.CheckWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(document.Address),
            cancellation.Token);

        Assert.Equal(
            BrowserErrorCode.ElementReferenceStale,
            forgedResult.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.NavigationStateChanged,
            wrongResult.Error?.Code);
        Assert.Equal(BrowserErrorCode.Cancelled, cancelled.Error?.Code);
        Assert.Equal(0, nativeView.CheckCount);
    }

    [Fact]
    public async Task CancellationAfterCheckCommitCannotOverwriteKnownResult()
    {
        var pendingCheck =
            new TaskCompletionSource<NativeBrowserCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
            PendingCheck = pendingCheck,
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var check = surface.CheckWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(document.Address),
            cancellation.Token).AsTask();
        cancellation.Cancel();
        await Task.Yield();

        Assert.False(check.IsCompleted);
        Assert.Equal(1, nativeView.CheckCount);
        pendingCheck.SetResult(NativeBrowserCheckResult.Checked());

        Assert.True((await check).IsSuccess);
    }

    [Fact]
    public async Task CheckSerializesAllBrowserMutations()
    {
        var pendingCheck =
            new TaskCompletionSource<NativeBrowserCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
            PendingCheck = pendingCheck,
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var reference = snapshot.Value!.Nodes[1].Reference!;
        var check = surface.CheckWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None).AsTask();

        var concurrentCheck = await surface.CheckWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var concurrentClick = await surface.ClickWithinOriginAsync(
            reference,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var concurrentFill = await surface.FillWithinOriginAsync(
            reference,
            "value",
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var concurrentSnapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var concurrentNavigation = await surface.NavigateAsync(
            Address("https://example.test/next"),
            CancellationToken.None);

        Assert.All(
            [
                concurrentCheck.Error,
                concurrentClick.Error,
                concurrentFill.Error,
                concurrentSnapshot.Error,
                concurrentNavigation.Error,
            ],
            error => Assert.Equal(
                BrowserErrorCode.NavigationInProgress,
                error?.Code));
        Assert.Equal(1, nativeView.CheckCount);
        Assert.Equal(0, nativeView.ClickCount);
        Assert.Equal(0, nativeView.FillCount);
        Assert.Equal(0, nativeView.NavigateCount);

        pendingCheck.SetResult(
            NativeBrowserCheckResult.NotCheckable());
        var result = await check;
        Assert.Equal(
            BrowserErrorCode.ElementNotCheckable,
            result.Error?.Code);
    }

    [Theory]
    [InlineData(
        2,
        BrowserErrorCode.ElementNotInteractable)]
    [InlineData(
        3,
        BrowserErrorCode.ElementNotCheckable)]
    [InlineData(
        1,
        BrowserErrorCode.ElementReferenceStale)]
    public async Task CheckMapsClosedPreCommitFailures(
        int nativeStatusValue,
        BrowserErrorCode expectedError)
    {
        var nativeStatus =
            (NativeBrowserCheckStatus)nativeStatusValue;
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
            CheckResult = nativeStatus switch
            {
                NativeBrowserCheckStatus.NotInteractable =>
                    NativeBrowserCheckResult.NotInteractable(),
                NativeBrowserCheckStatus.NotCheckable =>
                    NativeBrowserCheckResult.NotCheckable(),
                NativeBrowserCheckStatus.Stale =>
                    NativeBrowserCheckResult.Stale(),
                NativeBrowserCheckStatus.Unchecked =>
                    NativeBrowserCheckResult.Unchecked(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(nativeStatus)),
            },
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        var result = await surface.CheckWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.Equal(expectedError, result.Error?.Code);
    }

    [Fact]
    public async Task CheckBlocksSameOriginNavigationAndPreservesDocument()
    {
        var pendingCheck =
            new TaskCompletionSource<NativeBrowserCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
            PendingCheck = pendingCheck,
        };
        var surface = Surface(nativeView);
        var source = Address("https://example.test/start");
        _ = await surface.NavigateAsync(source, CancellationToken.None);
        nativeView.RaiseNavigationCompleted(source, isSuccess: true);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var destination = Address("https://example.test/next");
        var check = surface.CheckWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(destination),
            CancellationToken.None).AsTask();

        Assert.True(nativeView.RaiseNavigationStarted(destination));
        pendingCheck.SetResult(NativeBrowserCheckResult.Checked());
        var result = await check;

        Assert.True(result.IsSuccess);
        Assert.Equal(source, surface.State.Address);
        Assert.Equal(BrowserLoadState.Ready, surface.State.LoadState);
        Assert.Equal(1, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task CheckBlocksCrossOriginNavigationWithoutReplacingRenderer()
    {
        var pendingCheck =
            new TaskCompletionSource<NativeBrowserCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
            PendingCheck = pendingCheck,
        };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = SurfaceWithReplacement(nativeView, replacement);
        var source = Address("https://example.test/start");
        _ = await surface.NavigateAsync(source, CancellationToken.None);
        nativeView.RaiseNavigationCompleted(source, isSuccess: true);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var check = surface.CheckWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(source),
            CancellationToken.None).AsTask();

        Assert.True(nativeView.RaiseNavigationStarted(
            Address("https://other.test/escape")));
        pendingCheck.SetResult(NativeBrowserCheckResult.Checked());
        var result = await check;

        Assert.True(result.IsSuccess);
        Assert.Equal(source, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);
        Assert.Equal(0, replacement.NavigateCount);
    }

    [Fact]
    public async Task CheckedResultKeepsOriginGuardUntilUiObservationBarrier()
    {
        var pendingCheck =
            new TaskCompletionSource<NativeBrowserCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
            PendingCheck = pendingCheck,
        };
        var replacement = new RecordingEmbeddedBrowserView();
        var dispatcher = new QueuedBrowserUiDispatcher();
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            dispatcher,
            () => replacement,
            static _ => { },
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var source = Address("https://example.test/start");
        _ = await surface.NavigateAsync(source, CancellationToken.None);
        nativeView.RaiseNavigationCompleted(source, isSuccess: true);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var check = surface.CheckWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(source),
            CancellationToken.None).AsTask();

        pendingCheck.SetResult(NativeBrowserCheckResult.Checked());
        await dispatcher.WaitForWorkAsync().WaitAsync(
            TimeSpan.FromSeconds(1));

        Assert.False(check.IsCompleted);
        Assert.True(nativeView.RaiseNavigationStarted(
            Address("https://other.test/escape")));
        dispatcher.Drain();
        var result = await check.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(source, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task CheckDeadlineIsOutcomeUnknownAndFencesLateCompletion()
    {
        var pendingCheck =
            new TaskCompletionSource<NativeBrowserCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
            PendingCheck = pendingCheck,
        };
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            () => replacement,
            static _ => { },
            nativeSnapshotDeadline: TimeSpan.FromMilliseconds(25),
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        var result = await surface.CheckWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.InteractionOutcomeUnknown,
            result.Error?.Code);
        Assert.False(result.Error?.Retryable);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(0, surface.State.DocumentRevision);

        pendingCheck.SetResult(NativeBrowserCheckResult.Checked());
        await Task.Yield();
        await Task.Yield();

        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(0, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task CheckDeadlineWinsOverAnEarlierQueuedNativeResult()
    {
        var pendingCheck =
            new TaskCompletionSource<NativeBrowserCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
            PendingCheck = pendingCheck,
        };
        var replacement = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("replacement"),
        };
        var dispatcher = new QueuedBrowserUiDispatcher();
        var replacementCount = 0;
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            dispatcher,
            () =>
            {
                replacementCount++;
                return replacement;
            },
            static _ => { },
            nativeSnapshotDeadline: TimeSpan.FromMilliseconds(25),
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var check = surface.CheckWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None).AsTask();
        dispatcher.Suspend();

        pendingCheck.SetResult(NativeBrowserCheckResult.Checked());
        await dispatcher.WaitForWorkAsync().WaitAsync(
            TimeSpan.FromSeconds(1));
        var result = await check.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            BrowserErrorCode.InteractionOutcomeUnknown,
            result.Error?.Code);
        Assert.False(result.Error?.Retryable);
        Assert.Equal(0, replacementCount);
        Assert.Equal(0, surface.State.DocumentRevision);

        dispatcher.Drain();

        Assert.Equal(0, replacementCount);
        Assert.Equal(0, surface.State.DocumentRevision);
        var recovered = await surface.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(surface.State),
            CancellationToken.None);
        Assert.True(recovered.IsSuccess);
        Assert.Equal(0, replacement.SnapshotCount);
        Assert.Equal(2, nativeView.SnapshotCount);
    }

    [Fact]
    public async Task SynchronousCheckFailureWithoutReplacementBlocksFurtherUse()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
            ThrowOnCheck = true,
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        var check = await surface.CheckWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None);
        var navigation = await surface.NavigateAsync(
            Address("https://example.test/next"),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.InteractionOutcomeUnknown,
            check.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            navigation.Error?.Code);
        Assert.Equal(0, nativeView.NavigateCount);
    }

    [Fact]
    public async Task CheckDispatcherFailureReturnsUnknownAndPermanentlyFencesAdapter()
    {
        var pendingCheck =
            new TaskCompletionSource<NativeBrowserCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = CheckableSnapshot("original"),
            PendingCheck = pendingCheck,
        };
        var dispatcher = new FailingBrowserUiDispatcher();
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            dispatcher,
            () => new RecordingEmbeddedBrowserView(),
            static _ => { },
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var check = surface.CheckWithinOriginAsync(
            snapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(document.Address),
            CancellationToken.None).AsTask();

        dispatcher.FailMarshalling();
        pendingCheck.SetResult(NativeBrowserCheckResult.Checked());
        var result = await check.WaitAsync(TimeSpan.FromSeconds(1));
        dispatcher.RestoreAccess();
        var navigation = await surface.NavigateAsync(
            Address("https://example.test/next"),
            CancellationToken.None);

        Assert.Equal(
            BrowserErrorCode.InteractionOutcomeUnknown,
            result.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            navigation.Error?.Code);
        Assert.Equal(0, nativeView.NavigateCount);
    }

    [Fact]
    public async Task SnapshotRequiresAReadyMatchingDocument()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var stale = BrowserDocumentBinding.FromState(surface.State);
        _ = await surface.NavigateAsync(
            Address("https://example.test/loading"),
            CancellationToken.None);

        var result = await surface.CaptureSnapshotAsync(
            stale,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            result.Error?.Code);
        Assert.Equal(0, nativeView.SnapshotCount);
    }

    [Fact]
    public async Task SnapshotCancellationBeforeDispatchDoesNotInvokeNativeView()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await surface.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(surface.State),
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.Cancelled, result.Error?.Code);
        Assert.Equal(0, nativeView.SnapshotCount);
    }

    [Fact]
    public async Task CancelledSnapshotKeepsNativeInvocationBoundedUntilItDrains()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("0"),
            PendingSnapshot = new(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        using var cancellation = new CancellationTokenSource();
        var first = surface.CaptureSnapshotAsync(
            document,
            cancellation.Token).AsTask();

        cancellation.Cancel();
        var cancelled = await first;
        var overlapping = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);

        Assert.False(cancelled.IsSuccess);
        Assert.Equal(BrowserErrorCode.Cancelled, cancelled.Error?.Code);
        Assert.False(overlapping.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            overlapping.Error?.Code);
        Assert.Equal(1, nativeView.SnapshotCount);

        nativeView.PendingSnapshot.SetResult(
            nativeView.SnapshotResult);
        nativeView.PendingSnapshot = null;
        var next = await CaptureAfterNativeSnapshotDrainAsync(
            surface,
            document,
            CancellationToken.None);

        Assert.True(next.IsSuccess);
        Assert.Equal(2, nativeView.SnapshotCount);
    }

    [Fact]
    public async Task GovernedNavigationCannotOverlapANativeSnapshot()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            PendingSnapshot = new(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var snapshot = surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None).AsTask();

        var governed = await BeginGovernedNavigation(
            surface,
            Address("https://example.test/governed"),
            CancellationToken.None);

        Assert.False(governed.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            governed.Error?.Code);
        Assert.Equal(0, nativeView.NavigateCount);

        nativeView.PendingSnapshot.SetResult(nativeView.SnapshotResult);
        Assert.True((await snapshot).IsSuccess);
    }

    [Fact]
    public async Task TimedOutSnapshotReplacesNativeViewAndFencesLateCompletion()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            PendingSnapshot = new(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var replacement = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("replacement"),
        };
        var reentrantAddress =
            Address("https://example.test/reentrant");
        var capturedOldNavigationCompletion =
            nativeView.CaptureNavigationCompletedCallback(
                reentrantAddress,
                isSuccess: true,
                navigationGeneration: 1);
        var replacementCount = 0;
        var presentationCount = 0;
        var replacementPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BrowserResult<BrowserSessionState>? reentrantNavigation = null;
        BrowserResult<BrowserDocumentSnapshot>? reentrantSnapshot = null;
        BrowserSurface? surface = null;
        surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            () =>
            {
                replacementCount++;
                return replacement;
            },
            _ =>
            {
                presentationCount++;
                capturedOldNavigationCompletion();
                reentrantNavigation = surface!.NavigateAsync(
                        reentrantAddress,
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                reentrantSnapshot = surface!.CaptureSnapshotAsync(
                        BrowserDocumentBinding.FromState(surface.State),
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            },
            nativeSnapshotDeadline: TimeSpan.FromMilliseconds(25),
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        surface.StateChanged += (_, args) =>
        {
            if (args.State.DocumentRevision == 1)
            {
                replacementPublished.TrySetResult();
            }
        };
        var firstDocument =
            BrowserDocumentBinding.FromState(surface.State);

        var timedOut = await surface.CaptureSnapshotAsync(
            firstDocument,
            CancellationToken.None);
        await replacementPublished.Task.WaitAsync(
            TimeSpan.FromSeconds(1));

        Assert.False(timedOut.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            timedOut.Error?.Code);
        Assert.True(timedOut.Error?.Retryable);
        Assert.Equal(1, replacementCount);
        Assert.Equal(1, presentationCount);
        Assert.Equal(1, surface.State.DocumentRevision);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            reentrantNavigation?.Error?.Code);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            reentrantSnapshot?.Error?.Code);
        Assert.Equal(0, nativeView.NavigateCount);
        Assert.Equal(0, replacement.NavigateCount);

        var recovered = await surface.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(surface.State),
            CancellationToken.None);
        var recoveredReference =
            recovered.Value?.Nodes[1].Reference;

        Assert.True(recovered.IsSuccess);
        Assert.Equal(1, nativeView.SnapshotCount);
        Assert.Equal(1, replacement.SnapshotCount);
        Assert.NotNull(recoveredReference);
        Assert.True(surface.TryResolveElementReference(
            recoveredReference!,
            out var recoveredHandle));
        Assert.Equal(NativeHandle("replacement"), recoveredHandle);

        nativeView.PendingSnapshot.SetException(
            new InvalidOperationException(
                "late vendor failure must stay fenced"));
        await Task.Yield();
        await Task.Yield();

        Assert.Equal(1, surface.State.DocumentRevision);
        Assert.True(surface.TryResolveElementReference(
            recoveredReference,
            out recoveredHandle));
        Assert.Equal(NativeHandle("replacement"), recoveredHandle);
    }

    [Fact]
    public async Task DeadlineRemainsAuthoritativeWhileUiQuarantineIsQueued()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            PendingSnapshot = new(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var replacement = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("replacement"),
        };
        var dispatcher = new QueuedBrowserUiDispatcher();
        var replacementCount = 0;
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            dispatcher,
            () =>
            {
                replacementCount++;
                return replacement;
            },
            static _ => { },
            nativeSnapshotDeadline: TimeSpan.FromMilliseconds(25),
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var capture = surface.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(surface.State),
            CancellationToken.None).AsTask();
        dispatcher.Suspend();

        var timedOut = await capture.WaitAsync(
            TimeSpan.FromSeconds(1));
        await dispatcher.WaitForWorkAsync().WaitAsync(
            TimeSpan.FromSeconds(1));

        Assert.False(timedOut.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            timedOut.Error?.Code);
        Assert.True(timedOut.Error?.Retryable);
        Assert.Equal(0, replacementCount);
        Assert.Equal(0, surface.State.DocumentRevision);

        nativeView.PendingSnapshot.SetResult(nativeView.SnapshotResult);
        dispatcher.Drain();

        Assert.Equal(1, replacementCount);
        Assert.Equal(1, surface.State.DocumentRevision);
        var recovered = await surface.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(surface.State),
            CancellationToken.None);
        Assert.True(recovered.IsSuccess);
        Assert.Equal(1, replacement.SnapshotCount);
    }

    [Fact]
    public async Task FailedReplacementFencesNativeViewUntilSnapshotDrains()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = ActionableSnapshot("recovered"),
            PendingSnapshot = new(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var replacement = new RecordingEmbeddedBrowserView();
        var presentationCount = 0;
        var presentationAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            () => replacement,
            _ =>
            {
                presentationCount++;
                presentationAttempted.TrySetResult();
                throw new InvalidOperationException(
                    "the host rejected presentation");
            },
            nativeSnapshotDeadline: TimeSpan.FromMilliseconds(25),
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var document = BrowserDocumentBinding.FromState(surface.State);

        var timedOut = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        await presentationAttempted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        var blockedSnapshot = await surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None);
        var blockedNavigation = await surface.NavigateAsync(
            Address("https://example.test/fenced"),
            CancellationToken.None);

        Assert.False(timedOut.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            timedOut.Error?.Code);
        Assert.True(timedOut.Error?.Retryable);
        Assert.Equal(1, presentationCount);
        Assert.False(blockedSnapshot.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            blockedSnapshot.Error?.Code);
        Assert.False(blockedNavigation.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            blockedNavigation.Error?.Code);
        Assert.True(blockedNavigation.Error?.Retryable);
        Assert.Equal(0, nativeView.NavigateCount);
        Assert.Equal(1, nativeView.SnapshotCount);
        Assert.Equal(0, replacement.SnapshotCount);
        Assert.Equal(0, surface.State.DocumentRevision);

        nativeView.PendingSnapshot.SetResult(nativeView.SnapshotResult);
        nativeView.PendingSnapshot = null;
        var recovered = await CaptureAfterNativeSnapshotDrainAsync(
            surface,
            document,
            CancellationToken.None);

        Assert.True(recovered.IsSuccess);
        Assert.Equal(2, nativeView.SnapshotCount);
        Assert.Equal(0, replacement.SnapshotCount);
        Assert.Equal(0, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task DocumentChangeDuringSnapshotDiscardsLateNativeData()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            PendingSnapshot = new(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var surface = Surface(nativeView);
        var document = BrowserDocumentBinding.FromState(surface.State);
        var capture = surface.CaptureSnapshotAsync(
            document,
            CancellationToken.None).AsTask();
        var next = Address("https://example.test/next");
        _ = await surface.NavigateAsync(next, CancellationToken.None);
        nativeView.RaiseNavigationStarted(next);
        nativeView.RaiseNavigationCompleted(next, isSuccess: true);

        nativeView.PendingSnapshot.SetResult(
            ActionableSnapshot("0"));
        var result = await capture;

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationStateChanged,
            result.Error?.Code);
        Assert.Equal("browser_state_changed", result.Error?.StableCode);
    }

    [Fact]
    public async Task InvalidNativeSnapshotUsesOnlyStableSanitizedFailure()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SnapshotResult = NativeBrowserSnapshotResult.Invalid(),
        };
        var surface = Surface(nativeView);

        var result = await surface.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(surface.State),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.SnapshotInvalid,
            result.Error?.Code);
        Assert.Equal(
            "browser_snapshot_invalid",
            result.Error?.StableCode);
        Assert.DoesNotContain(
            "vendor",
            result.Error!.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NavigateQueuesTheValidatedAddressAndPublishesLoading()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var address = Address("https://example.test/path?q=one");
        var published = new List<BrowserSessionState>();
        surface.StateChanged += (_, args) => published.Add(args.State);

        var result = await surface.NavigateAsync(address, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(address, nativeView.NavigatedAddress);
        Assert.Equal(BrowserLoadState.Loading, surface.State.LoadState);
        Assert.Equal(address, surface.State.Address);
        Assert.Equal([surface.State], published);
    }

    [Fact]
    public void SuccessfulCompletionAdvancesTheDocumentRevisionAndHistoryState()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            CanGoBack = true,
            CanGoForward = false,
        };
        var surface = Surface(nativeView);
        var address = Address("https://example.test/complete");

        nativeView.RaiseNavigationStarted(address);
        nativeView.RaiseNavigationCompleted(address, isSuccess: true);

        Assert.Equal(BrowserLoadState.Ready, surface.State.LoadState);
        Assert.Equal(address, surface.State.Address);
        Assert.True(surface.State.CanGoBack);
        Assert.False(surface.State.CanGoForward);
        Assert.Equal(1, surface.State.DocumentRevision);
        Assert.Null(surface.State.Failure);
    }

    [Fact]
    public void SameDocumentAddressChangeUpdatesChromeWithoutAdvancingRevision()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var document = Address("https://example.test/watch?v=one");
        var nextRoute = Address("https://example.test/watch?v=two");

        nativeView.RaiseNavigationStarted(document);
        nativeView.RaiseNavigationCompleted(document, isSuccess: true);
        var documentRevision = surface.State.DocumentRevision;
        nativeView.CanGoBack = true;
        nativeView.CanGoForward = false;

        nativeView.RaiseAddressChanged(nextRoute);

        Assert.Equal(nextRoute, surface.State.Address);
        Assert.Equal(BrowserLoadState.Ready, surface.State.LoadState);
        Assert.Equal(documentRevision, surface.State.DocumentRevision);
        Assert.True(surface.State.CanGoBack);
        Assert.False(surface.State.CanGoForward);
        Assert.Null(surface.State.Failure);
    }

    [Fact]
    public void FailedCompletionPublishesOnlyAStableEngineNeutralFailure()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var address = Address("https://example.test/failure");

        nativeView.RaiseNavigationStarted(address);
        nativeView.RaiseNavigationCompleted(address, isSuccess: false);

        Assert.Equal(BrowserLoadState.Failed, surface.State.LoadState);
        Assert.Equal(BrowserErrorCode.NavigationFailed, surface.State.Failure?.Code);
        Assert.Equal("navigation_failed", surface.State.Failure?.StableCode);
        Assert.DoesNotContain("vendor", surface.State.Failure?.Message);
        Assert.Equal(0, surface.State.DocumentRevision);
    }

    [Theory]
    [InlineData(
        (int)NativeBrowserLoadFailureKind.NetworkUnavailable,
        BrowserErrorCode.NetworkUnavailable,
        "network_unavailable")]
    [InlineData(
        (int)NativeBrowserLoadFailureKind.TimedOut,
        BrowserErrorCode.NavigationTimedOut,
        "navigation_timed_out")]
    [InlineData(
        (int)NativeBrowserLoadFailureKind.CertificateRejected,
        BrowserErrorCode.CertificateRejected,
        "certificate_rejected")]
    public void NavigationFailuresKeepTheirProductMeaning(
        int failureKind,
        BrowserErrorCode errorCode,
        string stableCode)
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var address = Address("https://example.test/distinct-failure");

        nativeView.RaiseNavigationStarted(address);
        nativeView.RaiseNavigationCompleted(
            address,
            isSuccess: false,
            failureKind: (NativeBrowserLoadFailureKind)failureKind);

        Assert.Equal(errorCode, surface.State.Failure?.Code);
        Assert.Equal(stableCode, surface.State.Failure?.StableCode);
    }

    [Fact]
    public void ProductEventsAndFindCommandsCrossOnlyTheEngineNeutralBoundary()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        BrowserProductEvent? observed = null;
        surface.ProductEvent += (_, productEvent) => observed = productEvent;
        var productEvent = new BrowserProductEvent.PermissionDenied(
            "https://example.test",
            BrowserPermissionKind.Camera);

        nativeView.RaiseProductEvent(productEvent);

        Assert.Same(productEvent, observed);
        Assert.True(surface.StartFind("needle"));
        Assert.Equal("needle", nativeView.LastFindText);
        Assert.True(surface.FindNext(BrowserFindDirection.Previous));
        Assert.Equal(BrowserFindDirection.Previous, nativeView.LastFindDirection);
        Assert.True(surface.StopFind());
        Assert.Equal(1, nativeView.StopFindCount);
    }

    [Fact]
    public void RejectedTopLevelNavigationKeepsTheLastSupportedAddress()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);

        nativeView.RaiseNavigationRejected();

        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(BrowserLoadState.Failed, surface.State.LoadState);
        Assert.Equal(BrowserErrorCode.NavigationFailed, surface.State.Failure?.Code);
    }

    [Fact]
    public async Task MissingHistoryReturnsAnExpectedFailureWithoutChangingState()
    {
        var surface = Surface(new RecordingEmbeddedBrowserView());
        var initial = surface.State;

        var result = await surface.GoBackAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.HistoryUnavailable, result.Error?.Code);
        Assert.Same(initial, surface.State);
    }

    [Fact]
    public async Task AcceptedBackAndForwardOperationsEnterLoadingState()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            AcceptBack = true,
            AcceptForward = true,
        };
        var surface = Surface(nativeView);

        var back = await surface.GoBackAsync(CancellationToken.None);
        nativeView.RaiseNavigationCompleted(
            Address("https://example.test/back"),
            isSuccess: true);
        var forward = await surface.GoForwardAsync(CancellationToken.None);

        Assert.True(back.IsSuccess);
        Assert.True(forward.IsSuccess);
        Assert.Equal(BrowserLoadState.Loading, surface.State.LoadState);
        Assert.Equal(1, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task ReloadAndStopPublishLoadingThenReadyWithoutAdvancingRevision()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            AcceptReload = true,
            AcceptStop = true,
        };
        var surface = Surface(nativeView);

        var reload = await surface.ReloadAsync(CancellationToken.None);
        var stop = await surface.StopAsync(CancellationToken.None);
        nativeView.RaiseNavigationCompleted(
            Address("https://example.test/stopped"),
            isSuccess: false,
            wasStopped: true);

        Assert.True(reload.IsSuccess);
        Assert.True(stop.IsSuccess);
        Assert.Equal(BrowserLoadState.Ready, surface.State.LoadState);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(0, surface.State.DocumentRevision);
        Assert.Null(surface.State.Failure);
    }

    [Fact]
    public async Task VendorExceptionMapsToAStableFailureAndDoesNotEscape()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            ThrowOnNavigate = true,
        };
        var surface = Surface(nativeView);

        var result = await surface.NavigateAsync(
            Address("https://example.test"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.EngineFailed, result.Error?.Code);
        Assert.Equal("engine_failed", result.Error?.StableCode);
        Assert.Equal(BrowserLoadState.Failed, surface.State.LoadState);
        Assert.DoesNotContain("vendor", surface.State.Failure?.Message);
    }

    [Fact]
    public async Task VendorExceptionReplacesTheFailedNativeViewOnTheNextUiTurn()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            ThrowOnReload = true,
        };
        var replacement = new RecordingEmbeddedBrowserView
        {
            AcceptReload = true,
        };
        var dispatcher = new QueuedBrowserUiDispatcher();
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            dispatcher,
            () => replacement);
        BrowserProductEvent? observed = null;
        surface.ProductEvent += (_, productEvent) => observed = productEvent;

        var failed = await surface.ReloadAsync(CancellationToken.None);

        Assert.False(failed.IsSuccess);
        Assert.Equal(BrowserErrorCode.EngineFailed, failed.Error?.Code);
        Assert.Equal(BrowserLoadState.Failed, surface.State.LoadState);
        Assert.False(nativeView.IsDisposed);
        await dispatcher.WaitForWorkAsync();

        dispatcher.Drain();

        Assert.True(nativeView.IsDisposed);
        Assert.Same(replacement.View, surface.Content);
        Assert.Equal(BrowserLoadState.Ready, surface.State.LoadState);
        Assert.IsType<BrowserProductEvent.RendererRecovered>(observed);

        var retried = await surface.ReloadAsync(CancellationToken.None);

        Assert.True(retried.IsSuccess);
        Assert.Equal(1, replacement.ReloadCount);
    }

    [Fact]
    public async Task CancellationDoesNotReachTheNativeRenderer()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await surface.NavigateAsync(
            Address("https://example.test"),
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.Cancelled, result.Error?.Code);
        Assert.Equal(0, nativeView.NavigateCount);
        Assert.Equal(BrowserSessionState.Initial(BrowserAddress.Blank), surface.State);
    }

    /// <summary>
    /// A loaded page keeps navigating things inside itself, and those starts are
    /// indistinguishable from the page's own: the platform's events name a
    /// request and never say which frame asked. So a Google tab announced itself
    /// as an ogs.google.com widget and stayed that way, because that widget was
    /// the last frame to start and nothing completed after it — and the shell
    /// then saved that address and reopened it on the next run.
    /// </summary>
    [Fact]
    public async Task AFrameNavigatingInsideALoadedPageDoesNotRenameIt()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var page = Address("https://www.google.com/");
        _ = await surface.NavigateAsync(page, CancellationToken.None);
        nativeView.RaiseNavigationCompleted(page, isSuccess: true);
        Assert.Equal(page, surface.State.Address);

        Assert.False(nativeView.RaiseNavigationStarted(
            Address("https://ogs.google.com/widget/app/so?eom=1")));

        Assert.Equal(page, surface.State.Address);
    }

    [Fact]
    public async Task GovernedNavigationWaitsThroughSameOriginRedirects()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var requested = Address("https://example.test/start");
        var redirected = Address("https://example.test/final?from=redirect");
        var operation = BeginGovernedNavigation(
            surface,
            requested,
            CancellationToken.None);

        Assert.False(operation.IsCompleted);
        Assert.Equal(BrowserLoadState.Loading, surface.State.LoadState);
        Assert.False(nativeView.RaiseNavigationStarted(requested));
        Assert.False(nativeView.RaiseNavigationStarted(redirected));
        Assert.Equal(redirected, surface.State.Address);

        nativeView.RaiseNavigationCompleted(redirected, isSuccess: true);
        var result = await operation;

        Assert.True(result.IsSuccess);
        Assert.Equal(BrowserLoadState.Ready, result.Value?.LoadState);
        Assert.Equal(redirected, result.Value?.Address);
        Assert.Equal(1, result.Value?.DocumentRevision);
    }

    [Fact]
    public async Task GovernedNavigationRejectsResolvedPrivateTargetBeforeDispatch()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var policy = BrowserDestinationPolicy.CreateLocal(
            static (_, _) => ValueTask.FromResult<IPAddress[]>(
                [IPAddress.Parse("10.0.0.1")]));
        var surface = new BrowserSurface(
            nativeView,
            policy,
            InlineBrowserUiDispatcher.Instance,
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var requested = Address("https://private.example.test/start");

        var result = await surface.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Navigate(requested),
            BrowserNavigationOrigin.FromAddress(requested),
            BrowserNavigationStartBinding.FromState(surface.State),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            result.Error?.Code);
        Assert.Equal(0, nativeView.NavigateCount);
    }

    [Fact(DisplayName = "content.browser.cef-peer-binding.fail-closed")]
    [Trait("SecurityCampaignCase", "content.browser.cef-peer-binding.fail-closed")]
    public async Task GovernedNavigationFailsBeforeTransportWithoutPeerBinding()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SupportsPeerBoundTransport = false,
        };
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var requested = Address("https://public.example.test/start");

        var result = await surface.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Navigate(requested),
            BrowserNavigationOrigin.FromAddress(requested),
            BrowserNavigationStartBinding.FromState(surface.State),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.NavigationPolicyDenied, result.Error?.Code);
        Assert.Contains("connected peer", result.Error?.Message, StringComparison.Ordinal);
        Assert.Equal(0, nativeView.NavigateCount);
    }

    [Fact]
    public async Task GovernedElementMutationsFailBeforeNativeDispatchWithoutPeerBinding()
    {
        var clickNative = new RecordingEmbeddedBrowserView
        {
            SupportsPeerBoundTransport = false,
            SnapshotResult = ActionableSnapshot("click"),
        };
        var clickSurface = Surface(clickNative);
        var clickDocument = BrowserDocumentBinding.FromState(clickSurface.State);
        var clickSnapshot = await clickSurface.CaptureSnapshotAsync(
            clickDocument,
            CancellationToken.None);
        var click = await clickSurface.ClickWithinOriginAsync(
            clickSnapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(clickDocument.Address),
            CancellationToken.None);

        var fillNative = new RecordingEmbeddedBrowserView
        {
            SupportsPeerBoundTransport = false,
            SnapshotResult = ActionableSnapshot("fill"),
        };
        var fillSurface = Surface(fillNative);
        var fillDocument = BrowserDocumentBinding.FromState(fillSurface.State);
        var fillSnapshot = await fillSurface.CaptureSnapshotAsync(
            fillDocument,
            CancellationToken.None);
        var fill = await fillSurface.FillWithinOriginAsync(
            fillSnapshot.Value!.Nodes[1].Reference!,
            "safe text",
            BrowserNavigationOrigin.FromAddress(fillDocument.Address),
            CancellationToken.None);

        var checkNative = new RecordingEmbeddedBrowserView
        {
            SupportsPeerBoundTransport = false,
            SnapshotResult = ActionableSnapshot("check"),
        };
        var checkSurface = Surface(checkNative);
        var checkDocument = BrowserDocumentBinding.FromState(checkSurface.State);
        var checkSnapshot = await checkSurface.CaptureSnapshotAsync(
            checkDocument,
            CancellationToken.None);
        var check = await checkSurface.CheckWithinOriginAsync(
            checkSnapshot.Value!.Nodes[1].Reference!,
            BrowserNavigationOrigin.FromAddress(checkDocument.Address),
            CancellationToken.None);

        Assert.All(
            [click.Error, fill.Error, check.Error],
            error => Assert.Equal(BrowserErrorCode.NavigationPolicyDenied, error?.Code));
        Assert.Equal(0, clickNative.ClickCount);
        Assert.Equal(0, fillNative.FillCount);
        Assert.Equal(0, checkNative.CheckCount);
    }

    [Fact]
    public async Task ManualNavigationAndLoadedContentObservationRemainAvailableWithoutPeerBinding()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            SupportsPeerBoundTransport = false,
        };
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var requested = Address("https://manual.example.test/start");

        var manual = await surface.NavigateAsync(
            requested,
            CancellationToken.None);
        Assert.True(manual.IsSuccess);
        Assert.False(nativeView.RaiseNavigationStarted(requested));
        nativeView.RaiseNavigationCompleted(requested, isSuccess: true);

        var snapshot = await surface.CaptureSnapshotAsync(
            BrowserDocumentBinding.FromState(surface.State),
            CancellationToken.None);
        var governed = await surface.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Reload(),
            BrowserNavigationOrigin.FromAddress(requested),
            BrowserNavigationStartBinding.FromState(surface.State),
            CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(1, nativeView.SnapshotCount);
        Assert.False(governed.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            governed.Error?.Code);
        Assert.Equal(1, nativeView.NavigateCount);
        Assert.Equal(0, nativeView.ReloadCount);
    }

    [Fact]
    public async Task GovernedNavigationRejectsOriginBeforeResolvingItsHost()
    {
        var resolutionCount = 0;
        var policy = BrowserDestinationPolicy.CreateLocal(
            (_, _) =>
            {
                resolutionCount++;
                return ValueTask.FromResult<IPAddress[]>(
                    [IPAddress.Parse("93.184.216.34")]);
            });
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = new BrowserSurface(
            nativeView,
            policy,
            InlineBrowserUiDispatcher.Instance,
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var requested = Address("https://outside.example.test/start");
        var approved = Address("https://approved.example.test/start");

        var result = await surface.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Navigate(requested),
            BrowserNavigationOrigin.FromAddress(approved),
            BrowserNavigationStartBinding.FromState(surface.State),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, resolutionCount);
        Assert.Equal(0, nativeView.NavigateCount);
    }

    [Fact]
    public async Task GovernedRequestGateRechecksEverySameOriginRedirectLeg()
    {
        var answers = new Queue<IPAddress[]>();
        answers.Enqueue([IPAddress.Parse("93.184.216.34")]);
        answers.Enqueue([IPAddress.Parse("93.184.216.34")]);
        answers.Enqueue([IPAddress.Parse("10.0.0.1")]);
        var policy = BrowserDestinationPolicy.CreateLocal(
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(answers.Dequeue());
            });
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = new BrowserSurface(
            nativeView,
            policy,
            InlineBrowserUiDispatcher.Instance,
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var requested = Address("https://rebind.example.test/start");
        var redirected = Address("https://rebind.example.test/final");
        using var cancellation = new CancellationTokenSource();
        var operation = surface.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Navigate(requested),
            BrowserNavigationOrigin.FromAddress(requested),
            BrowserNavigationStartBinding.FromState(surface.State),
            cancellation.Token).AsTask();

        Assert.False(nativeView.RaiseNavigationStarted(requested));
        Assert.True(await nativeView.AllowsActiveNavigationRequestAsync(requested));
        Assert.False(nativeView.RaiseNavigationStarted(redirected));
        Assert.False(await nativeView.AllowsActiveNavigationRequestAsync(redirected));

        cancellation.Cancel();
        var result = await operation;
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task UnrestrictedGovernedNavigationAllowsCrossOriginRedirects()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var requested = Address("https://example.test/start");
        var redirected = Address("https://outside.example.test/final");
        var operation = surface.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Navigate(requested),
            BrowserNavigationOrigin.Unrestricted,
            BrowserNavigationStartBinding.FromState(surface.State),
            CancellationToken.None).AsTask();

        Assert.False(nativeView.RaiseNavigationStarted(requested));
        Assert.False(nativeView.RaiseNavigationStarted(redirected));
        nativeView.RaiseNavigationCompleted(redirected, isSuccess: true);

        var result = await operation;

        Assert.True(result.IsSuccess);
        Assert.Equal(redirected, result.Value?.Address);
        Assert.Equal(BrowserLoadState.Ready, result.Value?.LoadState);
    }

    [Fact]
    public async Task GovernedNavigationRejectsCrossOriginRedirectAndLateCompletion()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var requested = Address("https://example.test/start");
        var escaped = Address("https://outside.example.test/redirect");
        var operation = BeginGovernedNavigation(
            surface,
            requested,
            CancellationToken.None);

        Assert.False(nativeView.RaiseNavigationStarted(requested));
        Assert.True(nativeView.RaiseNavigationStarted(escaped));
        var result = await operation;

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            result.Error?.Code);
        Assert.Equal(
            "browser_domain_policy_denied",
            result.Error?.StableCode);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(0, surface.State.DocumentRevision);
        Assert.Equal(result.Error, surface.State.Failure);

        nativeView.RaiseNavigationCompleted(escaped, isSuccess: true);

        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(0, surface.State.DocumentRevision);
        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            surface.State.Failure?.Code);
        var blockedWithoutReplacement = await surface.NavigateAsync(
            Address("https://human.example.test/page"),
            CancellationToken.None);
        Assert.False(blockedWithoutReplacement.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            blockedWithoutReplacement.Error?.Code);
    }

    [Fact]
    public async Task FinalOriginEscapeWithoutStartEventQuarantinesNativeView()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = SurfaceWithReplacement(
            nativeView,
            replacement);
        var requested = Address("https://example.test/start");
        var escaped = Address("https://outside.example.test/final");
        var governed = BeginGovernedNavigation(
            surface,
            requested,
            CancellationToken.None);

        nativeView.RaiseNavigationCompleted(
            escaped,
            isSuccess: true);
        var result = await governed;

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            result.Error?.Code);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);

        var next = Address("https://human.example.test/page");
        var accepted = await surface.NavigateAsync(
            next,
            CancellationToken.None);
        replacement.RaiseNavigationStarted(next);
        replacement.RaiseNavigationCompleted(next, isSuccess: true);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(next, surface.State.Address);
        Assert.Equal(2, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task CancellationDuringFinalEscapeResetCannotRegressReplacement()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = SurfaceWithReplacement(
            nativeView,
            replacement);
        var requested = Address("https://example.test/start");
        var escaped = Address("https://outside.example.test/final");
        using var cancellation = new CancellationTokenSource();
        surface.StateChanged += (_, args) =>
        {
            if (args.State.Address == BrowserAddress.Blank
                && args.State.DocumentRevision == 1)
            {
                cancellation.Cancel();
            }
        };
        var governed = BeginGovernedNavigation(
            surface,
            requested,
            cancellation.Token);

        nativeView.RaiseNavigationCompleted(
            escaped,
            isSuccess: true);
        var result = await governed;

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            result.Error?.Code);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);

        var next = Address("https://human.example.test/page");
        var accepted = await surface.NavigateAsync(
            next,
            CancellationToken.None);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(1, replacement.NavigateCount);
    }

    [Fact]
    public async Task RejectedAttemptDrainsBeforeLaterHumanNavigation()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = SurfaceWithReplacement(
            nativeView,
            replacement);
        var requested = Address("https://example.test/start");
        var escaped = Address("https://outside.example.test/redirect");
        var governed = BeginGovernedNavigation(
            surface,
            requested,
            CancellationToken.None);
        nativeView.RaiseNavigationStarted(requested);
        nativeView.RaiseNavigationStarted(escaped);
        _ = await governed;

        var humanAddress = Address("https://human.example.test/page");
        var blockedWhileDraining = await surface.NavigateAsync(
            humanAddress,
            CancellationToken.None);
        Assert.False(blockedWhileDraining.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            blockedWhileDraining.Error?.Code);

        nativeView.RaiseNavigationCompleted(escaped, isSuccess: false);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);
        var human = await surface.NavigateAsync(
            humanAddress,
            CancellationToken.None);
        replacement.RaiseNavigationStarted(humanAddress);
        replacement.RaiseNavigationCompleted(
            humanAddress,
            isSuccess: true);

        Assert.True(human.IsSuccess);
        Assert.Equal(humanAddress, surface.State.Address);
        Assert.Equal(BrowserLoadState.Ready, surface.State.LoadState);
        Assert.Equal(2, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task CancellationBeforeFirstStartKeepsDelayedNavigationContained()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            AcceptStop = true,
        };
        var surface = Surface(nativeView);
        var requested = Address("https://example.test/start");
        var escaped = Address("https://outside.example.test/redirect");
        using var cancellation = new CancellationTokenSource();
        var governed = BeginGovernedNavigation(
            surface,
            requested,
            cancellation.Token);

        cancellation.Cancel();
        var cancelled = await governed;

        Assert.False(cancelled.IsSuccess);
        Assert.Equal(BrowserErrorCode.Cancelled, cancelled.Error?.Code);
        Assert.True(nativeView.RaiseNavigationStarted(escaped));

        var blocked = await surface.NavigateAsync(
            Address("https://human.example.test/page"),
            CancellationToken.None);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            blocked.Error?.Code);

        nativeView.RaiseNavigationCompleted(escaped, isSuccess: false);

        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(0, surface.State.DocumentRevision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LateCompletionCannotBeAttributedToANewerNavigation(
        bool reuseRejectedAddress)
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = SurfaceWithReplacement(
            nativeView,
            replacement);
        var requested = Address("https://example.test/start");
        var escaped = Address("https://outside.example.test/redirect");
        var governed = BeginGovernedNavigation(
            surface,
            requested,
            CancellationToken.None);
        nativeView.RaiseNavigationStarted(requested);
        nativeView.RaiseNavigationStarted(escaped);
        _ = await governed;
        var rejectedGeneration =
            nativeView.LastNavigationGeneration;
        var capturedLateCompletion =
            nativeView.CaptureNavigationCompletedCallback(
                escaped,
                isSuccess: true,
                navigationGeneration: rejectedGeneration);
        var next = reuseRejectedAddress
            ? escaped
            : Address("https://human.example.test/page");

        var blocked = await surface.NavigateAsync(next, CancellationToken.None);

        Assert.False(blocked.IsSuccess);
        Assert.Equal(1, nativeView.NavigateCount);
        nativeView.RaiseNavigationCompleted(escaped, isSuccess: false);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);

        var accepted = await surface.NavigateAsync(next, CancellationToken.None);
        capturedLateCompletion();

        Assert.Equal(next, surface.State.Address);
        Assert.Equal(BrowserLoadState.Loading, surface.State.LoadState);
        Assert.Equal(1, surface.State.DocumentRevision);

        replacement.RaiseNavigationStarted(next);
        replacement.RaiseNavigationCompleted(next, isSuccess: true);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(next, surface.State.Address);
        Assert.Equal(2, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task CapturedOldCallbackDuringPresentationCannotReenterReplacement()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var replacement = new RecordingEmbeddedBrowserView();
        Action? capturedCompletion = null;
        var presentationCount = 0;
        var replacementCount = 0;
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            () =>
            {
                replacementCount++;
                return replacement;
            },
            _ =>
            {
                presentationCount++;
                var callback = capturedCompletion;
                capturedCompletion = null;
                callback?.Invoke();
            },
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);
        var requested = Address("https://example.test/start");
        var escaped = Address("https://outside.example.test/redirect");
        var governed = BeginGovernedNavigation(
            surface,
            requested,
            CancellationToken.None);
        nativeView.RaiseNavigationStarted(requested);
        nativeView.RaiseNavigationStarted(escaped);
        _ = await governed;
        capturedCompletion =
            nativeView.CaptureNavigationCompletedCallback(
                escaped,
                isSuccess: false,
                navigationGeneration:
                    nativeView.LastNavigationGeneration);

        nativeView.RaiseNavigationCompleted(
            escaped,
            isSuccess: false);

        Assert.Equal(1, replacementCount);
        Assert.Equal(1, presentationCount);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(1, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task UnsupportedSchemeRedirectIsAnOriginPolicyDenial()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var requested = Address("https://example.test/start");
        var governed = BeginGovernedNavigation(
            surface,
            requested,
            CancellationToken.None);
        nativeView.RaiseNavigationStarted(requested);

        nativeView.RaiseNavigationRejected(
            NativeBrowserNavigationRejectionReason.UnsupportedAddress);
        var result = await governed;

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            result.Error?.Code);
        Assert.Equal(
            "browser_domain_policy_denied",
            result.Error?.StableCode);

        nativeView.RaiseNavigationCompleted(address: null, isSuccess: false);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(0, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task GovernedNavigationRejectsAStaleStartingDocumentBinding()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var staleBinding = BrowserNavigationStartBinding.FromState(
            surface.State);
        var committed = Address("https://example.test/committed");
        _ = await surface.NavigateAsync(committed, CancellationToken.None);
        nativeView.RaiseNavigationStarted(committed);
        nativeView.RaiseNavigationCompleted(committed, isSuccess: true);
        nativeView.AcceptReload = true;

        var result = await surface.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Reload(),
            BrowserNavigationOrigin.FromAddress(committed),
            staleBinding,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationStateChanged,
            result.Error?.Code);
        Assert.Equal("browser_state_changed", result.Error?.StableCode);
        Assert.True(result.Error?.Retryable);
        Assert.Equal(0, nativeView.ReloadCount);
    }

    [Fact]
    public async Task GovernedNavigationFailsClosedWhileAnotherLoadIsActive()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var first = Address("https://example.test/first");
        var second = Address("https://example.test/second");
        _ = await surface.NavigateAsync(first, CancellationToken.None);

        var result = await surface.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Navigate(second),
            BrowserNavigationOrigin.FromAddress(second),
            BrowserNavigationStartBinding.FromState(surface.State),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationInProgress,
            result.Error?.Code);
        Assert.Equal(1, nativeView.NavigateCount);
        Assert.Equal(first, surface.State.Address);
    }

    [Theory]
    [InlineData("back")]
    [InlineData("forward")]
    [InlineData("reload")]
    public async Task GovernedHistoryAndReloadCannotEscapeTheCommittedOrigin(
        string operation)
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            AcceptBack = true,
            AcceptForward = true,
            AcceptReload = true,
        };
        var surface = Surface(nativeView);
        BrowserOriginConstrainedNavigationRequest request = operation switch
        {
            "back" => new BrowserOriginConstrainedNavigationRequest.Back(),
            "forward" => new BrowserOriginConstrainedNavigationRequest.Forward(),
            "reload" => new BrowserOriginConstrainedNavigationRequest.Reload(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var governed = surface.NavigateWithinOriginAsync(
            request,
            BrowserNavigationOrigin.FromAddress(BrowserAddress.Blank),
            BrowserNavigationStartBinding.FromState(surface.State),
            CancellationToken.None).AsTask();

        Assert.True(nativeView.RaiseNavigationStarted(
            Address("https://escaped.example.test/")));
        var result = await governed;

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.NavigationPolicyDenied,
            result.Error?.Code);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(0, surface.State.DocumentRevision);
    }

    [Fact]
    public async Task CancellingGovernedNavigationStopsAndPreservesCommittedState()
    {
        var nativeView = new RecordingEmbeddedBrowserView
        {
            AcceptStop = true,
        };
        var surface = Surface(nativeView);
        var requested = Address("https://example.test/start");
        using var cancellation = new CancellationTokenSource();
        var operation = BeginGovernedNavigation(
            surface,
            requested,
            cancellation.Token);
        nativeView.RaiseNavigationStarted(requested);

        cancellation.Cancel();
        var result = await operation;

        Assert.False(result.IsSuccess);
        Assert.Equal(BrowserErrorCode.Cancelled, result.Error?.Code);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(0, surface.State.DocumentRevision);
        Assert.Equal(1, nativeView.StopCount);

        nativeView.RaiseNavigationCompleted(requested, isSuccess: true);
        Assert.Equal(BrowserAddress.Blank, surface.State.Address);
        Assert.Equal(0, surface.State.DocumentRevision);
    }

    [Fact]
    public void DisposeReleasesTheEmbeddedBrowserExactlyOnce()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var networkLifetime = new CountingDisposable();
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            networkLifetime: networkLifetime);

        surface.Dispose();
        surface.Dispose();

        Assert.True(nativeView.IsDisposed);
        Assert.Equal(1, networkLifetime.DisposeCount);
        Assert.Null(surface.Content);
    }

    [Fact]
    public async Task DisposeSettlesAnInFlightGovernedNavigation()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var surface = Surface(nativeView);
        var requested = Address("https://example.test/disposed");
        var navigation = BeginGovernedNavigation(
            surface,
            requested,
            CancellationToken.None);
        nativeView.RaiseNavigationStarted(requested);

        surface.Dispose();
        var result = await navigation.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(result.IsSuccess);
        Assert.Equal(
            BrowserErrorCode.RendererUnavailable,
            result.Error?.Code);
        Assert.True(nativeView.IsDisposed);
    }

    [Fact]
    public void RendererProcessFailureReplacesAndDisposesTheFrozenView()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var replacement = new RecordingEmbeddedBrowserView();
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            () => replacement);
        var address = Address("https://example.test/volatile-form");
        BrowserProductEvent? observed = null;
        surface.ProductEvent += (_, productEvent) => observed = productEvent;
        nativeView.RaiseNavigationStarted(address);
        nativeView.RaiseNavigationCompleted(address, isSuccess: true);

        nativeView.RaiseRenderProcessFailed();

        Assert.True(nativeView.IsDisposed);
        Assert.False(replacement.IsDisposed);
        Assert.Same(replacement.View, surface.Content);
        Assert.Equal(2, surface.State.DocumentRevision);
        Assert.Equal(BrowserLoadState.Ready, surface.State.LoadState);
        var recovered = Assert.IsType<BrowserProductEvent.RendererRecovered>(observed);
        Assert.Equal(address, recovered.LostAddress);

        nativeView.RaiseProductEvent(
            new BrowserProductEvent.DownloadCancelled(1));
        Assert.Same(recovered, observed);

        replacement.RaiseProductEvent(
            new BrowserProductEvent.DownloadCancelled(2));
        Assert.IsType<BrowserProductEvent.DownloadCancelled>(observed);
    }

    [Fact]
    public async Task RendererProcessFailureWaitsUntilTheNativeCallbackReturns()
    {
        var nativeView = new RecordingEmbeddedBrowserView();
        var replacement = new RecordingEmbeddedBrowserView();
        var dispatcher = new QueuedBrowserUiDispatcher();
        var surface = new BrowserSurface(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            dispatcher,
            () => replacement);
        BrowserProductEvent? observed = null;
        surface.ProductEvent += (_, productEvent) => observed = productEvent;

        nativeView.RaiseRenderProcessFailed();

        Assert.False(nativeView.IsDisposed);
        Assert.Null(observed);
        await dispatcher.WaitForWorkAsync();

        dispatcher.Drain();

        Assert.True(nativeView.IsDisposed);
        Assert.Same(replacement.View, surface.Content);
        Assert.IsType<BrowserProductEvent.RendererRecovered>(observed);
    }

    [Fact]
    public void PublicApiDoesNotExposeTheVendorWebView()
    {
        var exportedTypes = typeof(BrowserSurface).Assembly.GetExportedTypes();
        var publicSignatures = exportedTypes
            .SelectMany(type => type.GetMembers())
            .Select(member => member.ToString() ?? string.Empty);

        Assert.DoesNotContain(
            publicSignatures,
            signature => signature.Contains(
                "Exclr8Cef",
                StringComparison.Ordinal));
    }

    private static BrowserAddress Address(string value)
    {
        Assert.True(BrowserAddress.TryParse(value, out var address));
        return address;
    }

    private static NativeBrowserSnapshotResult ActionableSnapshot(
        string token) =>
        NativeBrowserSnapshotResult.Success(
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
                        NativeHandle(token)),
                ],
                IsTruncated: false));

    private static NativeBrowserSnapshotResult FillableSnapshot(
        string token) =>
        NativeBrowserSnapshotResult.Success(
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
                        "textbox",
                        "Name",
                        BrowserSnapshotNodeState.None,
                        NativeHandle(token)),
                ],
                IsTruncated: false));

    private static NativeBrowserSnapshotResult CheckableSnapshot(
        string token) =>
        NativeBrowserSnapshotResult.Success(
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
                        "checkbox",
                        "Remember me",
                        BrowserSnapshotNodeState.None,
                        NativeHandle(token)),
                    new NativeBrowserSnapshotNode(
                        1,
                        "radio",
                        "Daily",
                        BrowserSnapshotNodeState.None,
                        NativeHandle(string.Concat(token, "_peer"))),
                ],
                IsTruncated: false));

    private static NativeBrowserElementHandle NativeHandle(
        string token) =>
        new(
            "snapshot_test",
            token.Replace('.', '_'),
            0);

    private static Task<BrowserResult<BrowserSessionState>>
        BeginGovernedNavigation(
            BrowserSurface surface,
            BrowserAddress address,
            CancellationToken cancellationToken) =>
        surface.NavigateWithinOriginAsync(
            new BrowserOriginConstrainedNavigationRequest.Navigate(address),
            BrowserNavigationOrigin.FromAddress(address),
            BrowserNavigationStartBinding.FromState(surface.State),
            cancellationToken).AsTask();

    private static async Task<BrowserResult<BrowserDocumentSnapshot>>
        CaptureAfterNativeSnapshotDrainAsync(
            BrowserSurface surface,
            BrowserDocumentBinding document,
            CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var result = await surface.CaptureSnapshotAsync(
                document,
                cancellationToken);
            if (result.Error?.Code
                != BrowserErrorCode.NavigationInProgress)
            {
                return result;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(1),
                cancellationToken);
        }

        throw new TimeoutException(
            "The drained native snapshot did not release its BrowserSurface fence.");
    }

    private static BrowserSurface Surface(IEmbeddedBrowserView nativeView) =>
        new(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);

    private static BrowserSurface Surface(
        IEmbeddedBrowserView nativeView,
        TimeProvider timeProvider) =>
        new(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            timeProvider: timeProvider,
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);

    private static BrowserSurface SurfaceWithReplacement(
        IEmbeddedBrowserView nativeView,
        IEmbeddedBrowserView replacement) =>
        new(
            nativeView,
            BrowserTestDestinationPolicy.Public,
            InlineBrowserUiDispatcher.Instance,
            () => replacement,
            static _ => { },
            capabilityProfile: BrowserCapabilityProfile.FullAutomationCandidate);

    private sealed class ManualTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) =>
            utcNow += duration;
    }

    private sealed class QueuedBrowserUiDispatcher : IBrowserUiDispatcher
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _operations = new();
        private readonly TaskCompletionSource _workQueued = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _hasAccess = true;

        public bool CheckAccess()
        {
            lock (_gate)
            {
                return _hasAccess;
            }
        }

        public ValueTask<T> InvokeAsync<T>(Func<T> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (CheckAccess())
            {
                return ValueTask.FromResult(operation());
            }

            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Enqueue(
                () =>
                {
                    try
                    {
                        completion.TrySetResult(operation());
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                });
            return new ValueTask<T>(completion.Task);
        }

        public void Post(Action operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            Enqueue(operation);
        }

        public void Suspend()
        {
            lock (_gate)
            {
                _hasAccess = false;
            }
        }

        public Task WaitForWorkAsync() => _workQueued.Task;

        public void Drain()
        {
            lock (_gate)
            {
                _hasAccess = true;
            }

            while (true)
            {
                Action operation;
                lock (_gate)
                {
                    if (!_operations.TryDequeue(out operation!))
                    {
                        return;
                    }
                }

                operation();
            }
        }

        private void Enqueue(Action operation)
        {
            lock (_gate)
            {
                _operations.Enqueue(operation);
                _workQueued.TrySetResult();
            }
        }
    }

    private sealed class FailingBrowserUiDispatcher :
        IBrowserUiDispatcher
    {
        private volatile bool _hasAccess = true;

        public bool CheckAccess() => _hasAccess;

        public ValueTask<T> InvokeAsync<T>(Func<T> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            return _hasAccess
                ? ValueTask.FromResult(operation())
                : ValueTask.FromException<T>(
                    new InvalidOperationException(
                        "The UI dispatcher is unavailable."));
        }

        public void Post(Action operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (!_hasAccess)
            {
                throw new InvalidOperationException(
                    "The UI dispatcher is unavailable.");
            }

            operation();
        }

        public void FailMarshalling() => _hasAccess = false;

        public void RestoreAccess() => _hasAccess = true;
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
