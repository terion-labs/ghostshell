using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private const string WorkspaceLayoutOutcomeUnknownCode =
        "workspace_layout_outcome_unknown";

    public async ValueTask<HostResult<AgentWorkspaceLayoutReceipt>>
        RunAgentWorkspaceLayoutActionAsync(
            AgentAuthorizationId authorizationId,
            AgentWorkspaceLayoutAction action,
            IAgentWorkspaceLayoutMutationPort mutationPort,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(mutationPort);
        if (_agentWorkspaceLayoutActionComposer is null
            || _agentAuthorizationConsumer is null)
        {
            return Unsupported<AgentWorkspaceLayoutReceipt>(
                "The governed workspace layout bridge is not composed.",
                revision: 0);
        }

        AgentActionPermit? permit = null;
        long revision = 0;
        HostResult<AgentWorkspaceLayoutReceipt>? result = null;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<AgentWorkspaceLayoutReceipt>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var contextResult = ResolveWorkspaceGraphAgentContext(
                action.Proposal.Target);
            if (contextResult
                is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<AgentWorkspaceLayoutReceipt>.Fail(
                    contextFailure.Error,
                    contextFailure.CurrentRevision);
            }

            var context =
                ((HostResult<AgentContextSnapshot>.Success)contextResult).Value;
            var first = context.Panels[0];
            revision = first.WorkspaceRevision;
            if (mutationPort.WindowId != first.WindowId
                || mutationPort.WorkspaceId != first.WorkspaceId
                || !PortSupports(action.Request, mutationPort))
            {
                return InvalidAgentWorkspaceLayoutAction(
                    "The trusted layout port does not match this run workspace.",
                    revision);
            }

            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentWorkspaceLayoutActionComposer.BindForExecution(
                    action,
                    context);
            }
            catch (ArgumentException)
            {
                return TargetChangedAgentWorkspaceLayoutAction(revision);
            }
            catch (InvalidOperationException)
            {
                return InvalidAgentWorkspaceLayoutAction(
                    "The prepared layout action no longer matches its request.",
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
                return MapAgentWorkspaceLayoutAuthorizationFailure(
                    denied.Error,
                    revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            if (!HasAgentWorkspaceLayoutAuthorization(
                    permit.Authorization,
                    action.Request)
                || !AuthorizationMatchesBinding(
                    permit.Authorization,
                    binding))
            {
                result = InvalidAgentWorkspaceLayoutAction(
                    "The consumed authorization does not grant this layout action.",
                    revision);
            }
            else if (permit.CancellationToken.IsCancellationRequested
                || cancellationToken.IsCancellationRequested)
            {
                result = CancelledAgentWorkspaceLayoutAction(
                    permit,
                    cancellationToken,
                    revision);
            }
            else
            {
                var freshResult = ResolveWorkspaceGraphAgentContext(
                    action.Proposal.Target);
                if (freshResult
                    is HostResult<AgentContextSnapshot>.Failure freshFailure)
                {
                    result = HostResult<AgentWorkspaceLayoutReceipt>.Fail(
                        freshFailure.Error,
                        freshFailure.CurrentRevision);
                }
                else
                {
                    var fresh = ((HostResult<AgentContextSnapshot>.Success)freshResult)
                        .Value;
                    try
                    {
                        var freshBinding = _agentWorkspaceLayoutActionComposer
                            .BindForExecution(action, fresh);
                        if (!WorkspaceLayoutBindingsMatch(binding, freshBinding)
                            || !AuthorizationMatchesBinding(
                                permit.Authorization,
                                freshBinding))
                        {
                            result = TargetChangedAgentWorkspaceLayoutAction(
                                fresh.Panels[0].WorkspaceRevision);
                        }
                    }
                    catch (ArgumentException)
                    {
                        result = TargetChangedAgentWorkspaceLayoutAction(
                            fresh.Panels[0].WorkspaceRevision);
                    }
                    catch (InvalidOperationException)
                    {
                        result = InvalidAgentWorkspaceLayoutAction(
                            "The typed layout request changed before dispatch.",
                            fresh.Panels[0].WorkspaceRevision);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<AgentWorkspaceLayoutReceipt>(revision);
        }
        catch (Exception) when (permit is null)
        {
            return HostResult<AgentWorkspaceLayoutReceipt>.Fail(
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The workspace layout authorization broker is unavailable."),
                revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (result is null)
        {
            // Crossing this call boundary is the layout mutation commit region.
            // Cancellation or transport failure after this point is never retried.
            try
            {
                using var dispatchCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        permit!.CancellationToken,
                        cancellationToken);
                var mutation = await mutationPort.MutateAsync(
                        action.Request,
                        revision,
                        dispatchCancellation.Token)
                    .ConfigureAwait(false);
                result = mutation switch
                {
                    AgentWorkspaceLayoutMutationResult.Observed observed =>
                        VerifyObservedWorkspaceConnections(
                            action.Request,
                            observed,
                            revision),
                    AgentWorkspaceLayoutMutationResult.Applied applied =>
                        VerifyAppliedWorkspaceLayout(action.Request, applied, revision),
                    AgentWorkspaceLayoutMutationResult.Rejected rejected =>
                        RejectedAgentWorkspaceLayoutAction(
                            rejected.StableCode,
                            revision),
                    AgentWorkspaceLayoutMutationResult.OutcomeUnknown =>
                        WorkspaceLayoutDispatchFailure(action.Request, revision),
                    _ => WorkspaceLayoutDispatchFailure(action.Request, revision),
                };
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                _ = exception;
                result = WorkspaceLayoutDispatchFailure(action.Request, revision);
            }
        }

        var completedResult = result;
        var completion = AgentWorkspaceLayoutCompletion(
            completedResult,
            permit!);
        return await CompleteConsumedAgentActionAsync(
                permit!,
                completion,
                completedResult,
                WorkspaceLayoutResultRevision(completedResult))
            .ConfigureAwait(false);
    }

    private HostResult<AgentWorkspaceLayoutReceipt>
        VerifyObservedWorkspaceConnections(
            AgentWorkspaceLayoutRequest request,
            AgentWorkspaceLayoutMutationResult.Observed observed,
            long previousRevision)
    {
        if (request is not AgentWorkspaceLayoutRequest.ConnectionList
            || observed.Connections.Count > 64)
        {
            return InvalidAgentWorkspaceLayoutAction(
                "The workspace connection observation was invalid.",
                previousRevision);
        }

        var authoritative = _workspaceGraphs.Get(observed.Snapshot.Workspace.Id);
        if (authoritative is not HostResult<WorkspaceGraphSnapshot>.Success success
            || success.Value.WindowId != observed.Snapshot.WindowId
            || success.Value.Workspace.Id != observed.Snapshot.Workspace.Id
            || success.Value.Revision != observed.Snapshot.Revision
            || success.Value.LastSequence != observed.Snapshot.LastSequence)
        {
            return TargetChangedAgentWorkspaceLayoutAction(previousRevision);
        }

        return HostResult<AgentWorkspaceLayoutReceipt>.Succeed(
            new AgentWorkspaceLayoutReceipt(
                BuiltInAgentTools.ConnectionsList,
                success.Value.WindowId,
                success.Value.Workspace.Id,
                success.Value.Revision,
                success.Value.LastSequence,
                null,
                null,
                null,
                observed.Connections),
            success.Value.Revision);
    }

    private HostResult<AgentWorkspaceLayoutReceipt>
        VerifyAppliedWorkspaceLayout(
            AgentWorkspaceLayoutRequest request,
            AgentWorkspaceLayoutMutationResult.Applied applied,
            long previousRevision)
    {
        var authoritative = _workspaceGraphs.Get(applied.Snapshot.Workspace.Id);
        if (authoritative
            is not HostResult<WorkspaceGraphSnapshot>.Success success)
        {
            return WorkspaceLayoutOutcomeUnknown(previousRevision);
        }

        var snapshot = success.Value;
        if (snapshot.WindowId != applied.Snapshot.WindowId
            || snapshot.Workspace.Id != applied.Snapshot.Workspace.Id
            || snapshot.Revision < applied.Snapshot.Revision
            || snapshot.LastSequence < applied.Snapshot.LastSequence
            || (request is not AgentWorkspaceLayoutRequest.PanelConnect
                && applied.Snapshot.Revision <= previousRevision)
            || !AppliedTargetMatches(request, applied, applied.Snapshot)
            || !AppliedTargetMatches(request, applied, snapshot))
        {
            return WorkspaceLayoutOutcomeUnknown(snapshot.Revision);
        }

        return HostResult<AgentWorkspaceLayoutReceipt>.Succeed(
            new AgentWorkspaceLayoutReceipt(
                AgentWorkspaceLayoutActionComposer.ToolName(request),
                snapshot.WindowId,
                snapshot.Workspace.Id,
                snapshot.Revision,
                snapshot.LastSequence,
                applied.TabId,
                applied.PanelId,
                applied.PanelKind,
                isPanelReady: applied.IsPanelReady),
            snapshot.Revision);
    }

    private static bool AppliedTargetMatches(
        AgentWorkspaceLayoutRequest request,
        AgentWorkspaceLayoutMutationResult.Applied applied,
        WorkspaceGraphSnapshot snapshot) => request switch
        {
            AgentWorkspaceLayoutRequest.TabCreate create =>
                applied.TabId is { } tabId
                && applied.PanelId is { } panelId
                && applied.PanelKind == create.Kind
                && snapshot.Workspace.Tabs.Any(tab =>
                    tab.Id == tabId
                    && tab.Panels.Any(panel =>
                        panel.Id == panelId && panel.Kind == create.Kind)),
            AgentWorkspaceLayoutRequest.TabClose close =>
                applied.TabId == close.TabId
                && applied.PanelId is null
                && snapshot.Workspace.Tabs.All(tab => tab.Id != close.TabId),
            AgentWorkspaceLayoutRequest.PanelAdd add =>
                applied.TabId == add.TabId
                && applied.PanelId is { } panelId
                && applied.PanelKind == add.Kind
                && snapshot.Workspace.Tabs.Any(tab =>
                    tab.Id == add.TabId
                    && tab.Panels.Any(panel =>
                        panel.Id == panelId && panel.Kind == add.Kind)),
            AgentWorkspaceLayoutRequest.PanelSplit split =>
                applied.TabId is { } tabId
                && applied.PanelId is { } panelId
                && applied.PanelKind == split.Kind
                && snapshot.Workspace.Tabs.Any(tab =>
                    tab.Id == tabId
                    && tab.Panels.Any(panel => panel.Id == split.PanelId)
                    && tab.Panels.Any(panel =>
                        panel.Id == panelId && panel.Kind == split.Kind)),
            AgentWorkspaceLayoutRequest.PanelClose close =>
                applied.PanelId == close.PanelId
                && snapshot.Workspace.Tabs.All(tab =>
                    tab.Panels.All(panel => panel.Id != close.PanelId)),
            AgentWorkspaceLayoutRequest.PanelConnect connect =>
                applied.PanelId == connect.PanelId
                && snapshot.Workspace.Tabs.Any(tab => tab.Panels.Any(panel =>
                    panel.Id == connect.PanelId
                    && panel.Kind == applied.PanelKind)),
            _ => false,
        };

    private static bool PortSupports(
        AgentWorkspaceLayoutRequest request,
        IAgentWorkspaceLayoutMutationPort port) => request switch
        {
            AgentWorkspaceLayoutRequest.TabCreate create =>
                port.SupportedPanelKinds.Contains(create.Kind),
            AgentWorkspaceLayoutRequest.PanelAdd add =>
                port.SupportedPanelKinds.Contains(add.Kind),
            AgentWorkspaceLayoutRequest.PanelSplit split =>
                port.SupportedPanelKinds.Contains(split.Kind),
            AgentWorkspaceLayoutRequest.TabClose
                or AgentWorkspaceLayoutRequest.PanelClose
                or AgentWorkspaceLayoutRequest.ConnectionList
                or AgentWorkspaceLayoutRequest.PanelConnect => true,
            _ => false,
        };

    private static bool HasAgentWorkspaceLayoutAuthorization(
        AgentActionAuthorization authorization,
        AgentWorkspaceLayoutRequest request)
    {
        var toolName = AgentWorkspaceLayoutActionComposer.ToolName(request);
        return string.Equals(
                authorization.ToolName,
                toolName,
                StringComparison.Ordinal)
            && BuiltInAgentTools.Catalog.TryGet(toolName, out var descriptor)
            && descriptor!.Capability == (request is
                AgentWorkspaceLayoutRequest.ConnectionList
                    ? AgentCapability.Search
                    : AgentCapability.WorkspaceLayout);
    }

    private static bool WorkspaceLayoutBindingsMatch(
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

    private AgentActionCompletion AgentWorkspaceLayoutCompletion(
        HostResult<AgentWorkspaceLayoutReceipt> result,
        AgentActionPermit permit)
    {
        var (outcome, stableCode) = result switch
        {
            HostResult<AgentWorkspaceLayoutReceipt>.Success success =>
                (AgentActionOutcome.Succeeded, SuccessCode(success.Value.Operation)),
            HostResult<AgentWorkspaceLayoutReceipt>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (AgentActionOutcome.Cancelled, failure.Error.StableCode),
            HostResult<AgentWorkspaceLayoutReceipt>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode),
            _ => throw new InvalidOperationException(
                "A workspace layout action returned an unknown result."),
        };
        return Completion(permit, outcome, stableCode);
    }

    private static string SuccessCode(string operation) => operation switch
    {
        BuiltInAgentTools.TabCreate => "tab_created",
        BuiltInAgentTools.TabClose => "tab_closed",
        BuiltInAgentTools.PanelAdd => "panel_added",
        BuiltInAgentTools.PanelSplit => "panel_split",
        BuiltInAgentTools.PanelClose => "panel_closed",
        BuiltInAgentTools.ConnectionsList => "connections_listed",
        BuiltInAgentTools.PanelConnect => "panel_connected",
        _ => "workspace_layout_changed",
    };

    private static HostResult<AgentWorkspaceLayoutReceipt>
        TargetChangedAgentWorkspaceLayoutAction(long revision) =>
        HostResult<AgentWorkspaceLayoutReceipt>.Fail(
            new HostError(
                HostErrorCode.RevisionConflict,
                "target_changed",
                "The workspace graph changed before layout dispatch."),
            revision);

    private static HostResult<AgentWorkspaceLayoutReceipt>
        InvalidAgentWorkspaceLayoutAction(string message, long revision) =>
        HostResult<AgentWorkspaceLayoutReceipt>.Fail(
            HostError.Create(HostErrorCode.InvalidRequest, message),
            revision);

    private static HostResult<AgentWorkspaceLayoutReceipt>
        RejectedAgentWorkspaceLayoutAction(string stableCode, long revision)
    {
        var allowed = stableCode is
            "workspace_layout_rejected"
            or "workspace_layout_unsaved_changes"
            or "workspace_panel_startup_failed";
        return HostResult<AgentWorkspaceLayoutReceipt>.Fail(
            new HostError(
                HostErrorCode.InvalidRequest,
                allowed ? stableCode : "workspace_layout_rejected",
                "The trusted workspace rejected the layout mutation."),
            revision);
    }

    private static HostResult<AgentWorkspaceLayoutReceipt>
        WorkspaceLayoutDispatchFailure(
            AgentWorkspaceLayoutRequest request,
            long revision) => request is AgentWorkspaceLayoutRequest.ConnectionList
            ? HostResult<AgentWorkspaceLayoutReceipt>.Fail(
                new HostError(
                    HostErrorCode.EngineFailed,
                    "workspace_connections_failed",
                    "The workspace connection observation failed."),
                revision)
            : WorkspaceLayoutOutcomeUnknown(revision);

    private static HostResult<AgentWorkspaceLayoutReceipt>
        WorkspaceLayoutOutcomeUnknown(long revision) =>
        HostResult<AgentWorkspaceLayoutReceipt>.Fail(
            new HostError(
                HostErrorCode.EngineFailed,
                WorkspaceLayoutOutcomeUnknownCode,
                "The workspace layout mutation outcome is unknown."),
            revision);

    private static HostResult<AgentWorkspaceLayoutReceipt>
        CancelledAgentWorkspaceLayoutAction(
            AgentActionPermit permit,
            CancellationToken callerCancellation,
            long revision)
    {
        var stableCode = permit.CancellationToken.IsCancellationRequested
            ? "authority_revoked"
            : callerCancellation.IsCancellationRequested
                ? "caller_cancelled"
                : "operation_cancelled";
        return HostResult<AgentWorkspaceLayoutReceipt>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                stableCode,
                "The workspace layout action was cancelled."),
            revision);
    }

    private static HostResult<AgentWorkspaceLayoutReceipt>
        MapAgentWorkspaceLayoutAuthorizationFailure(
            AgentAuthorizationError error,
            long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                HostError.Create(
                    HostErrorCode.DeadlineExceeded,
                    "The one-action workspace layout authorization expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                HostError.Create(
                    HostErrorCode.Cancelled,
                    "The workspace layout action was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                HostError.Create(
                    HostErrorCode.EngineFailed,
                    "The workspace layout audit trail is unavailable."),
            _ => HostError.Create(
                HostErrorCode.InvalidRequest,
                "The workspace layout authorization was rejected."),
        };
        return HostResult<AgentWorkspaceLayoutReceipt>.Fail(hostError, revision);
    }

    private static long WorkspaceLayoutResultRevision(
        HostResult<AgentWorkspaceLayoutReceipt> result) => result switch
        {
            HostResult<AgentWorkspaceLayoutReceipt>.Success success =>
                success.ResultingRevision,
            HostResult<AgentWorkspaceLayoutReceipt>.Failure failure =>
                failure.CurrentRevision,
            _ => throw new InvalidOperationException(
                "A workspace layout action returned an unknown result."),
        };
}
