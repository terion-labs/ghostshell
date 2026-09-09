using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<GitAgentOperationResult>> RunAgentGitActionAsync(
        AgentAuthorizationId authorizationId,
        AgentGitAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_agentGitActionComposer is null || _agentAuthorizationConsumer is null)
        {
            return Unsupported<GitAgentOperationResult>(
                "The governed Git execution bridge is not composed.",
                0);
        }

        GitDispatch? dispatch = null;
        AgentActionPermit? permit = null;
        HostResult<GitAgentOperationResult>? preDispatchFailure = null;
        long revision = 0;
        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<GitAgentOperationResult>(revision);
        }

        try
        {
            ThrowIfDisposed();
            var exactContextResult = ResolveExactAgentContext(action.Proposal.Target);
            if (exactContextResult is HostResult<AgentContextSnapshot>.Failure contextFailure)
            {
                return HostResult<GitAgentOperationResult>.Fail(
                    contextFailure.Error,
                    contextFailure.CurrentRevision);
            }

            var exactContext =
                ((HostResult<AgentContextSnapshot>.Success)exactContextResult).Value;
            revision = exactContext.Revision;
            var exactPanel = exactContext.Panels.SingleOrDefault(
                panel => panel.PanelId == action.Request.PanelId);
            if (exactPanel?.SessionId is not { } sessionId
                || exactPanel.SessionRevision is not long expectedSessionRevision
                || !TryGetSession(sessionId, out var session))
            {
                return GitFailure(
                    "git_session_unavailable",
                    revision,
                    HostErrorCode.NotFound);
            }

            AgentActionExecutionBinding binding;
            try
            {
                binding = _agentGitActionComposer.BindForExecution(action, exactContext);
                dispatch = CaptureGitDispatch(
                    action.Request,
                    session,
                    expectedSessionRevision,
                    exactPanel.WorkspaceRevision,
                    exactPanel.GraphSequence,
                    revision,
                    binding);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException)
            {
                return GitFailure(
                    "git_action_invalid",
                    revision,
                    HostErrorCode.InvalidRequest);
            }

            var permitResult = await _agentAuthorizationConsumer
                .ConsumeAsync(authorizationId, binding, cancellationToken)
                .ConfigureAwait(false);
            if (permitResult is AgentPermitResult.Denied denied)
            {
                return MapGitAuthorizationFailure(denied.Error, revision);
            }

            permit = ((AgentPermitResult.Granted)permitResult).Permit;
            preDispatchFailure = RevalidateGitDispatch(
                action,
                dispatch,
                permit,
                binding,
                cancellationToken,
                out revision);
        }
        catch (OperationCanceledException) when (permit is null)
        {
            return Cancelled<GitAgentOperationResult>(revision);
        }
        catch (OperationCanceledException)
        {
            preDispatchFailure = CancelledGitAction(
                permit!,
                dispatch?.RuntimeCancellation ?? default,
                cancellationToken,
                revision);
        }
        catch (Exception) when (permit is null)
        {
            return GitFailure(
                "git_authorization_unavailable",
                revision,
                retryable: true);
        }
        catch (Exception)
        {
            preDispatchFailure = GitFailure("git_dispatch_failed", revision);
        }
        finally
        {
            _sessionGraphGate.Release();
        }

        if (preDispatchFailure is not null)
        {
            return await CompleteGitActionAsync(permit!, preDispatchFailure)
                .ConfigureAwait(false);
        }

        return await ExecuteAndCompleteGitActionAsync(
                dispatch!,
                permit!,
                action.Request,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<HostResult<GitAgentOperationResult>>
        ExecuteAndCompleteGitActionAsync(
            GitDispatch dispatch,
            AgentActionPermit permit,
            AgentGitRequest request,
            CancellationToken callerCancellation)
    {
        HostResult<GitAgentOperationResult> result;
        try
        {
            using var operationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    permit.CancellationToken,
                    dispatch.RuntimeCancellation,
                    callerCancellation);
            if (operationCancellation.IsCancellationRequested)
            {
                result = CancelledGitAction(
                    permit,
                    dispatch.RuntimeCancellation,
                    callerCancellation,
                    dispatch.InitialRevision);
            }
            else
            {
                var value = await ExecuteGitAsync(
                        dispatch.Git,
                        request,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
                result = MapGitOperationResult(value, dispatch.InitialRevision);
            }
        }
        catch (OperationCanceledException)
        {
            result = request.IsMutation
                ? GitFailure("git_mutation_outcome_unknown", dispatch.InitialRevision)
                : CancelledGitAction(
                    permit,
                    dispatch.RuntimeCancellation,
                    callerCancellation,
                    dispatch.InitialRevision);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException)
        {
            result = GitFailure(
                "git_action_rejected",
                dispatch.InitialRevision,
                HostErrorCode.InvalidRequest);
        }
        catch (Exception)
        {
            result = request.IsMutation
                ? GitFailure("git_mutation_outcome_unknown", dispatch.InitialRevision)
                : GitFailure("git_observation_failed", dispatch.InitialRevision);
        }

        return await CompleteGitActionAsync(permit, result).ConfigureAwait(false);
    }

    private HostResult<GitAgentOperationResult>? RevalidateGitDispatch(
        AgentGitAction action,
        GitDispatch dispatch,
        AgentActionPermit permit,
        AgentActionExecutionBinding consumedBinding,
        CancellationToken callerCancellation,
        out long revision)
    {
        revision = dispatch.InitialRevision;
        if (!string.Equals(
                permit.Authorization.ToolName,
                action.Request.ToolName,
                StringComparison.Ordinal)
            || action.Request.IsMutation
                && permit.Authorization.Source == AgentAuthorizationSource.AutoPolicy)
        {
            return GitFailure(
                "git_authorization_rejected",
                revision,
                HostErrorCode.InvalidRequest);
        }

        if (permit.CancellationToken.IsCancellationRequested
            || dispatch.RuntimeCancellation.IsCancellationRequested
            || callerCancellation.IsCancellationRequested)
        {
            return CancelledGitAction(
                permit,
                dispatch.RuntimeCancellation,
                callerCancellation,
                revision);
        }

        if (action.Request is AgentGitRequest.Push)
        {
            return GitFailure(
                "git_push_transport_unavailable",
                revision,
                HostErrorCode.InvalidRequest);
        }

        var contextResult = ResolveExactAgentContext(action.Proposal.Target);
        if (contextResult is HostResult<AgentContextSnapshot>.Failure contextFailure)
        {
            return HostResult<GitAgentOperationResult>.Fail(
                contextFailure.Error,
                contextFailure.CurrentRevision);
        }

        var context = ((HostResult<AgentContextSnapshot>.Success)contextResult).Value;
        revision = context.Revision;
        AgentActionExecutionBinding currentBinding;
        try
        {
            currentBinding = _agentGitActionComposer!.BindForExecution(action, context);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException)
        {
            return GitFailure(
                "git_context_changed",
                revision,
                HostErrorCode.InvalidRequest);
        }

        if (!GitBindingsMatch(consumedBinding, currentBinding)
            || !AuthorizationMatchesBinding(permit.Authorization, currentBinding))
        {
            return GitFailure(
                "git_binding_changed",
                revision,
                HostErrorCode.InvalidRequest);
        }

        var panel = context.Panels.SingleOrDefault(candidate =>
            candidate.PanelId == action.Request.PanelId
            && candidate.SessionId == dispatch.Session.Id);
        if (panel?.SessionRevision != dispatch.ExpectedSessionRevision
            || panel.WorkspaceRevision != dispatch.ExpectedWorkspaceRevision
            || panel.GraphSequence != dispatch.ExpectedGraphSequence
            || panel.Kind != PanelKind.Git
            || !panel.Capabilities.Contains(
                action.Request.RequiredSessionCapability,
                StringComparer.Ordinal)
            || !TryGetSession(dispatch.Session.Id, out var currentSession)
            || !ReferenceEquals(currentSession, dispatch.Session)
            || !dispatch.Session.CanExecuteAgentGitAction(
                dispatch.Git,
                dispatch.ExpectedBinding,
                dispatch.ExpectedSessionRevision,
                action.Request.RequiredSessionCapability,
                dispatch.RuntimeCancellation))
        {
            return GitFailure(
                "git_session_changed",
                revision,
                HostErrorCode.InvalidRequest);
        }

        return null;
    }

    private static GitDispatch CaptureGitDispatch(
        AgentGitRequest request,
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
            || session.Engine is not IGitPanelSession git
            || session.Engine.Kind != PanelKind.Git
            || !descriptor.Capabilities.Contains(request.RequiredSessionCapability)
            || !git.Capabilities.Contains(request.RequiredSessionCapability))
        {
            throw new InvalidOperationException(
                "The exact session does not support this governed Git action.");
        }

        return new GitDispatch(
            session,
            git,
            git.Binding,
            expectedSessionRevision,
            expectedWorkspaceRevision,
            expectedGraphSequence,
            session.CaptureRuntimeAuthority(),
            initialRevision,
            binding);
    }

    private static ValueTask<GitAgentOperationResult> ExecuteGitAsync(
        IGitPanelSession git,
        AgentGitRequest request,
        CancellationToken cancellationToken) =>
        request switch
        {
            AgentGitRequest.ReadState => git.ReadStateAsync(cancellationToken),
            AgentGitRequest.ReadDiff value => git.ReadDiffAsync(
                value.State,
                value.Change,
                value.Area,
                cancellationToken),
            AgentGitRequest.ReadRemoteRef value => git.ReadRemoteRefAsync(
                value.State,
                value.Remote,
                value.Branch,
                cancellationToken),
            AgentGitRequest.Stage value => git.StageAsync(
                value.State,
                value.Change,
                cancellationToken),
            AgentGitRequest.Unstage value => git.UnstageAsync(
                value.State,
                value.Change,
                cancellationToken),
            AgentGitRequest.BranchCreate value => git.CreateBranchAsync(
                value.State,
                value.Name,
                cancellationToken),
            AgentGitRequest.BranchCheckout value => git.CheckoutBranchAsync(
                value.State,
                value.Branch,
                cancellationToken),
            AgentGitRequest.Commit value => git.CommitAsync(
                value.State,
                value.Subject,
                value.Body,
                cancellationToken),
            AgentGitRequest.Push value => git.PushAsync(
                value.State,
                value.RemoteState,
                value.Remote,
                value.Branch,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

    private static HostResult<GitAgentOperationResult> MapGitOperationResult(
        GitAgentOperationResult result,
        long revision) => result switch
        {
            GitAgentOperationResult.OutcomeUnknown value => GitFailure(
                value.StableCode,
                revision),
            GitAgentOperationResult.Rejected value => GitFailure(
                value.StableCode,
                revision,
                HostErrorCode.InvalidRequest),
            _ => HostResult<GitAgentOperationResult>.Succeed(result, revision),
        };

    private async ValueTask<HostResult<GitAgentOperationResult>> CompleteGitActionAsync(
        AgentActionPermit permit,
        HostResult<GitAgentOperationResult> result)
    {
        var (outcome, stableCode, count) = result switch
        {
            HostResult<GitAgentOperationResult>.Failure failure
                when failure.Error.Code == HostErrorCode.Cancelled =>
                (AgentActionOutcome.Cancelled, failure.Error.StableCode, (int?)null),
            HostResult<GitAgentOperationResult>.Failure failure =>
                (AgentActionOutcome.Failed, failure.Error.StableCode, (int?)null),
            HostResult<GitAgentOperationResult>.Success success =>
                (AgentActionOutcome.Succeeded, "git_action_completed", GitResultCount(success.Value)),
            _ => throw new InvalidOperationException(
                "A governed Git dispatch returned an unknown result."),
        };
        return await CompleteConsumedAgentActionAsync(
                permit,
                Completion(permit, outcome, stableCode, count),
                result,
                GitResultRevision(result))
            .ConfigureAwait(false);
    }

    private static int GitResultCount(GitAgentOperationResult result) => result switch
    {
        GitAgentOperationResult.State value => value.Value.Changes.Count,
        GitAgentOperationResult.Diff value => value.Value.LineCount,
        GitAgentOperationResult.RemoteRef => 1,
        GitAgentOperationResult.Mutation value => value.Value.ChangedPathCount,
        _ => 0,
    };

    private static HostResult<GitAgentOperationResult> MapGitAuthorizationFailure(
        AgentAuthorizationError error,
        long revision)
    {
        var hostError = error.Code switch
        {
            AgentAuthorizationErrorCode.AuthorizationExpired
                or AgentAuthorizationErrorCode.ApprovalExpired =>
                new HostError(
                    HostErrorCode.DeadlineExceeded,
                    "git_authorization_expired",
                    "The one-action Git authorization expired."),
            AgentAuthorizationErrorCode.Cancelled
                or AgentAuthorizationErrorCode.RunCancelled =>
                new HostError(
                    HostErrorCode.Cancelled,
                    "git_action_cancelled",
                    "The governed Git action was cancelled."),
            AgentAuthorizationErrorCode.AuditUnavailable =>
                new HostError(
                    HostErrorCode.EngineFailed,
                    "git_audit_unavailable",
                    "The Git-agent audit trail is unavailable."),
            _ => new HostError(
                HostErrorCode.InvalidRequest,
                "git_authorization_rejected",
                "The exact one-action Git authorization was rejected."),
        };
        return HostResult<GitAgentOperationResult>.Fail(hostError, revision);
    }

    private static HostResult<GitAgentOperationResult> CancelledGitAction(
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
        return HostResult<GitAgentOperationResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                stableCode,
                "The governed Git action was cancelled."),
            revision);
    }

    private static HostResult<GitAgentOperationResult> GitFailure(
        string stableCode,
        long revision,
        HostErrorCode code = HostErrorCode.EngineFailed,
        bool retryable = false) =>
        HostResult<GitAgentOperationResult>.Fail(
            new HostError(
                code,
                stableCode,
                "The Git panel could not complete the governed action.",
                retryable),
            revision);

    private static long GitResultRevision(HostResult<GitAgentOperationResult> result) =>
        result switch
        {
            HostResult<GitAgentOperationResult>.Success success => success.ResultingRevision,
            HostResult<GitAgentOperationResult>.Failure failure => failure.CurrentRevision,
            _ => throw new InvalidOperationException(
                "A governed Git action returned an unknown result."),
        };

    private static bool GitBindingsMatch(
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

    private sealed record GitDispatch(
        HostedSession Session,
        IGitPanelSession Git,
        GitSessionBinding ExpectedBinding,
        long ExpectedSessionRevision,
        long ExpectedWorkspaceRevision,
        long ExpectedGraphSequence,
        CancellationToken RuntimeCancellation,
        long InitialRevision,
        AgentActionExecutionBinding Binding);
}
