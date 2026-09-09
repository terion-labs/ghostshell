using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteDockerProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        IReadOnlySet<PanelInstanceId> resizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> browserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> fileMetadata,
        CancellationToken cancellationToken)
    {
        if (_agentDockerHost is null || _dockerComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var eligible = context.Panels
            .Where(panel => panel.Kind == PanelKind.Docker)
            .ToArray();
        if (eligible.Length == 0)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var exactTarget = context.Target
            is AgentTarget.Panel or AgentTarget.ConnectionSession;
        if (DockerAgentToolSet.IsControlTool(proposal.ToolName))
        {
            return await ExecuteDockerControlProposalAsync(
                    proposal,
                    descriptor,
                    context,
                    eligible,
                    exactTarget,
                    resizeEligiblePanelIds,
                    browserEligiblePanelIds,
                    fileMetadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var parsed = exactTarget
            ? DockerAgentToolParser.Parse(proposal, eligible.Single())
            : DockerAgentToolParser.Parse(proposal, eligible);
        if (parsed is DockerAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var selected = (DockerAgentIntentResult.Parsed)parsed;
        PanelInstanceId? resultPanelId = exactTarget ? null : selected.PanelId;
        var panel = context.Panels.SingleOrDefault(candidate =>
            candidate.PanelId == selected.PanelId);
        if (panel is null
            || !DockerAgentToolSet.Supports(
                panel,
                selected.Request.RequiredSessionCapability))
        {
            return CreateRejectedResult(proposal, "target_changed", resultPanelId);
        }

        UpdateTargetPresentation(
            context,
            resizeEligiblePanelIds,
            browserEligiblePanelIds,
            fileMetadata);

        AgentDockerReadAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            var envelope = new AgentActionEnvelope(
                AgentActionId.New(),
                GetRequiredSession().RunId,
                GetOrCreateAgent(),
                GetPolicyGeneration(),
                now,
                now + ActionLifetime);
            action = _dockerComposer.Prepare(envelope, context, selected.Request);
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
        HostResult<AgentDockerReadResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentDockerHost
                    .RunAgentDockerReadAsync(
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
                hostResult = HostResult<AgentDockerReadResult>.Fail(
                    new HostError(
                        HostErrorCode.Cancelled,
                        "caller_cancelled",
                        "The Docker observation was cancelled."),
                    context.Revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return CreateRejectedResult(
                    proposal,
                    "docker_read_failed",
                    resultPanelId);
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation).ConfigureAwait(false);
        }

        if (hostResult is HostResult<AgentDockerReadResult>.Failure failure)
        {
            var stableCode = DockerAgentToolResultJson.ProviderStableCode(
                failure.Error);
            return CreateFailedResult(
                proposal,
                stableCode,
                DockerAgentToolResultJson.Failure(
                    failure.Error,
                    resultPanelId));
        }

        if (hostResult is not HostResult<AgentDockerReadResult>.Success success)
        {
            return CreateRejectedResult(
                proposal,
                "docker_read_failed",
                resultPanelId);
        }

        DockerAgentToolJsonProjection projection;
        try
        {
            projection = DockerAgentToolResultJson.Project(
                success.Value,
                resultPanelId);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or OverflowException)
        {
            return CreateRejectedResult(
                proposal,
                "docker_result_invalid",
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

    private sealed class DockerToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context)
        {
            if (runtime._agentDockerHost is null
                || runtime._dockerComposer is null)
            {
                return [];
            }

            if (context.Context.Target is AgentTarget.Workspace)
            {
                return DockerAgentToolSet.ForWorkspace(context.Context.Panels);
            }

            var eligible = context.Context.Panels
                .Where(panel => panel.Kind == PanelKind.Docker)
                .ToArray();
            if (eligible.Length == 0)
            {
                return [];
            }

            return context.HasExactTarget
                ? DockerAgentToolSet.For(eligible[0])
                : DockerAgentToolSet.For(eligible);
        }

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            DockerAgentToolSet.RequiredCapability(toolName) is not null
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
            runtime.ExecuteDockerProposalAsync(
                request.Proposal,
                request.Descriptor,
                context.Context,
                context.ResizeEligiblePanelIds,
                context.BrowserEligiblePanelIds,
                context.FileMetadata,
                cancellationToken);
    }
}
