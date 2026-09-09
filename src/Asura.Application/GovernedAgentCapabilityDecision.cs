namespace Asura.Application;

/// <summary>
/// A local-human decision for one capability request. AllowAsk can only enable
/// ordinary per-action approval; neither branch grants Auto or YOLO authority.
/// </summary>
public abstract record GovernedAgentCapabilityDecision
{
    private GovernedAgentCapabilityDecision()
    {
    }

    public sealed record AllowAsk : GovernedAgentCapabilityDecision;

    public sealed record KeepOff : GovernedAgentCapabilityDecision;
}
