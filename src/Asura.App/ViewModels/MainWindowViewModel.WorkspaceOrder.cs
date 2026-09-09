using Asura.Core;

namespace Asura.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task MoveWorkspaceAsync(
        WorkspaceId workspaceId,
        WorkspaceId anchorId,
        bool after,
        CancellationToken cancellationToken)
    {
        if (workspaceId == anchorId)
        {
            return;
        }
        var ids = Workspaces.Select(item => item.Id).ToList();
        if (!ids.Contains(anchorId) || !ids.Remove(workspaceId))
        {
            return;
        }
        ids.Insert(ids.IndexOf(anchorId) + (after ? 1 : 0), workspaceId);
        if (ids.SequenceEqual(Workspaces.Select(item => item.Id)))
        {
            return;
        }
        var error = await _catalog.ReorderWorkspacesAsync(ids, cancellationToken);
        if (error is not null)
        {
            SetError(error.Message);
            return;
        }
        RefreshCatalog(_catalog.Snapshot);
    }
}
