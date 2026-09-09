using Asura.Core;

namespace Asura.Application;

/// <summary>
/// The only workspace-panel execution port accepted by the governed agent
/// path. The host consumes one-action authorization after re-resolving the
/// exact graph panel and its current session.
/// </summary>
public interface IAgentPanelSessionHost
{
    ValueTask<HostResult<AgentPanelActionResult>> RunAgentPanelActionAsync(
        AgentAuthorizationId authorizationId,
        AgentPanelAction action,
        CancellationToken cancellationToken);
}

public abstract record AgentPanelActionResult
{
    private AgentPanelActionResult()
    {
    }

    public sealed record Inspected : AgentPanelActionResult
    {
        public Inspected(AgentContextPanel panel)
        {
            Panel = panel ?? throw new ArgumentNullException(nameof(panel));
        }

        public AgentContextPanel Panel { get; }
    }

    public sealed record Focused : AgentPanelActionResult
    {
        public Focused(AgentPanelFocusReceipt receipt)
        {
            Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        }

        public AgentPanelFocusReceipt Receipt { get; }
    }
}

public sealed record AgentPanelFocusReceipt
{
    public AgentPanelFocusReceipt(
        WindowInstanceId windowId,
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        long workspaceRevision,
        long graphSequence,
        bool changed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workspaceRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(graphSequence);
        WindowId = windowId;
        WorkspaceId = workspaceId;
        TabId = tabId;
        PanelId = panelId;
        WorkspaceRevision = workspaceRevision;
        GraphSequence = graphSequence;
        Changed = changed;
    }

    public WindowInstanceId WindowId { get; }

    public WorkspaceInstanceId WorkspaceId { get; }

    public TabInstanceId TabId { get; }

    public PanelInstanceId PanelId { get; }

    public long WorkspaceRevision { get; }

    public long GraphSequence { get; }

    public bool Changed { get; }
}
