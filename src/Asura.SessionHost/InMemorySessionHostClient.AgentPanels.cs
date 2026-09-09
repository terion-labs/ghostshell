using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<AgentPanelActionResult>>
        RunAgentPanelActionAsync(
            AgentAuthorizationId authorizationId,
            AgentPanelAction action,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentPanelActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentPanelActionResult>(
                "The governed panel execution bridge is not composed.",
                revision: 0);
        }

        AgentActionPermit? permit = null;
        HostResult<AgentPanelActionResult>? result = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentPanelActionResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var exactContextResult = ResolveExactAgentContext(
                action.Proposal.Target);
            if (exactContextResult
                is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentPanelActionResult>.Fail(
                    contextFailure.Error,
                    contextFailure.CurrentRevision);
            }

            var exactContext =
                ((HostResult<AgentContextSnapshot>.Success)exactContextResult).Value;
            revision = exactContext.Revision;
            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentPanelActionComposer.BindForExecution(
                    action,
                    exactContext);
            }
            catch (ArgumentException)
            {
                return InvalidAgentPanelAction(
                    "The prepared action no longer matches the exact live panel.",
                    revision);
            }
            catch (InvalidOperationException)
            {
                return InvalidAgentPanelAction(
                    "The prepared action no longer matches its typed panel request.",
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
                return MapAgentPanelAuthorizationFailure(
                    denied.Error,
                    revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            result = RevalidateAndRunAgentPanelAction(
                action,
                binding,
                permit,
                cancellationToken,
                revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentPanelActionResult>(revision);
        }
        catch (OperationCanceledException)
        {
            result = CancelledAgentPanelAction(
                permit!,
                cancellationToken,
                revision);
        }
        catch (ObjectDisposedException) when (permit is null)
        {
            return Cancelled<AgentPanelActionResult>(revision);
        }
        catch (ObjectDisposedException)
        {
            result = CancelledAgentPanelAction(
                permit!,
                cancellationToken,
                revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentPanelActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The panel authorization broker is unavailable.",
                    retryable: true),
                revision);
        }
        catch (Exception)
        {
            result = HostResult<AgentPanelActionResult>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The governed panel action could not be completed.",
                    retryable: true),
                revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        var completedResult = result
            ?? throw new InvalidOperationException(
                "A consumed panel action requires a result.");
        var completion = AgentPanelCompletion(completedResult, permit!);
        return await CompleteConsumedAgentActionAsync(
                permit!,
                completion,
                completedResult,
                ResultRevision(completedResult))
            .ConfigureAwait(false);
    }

    private HostResult<AgentPanelActionResult>
        RevalidateAndRunAgentPanelAction(
            AgentPanelAction action,
            AgentActionExecutionBinding consumedBinding,
            AgentActionPermit permit,
            CancellationToken callerCancellation,
            long revision)
    {
        if (!HasAgentPanelAuthorization(
                permit.Authorization,
                action.Request))
        {
            return InvalidAgentPanelAction(
                "The consumed authorization does not grant the exact panel tool.",
                revision);
        }

        if (permit.CancellationToken.IsCancellationRequested
            || callerCancellation.IsCancellationRequested)
        {
            return CancelledAgentPanelAction(
                permit,
                callerCancellation,
                revision);
        }

        var currentContextResult = ResolveExactAgentContext(
            action.Proposal.Target);
        if (currentContextResult
            is HostResult<AgentContextSnapshot>.Failure failure)
        {
            return HostResult<AgentPanelActionResult>.Fail(
                failure.Error,
                failure.CurrentRevision);
        }

        var currentContext =
            ((HostResult<AgentContextSnapshot>.Success)currentContextResult).Value;
        AgentActionExecutionBinding currentBinding;
        try
        {
            currentBinding = _agentPanelActionComposer!.BindForExecution(
                action,
                currentContext);
        }
        catch (ArgumentException)
        {
            return InvalidAgentPanelAction(
                "The exact panel target changed while authorization was consumed.",
                revision);
        }
        catch (InvalidOperationException)
        {
            return InvalidAgentPanelAction(
                "The typed panel request changed while authorization was consumed.",
                revision);
        }

        if (!PanelBindingsMatch(consumedBinding, currentBinding)
            || !AuthorizationMatchesBinding(
                permit.Authorization,
                currentBinding))
        {
            return InvalidAgentPanelAction(
                "The exact panel execution binding changed before dispatch.",
                revision);
        }

        var panel = currentContext.Panels.Single();
        return action.Request switch
        {
            AgentPanelRequest.Inspect =>
                HostResult<AgentPanelActionResult>.Succeed(
                    new AgentPanelActionResult.Inspected(panel),
                    currentContext.Revision),
            AgentPanelRequest.Focus focus =>
                FocusAgentPanel(
                    focus,
                    panel,
                    callerCancellation,
                    permit),
            _ => InvalidAgentPanelAction(
                "The governed panel request kind is unsupported.",
                revision),
        };
    }

    private HostResult<AgentPanelActionResult> FocusAgentPanel(
        AgentPanelRequest.Focus request,
        AgentContextPanel panel,
        CancellationToken callerCancellation,
        AgentActionPermit permit)
    {
        // Cancellation has authority until this adjacent pre-commit check.
        // WorkspaceGraphRegistry.ActivatePanel is the synchronous commit point;
        // a later cancellation cannot make its receipt ambiguous.
        if (permit.CancellationToken.IsCancellationRequested
            || callerCancellation.IsCancellationRequested)
        {
            return CancelledAgentPanelAction(
                permit,
                callerCancellation,
                panel.WorkspaceRevision);
        }

        var graphResult = _workspaceGraphs.Get(panel.WorkspaceId);
        if (graphResult
            is HostResult<WorkspaceGraphSnapshot>.Failure graphFailure)
        {
            return HostResult<AgentPanelActionResult>.Fail(
                graphFailure.Error,
                graphFailure.CurrentRevision);
        }

        var before =
            ((HostResult<WorkspaceGraphSnapshot>.Success)graphResult).Value;
        var tab = before.Workspace.Tabs.SingleOrDefault(
            candidate => candidate.Id == panel.TabId);
        if (before.WindowId != panel.WindowId
            || before.Revision != panel.WorkspaceRevision
            || tab is null
            || tab.Panels.All(candidate =>
                candidate.Id != request.PanelId))
        {
            return InvalidAgentPanelAction(
                "The exact panel graph changed before focus.",
                before.Revision);
        }

        var changed = before.Workspace.ActiveTabId != panel.TabId
            || tab.ActivePanelId != request.PanelId;
        var activated = _workspaceGraphs.ActivatePanel(
            new ActivateWorkspacePanelRequest(
                panel.WorkspaceId,
                panel.TabId,
                request.PanelId),
            panel.WorkspaceRevision);
        if (activated
            is HostResult<WorkspaceGraphSnapshot>.Failure activateFailure)
        {
            return HostResult<AgentPanelActionResult>.Fail(
                activateFailure.Error,
                activateFailure.CurrentRevision);
        }

        var snapshot =
            ((HostResult<WorkspaceGraphSnapshot>.Success)activated).Value;
        return HostResult<AgentPanelActionResult>.Succeed(
            new AgentPanelActionResult.Focused(
                new AgentPanelFocusReceipt(
                    snapshot.WindowId,
                    snapshot.Workspace.Id,
                    panel.TabId,
                    request.PanelId,
                    snapshot.Revision,
                    snapshot.LastSequence,
                    changed)),
            snapshot.Revision);
    }

    private AgentActionCompletion AgentPanelCompletion(
        HostResult<AgentPanelActionResult> result,
        AgentActionPermit permit)
    {
        var (outcome, stableCode) = result switch
        {
            HostResult<AgentPanelActionResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (AgentActionOutcome.Cancelled, failure.Error.StableCode),
            HostResult<AgentPanelActionResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode),
            HostResult<AgentPanelActionResult>.Success
            {
                Value: AgentPanelActionResult.Inspected,
            } =>
                (AgentActionOutcome.Succeeded, "panel_inspected"),
            HostResult<AgentPanelActionResult>.Success
            {
                Value: AgentPanelActionResult.Focused,
            } =>
                (AgentActionOutcome.Succeeded, "panel_focused"),
            _ => throw new InvalidOperationException(
                "A governed panel dispatch returned an unknown result."),
        };
        return Completion(permit, outcome, stableCode);
    }

    private static bool HasAgentPanelAuthorization(
        AgentActionAuthorization authorization,
        AgentPanelRequest request)
    {
        var requiredTool = request switch
        {
            AgentPanelRequest.Inspect => BuiltInAgentTools.PanelInspect,
            AgentPanelRequest.Focus => BuiltInAgentTools.PanelFocus,
            _ => string.Empty,
        };
        var requiredCapability = request switch
        {
            AgentPanelRequest.Inspect => AgentCapability.Search,
            AgentPanelRequest.Focus => AgentCapability.RunCommands,
            _ => (AgentCapability?)null,
        };
        return requiredCapability is { } capability
            && string.Equals(
                authorization.ToolName,
                requiredTool,
                StringComparison.Ordinal)
            && BuiltInAgentTools.Catalog.TryGet(
                requiredTool,
                out var descriptor)
            && descriptor!.Capability == capability;
    }

    private static bool PanelBindingsMatch(
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

    private static HostResult<AgentPanelActionResult>
        CancelledAgentPanelAction(
            AgentActionPermit permit,
            CancellationToken callerCancellation,
            long revision)
    {
        var stableCode = permit.CancellationToken.IsCancellationRequested
            ? "authority_revoked"
            : callerCancellation.IsCancellationRequested
                ? "caller_cancelled"
                : "operation_cancelled";
        return HostResult<AgentPanelActionResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                stableCode,
                "The governed panel action was cancelled."),
            revision);
    }

    private static HostResult<AgentPanelActionResult>
        InvalidAgentPanelAction(
            string message,
            long revision) =>
        HostResult<AgentPanelActionResult>.Fail(
            HostError.Create(
                HostErrorCode.InvalidRequest,
                message),
            revision);

    private static HostResult<AgentPanelActionResult>
        MapAgentPanelAuthorizationFailure(
            AgentAuthorizationError error,
            long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                HostError.Create(
                    HostErrorCode.DeadlineExceeded,
                    "The one-action panel authorization has expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                HostError.Create(
                    HostErrorCode.Cancelled,
                    "The governed panel action was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The panel-agent audit trail is unavailable.",
                    retryable: true),
            _ => HostError.Create(
                HostErrorCode.InvalidRequest,
                "The exact one-action panel authorization was rejected."),
        };
        return HostResult<AgentPanelActionResult>.Fail(
            hostError,
            revision);
    }

    private static long ResultRevision(
        HostResult<AgentPanelActionResult> result) =>
        result switch
        {
            HostResult<AgentPanelActionResult>.Success success =>
                success.ResultingRevision,
            HostResult<AgentPanelActionResult>.Failure failure =>
                failure.CurrentRevision,
            _ => throw new InvalidOperationException(
                "A governed panel action returned an unknown result."),
        };
}
