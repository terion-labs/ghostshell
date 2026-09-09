using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private const string InvalidAgentStatisticsResultCode =
        "statistics_result_invalid";
    private const string AgentStatisticsCaptureFailedCode =
        "statistics_capture_failed";

    public async ValueTask<HostResult<AgentStatisticsReadResult>>
        RunAgentStatisticsReadAsync(
            AgentAuthorizationId authorizationId,
            AgentStatisticsReadAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentStatisticsReadActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentStatisticsReadResult>(
                "The governed Statistics execution bridge is not composed.",
                revision: 0);
        }

        AgentStatisticsDispatch? dispatch = null;
        AgentActionPermit? permit = null;
        HostResult<AgentStatisticsReadResult>? preDispatchFailure = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentStatisticsReadResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var exactContextResult = ResolveExactAgentContext(
                action.Proposal.Target);
            if (exactContextResult
                is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentStatisticsReadResult>.Fail(
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
                return InvalidAgentStatisticsAction(
                    "The exact Statistics context has no matching live session.",
                    revision);
            }

            if (!TryGetSession(sessionId, out var session))
            {
                return NotFound<AgentStatisticsReadResult>("session", revision);
            }

            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentStatisticsReadActionComposer.BindForExecution(
                    action,
                    exactContext);
                dispatch = CaptureAgentStatisticsDispatch(
                    action.Request,
                    session,
                    expectedSessionRevision,
                    exactPanel.WorkspaceRevision,
                    exactPanel.GraphSequence,
                    revision,
                    binding);
            }
            catch (AgentStatisticsDispatchException exception)
            {
                return HostResult<AgentStatisticsReadResult>.Fail(
                    exception.Error,
                    revision);
            }
            catch (ArgumentException)
            {
                return InvalidAgentStatisticsAction(
                    "The prepared action no longer matches the exact Statistics panel.",
                    revision);
            }
            catch (InvalidOperationException)
            {
                return InvalidAgentStatisticsAction(
                    "The prepared action no longer matches its typed statistics request.",
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
                return MapAgentStatisticsAuthorizationFailure(
                    denied.Error,
                    revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            preDispatchFailure = RevalidateAgentStatisticsDispatch(
                action,
                dispatch,
                permit,
                binding,
                cancellationToken,
                out revision);
        }
        catch (AgentStatisticsDispatchException exception) when (permit is null)
        {
            return HostResult<AgentStatisticsReadResult>.Fail(
                exception.Error,
                revision);
        }
        catch (AgentStatisticsDispatchException exception)
        {
            preDispatchFailure = HostResult<AgentStatisticsReadResult>.Fail(
                exception.Error,
                revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentStatisticsReadResult>(revision);
        }
        catch (OperationCanceledException)
        {
            preDispatchFailure = CancelledAgentStatisticsAction(
                permit!,
                dispatch?.RuntimeCancellation ?? default,
                cancellationToken,
                revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentStatisticsReadResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            preDispatchFailure = CancelledAgentStatisticsAction(
                permit!,
                dispatch?.RuntimeCancellation ?? default,
                cancellationToken,
                revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentStatisticsReadResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The Statistics authorization broker is unavailable.",
                    retryable: true),
                revision);
        }
        catch (Exception)
        {
            preDispatchFailure = AgentStatisticsEngineFailure(revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (preDispatchFailure is not null)
        {
            return await CompleteAgentStatisticsActionAsync(
                    permit!,
                    preDispatchFailure)
                .ConfigureAwait(false);
        }

        return await CaptureAndCompleteAgentStatisticsAsync(
                action,
                dispatch!,
                permit!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<HostResult<AgentStatisticsReadResult>>
        CaptureAndCompleteAgentStatisticsAsync(
            AgentStatisticsReadAction action,
            AgentStatisticsDispatch dispatch,
            AgentActionPermit permit,
            CancellationToken callerCancellation)
    {
        HostResult<AgentStatisticsReadResult>? result = null;
        MonitorPanelResult<SystemStatisticsSnapshot>? captured = null;
        try
        {
            using var operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    permit.CancellationToken,
                    dispatch.RuntimeCancellation,
                    callerCancellation);
            if (operationCancellation.IsCancellationRequested)
            {
                result = CancelledAgentStatisticsAction(
                    permit,
                    dispatch.RuntimeCancellation,
                    callerCancellation,
                    dispatch.InitialRevision);
            }
            else
            {
                captured = await dispatch.Statistics
                    .ReadStatisticsAsync(operationCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result = CancelledAgentStatisticsAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                dispatch.InitialRevision);
        }
        catch (ObjectDisposedException)
        {
            result = CancelledAgentStatisticsAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                dispatch.InitialRevision);
        }
        catch (Exception)
        {
            result = AgentStatisticsEngineFailure(dispatch.InitialRevision);
        }

        if (result is null)
        {
            await _sessionGraphGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var driftFailure = RevalidateAgentStatisticsDispatch(
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
                    result = MapAgentStatisticsMonitorFailure(
                        captured?.Error,
                        currentRevision);
                }
                else
                {
                    try
                    {
                        var projection =
                            _agentStatisticsReadActionComposer!.Project(
                                action,
                                captured.Value!);
                        result = permit.CancellationToken.IsCancellationRequested
                            || dispatch.RuntimeCancellation.IsCancellationRequested
                            || callerCancellation.IsCancellationRequested
                                ? CancelledAgentStatisticsAction(
                                    permit,
                                    dispatch.RuntimeCancellation,
                                    callerCancellation,
                                    currentRevision)
                                : HostResult<AgentStatisticsReadResult>.Succeed(
                                    projection,
                                    currentRevision);
                    }
                    catch (Exception)
                    {
                        result = InvalidAgentStatisticsResult(currentRevision);
                    }
                }
            }
            finally
            {
                _sessionGraphGate.Release();
            }
        }

        return await CompleteAgentStatisticsActionAsync(permit, result)
            .ConfigureAwait(false);
    }

    private HostResult<AgentStatisticsReadResult>?
        RevalidateAgentStatisticsDispatch(
            AgentStatisticsReadAction action,
            AgentStatisticsDispatch dispatch,
            AgentActionPermit permit,
            AgentActionExecutionBinding consumedBinding,
            CancellationToken callerCancellation,
            out long revision)
    {
        revision = dispatch.InitialRevision;
        if (!HasAgentStatisticsAuthorization(permit.Authorization))
        {
            return InvalidAgentStatisticsAction(
                "The consumed authorization does not grant Statistics observation.",
                revision);
        }

        if (permit.CancellationToken.IsCancellationRequested
            || dispatch.RuntimeCancellation.IsCancellationRequested
            || callerCancellation.IsCancellationRequested)
        {
            return CancelledAgentStatisticsAction(
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
            return HostResult<AgentStatisticsReadResult>.Fail(
                contextFailure.Error,
                contextFailure.CurrentRevision);
        }

        var currentContext =
            ((HostResult<AgentContextSnapshot>.Success)currentContextResult).Value;
        revision = currentContext.Revision;
        AgentActionExecutionBinding currentBinding;
        try
        {
            currentBinding = _agentStatisticsReadActionComposer!
                .BindForExecution(action, currentContext);
        }
        catch (ArgumentException)
        {
            return InvalidAgentStatisticsAction(
                "The exact Statistics target changed during authorization or capture.",
                revision);
        }
        catch (InvalidOperationException)
        {
            return InvalidAgentStatisticsAction(
                "The prepared statistics request changed during authorization or capture.",
                revision);
        }

        if (!AgentStatisticsBindingsMatch(consumedBinding, currentBinding)
            || !AuthorizationMatchesBinding(
                permit.Authorization,
                currentBinding))
        {
            return InvalidAgentStatisticsAction(
                "The exact Statistics execution binding changed before projection.",
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
            || currentPanel.Kind != PanelKind.Statistics
            || !currentPanel.Capabilities.Contains(
                SessionCapabilities.StatisticsRead,
                StringComparer.Ordinal))
        {
            return InvalidAgentStatisticsAction(
                "The exact Statistics session changed before projection.",
                revision);
        }

        if (!TryGetSession(dispatch.Session.Id, out var currentSession)
            || !ReferenceEquals(currentSession, dispatch.Session))
        {
            return InvalidAgentStatisticsAction(
                "The exact Statistics session was replaced before projection.",
                revision);
        }

        if (!HasLiveAgentStatisticsCapability(dispatch))
        {
            return HostResult<AgentStatisticsReadResult>.Fail(
                new HostError(
                    HostErrorCode.CapabilityNotSupported,
                    "statistics_unavailable",
                    "The Statistics panel no longer supports reading."),
                revision);
        }

        if (!dispatch.Session.CanExecuteAgentStatisticsRead(
                dispatch.Statistics,
                dispatch.ExpectedSessionRevision,
                dispatch.RuntimeCancellation))
        {
            return InvalidAgentStatisticsAction(
                "The exact Statistics scope changed before projection.",
                revision);
        }

        return null;
    }

    private static AgentStatisticsDispatch CaptureAgentStatisticsDispatch(
        AgentStatisticsReadRequest request,
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
            throw AgentStatisticsDispatchFailure(
                HostErrorCode.SessionClosed,
                "The exact Statistics session is no longer active.");
        }

        if (descriptor.Revision != expectedSessionRevision)
        {
            throw AgentStatisticsDispatchFailure(
                HostErrorCode.InvalidRequest,
                "The exact Statistics revision changed before authorization.");
        }

        if (descriptor.Owner.PanelId != request.PanelId
            || session.Engine is not IStatisticsPanelSession statistics
            || session.Engine.Kind != PanelKind.Statistics)
        {
            throw AgentStatisticsDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The exact session is not the requested Statistics panel.");
        }

        if (!descriptor.Capabilities.Contains(
                SessionCapabilities.StatisticsRead)
            || !statistics.Capabilities.Contains(
                SessionCapabilities.StatisticsRead))
        {
            throw AgentStatisticsDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The Statistics panel does not support reading.");
        }

        return new AgentStatisticsDispatch(
            session,
            statistics,
            expectedSessionRevision,
            expectedWorkspaceRevision,
            expectedGraphSequence,
            session.CaptureRuntimeAuthority(),
            initialRevision,
            binding);
    }

    private static bool HasLiveAgentStatisticsCapability(
        AgentStatisticsDispatch dispatch)
    {
        try
        {
            return dispatch.Statistics.Capabilities.Contains(
                SessionCapabilities.StatisticsRead);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasAgentStatisticsAuthorization(
        AgentActionAuthorization authorization) =>
        string.Equals(
            authorization.ToolName,
            BuiltInAgentTools.StatisticsRead,
            StringComparison.Ordinal)
        && BuiltInAgentTools.Catalog.TryGet(
            BuiltInAgentTools.StatisticsRead,
            out var descriptor)
        && descriptor!.Capability == AgentCapability.SystemData
        && descriptor.Risk == AgentActionRisk.Observation;

    private static bool AgentStatisticsBindingsMatch(
        AgentActionExecutionBinding left,
        AgentActionExecutionBinding right) =>
        left.ActionId == right.ActionId
        && left.RunId == right.RunId
        && left.ActorId == right.ActorId
        && string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal)
        && left.Target == right.Target
        && left.TargetIdentity == right.TargetIdentity
        && left.TargetFingerprint == right.TargetFingerprint
        && left.ArgumentDigest == right.ArgumentDigest
        && left.PolicyGeneration == right.PolicyGeneration;

    private async ValueTask<HostResult<AgentStatisticsReadResult>>
        CompleteAgentStatisticsActionAsync(
            AgentActionPermit permit,
            HostResult<AgentStatisticsReadResult> result)
    {
        var completion = AgentStatisticsCompletion(result, permit);
        return await CompleteConsumedAgentActionAsync(
                permit,
                completion,
                result,
                AgentStatisticsResultRevision(result))
            .ConfigureAwait(false);
    }

    private AgentActionCompletion AgentStatisticsCompletion(
        HostResult<AgentStatisticsReadResult> result,
        AgentActionPermit permit)
    {
        var (outcome, stableCode, resultCount) = result switch
        {
            HostResult<AgentStatisticsReadResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (AgentActionOutcome.Cancelled, failure.Error.StableCode, (int?)null),
            HostResult<AgentStatisticsReadResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode, (int?)null),
            HostResult<AgentStatisticsReadResult>.Success =>
                (AgentActionOutcome.Succeeded, "statistics_read", 1),
            _ => throw new InvalidOperationException(
                "A governed Statistics dispatch returned an unknown result."),
        };
        return Completion(permit, outcome, stableCode, resultCount);
    }

    private static HostResult<AgentStatisticsReadResult>
        MapAgentStatisticsMonitorFailure(
            MonitorPanelError? error,
            long revision)
    {
        var mapped = error?.Code switch
        {
            MonitorPanelErrorCode.InvalidQuery =>
                (HostErrorCode.EngineFailed, InvalidAgentStatisticsResultCode, false),
            MonitorPanelErrorCode.Unavailable =>
                (HostErrorCode.EngineFailed, "statistics_unavailable", true),
            MonitorPanelErrorCode.AccessDenied =>
                (HostErrorCode.EngineFailed, "statistics_unavailable", false),
            MonitorPanelErrorCode.CaptureFailed =>
                (HostErrorCode.EngineFailed, AgentStatisticsCaptureFailedCode, true),
            MonitorPanelErrorCode.SessionClosed =>
                (HostErrorCode.SessionClosed, "statistics_unavailable", false),
            MonitorPanelErrorCode.Cancelled =>
                (HostErrorCode.Cancelled, "cancelled", false),
            _ =>
                (HostErrorCode.EngineFailed, InvalidAgentStatisticsResultCode, false),
        };
        return HostResult<AgentStatisticsReadResult>.Fail(
            new HostError(
                mapped.Item1,
                mapped.Item2,
                "The Statistics panel could not complete the governed observation.",
                mapped.Item3),
            revision);
    }

    private static HostResult<AgentStatisticsReadResult>
        MapAgentStatisticsAuthorizationFailure(
            AgentAuthorizationError error,
            long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                HostError.Create(
                    HostErrorCode.DeadlineExceeded,
                    "The one-action Statistics authorization has expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                HostError.Create(
                    HostErrorCode.Cancelled,
                    "The governed Statistics observation was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The Statistics-agent audit trail is unavailable.",
                    retryable: true),
            _ => HostError.Create(
                HostErrorCode.InvalidRequest,
                "The exact one-action Statistics authorization was rejected."),
        };
        return HostResult<AgentStatisticsReadResult>.Fail(hostError, revision);
    }

    private static HostResult<AgentStatisticsReadResult>
        CancelledAgentStatisticsAction(
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
        return HostResult<AgentStatisticsReadResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                stableCode,
                "The governed Statistics observation was cancelled."),
            revision);
    }

    private static HostResult<AgentStatisticsReadResult>
        InvalidAgentStatisticsAction(string message, long revision) =>
        HostResult<AgentStatisticsReadResult>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            revision);

    private static HostResult<AgentStatisticsReadResult>
        InvalidAgentStatisticsResult(long revision) =>
        HostResult<AgentStatisticsReadResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                InvalidAgentStatisticsResultCode,
                "The Statistics panel returned an invalid governed result."),
            revision);

    private static HostResult<AgentStatisticsReadResult>
        AgentStatisticsEngineFailure(long revision) =>
        HostResult<AgentStatisticsReadResult>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                AgentStatisticsCaptureFailedCode,
                "The Statistics panel could not complete the governed observation.",
                Retryable: true),
            revision);

    private static long AgentStatisticsResultRevision(
        HostResult<AgentStatisticsReadResult> result) =>
        result switch
        {
            HostResult<AgentStatisticsReadResult>.Success success =>
                success.ResultingRevision,
            HostResult<AgentStatisticsReadResult>.Failure failure =>
                failure.CurrentRevision,
            _ => throw new InvalidOperationException(
                "A governed Statistics action returned an unknown result."),
        };

    private static AgentStatisticsDispatchException
        AgentStatisticsDispatchFailure(
            HostErrorCode code,
            string message) =>
        new(HostError.Create(code, message));

    private sealed record AgentStatisticsDispatch(
        HostedSession Session,
        IStatisticsPanelSession Statistics,
        long ExpectedSessionRevision,
        long ExpectedWorkspaceRevision,
        long ExpectedGraphSequence,
        CancellationToken RuntimeCancellation,
        long InitialRevision,
        AgentActionExecutionBinding Binding);

    private sealed class AgentStatisticsDispatchException(HostError error)
        : Exception(error.Message)
    {
        public HostError Error { get; } = error;

    }
}
