namespace Asura.App.ViewModels;

public enum AgentRunScopeKind
{
    ActivePanel,
    CurrentTab,
    Workspace,
    SelectedPanels,
}

public sealed record AgentRunScopeOption(
    AgentRunScopeKind Kind,
    string Label);
