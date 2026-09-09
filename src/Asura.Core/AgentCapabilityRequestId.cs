using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// Correlates one run-local request to enable an otherwise disabled
/// capability. It conveys no approval or reusable authority.
/// </summary>
public readonly record struct AgentCapabilityRequestId
{
    [JsonConstructor]
    public AgentCapabilityRequestId(string value) =>
        Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static AgentCapabilityRequestId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
