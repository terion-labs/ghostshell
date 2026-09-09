using Asura.Core;

namespace Asura.Application;

internal static class AgentAuditEventId
{
    public static string ForPhase(
        AgentActionId actionId,
        AuditOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        var digest = AgentActionDigest.FromUtf8(
            $"asura-agent-audit-phase-v1\0{actionId.Value}\0{outcome}");
        return $"agent-{digest.Value}";
    }

    // Policy generations are scoped to one live broker registration and may
    // repeat when a durable conversation is restored. The broker retains this
    // exact ID while retrying one ambiguous audit commit.
    public static string NewPolicyTransition() =>
        $"agent-policy-{Guid.CreateVersion7():N}";

    public static string ForCapabilityRequestRequested(
        AgentCapabilityRequestId requestId) =>
        ForCapabilityRequest(requestId, "requested");

    public static string ForCapabilityRequestTerminal(
        AgentCapabilityRequestId requestId) =>
        ForCapabilityRequest(requestId, "terminal");

    private static string ForCapabilityRequest(
        AgentCapabilityRequestId requestId,
        string phase)
    {
        var digest = AgentActionDigest.FromUtf8(
            $"asura-capability-request-audit-v1\0{requestId.Value}\0{phase}");
        return $"agent-capability-{digest.Value}";
    }
}
