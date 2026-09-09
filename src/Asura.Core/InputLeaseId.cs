using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct InputLeaseId
{
    [JsonConstructor]
    public InputLeaseId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static InputLeaseId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
