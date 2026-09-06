using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Owns the closed set of definition payloads this binary can interpret. Future kinds and
/// schemas are rejected instead of being loaded with fields silently discarded.
/// </summary>
internal static class KnownDefinitionRegistry
{
    private static CommandRegistry EmptyCommandRegistry { get; } = new([]);

    public static bool SupportsRepositoryType<TDefinition>()
        where TDefinition : IDurableDefinition =>
        IsExpectedRuntimeType(typeof(TDefinition), TDefinition.Kind);

    public static bool IsKnownKind(DefinitionKind kind) =>
        kind == DefinitionKind.Connection
        || kind == DefinitionKind.Layout
        || kind == DefinitionKind.Screen
        || kind == DefinitionKind.Workspace
        || kind == DefinitionKind.Theme
        || kind == DefinitionKind.TerminalProfile
        || kind == DefinitionKind.Keymap
        || kind == DefinitionKind.FileProviderProfile
        || kind == DefinitionKind.AiProviderProfile
        || kind == DefinitionKind.McpServerProfile
        || kind == DefinitionKind.BrowserProfile
        || kind == DefinitionKind.DatabaseConnection
        || kind == DefinitionKind.QuickTerminalSettings
        || kind == DefinitionKind.NetworkConnection
        || kind == DefinitionKind.ApplicationNetworkSettings;

