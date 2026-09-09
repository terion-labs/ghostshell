using System.Runtime.CompilerServices;
using Asura.Application;
using Asura.Core;

namespace Asura.Browser;

/// <summary>
/// Keeps browser session identity and lifecycle independent from the native
/// control. Detaching a renderer preserves the current address and history
/// state, while every operation fails closed until another renderer attaches.
/// </summary>
public sealed partial class BrowserPanelSession : IBrowserPanelSession
{
    // Page navigation can continue for the lifetime of a workspace. The host
    // snapshot is authoritative, so only recent lifecycle events stay in memory.
    private const int MaximumRetainedEvents = 256;

    private readonly object _gate = new();
    private readonly List<PanelSessionEvent> _events = [];
    private readonly Stack<IBrowserRenderer> _networkObservationRenderers = [];
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _eventsChanged = NewSignal();
    private IBrowserRenderer? _renderer;
    private IBrowserRenderer? _lastDetachedRenderer;
    private BrowserSessionState? _lastDetachedRendererStateAtProjection;
    private BrowserSessionState? _rendererStateAtLastProjection;
    private long _rendererRevisionBaseline;
    private long _logicalRevisionAtAttach;
    private BrowserSessionState _state;
    private ActiveOperation _activeOperation;
    private CancellationTokenSource? _governedOperationCancellation;
    private int _governedInterruptionsInFlight;
    private TaskCompletionSource? _governedInterruptionsDrained;
    private bool _closed;
    private bool _disposed;
    private bool _hasEverAttached;
    private long _sequence;

    public BrowserPanelSession(
        SessionId id,
        BrowserAddress initialAddress,
        TimeProvider timeProvider)
        : this(
            id,
            initialAddress,
            timeProvider,
            BrowserCapabilityProfile.Production)
    {
    }

    internal BrowserPanelSession(
        SessionId id,
        BrowserAddress initialAddress,
        TimeProvider timeProvider,
        BrowserCapabilityProfile capabilityProfile)
    {
        Id = id;
        ArgumentNullException.ThrowIfNull(initialAddress);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        CapabilityProfile = capabilityProfile
            ?? throw new ArgumentNullException(nameof(capabilityProfile));
        _state = BrowserSessionState.Initial(initialAddress);
        Publish(
            SessionLifecycle.Starting,
            SessionHealth.Starting,
            "Waiting for a browser renderer attachment.");
    }

    public SessionId Id { get; }

    public PanelKind Kind => PanelKind.Browser;

    public BrowserCapabilityProfile CapabilityProfile { get; }

    public CapabilitySet Capabilities => CapabilityProfile.Capabilities;

    public BrowserSessionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public async ValueTask AttachRendererAsync(
        IBrowserRenderer renderer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        EnsureExactCapabilities(renderer);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BeginOperation(ActiveOperation.Serialized);
        try
        {
            BrowserAddress desiredAddress;
            var canResumeRetainedRenderer = false;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_closed)
                {
                    throw new InvalidOperationException(
                        "The browser session is closed.");
                }

                if (ReferenceEquals(_renderer, renderer))
                {
                    return;
                }

                if (_renderer is not null)
                {
                    throw new InvalidOperationException(
                        "The browser session already has a renderer.");
                }

                var retainedProjection =
                    ReferenceEquals(_lastDetachedRenderer, renderer)
                        ? _lastDetachedRendererStateAtProjection
                        : null;
                _renderer = renderer;
                canResumeRetainedRenderer =
                    retainedProjection is not null
                    && renderer.State.Address == _state.Address
                    && renderer.State.DocumentRevision
                        >= retainedProjection.DocumentRevision;
                _lastDetachedRenderer = null;
                _lastDetachedRendererStateAtProjection = null;
                _rendererRevisionBaseline = canResumeRetainedRenderer
                    ? retainedProjection!.DocumentRevision
                    : renderer.State.DocumentRevision;
                _logicalRevisionAtAttach = _state.DocumentRevision;
                _rendererStateAtLastProjection = canResumeRetainedRenderer
                    ? retainedProjection
                    : renderer.State;
                _hasEverAttached = true;
                desiredAddress = _state.Address;
                renderer.StateChanged += OnRendererStateChanged;
                PublishUnsafe(
                    SessionLifecycle.Active,
                    SessionHealth.Healthy,
                    "Browser renderer attached.");
            }

            if (canResumeRetainedRenderer)
            {
                ApplyRendererState(renderer, renderer.State);
                return;
            }

            BrowserResult<BrowserSessionState> initialNavigation;
            try
            {
                initialNavigation = await renderer
                    .NavigateAsync(desiredAddress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                DetachAfterFailedAttachment(renderer);
                throw;
            }
            catch (Exception exception)
            {
                DetachAfterFailedAttachment(renderer);
                throw new InvalidOperationException(
                    "The browser renderer failed during attachment.",
                    exception);
            }

            if (!initialNavigation.IsSuccess)
            {
                DetachAfterFailedAttachment(renderer);
                if (initialNavigation.Error?.Code == BrowserErrorCode.Cancelled
                    && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new InvalidOperationException(
                    "The browser renderer could not open the session address.");
            }

            ApplyRendererState(renderer, initialNavigation.Value!);
        }
        finally
        {
            CompleteOperation(ActiveOperation.Serialized);
        }
    }

