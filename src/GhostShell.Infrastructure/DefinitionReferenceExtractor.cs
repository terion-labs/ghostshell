using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal static class DefinitionReferenceExtractor
{
    public static IReadOnlyList<DefinitionReference> Extract(IDurableDefinition definition) =>
        definition switch
        {
            ScreenDefinition screen => ExtractScreen(screen),
            WorkspaceDefinition workspace => ExtractWorkspace(workspace),
            TerminalProfile terminal =>
            [
                new(
                    new DefinitionKey(DefinitionKind.Keymap, terminal.KeymapId.Value),
                    "terminal-keymap"),
            ],
            KeymapProfile { BasedOn: { } basedOn } =>
            [
                new(
                    new DefinitionKey(DefinitionKind.Keymap, basedOn.Value),
                    "base-keymap"),
            ],
            FileProviderProfile
            {
                Configuration: FileProviderConfiguration.Sftp sftp,
            } =>
            [
                new(
                    new DefinitionKey(DefinitionKind.Connection, sftp.ConnectionId.Value),
                    "sftp-connection"),
            ],
            DatabaseConnectionProfile { TunnelConnectionId: { } tunnelId } =>
            [
                new(
                    new DefinitionKey(DefinitionKind.Connection, tunnelId.Value),
                    "database-tunnel"),
            ],
            // A Git connection that delegates its endpoint to a saved SSH
            // connection: the edge protects the referenced connection from
            // deletion while this profile points at it.
            ConnectionProfile { HostConnectionId: { } hostId } =>
            [
                new(
                    new DefinitionKey(DefinitionKind.Connection, hostId.Value),
                    "host-connection"),
            ],
            ApplicationNetworkSettings settings => ExtractNetworkPolicy(settings.Policy),
            // Vault references are not durable definitions and deliberately do
            // not create rows in the definition dependency graph.
            McpServerProfile => [],
            _ => [],
        };

    private static IReadOnlyList<DefinitionReference> ExtractScreen(ScreenDefinition screen)
    {
        var references = new List<DefinitionReference>
        {
            new(new DefinitionKey(DefinitionKind.Layout, screen.LayoutId.Value), "layout"),
        };
        if (screen.AgentPolicyOverride is { } policy)
        {
            references.Add(new DefinitionReference(
                new DefinitionKey(
                    DefinitionKind.AiProviderProfile,
                    policy.Provider),
                "agent-policy-provider"));
        }

        references.AddRange(screen.Panels
            .Where(panel => panel.ConnectionId is not null)
            .Select(panel => new DefinitionReference(
                new DefinitionKey(DefinitionKind.Connection, panel.ConnectionId!.Value.Value),
                $"panel:{panel.Id.Value}:connection")));
        references.AddRange(screen.Panels
            .Where(panel =>
                panel.FileProviderProfileId is { } id
                && !BuiltInFileProviders.IsIntrinsic(id))
            .Select(panel => new DefinitionReference(
                new DefinitionKey(
                    DefinitionKind.FileProviderProfile,
                    panel.FileProviderProfileId!.Value.Value),
                $"panel:{panel.Id.Value}:file-provider")));
        return references;
    }

    private static IReadOnlyList<DefinitionReference> ExtractWorkspace(WorkspaceDefinition workspace)
    {
        var references = new List<DefinitionReference>();
        references.AddRange(ExtractNetworkPolicy(workspace.NetworkOverride));
        if (workspace.AgentPolicyOverride is { } policy)
        {
            references.Add(new DefinitionReference(
                new DefinitionKey(
                    DefinitionKind.AiProviderProfile,
                    policy.Provider),
                "agent-policy-provider"));
        }

        foreach (var entry in workspace.Entries)
        {
            switch (entry)
            {
                case WorkspaceEntry.ConnectionReference connection:
                    references.Add(new DefinitionReference(
                        new DefinitionKey(DefinitionKind.Connection, connection.ConnectionId.Value),
                        $"entry:{entry.Id.Value}:connection"));
                    break;
                case WorkspaceEntry.ScreenReference screen:
                    references.Add(new DefinitionReference(
                        new DefinitionKey(DefinitionKind.Screen, screen.ScreenId.Value),
                        $"entry:{entry.Id.Value}:screen"));
                    break;
                case WorkspaceEntry.Tab tab:
                    references.Add(new DefinitionReference(
                        new DefinitionKey(DefinitionKind.Layout, tab.LayoutId.Value),
                        $"entry:{entry.Id.Value}:layout"));
                    references.AddRange(tab.Panels
                        .Where(panel => panel.ConnectionId is not null)
                        .Select(panel => new DefinitionReference(
                            new DefinitionKey(
                                DefinitionKind.Connection,
                                panel.ConnectionId!.Value.Value),
                            $"entry:{entry.Id.Value}:panel:{panel.Id.Value}:connection")));
                    references.AddRange(tab.Panels
                        .Where(panel =>
                            panel.FileProviderProfileId is { } id
                            && !BuiltInFileProviders.IsIntrinsic(id))
                        .Select(panel => new DefinitionReference(
                            new DefinitionKey(
                                DefinitionKind.FileProviderProfile,
                                panel.FileProviderProfileId!.Value.Value),
                            $"entry:{entry.Id.Value}:panel:{panel.Id.Value}:file-provider")));
                    break;
            }
        }

        return references;
    }

    private static IReadOnlyList<DefinitionReference> ExtractNetworkPolicy(NetworkPolicy? policy) =>
        policy?.Connections
            .Select(connectionId => new DefinitionReference(
                new DefinitionKey(DefinitionKind.NetworkConnection, connectionId.Value),
                "network-connection"))
            .ToArray()
        ?? [];
}
