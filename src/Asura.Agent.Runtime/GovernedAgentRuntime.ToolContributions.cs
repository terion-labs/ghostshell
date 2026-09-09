using System.Collections.Immutable;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

public sealed partial class GovernedAgentRuntime
{
    private ImmutableArray<IAgentToolContribution> CreateToolContributions() =>
    [
        new WorkspaceGraphToolContribution(this),
        new WorkspaceLayoutToolContribution(this),
        new PanelToolContribution(this),
        new TerminalToolContribution(this),
        new BrowserToolContribution(this),
        new WebToolContribution(this),
        new ProcessToolContribution(this),
        new StatisticsToolContribution(this),
        new DatabaseToolContribution(this),
        new DockerToolContribution(this),
        new GitToolContribution(this),
        new FileToolContribution(this),
        new McpToolContribution(this),
    ];

    private ResolvedAgentToolContribution? ResolveToolContribution(
        string toolName)
    {
        ResolvedAgentToolContribution? resolved = null;
        foreach (var contribution in _toolContributions)
        {
            var candidate = contribution.Resolve(toolName);
            if (candidate is null)
            {
                continue;
            }

            // A tool name can have only one owner. Ambiguity is rejected rather
            // than allowing registration order to select an execution path.
            if (resolved is not null)
            {
                return null;
            }

            resolved = candidate;
        }

        return resolved;
    }

    private async ValueTask<AgentToolResult>
        ExecutePanelToolContributionAsync(
            AgentToolExecutionRequest request,
            AgentPanelToolExecutor executor,
            CancellationToken cancellationToken)
    {
        if (!MatchesPinnedScope(request.Context))
        {
            return CreateRejectedResult(request.Proposal, "target_changed");
        }

        var context = request.Context;

        var resizeAttachments = await InspectResizeAttachmentsAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var browserEligiblePanelIds = await InspectBrowserAttachmentsAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var fileMetadata = await InspectFileSessionsAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var panelContext = new AgentPanelToolContext(
            context,
            resizeAttachments,
            browserEligiblePanelIds,
            fileMetadata);
        return await executor(
                request,
                panelContext,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private interface IAgentToolContribution
    {
        ImmutableArray<AgentToolDefinition> BuildTools(
            AgentToolBuildContext context);

        ResolvedAgentToolContribution? Resolve(string toolName);
    }

    private sealed record AgentToolBuildContext(
        AgentContextSnapshot Context,
        IReadOnlySet<PanelInstanceId> ResizeEligiblePanelIds,
        IReadOnlySet<PanelInstanceId> BrowserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> FileMetadata,
        AgentMcpRunManifest? McpManifest)
    {
        public bool HasExactTarget => Context.Target
            is AgentTarget.Panel or AgentTarget.ConnectionSession;
    }

    private sealed record AgentToolExecutionRequest(
        AgentToolProposal Proposal,
        AgentToolDescriptor Descriptor,
        AgentContextSnapshot Context);

    private sealed record AgentPanelToolContext(
        AgentContextSnapshot Context,
        IReadOnlyDictionary<PanelInstanceId, ResizeAttachmentBinding>
            ResizeAttachments,
        IReadOnlySet<PanelInstanceId> BrowserEligiblePanelIds,
        IReadOnlyDictionary<PanelInstanceId, FileSessionMetadata> FileMetadata)
    {
        public IReadOnlySet<PanelInstanceId> ResizeEligiblePanelIds =>
            ResizeAttachments.Keys.ToImmutableHashSet();
    }

    private sealed record ResolvedAgentToolContribution(
        string CatalogToolName,
        Func<
            AgentToolExecutionRequest,
            CancellationToken,
            ValueTask<AgentToolResult>> ExecuteAsync);

    private delegate ValueTask<AgentToolResult> AgentPanelToolExecutor(
        AgentToolExecutionRequest request,
        AgentPanelToolContext context,
        CancellationToken cancellationToken);
}
