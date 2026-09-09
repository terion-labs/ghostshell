using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Coordinates saved workspace definitions across runtime reservations and cold
/// configuration saves. Each runtime owner is exact, so closing one cannot
/// release another window's occupancy for the same definition.
/// </summary>
public sealed class WorkspaceDefinitionOccupancy
{
    private readonly object _gate = new();
    private readonly Dictionary<RuntimeSourceIdentity, DefinitionKey> _runtimeSources = [];
    private readonly HashSet<DefinitionKey> _coldConfigurationEdits = [];
    private readonly HashSet<DefinitionKey> _unreconciledCatalogDefinitions = [];

    public bool IsOccupied(DefinitionKey definition)
    {
        if (definition.Kind != WorkspaceDefinition.Kind)
        {
            return false;
        }

        lock (_gate)
        {
            return _runtimeSources.ContainsValue(definition);
        }
    }

    public bool TryRegisterRuntime(
        WindowInstanceId windowId,
        WorkspaceInstanceId runtimeWorkspaceId,
        DefinitionKey sourceDefinition)
    {
        if (sourceDefinition.Kind != WorkspaceDefinition.Kind)
        {
            return true;
        }

        lock (_gate)
        {
            var identity = new RuntimeSourceIdentity(windowId, runtimeWorkspaceId);
            if (_runtimeSources.TryGetValue(identity, out var registered))
            {
                return registered == sourceDefinition;
            }

            var sameWindowAlreadyOwnsDefinition = _runtimeSources.Any(pair =>
                pair.Key.WindowId == windowId
                && pair.Value == sourceDefinition);
            if (sameWindowAlreadyOwnsDefinition
                || _coldConfigurationEdits.Contains(sourceDefinition)
                || _unreconciledCatalogDefinitions.Contains(sourceDefinition))
            {
                return false;
            }

            _runtimeSources.Add(identity, sourceDefinition);
            return true;
        }
    }

    /// <summary>
    /// Reserves a definition while its isolation or mount configuration is
    /// being saved. The caller must keep the returned lease until persistence
    /// completes. A null result means a runtime or another cold edit owns it.
    /// </summary>
    public IDisposable? TryReserveColdConfigurationEdit(DefinitionKey definition)
    {
        if (definition.Kind != WorkspaceDefinition.Kind)
        {
            return null;
        }

        return TryReserveColdConfigurationEdits([definition]);
    }

    /// <summary>
    /// Atomically reserves several workspace definitions for one durable mutation. The caller
    /// must keep the returned lease until both persistence and any catalog reload complete. A
    /// null result means at least one runtime or another cold edit owns an affected definition.
    /// </summary>
    public IDisposable? TryReserveColdConfigurationEdits(
        IReadOnlyCollection<DefinitionKey> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count == 0)
        {
            throw new ArgumentException(
                "At least one workspace definition is required.",
                nameof(definitions));
        }

        var uniqueDefinitions = definitions.ToHashSet();
        if (uniqueDefinitions.Any(definition => definition.Kind != WorkspaceDefinition.Kind))
        {
            throw new ArgumentException(
                "Only workspace definitions can be reserved.",
                nameof(definitions));
        }

        lock (_gate)
        {
            if (_runtimeSources.Values.Any(uniqueDefinitions.Contains)
                || _coldConfigurationEdits.Overlaps(uniqueDefinitions)
                || _unreconciledCatalogDefinitions.Overlaps(uniqueDefinitions))
            {
                return null;
            }

            _coldConfigurationEdits.UnionWith(uniqueDefinitions);
            return new ColdConfigurationEditLease(this, [.. uniqueDefinitions]);
        }
    }

    /// <summary>
    /// Keeps definitions unavailable after their durable records changed but the in-memory
    /// catalog did not reload. The definitions must already belong to the caller's cold lease,
    /// which prevents a gap while that lease transfers ownership back to this coordinator.
    /// </summary>
    public void RetainWorkspaceDefinitionsUntilCatalogReconciled(
        IReadOnlyCollection<DefinitionKey> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var uniqueDefinitions = definitions.ToHashSet();
        if (uniqueDefinitions.Count == 0
            || uniqueDefinitions.Any(definition => definition.Kind != WorkspaceDefinition.Kind))
        {
            throw new ArgumentException(
                "At least one workspace definition is required.",
                nameof(definitions));
        }

        lock (_gate)
        {
            if (!uniqueDefinitions.All(_coldConfigurationEdits.Contains))
            {
                throw new InvalidOperationException(
                    "Workspace definitions can be retained only while cold-reserved.");
            }

            _unreconciledCatalogDefinitions.UnionWith(uniqueDefinitions);
        }
    }

    /// <summary>
    /// Releases fail-closed definitions after one catalog reload has observed the current
    /// durable store. Active cold-edit leases remain reserved independently.
    /// </summary>
    public void MarkCatalogReconciled()
    {
        lock (_gate)
        {
            _unreconciledCatalogDefinitions.Clear();
        }
    }

    public void Unregister(
        WindowInstanceId windowId,
        WorkspaceInstanceId runtimeWorkspaceId)
    {
        lock (_gate)
        {
            _runtimeSources.Remove(new RuntimeSourceIdentity(windowId, runtimeWorkspaceId));
        }
    }

    public void UnregisterWindow(WindowInstanceId windowId)
    {
        lock (_gate)
        {
            foreach (var identity in _runtimeSources.Keys
                .Where(candidate => candidate.WindowId == windowId)
                .ToArray())
            {
                _runtimeSources.Remove(identity);
            }
        }
    }

    private void ReleaseColdConfigurationEdits(IReadOnlyList<DefinitionKey> definitions)
    {
        lock (_gate)
        {
            foreach (var definition in definitions)
            {
                _coldConfigurationEdits.Remove(definition);
            }
        }
    }

    private sealed class ColdConfigurationEditLease(
        WorkspaceDefinitionOccupancy owner,
        IReadOnlyList<DefinitionKey> definitions) : IDisposable
    {
        private WorkspaceDefinitionOccupancy? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?
                .ReleaseColdConfigurationEdits(definitions);
    }

    private readonly record struct RuntimeSourceIdentity(
        WindowInstanceId WindowId,
        WorkspaceInstanceId RuntimeWorkspaceId);
}
