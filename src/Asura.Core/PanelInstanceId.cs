using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct PanelInstanceId
{
    [JsonConstructor]
    public PanelInstanceId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static PanelInstanceId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
