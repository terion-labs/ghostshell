using Asura.Application;

namespace Asura.Browser.Tests;

internal sealed class RecordingBrowserRenderer :
    IBrowserRenderer,
    IBrowserPhysicalInputBarrier
{
    private TaskCompletionSource? _reloadRelease;
    private TaskCompletionSource? _reloadStarted;
    private TaskCompletionSource? _governedRelease;
    private TaskCompletionSource? _governedStarted;

    public RecordingBrowserRenderer(
        CapabilitySet? capabilities = null,
        BrowserSessionState? initialState = null)
    {
        Capabilities = capabilities
            ?? BrowserCapabilityProfile.FullAutomationCandidate.Capabilities;
        State = initialState ?? BrowserSessionState.Initial(BrowserAddress.Blank);
    }

    public BrowserSessionState State { get; private set; }

    public CapabilitySet Capabilities { get; }

    public int NavigateCount { get; private set; }

    public int StopCount { get; private set; }

    public int ClickCount { get; private set; }

    public int FillCount { get; private set; }

    public int CheckCount { get; private set; }

    public int MouseCount { get; private set; }

    public int EvaluateCount { get; private set; }

    public int BeginNetworkActivityObservationCount { get; private set; }

    public int EndNetworkActivityObservationCount { get; private set; }

    public BrowserAutomationBinding? LastAutomationBinding { get; private set; }

    public bool RejectStopAsUnavailable { get; set; }

    public BrowserAddress? LastNavigatedAddress { get; private set; }

    public BrowserNavigationStartBinding? LastStartBinding { get; private set; }

    public BrowserDocumentBinding? LastSnapshotBinding { get; private set; }

    public BrowserElementReference? LastClickReference { get; private set; }

    public BrowserNavigationOrigin? LastClickOrigin { get; private set; }

    public BrowserElementReference? LastFillReference { get; private set; }

    public string? LastFillText { get; private set; }

    public BrowserNavigationOrigin? LastFillOrigin { get; private set; }

    public BrowserElementReference? LastCheckReference { get; private set; }

    public BrowserNavigationOrigin? LastCheckOrigin { get; private set; }

    public BrowserResult<BrowserClickReceipt>? ClickResult { get; set; }

    public BrowserResult<BrowserFillReceipt>? FillResult { get; set; }

    public BrowserResult<BrowserCheckReceipt>? CheckResult { get; set; }

    public BrowserSessionState? StateAfterClick { get; set; }

    public BrowserSessionState? StateAfterFill { get; set; }

    public BrowserSessionState? StateAfterCheck { get; set; }

    public event EventHandler<BrowserStateChangedEventArgs>? StateChanged;

    public ValueTask BeginNetworkActivityObservationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginNetworkActivityObservationCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask EndNetworkActivityObservationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EndNetworkActivityObservationCount++;
        return ValueTask.CompletedTask;
    }

    public Func<NativeRendererPhysicalInput, bool>? PhysicalInputGate
    { get; private set; }

    public void BindPhysicalInputGate(
        Func<NativeRendererPhysicalInput, bool>? physicalInputGate) =>
        PhysicalInputGate = physicalInputGate;

    public ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
        BrowserAddress address,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NavigateCount++;
        LastNavigatedAddress = address;
        Publish(new BrowserSessionState(
            address,
            string.Empty,
            BrowserLoadState.Loading,
            State.CanGoBack,
            State.CanGoForward,
            State.DocumentRevision));
        return Success();
    }

    public ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
        CancellationToken cancellationToken) =>
        BeginHistoryNavigation(cancellationToken);

    public ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
        CancellationToken cancellationToken) =>
        BeginHistoryNavigation(cancellationToken);

    public async ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
        CancellationToken cancellationToken)
    {
        if (_reloadRelease is { } release)
        {
            _reloadStarted!.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _reloadRelease = null;
            _reloadStarted = null;
        }

        return await BeginHistoryNavigation(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        if (RejectStopAsUnavailable)
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserSessionState>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.RendererUnavailable,
                        "The embedded browser is already idle.",
                        retryable: true)));
        }

        Publish(new BrowserSessionState(
            State.Address,
            State.Title,
            BrowserLoadState.Ready,
            State.CanGoBack,
            State.CanGoForward,
            State.DocumentRevision));
        return Success();
    }

    public ValueTask<BrowserResult<BrowserSessionState>>
        NavigateWithinOriginAsync(
            BrowserOriginConstrainedNavigationRequest request,
            BrowserNavigationOrigin allowedOrigin,
            BrowserNavigationStartBinding startBinding,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        ArgumentNullException.ThrowIfNull(startBinding);
        if (_governedRelease is not null)
        {
            return AwaitGovernedReleaseAsync(
                request,
                allowedOrigin,
                startBinding,
                cancellationToken);
        }

        return NavigateWithinOriginCore(
            request,
            allowedOrigin,
            startBinding,
            cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserDocumentSnapshot>>
        CaptureSnapshotAsync(
            BrowserDocumentBinding document,
            CancellationToken cancellationToken,
            BrowserSnapshotQuery? query = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastSnapshotBinding = document;
        if (!document.Matches(State))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationStateChanged,
                        "The browser document changed during capture.",
                        retryable: true)));
        }

        var reference = new BrowserElementReference(
            "be_recording",
            document);
        return ValueTask.FromResult(
            BrowserResult<BrowserDocumentSnapshot>.Success(
                new BrowserDocumentSnapshot(
                    document,
                    [
                        new BrowserSnapshotNode(
                            0,
                            "document",
                            "Example"),
                        new BrowserSnapshotNode(
                            1,
                            "button",
                            "Continue",
                            reference),
                    ],
                    DateTimeOffset.UnixEpoch)));
    }

    public ValueTask<BrowserResult<BrowserClickReceipt>>
        ClickWithinOriginAsync(
            BrowserElementReference reference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        cancellationToken.ThrowIfCancellationRequested();
        ClickCount++;
        LastClickReference = reference;
        LastClickOrigin = allowedOrigin;
        var result = ClickResult
            ?? BrowserResult<BrowserClickReceipt>.Success(
                new BrowserClickReceipt(reference.Document));
        if (StateAfterClick is { } stateAfterClick)
        {
            Publish(stateAfterClick);
            StateAfterClick = null;
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<BrowserResult<BrowserFillReceipt>>
        FillWithinOriginAsync(
            BrowserElementReference reference,
            string text,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        cancellationToken.ThrowIfCancellationRequested();
        FillCount++;
        LastFillReference = reference;
        LastFillText = text;
        LastFillOrigin = allowedOrigin;
        var result = FillResult
            ?? BrowserResult<BrowserFillReceipt>.Success(
                new BrowserFillReceipt(reference.Document));
        if (StateAfterFill is { } stateAfterFill)
        {
            Publish(stateAfterFill);
            StateAfterFill = null;
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<BrowserResult<BrowserCheckReceipt>>
        CheckWithinOriginAsync(
            BrowserElementReference reference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        cancellationToken.ThrowIfCancellationRequested();
        CheckCount++;
        LastCheckReference = reference;
        LastCheckOrigin = allowedOrigin;
        var result = CheckResult
            ?? BrowserResult<BrowserCheckReceipt>.Success(
                new BrowserCheckReceipt(reference.Document));
        if (StateAfterCheck is { } stateAfterCheck)
        {
            Publish(stateAfterCheck);
            StateAfterCheck = null;
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<BrowserResult<BrowserAutomationReceipt>>
        DispatchMouseWithinOriginAsync(
            BrowserMouseRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MouseCount++;
        LastAutomationBinding = request.Binding;
        if (!request.Binding.Matches(State) || !allowedOrigin.Allows(State.Address))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserAutomationReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationStateChanged,
                        "The browser automation binding is stale.",
                        retryable: true)));
        }

        Publish(new BrowserSessionState(
            State.Address,
            State.Title,
            State.LoadState,
            State.CanGoBack,
            State.CanGoForward,
            State.DocumentRevision,
            State.Failure,
            State.Viewport,
            State.ViewportRevision,
            State.InputEpoch + 1));
        return ValueTask.FromResult(
            BrowserResult<BrowserAutomationReceipt>.Success(
                new BrowserAutomationReceipt(request.Binding, State)));
    }

    public ValueTask<BrowserResult<BrowserEvaluationResult>>
        EvaluateWithinOriginAsync(
            BrowserEvaluateRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EvaluateCount++;
        LastAutomationBinding = request.Binding;
        return request.Binding.Matches(State) && allowedOrigin.Allows(State.Address)
            ? ValueTask.FromResult(
                BrowserResult<BrowserEvaluationResult>.Success(
                    new BrowserEvaluationResult(request.Binding, State, "2")))
            : ValueTask.FromResult(
                BrowserResult<BrowserEvaluationResult>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationStateChanged,
                        "The browser automation binding is stale.",
                        retryable: true)));
    }

    public void SetAutomationViewport(double width = 800, double height = 600)
    {
        Publish(new BrowserSessionState(
            State.Address,
            State.Title,
            BrowserLoadState.Ready,
            State.CanGoBack,
            State.CanGoForward,
            State.DocumentRevision,
            viewport: new BrowserViewportState(width, height, 1),
            viewportRevision: State.ViewportRevision + 1,
            inputEpoch: State.InputEpoch));
    }

    private ValueTask<BrowserResult<BrowserSessionState>>
        NavigateWithinOriginCore(
            BrowserOriginConstrainedNavigationRequest request,
            BrowserNavigationOrigin allowedOrigin,
            BrowserNavigationStartBinding startBinding,
            CancellationToken cancellationToken)
    {
        LastStartBinding = startBinding;
        if (!startBinding.Matches(State))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserSessionState>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationStateChanged,
                        "The browser document changed after authorization.",
                        retryable: true)));
        }

        return request switch
        {
            BrowserOriginConstrainedNavigationRequest.Navigate navigate
                when allowedOrigin.Allows(navigate.Address) =>
                NavigateAsync(navigate.Address, cancellationToken),
            BrowserOriginConstrainedNavigationRequest.Back
                when allowedOrigin.Allows(State.Address) =>
                GoBackAsync(cancellationToken),
            BrowserOriginConstrainedNavigationRequest.Forward
                when allowedOrigin.Allows(State.Address) =>
                GoForwardAsync(cancellationToken),
            BrowserOriginConstrainedNavigationRequest.Reload
                when allowedOrigin.Allows(State.Address) =>
                ReloadAsync(cancellationToken),
            _ => ValueTask.FromResult(
                BrowserResult<BrowserSessionState>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationPolicyDenied,
                        "The browser blocked a top-level navigation outside the approved origin."))),
        };
    }

    private async ValueTask<BrowserResult<BrowserSessionState>>
        AwaitGovernedReleaseAsync(
            BrowserOriginConstrainedNavigationRequest request,
            BrowserNavigationOrigin allowedOrigin,
            BrowserNavigationStartBinding startBinding,
            CancellationToken cancellationToken)
    {
        var release = _governedRelease
            ?? throw new InvalidOperationException(
                "No governed navigation is paused.");
        _governedStarted!.TrySetResult();
        try
        {
            await release.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _governedRelease = null;
            _governedStarted = null;
        }

        return await NavigateWithinOriginCore(
                request,
                allowedOrigin,
                startBinding,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void PauseNextReload()
    {
        if (_reloadRelease is not null)
        {
            throw new InvalidOperationException("A reload is already paused.");
        }

        _reloadStarted = NewSignal();
        _reloadRelease = NewSignal();
    }

    public void PauseNextGovernedNavigation()
    {
        if (_governedRelease is not null)
        {
            throw new InvalidOperationException(
                "A governed navigation is already paused.");
        }

        _governedStarted = NewSignal();
        _governedRelease = NewSignal();
    }

    public Task WaitForPausedGovernedNavigationAsync(
        CancellationToken cancellationToken) =>
        (_governedStarted?.Task
            ?? throw new InvalidOperationException(
                "No governed navigation is paused."))
        .WaitAsync(cancellationToken);

    public Task WaitForPausedReloadAsync(CancellationToken cancellationToken) =>
        (_reloadStarted?.Task
            ?? throw new InvalidOperationException("No reload is paused."))
        .WaitAsync(cancellationToken);

    public void ResumeReload()
    {
        (_reloadRelease
            ?? throw new InvalidOperationException("No reload is paused."))
        .TrySetResult();
    }

    public void Complete(BrowserAddress address, bool canGoBack = false)
    {
        Publish(new BrowserSessionState(
            address,
            string.Empty,
            BrowserLoadState.Ready,
            canGoBack,
            false,
            checked(State.DocumentRevision + 1)));
    }

    public void SetDocumentRevisionForTest(long documentRevision)
    {
        Publish(new BrowserSessionState(
            State.Address,
            State.Title,
            State.LoadState,
            State.CanGoBack,
            State.CanGoForward,
            documentRevision,
            State.Failure));
    }

    private ValueTask<BrowserResult<BrowserSessionState>> BeginHistoryNavigation(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(new BrowserSessionState(
            State.Address,
            State.Title,
            BrowserLoadState.Loading,
            State.CanGoBack,
            State.CanGoForward,
            State.DocumentRevision));
        return Success();
    }

    private ValueTask<BrowserResult<BrowserSessionState>> Success() =>
        ValueTask.FromResult(
            BrowserResult<BrowserSessionState>.Success(State));

    private void Publish(BrowserSessionState state)
    {
        State = state;
        StateChanged?.Invoke(this, new BrowserStateChangedEventArgs(state));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
