using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private const string InvalidAgentProcessResultCode =
        "processes_result_invalid";
    private const string AgentProcessCaptureFailedCode =
        "processes_capture_failed";

    public async ValueTask<HostResult<AgentProcessListResult>>
        RunAgentProcessListAsync(
            AgentAuthorizationId authorizationId,
            AgentProcessListAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentProcessListActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentProcessListResult>(
                "The governed local-process execution bridge is not composed.",
                revision: 0);
        }

        AgentProcessDispatch? dispatch = null;
        AgentActionPermit? permit = null;
        HostResult<AgentProcessListResult>? preDispatchFailure = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentProcessListResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var exactContextResult = ResolveExactAgentContext(
                action.Proposal.Target);
            if (exactContextResult
                is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentProcessListResult>.Fail(
                    contextFailure.Error,
                    contextFailure.CurrentRevision);
            }

            var exactContext =
                ((HostResult<AgentContextSnapshot>.Success)exactContextResult).Value;
            revision = exactContext.Revision;
            var exactPanel = exactContext.Panels.SingleOrDefault(
                panel => panel.PanelId == action.Request.PanelId);
            if (exactPanel?.SessionId is not { } sessionId
                || exactPanel.SessionRevision is not long expectedSessionRevision)
            {
                return InvalidAgentProcessAction(
                    "The exact Process Monitor context has no matching live session.",
                    revision);
            }

            if (!TryGetSession(sessionId, out var session))
            {
                return NotFound<AgentProcessListResult>("session", revision);
            }

            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentProcessListActionComposer.BindForExecution(
                    action,
                    exactContext);
                dispatch = CaptureAgentProcessDispatch(
                    action.Request,
                    session,
                    expectedSessionRevision,
                    exactPanel.WorkspaceRevision,
                    exactPanel.GraphSequence,
                    revision,
                    binding);
            }
            catch (AgentProcessDispatchException exception)
            {
                return HostResult<AgentProcessListResult>.Fail(
                    exception.Error,
                    revision);
            }
            catch (ArgumentException)
            {
                return InvalidAgentProcessAction(
                    "The prepared action no longer matches the exact local Process Monitor.",
                    revision);
            }
            catch (InvalidOperationException)
            {
                return InvalidAgentProcessAction(
                    "The prepared action no longer matches its typed process request.",
                    revision);
            }

            var permitResult = await _agentAuthorizationConsumer
                .ConsumeAsync(
                    authorizationId,
                    binding,
                    cancellationToken)
                .ConfigureAwait(false);
            if (permitResult is AgentPermitResult.Denied denied)
            {
                return MapAgentProcessAuthorizationFailure(
                    denied.Error,
                    revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            preDispatchFailure = RevalidateAgentProcessDispatch(
                action,
                dispatch,
                permit,
                binding,
                cancellationToken,
                out revision);
        }
        catch (AgentProcessDispatchException exception) when (permit is null)
        {
            return HostResult<AgentProcessListResult>.Fail(
                exception.Error,
                revision);
        }
        catch (AgentProcessDispatchException exception)
        {
            preDispatchFailure = HostResult<AgentProcessListResult>.Fail(
                exception.Error,
                revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentProcessListResult>(revision);
        }
        catch (OperationCanceledException)
        {
            preDispatchFailure = CancelledAgentProcessAction(
                permit!,
                dispatch?.RuntimeCancellation ?? default,
                cancellationToken,
                revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentProcessListResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            preDispatchFailure = CancelledAgentProcessAction(
                permit!,
                dispatch?.RuntimeCancellation ?? default,
                cancellationToken,
                revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentProcessListResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The local-process authorization broker is unavailable.",
                    retryable: true),
                revision);
        }
        catch (Exception)
        {
            preDispatchFailure = AgentProcessEngineFailure(revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (preDispatchFailure is not null)
        {
            return await CompleteAgentProcessActionAsync(
                    permit!,
                    preDispatchFailure)
                .ConfigureAwait(false);
        }

        return await CaptureAndCompleteAgentProcessListAsync(
                action,
                dispatch!,
                permit!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<HostResult<AgentProcessListResult>>
        CaptureAndCompleteAgentProcessListAsync(
            AgentProcessListAction action,
            AgentProcessDispatch dispatch,
            AgentActionPermit permit,
            CancellationToken callerCancellation)
    {
        HostResult<AgentProcessListResult>? result = null;
        MonitorPanelResult<ProcessMonitorSnapshot>? captured = null;
        try
        {
            using var operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    permit.CancellationToken,
                    dispatch.RuntimeCancellation,
                    callerCancellation);
            if (operationCancellation.IsCancellationRequested)
            {
                result = CancelledAgentProcessAction(
                    permit,
                    dispatch.RuntimeCancellation,
                    callerCancellation,
                    dispatch.InitialRevision);
            }
            else
            {
                captured = await dispatch.Processes
                    .ListProcessesAsync(
                        dispatch.Query,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result = CancelledAgentProcessAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                dispatch.InitialRevision);
        }
        catch (ObjectDisposedException)
        {
            result = CancelledAgentProcessAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                dispatch.InitialRevision);
        }
        catch (Exception)
        {
            result = AgentProcessEngineFailure(
                dispatch.InitialRevision);
        }

        if (result is null)
        {
            await _sessionGraphGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var driftFailure = RevalidateAgentProcessDispatch(
                    action,
                    dispatch,
                    permit,
                    dispatch.Binding,
                    callerCancellation,
                    out var currentRevision);
                if (driftFailure is not null)
                {
                    result = driftFailure;
                }
                else if (captured is not { IsSuccess: true })
                {
                    result = MapAgentProcessMonitorFailure(
                        captured?.Error,
                        currentRevision);
                }
                else
                {
                    try
                    {
                        var projection =
                            _agentProcessListActionComposer!.Project(
                                action,
                                captured.Value!);
                        result = permit.CancellationToken.IsCancellationRequested
                            || dispatch.RuntimeCancellation.IsCancellationRequested
                            || callerCancellation.IsCancellationRequested
                                ? CancelledAgentProcessAction(
                                    permit,
                                    dispatch.RuntimeCancellation,
                                    callerCancellation,
                                    currentRevision)
                                : HostResult<AgentProcessListResult>.Succeed(
                                    projection,
                                    currentRevision);
                    }
                    catch (Exception)
                    {
                        result = InvalidAgentProcessResult(
                            currentRevision);
                    }
                }
            }
            finally
            {
                _sessionGraphGate.Release();
            }
        }

        return await CompleteAgentProcessActionAsync(
                permit,
                result)
            .ConfigureAwait(false);
    }

    private HostResult<AgentProcessListResult>?
        RevalidateAgentProcessDispatch(
            AgentProcessListAction action,
            AgentProcessDispatch dispatch,
            AgentActionPermit permit,
            AgentActionExecutionBinding consumedBinding,
            CancellationToken callerCancellation,
            out long revision)
    {
        revision = dispatch.InitialRevision;
        if (!HasAgentProcessAuthorization(permit.Authorization))
        {
            return InvalidAgentProcessAction(
                "The consumed authorization does not grant local process observation.",
                revision);
        }

        if (permit.CancellationToken.IsCancellationRequested
            || dispatch.RuntimeCancellation.IsCancellationRequested
            || callerCancellation.IsCancellationRequested)
        {
            return CancelledAgentProcessAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                revision);
        }

        var currentContextResult = ResolveExactAgentContext(
            action.Proposal.Target);
        if (currentContextResult
            is HostResult<AgentContextSnapshot>.Failure contextFailure)
        {
            return HostResult<AgentProcessListResult>.Fail(
                contextFailure.Error,
                contextFailure.CurrentRevision);
        }

        var currentContext =
            ((HostResult<AgentContextSnapshot>.Success)currentContextResult).Value;
        revision = currentContext.Revision;
        AgentActionExecutionBinding currentBinding;
        try
        {
            currentBinding = _agentProcessListActionComposer!
                .BindForExecution(action, currentContext);
        }
        catch (ArgumentException)
        {
            return InvalidAgentProcessAction(
                "The exact Process Monitor target changed during authorization or capture.",
                revision);
        }
        catch (InvalidOperationException)
        {
            return InvalidAgentProcessAction(
                "The prepared process request changed during authorization or capture.",
                revision);
        }

        if (!AgentProcessBindingsMatch(
                consumedBinding,
                currentBinding)
            || !AuthorizationMatchesBinding(
                permit.Authorization,
                currentBinding))
        {
            return InvalidAgentProcessAction(
                "The exact Process Monitor execution binding changed before projection.",
                revision);
        }

        var currentPanel = currentContext.Panels.SingleOrDefault(
            panel => panel.PanelId == action.Request.PanelId
                && panel.SessionId == dispatch.Session.Id);
        if (currentPanel?.SessionRevision
                != dispatch.ExpectedSessionRevision
            || currentPanel.WorkspaceRevision
                != dispatch.ExpectedWorkspaceRevision
            || currentPanel.GraphSequence
                != dispatch.ExpectedGraphSequence
            || currentPanel.Kind != PanelKind.ProcessMonitor
            || !currentPanel.Capabilities.Contains(
                SessionCapabilities.ProcessesList,
                StringComparer.Ordinal))
        {
            return InvalidAgentProcessAction(
                "The exact Process Monitor session changed before projection.",
                revision);
        }

        if (!TryGetSession(dispatch.Session.Id, out var currentSession)
            || !ReferenceEquals(currentSession, dispatch.Session))
        {
            return InvalidAgentProcessAction(
                "The exact Process Monitor session was replaced before projection.",
                revision);
        }

        if (!HasLiveAgentProcessCapability(dispatch))
        {
            return HostResult<AgentProcessListResult>.Fail(
                new HostError(
                    HostErrorCode.CapabilityNotSupported,
                    "processes_unavailable",
                    "The local Process Monitor no longer supports process listing."),
                revision);
        }

        if (!dispatch.Session.CanExecuteAgentProcessList(
                dispatch.Processes,
                dispatch.ExpectedSessionRevision,
                dispatch.RuntimeCancellation))
        {
            return InvalidAgentProcessAction(
                "The exact Process Monitor scope changed before projection.",
                revision);
        }

        return null;
    }

    private static AgentProcessDispatch CaptureAgentProcessDispatch(
        AgentProcessListRequest request,
        HostedSession session,
        long expectedSessionRevision,
        long expectedWorkspaceRevision,
        long expectedGraphSequence,
        long initialRevision,
        AgentActionExecutionBinding binding)
    {
        var descriptor = session.Snapshot().Descriptor;
        if (descriptor.Lifecycle != SessionLifecycle.Active)
        {
            throw AgentProcessDispatchFailure(
                HostErrorCode.SessionClosed,
                "The exact Process Monitor session is no longer active.");
        }

        if (descriptor.Revision != expectedSessionRevision)
        {
            throw AgentProcessDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The exact Process Monitor revision changed before authorization.");
        }

        if (descriptor.Owner.PanelId != request.PanelId
            || session.Engine is not IProcessMonitorPanelSession processes
            || session.Engine.Kind != PanelKind.ProcessMonitor)
        {
            throw AgentProcessDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The exact session is not the requested local Process Monitor.");
        }

        if (!descriptor.Capabilities.Contains(
                SessionCapabilities.ProcessesList)
            || !processes.Capabilities.Contains(
                SessionCapabilities.ProcessesList))
        {
            throw AgentProcessDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The local Process Monitor does not support process listing.");
        }

        return new AgentProcessDispatch(
            session,
            processes,
            new ProcessMonitorQuery(
                request.Limit,
                request.Sort,
                request.Offset,
                request.NameContains,
                request.ProcessId),
            expectedSessionRevision,
            expectedWorkspaceRevision,
            expectedGraphSequence,
            session.CaptureRuntimeAuthority(),
            initialRevision,
            binding);
    }

    private static bool HasLiveAgentProcessCapability(
        AgentProcessDispatch dispatch)
    {
        try
        {
            return dispatch.Processes.Capabilities.Contains(
                SessionCapabilities.ProcessesList);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasAgentProcessAuthorization(
        AgentActionAuthorization authorization) =>
        string.Equals(
            authorization.ToolName,
            BuiltInAgentTools.ProcessesList,
            StringComparison.Ordinal)
        && BuiltInAgentTools.Catalog.TryGet(
            BuiltInAgentTools.ProcessesList,
            out var descriptor)
        && descriptor!.Capability == AgentCapability.ProcessData
        && descriptor.Risk == AgentActionRisk.Observation;

    private static bool AgentProcessBindingsMatch(
        AgentActionExecutionBinding left,
        AgentActionExecutionBinding right) =>
        left.ActionId == right.ActionId
        && left.RunId == right.RunId
        && left.ActorId == right.ActorId
        && string.Equals(
            left.ToolName,
            right.ToolName,
            StringComparison.Ordinal)
        && left.Target == right.Target
        && left.TargetIdentity == right.TargetIdentity
        && left.TargetFingerprint == right.TargetFingerprint
        && left.ArgumentDigest == right.ArgumentDigest
        && left.PolicyGeneration == right.PolicyGeneration;

    private async ValueTask<HostResult<AgentProcessListResult>>
        CompleteAgentProcessActionAsync(
            AgentActionPermit permit,
            HostResult<AgentProcessListResult> result)
    {
        var completion = AgentProcessCompletion(result, permit);
        return await CompleteConsumedAgentActionAsync(
                permit,
                completion,
                result,
                AgentProcessResultRevision(result))
            .ConfigureAwait(false);
    }

    private AgentActionCompletion AgentProcessCompletion(
        HostResult<AgentProcessListResult> result,
        AgentActionPermit permit)
    {
        var (outcome, stableCode, resultCount) = result switch
        {
            HostResult<AgentProcessListResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (
                    AgentActionOutcome.Cancelled,
                    failure.Error.StableCode,
                    (int?)null),
            HostResult<AgentProcessListResult>.Failure failure =>
                (
                    AgentActionOutcome.Failed,
                    failure.Error.StableCode,
                    (int?)null),
            HostResult<AgentProcessListResult>.Success success =>
                (
                    AgentActionOutcome.Succeeded,
                    "processes_listed",
                    success.Value.ReturnedCount),
            _ => throw new InvalidOperationException(
                "A governed process dispatch returned an unknown result."),
        };
        return Completion(
            permit,
            outcome,
            stableCode,
            resultCount);
    }

    private static HostResult<AgentProcessListResult>
        MapAgentProcessMonitorFailure(
            MonitorPanelError? error,
            long revision)
    {
        var mapped = error?.Code switch
        {
            MonitorPanelErrorCode.InvalidQuery =>
                (
                    HostErrorCode.EngineFailed,
                    InvalidAgentProcessResultCode,
                    false),
            MonitorPanelErrorCode.Unavailable =>
                (
                    HostErrorCode.EngineFailed,
                    "processes_unavailable",
                    true),
            MonitorPanelErrorCode.AccessDenied =>
                (
                    HostErrorCode.EngineFailed,
                    "processes_unavailable",
                    false),
            MonitorPanelErrorCode.CaptureFailed =>
                (
                    HostErrorCode.EngineFailed,
                    AgentProcessCaptureFailedCode,
                    true),
            MonitorPanelErrorCode.SessionClosed =>
                (
                    HostErrorCode.SessionClosed,
                    "processes_unavailable",
                    false),
            MonitorPanelErrorCode.Cancelled =>
                (
                    HostErrorCode.Cancelled,
                    "cancelled",
                    false),
            _ =>
                (
                    HostErrorCode.EngineFailed,
                    InvalidAgentProcessResultCode,
                    false),
        };
        return HostResult<AgentProcessListResult>.Fail(
            new HostError(
                mapped.Item1,
                mapped.Item2,
                "The local Process Monitor could not complete the governed observation.",
                mapped.Item3),
            revision);
    }

    private static HostResult<AgentProcessListResult>
        MapAgentProcessAuthorizationFailure(
            AgentAuthorizationError error,
            long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                HostError.Create(
                    HostErrorCode.DeadlineExceeded,
                    "The one-action process authorization has expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                HostError.Create(
                    HostErrorCode.Cancelled,
                    "The governed process observation was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The process-agent audit trail is unavailable.",
                    retryable: true),
            _ => HostError.Create(
                HostErrorCode.InvalidRequest,
                "The exact one-action process authorization was rejected."),
        };
        return HostResult<AgentProcessListResult>.Fail(
            hostError,
            revision);
    }

    private static HostResult<AgentProcessListResult>
        CancelledAgentProcessAction(
            AgentActionPermit permit,
            CancellationToken runtimeCancellation,
            CancellationToken callerCancellation,
            long revision)
    {
        var stableCode = permit.CancellationToken.IsCancellationRequested
            ? "authority_revoked"
            : runtimeCancellation.IsCancellationRequested
                ? "session_revoked"
                : callerCancellation.IsCancellationRequested
                    ? "caller_cancelled"
                    : "operation_cancelled";
        return HostResult<AgentProcessListResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                stableCode,
                "The governed process observation was cancelled."),
            revision);
    }

    private static HostResult<AgentProcessListResult>
        InvalidAgentProcessAction(
            string message,
            long revision) =>
        HostResult<AgentProcessListResult>.Fail(
            HostError.Create(
                HostErrorCode.InvalidRequest,
                message),
            revision);

    private static HostResult<AgentProcessListResult>
        InvalidAgentProcessResult(long revision) =>
        HostResult<AgentProcessListResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                InvalidAgentProcessResultCode,
                "The local Process Monitor returned an invalid governed result."),
            revision);

    private static HostResult<AgentProcessListResult>
        AgentProcessEngineFailure(long revision) =>
        HostResult<AgentProcessListResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                AgentProcessCaptureFailedCode,
                "The local Process Monitor could not complete the governed observation.",
                Retryable: true),
            revision);

    private static long AgentProcessResultRevision(
        HostResult<AgentProcessListResult> result) =>
        result switch
        {
            HostResult<AgentProcessListResult>.Success success =>
                success.ResultingRevision,
            HostResult<AgentProcessListResult>.Failure failure =>
                failure.CurrentRevision,
            _ => throw new InvalidOperationException(
                "A governed process action returned an unknown result."),
        };

    private static AgentProcessDispatchException
        AgentProcessDispatchFailure(
            HostErrorCode code,
            string message) =>
        new(HostError.Create(code, message));

    private sealed record AgentProcessDispatch(
        HostedSession Session,
        IProcessMonitorPanelSession Processes,
        ProcessMonitorQuery Query,
        long ExpectedSessionRevision,
        long ExpectedWorkspaceRevision,
        long ExpectedGraphSequence,
        CancellationToken RuntimeCancellation,
        long InitialRevision,
        AgentActionExecutionBinding Binding);

    private sealed class AgentProcessDispatchException(HostError error)
        : Exception(error.Message)
    {
        public HostError Error { get; } = error;

    }
}
