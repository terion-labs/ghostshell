using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentToolResult> ExecuteWebProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentContextSnapshot context,
        CancellationToken cancellationToken)
    {
        if (_agentWebToolHost is null || _webToolComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var parsed = WebAgentToolParser.Parse(proposal);
        if (parsed is WebAgentIntentResult.Rejected rejected)
        {
            return CreateRejectedResult(proposal, rejected.StableCode);
        }

        var request = ((WebAgentIntentResult.Parsed)parsed).Request;
        AgentWebToolAction action;
        try
        {
            var now = _timeProvider.GetUtcNow();
            action = _webToolComposer.Prepare(
                new AgentActionEnvelope(
                    AgentActionId.New(),
                    GetRequiredSession().RunId,
                    GetOrCreateAgent(),
                    GetPolicyGeneration(),
                    now,
                    now + ActionLifetime),
                context,
                request);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _ = exception;
            return CreateRejectedResult(proposal, "tool_request_rejected");
        }

        var authorization = await _broker.RequestAsync(action.Proposal, cancellationToken)
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
            return CreateRejectedResult(proposal, StableCode(denied.Error.Code));
        }

        if (authorization is not AgentAuthorizationResult.Authorized authorized)
        {
            return CreateRejectedResult(proposal, "approval_still_required");
        }

        var actionCancellation = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken);
        HostResult<AgentWebToolResult> hostResult;
        try
        {
            try
            {
                hostResult = await _agentWebToolHost.RunAgentWebToolAsync(
                        authorized.Authorization.Id,
                        action,
                        actionCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                hostResult = CancelledWeb(context.Revision, request.ToolName);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
                return CreateRejectedResult(proposal, "web_host_failed");
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation).ConfigureAwait(false);
        }

        if (hostResult is HostResult<AgentWebToolResult>.Failure failure)
        {
            return CreateFailedResult(
                proposal,
                WebAgentToolResultJson.ProviderStableCode(failure.Error),
                WebAgentToolResultJson.Failure(failure.Error));
        }

        if (hostResult is not HostResult<AgentWebToolResult>.Success success)
        {
            return CreateRejectedResult(proposal, "web_failed");
        }

        WebAgentToolJsonProjection projection;
        try
        {
            projection = WebAgentToolResultJson.Project(action.Request, success.Value);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            _ = exception;
            return CreateRejectedResult(proposal, "web_result_invalid");
        }

        return new AgentToolResult(
            proposal,
            projection.IsSuccess ? AgentToolResultStatus.Succeeded : AgentToolResultStatus.Failed,
            projection.StableCode,
            JsonValue(projection.Json));
    }

    private static HostResult<AgentWebToolResult> CancelledWeb(
        long revision,
        string toolName) =>
        HostResult<AgentWebToolResult>.Fail(
            new HostError(
                HostErrorCode.Cancelled,
                Prefix(toolName) + "cancelled",
                "The web operation was cancelled."),
            revision);

    private static string Prefix(string toolName) => toolName switch
    {
        BuiltInAgentTools.HttpFetch => "http_fetch_",
        BuiltInAgentTools.WebRead => "web_read_",
        BuiltInAgentTools.WebSearch => "web_search_",
        _ => "web_",
    };

    private sealed class WebToolContribution(GovernedAgentRuntime runtime)
        : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(AgentToolBuildContext context) =>
            runtime._agentWebToolHost is not null && runtime._webToolComposer is not null
                ? WebAgentToolSet.Tools
                : [];

        public ResolvedAgentToolContribution? Resolve(string toolName) =>
            WebAgentToolSet.Owns(toolName)
                ? new ResolvedAgentToolContribution(toolName, ExecuteAsync)
                : null;

        private ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            if (!runtime.MatchesPinnedScope(request.Context))
            {
                return ValueTask.FromResult(
                    CreateRejectedResult(request.Proposal, "target_changed"));
            }

            return runtime.ExecuteWebProposalAsync(
                request.Proposal,
                request.Descriptor,
                request.Context,
                cancellationToken);
        }
    }
}
