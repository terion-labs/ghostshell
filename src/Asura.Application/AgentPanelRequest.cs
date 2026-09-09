using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Closed workspace-panel operations. The panel identifier is selected from a
/// freshly resolved run scope; graph ownership and revisions remain host-owned.
/// </summary>
public abstract record AgentPanelRequest
{
    private AgentPanelRequest()
    {
    }

    public sealed record Inspect(PanelInstanceId PanelId) : AgentPanelRequest;

    public sealed record Focus(PanelInstanceId PanelId) : AgentPanelRequest;
}
