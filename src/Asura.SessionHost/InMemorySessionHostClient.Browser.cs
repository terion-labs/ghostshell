using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<SessionSnapshot>> EnsureBrowserSessionAsync(
        EnsureBrowserSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.InitialAddress);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();

        var fingerprint = Fingerprint(
            ApplicationOperations.BrowserOpen,
            request.SessionId.Value,
            request.Owner.PanelId.Value,
            request.InitialAddress.ToString());
        if (TryReplay(context, fingerprint, 0, out HostResult<SessionSnapshot>? replay))
        {
            return replay;
        }

        var invalid = ValidateContext<SessionSnapshot>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return invalid;
        }

        if (_browserPanelFactory is null)
        {
            return Unsupported<SessionSnapshot>(
                "This session host has no browser-panel session factory.",
                0);
        }

        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<SessionSnapshot>(0);
        }

        try
        {
            ThrowIfDisposed();
            if (WorkspaceGraphFailure<SessionSnapshot>(
                    _workspaceGraphs.ValidateSessionOwner(
                        request.Owner,
                        PanelKind.Browser)) is { } ownerFailure)
            {
                return ownerFailure;
            }

            if (TryReplay(
                    context,
                    fingerprint,
                    0,
                    out HostResult<SessionSnapshot>? inGateReplay))
            {
                return inGateReplay;
            }

            using var operationCancellation = HostedOperationCancellation.Create(
                context,
                cancellationToken,
                _timeProvider);
            if (operationCancellation.Token.IsCancellationRequested)
            {
                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<SessionSnapshot>(0)
                    : Cancelled<SessionSnapshot>(0);
            }

            if (TryGetSession(request.SessionId, out var existing))
            {
                var existingSnapshot = existing.Snapshot();
                if (existingSnapshot.Descriptor.Owner != request.Owner
                    || existing.Engine.Kind != PanelKind.Browser)
                {
                    return HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.InvalidRequest,
                            "The requested session ID already belongs to another panel or session kind."),
                        existingSnapshot.Descriptor.Revision);
                }

                if (existingSnapshot.Descriptor.Lifecycle is
                    SessionLifecycle.Closed or SessionLifecycle.Failed)
                {
                    return ClosedSession<SessionSnapshot>(
                        existingSnapshot.Descriptor.Revision);
                }

                var existingReservation = ReserveReplay<SessionSnapshot>(
                    context,
                    fingerprint,
                    existingSnapshot.Descriptor.Revision,
                    out var existingOutcomeReserved);
                if (existingReservation is not null)
                {
                    return existingReservation;
                }

                if (WorkspaceGraphFailure<SessionSnapshot>(
                        _workspaceGraphs.LinkSession(
                            request.Owner,
                            PanelKind.Browser,
                            request.SessionId)) is { } existingLinkFailure)
                {
                    return existingOutcomeReserved
                        ? OutcomeUncertain<SessionSnapshot>(
                            existingSnapshot.Descriptor.Revision)
                        : existingLinkFailure;
                }

                var existingResult = HostResult<SessionSnapshot>.Succeed(
                    existingSnapshot,
                    existingSnapshot.Descriptor.Revision);
                CompleteReplay(context, fingerprint, existingResult);
                return existingResult;
            }

            var reservationReplay = ReserveReplay<SessionSnapshot>(
                context,
                fingerprint,
                currentRevision: 0,
                out var outcomeReserved);
            if (reservationReplay is not null)
            {
                return reservationReplay;
            }

            IBrowserPanelSession? createdEngine = null;
            HostedSession hosted;
            try
            {
                createdEngine = await _browserPanelFactory
                    .CreateAsync(
                        request.SessionId,
                        request.InitialAddress,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
                if (!_browserPanelCapabilities.Values.SequenceEqual(
                        createdEngine.Capabilities.Values,
                        StringComparer.Ordinal))
                {
                    await DisposeUnownedSessionAsync(createdEngine)
                        .ConfigureAwait(false);
                    createdEngine = null;
                    var mismatch = HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.EngineFailed,
                            "The browser session capability profile does not match its factory."),
                        0);
                    return outcomeReserved
                        ? OutcomeUncertain<SessionSnapshot>(0)
                        : mismatch;
                }

                var engineSnapshot = await createdEngine
                    .SnapshotAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                hosted = new HostedSession(
                    createdEngine,
                    request.Owner,
                    request.Title,
                    engineSnapshot,
                    _eventRetention,
                    _timeProvider);
                lock (_gate)
                {
                    _sessions.Add(request.SessionId, hosted);
                }

                // Ownership transferred to the host graph. Later rejection
                // removes and disposes through RemoveRejectedSessionAsync.
                createdEngine = null;
            }
            catch (OperationCanceledException)
            {
                await DisposeUnownedSessionAsync(createdEngine).ConfigureAwait(false);
                return outcomeReserved
                    ? OutcomeUncertain<SessionSnapshot>(0)
                    : operationCancellation.DeadlineElapsed
                        ? DeadlineExceeded<SessionSnapshot>(0)
                        : Cancelled<SessionSnapshot>(0);
            }
            catch (Exception exception)
            {
                await DisposeUnownedSessionAsync(createdEngine).ConfigureAwait(false);
                return outcomeReserved
                    ? OutcomeUncertain<SessionSnapshot>(0)
                    : EngineFailure<SessionSnapshot>(exception, 0);
            }

            if (WorkspaceGraphFailure<SessionSnapshot>(
                    _workspaceGraphs.LinkSession(
                        request.Owner,
                        PanelKind.Browser,
                        request.SessionId)) is { } linkFailure)
            {
                var rejected = await RemoveRejectedSessionAsync(hosted, linkFailure)
                    .ConfigureAwait(false);
                return outcomeReserved
                    ? OutcomeUncertain<SessionSnapshot>(0)
                    : rejected;
            }

            var snapshot = hosted.Snapshot();
            var result = HostResult<SessionSnapshot>.Succeed(
                snapshot,
                snapshot.Descriptor.Revision);
            CompleteReplay(context, fingerprint, result);
            return result;
        }
        finally
        {
            _sessionGraphGate.Release();
        }
    }

    public async ValueTask<HostResult<Unit>> AttachBrowserRendererAsync(
        AttachBrowserRendererRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Renderer);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetSession(request.SessionId, out var session))
        {
            return NotFound<Unit>("session", 0);
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.IsInteractiveAttachmentOwner(request.AttachmentId, context.Actor))
        {
            return HostResult<Unit>.Fail(
                HostError.Create(
                    HostErrorCode.LeaseDenied,
                    "The browser renderer requires the exact interactive human attachment."),
                revision);
        }

        if (session.Engine.Kind != PanelKind.Browser
            || session.Engine is not IBrowserRendererAttachment rendererAttachment)
        {
            return Unsupported<Unit>("The session is not a browser.", revision);
        }

        if (request.Renderer is not IBrowserPhysicalInputBarrier inputBarrier
            || !request.Renderer.Capabilities.Contains(
                SessionCapabilities.BrowserAgentInputBarrier))
        {
            return Unsupported<Unit>(
                "The browser renderer cannot fence agent input from physical human input.",
                revision);
        }

        try
        {
            inputBarrier.BindPhysicalInputGate(
                _ => session.TryAcceptPhysicalInput(
                    request.AttachmentId,
                    context.Actor,
                    HumanPhysicalInputLeaseDuration));
            await rendererAttachment
                .AttachRendererAsync(request.Renderer, cancellationToken)
                .ConfigureAwait(false);
            var engineSnapshot = await session.Engine
                .SnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!session.ApplyEngineSnapshot(engineSnapshot))
            {
                session.RecordStateChange("Browser renderer attached.");
            }

            return HostResult<Unit>.Succeed(
                Unit.Value,
                session.Snapshot().Descriptor.Revision);
        }
        catch (OperationCanceledException)
        {
            inputBarrier.BindPhysicalInputGate(null);
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            inputBarrier.BindPhysicalInputGate(null);
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> ReadBrowserStateAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ExecuteBrowserOperationAsync(
            sessionId,
            ApplicationOperations.BrowserReadState,
            string.Empty,
            SessionCapabilities.BrowserReadState,
            context,
            cancellationToken,
            changesState: false,
            static (browser, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(
                    BrowserResult<BrowserSessionState>.Success(browser.State));
            });

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> NavigateBrowserAsync(
        BrowserNavigateRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Address);
        return ExecuteBrowserOperationAsync(
            request.SessionId,
            ApplicationOperations.BrowserNavigate,
            request.Address.ToString(),
            SessionCapabilities.BrowserNavigate,
            context,
            cancellationToken,
            changesState: true,
            (browser, token) => browser.NavigateAsync(request.Address, token));
    }

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> GoBackBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ExecuteBrowserOperationAsync(
            sessionId,
            ApplicationOperations.BrowserBack,
            string.Empty,
            SessionCapabilities.BrowserBack,
            context,
            cancellationToken,
            changesState: true,
            static (browser, token) => browser.GoBackAsync(token));

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> GoForwardBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ExecuteBrowserOperationAsync(
            sessionId,
            ApplicationOperations.BrowserForward,
            string.Empty,
            SessionCapabilities.BrowserForward,
            context,
            cancellationToken,
            changesState: true,
            static (browser, token) => browser.GoForwardAsync(token));

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> ReloadBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ExecuteBrowserOperationAsync(
            sessionId,
            ApplicationOperations.BrowserReload,
            string.Empty,
            SessionCapabilities.BrowserReload,
            context,
            cancellationToken,
            changesState: true,
            static (browser, token) => browser.ReloadAsync(token));

    public ValueTask<HostResult<BrowserResult<BrowserSessionState>>> StopBrowserAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        ExecuteBrowserOperationAsync(
            sessionId,
            ApplicationOperations.BrowserStop,
            string.Empty,
            SessionCapabilities.BrowserStop,
            context,
            cancellationToken,
            changesState: true,
            static (browser, token) => browser.StopAsync(token));

    private async ValueTask<HostResult<BrowserResult<BrowserSessionState>>>
        ExecuteBrowserOperationAsync(
            SessionId sessionId,
            string operationName,
            string operationKey,
            string requiredCapability,
            OperationContext context,
            CancellationToken cancellationToken,
            bool changesState,
            Func<
                IBrowserPanelSession,
                CancellationToken,
                ValueTask<BrowserResult<BrowserSessionState>>> operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        var useIdempotencyGate = changesState && context.IdempotencyKey is not null;
        if (useIdempotencyGate)
        {
            try
            {
                await _idempotentBrowserOperationGate
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Cancelled<BrowserResult<BrowserSessionState>>(
                    CurrentRevision(sessionId));
            }
        }

        try
        {
            if (!TryGetBrowserPanel(
                    sessionId,
                    out var session,
                    out var browser,
                    out HostResult<BrowserResult<BrowserSessionState>>? failure))
            {
                return failure;
            }

            var revision = session.Snapshot().Descriptor.Revision;
            var invalid = ValidateContext<BrowserResult<BrowserSessionState>>(
                context,
                cancellationToken,
                revision);
            if (invalid is not null)
            {
                return invalid;
            }

            if (RevisionConflict(
                    context,
                    session,
                    out HostResult<BrowserResult<BrowserSessionState>>? conflict))
            {
                return conflict;
            }

            if (context.Actor is not
                {
                    Kind: ActorKind.Human,
                    ClientId: { } clientId,
                }
                || !string.Equals(context.Actor.Id.Value, clientId.Value
, StringComparison.Ordinal) || !session.TryCaptureBrowserAttachmentAuthority(
                    clientId,
                    revision,
                    out _,
                    out var attachmentAuthority))
            {
                return HostResult<BrowserResult<BrowserSessionState>>.Fail(
                    HostError.Create(
                        HostErrorCode.LeaseDenied,
                        "Browser operations require the current interactive human attachment."),
                    revision);
            }

            var fingerprint = Fingerprint(
                operationName,
                sessionId.Value,
                operationKey);
            if (changesState
                && TryReplay(
                    context,
                    fingerprint,
                    revision,
                    out HostResult<BrowserResult<BrowserSessionState>>? replay))
            {
                return replay;
            }

            if (!browser.Capabilities.Contains(requiredCapability))
            {
                return Unsupported<BrowserResult<BrowserSessionState>>(
                    $"The browser engine does not support {operationName}.",
                    revision);
            }

            using var attachmentCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    attachmentAuthority);
            using var operationCancellation = HostedOperationCancellation.Create(
                context,
                attachmentCancellation.Token,
                _timeProvider);
            if (operationCancellation.Token.IsCancellationRequested)
            {
                if (attachmentAuthority.IsCancellationRequested)
                {
                    return HostResult<BrowserResult<BrowserSessionState>>.Fail(
                        new HostError(
                            HostErrorCode.Cancelled,
                            "attachment_revoked",
                            "The interactive browser attachment was revoked."),
                        revision);
                }

                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<BrowserResult<BrowserSessionState>>(revision)
                    : Cancelled<BrowserResult<BrowserSessionState>>(revision);
            }

            HostResult<BrowserResult<BrowserSessionState>>? reservationReplay = null;
            var outcomeReserved = false;
            if (changesState)
            {
                reservationReplay = ReserveReplay<BrowserResult<BrowserSessionState>>(
                    context,
                    fingerprint,
                    revision,
                    out outcomeReserved);
            }

            if (reservationReplay is not null)
            {
                return reservationReplay;
            }

            try
            {
                var browserResult = await operation(browser, operationCancellation.Token)
                    .ConfigureAwait(false);
                if (!browserResult.IsSuccess
                    && attachmentAuthority.IsCancellationRequested)
                {
                    if (outcomeReserved)
                    {
                        return OutcomeUncertain<BrowserResult<BrowserSessionState>>(revision);
                    }

                    return HostResult<BrowserResult<BrowserSessionState>>.Fail(
                        new HostError(
                            HostErrorCode.Cancelled,
                            "attachment_revoked",
                            "The interactive browser attachment was revoked."),
                        revision);
                }

                if (!browserResult.IsSuccess
                    && cancellationToken.IsCancellationRequested)
                {
                    return outcomeReserved
                        ? OutcomeUncertain<BrowserResult<BrowserSessionState>>(revision)
                        : Cancelled<BrowserResult<BrowserSessionState>>(revision);
                }

                if (!browserResult.IsSuccess
                    && operationCancellation.DeadlineElapsed)
                {
                    return outcomeReserved
                        ? OutcomeUncertain<BrowserResult<BrowserSessionState>>(revision)
                        : DeadlineExceeded<BrowserResult<BrowserSessionState>>(revision);
                }

                if (changesState && browserResult.IsSuccess)
                {
                    var engineSnapshot = await browser
                        .SnapshotAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!session.ApplyEngineSnapshot(engineSnapshot))
                    {
                        session.RecordStateChange($"{operationName} completed.");
                    }
                }

                var resultingRevision = session.Snapshot().Descriptor.Revision;
                var result =
                    HostResult<BrowserResult<BrowserSessionState>>.Succeed(
                        browserResult,
                        resultingRevision);
                if (changesState)
                {
                    CompleteReplay(context, fingerprint, result);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                if (outcomeReserved)
                {
                    return OutcomeUncertain<BrowserResult<BrowserSessionState>>(revision);
                }

                if (attachmentAuthority.IsCancellationRequested)
                {
                    return HostResult<BrowserResult<BrowserSessionState>>.Fail(
                        new HostError(
                            HostErrorCode.Cancelled,
                            "attachment_revoked",
                            "The interactive browser attachment was revoked."),
                        revision);
                }

                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<BrowserResult<BrowserSessionState>>(revision)
                    : Cancelled<BrowserResult<BrowserSessionState>>(revision);
            }
            catch (Exception exception)
            {
                return outcomeReserved
                    ? OutcomeUncertain<BrowserResult<BrowserSessionState>>(revision)
                    : EngineFailure<BrowserResult<BrowserSessionState>>(
                        exception,
                        revision);
            }
        }
        finally
        {
            if (useIdempotencyGate)
            {
                _idempotentBrowserOperationGate.Release();
            }
        }
    }

    private bool TryGetBrowserPanel<T>(
        SessionId sessionId,
        out HostedSession session,
        out IBrowserPanelSession browser,
        out HostResult<T> failure)
    {
        if (!TryGetSession(sessionId, out session))
        {
            browser = null!;
            failure = NotFound<T>("session", 0);
            return false;
        }

        var snapshot = session.Snapshot();
        if (snapshot.Descriptor.Lifecycle is SessionLifecycle.Closed or SessionLifecycle.Failed)
        {
            browser = null!;
            failure = HostResult<T>.Fail(
                HostError.Create(
                    HostErrorCode.SessionClosed,
                    "The browser session is closed."),
                snapshot.Descriptor.Revision);
            return false;
        }

        if (session.Engine is not IBrowserPanelSession browserSession
            || session.Engine.Kind != PanelKind.Browser)
        {
            browser = null!;
            failure = Unsupported<T>(
                "The requested session does not expose browser operations.",
                snapshot.Descriptor.Revision);
            return false;
        }

        browser = browserSession;
        failure = null!;
        return true;
    }
}
