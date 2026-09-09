namespace Asura.Application;

public sealed record AgentRunAuditPolicyEntry : AgentRunAuditEntry
{
    public AgentRunAuditPolicyEntry(
        AgentActionDigest entryId,
        AgentRunPolicyTransition transition,
        long policyGeneration,
        AgentActionDigest targetIdentity,
        DateTimeOffset? yoloExpiresAtUtc,
        DateTimeOffset occurredAtUtc)
        : base(entryId, occurredAtUtc)
    {
        if (!Enum.IsDefined(transition))
        {
            throw new ArgumentOutOfRangeException(nameof(transition));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
        if (string.IsNullOrWhiteSpace(targetIdentity.Value))
        {
            throw new ArgumentException(
                "An audited policy transition requires a target-identity digest.",
                nameof(targetIdentity));
        }

        if (yoloExpiresAtUtc is { Offset: var offset }
            && offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An audited YOLO expiry must be UTC.",
                nameof(yoloExpiresAtUtc));
        }

        Transition = transition;
        PolicyGeneration = policyGeneration;
        TargetIdentity = targetIdentity;
        YoloExpiresAtUtc = yoloExpiresAtUtc;
    }

    public AgentRunPolicyTransition Transition { get; }

    public long PolicyGeneration { get; }

    public AgentActionDigest TargetIdentity { get; }

    public DateTimeOffset? YoloExpiresAtUtc { get; }
}
