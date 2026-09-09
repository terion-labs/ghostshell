using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteDatabaseProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        IReadOnlySet<PanelInstanceId> resizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> browserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> fileMetadata,
        CancellationToken cancellationToken)
    {
        if (_agentDatabaseHost is null || _databaseComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var eligible = context.Panels
            .Where(panel => panel.Kind == PanelKind.DatabaseViewer)
            .ToArray();
        if (eligible.Length == 0)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var exactTarget = context.Target
            is AgentTarget.Panel or AgentTarget.ConnectionSession;
        var parsed = exactTarget
            ? DatabaseAgentToolParser.Parse(proposal, eligible.Single())
            : DatabaseAgentToolParser.Parse(proposal, eligible);
        if (parsed is DatabaseAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var selected = (DatabaseAgentIntentResult.Parsed)parsed;
        PanelInstanceId? resultPanelId = exactTarget ? null : selected.PanelId;
        var panel = context.Panels.SingleOrDefault(candidate =>
            candidate.PanelId == selected.PanelId);
        if (panel is null
            || !DatabaseAgentToolSet.Supports(
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

        AgentDatabaseReadAction action;
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
            action = _databaseComposer.Prepare(envelope, context, selected.Request);
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
        HostResult<AgentDatabaseReadResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentDatabaseHost
                    .RunAgentDatabaseReadAsync(
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
                hostResult = HostResult<AgentDatabaseReadResult>.Fail(
                    new HostError(
                        HostErrorCode.Cancelled,
                        "caller_cancelled",
                        "The database observation was cancelled."),
                    context.Revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return CreateRejectedResult(
                    proposal,
                    "database_read_failed",
                    resultPanelId);
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation).ConfigureAwait(false);
        }

        if (hostResult is HostResult<AgentDatabaseReadResult>.Failure failure)
        {
            var stableCode = DatabaseAgentToolResultJson.ProviderStableCode(
                failure.Error);
            return CreateFailedResult(
                proposal,
                stableCode,
                DatabaseAgentToolResultJson.Failure(
                    failure.Error,
                    resultPanelId));
        }

        if (hostResult
            is not HostResult<AgentDatabaseReadResult>.Success success)
        {
            return CreateRejectedResult(
                proposal,
                "database_read_failed",
                resultPanelId);
        }

        DatabaseAgentToolJsonProjection projection;
        try
        {
            projection = DatabaseAgentToolResultJson.Project(
                success.Value,
                resultPanelId);
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            return CreateRejectedResult(
                proposal,
                "database_result_invalid",
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

    private sealed class DatabaseToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context)
        {
            if (runtime._agentDatabaseHost is null
                || runtime._databaseComposer is null)
            {
                return [];
            }

            if (context.Context.Target is AgentTarget.Workspace)
            {
                return DatabaseAgentToolSet.ForWorkspace();
            }

            var eligible = context.Context.Panels
                .Where(panel => panel.Kind == PanelKind.DatabaseViewer)
                .ToArray();
            if (eligible.Length == 0)
            {
                return [];
            }

            return context.HasExactTarget
                ? DatabaseAgentToolSet.For(eligible[0])
                : DatabaseAgentToolSet.For(eligible);
        }

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            DatabaseAgentToolSet.RequiredCapability(toolName) is not null
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
            runtime.ExecuteDatabaseProposalAsync(
                request.Proposal,
                request.Descriptor,
                context.Context,
                context.ResizeEligiblePanelIds,
                context.BrowserEligiblePanelIds,
                context.FileMetadata,
                cancellationToken);
    }
}