    public async ValueTask DetachRendererAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BeginOperation(ActiveOperation.Serialized);
        try
        {
            lock (_gate)
            {
                DetachRendererUnsafe(rememberRenderer: true);
            }
        }
        finally
        {
            CompleteOperation(ActiveOperation.Serialized);
        }
    }

    public ValueTask<BrowserResult<BrowserSessionState>> NavigateAsync(
        BrowserAddress address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        return ExecuteAsync(
            (renderer, token) => renderer.NavigateAsync(address, token),
            cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserSessionState>> GoBackAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            static (renderer, token) => renderer.GoBackAsync(token),
            cancellationToken);

    public ValueTask<BrowserResult<BrowserSessionState>> GoForwardAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            static (renderer, token) => renderer.GoForwardAsync(token),
            cancellationToken);

    public ValueTask<BrowserResult<BrowserSessionState>> ReloadAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            static (renderer, token) => renderer.ReloadAsync(token),
            cancellationToken);

    public async ValueTask<BrowserResult<BrowserSessionState>> StopAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            bool ownsOperationGate;
            try
            {
                ownsOperationGate = await _operationGate
                    .WaitAsync(TimeSpan.Zero, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Cancelled();
            }

            if (ownsOperationGate)
            {
                BeginOperation(ActiveOperation.Serialized);
                try
                {
                    return await ExecuteWithCurrentRendererAsync(
                            static (renderer, token) => renderer.StopAsync(token),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CompleteOperation(ActiveOperation.Serialized);
                }
            }

            var governedInterruption = ReserveGovernedInterruption();
            if (governedInterruption is not null)
            {
                TryCancelGovernedOperation(
                    governedInterruption.Cancellation);
                try
                {
                    var stopResult = await ExecuteWithRendererAsync(
                            governedInterruption.Renderer,
                            static (renderer, token) => renderer.StopAsync(token),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return stopResult.Error?.Code
                            == BrowserErrorCode.RendererUnavailable
                        ? BrowserResult<BrowserSessionState>.Success(State)
                        : stopResult;
                }
                finally
                {
                    CompleteGovernedInterruption();
                }
            }

            if (GetActiveOperation() == ActiveOperation.Serialized)
            {
                return await ExecuteAsync(
                        static (renderer, token) => renderer.StopAsync(token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // The semaphore owner classifies itself immediately after
            // acquisition. Yield across that narrow hand-off before deciding
            // whether this stop is an allowed governed interruption.
            await Task.Yield();
        }
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
        if (!CapabilityProfile.Supports(
                SessionCapabilities.BrowserOriginGuard))
        {
            return ValueTask.FromResult(
                UnsupportedCapability<BrowserSessionState>(
                    SessionCapabilities.BrowserOriginGuard));
        }

        return ExecuteGovernedNavigationAsync(
            (renderer, token) => ExecuteOriginConstrainedNavigationAsync(
                renderer,
                request,
                allowedOrigin,
                startBinding,
                token),
            cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserDocumentSnapshot>>
        CaptureSnapshotAsync(
            BrowserDocumentBinding document,
            CancellationToken cancellationToken,
            BrowserSnapshotQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        query ??= BrowserSnapshotQuery.Lean;
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserSnapshot))
        {
            return ValueTask.FromResult(
                UnsupportedCapability<BrowserDocumentSnapshot>(
                    SessionCapabilities.BrowserSnapshot));
        }

        return ExecuteDocumentSnapshotAsync(
            document,
            query,
            cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserClickReceipt>>
        ClickWithinOriginAsync(
            BrowserElementReference reference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserClick))
        {
            return ValueTask.FromResult(
                UnsupportedCapability<BrowserClickReceipt>(
                    SessionCapabilities.BrowserClick));
        }

        return ExecuteGovernedElementClickAsync(
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
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserFill))
        {
            return ValueTask.FromResult(
                UnsupportedCapability<BrowserFillReceipt>(
                    SessionCapabilities.BrowserFill));
        }

        return ExecuteGovernedElementFillAsync(
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
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(allowedOrigin);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserCheck))
        {
            return ValueTask.FromResult(
                UnsupportedCapability<BrowserCheckReceipt>(
                    SessionCapabilities.BrowserCheck));
        }

        return ExecuteGovernedElementCheckAsync(
            reference,
            allowedOrigin,
            cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserElementStateSnapshot>>
        ReadElementStateAsync(
            BrowserElementReference reference,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserWait))
        {
            return ValueTask.FromResult(
                UnsupportedCapability<BrowserElementStateSnapshot>(
                    SessionCapabilities.BrowserWait));
        }

        return ExecuteElementStateReadAsync(reference, cancellationToken);
    }

    public ValueTask BeginNetworkActivityObservationAsync(
        CancellationToken cancellationToken)
    {
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserWait))
        {
            return ValueTask.CompletedTask;
        }

        return ExecuteNetworkActivityObservationTransitionAsync(
            NetworkActivityObservationTransition.Begin,
            cancellationToken);
    }

    public ValueTask EndNetworkActivityObservationAsync(
        CancellationToken cancellationToken)
    {
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserWait))
        {
            return ValueTask.CompletedTask;
        }

        return ExecuteNetworkActivityObservationTransitionAsync(
            NetworkActivityObservationTransition.End,
            cancellationToken);
    }

    public ValueTask<BrowserResult<BrowserNetworkActivitySnapshot>>
        ReadNetworkActivityAsync(CancellationToken cancellationToken)
    {
        if (!CapabilityProfile.Supports(SessionCapabilities.BrowserWait))
        {
            return ValueTask.FromResult(
                UnsupportedCapability<BrowserNetworkActivitySnapshot>(
                    SessionCapabilities.BrowserWait));
        }

        return ExecuteNetworkActivityReadAsync(cancellationToken);
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(SnapshotUnsafe());
        }
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            PanelSessionEvent[] pending;
            Task waitForChange;
            bool completed;
            lock (_gate)
            {
                pending = [.. _events.Where(sessionEvent => sessionEvent.Sequence > afterSequence)];
                completed = _closed;
                waitForChange = _eventsChanged.Task;
            }

            foreach (var sessionEvent in pending)
            {
                afterSequence = sessionEvent.Sequence;
                yield return sessionEvent;
            }

            if (completed)
            {
                yield break;
            }

            await waitForChange
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PanelCloseOutcome.Cancelled;
        }

        BeginOperation(ActiveOperation.Serialized);
        try
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return PanelCloseOutcome.AlreadyClosed;
                }

                CloseUnsafe();
                return mode == PanelCloseMode.Force
                    ? PanelCloseOutcome.ForceTerminated
                    : PanelCloseOutcome.GracefullyClosed;
            }
        }
        finally
        {
            CompleteOperation(ActiveOperation.Serialized);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        BeginOperation(ActiveOperation.Serialized);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (!_closed)
                {
                    CloseUnsafe();
                }
            }
        }
        finally
        {
            CompleteOperation(ActiveOperation.Serialized);
        }
    }

    private async ValueTask<BrowserResult<BrowserSessionState>> ExecuteAsync(
        Func<
            IBrowserRenderer,
            CancellationToken,
            ValueTask<BrowserResult<BrowserSessionState>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }

        BeginOperation(ActiveOperation.Serialized);
        try
        {
            return await ExecuteWithCurrentRendererAsync(
                    operation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CompleteOperation(ActiveOperation.Serialized);
        }
    }

    private async ValueTask<BrowserResult<BrowserDocumentSnapshot>>
        ExecuteDocumentSnapshotAsync(
            BrowserDocumentBinding logicalDocument,
            BrowserSnapshotQuery query,
            CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SnapshotCancelled();
        }

        BeginOperation(ActiveOperation.Serialized);
        try
        {
            IBrowserRenderer renderer;
            BrowserDocumentBinding rendererDocument;
            lock (_gate)
            {
                if (_closed || _disposed)
                {
                    return SnapshotSessionClosed();
                }

                if (_renderer is null)
                {
                    return SnapshotRendererUnavailable();
                }

                renderer = _renderer;
                var rendererState = renderer.State;
                if (!logicalDocument.Matches(_state)
                    || _rendererStateAtLastProjection != rendererState
                    || rendererState.Address != _state.Address)
                {
                    return SnapshotStateChanged();
                }

                rendererDocument =
                    BrowserDocumentBinding.FromState(rendererState);
            }

            BrowserResult<BrowserDocumentSnapshot> result;
            try
            {
                result = await renderer.CaptureSnapshotAsync(
                        rendererDocument,
                        cancellationToken,
                        query)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return SnapshotCancelled();
            }
            catch (Exception)
            {
                return BrowserResult<BrowserDocumentSnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.EngineFailed,
                        "The browser renderer failed.",
                        retryable: true));
            }

            if (!result.IsSuccess)
            {
                return result;
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_renderer, renderer)
                    || !logicalDocument.Matches(_state)
                    || _rendererStateAtLastProjection != renderer.State
                    || !rendererDocument.Matches(renderer.State))
                {
                    return SnapshotStateChanged();
                }
            }

            try
            {
                return BrowserResult<BrowserDocumentSnapshot>.Success(
                    TranslateDocumentSnapshot(
                        result.Value!,
                        rendererDocument,
                        logicalDocument));
            }
            catch (ArgumentException)
            {
                return BrowserResult<BrowserDocumentSnapshot>.Failure(
                    BrowserError.Create(
                        BrowserErrorCode.SnapshotInvalid,
                        "The browser returned an invalid document snapshot."));
            }
        }
        finally
        {
            CompleteOperation(ActiveOperation.Serialized);
        }
    }

    private async ValueTask<BrowserResult<BrowserElementStateSnapshot>>
        ExecuteElementStateReadAsync(
            BrowserElementReference logicalReference,
            CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WaitObservationCancelled<BrowserElementStateSnapshot>();
        }

        BeginOperation(ActiveOperation.Serialized);
        try
        {
            IBrowserRenderer renderer;
            BrowserDocumentBinding rendererDocument;
            lock (_gate)
            {
                if (_closed || _disposed)
                {
                    return WaitObservationSessionClosed<
                        BrowserElementStateSnapshot>();
                }

                if (_renderer is null)
                {
                    return WaitObservationRendererUnavailable<
                        BrowserElementStateSnapshot>();
                }

                renderer = _renderer;
                var rendererState = renderer.State;
                if (!logicalReference.Document.Matches(_state)
                    || _rendererStateAtLastProjection != rendererState
                    || rendererState.Address != _state.Address)
                {
                    return WaitObservationStateChanged<
                        BrowserElementStateSnapshot>();
                }

                rendererDocument = BrowserDocumentBinding.FromState(
                    rendererState);
            }

            var rendererReference = new BrowserElementReference(
                logicalReference.Id,
                rendererDocument);
            BrowserResult<BrowserElementStateSnapshot> result;
            try
            {
                result = await renderer.ReadElementStateAsync(
                        rendererReference,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return WaitObservationCancelled<BrowserElementStateSnapshot>();
            }
            catch (Exception)
            {
                return WaitObservationRendererUnavailable<
                    BrowserElementStateSnapshot>();
            }

            if (!result.IsSuccess)
            {
                return result;
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_renderer, renderer)
                    || !logicalReference.Document.Matches(_state)
                    || _rendererStateAtLastProjection != renderer.State
                    || !rendererDocument.Matches(renderer.State))
                {
                    return WaitObservationStateChanged<
                        BrowserElementStateSnapshot>();
                }
            }

            var value = result.Value!;
            if (value.Document != rendererDocument)
            {
                return WaitObservationStateChanged<
                    BrowserElementStateSnapshot>();
            }

            return BrowserResult<BrowserElementStateSnapshot>.Success(
                value with { Document = logicalReference.Document });
        }
        finally
        {
            CompleteOperation(ActiveOperation.Serialized);
        }
    }

    private async ValueTask<BrowserResult<BrowserNetworkActivitySnapshot>>
        ExecuteNetworkActivityReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WaitObservationCancelled<BrowserNetworkActivitySnapshot>();
        }

        BeginOperation(ActiveOperation.Serialized);
        try
        {
            IBrowserRenderer renderer;
            lock (_gate)
            {
                if (_closed || _disposed)
                {
                    return WaitObservationSessionClosed<
                        BrowserNetworkActivitySnapshot>();
                }

                if (_renderer is null)
                {
                    return WaitObservationRendererUnavailable<
                        BrowserNetworkActivitySnapshot>();
                }

                renderer = _renderer;
            }

            try
            {
                return await renderer.ReadNetworkActivityAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return WaitObservationCancelled<
                    BrowserNetworkActivitySnapshot>();
            }
            catch (Exception)
            {
                return WaitObservationRendererUnavailable<
                    BrowserNetworkActivitySnapshot>();
            }
        }
        finally
        {
            CompleteOperation(ActiveOperation.Serialized);
        }
    }

    private async ValueTask ExecuteNetworkActivityObservationTransitionAsync(
        NetworkActivityObservationTransition transition,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        BeginOperation(ActiveOperation.Serialized);
        try
        {
            IBrowserRenderer? renderer;
            if (transition == NetworkActivityObservationTransition.Begin)
            {
                lock (_gate)
                {
                    renderer = _closed || _disposed ? null : _renderer;
                }

                if (renderer is null)
                {
                    return;
                }

                await renderer.BeginNetworkActivityObservationAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    _networkObservationRenderers.Push(renderer);
                }

                return;
            }

            lock (_gate)
            {
                renderer = _networkObservationRenderers.Count == 0
                    ? null
                    : _networkObservationRenderers.Pop();
            }

            if (renderer is null)
            {
                return;
            }

            await renderer.EndNetworkActivityObservationAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CompleteOperation(ActiveOperation.Serialized);
        }
    }

    private static BrowserDocumentSnapshot TranslateDocumentSnapshot(
        BrowserDocumentSnapshot snapshot,
        BrowserDocumentBinding rendererDocument,
        BrowserDocumentBinding logicalDocument)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Document != rendererDocument)
        {
            throw new ArgumentException(
                "The browser snapshot does not match its renderer document.",
                nameof(snapshot));
        }

        var nodes = new BrowserSnapshotNode[snapshot.Nodes.Count];
        for (var index = 0; index < nodes.Length; index++)
        {
            var source = snapshot.Nodes[index];
            var reference = source.Reference is null
                ? null
                : new BrowserElementReference(
                    source.Reference.Value,
                    logicalDocument);
            nodes[index] = new BrowserSnapshotNode(
                source.Depth,
                source.Role,
                source.Name,
                reference,
                source.States);
        }

        return new BrowserDocumentSnapshot(
            logicalDocument,
            nodes,
            snapshot.CapturedAtUtc,
            snapshot.IsTruncated);
    }

    private async ValueTask<BrowserResult<BrowserClickReceipt>>
        ExecuteGovernedElementClickAsync(
            BrowserElementReference logicalReference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ClickCancelled();
        }

        using var governedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        BeginGovernedOperation(governedCancellation);
        try
        {
            IBrowserRenderer renderer;
            BrowserDocumentBinding rendererDocument;
            BrowserDocumentBinding logicalDocument;
            lock (_gate)
            {
                if (_closed || _disposed)
                {
                    return ClickSessionClosed();
                }

                if (_renderer is null)
                {
                    return ClickRendererUnavailable();
                }

                renderer = _renderer;
                logicalDocument = logicalReference.Document;
                var rendererState = renderer.State;
                if (!logicalDocument.Matches(_state)
                    || _rendererStateAtLastProjection != rendererState
                    || rendererState.Address != _state.Address)
                {
                    return ClickStateChanged();
                }

                rendererDocument =
                    BrowserDocumentBinding.FromState(rendererState);
            }

            var rendererReference = new BrowserElementReference(
                logicalReference.Id,
                rendererDocument);
            BrowserResult<BrowserClickReceipt> result;
            try
            {
                result = await renderer.ClickWithinOriginAsync(
                        rendererReference,
                        allowedOrigin,
                        governedCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ClickOutcomeUnknown();
            }
            catch (Exception)
            {
                return ClickOutcomeUnknown();
            }

            if (!result.IsSuccess)
            {
                return result;
            }

            if (result.Value!.SourceDocument != rendererDocument)
            {
                return ClickOutcomeUnknown();
            }

            ApplyRendererState(renderer, renderer.State);
            lock (_gate)
            {
                if (!ReferenceEquals(_renderer, renderer)
                    || _rendererStateAtLastProjection is null)
                {
                    return ClickOutcomeUnknown();
                }
            }

            return BrowserResult<BrowserClickReceipt>.Success(
                new BrowserClickReceipt(logicalDocument));
        }
        finally
        {
            await CompleteGovernedOperationAsync()
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<BrowserResult<BrowserFillReceipt>>
        ExecuteGovernedElementFillAsync(
            BrowserElementReference logicalReference,
            string text,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FillCancelled();
        }

        using var governedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        BeginGovernedOperation(governedCancellation);
        try
        {
            IBrowserRenderer renderer;
            BrowserDocumentBinding rendererDocument;
            BrowserDocumentBinding logicalDocument;
            lock (_gate)
            {
                if (_closed || _disposed)
                {
                    return FillSessionClosed();
                }

                if (_renderer is null)
                {
                    return FillRendererUnavailable();
                }

                renderer = _renderer;
                logicalDocument = logicalReference.Document;
                var rendererState = renderer.State;
                if (!logicalDocument.Matches(_state)
                    || _rendererStateAtLastProjection != rendererState
                    || rendererState.Address != _state.Address)
                {
                    return FillStateChanged();
                }

                rendererDocument =
                    BrowserDocumentBinding.FromState(rendererState);
            }

            var rendererReference = new BrowserElementReference(
                logicalReference.Id,
                rendererDocument);
            BrowserResult<BrowserFillReceipt> result;
            try
            {
                result = await renderer.FillWithinOriginAsync(
                        rendererReference,
                        text,
                        allowedOrigin,
                        governedCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return FillOutcomeUnknown();
            }
            catch (Exception)
            {
                return FillOutcomeUnknown();
            }

            if (!result.IsSuccess)
            {
                return result;
            }

            if (result.Value!.SourceDocument != rendererDocument)
            {
                return FillOutcomeUnknown();
            }

            ApplyRendererState(renderer, renderer.State);
            lock (_gate)
            {
                if (!ReferenceEquals(_renderer, renderer)
                    || _rendererStateAtLastProjection is null)
                {
                    return FillOutcomeUnknown();
                }
            }

            return BrowserResult<BrowserFillReceipt>.Success(
                new BrowserFillReceipt(logicalDocument));
        }
        finally
        {
            await CompleteGovernedOperationAsync()
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<BrowserResult<BrowserCheckReceipt>>
        ExecuteGovernedElementCheckAsync(
            BrowserElementReference logicalReference,
            BrowserNavigationOrigin allowedOrigin,
            CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CheckCancelled();
        }

        using var governedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        BeginGovernedOperation(governedCancellation);
        try
        {
            IBrowserRenderer renderer;
            BrowserDocumentBinding rendererDocument;
            BrowserDocumentBinding logicalDocument;
            lock (_gate)
            {
                if (_closed || _disposed)
                {
                    return CheckSessionClosed();
                }

                if (_renderer is null)
                {
                    return CheckRendererUnavailable();
                }

                renderer = _renderer;
                logicalDocument = logicalReference.Document;
                var rendererState = renderer.State;
                if (!logicalDocument.Matches(_state)
                    || _rendererStateAtLastProjection != rendererState
                    || rendererState.Address != _state.Address)
                {
                    return CheckStateChanged();
                }

                rendererDocument =
                    BrowserDocumentBinding.FromState(rendererState);
            }

            var rendererReference = new BrowserElementReference(
                logicalReference.Id,
                rendererDocument);
            BrowserResult<BrowserCheckReceipt> result;
            try
            {
                result = await renderer.CheckWithinOriginAsync(
                        rendererReference,
                        allowedOrigin,
                        governedCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return CheckOutcomeUnknown();
            }
            catch (Exception)
            {
                return CheckOutcomeUnknown();
            }

            if (!result.IsSuccess)
            {
                return result;
            }

            if (result.Value!.SourceDocument != rendererDocument)
            {
                return CheckOutcomeUnknown();
            }

            ApplyRendererState(renderer, renderer.State);
            lock (_gate)
            {
                if (!ReferenceEquals(_renderer, renderer)
                    || _rendererStateAtLastProjection is null)
                {
                    return CheckOutcomeUnknown();
                }
            }

            return BrowserResult<BrowserCheckReceipt>.Success(
                new BrowserCheckReceipt(logicalDocument));
        }
        finally
        {
            await CompleteGovernedOperationAsync()
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<BrowserResult<BrowserSessionState>>
        ExecuteGovernedNavigationAsync(
            Func<
                IBrowserRenderer,
                CancellationToken,
                ValueTask<BrowserResult<BrowserSessionState>>> operation,
            CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }

        using var governedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        BeginGovernedOperation(governedCancellation);
        try
        {
            return await ExecuteWithCurrentRendererAsync(
                    operation,
                    governedCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            await CompleteGovernedOperationAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<BrowserResult<BrowserSessionState>>
        ExecuteWithCurrentRendererAsync(
            Func<
                IBrowserRenderer,
                CancellationToken,
                ValueTask<BrowserResult<BrowserSessionState>>> operation,
            CancellationToken cancellationToken)
    {
        IBrowserRenderer renderer;
        lock (_gate)
        {
            if (_closed || _disposed)
            {
                return SessionClosed();
            }

            if (_renderer is null)
            {
                return RendererUnavailable();
            }

            renderer = _renderer;
        }

        return await ExecuteWithRendererAsync(
                renderer,
                operation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<BrowserResult<BrowserSessionState>>
        ExecuteWithRendererAsync(
            IBrowserRenderer renderer,
            Func<
                IBrowserRenderer,
                CancellationToken,
                ValueTask<BrowserResult<BrowserSessionState>>> operation,
            CancellationToken cancellationToken)
    {
        BrowserResult<BrowserSessionState> result;
        try
        {
            result = await operation(renderer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception)
        {
            var error = BrowserError.Create(
                BrowserErrorCode.EngineFailed,
                "The browser renderer failed.",
                retryable: true);
            ApplyFailure(renderer, error);
            return BrowserResult<BrowserSessionState>.Failure(error);
        }

        if (!result.IsSuccess)
        {
            return result;
        }

        ApplyRendererState(renderer, result.Value!);
        return BrowserResult<BrowserSessionState>.Success(State);
    }

    private ValueTask<BrowserResult<BrowserSessionState>>
        ExecuteOriginConstrainedNavigationAsync(
            IBrowserRenderer renderer,
            BrowserOriginConstrainedNavigationRequest request,
            BrowserNavigationOrigin allowedOrigin,
            BrowserNavigationStartBinding logicalStartBinding,
            CancellationToken cancellationToken)
    {
        BrowserNavigationStartBinding rendererStartBinding;
        lock (_gate)
        {
            var rendererState = renderer.State;
            if (!ReferenceEquals(_renderer, renderer)
                || !logicalStartBinding.Matches(_state)
                || _rendererStateAtLastProjection != rendererState
                || rendererState.Address != _state.Address)
            {
                return ValueTask.FromResult(NavigationStateChanged());
            }

            rendererStartBinding =
                BrowserNavigationStartBinding.FromState(rendererState);
        }

        return renderer.NavigateWithinOriginAsync(
            request,
            allowedOrigin,
            rendererStartBinding,
            cancellationToken);
    }

    private void BeginOperation(ActiveOperation operation)
    {
        lock (_gate)
        {
            if (_activeOperation != ActiveOperation.None)
            {
                throw new InvalidOperationException(
                    "The browser session operation gate is already owned.");
            }

            _activeOperation = operation;
        }
    }

    private void CompleteOperation(ActiveOperation operation)
    {
        lock (_gate)
        {
            if (_activeOperation != operation)
            {
                throw new InvalidOperationException(
                    "The browser session operation gate owner changed unexpectedly.");
            }

            _activeOperation = ActiveOperation.None;
            _operationGate.Release();
        }
    }

    private ActiveOperation GetActiveOperation()
    {
        lock (_gate)
        {
            return _activeOperation;
        }
    }

    private void BeginGovernedOperation(
        CancellationTokenSource governedCancellation)
    {
        ArgumentNullException.ThrowIfNull(governedCancellation);
        lock (_gate)
        {
            if (_activeOperation != ActiveOperation.None
                || _governedOperationCancellation is not null)
            {
                throw new InvalidOperationException(
                    "The browser session operation gate is already owned.");
            }

            _activeOperation = ActiveOperation.GovernedNavigation;
            _governedOperationCancellation = governedCancellation;
        }
    }

    private GovernedInterruption? ReserveGovernedInterruption()
    {
        lock (_gate)
        {
            if (_activeOperation != ActiveOperation.GovernedNavigation
                || _renderer is null
                || _governedOperationCancellation is null)
            {
                return null;
            }

            if (_governedInterruptionsInFlight == 0)
            {
                _governedInterruptionsDrained = NewSignal();
            }

            _governedInterruptionsInFlight++;
            return new GovernedInterruption(
                _renderer,
                _governedOperationCancellation);
        }
    }

    private static void TryCancelGovernedOperation(
        CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The reservation keeps disposal behind interruption completion,
            // but a defensive stop still continues if a custom token source
            // violates that lifetime.
        }
        catch (AggregateException)
        {
            // Renderer cancellation callbacks cannot prevent the explicit
            // stop from reaching the native boundary.
        }
    }

    private void CompleteGovernedInterruption()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            if (_governedInterruptionsInFlight <= 0)
            {
                throw new InvalidOperationException(
                    "No governed browser interruption is active.");
            }

            _governedInterruptionsInFlight--;
            if (_governedInterruptionsInFlight == 0)
            {
                drained = _governedInterruptionsDrained;
                _governedInterruptionsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private async ValueTask CompleteGovernedOperationAsync()
    {
        while (true)
        {
            Task? interruptionsDrained;
            lock (_gate)
            {
                if (_activeOperation != ActiveOperation.GovernedNavigation)
                {
                    throw new InvalidOperationException(
                        "The governed browser operation gate owner changed unexpectedly.");
                }

                if (_governedInterruptionsInFlight == 0)
                {
                    _governedOperationCancellation = null;
                    _activeOperation = ActiveOperation.None;
                    _operationGate.Release();
                    return;
                }

                interruptionsDrained = _governedInterruptionsDrained?.Task;
            }

            if (interruptionsDrained is null)
            {
                throw new InvalidOperationException(
                    "The governed browser interruption signal is unavailable.");
            }

            await interruptionsDrained.ConfigureAwait(false);
        }
    }

    private void OnRendererStateChanged(
        object? sender,
        BrowserStateChangedEventArgs args)
    {
        if (sender is IBrowserRenderer renderer)
        {
            ApplyRendererState(renderer, args.State);
        }
    }

    private void ApplyRendererState(
        IBrowserRenderer renderer,
        BrowserSessionState rendererState)
    {
        lock (_gate)
        {
            if (_closed || !ReferenceEquals(_renderer, renderer))
            {
                return;
            }

            if (rendererState.DocumentRevision < _rendererRevisionBaseline)
            {
                RejectRendererRevisionUnsafe(
                    renderer,
                    "The browser renderer regressed its document revision.");
                return;
            }

            var relativeRevision =
                rendererState.DocumentRevision - _rendererRevisionBaseline;
            if (relativeRevision > long.MaxValue - _logicalRevisionAtAttach)
            {
                RejectRendererRevisionUnsafe(
                    renderer,
                    "The browser renderer reported an invalid document revision.");
                return;
            }

            var candidateRevision = _logicalRevisionAtAttach + relativeRevision;
            var logicalRevision = Math.Max(_state.DocumentRevision, candidateRevision);
            var state = new BrowserSessionState(
                rendererState.Address,
                rendererState.Title,
                rendererState.LoadState,
                rendererState.CanGoBack,
                rendererState.CanGoForward,
                logicalRevision,
                rendererState.Failure,
                rendererState.Viewport,
                rendererState.ViewportRevision,
                rendererState.InputEpoch);
            _rendererStateAtLastProjection = rendererState;
            if (_state == state)
            {
                return;
            }

            _state = state;
            PublishStateUnsafe();
        }
    }

    private void RejectRendererRevisionUnsafe(
        IBrowserRenderer renderer,
        string message)
    {
        if (renderer is IBrowserElementReferenceRegistry registry)
        {
            registry.InvalidateElementReferences();
        }

        _rendererStateAtLastProjection = null;
        var error = BrowserError.Create(
            BrowserErrorCode.EngineFailed,
            message);
        _state = new BrowserSessionState(
            _state.Address,
            string.Empty,
            BrowserLoadState.Failed,
            _state.CanGoBack,
            _state.CanGoForward,
            _state.DocumentRevision,
            error,
            _state.Viewport,
            _state.ViewportRevision,
            _state.InputEpoch);
        PublishStateUnsafe();
    }

    private void ApplyFailure(IBrowserRenderer renderer, BrowserError error)
    {
        lock (_gate)
        {
            if (_closed || !ReferenceEquals(_renderer, renderer))
            {
                return;
            }

            _state = new BrowserSessionState(
                _state.Address,
                string.Empty,
                BrowserLoadState.Failed,
                _state.CanGoBack,
                _state.CanGoForward,
                _state.DocumentRevision,
                error,
                _state.Viewport,
                _state.ViewportRevision,
                _state.InputEpoch);
            PublishStateUnsafe();
        }
    }

    private void DetachAfterFailedAttachment(IBrowserRenderer renderer)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_renderer, renderer))
            {
                DetachRendererUnsafe(rememberRenderer: false);
            }
        }
    }

    private void DetachRendererUnsafe(bool rememberRenderer)
    {
        if (_renderer is null)
        {
            return;
        }

        _renderer.StateChanged -= OnRendererStateChanged;
        if (_renderer is IBrowserElementReferenceRegistry registry)
        {
            registry.InvalidateElementReferences();
        }

        if (_renderer is IBrowserPhysicalInputBarrier inputBarrier)
        {
            inputBarrier.BindPhysicalInputGate(null);
        }

        _lastDetachedRenderer = rememberRenderer ? _renderer : null;
        _lastDetachedRendererStateAtProjection = rememberRenderer
            ? _rendererStateAtLastProjection
            : null;
        _renderer = null;
        _rendererStateAtLastProjection = null;
        PublishUnsafe(
            SessionLifecycle.Active,
            SessionHealth.Unavailable,
            "Browser renderer detached; session remains open.");
    }

    private void CloseUnsafe()
    {
        if (_renderer is not null)
        {
            _renderer.StateChanged -= OnRendererStateChanged;
            if (_renderer is IBrowserElementReferenceRegistry registry)
            {
                registry.InvalidateElementReferences();
            }

            if (_renderer is IBrowserPhysicalInputBarrier inputBarrier)
            {
                inputBarrier.BindPhysicalInputGate(null);
            }

            _renderer = null;
            _rendererStateAtLastProjection = null;
        }

        _lastDetachedRenderer = null;
        _lastDetachedRendererStateAtProjection = null;
        _closed = true;
        PublishUnsafe(
            SessionLifecycle.Closed,
            SessionHealth.Ended,
            "Browser session closed.");
    }

    private PanelSessionSnapshot SnapshotUnsafe()
    {
        if (_closed)
        {
            return new PanelSessionSnapshot(
                SessionLifecycle.Closed,
                SessionHealth.Ended,
                false,
                "The browser session is closed.");
        }

        if (_renderer is null)
        {
            return new PanelSessionSnapshot(
                _hasEverAttached
                    ? SessionLifecycle.Active
                    : SessionLifecycle.Starting,
                _hasEverAttached
                    ? SessionHealth.Unavailable
                    : SessionHealth.Starting,
                false,
                _hasEverAttached
                    ? "Browser renderer detached; session remains open."
                    : "Waiting for a browser renderer attachment.");
        }

        return _state.LoadState switch
        {
            BrowserLoadState.Loading => new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                false,
                "The browser is loading a page."),
            BrowserLoadState.Failed => new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Degraded,
                false,
                _state.Failure!.Message,
                new SessionFailure(
                    _state.Failure.StableCode,
                    _state.Failure.Message,
                    _state.Failure.Retryable)),
            BrowserLoadState.Ready => new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                false,
                "The browser renderer is ready."),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private void PublishStateUnsafe()
    {
        var snapshot = SnapshotUnsafe();
        PublishUnsafe(snapshot.Lifecycle, snapshot.Health, snapshot.StatusDetail);
    }

    private void Publish(
        SessionLifecycle lifecycle,
        SessionHealth health,
        string detail)
    {
        lock (_gate)
        {
            PublishUnsafe(lifecycle, health, detail);
        }
    }

    private void PublishUnsafe(
        SessionLifecycle lifecycle,
        SessionHealth health,
        string detail)
    {
        _sequence++;
        _events.Add(new PanelSessionEvent(
            _sequence,
            lifecycle,
            health,
            _timeProvider.GetUtcNow(),
            detail));
        if (_events.Count > MaximumRetainedEvents)
        {
            _events.RemoveRange(0, _events.Count - MaximumRetainedEvents);
        }

        var changed = _eventsChanged;
        _eventsChanged = NewSignal();
        changed.TrySetResult();
    }

    private void EnsureExactCapabilities(IBrowserRenderer renderer)
    {
        if (!Capabilities.Values.SequenceEqual(
                renderer.Capabilities.Values,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The browser renderer capability profile does not match the session.");
        }

        if (Capabilities.Contains(
                SessionCapabilities.BrowserAgentInputBarrier)
            && renderer is not IBrowserPhysicalInputBarrier)
        {
            throw new InvalidOperationException(
                "The browser renderer advertises no enforceable physical input barrier.");
        }
    }

    private static BrowserResult<T> UnsupportedCapability<T>(
        string capability) =>
        BrowserResult<T>.Failure(
            BrowserError.Create(
                BrowserErrorCode.UnsupportedCapability,
                $"The browser capability '{capability}' is not enabled for this session."));

    private static BrowserResult<T> WaitObservationRendererUnavailable<T>() =>
        BrowserResult<T>.Failure(
            BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                "The browser renderer is detached.",
                retryable: true));

    private static BrowserResult<T> WaitObservationSessionClosed<T>() =>
        BrowserResult<T>.Failure(
            BrowserError.Create(
                BrowserErrorCode.SessionClosed,
                "The browser session is closed."));

    private static BrowserResult<T> WaitObservationCancelled<T>() =>
        BrowserResult<T>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser wait observation was cancelled."));

    private static BrowserResult<T> WaitObservationStateChanged<T>() =>
        BrowserResult<T>.Failure(
            BrowserError.Create(
                BrowserErrorCode.NavigationStateChanged,
                "The browser document changed while its wait condition was observed.",
                retryable: true));

    private static BrowserResult<BrowserSessionState> RendererUnavailable() =>
        BrowserResult<BrowserSessionState>.Failure(
            BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                "The browser renderer is detached.",
                retryable: true));

    private static BrowserResult<BrowserSessionState> SessionClosed() =>
        BrowserResult<BrowserSessionState>.Failure(
            BrowserError.Create(
                BrowserErrorCode.SessionClosed,
                "The browser session is closed."));

    private static BrowserResult<BrowserSessionState> Cancelled() =>
        BrowserResult<BrowserSessionState>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser operation was cancelled."));

    private static BrowserResult<BrowserSessionState> NavigationStateChanged() =>
        BrowserResult<BrowserSessionState>.Failure(
            BrowserError.Create(
                BrowserErrorCode.NavigationStateChanged,
                "The browser document changed after navigation was authorized.",
                retryable: true));

    private static BrowserResult<BrowserDocumentSnapshot>
        SnapshotRendererUnavailable() =>
        BrowserResult<BrowserDocumentSnapshot>.Failure(
            BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                "The browser renderer is detached.",
                retryable: true));

    private static BrowserResult<BrowserDocumentSnapshot>
        SnapshotSessionClosed() =>
        BrowserResult<BrowserDocumentSnapshot>.Failure(
            BrowserError.Create(
                BrowserErrorCode.SessionClosed,
                "The browser session is closed."));

    private static BrowserResult<BrowserDocumentSnapshot>
        SnapshotCancelled() =>
        BrowserResult<BrowserDocumentSnapshot>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser snapshot was cancelled."));

    private static BrowserResult<BrowserDocumentSnapshot>
        SnapshotStateChanged() =>
        BrowserResult<BrowserDocumentSnapshot>.Failure(
            BrowserError.Create(
                BrowserErrorCode.NavigationStateChanged,
                "The browser document changed while its snapshot was captured.",
                retryable: true));

    private static BrowserResult<BrowserClickReceipt>
        ClickRendererUnavailable() =>
        BrowserResult<BrowserClickReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                "The browser renderer is detached.",
                retryable: true));

    private static BrowserResult<BrowserClickReceipt>
        ClickSessionClosed() =>
        BrowserResult<BrowserClickReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.SessionClosed,
                "The browser session is closed."));

    private static BrowserResult<BrowserClickReceipt>
        ClickCancelled() =>
        BrowserResult<BrowserClickReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser element activation was cancelled before dispatch."));

    private static BrowserResult<BrowserClickReceipt>
        ClickStateChanged() =>
        BrowserResult<BrowserClickReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.NavigationStateChanged,
                "The browser document changed before the referenced element could be activated.",
                retryable: true));

    private static BrowserResult<BrowserClickReceipt>
        ClickOutcomeUnknown() =>
        BrowserResult<BrowserClickReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.InteractionOutcomeUnknown,
                "The browser could not determine whether the element activation completed."));

    private static BrowserResult<BrowserFillReceipt>
        FillRendererUnavailable() =>
        BrowserResult<BrowserFillReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                "The browser renderer is detached.",
                retryable: true));

    private static BrowserResult<BrowserFillReceipt>
        FillSessionClosed() =>
        BrowserResult<BrowserFillReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.SessionClosed,
                "The browser session is closed."));

    private static BrowserResult<BrowserFillReceipt>
        FillCancelled() =>
        BrowserResult<BrowserFillReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser element fill was cancelled before dispatch."));

    private static BrowserResult<BrowserFillReceipt>
        FillStateChanged() =>
        BrowserResult<BrowserFillReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.NavigationStateChanged,
                "The browser document changed before the referenced element could be filled.",
                retryable: true));

    private static BrowserResult<BrowserFillReceipt>
        FillOutcomeUnknown() =>
        BrowserResult<BrowserFillReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.InteractionOutcomeUnknown,
                "The browser could not determine whether the element fill completed."));

    private static BrowserResult<BrowserCheckReceipt>
        CheckRendererUnavailable() =>
        BrowserResult<BrowserCheckReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                "The browser renderer is detached.",
                retryable: true));

    private static BrowserResult<BrowserCheckReceipt>
        CheckSessionClosed() =>
        BrowserResult<BrowserCheckReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.SessionClosed,
                "The browser session is closed."));

    private static BrowserResult<BrowserCheckReceipt>
        CheckCancelled() =>
        BrowserResult<BrowserCheckReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The browser element check was cancelled before dispatch."));

    private static BrowserResult<BrowserCheckReceipt>
        CheckStateChanged() =>
        BrowserResult<BrowserCheckReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.NavigationStateChanged,
                "The browser document changed before the referenced element could be checked.",
                retryable: true));

    private static BrowserResult<BrowserCheckReceipt>
        CheckOutcomeUnknown() =>
        BrowserResult<BrowserCheckReceipt>.Failure(
            BrowserError.Create(
                BrowserErrorCode.InteractionOutcomeUnknown,
                "The browser could not determine whether the element check completed."));

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private enum ActiveOperation
    {
        None,
        Serialized,
        GovernedNavigation,
    }

    private enum NetworkActivityObservationTransition
    {
        Begin,
        End,
    }

    private sealed record GovernedInterruption(
        IBrowserRenderer Renderer,
        CancellationTokenSource Cancellation);
}
