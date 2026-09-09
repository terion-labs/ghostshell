using System.Runtime.CompilerServices;
using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

internal sealed class FakeBrowserPanelSessionFactory : IBrowserPanelSessionFactory
{
    private readonly Dictionary<SessionId, FakeBrowserPanelSession> _sessions = [];
    private readonly CapabilitySet _createdSessionCapabilities;

    public FakeBrowserPanelSessionFactory(
        CapabilitySet? advertisedCapabilities = null,
        CapabilitySet? createdSessionCapabilities = null)
    {
        Capabilities = advertisedCapabilities ?? DefaultCapabilities();
        _createdSessionCapabilities = createdSessionCapabilities ?? Capabilities;
    }

    public CapabilitySet Capabilities { get; }

    public int CreateCount { get; private set; }

    public Func<FakeBrowserPanelSession, CancellationToken, ValueTask>? AfterCreateAsync
    {
        get;
        set;
    }

    public Func<CancellationToken, ValueTask>? BeforeSnapshotForNewSessions
    {
        get;
        set;
    }

    public FakeBrowserPanelSession this[SessionId id] => _sessions[id];

    public async ValueTask<IBrowserPanelSession> CreateAsync(
        SessionId sessionId,
        BrowserAddress initialAddress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateCount++;
        var session = new FakeBrowserPanelSession(
            sessionId,
            initialAddress,
            _createdSessionCapabilities)
        {
            BeforeSnapshotAsync = BeforeSnapshotForNewSessions,
        };
        _sessions.Add(sessionId, session);
        if (AfterCreateAsync is { } afterCreate)
        {
            await afterCreate(session, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    private static CapabilitySet DefaultCapabilities() => new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.AttachInteractive,
        SessionCapabilities.BrowserReadState,
        SessionCapabilities.BrowserSnapshot,
        SessionCapabilities.BrowserWait,
        SessionCapabilities.BrowserClick,
        SessionCapabilities.BrowserFill,
        SessionCapabilities.BrowserCheck,
        SessionCapabilities.BrowserMouse,
        SessionCapabilities.BrowserKey,
        SessionCapabilities.BrowserScroll,
        SessionCapabilities.BrowserEvaluate,
        SessionCapabilities.BrowserNavigate,
        SessionCapabilities.BrowserBack,
        SessionCapabilities.BrowserForward,
        SessionCapabilities.BrowserReload,
        SessionCapabilities.BrowserStop,
        SessionCapabilities.BrowserOriginGuard,
        SessionCapabilities.BrowserAgentInputBarrier,
    ]);
}

internal sealed class FakeBrowserPanelSession(
    SessionId id,
    BrowserAddress initialAddress,
    CapabilitySet capabilities) : IBrowserPanelSession
{
    private IBrowserRenderer? _renderer;
    private bool _closed;

    public SessionId Id { get; } = id;

    public PanelKind Kind => PanelKind.Browser;

    public CapabilitySet Capabilities { get; } = capabilities;

    public BrowserSessionState State { get; private set; } =
        BrowserSessionState.Initial(initialAddress);

    public int AttachCount { get; private set; }

    public int DetachCount { get; private set; }

    public int DisposeCount { get; private set; }

    public Func<CancellationToken, ValueTask>? BeforeSnapshotAsync { get; set; }

    public ValueTask AttachRendererAsync(
        IBrowserRenderer renderer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_renderer is not null)
        {
            throw new InvalidOperationException("A browser renderer is already attached.");
        }

        _renderer = renderer;
        _renderer.StateChanged += OnRendererStateChanged;
        State = renderer.State;
        AttachCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DetachRendererAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_renderer is null)
        {
            return ValueTask.CompletedTask;
        }

