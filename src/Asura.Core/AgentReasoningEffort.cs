namespace Asura.Core;

/// <summary>
/// Provider-neutral reasoning effort requested for one agent turn. Adapters
/// translate the bounded set to their native protocol and may reject a level
/// that the selected model does not support.
/// </summary>
public enum AgentReasoningEffort
{
    Automatic,
    Off,
    Minimal,
    Low,
    Medium,
    High,
    ExtraHigh,
    Max,
}
