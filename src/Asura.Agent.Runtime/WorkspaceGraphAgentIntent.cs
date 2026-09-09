using Asura.Application;

namespace Asura.Agent.Runtime;

internal abstract record WorkspaceGraphAgentIntent
{
    private WorkspaceGraphAgentIntent()
    {
    }

    public sealed record WorkspaceInspect : WorkspaceGraphAgentIntent;

    public sealed record TabList(int Offset) : WorkspaceGraphAgentIntent;

    public sealed record PanelList(int Offset) : WorkspaceGraphAgentIntent;

    public AgentWorkspaceGraphRequest ToRequest() =>
        this switch
        {
            WorkspaceInspect => new AgentWorkspaceGraphRequest.WorkspaceInspect(),
            TabList tabs => new AgentWorkspaceGraphRequest.TabList(tabs.Offset),
            PanelList panels => new AgentWorkspaceGraphRequest.PanelList(panels.Offset),
            _ => throw new ArgumentOutOfRangeException(
                nameof(WorkspaceGraphAgentIntent),
                GetType(),
                "The workspace graph intent is unsupported."),
        };
}

internal abstract record WorkspaceGraphAgentIntentResult
{
    private WorkspaceGraphAgentIntentResult()
    {
    }

    public sealed record Parsed(WorkspaceGraphAgentIntent Intent)
        : WorkspaceGraphAgentIntentResult;

    public sealed record Rejected(string StableCode, string Message)
        : WorkspaceGraphAgentIntentResult;
}
