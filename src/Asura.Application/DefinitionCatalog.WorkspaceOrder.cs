using Asura.Core;

namespace Asura.Application;

public sealed partial class DefinitionCatalog
{
    public async ValueTask<DefinitionStoreError?> ReorderWorkspacesAsync(
        IReadOnlyList<WorkspaceId> orderedIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        // Snapshot the caller's mutable list before the first await.
        var ids = orderedIds.ToArray();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Snapshot.Workspaces.ToDictionary(item => item.Value.Id);
            if (ids.Length != current.Count
                || ids.Distinct().Count() != ids.Length
                || ids.Any(id => !current.ContainsKey(id)))
            {
                return new(DefinitionStoreErrorCode.DependencyConflict,
                    "The workspace list changed. Try dragging the workspace again.");
            }

            var writes = new List<DefinitionGraphWrite>();
            for (var index = 0; index < ids.Length; index++)
            {
                var workspace = current[ids[index]];
                if (workspace.Value.SortOrder != index)
                {
                    writes.Add(new(workspace.Value with { SortOrder = index }, workspace.Revision));
                }
            }
            if (writes.Count == 0)
            {
                return null;
            }
            if (_layoutGraph is null)
            {
                return new(DefinitionStoreErrorCode.UnsupportedKind,
                    "This catalog cannot reorder workspaces atomically.");
            }

            // Position changes must land together; a failed write leaves both
            // durable order and the published catalog untouched.
            var error = await _layoutGraph.SaveGraphAsync(writes, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                return error;
            }
            var refreshed = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.IsSuccess)
            {
                return refreshed.Error;
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        finally
        {
            _mutationGate.Release();
        }
    }
}
