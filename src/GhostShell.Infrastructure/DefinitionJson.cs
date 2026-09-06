using System.Text.Json;
using System.Text.Json.Serialization;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal static class DefinitionJson
{
    private static DefinitionJsonContext Context { get; } = CreateContext();

    public static JsonSerializerOptions Options => Context.Options;

    public static string SerializeAgentPolicy(AgentPolicy policy) =>
        JsonSerializer.Serialize(policy, Context.AgentPolicy);

    public static AgentPolicy? DeserializeAgentPolicy(string payloadJson) =>
        JsonSerializer.Deserialize(payloadJson, Context.AgentPolicy);

    public static string Serialize(IDurableDefinition definition) =>
        definition switch
        {
            ConnectionProfile value => JsonSerializer.Serialize(value, Context.ConnectionProfile),
            LayoutDefinition value => JsonSerializer.Serialize(value, Context.LayoutDefinition),
            ScreenDefinition value => JsonSerializer.Serialize(value, Context.ScreenDefinition),
            WorkspaceDefinition value => JsonSerializer.Serialize(value, Context.WorkspaceDefinition),
            ThemePreference value => JsonSerializer.Serialize(value, Context.ThemePreference),
            TerminalProfile value => JsonSerializer.Serialize(value, Context.TerminalProfile),
            KeymapProfile value => JsonSerializer.Serialize(value, Context.KeymapProfile),
            FileProviderProfile value => JsonSerializer.Serialize(value, Context.FileProviderProfile),
            AiProviderProfile value => JsonSerializer.Serialize(value, Context.AiProviderProfile),
            McpServerProfile value => JsonSerializer.Serialize(value, Context.McpServerProfile),
            BrowserProfileDefinition value => JsonSerializer.Serialize(value, Context.BrowserProfileDefinition),
            DatabaseConnectionProfile value => JsonSerializer.Serialize(value, Context.DatabaseConnectionProfile),
            QuickTerminalSettings value => JsonSerializer.Serialize(value, Context.QuickTerminalSettings),
            NetworkConnectionProfile value => JsonSerializer.Serialize(value, Context.NetworkConnectionProfile),
            ApplicationNetworkSettings value => JsonSerializer.Serialize(value, Context.ApplicationNetworkSettings),
            _ => throw new NotSupportedException(
                $"Definition type '{definition.GetType().FullName}' is not supported."),
        };

    public static IDurableDefinition? Deserialize(DefinitionKind kind, string payloadJson) =>
        kind switch
        {
            var value when value == DefinitionKind.Connection =>
                JsonSerializer.Deserialize(payloadJson, Context.ConnectionProfile),
            var value when value == DefinitionKind.Layout =>
                JsonSerializer.Deserialize(payloadJson, Context.LayoutDefinition),
            var value when value == DefinitionKind.Screen =>
                JsonSerializer.Deserialize(payloadJson, Context.ScreenDefinition),
            var value when value == DefinitionKind.Workspace =>
                JsonSerializer.Deserialize(payloadJson, Context.WorkspaceDefinition),
            var value when value == DefinitionKind.Theme =>
                JsonSerializer.Deserialize(payloadJson, Context.ThemePreference),
            var value when value == DefinitionKind.TerminalProfile =>
                JsonSerializer.Deserialize(payloadJson, Context.TerminalProfile),
            var value when value == DefinitionKind.Keymap =>
                JsonSerializer.Deserialize(payloadJson, Context.KeymapProfile),
            var value when value == DefinitionKind.FileProviderProfile =>
                JsonSerializer.Deserialize(payloadJson, Context.FileProviderProfile),
            var value when value == DefinitionKind.AiProviderProfile =>
                JsonSerializer.Deserialize(payloadJson, Context.AiProviderProfile),
            var value when value == DefinitionKind.McpServerProfile =>
                JsonSerializer.Deserialize(payloadJson, Context.McpServerProfile),
            var value when value == DefinitionKind.BrowserProfile =>
                JsonSerializer.Deserialize(payloadJson, Context.BrowserProfileDefinition),
            var value when value == DefinitionKind.DatabaseConnection =>
                JsonSerializer.Deserialize(payloadJson, Context.DatabaseConnectionProfile),
            var value when value == DefinitionKind.QuickTerminalSettings =>
                JsonSerializer.Deserialize(payloadJson, Context.QuickTerminalSettings),
            var value when value == DefinitionKind.NetworkConnection =>
                JsonSerializer.Deserialize(payloadJson, Context.NetworkConnectionProfile),
            var value when value == DefinitionKind.ApplicationNetworkSettings =>
                JsonSerializer.Deserialize(payloadJson, Context.ApplicationNetworkSettings),
            _ => null,
        };

    private static DefinitionJsonContext CreateContext()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowDuplicateProperties = false,
            AllowTrailingCommas = false,
            MaxDepth = 64,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };

        AddStrictStringEnumConverter<AgentCapability>(options);
        AddStrictStringEnumConverter<AgentPermission>(options);
        AddStrictStringEnumConverter<AiProviderKind>(options);
        AddStrictStringEnumConverter<AiProviderProtocol>(options);
        AddStrictStringEnumConverter<AiProviderOAuthFlow>(options);
        AddStrictStringEnumConverter<AppearanceMode>(options);
        AddStrictStringEnumConverter<PlatformProfile>(options);
        AddStrictStringEnumConverter<InterfaceDensity>(options);
        AddStrictStringEnumConverter<TabStripPlacement>(options);
        AddStrictStringEnumConverter<WorkspacePanelPlacement>(options);
        AddStrictStringEnumConverter<AccentPreferenceKind>(options);
        AddStrictStringEnumConverter<PanelKind>(options);
        AddStrictStringEnumConverter<SshHostKeyPolicy>(options);
        AddStrictStringEnumConverter<ScreenPanelKind>(options);
        AddStrictStringEnumConverter<StartupCommandDeliveryFailurePolicy>(options);
        AddStrictStringEnumConverter<TerminalCursorStyle>(options);
        AddStrictStringEnumConverter<TerminalClipboardAccess>(options);
        AddStrictStringEnumConverter<TerminalPasteSafetyPolicy>(options);
        AddStrictStringEnumConverter<TerminalLinkPolicy>(options);
        AddStrictStringEnumConverter<TerminalShellIntegrationMode>(options);
        AddStrictStringEnumConverter<TerminalBellMode>(options);
        AddStrictStringEnumConverter<TerminalCompatibilityProfile>(options);
        AddStrictStringEnumConverter<KeymapLayer>(options);
        AddStrictStringEnumConverter<FailedSequenceBehavior>(options);
        AddStrictStringEnumConverter<KeyModifiers>(options);
        AddStrictStringEnumConverter<CommandContext>(options);
        AddStrictStringEnumConverter<FtpSecurityMode>(options);
        AddStrictStringEnumConverter<FtpConnectionMode>(options);
        AddStrictStringEnumConverter<SmbCredentialMode>(options);
        AddStrictStringEnumConverter<QuickTerminalMonitorPolicy>(options);
        AddStrictStringEnumConverter<TerminalMultiplexingMode>(options);
        AddStrictStringEnumConverter<WorkspaceBrowserProfileMode>(options);
        AddStrictStringEnumConverter<BrowserProfilePersistence>(options);
        AddStrictStringEnumConverter<BrowserWebContentRetention>(options);
        AddStrictStringEnumConverter<BrowserPermissionRetention>(options);
        AddStrictStringEnumConverter<BrowserActivityRetention>(options);
        AddStrictStringEnumConverter<BrowserAuthenticationScheme>(options);
        AddStrictStringEnumConverter<NetworkProxyProtocol>(options);

        return new DefinitionJsonContext(options);
    }

    private static void AddStrictStringEnumConverter<TEnum>(JsonSerializerOptions options)
        where TEnum : struct, Enum =>
        options.Converters.Add(
            new JsonStringEnumConverter<TEnum>(allowIntegerValues: false));
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    AllowDuplicateProperties = false,
    AllowTrailingCommas = false,
    MaxDepth = 64,
    PropertyNameCaseInsensitive = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(ConnectionProfile))]
