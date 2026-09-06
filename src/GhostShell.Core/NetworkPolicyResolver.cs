namespace GhostShell.Core;

public static class NetworkPolicyResolver
{
    /// <summary>
    /// Resolves the application policy against the global connection catalog. Application
    /// availability is derived from that catalog; only workspace overrides own a subset.
    /// </summary>
    public static NetworkPolicy ResolveApplication(
        NetworkPolicy policy,
        IReadOnlyList<NetworkConnectionProfile> connections)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(connections);
        var connectionIds = connections
            .Select(connection => connection.Id)
            .ToArray();
        NetworkConnectionId? selectedConnectionId = policy.SelectedConnectionId is { } selected
            && connectionIds.Contains(selected)
                ? selected
                : connectionIds.Length == 0 ? null : connectionIds[0];
        return new NetworkPolicy(
            connectionIds,
            selectedConnectionId,
            policy.IsEnabled && selectedConnectionId is not null,
            policy.KillSwitchEnabled);
    }

    public static NetworkPolicy Resolve(
        ApplicationNetworkSettings applicationSettings,
        WorkspaceDefinition workspace,
        IReadOnlyList<NetworkConnectionProfile> connections)
    {
        ArgumentNullException.ThrowIfNull(applicationSettings);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(connections);
        return workspace.NetworkOverride
            ?? ResolveApplication(applicationSettings.Policy, connections);
    }
}
