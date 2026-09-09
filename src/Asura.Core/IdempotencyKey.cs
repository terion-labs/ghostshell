using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct IdempotencyKey
{
    [JsonConstructor]
    public IdempotencyKey(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static IdempotencyKey New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
