using System.Data.Common;
using System.Globalization;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

public sealed partial class SqliteDefinitionBundleStore
{
    private IDurableDefinition SanitizeExportedDatabaseTargets(IDurableDefinition definition, ref int count)
    {
        if (definition is ScreenDefinition screen)
        {
            var panels = SanitizeExportedDatabasePanels(screen.Panels, ref count);
            return ReferenceEquals(panels, screen.Panels) ? screen
                : new ScreenDefinition(screen.Id, screen.SchemaVersion, screen.Name, screen.Description,
                    screen.LayoutId, panels, screen.Tags, screen.AgentPolicyOverride);
        }
        if (definition is WorkspaceDefinition workspace)
        {
            var entries = workspace.Entries.ToArray();
            var changed = false;
            for (var index = 0; index < entries.Length; index++)
            {
                if (entries[index] is not WorkspaceEntry.Tab tab)
                {
                    continue;
                }
                var panels = SanitizeExportedDatabasePanels(tab.Panels, ref count);
                if (!ReferenceEquals(panels, tab.Panels))
                {
                    entries[index] = new WorkspaceEntry.Tab(tab.Id, tab.Name, tab.LayoutId, panels);
                    changed = true;
                }
            }
            return changed ? WithWorkspaceEntries(workspace, entries) : workspace;
        }
        return definition;
    }

    private IReadOnlyList<ScreenPanelDefinition> SanitizeExportedDatabasePanels(
        IReadOnlyList<ScreenPanelDefinition> panels, ref int count)
    {
        ScreenPanelDefinition[]? changed = null;
        for (var index = 0; index < panels.Count; index++)
        {
            var panel = panels[index];
            if (panel.Kind != ScreenPanelKind.DatabaseViewer || string.IsNullOrWhiteSpace(panel.Startup.Location)
                || panel.Startup.Location.StartsWith("saved:", StringComparison.Ordinal)
                || panel.Startup.Location.StartsWith(DatabaseRecoveryToken.Prefix, StringComparison.Ordinal)
                || string.Equals(panel.Startup.Location, DatabaseRecoveryToken.ReconnectTarget, StringComparison.Ordinal))
            {
                continue;
            }
            var target = DatabasePanelTarget.TryParse(panel.Startup.Location);
            if (target is null || _databaseConnections is null
                || !_databaseConnections.IsConnectionStringValid(target.DriverId, target.ConnectionString))
            {
                throw new NotSupportedException("The legacy database target cannot be validated for export.");
            }
            DatabaseConnectionDetails details;
            try
            {
                details = _databaseConnections.ParseConnectionDetails(target.DriverId, target.ConnectionString);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
            {
                throw new NotSupportedException("The legacy database target cannot be parsed for export.");
            }
            if (!HasExportedDatabaseSecret(target, details))
            {
                continue;
            }
            changed ??= [.. panels];
            changed[index] = panel with
            {
                ConnectionId = null,
                Startup = new(DatabaseRecoveryToken.ReconnectTarget, panel.Startup.Commands,
                    panel.Startup.DeliveryFailurePolicy),
            };
            count++;
        }
        return changed ?? panels;
    }

    private static bool HasExportedDatabaseSecret(DatabasePanelTarget target, DatabaseConnectionDetails details)
    {
        if (!string.IsNullOrEmpty(details.Password)
            || LiteralSecretValidator.ContainsLikelyLiteralSecret(target.ConnectionString)
            || (details.Options is not null && LiteralSecretValidator.ContainsLikelyLiteralSecret(details.Options)))
        {
            return true;
        }
        // Typed driver validation ran first. Inspect decoded option names as
        // well as primary Password: TLS key passwords and access tokens can be
        // preserved in Options, and URL query names can be percent-encoded.
        if (Uri.TryCreate(target.ConnectionString, UriKind.Absolute, out var uri)
            && target.ConnectionString.Contains("://", StringComparison.Ordinal))
        {
            foreach (var part in uri.Query.TrimStart('?').Split(['&', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2)
                {
                    throw new NotSupportedException("An unrecognized database URL option cannot be exported.");
                }
                if (IsConfidentialDatabaseOption(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1])))
                {
                    return true;
                }
            }
        }
        if (string.Equals(target.DriverId, "redis", StringComparison.Ordinal))
        {
            // Redis's strict ConfigurationOptions parser and Password field
            // handle its comma-delimited syntax; it has no opaque extra keys.
            return false;
        }
        if (details.Options is { Length: > 0 } options)
        {
            DbConnectionStringBuilder parsed;
            try
            {
                parsed = new() { ConnectionString = options };
            }
            catch (ArgumentException)
            {
                throw new NotSupportedException("Unrecognized database options cannot be exported.");
            }
            foreach (string key in parsed.Keys)
            {
                if (IsConfidentialDatabaseOption(key, Convert.ToString(parsed[key], CultureInfo.InvariantCulture)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsConfidentialDatabaseOption(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        var compact = string.Concat(key.Where(character => !char.IsWhiteSpace(character) && character is not ('_' or '-')));
        return compact.EndsWith("password", StringComparison.OrdinalIgnoreCase)
            || compact.EndsWith("passphrase", StringComparison.OrdinalIgnoreCase)
            || compact.EndsWith("token", StringComparison.OrdinalIgnoreCase)
            || compact.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "pwd", StringComparison.OrdinalIgnoreCase)
            || LiteralSecretValidator.ContainsLikelyLiteralSecret(key + "=export-credential-probe")
            || LiteralSecretValidator.ContainsLikelyLiteralSecret(value);
    }
}
