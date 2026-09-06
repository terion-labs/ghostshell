using System.Text.Json.Serialization;

namespace GhostShell.Core;

/// <summary>
/// A durable discriminator. It is intentionally open-ended so newer definition kinds
/// can round-trip through an older client without being collapsed into an enum fallback.
/// </summary>
public readonly record struct DefinitionKind
{
    [JsonConstructor]
    public DefinitionKind(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static DefinitionKind Connection { get; } = new("connection");

    public static DefinitionKind Layout { get; } = new("layout");

    public static DefinitionKind Screen { get; } = new("screen");

    public static DefinitionKind Workspace { get; } = new("workspace");

    public static DefinitionKind Theme { get; } = new("theme");

    public static DefinitionKind TerminalProfile { get; } = new("terminal-profile");

    public static DefinitionKind Keymap { get; } = new("keymap");

    public static DefinitionKind FileProviderProfile { get; } = new("file-provider-profile");

    public static DefinitionKind AiProviderProfile { get; } = new("ai-provider-profile");

    public static DefinitionKind McpServerProfile { get; } = new("mcp-server-profile");

    public static DefinitionKind BrowserProfile { get; } = new("browser-profile");

    public static DefinitionKind DatabaseConnection { get; } = new("database-connection");

    public static DefinitionKind QuickTerminalSettings { get; } = new("quick-terminal-settings");

    public static DefinitionKind NetworkConnection { get; } = new("network-connection");

    public static DefinitionKind ApplicationNetworkSettings { get; } =
        new("application-network-settings");

    public override string ToString() => Value;
}
