using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IWorkspaceNetworkRuntime? _workspaceNetworkRuntime;
    private readonly Dictionary<WorkspaceInstanceId, WorkspaceNetworkControlViewModel>
        _workspaceNetworkControls = [];
    private readonly object _workspaceNetworkCleanupGate = new();
    private readonly HashSet<Task> _workspaceNetworkCleanupTasks = [];
    private readonly WorkspaceNetworkControlViewModel _inactiveWorkspaceNetwork;

    public WorkspaceNetworkControlViewModel WorkspaceNetwork =>
        RuntimeWorkspace is { } workspace
        && _workspaceNetworkControls.TryGetValue(workspace.Id, out var control)
            ? control
            : _inactiveWorkspaceNetwork;

    public bool IsWorkspaceNetworkControlVisible => RuntimeWorkspace is not null;

    private async Task PrepareWorkspaceNetworkAsync(
        RuntimeWorkspaceViewModel workspace,
        CancellationToken cancellationToken)
    {
        if (_workspaceNetworkControls.ContainsKey(workspace.Id))
        {
            return;
        }

        var snapshot = _catalog.Snapshot;
        var policy = EffectiveNetworkPolicy(workspace, snapshot);
        var update = new WorkspaceNetworkPolicyUpdate(
            policy,
            NetworkProfilesFor(policy, snapshot));
        IWorkspaceNetworkSession? session = null;
        if (_workspaceNetworkRuntime is not null)
        {
            session = await _workspaceNetworkRuntime.OpenAsync(
                    new WorkspaceNetworkOpenRequest(
                        workspace.Id,
                        update,
                        workspace.IsolationBinding is { } binding
                            ? WorkspaceNetworkPlacement.Isolated(binding)
                            : WorkspaceNetworkPlacement.Host),
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var control = new WorkspaceNetworkControlViewModel(
            update,
            session,
            _uiThreadDispatcher,
            snapshot => WorkspaceRuntimeServicesFor(workspace.Id)?
                .ApplyNetworkEgress(snapshot.Egress));
        if (!_workspaceNetworkControls.TryAdd(workspace.Id, control))
        {
            await control.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (ReferenceEquals(RuntimeWorkspace, workspace))
        {
            await _uiThreadDispatcher.InvokeAsync(
                    () =>
                    {
                        OnPropertyChanged(nameof(WorkspaceNetwork));
                        OnPropertyChanged(nameof(IsWorkspaceNetworkControlVisible));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private NetworkPolicy EffectiveNetworkPolicy(
        RuntimeWorkspaceViewModel workspace,
        DefinitionCatalogSnapshot snapshot)
    {
        var applicationSettings = snapshot.ApplicationNetworkSettings
            .Select(stored => stored.Value)
            .SingleOrDefault()
            ?? ApplicationNetworkSettings.Default;
        var connections = snapshot.NetworkConnections
            .Select(stored => stored.Value)
            .ToArray();
        if (!_runtimeSources.TryGetValue(workspace.Id, out var source)
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind)
        {
            return NetworkPolicyResolver.ResolveApplication(
                applicationSettings.Policy,
                connections);
        }

        var definition = snapshot.Workspaces
            .FirstOrDefault(stored => stored.Value.Key == source.SourceDefinition)
            ?.Value;
        return definition is null
            ? NetworkPolicyResolver.ResolveApplication(applicationSettings.Policy, connections)
            : NetworkPolicyResolver.Resolve(applicationSettings, definition, connections);
    }

    private static IReadOnlyList<NetworkConnectionProfile> NetworkProfilesFor(
        NetworkPolicy policy,
        DefinitionCatalogSnapshot snapshot)
    {
        var profiles = snapshot.NetworkConnections
            .Select(stored => stored.Value)
            .ToDictionary(profile => profile.Id);
        return [.. policy.Connections.Select(id => profiles[id])];
    }

    private void RefreshOpenWorkspaceNetworkPolicies(DefinitionCatalogSnapshot snapshot)
    {
        foreach (var (workspaceId, control) in _workspaceNetworkControls.ToArray())
        {
            var workspace = _openWorkspaces
                .Append(RuntimeWorkspace)
                .OfType<RuntimeWorkspaceViewModel>()
                .FirstOrDefault(candidate => candidate.Id == workspaceId);
            if (workspace is null)
            {
                continue;
            }

            var policy = EffectiveNetworkPolicy(workspace, snapshot);
            var update = new WorkspaceNetworkPolicyUpdate(
                policy,
                NetworkProfilesFor(policy, snapshot));
            _ = ApplyWorkspaceNetworkPolicySafelyAsync(control, update);
        }
    }

    private async Task ApplyWorkspaceNetworkPolicySafelyAsync(
        WorkspaceNetworkControlViewModel control,
        WorkspaceNetworkPolicyUpdate update)
    {
        try
        {
            await control.UpdatePolicyAsync(update, _runtimeGraphLifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_shutdownStarted)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-network.catalog-refresh.failed",
                exception);
        }
    }

    private void ScheduleWorkspaceNetworkCleanup(WorkspaceInstanceId workspaceId)
    {
        if (!_workspaceNetworkControls.Remove(workspaceId, out var control))
        {
            return;
        }

        var cleanup = DisposeWorkspaceNetworkAsync(control);
        lock (_workspaceNetworkCleanupGate)
        {
            _workspaceNetworkCleanupTasks.Add(cleanup);
        }

        _ = RemoveCompletedWorkspaceNetworkCleanupAsync(cleanup);
    }

    private static async Task DisposeWorkspaceNetworkAsync(
        WorkspaceNetworkControlViewModel control)
    {
        try
        {
            await control.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-network.dispose.failed",
                exception);
        }
    }

    private async Task RemoveCompletedWorkspaceNetworkCleanupAsync(Task cleanup)
    {
        await cleanup.ConfigureAwait(false);
        lock (_workspaceNetworkCleanupGate)
        {
            _workspaceNetworkCleanupTasks.Remove(cleanup);
        }
    }

    private async Task FinalizeWorkspaceNetworkShutdownAsync()
    {
        foreach (var workspaceId in _workspaceNetworkControls.Keys.ToArray())
        {
            ScheduleWorkspaceNetworkCleanup(workspaceId);
        }

        while (true)
        {
            Task[] pending;
            lock (_workspaceNetworkCleanupGate)
            {
                pending = [.. _workspaceNetworkCleanupTasks];
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private ValueTask DisposeInactiveWorkspaceNetworkAsync() =>
        _inactiveWorkspaceNetwork.DisposeAsync();
}
