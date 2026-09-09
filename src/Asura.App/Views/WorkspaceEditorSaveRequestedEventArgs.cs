using Asura.App.ViewModels;

namespace Asura.App.Views;

public sealed class WorkspaceEditorSaveRequestedEventArgs(
    WorkspaceEditorSaveRequest request) : EventArgs
{
    public WorkspaceEditorSaveRequest Request { get; } = request
        ?? throw new ArgumentNullException(nameof(request));
}
