using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct AgentActionId
{
    [JsonConstructor]
    public AgentActionId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static AgentActionId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct AgentApprovalId
{
    [JsonConstructor]
    public AgentApprovalId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static AgentApprovalId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public readonly record struct AgentAuthorizationId
{
    [JsonConstructor]
    public AgentAuthorizationId(string value) => Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static AgentAuthorizationId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

public enum AgentAuthorizationSource
{
    AutoPolicy,
    YoloPolicy,
    HumanApproval,
}
