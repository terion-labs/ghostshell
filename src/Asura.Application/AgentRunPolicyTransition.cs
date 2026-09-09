namespace Asura.Application;

/// <summary>
/// Identifies the observable effect of one authoritative agent-run policy change.
/// </summary>
public enum AgentRunPolicyTransition
{
    Updated,
    YoloEnabled,
    YoloDisabled,
    YoloExpired,
}
