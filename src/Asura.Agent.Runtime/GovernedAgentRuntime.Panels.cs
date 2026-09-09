using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecutePanelProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        IReadOnlySet<PanelInstanceId> resizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> browserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> fileMetadata,
        CancellationToken cancellationToken)
    {
        if (_agentPanelHost is null || _panelComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var parsed = PanelAgentToolParser.Parse(proposal, context);
        if (parsed is PanelAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var selected = (PanelAgentIntentResult.Parsed)parsed;
        var panel = context.Panels.SingleOrDefault(
            candidate => candidate.PanelId == selected.PanelId);
        if (panel?.SessionId is null
            || !PanelAgentToolSet.IsEligible(panel))
        {
            return CreateRejectedResult(proposal, "target_changed");
        }

        UpdateTargetPresentation(
            context,
            resizeEligiblePanelIds,
            browserEligiblePanelIds,
            fileMetadata);

        AgentPanelAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            action = _panelComposer.Prepare(
                new AgentActionEnvelope(
                    AgentActionId.New(),
                    GetRequiredSession().RunId,
                    GetOrCreateAgent(),
                    GetPolicyGeneration(),
                    now,
                    now + ActionLifetime),
                context,
                CreatePanelRequest(
                    selected.Intent,
                    selected.PanelId));
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
                panel.PanelId);
        }

        if (authorization
            is not AgentAuthorizationResult.Authorized authorizedResult)
        {
            return CreateRejectedResult(
                proposal,
                "approval_still_required",
                panel.PanelId);
        }

        var actionCancellation = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken,
            selected.PanelId);
        HostResult<AgentPanelActionResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentPanelHost
                    .RunAgentPanelActionAsync(
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
                hostResult = HostResult<AgentPanelActionResult>.Fail(
                    new HostError(
                        HostErrorCode.Cancelled,
                        "caller_cancelled",
                        "The panel action was cancelled."),
                    context.Revision);
            }
            catch (Exception exception)
                when (exception is not OutOfMemoryException)
            {
                _ = exception;
                return CreateRejectedResult(
                    proposal,
                    "panel_host_failed",
                    panel.PanelId);
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation)
                .ConfigureAwait(false);
        }

        if (hostResult is HostResult<AgentPanelActionResult>.Success)
        {
            await RefreshTargetPresentationBestEffortAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return hostResult switch
        {
            HostResult<AgentPanelActionResult>.Success success =>
                new AgentToolResult(
                    proposal,
                    AgentToolResultStatus.Succeeded,
                    "tool_succeeded",
                    JsonValue(PanelAgentToolResultJson.Success(
                        success.Value,
                        panel.PanelId))),
            HostResult<AgentPanelActionResult>.Failure failure =>
                CreateFailedResult(
                    proposal,
                    StableCode(
                        failure.Error.StableCode,
                        "panel_action_failed"),
                    PanelAgentToolResultJson.Failure(
                        failure.Error,
                        panel.PanelId)),
            _ => CreateRejectedResult(
                proposal,
                "panel_action_failed",
                panel.PanelId),
        };
    }

    private static AgentPanelRequest CreatePanelRequest(
        PanelAgentIntent intent,
        PanelInstanceId panelId) =>
        intent switch
        {
            PanelAgentIntent.Inspect =>
                new AgentPanelRequest.Inspect(panelId),
            PanelAgentIntent.Focus =>
                new AgentPanelRequest.Focus(panelId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(intent),
                intent.GetType(),
                "The panel intent is unsupported."),
        };

    private static bool IsPanelTool(string toolName) =>
        toolName is
            BuiltInAgentTools.PanelInspect
            or BuiltInAgentTools.PanelFocus;

    private sealed class PanelToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context) =>
            runtime._agentPanelHost is not null
                && runtime._panelComposer is not null
                    ? context.Context.Target is AgentTarget.Workspace
                        ? PanelAgentToolSet.ForWorkspace()
                        : PanelAgentToolSet.For(context.Context)
                    : [];

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            IsPanelTool(toolName)
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
            runtime.ExecutePanelProposalAsync(
                request.Proposal,
                request.Descriptor,
                context.Context,
                context.ResizeEligiblePanelIds,
                context.BrowserEligiblePanelIds,
                context.FileMetadata,
                cancellationToken);
    }
}
