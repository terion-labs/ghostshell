using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct DefinitionKey
{
    [JsonConstructor]
    public DefinitionKey(DefinitionKind kind, string value)
    {
        RuntimeId.Require(kind.Value, nameof(kind));
        Kind = kind;
        Value = RuntimeId.Require(value, nameof(value));
    }

    public DefinitionKind Kind { get; }

    public string Value { get; }

    public override string ToString() => $"{Kind}:{Value}";
}
