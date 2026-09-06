using System.Collections.Immutable;
using GhostShell.Agent;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private async ValueTask<AgentMcpHostError?> EnsureMcpRunManifestAsync(
        CancellationToken cancellationToken)
    {
        if (_agentMcpHost is null || _mcpComposer is null)
        {
            return null;
        }

        AgentRunId? disallowedRunId;
        lock (_gate)
        {
            var effectivePermission =
                _effectivePolicy.GetPermission(AgentCapability.McpTools);
            var mayOpen = AllowsMcpDiscovery(effectivePermission);
            disallowedRunId = mayOpen
                ? null
                : _mcpManifest?.RunId;
            if (!mayOpen && disallowedRunId is null)
            {
                return null;
            }
        }

        if (disallowedRunId is { } openRunId)
        {
            await CloseMcpRunBestEffortAsync(openRunId)
                .ConfigureAwait(false);
            return null;
        }

        AgentRunId runId;
        ActorDescriptor actor;
        lock (_gate)
        {
            if (_mcpManifest is not null)
            {
                return null;
            }

            runId = _session?.RunId
                ?? throw new InvalidOperationException(
                    "An MCP manifest requires a bound agent run.");
            actor = _agent
                ?? throw new InvalidOperationException(
                    "An MCP manifest requires a bound agent actor.");
        }

        AgentMcpHostResult<AgentMcpRunManifest> result;
        try
        {
            result = await _agentMcpHost.OpenRunAsync(
                    new AgentMcpOpenRunRequest(
                        runId,
                        actor,
                        _workspaceId ?? throw new InvalidOperationException(
                            "An MCP run requires a bound workspace."),
                        _timeProvider.GetUtcNow().ToUniversalTime()),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return new AgentMcpHostError(
                "mcp_discovery_failed",
                "MCP discovery failed safely.");
        }

        if (result
            is AgentMcpHostResult<AgentMcpRunManifest>.Failure failure)
        {
            return failure.Error;
        }

        var manifest =
            ((AgentMcpHostResult<AgentMcpRunManifest>.Success)result).Value;
        if (manifest.RunId != runId)
        {
            await CloseMcpRunBestEffortAsync(runId).ConfigureAwait(false);
            return new AgentMcpHostError(
                "mcp_manifest_invalid",
                "The MCP host returned a manifest for another run.");
        }

        var accepted = false;
        lock (_gate)
        {
            if (!_disposed
                && _session?.RunId == runId
                && _runRegistered)
            {
                _mcpManifest = manifest;
                accepted = true;
            }
        }

        if (!accepted)
        {
            await CloseMcpRunBestEffortAsync(runId).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new AgentMcpHostError(
                "agent_run_stopped",
                "The agent run stopped during MCP discovery.");
        }

        return null;
    }

    private AgentMcpRunManifest? GetMcpRunManifest()
    {
        lock (_gate)
        {
            return _mcpManifest;
        }
    }

    private AgentMcpToolManifest? FindMcpTool(string providerAlias)
    {
        lock (_gate)
        {
            return _mcpManifest?.Tools.SingleOrDefault(tool =>
                string.Equals(
                    tool.ProviderAlias,
                    providerAlias,
                    StringComparison.Ordinal));
        }
    }

    private async ValueTask<AgentToolResult> ExecuteMcpProposalAsync(
        AgentToolProposal proposal,
        AgentToolDescriptor descriptor,
        AgentMcpToolManifest frozenTool,
        AgentContextSnapshot context,
        IReadOnlySet<PanelInstanceId> resizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> browserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> fileMetadata,
        CancellationToken cancellationToken)
    {
        if (_agentMcpHost is null || _mcpComposer is null)
        {
            return CreateRejectedResult(proposal, "tool_not_available");
        }

        var currentTool = FindMcpTool(proposal.ToolName);
        if (currentTool is null
            || currentTool.ManifestDigest != frozenTool.ManifestDigest)
        {
            return CreateRejectedResult(
                proposal,
                McpAgentToolResultJson.ManifestChangedStableCode);
        }

        AgentMcpToolCallRequest request;
        AgentMcpToolCallAction action;
        try
        {
            request = new AgentMcpToolCallRequest(
                currentTool,
                proposal.Arguments);
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            action = _mcpComposer.Prepare(
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
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            _ = exception;
            return CreateRejectedResult(
                proposal,
                "invalid_tool_arguments");
        }

        UpdateTargetPresentation(
            context,
            resizeEligiblePanelIds,
            browserEligiblePanelIds,
            fileMetadata);
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

        if (authorized.Authorization.Source is not (
                AgentAuthorizationSource.HumanApproval
                or AgentAuthorizationSource.YoloPolicy))
        {
            return CreateRejectedResult(
                proposal,
                "mcp_human_approval_required");
        }

        var actionCancellation = BeginToolActivity(
            descriptor,
            action.Proposal.Presentation,
            cancellationToken);
        AgentMcpHostResult<AgentMcpToolCallReceipt> hostResult;
        try
        {
            try
            {
                hostResult = await _agentMcpHost.RunToolAsync(
                        authorized.Authorization.Id,
                        action,
                        actionCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = exception;
                hostResult =
                    new AgentMcpHostResult<AgentMcpToolCallReceipt>.Failure(
                        new AgentMcpHostError(
                            McpAgentToolResultJson.OutcomeUnknownStableCode,
                            "The MCP call outcome could not be confirmed.",
                            outcomeUnknown: true));
            }
        }
        finally
        {
            await EndToolActivityAsync(actionCancellation)
                .ConfigureAwait(false);
        }

        if (hostResult
            is AgentMcpHostResult<AgentMcpToolCallReceipt>.Failure failure)
        {
            var stableCode = failure.Error.OutcomeUnknown
                ? McpAgentToolResultJson.OutcomeUnknownStableCode
                : failure.Error.StableCode;
            if (failure.Error.OutcomeUnknown)
            {
                await CloseMcpRunIfOpenBestEffortAsync(
                        GetRequiredSession().RunId)
                    .ConfigureAwait(false);
            }

            return CreateFailedResult(
                proposal,
                stableCode,
                McpAgentToolResultJson.Failure(stableCode));
        }

        var receipt =
            ((AgentMcpHostResult<AgentMcpToolCallReceipt>.Success)hostResult).Value;
        try
        {
            return new AgentToolResult(
                proposal,
                receipt.IsError
                    ? AgentToolResultStatus.Failed
                    : AgentToolResultStatus.Succeeded,
                receipt.IsError
                    ? "mcp_tool_error"
                    : "mcp_tool_succeeded",
                JsonValue(receipt.ProviderJson));
        }
        catch (ArgumentException)
        {
            return CreateFailedResult(
                proposal,
                "mcp_result_invalid",
                McpAgentToolResultJson.Failure("mcp_result_invalid"));
        }
    }

    private async ValueTask CloseMcpRunBestEffortAsync(AgentRunId runId)
    {
        lock (_gate)
        {
            if (_mcpManifest?.RunId == runId)
            {
                _mcpManifest = null;
            }
        }

        if (_agentMcpHost is null)
        {
            return;
        }

        try
        {
            await _agentMcpHost
                .CloseRunAsync(runId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
        }
    }

    private async ValueTask CloseMcpRunIfOpenBestEffortAsync(
        AgentRunId runId)
    {
        lock (_gate)
        {
            if (_mcpManifest?.RunId != runId)
            {
                return;
            }
        }

        await CloseMcpRunBestEffortAsync(runId).ConfigureAwait(false);
    }

    private async ValueTask<GovernedAgentSendResult>
        QuarantineMcpManifestChangeAsync(
            CancellationTokenSource turnCancellation,
            IReadOnlyList<AgentChatMessage> messages)
    {
        NativeAgentSession? session;
        lock (_gate)
        {
            session = _session;
        }

        session?.Cancel();
        var revocationError = await CancelRegisteredRunBestEffortAsync(
                McpAgentToolResultJson.ManifestChangedStableCode,
                CancellationToken.None)
            .ConfigureAwait(false);
        return FinishFailure(
            turnCancellation,
            messages,
            McpAgentToolResultJson.ManifestChangedStableCode,
            revocationError is null
                ? "The MCP manifest changed during the run. The MCP "
                    + "session was closed, and the run was quarantined until cleared."
                : "The MCP manifest changed during the run. The MCP "
                    + "session was closed, but agent authority revocation "
                    + "could not be confirmed.");
    }

    internal static bool AllowsMcpDiscovery(AgentPermission permission) =>
        permission is AgentPermission.Ask
            or AgentPermission.Auto
            or AgentPermission.Yolo;

    private sealed class McpToolContribution(
        GovernedAgentRuntime runtime) : IAgentToolContribution
    {
        public ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context) =>
            runtime._agentMcpHost is not null
                && runtime._mcpComposer is not null
                    ? McpAgentToolSet.For(context.McpManifest)
                    : [];

        public ResolvedAgentToolContribution? Resolve(string toolName)
        {
            var frozenTool = runtime.FindMcpTool(toolName);
            if (frozenTool is null)
            {
                return null;
            }

            return new ResolvedAgentToolContribution(
                BuiltInAgentTools.McpCall,
                (request, cancellationToken) => ExecuteAsync(
                    request,
                    frozenTool,
                    cancellationToken));
        }

        private ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionRequest request,
            AgentMcpToolManifest frozenTool,
            CancellationToken cancellationToken) =>
            runtime.ExecutePanelToolContributionAsync(
                request,
                (boundRequest, context, boundCancellationToken) =>
                    runtime.ExecuteMcpProposalAsync(
                        boundRequest.Proposal,
                        boundRequest.Descriptor,
                        frozenTool,
                        context.Context,
                        context.ResizeEligiblePanelIds,
                        context.BrowserEligiblePanelIds,
                        context.FileMetadata,
                        boundCancellationToken),
                cancellationToken);
    }
}
