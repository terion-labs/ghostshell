using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct WorkspaceId
{
    [JsonConstructor]
    public WorkspaceId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static WorkspaceId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct TabId
{
    [JsonConstructor]
    public TabId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static TabId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct PanelId
{
    [JsonConstructor]
    public PanelId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static PanelId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct ConnectionId
{
    [JsonConstructor]
    public ConnectionId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static ConnectionId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct CommandBlockId
{
    [JsonConstructor]
    public CommandBlockId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static CommandBlockId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct AgentRunId
{
    [JsonConstructor]
    public AgentRunId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static AgentRunId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct LayoutId
{
    [JsonConstructor]
    public LayoutId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static LayoutId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct LayoutSlotId
{
    [JsonConstructor]
    public LayoutSlotId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static LayoutSlotId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct ScreenId
{
    [JsonConstructor]
    public ScreenId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static ScreenId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct ScreenPanelId
{
    [JsonConstructor]
    public ScreenPanelId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static ScreenPanelId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct WorkspaceEntryId
{
    [JsonConstructor]
    public WorkspaceEntryId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static WorkspaceEntryId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct SecretRef
{
    [JsonConstructor]
    public SecretRef(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static SecretRef New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
