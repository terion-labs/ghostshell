using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct SessionId
{
    [JsonConstructor]
    public SessionId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static SessionId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
