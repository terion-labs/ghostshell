using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// Identifies one editable, run-local user message waiting in the agent queue.
/// The identifier conveys neither approval nor tool authority.
/// </summary>
public readonly record struct AgentQueuedFollowUpId
{
    [JsonConstructor]
    public AgentQueuedFollowUpId(string value) =>
        Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static AgentQueuedFollowUpId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}
