using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteStatisticsProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        IReadOnlySet<PanelInstanceId> resizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> browserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> fileMetadata,
        CancellationToken cancellationToken)
    {
        if (_agentStatisticsHost is null || _statisticsComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var eligibleStatistics = context.Panels
            .Where(StatisticsAgentToolSet.Supports)
            .ToArray();
        if (eligibleStatistics.Length == 0)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var exactTarget = context.Target
            is AgentTarget.Panel or AgentTarget.ConnectionSession;
        var parsed = exactTarget
            ? StatisticsAgentToolParser.Parse(
                proposal,
                eligibleStatistics.Single())
            : StatisticsAgentToolParser.Parse(proposal, eligibleStatistics);
        if (parsed is StatisticsAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var selected = (StatisticsAgentIntentResult.Parsed)parsed;
        PanelInstanceId? resultPanelId = exactTarget
            ? null
            : selected.PanelId;
        var panel = context.Panels.SingleOrDefault(
            candidate => candidate.PanelId == selected.PanelId);
        if (panel is null || !StatisticsAgentToolSet.Supports(panel))
        {
            return CreateRejectedResult(
                proposal,
                "target_changed",
                resultPanelId);
        }

        UpdateTargetPresentation(
            context,
            resizeEligiblePanelIds,
            browserEligiblePanelIds,
            fileMetadata);

        AgentStatisticsReadAction action;
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
            action = _statisticsComposer.Prepare(
                envelope,
                context,
                new AgentStatisticsReadRequest(selected.PanelId));
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            _ = exception;
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

        if (authorization
            is not AgentAuthorizationResult.Authorized authorizedResult)
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
        HostResult<AgentStatisticsReadResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentStatisticsHost
                    .RunAgentStatisticsReadAsync(
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
                hostResult = StatisticsActionCancelled(context.Revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
                return CreateRejectedResult(
                    proposal,
                    "statistics_capture_failed",
                    resultPanelId);
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation)
                .ConfigureAwait(false);
        }

        hostResult = NormalizeRequestedStatisticsCancellation(
            hostResult,
            actionCancellation.CancellationRequested
                && !cancellationToken.IsCancellationRequested);
        if (hostResult is HostResult<AgentStatisticsReadResult>.Failure failure)
        {
            var stableCode =
                StatisticsAgentToolResultJson.ProviderStableCode(failure.Error);
            return CreateFailedResult(
                proposal,
                stableCode,
                StatisticsAgentToolResultJson.Failure(
                    failure.Error,
                    resultPanelId));
        }

        if (hostResult
            is not HostResult<AgentStatisticsReadResult>.Success success)
        {
            return CreateRejectedResult(
                proposal,
                "statistics_capture_failed",
                resultPanelId);
        }

        StatisticsAgentToolJsonProjection projection;
        try
        {
            projection = StatisticsAgentToolResultJson.Project(
                success.Value,
                resultPanelId);
        }
        catch (Exception exception)
            when (exception is
                ArgumentException
                or InvalidOperationException
                or OverflowException)
        {
            _ = exception;
            return CreateRejectedResult(
                proposal,
                "statistics_result_invalid",
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

    private static HostResult<AgentStatisticsReadResult>
        NormalizeRequestedStatisticsCancellation(
            HostResult<AgentStatisticsReadResult> result,
            bool cancellationRequested)
    {
        if (!cancellationRequested
            || result is not HostResult<AgentStatisticsReadResult>.Failure
            {
                Error:
                {
                    Code: HostErrorCode.Cancelled,
                    StableCode: "cancelled" or "operation_cancelled",
                },
            } failure)
        {
            return result;
        }

        return HostResult<AgentStatisticsReadResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                "caller_cancelled",
                "The Statistics observation was cancelled."),
            failure.CurrentRevision);
    }

    private static HostResult<AgentStatisticsReadResult>
        StatisticsActionCancelled(long revision) =>
        HostResult<AgentStatisticsReadResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                "caller_cancelled",
                "The Statistics observation was cancelled."),
            revision);

    private static bool IsStatisticsTool(string toolName) =>
        string.Equals(
            toolName,
            BuiltInAgentTools.StatisticsRead,
            StringComparison.Ordinal);

    private sealed class StatisticsToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context)
        {
            if (runtime._agentStatisticsHost is null
                || runtime._statisticsComposer is null)
            {
                return [];
            }

            if (context.Context.Target is AgentTarget.Workspace)
            {
                return StatisticsAgentToolSet.ForWorkspace();
            }

            var eligiblePanels = context.Context.Panels
                .Where(StatisticsAgentToolSet.Supports)
                .ToArray();
            if (eligiblePanels.Length == 0)
            {
                return [];
            }

            return context.HasExactTarget
                ? StatisticsAgentToolSet.For(eligiblePanels[0])
                : StatisticsAgentToolSet.For(eligiblePanels);
        }

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            IsStatisticsTool(toolName)
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
            runtime.ExecuteStatisticsProposalAsync(
                request.Proposal,
                request.Descriptor,
                context.Context,
                context.ResizeEligiblePanelIds,
                context.BrowserEligiblePanelIds,
                context.FileMetadata,
                cancellationToken);
    }
}
