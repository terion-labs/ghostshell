using System.Collections.ObjectModel;

namespace GhostShell.Core;

/// <summary>
/// A complete network choice. Workspaces either inherit the application policy or store
/// one complete replacement, so effective behavior never depends on merging partial fields.
/// </summary>
public sealed record NetworkPolicy
{
    public static NetworkPolicy Direct { get; } = new([], null, false, false);

    public NetworkPolicy(
        IReadOnlyList<NetworkConnectionId>? connections,
        NetworkConnectionId? selectedConnectionId,
        bool isEnabled,
        bool killSwitchEnabled)
    {
        Connections = Snapshot(connections);
        if (selectedConnectionId is { } selected)
        {
            RuntimeId.Require(selected.Value, nameof(selectedConnectionId));
            if (!Connections.Contains(selected))
            {
                throw new ArgumentException(
                    "The selected network connection must belong to the policy.",
                    nameof(selectedConnectionId));
            }
        }

        if (isEnabled && selectedConnectionId is null)
        {
            throw new ArgumentException(
                "An enabled network policy requires a selected connection.",
                nameof(selectedConnectionId));
        }

        SelectedConnectionId = selectedConnectionId;
        IsEnabled = isEnabled;
        KillSwitchEnabled = killSwitchEnabled;
    }

    public IReadOnlyList<NetworkConnectionId> Connections { get; }

    /// <summary>The remembered choice, including while networking is manually disabled.</summary>
    public NetworkConnectionId? SelectedConnectionId { get; }

    /// <summary>False uses direct networking without discarding the remembered choice.</summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// When enabled networking cannot establish its selected route, block traffic instead
    /// of falling back to the direct host or isolate route.
    /// </summary>
    public bool KillSwitchEnabled { get; }

    private static IReadOnlyList<NetworkConnectionId> Snapshot(
        IReadOnlyList<NetworkConnectionId>? connections)
    {
        if (connections is null || connections.Count == 0)
        {
            return Array.AsReadOnly(Array.Empty<NetworkConnectionId>());
        }

        var snapshot = connections.ToArray();
        foreach (var connection in snapshot)
        {
            RuntimeId.Require(connection.Value, nameof(connections));
        }

        if (snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "A network policy cannot contain duplicate connections.",
                nameof(connections));
        }

        return new ReadOnlyCollection<NetworkConnectionId>(snapshot);
    }
}
