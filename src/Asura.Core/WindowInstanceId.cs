using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct WindowInstanceId
{
    [JsonConstructor]
    public WindowInstanceId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static WindowInstanceId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
