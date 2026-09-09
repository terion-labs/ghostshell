namespace Asura.Core;

public enum WorkspaceTabKind
{
    Terminal,
    Screen,
}

public sealed record WorkspaceTab(
    TabId Id,
    string Title,
    WorkspaceTabKind Kind,
    IReadOnlyList<TerminalPanel> Panels,
    bool IsActive);
