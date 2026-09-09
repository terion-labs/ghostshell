using Asura.Application;
using Asura.Core;
using Asura.Docker;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<AgentDockerReadResult>>
        RunAgentDockerReadAsync(
            AgentAuthorizationId authorizationId,
            AgentDockerReadAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentDockerReadActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentDockerReadResult>(
                "The governed Docker execution bridge is not composed.",
                0);
        }

        AgentDockerDispatch? dispatch = null;
        AgentActionPermit? permit = null;
        HostResult<AgentDockerReadResult>? preDispatchFailure = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentDockerReadResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var exactContextResult = ResolveExactAgentContext(action.Proposal.Target);
            if (exactContextResult
                is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentDockerReadResult>.Fail(
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
                return InvalidAgentDockerAction(
                    "The exact Docker context has no matching live session.",
                    revision);
            }

            if (!TryGetSession(sessionId, out var session))
            {
                return NotFound<AgentDockerReadResult>("session", revision);
            }

            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentDockerReadActionComposer.BindForExecution(
                    action,
                    exactContext);
                dispatch = CaptureAgentDockerDispatch(
                    action.Request,
                    session,
                    expectedSessionRevision,
                    exactPanel.WorkspaceRevision,
                    exactPanel.GraphSequence,
                    revision,
                    binding);
            }
            catch (AgentDockerDispatchException exception)
            {
                return HostResult<AgentDockerReadResult>.Fail(
                    exception.Error,
                    revision);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException)
            {
                return InvalidAgentDockerAction(
                    "The prepared Docker action no longer matches its exact typed request.",
                    revision);
            }

            var permitResult = await _agentAuthorizationConsumer
                .ConsumeAsync(authorizationId, binding, cancellationToken)
                .ConfigureAwait(false);
            if (permitResult is AgentPermitResult.Denied denied)
            {
                return MapAgentDockerAuthorizationFailure(denied.Error, revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            preDispatchFailure = RevalidateAgentDockerDispatch(
                action,
                dispatch,
                permit,
                binding,
                cancellationToken,
                out revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentDockerReadResult>(revision);
        }
        catch (OperationCanceledException)
        {
            preDispatchFailure = CancelledAgentDockerAction(
                permit!,
                dispatch?.RuntimeCancellation ?? default,
                cancellationToken,
                revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentDockerReadResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            preDispatchFailure = CancelledAgentDockerAction(
                permit!,
                dispatch?.RuntimeCancellation ?? default,
                cancellationToken,
                revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentDockerReadResult>.Fail(
                new HostError(
                    HostErrorCode.EngineFailed,
                    "docker_authorization_unavailable",
                    "The Docker authorization broker is unavailable.",
                    Retryable: true),
                revision);
        }
        catch (Exception)
        {
            preDispatchFailure = AgentDockerFailure(
                "docker_read_failed",
                revision,
                retryable: true);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (preDispatchFailure is not null)
        {
            return await CompleteAgentDockerActionAsync(permit!, preDispatchFailure)
                .ConfigureAwait(false);
        }

        return await CaptureAndCompleteAgentDockerReadAsync(
                action,
                dispatch!,
                permit!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<HostResult<AgentDockerReadResult>>
        CaptureAndCompleteAgentDockerReadAsync(
            AgentDockerReadAction action,
            AgentDockerDispatch dispatch,
            AgentActionPermit permit,
            CancellationToken callerCancellation)
    {
        HostResult<AgentDockerReadResult>? result = null;
        object? captured = null;
        try
        {
            using var operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    permit.CancellationToken,
                    dispatch.RuntimeCancellation,
                    callerCancellation);
            if (operationCancellation.IsCancellationRequested)
            {
                result = CancelledAgentDockerAction(
                    permit,
                    dispatch.RuntimeCancellation,
                    callerCancellation,
                    dispatch.InitialRevision);
            }
            else
            {
                captured = await ExecuteAgentDockerReadAsync(
                        dispatch,
                        action.Request,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result = CancelledAgentDockerAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                dispatch.InitialRevision);
        }
        catch (DockerReferenceExpiredException)
        {
            result = AgentDockerFailure(
                "docker_reference_expired",
                dispatch.InitialRevision,
                HostErrorCode.InvalidRequest);
        }
        catch (DockerOperationUnavailableException)
        {
            result = AgentDockerFailure(
                "docker_filesystem_unavailable",
                dispatch.InitialRevision,
                HostErrorCode.CapabilityNotSupported);
        }
        catch (ArgumentException)
        {
            result = AgentDockerFailure(
                "docker_read_rejected",
                dispatch.InitialRevision,
                HostErrorCode.InvalidRequest);
        }
        catch (InvalidDataException)
        {
            result = AgentDockerFailure(
                "docker_result_invalid",
                dispatch.InitialRevision);
        }
        catch (Exception)
        {
            result = AgentDockerFailure(
                "docker_read_failed",
                dispatch.InitialRevision,
                retryable: true);
        }

        if (result is null)
        {
            await _sessionGraphGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var driftFailure = RevalidateAgentDockerDispatch(
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
                else
                {
                    try
                    {
                        var projection = ProjectAgentDockerResult(action, captured!);
                        result = permit.CancellationToken.IsCancellationRequested
                            || dispatch.RuntimeCancellation.IsCancellationRequested
                            || callerCancellation.IsCancellationRequested
                                ? CancelledAgentDockerAction(
                                    permit,
                                    dispatch.RuntimeCancellation,
                                    callerCancellation,
                                    currentRevision)
                                : HostResult<AgentDockerReadResult>.Succeed(
                                    projection,
                                    currentRevision);
                    }
                    catch (Exception exception) when (exception is
                        ArgumentException
                        or InvalidOperationException
                        or OverflowException)
                    {
                        result = AgentDockerFailure(
                            "docker_result_invalid",
                            currentRevision);
                    }
                }
            }
            finally
            {
                _sessionGraphGate.Release();
            }
        }

        return await CompleteAgentDockerActionAsync(permit, result)
            .ConfigureAwait(false);
    }

    private HostResult<AgentDockerReadResult>? RevalidateAgentDockerDispatch(
        AgentDockerReadAction action,
        AgentDockerDispatch dispatch,
        AgentActionPermit permit,
        AgentActionExecutionBinding consumedBinding,
        CancellationToken callerCancellation,
        out long revision)
    {
        revision = dispatch.InitialRevision;
        if (!HasAgentDockerAuthorization(permit.Authorization, action.Request))
        {
            return InvalidAgentDockerAction(
                "The consumed authorization does not grant this Docker observation.",
                revision);
        }

        if (permit.CancellationToken.IsCancellationRequested
            || dispatch.RuntimeCancellation.IsCancellationRequested
            || callerCancellation.IsCancellationRequested)
        {
            return CancelledAgentDockerAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                revision);
        }

        var contextResult = ResolveExactAgentContext(action.Proposal.Target);
        if (contextResult is HostResult<AgentContextSnapshot>.Failure contextFailure)
        {
            return HostResult<AgentDockerReadResult>.Fail(
                contextFailure.Error,
                contextFailure.CurrentRevision);
        }

        var context = ((HostResult<AgentContextSnapshot>.Success)contextResult).Value;
        revision = context.Revision;
        AgentActionExecutionBinding currentBinding;
        try
        {
            currentBinding = _agentDockerReadActionComposer!
                .BindForExecution(action, context);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException)
        {
            return InvalidAgentDockerAction(
                "The exact Docker panel changed during authorization or capture.",
                revision);
        }

        if (!AgentDockerBindingsMatch(consumedBinding, currentBinding)
            || !AuthorizationMatchesBinding(permit.Authorization, currentBinding))
        {
            return InvalidAgentDockerAction(
                "The exact Docker execution binding changed before projection.",
                revision);
        }

        var panel = context.Panels.SingleOrDefault(candidate =>
            candidate.PanelId == action.Request.PanelId
            && candidate.SessionId == dispatch.Session.Id);
        if (panel?.SessionRevision != dispatch.ExpectedSessionRevision
            || panel.WorkspaceRevision != dispatch.ExpectedWorkspaceRevision
            || panel.GraphSequence != dispatch.ExpectedGraphSequence
            || panel.Kind != PanelKind.Docker
            || !panel.Capabilities.Contains(
                action.Request.RequiredSessionCapability,
                StringComparer.Ordinal))
        {
            return InvalidAgentDockerAction(
                "The exact hosted Docker session changed before projection.",
                revision);
        }

        if (!TryGetSession(dispatch.Session.Id, out var currentSession)
            || !ReferenceEquals(currentSession, dispatch.Session)
            || !dispatch.Session.CanExecuteAgentDockerRead(
                dispatch.Docker,
                dispatch.ExpectedBinding,
                dispatch.ExpectedEngineGeneration,
                dispatch.ExpectedSessionRevision,
                action.Request.RequiredSessionCapability,
                dispatch.RuntimeCancellation))
        {
            return InvalidAgentDockerAction(
                "The exact hosted Docker authority changed before projection.",
                revision);
        }

        return null;
    }

    private static AgentDockerDispatch CaptureAgentDockerDispatch(
        AgentDockerReadRequest request,
        HostedSession session,
        long expectedSessionRevision,
        long expectedWorkspaceRevision,
        long expectedGraphSequence,
        long initialRevision,
        AgentActionExecutionBinding binding)
    {
        var descriptor = session.Snapshot().Descriptor;
        if (descriptor.Lifecycle != SessionLifecycle.Active
            || descriptor.Revision != expectedSessionRevision)
        {
            throw AgentDockerDispatchFailure(
                HostErrorCode.SessionClosed,
                "The exact Docker session is no longer active.");
        }

        if (descriptor.Owner.PanelId != request.PanelId
            || session.Engine is not IDockerPanelSession docker
            || session.Engine.Kind != PanelKind.Docker)
        {
            throw AgentDockerDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The exact session does not support this Docker observation.");
        }

        if (!descriptor.Capabilities.Contains(request.RequiredSessionCapability)
            || !docker.Capabilities.Contains(request.RequiredSessionCapability))
        {
            throw AgentDockerDispatchFailure(
                HostErrorCode.CapabilityNotSupported,
                "The live Docker panel does not advertise this observation capability.");
        }

        return new AgentDockerDispatch(
            session,
            docker,
            docker.Binding,
            docker.State.EngineGeneration,
            expectedSessionRevision,
            expectedWorkspaceRevision,
            expectedGraphSequence,
            session.CaptureRuntimeAuthority(),
            initialRevision,
            binding);
    }

    private static async ValueTask<object> ExecuteAgentDockerReadAsync(
        AgentDockerDispatch dispatch,
        AgentDockerReadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return request switch
        {
            AgentDockerReadRequest.ReadState value => new AgentDockerStateCapture(
                dispatch.ExpectedEngineGeneration,
                Unwrap(
                    await dispatch.Docker.ReadStateAsync(
                            value.MaximumResourcesPerKind,
                            cancellationToken)
                        .ConfigureAwait(false),
                    hasOpaqueReference: false)),
            AgentDockerReadRequest.Inspect value => Unwrap(
                await dispatch.Docker.InspectAsync(value.Reference, cancellationToken)
                    .ConfigureAwait(false),
                hasOpaqueReference: true),
            AgentDockerReadRequest.Logs value => Unwrap(
                await dispatch.Docker.ReadLogsAsync(
                        value.ToSessionRequest(),
                        cancellationToken)
                    .ConfigureAwait(false),
                hasOpaqueReference: true),
            AgentDockerReadRequest.FilesList value => Unwrap(
                await dispatch.Docker.ListFilesAsync(
                        value.ToSessionRequest(),
                        cancellationToken)
                    .ConfigureAwait(false),
                hasOpaqueReference: true),
            AgentDockerReadRequest.FilesStat value => Unwrap(
                await dispatch.Docker.StatFileAsync(
                        value.ToSessionRequest(),
                        cancellationToken)
                    .ConfigureAwait(false),
                hasOpaqueReference: true),
            AgentDockerReadRequest.FileRead value => Unwrap(
                await dispatch.Docker.ReadFileAsync(
                        value.ToSessionRequest(),
                        cancellationToken)
                    .ConfigureAwait(false),
                hasOpaqueReference: true),
            _ => throw new InvalidOperationException(
                "The Docker request variant is unknown."),
        };
    }

    private AgentDockerReadResult ProjectAgentDockerResult(
        AgentDockerReadAction action,
        object captured) => (action.Request, captured) switch
        {
            (AgentDockerReadRequest.ReadState, AgentDockerStateCapture value) =>
                _agentDockerReadActionComposer!.Project(
                    action,
                    value.EngineGeneration,
                    value.Snapshot),
            (AgentDockerReadRequest.Inspect, DockerInspectionSnapshot value) =>
                _agentDockerReadActionComposer!.Project(action, value),
            (AgentDockerReadRequest.Logs, DockerContainerLogPage value) =>
                _agentDockerReadActionComposer!.Project(action, value),
            (AgentDockerReadRequest.FilesList, DockerFilePage value) =>
                _agentDockerReadActionComposer!.Project(action, value),
            (AgentDockerReadRequest.FilesStat, DockerFileEntry value) =>
                _agentDockerReadActionComposer!.Project(action, value),
            (AgentDockerReadRequest.FileRead, DockerFileSnapshot value) =>
                _agentDockerReadActionComposer!.Project(action, value),
            _ => throw new ArgumentException(
                "The hosted Docker panel returned a mismatched result type.",
                nameof(captured)),
        };

    private static T Unwrap<T>(DockerResult<T> result, bool hasOpaqueReference)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result is DockerResult<T>.Success success)
        {
            return success.Value;
        }

        var error = ((DockerResult<T>.Failure)result).Error;
        if (error.Code == DockerErrorCode.Cancelled)
        {
            throw new OperationCanceledException();
        }

        if (error.Code == DockerErrorCode.FileProtocolUnavailable)
        {
            throw new DockerOperationUnavailableException();
        }

        if (hasOpaqueReference
            && error.Code is DockerErrorCode.InvalidResponse
                or DockerErrorCode.FileNotFound
                or DockerErrorCode.NotDirectory)
        {
            throw new DockerReferenceExpiredException();
        }

        if (error.Code == DockerErrorCode.InvalidResponse)
        {
            throw new InvalidDataException("Docker returned an invalid result.");
        }

        throw new IOException("Docker could not complete the bounded observation.");
    }

    private static bool HasAgentDockerAuthorization(
        AgentActionAuthorization authorization,
        AgentDockerReadRequest request) =>
        string.Equals(authorization.ToolName, request.ToolName, StringComparison.Ordinal)
        && BuiltInAgentTools.Catalog.TryGet(request.ToolName, out var descriptor)
        && descriptor!.Capability == AgentCapability.DockerData
        && descriptor.Risk == AgentActionRisk.Observation;

    private static bool AgentDockerBindingsMatch(
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

    private async ValueTask<HostResult<AgentDockerReadResult>>
        CompleteAgentDockerActionAsync(
            AgentActionPermit permit,
            HostResult<AgentDockerReadResult> result)
    {
        var (outcome, stableCode, resultCount) = result switch
        {
            HostResult<AgentDockerReadResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (AgentActionOutcome.Cancelled, failure.Error.StableCode, (int?)null),
            HostResult<AgentDockerReadResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode, (int?)null),
            HostResult<AgentDockerReadResult>.Success success =>
                (AgentActionOutcome.Succeeded, "docker_read_completed", ResultCount(success.Value)),
            _ => throw new InvalidOperationException(
                "A governed Docker dispatch returned an unknown result."),
        };
        return await CompleteConsumedAgentActionAsync(
                permit,
                Completion(permit, outcome, stableCode, resultCount),
                result,
                AgentDockerResultRevision(result))
            .ConfigureAwait(false);
    }

    private static int ResultCount(AgentDockerReadResult result) => result switch
    {
        AgentDockerReadResult.State value =>
            value.Value.Snapshot.Containers.Count
            + value.Value.Snapshot.Images.Count
            + value.Value.Snapshot.Volumes.Count
            + value.Value.Snapshot.Networks.Count,
        AgentDockerReadResult.Inspection value => value.Value.Properties.Count,
        AgentDockerReadResult.Logs value => value.Value.Lines.Count,
        AgentDockerReadResult.Files value => value.Value.Entries.Count,
        AgentDockerReadResult.FileStat => 1,
        AgentDockerReadResult.FileText value => value.Value.Text.Length,
        _ => 0,
    };

    private static HostResult<AgentDockerReadResult>
        MapAgentDockerAuthorizationFailure(
            AgentAuthorizationError error,
            long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                new HostError(
                    HostErrorCode.DeadlineExceeded,
                    "docker_authorization_expired",
                    "The one-action Docker authorization expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                new HostError(
                    HostErrorCode.Cancelled,
                    "docker_read_cancelled",
                    "The governed Docker observation was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                new HostError(
                    HostErrorCode.EngineFailed,
                    "docker_audit_unavailable",
                    "The Docker-agent audit trail is unavailable.",
                    Retryable: true),
            _ => new HostError(
                HostErrorCode.InvalidRequest,
                "docker_authorization_rejected",
                "The exact one-action Docker authorization was rejected."),
        };
        return HostResult<AgentDockerReadResult>.Fail(hostError, revision);
    }

    private static HostResult<AgentDockerReadResult> CancelledAgentDockerAction(
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
        return HostResult<AgentDockerReadResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                stableCode,
                "The governed Docker observation was cancelled."),
            revision);
    }

    private static HostResult<AgentDockerReadResult> InvalidAgentDockerAction(
        string message,
        long revision) =>
        HostResult<AgentDockerReadResult>.Fail(
            new HostError(
                HostErrorCode.InvalidRequest,
                "docker_action_invalid",
                message),
            revision);

    private static HostResult<AgentDockerReadResult> AgentDockerFailure(
        string stableCode,
        long revision,
        HostErrorCode code = HostErrorCode.EngineFailed,
        bool retryable = false) =>
        HostResult<AgentDockerReadResult>.Fail(
            new HostError(
                code,
                stableCode,
                "The Docker panel could not complete the governed observation.",
                retryable),
            revision);

    private static long AgentDockerResultRevision(
        HostResult<AgentDockerReadResult> result) => result switch
        {
            HostResult<AgentDockerReadResult>.Success success => success.ResultingRevision,
            HostResult<AgentDockerReadResult>.Failure failure => failure.CurrentRevision,
            _ => throw new InvalidOperationException(
                "A governed Docker action returned an unknown result."),
        };

    private static AgentDockerDispatchException AgentDockerDispatchFailure(
        HostErrorCode code,
        string message) => new(HostError.Create(code, message));

    private sealed record AgentDockerStateCapture(
        DockerEngineGeneration EngineGeneration,
        DockerPanelSnapshot Snapshot);

    private sealed record AgentDockerDispatch(
        HostedSession Session,
        IDockerPanelSession Docker,
        DockerSessionBinding ExpectedBinding,
        DockerEngineGeneration ExpectedEngineGeneration,
        long ExpectedSessionRevision,
        long ExpectedWorkspaceRevision,
        long ExpectedGraphSequence,
        CancellationToken RuntimeCancellation,
        long InitialRevision,
        AgentActionExecutionBinding Binding);

    private sealed class AgentDockerDispatchException(HostError error)
        : Exception(error.Message)
    {
        public HostError Error { get; } = error;

    }

    private sealed class DockerReferenceExpiredException : Exception;

    private sealed class DockerOperationUnavailableException : Exception;
}
