using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteTerminalProposalAsync(
        AgentToolExecutionRequest request,
        AgentPanelToolContext panelContext,
        CancellationToken cancellationToken)
    {
        var proposal = request.Proposal;
        var descriptor = request.Descriptor;
        var context = panelContext.Context;
        var resizeAttachments = panelContext.ResizeAttachments;
        var resizeEligiblePanelIds =
            panelContext.ResizeEligiblePanelIds;
        var exactTarget = context.Target
            is AgentTarget.Panel or AgentTarget.ConnectionSession
            && context.Panels.Count == 1;
        var parsed = exactTarget
            ? TerminalAgentToolParser.Parse(
                proposal,
                context.Panels[0],
                resizeEligiblePanelIds)
            : TerminalAgentToolParser.Parse(
                proposal,
                context.Panels,
                resizeEligiblePanelIds);
        if (parsed is TerminalAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var selected = (TerminalAgentIntentResult.Parsed)parsed;
        var panel = context.Panels.SingleOrDefault(
            candidate => candidate.PanelId == selected.PanelId);
        if (panel?.SessionId is not { } sessionId)
        {
            return CreateRejectedResult(proposal, "target_changed");
        }

        var intent = selected.Intent;
        UpdateTargetPresentation(
            context,
            resizeEligiblePanelIds,
            panelContext.BrowserEligiblePanelIds,
            panelContext.FileMetadata);

        AgentTerminalAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            var proposalLifetime = intent is TerminalAgentIntent.WaitForDelay
                or TerminalAgentIntent.WaitForText
                or TerminalAgentIntent.WaitForChange
                or TerminalAgentIntent.WaitForStable
                or TerminalAgentIntent.WaitForPromptReady
                or TerminalAgentIntent.WaitForCommandFinished
                    ? descriptor.MaximumExecutionLifetime
                        + TimeSpan.FromMinutes(5)
                    : ActionLifetime;
            var envelope = new AgentActionEnvelope(
                AgentActionId.New(),
                GetRequiredSession().RunId,
                GetOrCreateAgent(),
                GetPolicyGeneration(),
                now,
                now + proposalLifetime);
            action = _composer.Prepare(
                envelope,
                context,
                CreateRequest(
                    intent,
                    sessionId,
                    resizeAttachments.GetValueOrDefault(panel.PanelId)));
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            return CreateRejectedResult(
                proposal,
                "tool_request_rejected",
                panel.PanelId);
        }

        var authorization = await _broker
            .RequestAsync(action.Proposal, cancellationToken)
            .ConfigureAwait(false);
        if (authorization is AgentAuthorizationResult.ApprovalRequired required)
        {
            authorization = await AwaitApprovalAsync(
                    required.Approval,
                    YieldsTerminalInput(intent),
                    cancellationToken)
                .ConfigureAwait(false);
            descriptor = required.Approval.Tool;
        }

        if (authorization is AgentAuthorizationResult.Denied denied)
        {
            return CreateRejectedResult(
                proposal,
                StableCode(denied.Error.Code),
                panel.PanelId);
        }

        if (authorization is not AgentAuthorizationResult.Authorized authorizedResult)
        {
            return CreateRejectedResult(
                proposal,
                "approval_still_required",
                panel.PanelId);
        }

        var authorized = authorizedResult.Authorization;
        var actionCancellation = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken,
            selected.PanelId);
        HostResult<AgentTerminalActionResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentTerminalHost.RunAgentTerminalActionAsync(
                        authorized.Id,
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
                when (actionCancellation.Token.IsCancellationRequested)
            {
                hostResult = HostResult<AgentTerminalActionResult>.Fail(
                    new HostError(
                        HostErrorCode.Cancelled,
                        "caller_cancelled",
                        "The terminal action was cancelled."),
                    context.Revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
                return CreateRejectedResult(
                    proposal,
                    "terminal_host_failed",
                    panel.PanelId);
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation).ConfigureAwait(false);
        }

        hostResult = NormalizeRequestedActionCancellation(
            hostResult,
            actionCancellation.CancellationRequested
                && !cancellationToken.IsCancellationRequested);
        if (hostResult is HostResult<AgentTerminalActionResult>.Success)
        {
            await RefreshTargetPresentationBestEffortAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return hostResult switch
        {
            HostResult<AgentTerminalActionResult>.Success success =>
                CreateSucceededResult(
                    proposal,
                    success.Value,
                    panel.PanelId),
            HostResult<AgentTerminalActionResult>.Failure failure =>
                CreateFailedResult(
                    proposal,
                    StableCode(failure.Error.StableCode, "terminal_action_failed"),
                    TerminalAgentToolResultJson.Failure(
                        failure.Error,
                        panel.PanelId)),
            _ => CreateRejectedResult(
                proposal,
                "terminal_action_failed",
                panel.PanelId),
        };
    }

    private sealed class TerminalToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context) =>
            context.Context.Target is AgentTarget.Workspace
                ? TerminalAgentToolSet.ForWorkspace()
                : context.HasExactTarget
                && context.Context.Panels.Count == 1
                    ? TerminalAgentToolSet.For(
                        context.Context.Panels[0],
                        context.ResizeEligiblePanelIds)
                    : TerminalAgentToolSet.For(
                        context.Context.Panels,
                        context.ResizeEligiblePanelIds);

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            TerminalAgentToolSet.IsToolName(toolName)
                ? new ResolvedAgentToolContribution(
                    toolName,
                    ExecuteAsync)
                : null;

        private ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken cancellationToken) =>
            runtime.ExecutePanelToolContributionAsync(
                request,
                runtime.ExecuteTerminalProposalAsync,
                cancellationToken);
    }
}
