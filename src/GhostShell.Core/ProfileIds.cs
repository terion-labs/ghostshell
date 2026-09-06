using System.Text.Json.Serialization;

namespace GhostShell.Core;

public readonly record struct ThemePreferenceId
{
    [JsonConstructor]
    public ThemePreferenceId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static ThemePreferenceId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct TerminalProfileId
{
    [JsonConstructor]
    public TerminalProfileId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static TerminalProfileId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct KeymapProfileId
{
    [JsonConstructor]
    public KeymapProfileId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static KeymapProfileId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct FileProviderProfileId
{
    [JsonConstructor]
    public FileProviderProfileId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static FileProviderProfileId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct AiProviderProfileId
{
    [JsonConstructor]
    public AiProviderProfileId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static AiProviderProfileId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct McpServerProfileId
{
    [JsonConstructor]
    public McpServerProfileId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static McpServerProfileId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct BrowserProfileId
{
    [JsonConstructor]
    public BrowserProfileId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static BrowserProfileId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct CommandId
{
    [JsonConstructor]
    public CommandId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct NetworkConnectionId
{
    [JsonConstructor]
    public NetworkConnectionId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static NetworkConnectionId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct ApplicationNetworkSettingsId
{
    [JsonConstructor]
    public ApplicationNetworkSettingsId(string value) =>
        Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}
