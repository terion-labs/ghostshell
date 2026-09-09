using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct RequestId
{
    [JsonConstructor]
    public RequestId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static RequestId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
