using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;
using Asura.Protocol;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient :
    ISessionHostClient,
    IAgentTerminalSessionHost,
    IAgentBrowserSessionHost,
    IAgentFileSessionHost,
    IAgentPanelSessionHost,
    IAgentWorkspaceGraphSessionHost,
    IAgentWorkspaceLayoutSessionHost,
    IAgentProcessSessionHost,
    IAgentStatisticsSessionHost,
    IAgentDatabaseSessionHost,
    IAgentDockerSessionHost,
    IAgentGitSessionHost,
    IAgentWebToolSessionHost,
    IAsyncDisposable
{
    private const int DefaultEventRetention = 256;
    private static readonly TimeSpan HumanPhysicalInputLeaseDuration =
        TimeSpan.FromHours(8);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _attachmentGate = new(1, 1);
    private readonly SemaphoreSlim _sessionGraphGate = new(1, 1);
    private readonly SemaphoreSlim _idempotentTerminalWriteGate = new(1, 1);
    private readonly SemaphoreSlim _idempotentFileOperationGate = new(1, 1);
    private readonly SemaphoreSlim _idempotentBrowserOperationGate = new(1, 1);
    private readonly Dictionary<SessionId, HostedSession> _sessions = [];
    private readonly Dictionary<(ActorId ActorId, string Key), IdempotencyRecord> _idempotency = [];
    private readonly WorkspaceGraphRegistry _workspaceGraphs;
    private readonly ITerminalSessionFactory _terminalFactory;
    private readonly IFilePanelSessionFactory? _filePanelFactory;
    private readonly IBrowserPanelSessionFactory? _browserPanelFactory;
    private readonly CapabilitySet _browserPanelCapabilities;
    private readonly ISystemMonitorPanelSessionFactory? _systemMonitorFactory;
    private readonly IDatabasePanelSessionFactory? _databasePanelFactory;
    private readonly IDockerPanelSessionFactory? _dockerPanelFactory;
    private readonly IGitPanelSessionFactory? _gitPanelFactory;
    private readonly ISessionLifecyclePolicy _lifecyclePolicy;
    private readonly TimeProvider _timeProvider;
    private readonly AgentTerminalActionComposer? _agentTerminalActionComposer;
    private readonly AgentBrowserActionComposer? _agentBrowserActionComposer;
    private readonly AgentFileActionComposer? _agentFileActionComposer;
    private readonly AgentPanelActionComposer? _agentPanelActionComposer;
    private readonly AgentWorkspaceGraphActionComposer?
        _agentWorkspaceGraphActionComposer;
    private readonly AgentWorkspaceLayoutActionComposer?
        _agentWorkspaceLayoutActionComposer;
    private readonly AgentProcessListActionComposer?
        _agentProcessListActionComposer;
    private readonly AgentStatisticsReadActionComposer?
        _agentStatisticsReadActionComposer;
    private readonly AgentDatabaseReadActionComposer?
        _agentDatabaseReadActionComposer;
    private readonly AgentDockerReadActionComposer?
        _agentDockerReadActionComposer;
    private readonly AgentGitActionComposer? _agentGitActionComposer;
    private readonly AgentWebToolActionComposer? _agentWebToolActionComposer;
    private readonly IAgentWebToolExecutor? _agentWebToolExecutor;
    private readonly IAgentAuthorizationConsumer? _agentAuthorizationConsumer;
    private readonly int _eventRetention;
    private readonly CapabilitySet _hostCapabilities;
    private bool _disposed;

    public InMemorySessionHostClient(
        ITerminalSessionFactory terminalFactory,
        ISessionLifecyclePolicy lifecyclePolicy,
        TimeProvider? timeProvider = null,
        int eventRetention = DefaultEventRetention,
        IFilePanelSessionFactory? filePanelFactory = null,
        IBrowserPanelSessionFactory? browserPanelFactory = null,
        ISystemMonitorPanelSessionFactory? systemMonitorFactory = null,
        AgentTerminalActionComposer? agentActionComposer = null,
        AgentBrowserActionComposer? agentBrowserActionComposer = null,
        IAgentAuthorizationConsumer? agentAuthorizationConsumer = null,
        AgentFileActionComposer? agentFileActionComposer = null,
        AgentPanelActionComposer? agentPanelActionComposer = null,
        AgentWorkspaceGraphActionComposer?
            agentWorkspaceGraphActionComposer = null,
        AgentProcessListActionComposer?
            agentProcessListActionComposer = null,
        AgentStatisticsReadActionComposer?
            agentStatisticsReadActionComposer = null,
        IDatabasePanelSessionFactory? databasePanelFactory = null,
        IDockerPanelSessionFactory? dockerPanelFactory = null,
        AgentDatabaseReadActionComposer?
            agentDatabaseReadActionComposer = null,
        AgentDockerReadActionComposer?
            agentDockerReadActionComposer = null,
        AgentWorkspaceLayoutActionComposer?
            agentWorkspaceLayoutActionComposer = null,
        AgentWebToolActionComposer? agentWebToolActionComposer = null,
        IAgentWebToolExecutor? agentWebToolExecutor = null,
        IGitPanelSessionFactory? gitPanelFactory = null,
        AgentGitActionComposer? agentGitActionComposer = null)
    {
        ArgumentNullException.ThrowIfNull(terminalFactory);
        ArgumentNullException.ThrowIfNull(lifecyclePolicy);
        if (eventRetention < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(eventRetention));
        }

        _terminalFactory = terminalFactory;
        _filePanelFactory = filePanelFactory;
        _browserPanelFactory = browserPanelFactory;
        _browserPanelCapabilities = browserPanelFactory is null
            ? CapabilitySet.Empty
            : new CapabilitySet(browserPanelFactory.Capabilities.Values);
        _systemMonitorFactory = systemMonitorFactory;
        _databasePanelFactory = databasePanelFactory;
        _dockerPanelFactory = dockerPanelFactory;
        _gitPanelFactory = gitPanelFactory;
        _lifecyclePolicy = lifecyclePolicy;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _agentTerminalActionComposer = agentActionComposer;
        _agentBrowserActionComposer = agentBrowserActionComposer;
        _agentFileActionComposer = agentFileActionComposer;
        _agentPanelActionComposer = agentPanelActionComposer;
        _agentWorkspaceGraphActionComposer =
            agentWorkspaceGraphActionComposer;
        _agentWorkspaceLayoutActionComposer =
            agentWorkspaceLayoutActionComposer;
        _agentProcessListActionComposer = agentProcessListActionComposer;
        _agentStatisticsReadActionComposer =
            agentStatisticsReadActionComposer;
        _agentDatabaseReadActionComposer = agentDatabaseReadActionComposer;
        _agentDockerReadActionComposer = agentDockerReadActionComposer;
        _agentGitActionComposer = agentGitActionComposer;
        _agentWebToolActionComposer = agentWebToolActionComposer;
        _agentWebToolExecutor = agentWebToolExecutor;
        _agentAuthorizationConsumer = agentAuthorizationConsumer;
        _eventRetention = eventRetention;
        _workspaceGraphs = new WorkspaceGraphRegistry(_eventRetention, _timeProvider);
        _hostCapabilities = new CapabilitySet(
        [
            SessionCapabilities.AttachRead,
            SessionCapabilities.AttachInteractive,
            SessionCapabilities.AgentContextInspect,
            SessionCapabilities.InputLease,
            .. terminalFactory.Capabilities.Values,
            .. (filePanelFactory?.Capabilities.Values ?? []),
            .. _browserPanelCapabilities.Values,
            .. (systemMonitorFactory?.StatisticsCapabilities.Values ?? []),
            .. (systemMonitorFactory?.ProcessMonitorCapabilities.Values ?? []),
            .. (databasePanelFactory?.RelationalCapabilities.Values ?? []),
            .. (databasePanelFactory?.RedisCapabilities.Values ?? []),
            .. (dockerPanelFactory?.Capabilities.Values ?? []),
            .. (gitPanelFactory?.Capabilities.Values ?? []),
        ]);
    }

    public ValueTask<HostResult<HostHello>> NegotiateAsync(
        ClientHello request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var invalid = ValidateContext<HostHello>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }

        var version = request.SupportedProtocolVersions
            .Intersect(ProtocolVersions.Supported)
            .DefaultIfEmpty(0)
            .Max();
        if (version == 0)
        {
            return ValueTask.FromResult(HostResult<HostHello>.Fail(
                HostError.Create(
                    HostErrorCode.UnsupportedProtocol,
                    "The client and session host do not share a protocol version."),
                0));
        }

        return ValueTask.FromResult(HostResult<HostHello>.Succeed(
            new HostHello(version, _lifecyclePolicy.HostMode, _hostCapabilities),
            0));
    }

    public async ValueTask<HostResult<SessionSnapshot>> EnsureTerminalSessionAsync(
        EnsureTerminalSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        var fingerprint = Fingerprint(
            ApplicationOperations.TerminalOpen,
            request.SessionId.Value,
            request.Owner.PanelId.Value,
            request.Launch.WorkingDirectory ?? string.Empty,
            request.Launch.Executable ?? string.Empty,
            request.Launch.ConnectionId?.Value ?? string.Empty,
            request.Launch.ConnectionMetadata?.ConnectionBoundary ?? string.Empty,
            request.Launch.ConnectionMetadata?.InitialWorkingDirectory ?? string.Empty,
            request.Role.ToString());
        if (TryReplay(context, fingerprint, 0, out HostResult<SessionSnapshot>? replay))
        {
            return replay;
        }

        var invalid = ValidateContext<SessionSnapshot>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return invalid;
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
                        PanelKind.Terminal,
                        request.Role)) is { } ownerFailure)
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
                    || existing.Engine.Kind != PanelKind.Terminal)
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
                            PanelKind.Terminal,
                            request.SessionId,
                            request.Role)) is { } existingLinkFailure)
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

            ITerminalPanelSession? createdEngine = null;
            HostedSession hosted;
            try
            {
                createdEngine = await _terminalFactory
                    .CreateAsync(
                        request.SessionId,
                        request.Launch,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
                var engineSnapshot = await createdEngine
                    .SnapshotAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                hosted = new HostedSession(
                    createdEngine,
                    request.Owner,
                    request.Title,
                    engineSnapshot,
                    _eventRetention,
                    _timeProvider,
                    TerminalSessionMetadata.FromLaunch(request.Launch),
                    role: request.Role);
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
                        PanelKind.Terminal,
                        request.SessionId,
                        request.Role)) is { } linkFailure)
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

    public async ValueTask<HostResult<AttachmentResult>> AttachAsync(
        AttachSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var fingerprint = Fingerprint(
            ApplicationOperations.SessionAttach,
            request.SessionId.Value,
            request.ClientId.Value,
            request.Kind.ToString());
        var revision = CurrentRevision(request.SessionId);
        if (TryReplay(context, fingerprint, revision, out HostResult<AttachmentResult>? replay))
        {
            return replay;
        }

        var invalid = ValidateContext<AttachmentResult>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!TryGetSession(request.SessionId, out var session))
        {
            return NotFound<AttachmentResult>("session", 0);
        }

        if (RevisionConflict(context, session, out HostResult<AttachmentResult>? conflict))
        {
            return conflict;
        }

        try
        {
            await _attachmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AttachmentResult>(revision);
        }

        var outcomeReserved = false;
        try
        {
            var gatedRevision = session.Snapshot().Descriptor.Revision;
            if (TryReplay(
                    context,
                    fingerprint,
                    gatedRevision,
                    out HostResult<AttachmentResult>? inGateReplay))
            {
                return inGateReplay;
            }

            if (RevisionConflict(
                    context,
                    session,
                    out HostResult<AttachmentResult>? gatedConflict))
            {
                return gatedConflict;
            }

            var gatedInvalid = ValidateContext<AttachmentResult>(
                context,
                cancellationToken,
                gatedRevision);
            if (gatedInvalid is not null)
            {
                return gatedInvalid;
            }

            var reservationReplay = ReserveReplay<AttachmentResult>(
                context,
                fingerprint,
                gatedRevision,
                out outcomeReserved);
            if (reservationReplay is not null)
            {
                return reservationReplay;
            }

            if (request.Kind == AttachmentKind.Interactive)
            {
                var replaced = session.AttachmentsForClient(request.ClientId)
                    .Where(item => item.Kind == AttachmentKind.Interactive)
                    .ToArray();
                if (replaced.Length > 0)
                {
                    await DetachInteractiveRendererAsync(
                            session.Engine,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    foreach (var attachment in replaced)
                    {
                        _ = session.Detach(attachment.Id);
                    }
                }
            }

            var result = session.Attach(request, _hostCapabilities);
            CompleteReplay(context, fingerprint, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return outcomeReserved
                ? OutcomeUncertain<AttachmentResult>(
                    session.Snapshot().Descriptor.Revision)
                : Cancelled<AttachmentResult>(
                    session.Snapshot().Descriptor.Revision);
        }
        catch (Exception exception)
        {
            return outcomeReserved
                ? OutcomeUncertain<AttachmentResult>(
                    session.Snapshot().Descriptor.Revision)
                : EngineFailure<AttachmentResult>(
                    exception,
                    session.Snapshot().Descriptor.Revision);
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    public async ValueTask<HostResult<bool>> UpdateTerminalRenderProfileAsync(
        UpdateTerminalRenderProfileRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetSession(request.SessionId, out var session))
        {
            return NotFound<bool>("session", 0);
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<bool>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HasAttachment(request.AttachmentId, AttachmentKind.Interactive))
        {
            return NotFound<bool>("attachment", revision);
        }

        if (session.Engine.Kind != PanelKind.Terminal
            || session.Engine is not ITerminalRendererAttachment renderer)
        {
            return Unsupported<bool>("The session is not a terminal.", revision);
        }

        var applied = await renderer
            .UpdateRenderProfileAsync(request.RenderProfile, cancellationToken)
            .ConfigureAwait(false);
        return HostResult<bool>.Succeed(applied, revision);
    }

    public async ValueTask<HostResult<Unit>> AttachTerminalRendererAsync(
        AttachTerminalRendererRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
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

        if (!session.HasAttachment(request.AttachmentId, AttachmentKind.Interactive))
        {
            return HostResult<Unit>.Fail(
                HostError.Create(
                    HostErrorCode.NotFound,
                    "An interactive attachment is required before attaching a renderer."),
                revision);
        }

        if (!session.CanBindPhysicalInputGate(
                request.AttachmentId,
                context.Actor))
        {
            return HostResult<Unit>.Fail(
                HostError.Create(
                    HostErrorCode.LeaseDenied,
                    "The native input gate requires the exact interactive human attachment."),
                revision);
        }

        if (session.Engine.Kind != PanelKind.Terminal
            || session.Engine is not ITerminalRendererAttachment renderer)
        {
            return Unsupported<Unit>("The session is not a terminal.", revision);
        }

        try
        {
            await session.WaitForResizeAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!session.HasAttachment(
                        request.AttachmentId,
                        AttachmentKind.Interactive))
                {
                    return NotFound<Unit>("attachment", revision);
                }

                var rendererHost = request.RendererHost with
                {
                    // Native event delivery cannot await a client or transport. Bind the
                    // callback here, where the authoritative session and attachment live.
                    PhysicalInputGate = _ => session.TryAcceptPhysicalInput(
                        request.AttachmentId,
                        context.Actor,
                        HumanPhysicalInputLeaseDuration),
                };
                cancellationToken.ThrowIfCancellationRequested();
                await renderer
                    .AttachRendererAsync(rendererHost, cancellationToken)
                    .ConfigureAwait(false);
                var engineSnapshot = await session.Engine
                    .SnapshotAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                session.ApplyEngineSnapshot(engineSnapshot);
                return session.UpdateViewport(
                    request.AttachmentId,
                    rendererHost.Viewport);
            }
            finally
            {
                session.ReleaseResize();
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> DetachAsync(
        DetachSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetSession(request.SessionId, out var session))
        {
            return HostResult<Unit>.Succeed(Unit.Value, 0);
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            await _attachmentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }

        try
        {
            if (session.HasAttachment(request.AttachmentId, AttachmentKind.Interactive))
            {
                try
                {
                    await DetachInteractiveRendererAsync(
                            session.Engine,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Cancelled<Unit>(revision);
                }
                catch (Exception exception)
                {
                    return EngineFailure<Unit>(exception, revision);
                }
            }

            return session.Detach(request.AttachmentId);
        }
        finally
        {
            _attachmentGate.Release();
        }
    }

    public async ValueTask<HostResult<SessionSnapshot>> GetSnapshotAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetSession(sessionId, out var session))
        {
            return NotFound<SessionSnapshot>("session", 0);
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<SessionSnapshot>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var engineSnapshot = await session.Engine
                .SnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            session.ApplyEngineSnapshot(engineSnapshot);
            var snapshot = session.Snapshot();
            return HostResult<SessionSnapshot>.Succeed(snapshot, snapshot.Descriptor.Revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<SessionSnapshot>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<SessionSnapshot>(exception, revision);
        }
    }

    public async IAsyncEnumerable<PanelNotificationEvent> WatchNotificationsAsync(
        WatchSessionRequest request,
        OperationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (ValidateContext<Unit>(context, cancellationToken, 0) is not null
            || !TryGetSession(request.SessionId, out var session))
        {
            yield break;
        }

        // Straight to the engine: notifications carry no host-side state, so
        // unlike the lifecycle stream there is nothing for the hosted session
        // to retain, replay, or resynchronize.
        await foreach (var notification in session.Engine
            .WatchNotificationsAsync(request.AfterSequence, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return notification;
        }
    }

    public async IAsyncEnumerable<SessionStreamItem> WatchAsync(
        WatchSessionRequest request,
        OperationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (ValidateContext<Unit>(context, cancellationToken, 0) is not null
            || !TryGetSession(request.SessionId, out var session))
        {
            yield break;
        }

        await foreach (var item in session
            .WatchAsync(request.AfterSequence, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public ValueTask<HostResult<InputLeaseDecision>> AcquireInputLeaseAsync(
        AcquireInputLeaseRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetSession(request.SessionId, out var session))
        {
            return ValueTask.FromResult(NotFound<InputLeaseDecision>("session", 0));
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<InputLeaseDecision>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }

        if (RevisionConflict(context, session, out HostResult<InputLeaseDecision>? conflict))
        {
            return ValueTask.FromResult(conflict);
        }

        return ValueTask.FromResult(session.AcquireLease(request, context.Actor));
    }

    public ValueTask<HostResult<Unit>> ReleaseInputLeaseAsync(
        ReleaseInputLeaseRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetSession(request.SessionId, out var session))
        {
            return ValueTask.FromResult(NotFound<Unit>("session", 0));
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        return ValueTask.FromResult(
            invalid ?? session.ReleaseLease(request.LeaseId, context.Actor));
    }

    public ValueTask<HostResult<Unit>> FocusTerminalAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        UpdateTerminalFocusAsync(
            sessionId,
            context,
            focused: true,
            cancellationToken);

    public ValueTask<HostResult<Unit>> BlurTerminalAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken) =>
        UpdateTerminalFocusAsync(
            sessionId,
            context,
            focused: false,
            cancellationToken);

    private async ValueTask<HostResult<Unit>> UpdateTerminalFocusAsync(
        SessionId sessionId,
        OperationContext context,
        bool focused,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<Unit, ITerminalRendererAttachment>(
                sessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            if (focused)
            {
                await terminal.FocusAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await terminal.BlurAsync(cancellationToken).ConfigureAwait(false);
            }

            return HostResult<Unit>.Succeed(Unit.Value, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> ResizeTerminalAsync(
        TerminalResizeRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<Unit, ITerminalProcess>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HasAttachment(request.AttachmentId, AttachmentKind.Interactive))
        {
            return NotFound<Unit>("attachment", revision);
        }

        try
        {
            await session.WaitForResizeAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!session.HasAttachment(
                        request.AttachmentId,
                        AttachmentKind.Interactive))
                {
                    return NotFound<Unit>("attachment", revision);
                }

                cancellationToken.ThrowIfCancellationRequested();
                await terminal
                    .ResizeAsync(request.Viewport, cancellationToken)
                    .ConfigureAwait(false);
                return session.UpdateViewport(
                    request.AttachmentId,
                    request.Viewport);
            }
            finally
            {
                session.ReleaseResize();
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> WriteTerminalAsync(
        TerminalWriteRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (context.IdempotencyKey is null)
        {
            return await WriteTerminalCoreAsync(request, context, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await _idempotentTerminalWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(CurrentRevision(request.SessionId));
        }

        try
        {
            return await WriteTerminalCoreAsync(request, context, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _idempotentTerminalWriteGate.Release();
        }
    }

    private async ValueTask<HostResult<Unit>> WriteTerminalCoreAsync(
        TerminalWriteRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var textHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Text)));
        var fingerprint = Fingerprint(
            ApplicationOperations.TerminalWrite,
            request.SessionId.Value,
            textHash);
        if (TryReplay(context, fingerprint, 0, out HostResult<Unit>? replay))
        {
            return replay;
        }

        if (!TryGetTerminalPort<Unit, ITerminalAutomation>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return HostResult<Unit>.Fail(
                HostError.Create(
                    HostErrorCode.LeaseDenied,
                    "Terminal input requires the current input lease."),
                revision);
        }

        invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        var reservationReplay = ReserveReplay<Unit>(
            context,
            fingerprint,
            revision,
            out var outcomeReserved);
        if (reservationReplay is not null)
        {
            return reservationReplay;
        }

        try
        {
            await terminal.WriteAsync(request.Text, cancellationToken).ConfigureAwait(false);
            var result = HostResult<Unit>.Succeed(Unit.Value, revision);
            CompleteReplay(context, fingerprint, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return outcomeReserved
                ? OutcomeUncertain<Unit>(revision)
                : Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return outcomeReserved
                ? OutcomeUncertain<Unit>(revision)
                : EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> SendTerminalKeyAsync(
        TerminalKeyRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<Unit, ITerminalAutomation>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<Unit>(revision);
        }

        try
        {
            await terminal.SendKeyAsync(request.KeyStroke, cancellationToken).ConfigureAwait(false);
            return HostResult<Unit>.Succeed(Unit.Value, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> SendTerminalPhysicalKeyAsync(
        TerminalPhysicalKeyRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<Unit, ITerminalAutomation>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<Unit>(revision);
        }

        try
        {
            await terminal.SendPhysicalKeyAsync(
                request.KeyEvent,
                cancellationToken).ConfigureAwait(false);
            return HostResult<Unit>.Succeed(Unit.Value, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> EnterTerminalAsync(
        TerminalEnterRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<Unit, ITerminalAutomation>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<Unit>(revision);
        }

        try
        {
            await terminal.EnterAsync(cancellationToken).ConfigureAwait(false);
            return HostResult<Unit>.Succeed(Unit.Value, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> InterruptTerminalAsync(
        TerminalInterruptRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<Unit, ITerminalAutomation>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<Unit>(revision);
        }

        try
        {
            await terminal.InterruptAsync(cancellationToken).ConfigureAwait(false);
            return HostResult<Unit>.Succeed(Unit.Value, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> SendTerminalMouseAsync(
        TerminalMouseRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<Unit, ITerminalAutomation>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<Unit>(revision);
        }

        try
        {
            await terminal.SendMouseAsync(request.MouseInput, cancellationToken).ConfigureAwait(false);
            return HostResult<Unit>.Succeed(Unit.Value, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> ScrollTerminalViewportAsync(
        TerminalViewportScrollRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<Unit, ITerminalState>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<Unit>(revision);
        }

        try
        {
            await terminal.ScrollViewportAsync(request.ScrollInput, cancellationToken).ConfigureAwait(false);
            return HostResult<Unit>.Succeed(Unit.Value, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> ClearTerminalScrollbackAsync(
        TerminalClearScrollbackRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<Unit, ITerminalState>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.Engine.Capabilities.Contains(SessionCapabilities.TerminalClearScrollback))
        {
            return Unsupported<Unit>(
                "The terminal engine does not support clearing scrollback.",
                revision);
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<Unit>(revision);
        }

        try
        {
            await terminal.ClearScrollbackAsync(cancellationToken).ConfigureAwait(false);
            return HostResult<Unit>.Succeed(Unit.Value, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<TerminalFindResult>> FindTerminalAsync(
        TerminalFindRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Find);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<TerminalFindResult, ITerminalState>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<TerminalFindResult>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.Engine.Capabilities.Contains(SessionCapabilities.TerminalFind))
        {
            return Unsupported<TerminalFindResult>(
                "The terminal engine does not support finding terminal output.",
                revision);
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<TerminalFindResult>(revision);
        }

        try
        {
            var result = await terminal.FindAsync(request.Find, cancellationToken).ConfigureAwait(false);
            return HostResult<TerminalFindResult>.Succeed(result, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<TerminalFindResult>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<TerminalFindResult>(exception, revision);
        }
    }

    public async ValueTask<HostResult<Unit>> UpdateTerminalSelectionAsync(
        TerminalSelectionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<Unit, ITerminalState>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<Unit>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<Unit>(revision);
        }

        try
        {
            await terminal.UpdateSelectionAsync(request.SelectionInput, cancellationToken).ConfigureAwait(false);
            return HostResult<Unit>.Succeed(Unit.Value, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<Unit>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<Unit>(exception, revision);
        }
    }

    public async ValueTask<HostResult<TerminalSelectionText>> ReadTerminalSelectionAsync(
        TerminalSelectionReadRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<TerminalSelectionText, ITerminalState>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<TerminalSelectionText>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<TerminalSelectionText>(revision);
        }

        try
        {
            var selection = await terminal.ReadSelectionAsync(cancellationToken).ConfigureAwait(false);
            return HostResult<TerminalSelectionText>.Succeed(selection, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<TerminalSelectionText>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<TerminalSelectionText>(exception, revision);
        }
    }

    public async ValueTask<HostResult<TerminalPasteResult>> PasteTerminalAsync(
        TerminalPasteRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<TerminalPasteResult, ITerminalAutomation>(
                request.SessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<TerminalPasteResult>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!session.HoldsLease(request.LeaseId, context.Actor.Id))
        {
            return LeaseDenied<TerminalPasteResult>(revision);
        }

        try
        {
            var result = await terminal.PasteAsync(request.PasteInput, cancellationToken).ConfigureAwait(false);
            return HostResult<TerminalPasteResult>.Succeed(result, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<TerminalPasteResult>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<TerminalPasteResult>(exception, revision);
        }
    }

    public async ValueTask<HostResult<TerminalScreenSnapshot>> ReadTerminalScreenAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<TerminalScreenSnapshot, ITerminalAutomation>(
                sessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<TerminalScreenSnapshot>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var snapshot = await terminal.ReadScreenAsync(cancellationToken).ConfigureAwait(false);
            session.UpdateTerminalWorkingDirectory(snapshot.WorkingDirectory);
            return HostResult<TerminalScreenSnapshot>.Succeed(
                snapshot,
                session.Snapshot().Descriptor.Revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<TerminalScreenSnapshot>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<TerminalScreenSnapshot>(exception, revision);
        }
    }

    public async ValueTask<HostResult<TerminalRenderFrame>> ReadTerminalRenderFrameAsync(
        SessionId sessionId,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryGetTerminalPort<TerminalRenderFrame, ITerminalRenderState>(
                sessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<TerminalRenderFrame>(context, cancellationToken, revision);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            var frame = await terminal
                .ReadRenderFrameAsync(cancellationToken)
                .ConfigureAwait(false);
            return HostResult<TerminalRenderFrame>.Succeed(frame, revision);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<TerminalRenderFrame>(revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<TerminalRenderFrame>(exception, revision);
        }
    }

    public ValueTask<HostResult<TerminalWaitOutcome>> WaitForTerminalTextAsync(
        TerminalWaitForTextRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Wait);
        ArgumentNullException.ThrowIfNull(context);
        return WaitForTerminalAsync(
            request.SessionId,
            request.Wait.Timeout,
            context,
            cancellationToken,
            (terminal, timeout, token) => terminal.WaitForTextAsync(
                new TerminalWaitForTextInput(request.Wait.Text, timeout),
                token));
    }

    public ValueTask<HostResult<TerminalWaitOutcome>> WaitForTerminalChangeAsync(
        TerminalWaitForChangeRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Wait);
        ArgumentNullException.ThrowIfNull(context);
        return WaitForTerminalAsync(
            request.SessionId,
            request.Wait.Timeout,
            context,
            cancellationToken,
            (terminal, timeout, token) => terminal.WaitForChangeAsync(
                new TerminalWaitForChangeInput(
                    request.Wait.AfterContentRevision,
                    timeout),
                token));
    }

    public ValueTask<HostResult<TerminalWaitOutcome>> WaitForTerminalStableAsync(
        TerminalWaitForStableRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Wait);
        ArgumentNullException.ThrowIfNull(context);
        return WaitForTerminalAsync(
            request.SessionId,
            request.Wait.Timeout,
            context,
            cancellationToken,
            (terminal, timeout, token) => terminal.WaitForStableAsync(
                new TerminalWaitForStableInput(request.Wait.StableFor, timeout),
                token));
    }

    private async ValueTask<HostResult<TerminalWaitOutcome>> WaitForTerminalAsync(
        SessionId sessionId,
        TimeSpan requestedTimeout,
        OperationContext context,
        CancellationToken cancellationToken,
        Func<
            ITerminalAutomation,
            TimeSpan,
            CancellationToken,
            ValueTask<TerminalWaitOutcome>> wait)
    {
        if (!TryGetTerminalPort<TerminalWaitOutcome, ITerminalAutomation>(
                sessionId,
                out var session,
                out var terminal,
                out var failure))
        {
            return failure;
        }

        var revision = session.Snapshot().Descriptor.Revision;
        var invalid = ValidateContext<TerminalWaitOutcome>(
            context,
            cancellationToken,
            revision);
        if (invalid is not null)
        {
            return invalid;
        }

        var effectiveTimeout = requestedTimeout;
        if (context.DeadlineUtc is { } deadline)
        {
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return HostResult<TerminalWaitOutcome>.Fail(
                    HostError.Create(
                        HostErrorCode.DeadlineExceeded,
                        "The operation deadline has elapsed."),
                    revision);
            }

            if (remaining < effectiveTimeout)
            {
                effectiveTimeout = remaining;
            }
        }

        try
        {
            var outcome = await wait(
                    terminal,
                    effectiveTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            return HostResult<TerminalWaitOutcome>.Succeed(outcome, revision);
        }
        catch (OperationCanceledException)
        {
            return HostResult<TerminalWaitOutcome>.Succeed(
                TerminalWaitOutcome.Cancelled(null, null),
                revision);
        }
        catch (Exception exception)
        {
            return EngineFailure<TerminalWaitOutcome>(exception, revision);
        }
    }

    private static HostResult<T> LeaseDenied<T>(long revision) => HostResult<T>.Fail(
        HostError.Create(
            HostErrorCode.LeaseDenied,
            "Terminal input requires the current input lease."),
        revision);

    public async ValueTask<HostResult<CloseScopeResult>> CloseAsync(
        CloseScopeRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var fingerprint = Fingerprint(
            OperationForScope(request.Scope),
            request.TargetId,
            request.Decision.ToString());
        if (TryReplay(context, fingerprint, 0, out HostResult<CloseScopeResult>? replay))
        {
            return replay;
        }

        var invalid = ValidateContext<CloseScopeResult>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<CloseScopeResult>(0);
        }

        try
        {
            ThrowIfDisposed();
            if (TryReplay(
                    context,
                    fingerprint,
                    0,
                    out HostResult<CloseScopeResult>? inGateReplay))
            {
                return inGateReplay;
            }

            var targets = SessionsForScope(request);
            foreach (var target in targets)
            {
                var currentRevision = target.Snapshot().Descriptor.Revision;
                if (request.ExpectedSessionRevisions is not null
                    && request.ExpectedSessionRevisions.TryGetValue(target.Id, out var expected)
                    && currentRevision != expected)
                {
                    return RevisionConflict<CloseScopeResult>(currentRevision, expected);
                }
            }

            var reservationRevision = targets.Count == 0
                ? 0
                : targets.Max(target => target.Snapshot().Descriptor.Revision);
            var gatedInvalid = ValidateContext<CloseScopeResult>(
                context,
                cancellationToken,
                reservationRevision);
            if (gatedInvalid is not null)
            {
                return gatedInvalid;
            }

            var reservationReplay = ReserveReplay<CloseScopeResult>(
                context,
                fingerprint,
                reservationRevision,
                out var outcomeReserved);
            if (reservationReplay is not null)
            {
                return reservationReplay;
            }

            if (targets.Count == 0)
            {
                var emptyResult = HostResult<CloseScopeResult>.Succeed(
                    new CloseScopeResult.Completed(request.Scope, request.TargetId, []),
                    0);
                RemoveWorkspaceGraphAfterSuccessfulClose(request, []);
                CompleteReplay(context, fingerprint, emptyResult);
                return emptyResult;
            }

            foreach (var target in targets)
            {
                var currentRevision = target.Snapshot().Descriptor.Revision;
                try
                {
                    var engineSnapshot = await target.Engine
                        .SnapshotAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    target.ApplyEngineSnapshot(engineSnapshot);
                }
                catch (OperationCanceledException)
                {
                    return outcomeReserved
                        ? OutcomeUncertain<CloseScopeResult>(currentRevision)
                        : Cancelled<CloseScopeResult>(currentRevision);
                }
                catch (Exception exception)
                {
                    return outcomeReserved
                        ? OutcomeUncertain<CloseScopeResult>(currentRevision)
                        : EngineFailure<CloseScopeResult>(exception, currentRevision);
                }
            }

            if (request.Decision == CloseDecision.Cancel)
            {
                var cancelledSessions = targets
                    .Select(target => new SessionCloseResult(
                        target.Id,
                        SessionCloseOutcome.Cancelled,
                        "Close cancelled by the user."))
                    .ToArray();
                var cancelled = HostResult<CloseScopeResult>.Succeed(
                    new CloseScopeResult.Completed(request.Scope, request.TargetId, cancelledSessions),
                    targets.Max(target => target.Snapshot().Descriptor.Revision));
                CompleteReplay(context, fingerprint, cancelled);
                return cancelled;
            }

            var active = targets
                .Select(target => (Session: target, Snapshot: target.Snapshot()))
                .Where(item => item.Snapshot.Descriptor.HasActiveWork)
                .Select(item => new ActiveSessionSummary(
                    item.Session.Id,
                    item.Session.Owner.PanelId,
                    item.Session.Title,
                    item.Snapshot.Descriptor.StatusDetail,
                    item.Snapshot.Descriptor.Revision))
                .ToArray();
            if (request.Decision == CloseDecision.Request && active.Length > 0)
            {
                var confirmation = HostResult<CloseScopeResult>.Succeed(
                    new CloseScopeResult.ConfirmationRequired(request.Scope, request.TargetId, active),
                    targets.Max(target => target.Snapshot().Descriptor.Revision));
                CompleteReplay(context, fingerprint, confirmation);
                return confirmation;
            }

            var closeResults = new List<SessionCloseResult>(targets.Count);
            foreach (var target in targets)
            {
                var targetSnapshot = target.Snapshot();
                var mode = request.Decision == CloseDecision.Confirm
                    && targetSnapshot.Descriptor.HasActiveWork
                        ? PanelCloseMode.Force
                        : PanelCloseMode.Graceful;
                target.MarkCloseRequested(
                    mode == PanelCloseMode.Force
                        ? "Force termination requested."
                        : "Graceful close requested.");

                PanelCloseOutcome outcome;
                string detail;
                try
                {
                    outcome = await target.Engine
                        .CloseAsync(mode, CancellationToken.None)
                        .ConfigureAwait(false);
                    detail = DetailFor(outcome);
                }
                catch (OperationCanceledException)
                {
                    if (outcomeReserved)
                    {
                        return OutcomeUncertain<CloseScopeResult>(
                            targetSnapshot.Descriptor.Revision);
                    }

                    outcome = PanelCloseOutcome.Cancelled;
                    detail = "Close cancelled.";
                }
                catch (Exception exception)
                {
                    if (outcomeReserved)
                    {
                        return OutcomeUncertain<CloseScopeResult>(
                            targetSnapshot.Descriptor.Revision);
                    }

                    outcome = PanelCloseOutcome.EngineFailed;
                    detail = exception.Message;
                }

                target.ApplyCloseOutcome(outcome, detail);
                var closeOutcome = MapCloseOutcome(outcome);
                closeResults.Add(new SessionCloseResult(target.Id, closeOutcome, detail));
                if (closeOutcome is SessionCloseOutcome.GracefullyClosed
                    or SessionCloseOutcome.ForceTerminated
                    or SessionCloseOutcome.AlreadyClosed)
                {
                    _workspaceGraphs.UnlinkSession(
                        target.Owner,
                        target.Engine.Kind,
                        target.Id,
                        target.Role);
                }
            }

            if (closeResults.Any(item => item.Outcome == SessionCloseOutcome.ConfirmationRequired))
            {
                var confirmationSessions = targets
                    .Where(target => closeResults.Any(
                        result => result.SessionId == target.Id
                            && result.Outcome == SessionCloseOutcome.ConfirmationRequired))
                    .Select(target => new ActiveSessionSummary(
                        target.Id,
                        target.Owner.PanelId,
                        target.Title,
                        target.Snapshot().Descriptor.StatusDetail,
                        target.Snapshot().Descriptor.Revision))
                    .ToArray();
                var confirmationResult = HostResult<CloseScopeResult>.Succeed(
                    new CloseScopeResult.ConfirmationRequired(
                        request.Scope,
                        request.TargetId,
                        confirmationSessions),
                    targets.Max(target => target.Snapshot().Descriptor.Revision));
                CompleteReplay(context, fingerprint, confirmationResult);
                return confirmationResult;
            }

            var completed = HostResult<CloseScopeResult>.Succeed(
                new CloseScopeResult.Completed(request.Scope, request.TargetId, closeResults),
                targets.Max(target => target.Snapshot().Descriptor.Revision));
            RemoveWorkspaceGraphAfterSuccessfulClose(request, closeResults);
            CompleteReplay(context, fingerprint, completed);
            return completed;
        }
        finally
        {
            _sessionGraphGate.Release();
        }
    }

    public async ValueTask<HostResult<Unit>> DisconnectClientAsync(
        ClientId clientId,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var invalid = ValidateContext<Unit>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return invalid;
        }

        HostedSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values];
        }

        var revision = 0L;
        foreach (var session in sessions)
        {
            foreach (var attachment in session.AttachmentsForClient(clientId))
            {
                if (attachment.Kind == AttachmentKind.Interactive)
                {
                    try
                    {
                        await DetachInteractiveRendererAsync(
                                session.Engine,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        return EngineFailure<Unit>(exception, revision);
                    }
                }

                var detached = session.Detach(attachment.Id);
                if (detached is HostResult<Unit>.Success success)
                {
                    revision = Math.Max(revision, success.ResultingRevision);
                }
            }
        }

        // A client disconnect removes its window projection and attachments, but it does
        // not terminate sessions. Explicit close commands remain the only session owner action.
        _workspaceGraphs.RemoveClient(clientId);
        return HostResult<Unit>.Succeed(Unit.Value, revision);
    }

    private static ValueTask DetachInteractiveRendererAsync(
        IPanelSession engine,
        CancellationToken cancellationToken) => engine switch
        {
            ITerminalRendererAttachment terminalRenderer =>
                terminalRenderer.DetachRendererAsync(cancellationToken),
            IBrowserRendererAttachment browserRenderer =>
                browserRenderer.DetachRendererAsync(cancellationToken),
            _ => ValueTask.CompletedTask,
        };

    public async ValueTask DisposeAsync()
    {
        await _sessionGraphGate.WaitAsync().ConfigureAwait(false);
        try
        {
            HostedSession[] sessions;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                sessions = [.. _sessions.Values];
                _sessions.Clear();
            }

            _workspaceGraphs.Dispose();

            foreach (var session in sessions)
            {
                session.RevokeRuntimeAuthority();
                await session.Engine.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        // These managed synchronizers intentionally remain undisposed. A call that passed
        // its initial disposal check before shutdown may still be queued and must be able
        // to acquire/release its gate safely before observing the disposed host.
    }

    /// <summary>
    /// A close that actually ended everything it named takes the graphs it
    /// named with it. A window close takes all of its window's; a workspace
    /// close takes exactly one, and its window keeps the rest.
    /// </summary>
    private void RemoveWorkspaceGraphAfterSuccessfulClose(
        CloseScopeRequest request,
        IReadOnlyList<SessionCloseResult> results)
    {
        if (request.Scope is not (CloseScopeKind.Window or CloseScopeKind.Workspace)
            || request.Decision == CloseDecision.Cancel
            || results.Any(result => result.Outcome is not (
                SessionCloseOutcome.GracefullyClosed
                or SessionCloseOutcome.ForceTerminated
                or SessionCloseOutcome.AlreadyClosed)))
        {
            return;
        }

        if (request.Scope == CloseScopeKind.Workspace)
        {
            _workspaceGraphs.RemoveWorkspace(new WorkspaceInstanceId(request.TargetId));
            return;
        }

        _workspaceGraphs.RemoveWindow(new WindowInstanceId(request.TargetId));
    }

    private bool TryGetTerminalPort<TResult, TPort>(
        SessionId sessionId,
        out HostedSession session,
        out TPort port,
        out HostResult<TResult> failure)
        where TPort : class
    {
        if (!TryGetSession(sessionId, out session))
        {
            port = null!;
            failure = NotFound<TResult>("session", 0);
            return false;
        }

        if (session.Engine.Kind != PanelKind.Terminal
            || session.Engine is not TPort terminalPort)
        {
            port = null!;
            failure = Unsupported<TResult>(
                "The requested session does not expose the required terminal capability.",
                session.Snapshot().Descriptor.Revision);
            return false;
        }

        port = terminalPort;
        failure = null!;
        return true;
    }

    private async ValueTask<HostResult<T>> RemoveRejectedSessionAsync<T>(
        HostedSession session,
        HostResult<T> failure)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.Id, out var registered)
                && ReferenceEquals(registered, session))
            {
                _sessions.Remove(session.Id);
            }
        }

        try
        {
            await session.Engine.DisposeAsync().ConfigureAwait(false);
            return failure;
        }
        catch (Exception exception)
        {
            var revision = failure is HostResult<T>.Failure rejected
                ? rejected.CurrentRevision
                : 0;
            return EngineFailure<T>(exception, revision);
        }
    }

    private static async ValueTask DisposeUnownedSessionAsync(IPanelSession? engine)
    {
        if (engine is null)
        {
            return;
        }

        try
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The reserved outcome remains uncertain even if best-effort
            // cleanup fails; no hosted reference exists for another attempt.
        }
    }

    private bool TryGetSession(SessionId sessionId, out HostedSession session)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out session!);
        }
    }

    private IReadOnlyList<HostedSession> SessionsForScope(CloseScopeRequest request)
    {
        lock (_gate)
        {
            return [.. _sessions.Values
                .Where(session => request.Scope switch
                {
                    CloseScopeKind.Panel => string.Equals(session.Owner.PanelId.Value, request.TargetId, StringComparison.Ordinal),
                    CloseScopeKind.Tab => string.Equals(session.Owner.TabId.Value, request.TargetId, StringComparison.Ordinal),
                    CloseScopeKind.Workspace => string.Equals(session.Owner.WorkspaceId.Value, request.TargetId, StringComparison.Ordinal),
                    CloseScopeKind.Window => string.Equals(session.Owner.WindowId.Value, request.TargetId, StringComparison.Ordinal),
                    CloseScopeKind.Session => string.Equals(session.Id.Value, request.TargetId, StringComparison.Ordinal),
                    _ => false,
                })
                .OrderBy(session => session.Id.Value, StringComparer.Ordinal)];
        }
    }

    private long CurrentRevision(SessionId sessionId) =>
        TryGetSession(sessionId, out var session)
            ? session.Snapshot().Descriptor.Revision
            : 0;

    private HostResult<T>? ValidateContext<T>(
        OperationContext context,
        CancellationToken cancellationToken,
        long currentRevision)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<T>(currentRevision);
        }

        if (context.DeadlineUtc is { } deadline && deadline <= _timeProvider.GetUtcNow())
        {
            return HostResult<T>.Fail(
                HostError.Create(
                    HostErrorCode.DeadlineExceeded,
                    "The operation deadline has elapsed."),
                currentRevision);
        }

        return null;
    }

    private static bool RevisionConflict<T>(
        OperationContext context,
        HostedSession session,
        out HostResult<T> conflict)
    {
        var current = session.Snapshot().Descriptor.Revision;
        if (context.ExpectedRevision is { } expected && expected != current)
        {
            conflict = RevisionConflict<T>(current, expected);
            return true;
        }

        conflict = null!;
        return false;
    }

    private bool TryReplay<T>(
        OperationContext context,
        string fingerprint,
        long currentRevision,
        out HostResult<T> result)
    {
        if (context.IdempotencyKey is not { } key)
        {
            result = null!;
            return false;
        }

        lock (_gate)
        {
            if (!_idempotency.TryGetValue((context.Actor.Id, key.Value), out var record))
            {
                result = null!;
                return false;
            }

            if (!string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal) || record.Result is not HostResult<T> typed)
            {
                result = HostResult<T>.Fail(
                    HostError.Create(
                        HostErrorCode.IdempotencyKeyReused,
                        "The idempotency key was already used for a different operation."),
                    currentRevision);
                return true;
            }

            result = typed;
            return true;
        }
    }

    private HostResult<T>? ReserveReplay<T>(
        OperationContext context,
        string fingerprint,
        long currentRevision,
        out bool reserved)
    {
        reserved = false;
        if (context.IdempotencyKey is not { } key)
        {
            return null;
        }

        lock (_gate)
        {
            var index = (context.Actor.Id, key.Value);
            if (_idempotency.TryGetValue(index, out var record))
            {
                if (string.Equals(
                        record.Fingerprint,
                        fingerprint,
                        StringComparison.Ordinal)
                    && record.Result is HostResult<T> typed)
                {
                    return typed;
                }

                return HostResult<T>.Fail(
                    HostError.Create(
                        HostErrorCode.IdempotencyKeyReused,
                        "The idempotency key was already used for a different operation."),
                    currentRevision);
            }

            var uncertain = OutcomeUncertain<T>(currentRevision);
            _idempotency.Add(
                index,
                new IdempotencyRecord(
                    fingerprint,
                    uncertain,
                    IsOutcomeUncertain: true));
            reserved = true;
            return null;
        }
    }

    private void CompleteReplay<T>(
        OperationContext context,
        string fingerprint,
        HostResult<T> result)
    {
        if (context.IdempotencyKey is not { } key
            || result is not HostResult<T>.Success)
        {
            return;
        }

        lock (_gate)
        {
            var index = (context.Actor.Id, key.Value);
            if (_idempotency.TryGetValue(index, out var record)
                && record.IsOutcomeUncertain
                && string.Equals(
                    record.Fingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                _idempotency[index] = new IdempotencyRecord(fingerprint, result);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static HostResult<T> NotFound<T>(string resource, long revision) =>
        HostResult<T>.Fail(
            HostError.Create(HostErrorCode.NotFound, $"The {resource} was not found."),
            revision);

    private static HostResult<T> Unsupported<T>(string message, long revision) =>
        HostResult<T>.Fail(
            HostError.Create(HostErrorCode.CapabilityNotSupported, message),
            revision);

    private static HostResult<T> Cancelled<T>(long revision) =>
        HostResult<T>.Fail(
            HostError.Create(HostErrorCode.Cancelled, "The operation was cancelled."),
            revision);

    private static HostResult<T> OutcomeUncertain<T>(long revision) =>
        HostResult<T>.Fail(
            HostError.Create(
                HostErrorCode.ResynchronizationRequired,
                "The operation was dispatched, but its outcome is uncertain."),
            revision);

    private static HostResult<T> ClosedSession<T>(long revision) =>
        HostResult<T>.Fail(
            HostError.Create(HostErrorCode.SessionClosed, "The session is closed."),
            revision);

    private static HostResult<T> EngineFailure<T>(Exception exception, long revision) =>
        HostResult<T>.Fail(
            HostError.Create(HostErrorCode.EngineFailed, exception.Message),
            revision);

    private static HostResult<T> RevisionConflict<T>(long current, long expected) =>
        HostResult<T>.Fail(
            HostError.Create(
                HostErrorCode.RevisionConflict,
                $"Expected revision {expected}, but the current revision is {current}."),
            current);

    private static HostResult<T>? WorkspaceGraphFailure<T>(
        HostResult<WorkspaceGraphSnapshot>? result) =>
        result is HostResult<WorkspaceGraphSnapshot>.Failure failure
            ? HostResult<T>.Fail(failure.Error, failure.CurrentRevision)
            : null;

    private static string OperationForScope(CloseScopeKind scope) => scope switch
    {
        CloseScopeKind.Panel => ApplicationOperations.PanelClose,
        CloseScopeKind.Tab => ApplicationOperations.TabClose,
        CloseScopeKind.Workspace => ApplicationOperations.WorkspaceClose,
        CloseScopeKind.Window => ApplicationOperations.WindowClose,
        CloseScopeKind.Session => ApplicationOperations.SessionClose,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    private static SessionCloseOutcome MapCloseOutcome(PanelCloseOutcome outcome) => outcome switch
    {
        PanelCloseOutcome.GracefullyClosed => SessionCloseOutcome.GracefullyClosed,
        PanelCloseOutcome.ConfirmationRequired => SessionCloseOutcome.ConfirmationRequired,
        PanelCloseOutcome.Cancelled => SessionCloseOutcome.Cancelled,
        PanelCloseOutcome.ForceTerminated => SessionCloseOutcome.ForceTerminated,
        PanelCloseOutcome.EngineFailed => SessionCloseOutcome.EngineFailed,
        PanelCloseOutcome.AlreadyClosed => SessionCloseOutcome.AlreadyClosed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private static string DetailFor(PanelCloseOutcome outcome) => outcome switch
    {
        PanelCloseOutcome.GracefullyClosed => "Session closed gracefully.",
        PanelCloseOutcome.ConfirmationRequired => "The session still has active work.",
        PanelCloseOutcome.Cancelled => "Close cancelled.",
        PanelCloseOutcome.ForceTerminated => "Session force-terminated after confirmation.",
        PanelCloseOutcome.EngineFailed => "The terminal engine failed while closing.",
        PanelCloseOutcome.AlreadyClosed => "Session was already closed.",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private static string Fingerprint(string operation, params string[] values)
    {
        var material = string.Join('\u001f', [operation, .. values]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
