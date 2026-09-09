using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct ActorId
{
    [JsonConstructor]
    public ActorId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static ActorId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
