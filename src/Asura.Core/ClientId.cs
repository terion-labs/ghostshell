using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct ClientId
{
    [JsonConstructor]
    public ClientId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static ClientId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
