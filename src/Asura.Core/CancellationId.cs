using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct CancellationId
{
    [JsonConstructor]
    public CancellationId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static CancellationId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
