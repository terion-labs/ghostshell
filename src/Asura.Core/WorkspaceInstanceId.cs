using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct WorkspaceInstanceId
{
    [JsonConstructor]
    public WorkspaceInstanceId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static WorkspaceInstanceId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