        _renderer.StateChanged -= OnRendererStateChanged;
        _renderer = null;
        DetachCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
        BrowserAddress address,
        CancellationToken cancellationToken) =>
        RendererOrFailure(
            renderer => renderer.NavigateAsync(address, cancellationToken));

    public ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
        CancellationToken cancellationToken) =>
        RendererOrFailure(renderer => renderer.GoBackAsync(cancellationToken));

    public ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
        CancellationToken cancellationToken) =>
        RendererOrFailure(renderer => renderer.GoForwardAsync(cancellationToken));

    public ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
        CancellationToken cancellationToken) =>
        RendererOrFailure(renderer => renderer.ReloadAsync(cancellationToken));

    public ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
        CancellationToken cancellationToken) =>
        RendererOrFailure(renderer => renderer.StopAsync(cancellationToken));

    public ValueTask<BrowserResult<BrowserSessionState>>
        NavigateWithinOriginAsync(
            BrowserOriginConstrainedNavigationRequest request,
            BrowserNavigationOrigin allowedOrigin,
            BrowserNavigationStartBinding startBinding,
            CancellationToken cancellationToken) =>
        RendererOrFailure(
            renderer => renderer.NavigateWithinOriginAsync(
                request,
                allowedOrigin,
                startBinding,
                cancellationToken));

    public ValueTask<BrowserResult<BrowserDocumentSnapshot>>
        CaptureSnapshotAsync(
            BrowserDocumentBinding document,
            CancellationToken cancellationToken,
            BrowserSnapshotQuery? query = null)
    {
        if (_closed)
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.SessionClosed,
                        "The browser session is closed.")));
        }

        return _renderer is null
            ? ValueTask.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.RendererUnavailable,
                        "No browser renderer is attached.",
                        retryable: true)))
            : _renderer.CaptureSnapshotAsync(
                document,
                cancellationToken,
                query);
    }

    public ValueTask<BrowserResult<BrowserClickReceipt>>
        ClickWithinOriginAsync(
            BrowserElementReference reference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        if (_closed)
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.SessionClosed,
                        "The browser session is closed.")));
        }

        return _renderer is null
            ? ValueTask.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.RendererUnavailable,
                        "No browser renderer is attached.",
                        retryable: true)))
            : _renderer.ClickWithinOriginAsync(
                reference,
                allowedOrigin,
                cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserFillReceipt>>
        FillWithinOriginAsync(
            BrowserElementReference reference,
            string text,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        if (_closed)
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.SessionClosed,
                        "The browser session is closed.")));
        }

        return _renderer is null
            ? ValueTask.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.RendererUnavailable,
                        "No browser renderer is attached.",
                        retryable: true)))
            : _renderer.FillWithinOriginAsync(
                reference,
                text,
                allowedOrigin,
                cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserCheckReceipt>>
        CheckWithinOriginAsync(
            BrowserElementReference reference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        if (_closed)
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.SessionClosed,
                        "The browser session is closed.")));
        }

        return _renderer is null
            ? ValueTask.FromResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.RendererUnavailable,
                        "No browser renderer is attached.",
                        retryable: true)))
            : _renderer.CheckWithinOriginAsync(
                reference,
                allowedOrigin,
                cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserAutomationReceipt>>
        DispatchMouseWithinOriginAsync(
            BrowserMouseRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken) =>
        _renderer is null
            ? ValueTask.FromResult(BrowserResult<BrowserAutomationReceipt>.Failure(
                BrowserError.Create(BrowserErrorCode.RendererUnavailable, "No browser renderer is attached.")))
            : _renderer.DispatchMouseWithinOriginAsync(request, allowedOrigin, cancellationToken);

    public ValueTask<BrowserResult<BrowserAutomationReceipt>>
        DispatchKeyWithinOriginAsync(
            BrowserKeyRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken) =>
        _renderer is null
            ? ValueTask.FromResult(BrowserResult<BrowserAutomationReceipt>.Failure(
                BrowserError.Create(BrowserErrorCode.RendererUnavailable, "No browser renderer is attached.")))
            : _renderer.DispatchKeyWithinOriginAsync(request, allowedOrigin, cancellationToken);

    public ValueTask<BrowserResult<BrowserAutomationReceipt>>
        ScrollWithinOriginAsync(
            BrowserScrollRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken) =>
        _renderer is null
            ? ValueTask.FromResult(BrowserResult<BrowserAutomationReceipt>.Failure(
                BrowserError.Create(BrowserErrorCode.RendererUnavailable, "No browser renderer is attached.")))
            : _renderer.ScrollWithinOriginAsync(request, allowedOrigin, cancellationToken);

    public ValueTask<BrowserResult<BrowserEvaluationResult>>
        EvaluateWithinOriginAsync(
            BrowserEvaluateRequest request,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken) =>
        _renderer is null
            ? ValueTask.FromResult(BrowserResult<BrowserEvaluationResult>.Failure(
                BrowserError.Create(BrowserErrorCode.RendererUnavailable, "No browser renderer is attached.")))
            : _renderer.EvaluateWithinOriginAsync(request, allowedOrigin, cancellationToken);

    public ValueTask<BrowserResult<BrowserElementStateSnapshot>>
        ReadElementStateAsync(
            BrowserElementReference reference,
            CancellationToken cancellationToken)
    {
        if (_closed)
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserElementStateSnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.SessionClosed,
                        "The browser session is closed.")));
        }

        return _renderer is null
            ? ValueTask.FromResult(
                BrowserResult<BrowserElementStateSnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.RendererUnavailable,
                        "No browser renderer is attached.",
                        retryable: true)))
            : _renderer.ReadElementStateAsync(reference, cancellationToken);
    }

    public ValueTask BeginNetworkActivityObservationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _closed || _renderer is null
            ? ValueTask.CompletedTask
            : _renderer.BeginNetworkActivityObservationAsync(
                cancellationToken);
    }

    public ValueTask EndNetworkActivityObservationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _closed || _renderer is null
            ? ValueTask.CompletedTask
            : _renderer.EndNetworkActivityObservationAsync(
                cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserNetworkActivitySnapshot>>
        ReadNetworkActivityAsync(CancellationToken cancellationToken)
    {
        if (_closed)
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserNetworkActivitySnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.SessionClosed,
                        "The browser session is closed.")));
        }

        return _renderer is null
            ? ValueTask.FromResult(
                BrowserResult<BrowserNetworkActivitySnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.RendererUnavailable,
                        "No browser renderer is attached.",
                        retryable: true)))
            : _renderer.ReadNetworkActivityAsync(cancellationToken);
    }

    public async ValueTask<PanelSessionSnapshot> SnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (BeforeSnapshotAsync is { } beforeSnapshot)
        {
            await beforeSnapshot(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _closed
            ? new PanelSessionSnapshot(
                SessionLifecycle.Closed,
                SessionHealth.Ended,
                false,
                "Browser closed.")
            : new PanelSessionSnapshot(
                SessionLifecycle.Active,
                State.LoadState == BrowserLoadState.Failed
                    ? SessionHealth.Degraded
                    : SessionHealth.Healthy,
                false,
                _renderer is null
                    ? "Waiting for browser renderer."
                    : "Browser renderer attached.",
                State.Failure is null
                    ? null
                    : new SessionFailure(
                        State.Failure.StableCode,
                        State.Failure.Message,
                        State.Failure.Retryable));
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    public async ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        _ = mode;
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed)
        {
            return PanelCloseOutcome.AlreadyClosed;
        }

        await DetachRendererAsync(cancellationToken);
        _closed = true;
        return PanelCloseOutcome.GracefullyClosed;
    }

    public async ValueTask DisposeAsync()
    {
        await DetachRendererAsync(CancellationToken.None);
        _closed = true;
        DisposeCount++;
    }

    private ValueTask<BrowserResult<BrowserSessionState>> RendererOrFailure(
        Func<
            IBrowserRenderer,
            ValueTask<BrowserResult<BrowserSessionState>>> operation)
    {
        if (_closed)
        {
            return ValueTask.FromResult(BrowserResult<BrowserSessionState>.Failure(
                BrowserError.Create(
                    BrowserErrorCode.SessionClosed,
                    "The browser session is closed.")));
        }

        return _renderer is null
            ? ValueTask.FromResult(BrowserResult<BrowserSessionState>.Failure(
                BrowserError.Create(
                    BrowserErrorCode.RendererUnavailable,
                    "No browser renderer is attached.",
                    retryable: true)))
            : operation(_renderer);
    }

    private void OnRendererStateChanged(
        object? sender,
        BrowserStateChangedEventArgs eventArgs)
    {
        _ = sender;
        State = eventArgs.State;
    }
}

