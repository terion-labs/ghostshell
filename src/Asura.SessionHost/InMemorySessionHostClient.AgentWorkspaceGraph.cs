using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<AgentWorkspaceGraphActionResult>>
        RunAgentWorkspaceGraphActionAsync(
            AgentAuthorizationId authorizationId,
            AgentWorkspaceGraphAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentWorkspaceGraphActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentWorkspaceGraphActionResult>(
                "The governed workspace graph execution bridge is not composed.",
                revision: 0);
        }

        AgentActionPermit? permit = null;
        HostResult<AgentWorkspaceGraphActionResult>? result = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentWorkspaceGraphActionResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var contextResult = ResolveWorkspaceGraphAgentContext(
                action.Proposal.Target);
            if (contextResult
                is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentWorkspaceGraphActionResult>.Fail(
                    contextFailure.Error,
                    contextFailure.CurrentRevision);
            }

            var context =
                ((HostResult<AgentContextSnapshot>.Success)contextResult).Value;
            revision = context.Panels[0].WorkspaceRevision;
            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentWorkspaceGraphActionComposer.BindForExecution(
                    action,
                    context);
            }
            catch (ArgumentException)
            {
                return InvalidAgentWorkspaceGraphAction(
                    "The prepared action no longer matches the scope-clipped graph.",
                    revision);
            }
            catch (InvalidOperationException)
            {
                return InvalidAgentWorkspaceGraphAction(
                    "The prepared action no longer matches its typed graph request.",
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
                return MapAgentWorkspaceGraphAuthorizationFailure(
                    denied.Error,
                    revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            result = RevalidateAndProjectAgentWorkspaceGraph(
                action,
                binding,
                permit,
                cancellationToken,
                revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentWorkspaceGraphActionResult>(revision);
        }
        catch (OperationCanceledException)
        {
            result = CancelledAgentWorkspaceGraphAction(
                permit!,
                cancellationToken,
                revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentWorkspaceGraphActionResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            result = CancelledAgentWorkspaceGraphAction(
                permit!,
                cancellationToken,
                revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentWorkspaceGraphActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The workspace graph authorization broker is unavailable.",
                    retryable: true),
                revision);
        }
        catch (Exception)
        {
            result = HostResult<AgentWorkspaceGraphActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The governed workspace graph observation could not be completed.",
                    retryable: true),
                revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        var completedResult = result
            ?? throw new InvalidOperationException(
                "A consumed workspace graph action requires a result.");
        var completion = AgentWorkspaceGraphCompletion(
            completedResult,
            permit!);
        return await CompleteConsumedAgentActionAsync(
                permit!,
                completion,
                completedResult,
                AgentWorkspaceGraphResultRevision(completedResult))
            .ConfigureAwait(false);
    }

    private HostResult<AgentWorkspaceGraphActionResult>
        RevalidateAndProjectAgentWorkspaceGraph(
            AgentWorkspaceGraphAction action,
            AgentActionExecutionBinding consumedBinding,
            AgentActionPermit permit,
            CancellationToken callerCancellation,
            long revision)
    {
        if (!HasAgentWorkspaceGraphAuthorization(
                permit.Authorization,
                action.Request))
        {
            return InvalidAgentWorkspaceGraphAction(
                "The consumed authorization does not grant the exact graph tool.",
                revision);
        }

        if (permit.CancellationToken.IsCancellationRequested
            || callerCancellation.IsCancellationRequested)
        {
            return CancelledAgentWorkspaceGraphAction(
                permit,
                callerCancellation,
                revision);
        }

        var contextResult = ResolveWorkspaceGraphAgentContext(
            action.Proposal.Target);
        if (contextResult
            is HostResult<AgentContextSnapshot>.Failure failure)
        {
            return HostResult<AgentWorkspaceGraphActionResult>.Fail(
                failure.Error,
                failure.CurrentRevision);
        }

        var context =
            ((HostResult<AgentContextSnapshot>.Success)contextResult).Value;
        revision = context.Panels[0].WorkspaceRevision;
        AgentActionExecutionBinding currentBinding;
        try
        {
            currentBinding =
                _agentWorkspaceGraphActionComposer!.BindForExecution(
                    action,
                    context);
        }
        catch (ArgumentException)
        {
            return InvalidAgentWorkspaceGraphAction(
                "The scope-clipped graph changed while authorization was consumed.",
                revision);
        }
        catch (InvalidOperationException)
        {
            return InvalidAgentWorkspaceGraphAction(
                "The typed graph request changed while authorization was consumed.",
                revision);
        }

        if (!AgentWorkspaceGraphBindingsMatch(
                consumedBinding,
                currentBinding)
            || !AuthorizationMatchesBinding(
                permit.Authorization,
                currentBinding))
        {
            return InvalidAgentWorkspaceGraphAction(
                "The graph execution binding changed before projection.",
                revision);
        }

        if (permit.CancellationToken.IsCancellationRequested
            || callerCancellation.IsCancellationRequested)
        {
            return CancelledAgentWorkspaceGraphAction(
                permit,
                callerCancellation,
                revision);
        }

        try
        {
            var projection = _agentWorkspaceGraphActionComposer.Project(
                action,
                context);
            return HostResult<AgentWorkspaceGraphActionResult>.Succeed(
                projection,
                revision);
        }
        catch (ArgumentException)
        {
            return InvalidAgentWorkspaceGraphAction(
                "The scope-clipped graph cannot be projected within its contract.",
                revision);
        }
        catch (InvalidOperationException)
        {
            return InvalidAgentWorkspaceGraphAction(
                "The typed graph request cannot be projected.",
                revision);
        }
    }

    private HostResult<AgentContextSnapshot>
        ResolveWorkspaceGraphAgentContext(AgentTarget target)
    {
        HostedSession[] hostedSessions;
        lock (_gate)
        {
            hostedSessions = [.. _sessions.Values];
        }

        var sessions = hostedSessions
            .Select(session => session.Snapshot().Descriptor)
            .ToDictionary(session => session.Id);
        return ResolveAgentContext(
            new AgentContextRequest(
                target,
                AgentTarget.SelectedPanels.MaximumPanelCount),
            sessions);
    }

    private AgentActionCompletion AgentWorkspaceGraphCompletion(
        HostResult<AgentWorkspaceGraphActionResult> result,
        AgentActionPermit permit)
    {
        var (outcome, stableCode) = result switch
        {
            HostResult<AgentWorkspaceGraphActionResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (AgentActionOutcome.Cancelled, failure.Error.StableCode),
            HostResult<AgentWorkspaceGraphActionResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode),
            HostResult<AgentWorkspaceGraphActionResult>.Success
            {
                Value: AgentWorkspaceGraphActionResult.WorkspaceInspected,
            } =>
                (AgentActionOutcome.Succeeded, "workspace_inspected"),
            HostResult<AgentWorkspaceGraphActionResult>.Success
            {
                Value: AgentWorkspaceGraphActionResult.TabsListed,
            } =>
                (AgentActionOutcome.Succeeded, "tabs_listed"),
            HostResult<AgentWorkspaceGraphActionResult>.Success
            {
                Value: AgentWorkspaceGraphActionResult.PanelsListed,
            } =>
                (AgentActionOutcome.Succeeded, "panels_listed"),
            _ => throw new InvalidOperationException(
                "A governed graph dispatch returned an unknown result."),
        };
        return Completion(permit, outcome, stableCode);
    }

    private static bool HasAgentWorkspaceGraphAuthorization(
        AgentActionAuthorization authorization,
        AgentWorkspaceGraphRequest request)
    {
        var requiredTool = request switch
        {
            AgentWorkspaceGraphRequest.WorkspaceInspect =>
                BuiltInAgentTools.WorkspaceInspect,
            AgentWorkspaceGraphRequest.TabList =>
                BuiltInAgentTools.TabList,
            AgentWorkspaceGraphRequest.PanelList =>
                BuiltInAgentTools.PanelList,
            _ => string.Empty,
        };
        return string.Equals(
                authorization.ToolName,
                requiredTool,
                StringComparison.Ordinal)
            && BuiltInAgentTools.Catalog.TryGet(
                requiredTool,
                out var descriptor)
            && descriptor!.Capability == AgentCapability.Search;
    }

    private static bool AgentWorkspaceGraphBindingsMatch(
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

    private static HostResult<AgentWorkspaceGraphActionResult>
        CancelledAgentWorkspaceGraphAction(
            AgentActionPermit permit,
            CancellationToken callerCancellation,
            long revision)
    {
        var stableCode = permit.CancellationToken.IsCancellationRequested
            ? "authority_revoked"
            : callerCancellation.IsCancellationRequested
                ? "caller_cancelled"
                : "operation_cancelled";
        return HostResult<AgentWorkspaceGraphActionResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                stableCode,
                "The governed workspace graph action was cancelled."),
            revision);
    }

    private static HostResult<AgentWorkspaceGraphActionResult>
        InvalidAgentWorkspaceGraphAction(
            string message,
            long revision) =>
        HostResult<AgentWorkspaceGraphActionResult>.Fail(
            HostError.Create(
                HostErrorCode.InvalidRequest,
                message),
            revision);

    private static HostResult<AgentWorkspaceGraphActionResult>
        MapAgentWorkspaceGraphAuthorizationFailure(
            AgentAuthorizationError error,
            long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                HostError.Create(
                    HostErrorCode.DeadlineExceeded,
                    "The one-action workspace graph authorization has expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                HostError.Create(
                    HostErrorCode.Cancelled,
                    "The governed workspace graph action was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The workspace graph audit trail is unavailable.",
                    retryable: true),
            _ => HostError.Create(
                HostErrorCode.InvalidRequest,
                "The exact one-action workspace graph authorization was rejected."),
        };
        return HostResult<AgentWorkspaceGraphActionResult>.Fail(
            hostError,
            revision);
    }

    private static long AgentWorkspaceGraphResultRevision(
        HostResult<AgentWorkspaceGraphActionResult> result) =>
        result switch
        {
            HostResult<AgentWorkspaceGraphActionResult>.Success success =>
                success.ResultingRevision,
            HostResult<AgentWorkspaceGraphActionResult>.Failure failure =>
                failure.CurrentRevision,
            _ => throw new InvalidOperationException(
                "A governed graph action returned an unknown result."),
        };
}
