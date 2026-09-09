using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteWorkspaceGraphProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot structuralContext,
        CancellationToken cancellationToken)
    {
        if (_agentWorkspaceGraphHost is null
            || _workspaceGraphComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        if (!WorkspaceGraphAgentToolSet.For(structuralContext)
            .Any(tool => string.Equals(
                tool.Name,
                proposal.ToolName,
                StringComparison.Ordinal)))
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var parsed = WorkspaceGraphAgentToolParser.Parse(proposal);
        if (parsed is WorkspaceGraphAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var intent = ((WorkspaceGraphAgentIntentResult.Parsed)parsed).Intent;
        AgentWorkspaceGraphAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            action = _workspaceGraphComposer.Prepare(
                new AgentActionEnvelope(
                    AgentActionId.New(),
                    GetRequiredSession().RunId,
                    GetOrCreateAgent(),
                    GetPolicyGeneration(),
                    now,
                    now + ActionLifetime),
                structuralContext,
                intent.ToRequest());
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            return CreateRejectedResult(
                proposal,
                "tool_request_rejected");
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
                StableCode(denied.Error.Code));
        }

        if (authorization
            is not AgentAuthorizationResult.Authorized authorizedResult)
        {
            return CreateRejectedResult(
                proposal,
                "approval_still_required");
        }

        var actionCancellation = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken);
        HostResult<AgentWorkspaceGraphActionResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentWorkspaceGraphHost
                    .RunAgentWorkspaceGraphActionAsync(
                        authorizedResult.Authorization.Id,
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
                hostResult = HostResult<AgentWorkspaceGraphActionResult>.Fail(
                    new HostError(
                        HostErrorCode.Cancelled,
                        "caller_cancelled",
                        "The workspace graph action was cancelled."),
                    structuralContext.Revision);
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                _ = exception;
                return CreateRejectedResult(
                    proposal,
                    "workspace_graph_host_failed");
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation)
                .ConfigureAwait(false);
        }

        if (hostResult
            is HostResult<AgentWorkspaceGraphActionResult>.Success success)
        {
            WorkspaceGraphAgentToolJsonProjection projection;
            try
            {
                projection = WorkspaceGraphAgentToolResultJson.Project(
                    success.Value);
            }
            catch (Exception exception)
                when (exception is ArgumentException
                    or InvalidOperationException
                    or OverflowException)
            {
                return CreateRejectedResult(
                    proposal,
                    "workspace_graph_result_invalid");
            }

            return new AgentToolResult(
                proposal,
                projection.IsSuccess
                    ? AgentToolResultStatus.Succeeded
                    : AgentToolResultStatus.Failed,
                projection.StableCode,
                JsonValue(projection.Json));
        }

        if (hostResult
            is HostResult<AgentWorkspaceGraphActionResult>.Failure failure)
        {
            return CreateFailedResult(
                proposal,
                WorkspaceGraphAgentToolResultJson.ProviderStableCode(
                    failure.Error),
                WorkspaceGraphAgentToolResultJson.Failure(
                    failure.Error));
        }

        return CreateRejectedResult(
            proposal,
            "workspace_graph_failed");
    }

    private static bool IsWorkspaceGraphTool(string toolName) =>
        toolName is
            BuiltInAgentTools.WorkspaceInspect
            or BuiltInAgentTools.TabList
            or BuiltInAgentTools.PanelList;

    private sealed class WorkspaceGraphToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context) =>
            runtime._agentWorkspaceGraphHost is not null
                && runtime._workspaceGraphComposer is not null
                    ? context.Context.Target is AgentTarget.Workspace
                        ? WorkspaceGraphAgentToolSet.ForWorkspace()
                        : WorkspaceGraphAgentToolSet.For(context.Context)
                    : [];

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            IsWorkspaceGraphTool(toolName)
                ? new ResolvedAgentToolContribution(
                    toolName,
                    ExecuteAsync)
                : null;

        private async ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            var structuralContext = request.Context;
            if (!runtime.MatchesPinnedGraphStructure(structuralContext))
            {
                return CreateRejectedResult(
                    request.Proposal,
                    "target_changed");
            }

            return await runtime.ExecuteWorkspaceGraphProposalAsync(
                    request.Proposal,
                    request.Descriptor,
                    structuralContext,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
