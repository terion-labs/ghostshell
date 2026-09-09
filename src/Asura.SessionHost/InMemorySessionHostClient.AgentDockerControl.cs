using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<AgentDockerControlResult>>
        RunAgentDockerControlAsync(
            AgentAuthorizationId authorizationId,
            AgentDockerControlAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentDockerReadActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentDockerControlResult>(
                "The governed Docker lifecycle bridge is not composed.",
                0);
        }

        AgentDockerDispatch? dispatch = null;
        AgentActionPermit? permit = null;
        HostResult<AgentDockerControlResult>? failure = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentDockerControlResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var contextResult = ResolveExactAgentContext(action.Proposal.Target);
            if (contextResult is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentDockerControlResult>.Fail(
                    contextFailure.Error,
                    contextFailure.CurrentRevision);
            }

            var context = ((HostResult<AgentContextSnapshot>.Success)contextResult).Value;
            revision = context.Revision;
            var panel = context.Panels.SingleOrDefault(candidate =>
                candidate.PanelId == action.Request.PanelId);
            if (panel?.SessionId is not { } sessionId
                || panel.SessionRevision is not long sessionRevision
                || !TryGetSession(sessionId, out var session))
            {
                return ControlFailure(
                    "docker_action_invalid",
                    HostErrorCode.InvalidRequest,
                    revision);
            }

            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentDockerReadActionComposer.BindForExecution(action, context);
                dispatch = CaptureAgentDockerControlDispatch(
                    action.Request,
                    session,
                    sessionRevision,
                    panel.WorkspaceRevision,
                    panel.GraphSequence,
                    revision,
                    binding);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException)
            {
                return ControlFailure(
                    "docker_action_invalid",
                    HostErrorCode.InvalidRequest,
                    revision);
            }

            var permitResult = await _agentAuthorizationConsumer
                .ConsumeAsync(authorizationId, binding, cancellationToken)
                .ConfigureAwait(false);
            if (permitResult is AgentPermitResult.Denied denied)
            {
                return MapDockerControlAuthorizationFailure(denied.Error, revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            failure = RevalidateAgentDockerControl(
                action,
                dispatch,
                permit,
                binding,
                cancellationToken,
                out revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentDockerControlResult>(revision);
        }
        catch (OperationCanceledException)
        {
            failure = ControlFailure(
                "docker_container_control_cancelled",
                HostErrorCode.Cancelled,
                revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentDockerControlResult>(revision);
        }
        catch (Exception) when (permit is null)
        {
            return ControlFailure(
                "docker_authorization_unavailable",
                HostErrorCode.EngineFailed,
                revision,
                retryable: true);
        }
        catch (Exception)
        {
            failure = ControlFailure(
                "docker_container_control_failed",
                HostErrorCode.EngineFailed,
                revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (failure is not null)
        {
            return await CompleteAgentDockerControlAsync(permit!, failure)
                .ConfigureAwait(false);
        }

        HostResult<AgentDockerControlResult> result;
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                permit!.CancellationToken,
                dispatch!.RuntimeCancellation,
                cancellationToken);
            var controlled = await dispatch.Docker.ControlContainerAsync(
                    action.Request.ToSessionRequest(),
                    operation.Token)
                .ConfigureAwait(false);
            result = HostResult<AgentDockerControlResult>.Succeed(
                new AgentDockerControlResult(
                    action.Request.ToolName,
                    controlled.Outcome,
                    controlled.StableCode,
                    controlled.Retryable),
                dispatch.InitialRevision);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Once the permit crosses the control boundary, an exception cannot
            // prove that Docker did not receive the command.
            result = HostResult<AgentDockerControlResult>.Succeed(
                new AgentDockerControlResult(
                    action.Request.ToolName,
                    DockerContainerControlOutcome.OutcomeUnknown,
                    "docker_mutation_outcome_unknown",
                    Retryable: false),
                dispatch!.InitialRevision);
        }

        return await CompleteAgentDockerControlAsync(permit!, result)
            .ConfigureAwait(false);
    }

    private HostResult<AgentDockerControlResult>? RevalidateAgentDockerControl(
        AgentDockerControlAction action,
        AgentDockerDispatch dispatch,
        AgentActionPermit permit,
        AgentActionExecutionBinding consumedBinding,
        CancellationToken cancellationToken,
        out long revision)
    {
        revision = dispatch.InitialRevision;
        if (!HasAgentDockerControlAuthorization(permit.Authorization, action.Request))
        {
            return ControlFailure(
                "docker_authorization_rejected",
                HostErrorCode.InvalidRequest,
                revision);
        }

        if (permit.CancellationToken.IsCancellationRequested
            || dispatch.RuntimeCancellation.IsCancellationRequested
            || cancellationToken.IsCancellationRequested)
        {
            return ControlFailure(
                "docker_container_control_cancelled",
                HostErrorCode.Cancelled,
                revision);
        }

        var contextResult = ResolveExactAgentContext(action.Proposal.Target);
        if (contextResult is HostResult<AgentContextSnapshot>.Failure contextFailure)
        {
            return HostResult<AgentDockerControlResult>.Fail(
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
            return ControlFailure(
                "docker_action_invalid",
                HostErrorCode.InvalidRequest,
                revision);
        }

        var panel = context.Panels.SingleOrDefault(candidate =>
            candidate.PanelId == action.Request.PanelId
            && candidate.SessionId == dispatch.Session.Id);
        if (!AgentDockerBindingsMatch(consumedBinding, currentBinding)
            || !AuthorizationMatchesBinding(permit.Authorization, currentBinding)
            || panel?.SessionRevision != dispatch.ExpectedSessionRevision
            || panel.WorkspaceRevision != dispatch.ExpectedWorkspaceRevision
            || panel.GraphSequence != dispatch.ExpectedGraphSequence
            || !dispatch.Session.CanExecuteAgentDockerRead(
                dispatch.Docker,
                dispatch.ExpectedBinding,
                dispatch.ExpectedEngineGeneration,
                dispatch.ExpectedSessionRevision,
                action.Request.RequiredSessionCapability,
                dispatch.RuntimeCancellation))
        {
            return ControlFailure(
                "docker_action_invalid",
                HostErrorCode.InvalidRequest,
                revision);
        }

        return null;
    }

    private static AgentDockerDispatch CaptureAgentDockerControlDispatch(
        AgentDockerControlRequest request,
        HostedSession session,
        long expectedSessionRevision,
        long expectedWorkspaceRevision,
        long expectedGraphSequence,
        long initialRevision,
        AgentActionExecutionBinding binding)
    {
        var descriptor = session.Snapshot().Descriptor;
        if (descriptor.Lifecycle != SessionLifecycle.Active
            || descriptor.Revision != expectedSessionRevision
            || descriptor.Owner.PanelId != request.PanelId
            || session.Engine is not IDockerPanelSession docker
            || session.Engine.Kind != PanelKind.Docker
            || docker.State.ConnectionKind != ConnectionKind.Local
            || !descriptor.Capabilities.Contains(request.RequiredSessionCapability)
            || !docker.Capabilities.Contains(request.RequiredSessionCapability))
        {
            throw new InvalidOperationException(
                "The exact local Docker session cannot execute this mutation.");
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

    private static bool HasAgentDockerControlAuthorization(
        AgentActionAuthorization authorization,
        AgentDockerControlRequest request) =>
        string.Equals(authorization.ToolName, request.ToolName, StringComparison.Ordinal)
        && BuiltInAgentTools.Catalog.TryGet(request.ToolName, out var descriptor)
        && descriptor!.Capability == AgentCapability.Docker
        && descriptor.Risk == AgentActionRisk.Destructive
        && authorization.Source is AgentAuthorizationSource.HumanApproval
            or AgentAuthorizationSource.YoloPolicy;

    private async ValueTask<HostResult<AgentDockerControlResult>>
        CompleteAgentDockerControlAsync(
            AgentActionPermit permit,
            HostResult<AgentDockerControlResult> result)
    {
        var (outcome, stableCode) = result switch
        {
            HostResult<AgentDockerControlResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (AgentActionOutcome.Cancelled, failure.Error.StableCode),
            HostResult<AgentDockerControlResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode),
            HostResult<AgentDockerControlResult>.Success success
                when success.Value.Outcome == DockerContainerControlOutcome.Applied =>
                (AgentActionOutcome.Succeeded, success.Value.StableCode),
            HostResult<AgentDockerControlResult>.Success success =>
                (AgentActionOutcome.Failed, success.Value.StableCode),
            _ => throw new InvalidOperationException(
                "A governed Docker mutation returned an unknown result."),
        };
        var revision = result switch
        {
            HostResult<AgentDockerControlResult>.Success success => success.ResultingRevision,
            HostResult<AgentDockerControlResult>.Failure failure => failure.CurrentRevision,
            _ => 0,
        };
        return await CompleteConsumedAgentActionAsync(
                permit,
                Completion(permit, outcome, stableCode, resultCount: 1),
                result,
                revision)
            .ConfigureAwait(false);
    }

    private static HostResult<AgentDockerControlResult>
        MapDockerControlAuthorizationFailure(
            AgentAuthorizationError error,
            long revision) => ControlFailure(
                error.Code switch
                {
                    AgentAuthorizationErrorCode.AuthorizationExpired
                        or AgentAuthorizationErrorCode.ApprovalExpired =>
                        "docker_authorization_expired",
                    AgentAuthorizationErrorCode.Cancelled
                        or AgentAuthorizationErrorCode.RunCancelled =>
                        "docker_container_control_cancelled",
                    AgentAuthorizationErrorCode.AuditUnavailable =>
                        "docker_audit_unavailable",
                    _ => "docker_authorization_rejected",
                },
                error.Code is AgentAuthorizationErrorCode.Cancelled
                    or AgentAuthorizationErrorCode.RunCancelled
                    ? HostErrorCode.Cancelled
                    : HostErrorCode.InvalidRequest,
                revision);

    private static HostResult<AgentDockerControlResult> ControlFailure(
        string stableCode,
        HostErrorCode code,
        long revision,
        bool retryable = false) =>
        HostResult<AgentDockerControlResult>.Fail(
            new HostError(
                code,
                stableCode,
                "The governed Docker mutation could not be completed.",
                retryable),
            revision);
}
