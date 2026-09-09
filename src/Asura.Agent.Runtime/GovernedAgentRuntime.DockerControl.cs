using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteDockerControlProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        IReadOnlyList<AgentContextPanel> eligible,
        bool exactTarget,
        IReadOnlySet<PanelInstanceId> resizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> browserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> fileMetadata,
        CancellationToken cancellationToken)
    {
        var parsed = DockerAgentControlToolParser.Parse(
            proposal,
            eligible,
            requirePanelId: !exactTarget);
        if (parsed is DockerAgentControlIntent.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var selected = (DockerAgentControlIntent.Parsed)parsed;
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

        AgentDockerControlAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            action = _dockerComposer!.Prepare(
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

        var activity = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken,
            selected.PanelId);
        HostResult<AgentDockerControlResult> hostResult;
        try
        {
            hostResult = await _agentDockerHost!
                .RunAgentDockerControlAsync(
                    authorized.Authorization.Id,
                    action,
                    activity.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new AgentToolResult(
                proposal,
                AgentToolResultStatus.Failed,
                DockerAgentControlToolResultJson.OutcomeUnknownStableCode,
                JsonValue("{\"ok\":false,\"stable_code\":\"docker_mutation_outcome_unknown\",\"outcome\":\"outcome_unknown\",\"retryable\":false}"));
        }
        finally
        {
            await EndToolActivityAsync(activity).ConfigureAwait(false);
        }

        if (hostResult is HostResult<AgentDockerControlResult>.Failure failure)
        {
            return CreateFailedResult(
                proposal,
                failure.Error.StableCode,
                DockerAgentToolResultJson.Failure(failure.Error, resultPanelId));
        }

        var result = ((HostResult<AgentDockerControlResult>.Success)hostResult).Value;
        return new AgentToolResult(
            proposal,
            result.Outcome == DockerContainerControlOutcome.Applied
                ? AgentToolResultStatus.Succeeded
                : AgentToolResultStatus.Failed,
            result.StableCode,
            JsonValue(DockerAgentControlToolResultJson.Write(result, resultPanelId)));
    }
}
