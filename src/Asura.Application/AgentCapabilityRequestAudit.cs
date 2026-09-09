using Asura.Core;

namespace Asura.Application;

public enum AgentCapabilityRequestAuditDecision
{
    Allowed,
    Denied,
    Expired,
    Cancelled,
    TargetChanged,
    CapabilityUnavailable,
    PolicyChanged,
    AuditFailed,
}

/// <summary>
/// Closed, secret-free input to the broker-owned capability-request audit chain.
/// Display labels and provider/model prose are deliberately excluded.
/// </summary>
public abstract record AgentCapabilityRequestAuditEvent
{
    private AgentCapabilityRequestAuditEvent()
    {
    }

    public sealed record Requested : AgentCapabilityRequestAuditEvent
    {
        public Requested(
            AgentCapabilityRequestId requestId,
            AgentRunId runId,
            AgentCapability capability,
            AgentTarget target,
            long policyGeneration)
        {
            Validate(requestId, runId);
            if (!Enum.IsDefined(capability))
            {
                throw new ArgumentOutOfRangeException(nameof(capability));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
            RequestId = requestId;
            RunId = runId;
            Capability = capability;
            Target = target ?? throw new ArgumentNullException(nameof(target));
            PolicyGeneration = policyGeneration;
        }

        public AgentCapabilityRequestId RequestId { get; }

        public AgentRunId RunId { get; }

        public AgentCapability Capability { get; }

        public AgentTarget Target { get; }

        public long PolicyGeneration { get; }
    }

    public sealed record Terminal : AgentCapabilityRequestAuditEvent
    {
        public Terminal(
            AgentCapabilityRequestId requestId,
            AgentRunId runId,
            AgentCapabilityRequestAuditDecision decision,
            ActorDescriptor actor)
        {
            Validate(requestId, runId);
            if (!Enum.IsDefined(decision))
            {
                throw new ArgumentOutOfRangeException(nameof(decision));
            }

            RequestId = requestId;
            RunId = runId;
            Decision = decision;
            Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        }

        public AgentCapabilityRequestId RequestId { get; }

        public AgentRunId RunId { get; }

        public AgentCapabilityRequestAuditDecision Decision { get; }

        public ActorDescriptor Actor { get; }
    }

    private static void Validate(
        AgentCapabilityRequestId requestId,
        AgentRunId runId)
    {
        if (string.IsNullOrWhiteSpace(requestId.Value)
            || requestId.Value.Any(char.IsControl)
            || requestId.Value.Length > 256)
        {
            throw new ArgumentException(
                "A capability request identifier must be printable and bounded.",
                nameof(requestId));
        }

        AgentRunRegistration.ValidateRunId(runId);
    }
}
