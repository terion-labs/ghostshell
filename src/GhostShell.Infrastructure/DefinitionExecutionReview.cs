using System.Globalization;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>Describes executable and connection authority before an import becomes trusted local state.</summary>
internal static class DefinitionExecutionReview
{
    public static IReadOnlyList<DefinitionExecutionReviewItem> Build(
        IEnumerable<object> imported,
        IReadOnlyDictionary<DefinitionKey, object> available)
    {
        var result = new List<DefinitionExecutionReviewItem>();
        var visited = new HashSet<DefinitionKey>();
        foreach (var definition in imported)
        {
            Visit(definition);
        }
        return result.AsReadOnly();

        void Reference(DefinitionKey key)
        {
            if (available.TryGetValue(key, out var definition))
            {
                Visit(definition);
            }
        }

        void Visit(object definition)
        {
            if (definition is not IDurableDefinition durable || !visited.Add(durable.Key))
            {
                return;
            }

            switch (definition)
            {
                case ConnectionProfile connection:
                    {
                        var details = new StringBuilder();
                        details.AppendLine(CultureInfo.InvariantCulture, $"Connection: {connection.Id.Value}");
                        details.AppendLine(CultureInfo.InvariantCulture, $"Endpoint: {DescribeEndpoint(connection.Endpoint)}; authentication: {DescribeAuthentication(connection.Authentication)}");
                        if (connection.Startup.Command is { } command)
                        {
                            details.AppendLine(CultureInfo.InvariantCulture, $"Automatically runs: {command}");
                        }
                        foreach (var variable in connection.Startup.Environment)
                        {
                            details.AppendLine(CultureInfo.InvariantCulture, $"Environment: {variable.Name}={variable.Value}");
                        }
                        result.Add(new($"Connection authority and startup — {connection.Name}", details.ToString()));
                        if (connection.HostConnectionId is { } host)
                        {
                            Reference(new(ConnectionProfile.Kind, host.Value));
                        }
                    }
                    break;
                case ScreenDefinition screen:
                    Panels(screen.Name, screen.Panels);
                    break;
                case WorkspaceDefinition workspace:
                    if (workspace.IsIsolated)
                    {
                        var details = new StringBuilder();
                        details.AppendLine(CultureInfo.InvariantCulture, $"Guest image: {workspace.IsolationImageReference ?? WorkspaceIsolationImages.Default}");
                        details.AppendLine("Opening this workspace boots code from that image.");
                        foreach (var mount in workspace.IsolationMounts)
                        {
                            details.AppendLine(CultureInfo.InvariantCulture, $"{(mount.IsReadOnly ? "Read-only" : "WRITABLE")} host mount: {mount.HostPath} → {mount.GuestPath}");
                        }
                        result.Add(new($"Isolated workspace — {workspace.Name}", details.ToString()));
                    }
                    foreach (var entry in workspace.Entries)
                    {
                        switch (entry)
                        {
                            case WorkspaceEntry.Tab tab:
                                Panels($"{workspace.Name} / {tab.Name}", tab.Panels);
                                break;
                            case WorkspaceEntry.ConnectionReference connection:
                                Reference(new(ConnectionProfile.Kind, connection.ConnectionId.Value));
                                break;
                            case WorkspaceEntry.ScreenReference screen:
                                Reference(new(ScreenDefinition.Kind, screen.ScreenId.Value));
                                break;
                        }
                    }
                    break;
                case DatabaseConnectionProfile database:
                    var databaseDetails = new StringBuilder();
                    databaseDetails.AppendLine(CultureInfo.InvariantCulture, $"Driver: {database.DriverId}");
                    databaseDetails.AppendLine(CultureInfo.InvariantCulture, $"Target/options: {database.ConnectionString}");
                    databaseDetails.AppendLine("Opening a database panel can connect automatically. Integrated authentication uses this computer's account authority when configured.");
                    if (database.PasswordSecret is { } secret)
                    {
                        databaseDetails.AppendLine(CultureInfo.InvariantCulture, $"Stored credential reference: {secret.Value}");
                    }
                    if (database.TunnelConnectionId is { } tunnel)
                    {
                        databaseDetails.AppendLine(CultureInfo.InvariantCulture, $"Uses saved SSH route: {tunnel.Value}");
                        if (available.GetValueOrDefault(new(ConnectionProfile.Kind, tunnel.Value)) is ConnectionProfile route)
                        {
                            databaseDetails.AppendLine(CultureInfo.InvariantCulture, $"SSH endpoint: {DescribeEndpoint(route.Endpoint)}; authentication: {DescribeAuthentication(route.Authentication)}");
                        }
                    }
                    if (database.InlineTunnel is { } inline)
                    {
                        databaseDetails.AppendLine(CultureInfo.InvariantCulture, $"Inline SSH endpoint: {DescribeEndpoint(inline.Endpoint)}; authentication: {DescribeAuthentication(inline.Authentication)}");
                    }
                    result.Add(new($"Database connection — {database.Name}", databaseDetails.ToString()));
                    break;
            }
        }

        void Panels(string owner, IReadOnlyList<ScreenPanelDefinition> panels)
        {
            foreach (var panel in panels)
            {
                if (panel.Startup.Commands.Count > 0)
                {
                    result.Add(new($"Panel startup — {owner} / {panel.Title}",
                        $"Panel: {panel.Id.Value}; connection: {panel.ConnectionId?.Value ?? "local"}\nAutomatically runs:\n{string.Join('\n', panel.Startup.Commands)}"));
                }
                if (panel.ConnectionId is { } connection)
                {
                    Reference(new(ConnectionProfile.Kind, connection.Value));
                }
                if (panel.Kind == ScreenPanelKind.DatabaseViewer && panel.Startup.Location is { } target)
                {
                    if (target.StartsWith("saved:", StringComparison.Ordinal))
                    {
                        Reference(new(DatabaseConnectionProfile.Kind, target["saved:".Length..]));
                    }
                    else if (string.Equals(target, DatabaseRecoveryToken.ReconnectTarget, StringComparison.Ordinal))
                    {
                        result.Add(new($"Database reconnect required — {owner} / {panel.Title}",
                            "The device-local confidential target is not imported. Choose this database connection again after opening the workspace. No stored database or SSH credential is accessed by this imported panel."));
                    }
                    else
                    {
                        result.Add(new($"Database auto-connect — {owner} / {panel.Title}",
                            $"Target/options: {target}\nOpening this panel connects automatically; integrated authentication may use this computer's account authority."));
                    }
                }
            }
        }
    }

    private static string DescribeAuthentication(ConnectionAuthentication authentication) => authentication switch
    {
        ConnectionAuthentication.None => "no stored credential",
        ConnectionAuthentication.SshAgent => "host SSH agent (may authenticate with loaded keys)",
        ConnectionAuthentication.Password password => $"stored password reference {password.PasswordSecret.Value}",
        ConnectionAuthentication.PrivateKey key => $"stored private-key reference {key.PrivateKeySecret.Value}",
        _ => "unknown authentication",
    };

    private static string DescribeEndpoint(ConnectionEndpoint endpoint) => endpoint switch
    {
        ConnectionEndpoint.Local local => $"Local shell {local.ShellPath ?? "(system default)"}",
        ConnectionEndpoint.Ssh ssh => $"SSH {ssh.Username}@{ssh.Host}:{ssh.Port}",
        ConnectionEndpoint.Docker docker => $"Docker {docker.Context}/{docker.Container}",
        ConnectionEndpoint.Wsl wsl => $"WSL {wsl.Distribution}/{wsl.Username}",
        _ => endpoint.Kind.ToString(),
    };
}