    public static bool TryParse(
        PortableDefinitionDocument document,
        out IDurableDefinition? definition,
        out DefinitionProblem? problem)
    {
        definition = null;
        problem = null;

        DefinitionKey key;
        try
        {
            key = new DefinitionKey(document.Kind, document.Id);
        }
        catch (ArgumentException)
        {
            problem = new(
                DefinitionProblemKind.InvalidDefinition,
                "The definition identity is invalid.");
            return false;
        }

        if (!IsKnownKind(document.Kind))
        {
            problem = new(
                DefinitionProblemKind.UnsupportedKind,
                $"Definition kind '{document.Kind}' is not supported by this application version.",
                key);
            return false;
        }

        var currentSchemaVersion = GetCurrentSchemaVersion(document.Kind);
        if (document.SchemaVersion != currentSchemaVersion)
        {
            problem = new(
                DefinitionProblemKind.UnsupportedSchema,
                $"Definition schema {document.SchemaVersion} is not supported for kind '{document.Kind}'.",
                key);
            return false;
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            problem = new(
                DefinitionProblemKind.InvalidDefinition,
                "The definition name is required.",
                key);
            return false;
        }

        if (document.PayloadJson is null)
        {
            problem = new(
                DefinitionProblemKind.InvalidDefinition,
                "The definition payload is required.",
                key);
            return false;
        }

        if (!PortablePayloadSafety.TryValidate(document.PayloadJson, out var safetyError))
        {
            problem = new(
                DefinitionProblemKind.UnsafePayload,
                safetyError!,
                key);
            return false;
        }

        if (!TryReadPayloadSchemaVersion(document.PayloadJson, out var payloadSchemaVersion)
            || payloadSchemaVersion != document.SchemaVersion)
        {
            problem = new(
                DefinitionProblemKind.InvalidDefinition,
                "The definition payload schema does not match its envelope.",
                key);
            return false;
        }

        try
        {
            definition = DefinitionJson.Deserialize(document.Kind, document.PayloadJson);
            if (definition is null || !IsExpectedRuntimeType(definition.GetType(), document.Kind))
            {
                problem = new(
                    DefinitionProblemKind.InvalidDefinition,
                    "The definition payload does not match its declared kind.",
                    key);
                definition = null;
                return false;
            }

            if (definition.Key != key
                || definition.SchemaVersion != document.SchemaVersion
                || !string.Equals(definition.Name, document.Name, StringComparison.Ordinal))
            {
                problem = new(
                    DefinitionProblemKind.InvalidDefinition,
                    "The definition payload identity, schema, or name does not match its envelope.",
                    key);
                definition = null;
                return false;
            }

            problem = ValidateSelf(definition);
            if (problem is not null)
            {
                definition = null;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (IsPayloadException(exception))
        {
            definition = null;
            problem = new(
                DefinitionProblemKind.InvalidDefinition,
                "The definition payload cannot be read by this application version.",
                key);
            return false;
        }
    }

    public static DefinitionProblem? ValidateForSave(IDurableDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        DefinitionKey key;
        try
        {
            key = definition.Key;
        }
        catch (ArgumentException)
        {
            return new(
                DefinitionProblemKind.InvalidDefinition,
                "The definition identity is invalid.");
        }

        if (!IsKnownKind(key.Kind)
            || !IsExpectedRuntimeType(definition.GetType(), key.Kind))
        {
            return new(
                DefinitionProblemKind.UnsupportedKind,
                "This repository type is not a supported durable definition.",
                key);
        }

        if (definition.SchemaVersion != GetCurrentSchemaVersion(key.Kind))
        {
            return new(
                DefinitionProblemKind.UnsupportedSchema,
                "The definition schema is not supported by this application version.",
                key);
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            return new(
                DefinitionProblemKind.InvalidDefinition,
                "The definition name is required.",
                key);
        }

        return ValidateSelf(definition);
    }

    private static DefinitionProblem? ValidateSelf(IDurableDefinition definition)
    {
        var key = definition.Key;
        try
        {
            var validation = definition switch
            {
                LayoutDefinition layout => LayoutValidator.Validate(layout),
                WorkspaceDefinition workspace => WorkspaceValidator.Validate(workspace),
                _ => DefinitionValidationResult.Valid,
            };
            if (!validation.IsValid)
            {
                return new(
                    DefinitionProblemKind.InvalidDefinition,
                    string.Join(" ", validation.Issues.Select(issue => issue.Message)),
                    key);
            }

            if (definition is ThemePreference theme
                && (!Enum.IsDefined(theme.Appearance)
                    || !Enum.IsDefined(theme.PlatformProfile)
                    || !Enum.IsDefined(theme.Accent.Kind)))
            {
                return InvalidEnum(key);
            }

            if (definition is TerminalProfile terminal
                && !Enum.IsDefined(terminal.CursorStyle))
            {
                return InvalidEnum(key);
            }

            if (definition is KeymapProfile keymap
                && (!Enum.IsDefined(keymap.Layer)
                    || keymap.Prefix is { } prefix
                    && !Enum.IsDefined(prefix.FailedSequenceBehavior)))
            {
                return InvalidEnum(key);
            }

            if (definition is QuickTerminalSettings quickTerminal
                && !Enum.IsDefined(quickTerminal.MonitorPolicy))
            {
                return InvalidEnum(key);
            }

            if (definition is AiProviderProfile aiProvider
                && !Enum.IsDefined(aiProvider.ProviderKind))
            {
                return InvalidEnum(key);
            }

            if (definition is BrowserProfileDefinition browserProfile
                && (!Enum.IsDefined(browserProfile.Persistence)
                    || !Enum.IsDefined(browserProfile.Privacy.WebContent)
                    || !Enum.IsDefined(browserProfile.Privacy.Permissions)
                    || !Enum.IsDefined(browserProfile.Privacy.History)
                    || !Enum.IsDefined(browserProfile.Privacy.Downloads)
                    || browserProfile.Authentication is { } authentication
                    && !Enum.IsDefined(authentication.Scheme)))
            {
                return InvalidEnum(key);
            }

            if (definition is BrowserProfileDefinition builtInBrowser
                && builtInBrowser.Id == BuiltInBrowserProfiles.Default.Id
                && (!builtInBrowser.IsEnabled
                    || builtInBrowser.Persistence
                        != BrowserProfilePersistence.DurableMetadata
                    || builtInBrowser.Authentication is not null))
            {
                return Invalid(
                    key,
                    "The built-in default browser profile has an invalid policy.");
            }

            if (definition is ApplicationNetworkSettings applicationNetworkSettings
                && applicationNetworkSettings.Id != ApplicationNetworkSettings.DefaultId)
            {
                return Invalid(
                    key,
                    "Application network settings must use the built-in identity.");
            }

            if (definition is ScreenDefinition screen
                && (string.IsNullOrWhiteSpace(screen.LayoutId.Value)
                    || screen.Panels.Any(panel =>
                        panel is null
                        || panel.ConnectionId is { } connectionId
                        && string.IsNullOrWhiteSpace(connectionId.Value)
                        || panel.FileProviderProfileId is { } fileProviderProfileId
                        && string.IsNullOrWhiteSpace(fileProviderProfileId.Value))))
            {
                return Invalid(
                    key,
                    "The screen contains an invalid layout, panel, or connection reference.");
            }

            if (definition is TerminalProfile terminalDefinition
                && string.IsNullOrWhiteSpace(terminalDefinition.KeymapId.Value))
            {
                return Invalid(key, "The terminal profile requires a valid keymap reference.");
            }

            if (definition is KeymapProfile keymapDefinition)
            {
                if (keymapDefinition.Bindings.Any(binding => binding is null)
                    || keymapDefinition.BasedOn is { } basedOn
                    && string.IsNullOrWhiteSpace(basedOn.Value)
                    || keymapDefinition.Bindings.Any(binding =>
                        string.IsNullOrWhiteSpace(binding.CommandId.Value)
                        || binding.Sequence.Strokes.Any(stroke =>
                            string.IsNullOrWhiteSpace(stroke.Key)))
                    || keymapDefinition.Prefix is { } prefixConfiguration
                    && string.IsNullOrWhiteSpace(prefixConfiguration.Stroke.Key))
                {
                    return Invalid(
                        key,
                        "The keymap contains an invalid binding or base-keymap reference.");
                }

                var conflict = KeymapConflictValidator
                    .Validate(keymapDefinition, EmptyCommandRegistry)
                    .FirstOrDefault(issue => issue.Severity == KeymapIssueSeverity.Error);
                if (conflict is not null)
                {
                    return Invalid(key, conflict.Message);
                }
            }

            var agentPolicy = definition switch
            {
                ScreenDefinition screenDefinition => screenDefinition.AgentPolicyOverride,
                WorkspaceDefinition workspaceDefinition => workspaceDefinition.AgentPolicyOverride,
                _ => null,
            };
            var policyProblem = ValidateAgentPolicy(key, agentPolicy);
            if (policyProblem is not null)
            {
                return policyProblem;
            }

            return null;
        }
        catch (Exception exception) when (IsPayloadException(exception))
        {
            return new(
                DefinitionProblemKind.InvalidDefinition,
                "The definition violates its domain invariants.",
                key);
        }
    }

    private static int GetCurrentSchemaVersion(DefinitionKind kind) =>
        kind switch
        {
            var value when value == DefinitionKind.Connection => ConnectionProfile.CurrentSchemaVersion,
            var value when value == DefinitionKind.Layout => LayoutDefinition.CurrentSchemaVersion,
            var value when value == DefinitionKind.Screen => ScreenDefinition.CurrentSchemaVersion,
            var value when value == DefinitionKind.Workspace => WorkspaceDefinition.CurrentSchemaVersion,
            var value when value == DefinitionKind.Theme => ThemePreference.CurrentSchemaVersion,
            var value when value == DefinitionKind.TerminalProfile => TerminalProfile.CurrentSchemaVersion,
            var value when value == DefinitionKind.Keymap => KeymapProfile.CurrentSchemaVersion,
            var value when value == DefinitionKind.FileProviderProfile =>
                FileProviderProfile.CurrentSchemaVersion,
            var value when value == DefinitionKind.AiProviderProfile =>
                AiProviderProfile.CurrentSchemaVersion,
            var value when value == DefinitionKind.McpServerProfile =>
                McpServerProfile.CurrentSchemaVersion,
            var value when value == DefinitionKind.BrowserProfile =>
                BrowserProfileDefinition.CurrentSchemaVersion,
            var value when value == DefinitionKind.DatabaseConnection =>
                DatabaseConnectionProfile.CurrentSchemaVersion,
            var value when value == DefinitionKind.QuickTerminalSettings =>
                QuickTerminalSettings.CurrentSchemaVersion,
            var value when value == DefinitionKind.NetworkConnection =>
                NetworkConnectionProfile.CurrentSchemaVersion,
            var value when value == DefinitionKind.ApplicationNetworkSettings =>
                ApplicationNetworkSettings.CurrentSchemaVersion,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static bool IsExpectedRuntimeType(Type type, DefinitionKind kind) =>
        kind switch
        {
            var value when value == DefinitionKind.Connection => type == typeof(ConnectionProfile),
            var value when value == DefinitionKind.Layout => type == typeof(LayoutDefinition),
            var value when value == DefinitionKind.Screen => type == typeof(ScreenDefinition),
            var value when value == DefinitionKind.Workspace => type == typeof(WorkspaceDefinition),
            var value when value == DefinitionKind.Theme => type == typeof(ThemePreference),
            var value when value == DefinitionKind.TerminalProfile => type == typeof(TerminalProfile),
            var value when value == DefinitionKind.Keymap => type == typeof(KeymapProfile),
            var value when value == DefinitionKind.FileProviderProfile =>
                type == typeof(FileProviderProfile),
            var value when value == DefinitionKind.AiProviderProfile =>
                type == typeof(AiProviderProfile),
            var value when value == DefinitionKind.McpServerProfile =>
                type == typeof(McpServerProfile),
            var value when value == DefinitionKind.BrowserProfile =>
                type == typeof(BrowserProfileDefinition),
            var value when value == DefinitionKind.DatabaseConnection =>
                type == typeof(DatabaseConnectionProfile),
            var value when value == DefinitionKind.QuickTerminalSettings =>
                type == typeof(QuickTerminalSettings),
            var value when value == DefinitionKind.NetworkConnection =>
                type == typeof(NetworkConnectionProfile),
            var value when value == DefinitionKind.ApplicationNetworkSettings =>
                type == typeof(ApplicationNetworkSettings),
            _ => false,
        };

    private static DefinitionProblem InvalidEnum(DefinitionKey key) =>
        Invalid(key, "The definition contains an unsupported enum value.");

    private static DefinitionProblem Invalid(DefinitionKey key, string message) =>
        new(DefinitionProblemKind.InvalidDefinition, message, key);

    private static DefinitionProblem? ValidateAgentPolicy(
        DefinitionKey key,
        AgentPolicy? policy)
    {
        if (policy is null)
        {
            return null;
        }

        if (!policy.IsStructurallyValid())
        {
            return Invalid(
                key,
                "The agent policy must define a supported set of valid permissions.");
        }

        if (!policy.IsValidForDurableStorage())
        {
            return Invalid(
                key,
                "YOLO is run-local and cannot be persisted in a saved agent policy.");
        }

        return null;
    }

    private static bool TryReadPayloadSchemaVersion(
        string payloadJson,
        out int schemaVersion)
    {
        schemaVersion = default;
        using var payload = JsonDocument.Parse(payloadJson);
        return payload.RootElement.TryGetProperty("schemaVersion", out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out schemaVersion);
    }

    private static bool IsPayloadException(Exception exception) =>
        exception is JsonException
            or NotSupportedException
            or ArgumentException
            or InvalidOperationException
            or NullReferenceException
            or OverflowException;
}
