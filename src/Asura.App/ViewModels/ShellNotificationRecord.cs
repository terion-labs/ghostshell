using Asura.Application;

namespace Asura.App.ViewModels;

/// <summary>
/// One routed notification retained by the shell. Runtime identifiers keep
/// the record valid without retaining panels, tabs, or workspaces after close.
/// </summary>
internal sealed record ShellNotificationRecord(
    string Id,
    NativeNotificationRoute Route,
    PanelNotificationEvent Notification,
    ShellNotificationVisibility Visibility,
    bool IsRead);

internal enum ShellNotificationVisibility
{
    Workspace,
    WorkspaceSource,
    Panel,
}