[JsonSerializable(typeof(LayoutDefinition))]
[JsonSerializable(typeof(ScreenDefinition))]
[JsonSerializable(typeof(WorkspaceDefinition))]
[JsonSerializable(typeof(ThemePreference))]
[JsonSerializable(typeof(TerminalProfile))]
[JsonSerializable(typeof(KeymapProfile))]
[JsonSerializable(typeof(FileProviderProfile))]
[JsonSerializable(typeof(AiProviderProfile))]
[JsonSerializable(typeof(McpServerProfile))]
[JsonSerializable(typeof(BrowserProfileDefinition))]
[JsonSerializable(typeof(DatabaseConnectionProfile))]
[JsonSerializable(typeof(QuickTerminalSettings))]
[JsonSerializable(typeof(NetworkConnectionProfile))]
[JsonSerializable(typeof(ApplicationNetworkSettings))]
[JsonSerializable(typeof(AgentPolicy))]
[JsonSerializable(typeof(ConnectionEndpoint.Local), TypeInfoPropertyName = "ConnectionEndpointLocal")]
[JsonSerializable(typeof(FileProviderConfiguration.Local), TypeInfoPropertyName = "FileProviderConfigurationLocal")]
[JsonSerializable(typeof(AiProviderAuthentication.None), TypeInfoPropertyName = "AiProviderAuthenticationNone")]
[JsonSerializable(typeof(ConnectionAuthentication.None), TypeInfoPropertyName = "ConnectionAuthenticationNone")]
[JsonSerializable(typeof(NetworkConnectionConfiguration.Proxy), TypeInfoPropertyName = "NetworkConfigurationProxy")]
[JsonSerializable(typeof(NetworkConnectionConfiguration.WireGuard), TypeInfoPropertyName = "NetworkConfigurationWireGuard")]
[JsonSerializable(typeof(NetworkConnectionConfiguration.OpenVpn), TypeInfoPropertyName = "NetworkConfigurationOpenVpn")]
[JsonSerializable(typeof(NetworkConnectionConfiguration.AnyConnect), TypeInfoPropertyName = "NetworkConfigurationAnyConnect")]
[JsonSerializable(typeof(NetworkConnectionConfiguration.Tailscale), TypeInfoPropertyName = "NetworkConfigurationTailscale")]
internal sealed partial class DefinitionJsonContext : JsonSerializerContext;
