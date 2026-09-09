using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteGitProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        IReadOnlySet<PanelInstanceId> resizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> browserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> fileMetadata,
        CancellationToken cancellationToken)
    {
        if (_agentGitHost is null || _gitComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var eligible = context.Panels
            .Where(panel => panel.Kind == PanelKind.Git)
            .ToArray();
        if (eligible.Length == 0)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var exactTarget = context.Target
            is AgentTarget.Panel or AgentTarget.ConnectionSession;
        var parsed = exactTarget
            ? GitAgentToolParser.Parse(proposal, eligible.Single())
            : GitAgentToolParser.Parse(proposal, eligible);
        if (parsed is GitAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var selected = (GitAgentIntentResult.Parsed)parsed;
        PanelInstanceId? resultPanelId = exactTarget ? null : selected.PanelId;
        var panel = context.Panels.SingleOrDefault(candidate =>
            candidate.PanelId == selected.PanelId);
        if (panel is null
            || !GitAgentToolSet.Supports(panel, selected.Request.ToolName))
        {
            return CreateRejectedResult(proposal, "target_changed", resultPanelId);
        }

        UpdateTargetPresentation(
            context,
            resizeEligiblePanelIds,
            browserEligiblePanelIds,
            fileMetadata);

        AgentGitAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            action = _gitComposer.Prepare(
                new AgentActionEnvelope(
                    AgentActionId.New(),
                    GetRequiredSession().RunId,
                    GetOrCreateAgent(),
                    GetPolicyGeneration(),
                    now,
                    now + ActionLifetime),
                context,
                selected.Request);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException)
        {
            return CreateRejectedResult(
                proposal,
                "tool_request_rejected",
                resultPanelId);
        }

        var authorization = await _broker
            .RequestAsync(action.Proposal, cancellationToken)
            .ConfigureAwait(false);
        if (authorization is AgentAuthorizationResult.ApprovalRequired required)
        {
            authorization = await AwaitApprovalAsync(
                    required.Approval,
                    yieldsInput: false,
                    cancellationToken)
                .ConfigureAwait(false);
            descriptor = required.Approval.Tool;
        }

        if (authorization is AgentAuthorizationResult.Denied denied)
        {
            return CreateRejectedResult(
                proposal,
                StableCode(denied.Error.Code),
                resultPanelId);
        }

        if (authorization is not AgentAuthorizationResult.Authorized authorized)
        {
            return CreateRejectedResult(
                proposal,
                "approval_still_required",
                resultPanelId);
        }

        var actionCancellation = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken,
            selected.PanelId);
        HostResult<GitAgentOperationResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentGitHost.RunAgentGitActionAsync(
                        authorized.Authorization.Id,
                        action,
                        actionCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                hostResult = HostResult<GitAgentOperationResult>.Fail(
                    new HostError(
                        HostErrorCode.Cancelled,
                        selected.Request.IsMutation
                            ? "git_mutation_outcome_unknown"
                            : "caller_cancelled",
                        "The governed Git action was cancelled."),
                    context.Revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return CreateRejectedResult(
                    proposal,
                    selected.Request.IsMutation
                        ? "git_mutation_outcome_unknown"
                        : "git_action_failed",
                    resultPanelId);
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation).ConfigureAwait(false);
        }

        if (hostResult is HostResult<GitAgentOperationResult>.Failure failure)
        {
            var stableCode = GitAgentToolResultJson.ProviderStableCode(failure.Error);
            return CreateFailedResult(
                proposal,
                stableCode,
                GitAgentToolResultJson.Failure(failure.Error, resultPanelId));
        }

        if (hostResult is not HostResult<GitAgentOperationResult>.Success success)
        {
            return CreateRejectedResult(
                proposal,
                "git_action_failed",
                resultPanelId);
        }

        GitAgentToolJsonProjection projection;
        try
        {
            projection = GitAgentToolResultJson.Project(success.Value, resultPanelId);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or OverflowException)
        {
            return CreateRejectedResult(
                proposal,
                "git_result_invalid",
                resultPanelId);
        }

        return new AgentToolResult(
            proposal,
            projection.IsSuccess
                ? AgentToolResultStatus.Succeeded
                : AgentToolResultStatus.Failed,
            projection.StableCode,
            JsonValue(projection.Json));
    }

    private sealed class GitToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context)
        {
            if (runtime._agentGitHost is null || runtime._gitComposer is null)
            {
                return [];
            }

            if (context.Context.Target is AgentTarget.Workspace)
            {
                return GitAgentToolSet.ForWorkspace(context.Context.Panels);
            }

            var eligible = context.Context.Panels
                .Where(panel => panel.Kind == PanelKind.Git)
                .ToArray();
            if (eligible.Length == 0)
            {
                return [];
            }

            return context.HasExactTarget
                ? GitAgentToolSet.For(eligible[0])
                : GitAgentToolSet.For(eligible);
        }

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            GitAgentToolSet.RequiredCapability(toolName) is not null
                ? new ResolvedAgentToolContribution(toolName, ExecuteAsync)
                : null;

        private ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken cancellationToken) =>
            runtime.ExecutePanelToolContributionAsync(
                request,
                ExecuteBoundAsync,
                cancellationToken);

        private ValueTask<AgentToolResult> ExecuteBoundAsync(
            AgentToolExecutionRequest request,
            AgentPanelToolContext context,
            CancellationToken cancellationToken) =>
            runtime.ExecuteGitProposalAsync(
                request.Proposal,
                request.Descriptor,
                context.Context,
                context.ResizeEligiblePanelIds,
                context.BrowserEligiblePanelIds,
                context.FileMetadata,
                cancellationToken);
    }
}
