using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal abstract record WorkspaceLayoutAgentIntent
{
    private WorkspaceLayoutAgentIntent()
    {
    }

    public sealed record TabCreate(PanelKind Kind, string? ConnectionRef)
        : WorkspaceLayoutAgentIntent;

    public sealed record ConnectionList : WorkspaceLayoutAgentIntent;

    public sealed record PanelConnect(
        PanelInstanceId PanelId,
        string ConnectionRef) : WorkspaceLayoutAgentIntent;

    public sealed record TabClose(TabInstanceId TabId)
        : WorkspaceLayoutAgentIntent;

    public sealed record PanelAdd(
        TabInstanceId TabId,
        PanelKind Kind,
        string? ConnectionRef)
        : WorkspaceLayoutAgentIntent;

    public sealed record PanelSplit(
        PanelInstanceId PanelId,
        AgentPanelSplitOrientation Orientation,
        PanelKind Kind,
        string? ConnectionRef) : WorkspaceLayoutAgentIntent;

    public sealed record PanelClose(PanelInstanceId PanelId)
        : WorkspaceLayoutAgentIntent;

    public AgentWorkspaceLayoutRequest ToRequest() => this switch
    {
        TabCreate create =>
            new AgentWorkspaceLayoutRequest.TabCreate(
                create.Kind,
                create.ConnectionRef),
        ConnectionList => new AgentWorkspaceLayoutRequest.ConnectionList(),
        PanelConnect connect => new AgentWorkspaceLayoutRequest.PanelConnect(
            connect.PanelId,
            connect.ConnectionRef),
        TabClose close =>
            new AgentWorkspaceLayoutRequest.TabClose(close.TabId),
        PanelAdd add =>
            new AgentWorkspaceLayoutRequest.PanelAdd(
                add.TabId,
                add.Kind,
                add.ConnectionRef),
        PanelSplit split =>
            new AgentWorkspaceLayoutRequest.PanelSplit(
                split.PanelId,
                split.Orientation,
                split.Kind,
                split.ConnectionRef),
        PanelClose close =>
            new AgentWorkspaceLayoutRequest.PanelClose(close.PanelId),
        _ => throw new ArgumentOutOfRangeException(
            nameof(WorkspaceLayoutAgentIntent),
            GetType(),
            "The workspace layout intent is unsupported."),
    };
}

internal abstract record WorkspaceLayoutAgentIntentResult
{
    private WorkspaceLayoutAgentIntentResult()
    {
    }

    public sealed record Parsed(WorkspaceLayoutAgentIntent Intent)
        : WorkspaceLayoutAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : WorkspaceLayoutAgentIntentResult;
}
