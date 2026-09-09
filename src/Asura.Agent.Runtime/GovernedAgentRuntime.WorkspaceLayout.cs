using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult>
        ExecuteWorkspaceLayoutProposalAsync(
            AgentToolProposal proposal,
            AgentToolDescriptor descriptor,
            AgentContextSnapshot structuralContext,
            CancellationToken cancellationToken)
    {
        var mutationPort = Volatile.Read(ref _workspaceLayoutPort);
        if (_agentWorkspaceLayoutHost is null
            || _workspaceLayoutComposer is null
            || mutationPort is null
            || mutationPort.WorkspaceId
                != ((AgentTarget.Workspace)structuralContext.Target).WorkspaceId)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var parsed = WorkspaceLayoutAgentToolParser.Parse(
            proposal,
            structuralContext,
            mutationPort.SupportedPanelKinds);
        if (parsed is WorkspaceLayoutAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var intent = ((WorkspaceLayoutAgentIntentResult.Parsed)parsed).Intent;
        var request = intent.ToRequest();
        AgentWorkspaceLayoutAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            action = _workspaceLayoutComposer.Prepare(
                new AgentActionEnvelope(
                    AgentActionId.New(),
                    GetRequiredSession().RunId,
                    GetOrCreateAgent(),
                    GetPolicyGeneration(),
                    now,
                    now + ActionLifetime),
                structuralContext,
                request);
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            return CreateRejectedResult(proposal, "tool_request_rejected");
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
            is not AgentAuthorizationResult.Authorized authorized)
        {
            return CreateRejectedResult(
                proposal,
                "approval_still_required");
        }

        var actionCancellation = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken,
            ActivityPanelId(intent));
        HostResult<AgentWorkspaceLayoutReceipt> hostResult;
        try
        {
            try
            {
                hostResult = await _agentWorkspaceLayoutHost
                    .RunAgentWorkspaceLayoutActionAsync(
                        authorized.Authorization.Id,
                        action,
                        mutationPort,
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
                hostResult = HostResult<AgentWorkspaceLayoutReceipt>.Fail(
                    new HostError(
                        HostErrorCode.EngineFailed,
                        request is AgentWorkspaceLayoutRequest.ConnectionList
                            ? "workspace_connections_failed"
                            : WorkspaceLayoutAgentToolResultJson.OutcomeUnknownStableCode,
                        request is AgentWorkspaceLayoutRequest.ConnectionList
                            ? "The workspace connection observation was cancelled."
                            : "The workspace layout mutation may have completed."),
                    structuralContext.Revision);
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                _ = exception;
                hostResult = HostResult<AgentWorkspaceLayoutReceipt>.Fail(
                    new HostError(
                        HostErrorCode.EngineFailed,
                        request is AgentWorkspaceLayoutRequest.ConnectionList
                            ? "workspace_connections_failed"
                            : WorkspaceLayoutAgentToolResultJson.OutcomeUnknownStableCode,
                        request is AgentWorkspaceLayoutRequest.ConnectionList
                            ? "The workspace connection observation failed."
                            : "The workspace layout mutation may have completed."),
                    structuralContext.Revision);
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation)
                .ConfigureAwait(false);
        }

        if (hostResult is HostResult<AgentWorkspaceLayoutReceipt>.Success success)
        {
            await RefreshTargetPresentationBestEffortAsync(cancellationToken)
                .ConfigureAwait(false);
            return new AgentToolResult(
                proposal,
                AgentToolResultStatus.Succeeded,
                WorkspaceLayoutAgentToolResultJson.SuccessStableCode(
                    success.Value),
                JsonValue(WorkspaceLayoutAgentToolResultJson.Success(success.Value)));
        }

        var failure = (HostResult<AgentWorkspaceLayoutReceipt>.Failure)hostResult;
        var stableCode = WorkspaceLayoutAgentToolResultJson.ProviderStableCode(
            failure.Error);
        return CreateFailedResult(
            proposal,
            stableCode,
            WorkspaceLayoutAgentToolResultJson.Failure(failure.Error));
    }

    private sealed class WorkspaceLayoutToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context)
        {
            var port = Volatile.Read(ref runtime._workspaceLayoutPort);
            return runtime._agentWorkspaceLayoutHost is not null
                && runtime._workspaceLayoutComposer is not null
                && port is not null
                && context.Context.Target is AgentTarget.Workspace target
                && port.WindowId == target.WindowId
                && port.WorkspaceId == target.WorkspaceId
                    ? WorkspaceLayoutAgentToolSet.For(
                        context.Context,
                        port.SupportedPanelKinds)
                    : [];
        }

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            WorkspaceLayoutAgentToolParser.IsKnownTool(toolName)
                ? new ResolvedAgentToolContribution(toolName, ExecuteAsync)
                : null;

        private async ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            var context = request.Context;
            if (!runtime.MatchesPinnedGraphStructure(context)
                || context.Target is not AgentTarget.Workspace)
            {
                return CreateRejectedResult(
                    request.Proposal,
                    "target_changed");
            }

            return await runtime.ExecuteWorkspaceLayoutProposalAsync(
                    request.Proposal,
                    request.Descriptor,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static PanelInstanceId? ActivityPanelId(
        WorkspaceLayoutAgentIntent intent) => intent switch
        {
            WorkspaceLayoutAgentIntent.PanelConnect connect => connect.PanelId,
            WorkspaceLayoutAgentIntent.PanelSplit split => split.PanelId,
            WorkspaceLayoutAgentIntent.PanelClose close => close.PanelId,
            _ => null,
        };
}
