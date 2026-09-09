using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// Correlates one run-local model question with exactly one local-human
/// response. It conveys no approval or reusable authority.
/// </summary>
public readonly record struct AgentQuestionId
{
    [JsonConstructor]
    public AgentQuestionId(string value) =>
        Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static AgentQuestionId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
