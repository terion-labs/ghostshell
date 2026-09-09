namespace Asura.Core;

public sealed record WorkspaceSnapshot(
    WorkspaceId Id,
    string Name,
    string Accent,
    IReadOnlyList<ConnectionProfile> Connections,
    IReadOnlyList<WorkspaceTab> Tabs,
    AgentPolicy AgentPolicy)
{
    public WorkspaceTab ActiveTab =>
        Tabs.SingleOrDefault(tab => tab.IsActive)
        ?? throw new InvalidOperationException("A workspace must have one active tab.");
}

