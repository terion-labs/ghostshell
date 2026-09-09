using Asura.App.ViewModels;

namespace Asura.App.Views;

public sealed class WorkspaceEditorCancelRequestedEventArgs(
    WorkspaceEditorCancelDisposition disposition) : EventArgs
{
    public WorkspaceEditorCancelDisposition Disposition { get; } = disposition;
}
