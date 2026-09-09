using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteProcessProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        IReadOnlySet<PanelInstanceId> resizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> browserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> fileMetadata,
        CancellationToken cancellationToken)
    {
        if (_agentProcessHost is null || _processComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var eligibleProcesses = context.Panels
            .Where(ProcessAgentToolSet.Supports)
            .ToArray();
        if (eligibleProcesses.Length == 0)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var exactTarget = context.Target
            is AgentTarget.Panel or AgentTarget.ConnectionSession;
        var parsed = exactTarget
            ? ProcessAgentToolParser.Parse(
                proposal,
                eligibleProcesses.Single())
            : ProcessAgentToolParser.Parse(
                proposal,
                eligibleProcesses);
        if (parsed is ProcessAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var selected = (ProcessAgentIntentResult.Parsed)parsed;
        PanelInstanceId? resultPanelId = exactTarget
            ? null
            : selected.PanelId;
        var panel = context.Panels.SingleOrDefault(
            candidate => candidate.PanelId == selected.PanelId);
        if (panel is null || !ProcessAgentToolSet.Supports(panel))
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

        AgentProcessListAction action;
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
            action = _processComposer.Prepare(
                envelope,
                context,
                new AgentProcessListRequest(
                    selected.PanelId,
                    selected.Intent.Limit,
                    selected.Intent.Sort,
                    selected.Intent.Offset,
                    selected.Intent.NameContains,
                    selected.Intent.ProcessId));
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
        HostResult<AgentProcessListResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentProcessHost
                    .RunAgentProcessListAsync(
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
                hostResult = ProcessActionCancelled(context.Revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
                return CreateRejectedResult(
                    proposal,
                    "processes_capture_failed",
                    resultPanelId);
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation)
                .ConfigureAwait(false);
        }

        hostResult = NormalizeRequestedProcessCancellation(
            hostResult,
            actionCancellation.CancellationRequested
                && !cancellationToken.IsCancellationRequested);
        if (hostResult is HostResult<AgentProcessListResult>.Failure failure)
        {
            var stableCode =
                ProcessAgentToolResultJson.ProviderStableCode(failure.Error);
            return CreateFailedResult(
                proposal,
                stableCode,
                ProcessAgentToolResultJson.Failure(
                    failure.Error,
                    resultPanelId));
        }

        if (hostResult
            is not HostResult<AgentProcessListResult>.Success success)
        {
            return CreateRejectedResult(
                proposal,
                "processes_capture_failed",
                resultPanelId);
        }

        ProcessAgentToolJsonProjection projection;
        try
        {
            projection = ProcessAgentToolResultJson.Project(
                success.Value,
                selected.Intent,
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
                "processes_result_invalid",
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

    private static HostResult<AgentProcessListResult>
        NormalizeRequestedProcessCancellation(
            HostResult<AgentProcessListResult> result,
            bool cancellationRequested)
    {
        if (!cancellationRequested
            || result is not HostResult<AgentProcessListResult>.Failure
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

        return HostResult<AgentProcessListResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                "caller_cancelled",
                "The process observation was cancelled."),
            failure.CurrentRevision);
    }

    private static HostResult<AgentProcessListResult> ProcessActionCancelled(
        long revision) =>
        HostResult<AgentProcessListResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                "caller_cancelled",
                "The process observation was cancelled."),
            revision);

    private static bool IsProcessTool(string toolName) =>
        string.Equals(
            toolName,
            BuiltInAgentTools.ProcessesList,
            StringComparison.Ordinal);

    private sealed class ProcessToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context)
        {
            if (runtime._agentProcessHost is null
                || runtime._processComposer is null)
            {
                return [];
            }

            if (context.Context.Target is AgentTarget.Workspace)
            {
                return ProcessAgentToolSet.ForWorkspace();
            }

            var eligiblePanels = context.Context.Panels
                .Where(ProcessAgentToolSet.Supports)
                .ToArray();
            if (eligiblePanels.Length == 0)
            {
                return [];
            }

            return context.HasExactTarget
                ? ProcessAgentToolSet.For(eligiblePanels[0])
                : ProcessAgentToolSet.For(eligiblePanels);
        }

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            IsProcessTool(toolName)
                ? new ResolvedAgentToolContribution(
                    toolName,
                    ExecuteAsync)
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
            runtime.ExecuteProcessProposalAsync(
                request.Proposal,
                request.Descriptor,
                context.Context,
                context.ResizeEligiblePanelIds,
                context.BrowserEligiblePanelIds,
                context.FileMetadata,
                cancellationToken);
    }
}