internal sealed class FakeBrowserRenderer(BrowserAddress initialAddress) :
    IBrowserRenderer,
    IBrowserPhysicalInputBarrier
{
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

    public BrowserSessionState State { get; private set; } =
        BrowserSessionState.Initial(initialAddress);

    public int NavigateCount { get; private set; }

    public int BackCount { get; private set; }

    public int ForwardCount { get; private set; }

    public int ReloadCount { get; private set; }

    public int StopCount { get; private set; }

    public TaskCompletionSource? NavigateEntered { get; set; }

    public TaskCompletionSource? NavigateRelease { get; set; }

    public bool IgnoreNavigateCancellation { get; set; }

    public int SnapshotCount { get; private set; }

    public int ClickCount { get; private set; }

    public BrowserElementReference? LastClickedReference { get; private set; }

    public BrowserNavigationOrigin? LastClickOrigin { get; private set; }

    public int FillCount { get; private set; }

    public BrowserElementReference? LastFilledReference { get; private set; }

    public string? LastFillText { get; private set; }

    public BrowserNavigationOrigin? LastFillOrigin { get; private set; }

    public int CheckCount { get; private set; }

    public BrowserElementReference? LastCheckedReference { get; private set; }

    public BrowserNavigationOrigin? LastCheckOrigin { get; private set; }

    public event EventHandler<BrowserStateChangedEventArgs>? StateChanged;

    public Func<NativeRendererPhysicalInput, bool>? PhysicalInputGate
    { get; private set; }

    public void BindPhysicalInputGate(
        Func<NativeRendererPhysicalInput, bool>? physicalInputGate) =>
        PhysicalInputGate = physicalInputGate;

    public void BeginLoading(BrowserAddress address)
    {
        State = new BrowserSessionState(
            address,
            State.Title,
            BrowserLoadState.Loading,
            State.CanGoBack,
            State.CanGoForward,
            State.DocumentRevision);
        StateChanged?.Invoke(this, new BrowserStateChangedEventArgs(State));
    }

    public async ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
        BrowserAddress address,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NavigateCount++;
        State = new BrowserSessionState(
            address,
            address.Value.Host,
            BrowserLoadState.Ready,
            canGoBack: true,
            canGoForward: false,
            State.DocumentRevision + 1);
        StateChanged?.Invoke(this, new BrowserStateChangedEventArgs(State));
        if (NavigateEntered is not null)
        {
            NavigateEntered.TrySetResult();
            var release = NavigateRelease
                ?? throw new InvalidOperationException(
                    "A blocked browser navigation requires a release signal.");
            if (IgnoreNavigateCancellation)
            {
                await release.Task.ConfigureAwait(false);
            }
            else
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return BrowserResult<BrowserSessionState>.Success(State);
    }

    public ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BackCount++;
        return Success();
    }

    public ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ForwardCount++;
        return Success();
    }

    public ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReloadCount++;
        return Success();
    }

    public ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
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
        if (!startBinding.Matches(State))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserSessionState>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationStateChanged,
                        "The browser document changed after authorization.",
                        retryable: true)));
        }

        if (!allowedOrigin.Allows(
                request is BrowserOriginConstrainedNavigationRequest.Navigate
                    requestedNavigation
                    ? requestedNavigation.Address
                    : State.Address))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserSessionState>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationPolicyDenied,
                        "The browser blocked a top-level navigation outside the approved origin.")));
        }

        return request switch
        {
            BrowserOriginConstrainedNavigationRequest.Navigate navigationRequest =>
                NavigateAsync(navigationRequest.Address, cancellationToken),
            BrowserOriginConstrainedNavigationRequest.Back =>
                GoBackAsync(cancellationToken),
            BrowserOriginConstrainedNavigationRequest.Forward =>
                GoForwardAsync(cancellationToken),
            BrowserOriginConstrainedNavigationRequest.Reload =>
                ReloadAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    public ValueTask<BrowserResult<BrowserDocumentSnapshot>>
        CaptureSnapshotAsync(
            BrowserDocumentBinding document,
            CancellationToken cancellationToken,
            BrowserSnapshotQuery? query = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotCount++;
        if (!document.Matches(State))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserDocumentSnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationStateChanged,
                        "The browser document changed during capture.",
                        retryable: true)));
        }

        return ValueTask.FromResult(
            BrowserResult<BrowserDocumentSnapshot>.Success(
                new BrowserDocumentSnapshot(
                    document,
                    [new BrowserSnapshotNode(
                        0,
                        "document",
                        "Example")],
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
        LastClickedReference = reference;
        LastClickOrigin = allowedOrigin;
        if (!reference.Document.Matches(State))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.ElementReferenceStale,
                        "The browser element reference is stale.",
                        retryable: true)));
        }

        if (!allowedOrigin.Allows(State.Address))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserClickReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationPolicyDenied,
                        "The browser click origin is no longer allowed.")));
        }

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
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        cancellationToken.ThrowIfCancellationRequested();
        FillCount++;
        LastFilledReference = reference;
        LastFillText = text;
        LastFillOrigin = allowedOrigin;
        if (!reference.Document.Matches(State))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.ElementReferenceStale,
                        "The browser element reference is stale.",
                        retryable: true)));
        }

        if (!allowedOrigin.Allows(State.Address))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserFillReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationPolicyDenied,
                        "The browser fill origin is no longer allowed.")));
        }

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
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        cancellationToken.ThrowIfCancellationRequested();
        CheckCount++;
        LastCheckedReference = reference;
        LastCheckOrigin = allowedOrigin;
        if (!reference.Document.Matches(State))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.ElementReferenceStale,
                        "The browser element reference is stale.",
                        retryable: true)));
        }

        if (!allowedOrigin.Allows(State.Address))
        {
            return ValueTask.FromResult(
                BrowserResult<BrowserCheckReceipt>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.NavigationPolicyDenied,
                        "The browser check origin is no longer allowed.")));
        }

        return ValueTask.FromResult(
            BrowserResult<BrowserCheckReceipt>.Success(
                new BrowserCheckReceipt(reference.Document)));
    }

    private ValueTask<BrowserResult<BrowserSessionState>> Success() =>
        ValueTask.FromResult(BrowserResult<BrowserSessionState>.Success(State));
}
