using System.Text;
using Asura.Core;

namespace Asura.Application;

public sealed record AgentActionProposal
{
    // Long-lived observation waits reserve approval and provider-continuation
    // grace around their one-hour condition. Runtime mutation proposals remain
    // two minutes and mutation execution permits remain thirty seconds.
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(70);

    internal AgentActionProposal(
        AgentActionId id,
        AgentRunId runId,
        ActorDescriptor actor,
        string toolName,
        AgentTarget target,
        AgentActionDigest targetFingerprint,
        AgentActionDigest argumentDigest,
        AgentApprovalPresentation presentation,
        long policyGeneration,
        DateTimeOffset createdAtUtc,
        DateTimeOffset deadlineUtc)
    {
        RequireIdentifier(id.Value, nameof(id));
        RequireIdentifier(runId.Value, nameof(runId));

        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != ActorKind.Agent
            || !IsBoundedText(actor.Id.Value, 256)
            || !IsBoundedText(actor.DisplayName, 256))
        {
            throw new ArgumentException(
                "An agent action requires an authenticated agent actor.",
                nameof(actor));
        }

        if (string.IsNullOrWhiteSpace(toolName)
            || toolName.Length > 128
            || toolName[0] == '.'
            || toolName[^1] == '.'
            || toolName.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '.'
                    and not '_'
                    and not '-'))
        {
            throw new ArgumentException(
                "A proposal tool name must be a bounded lowercase identifier.",
                nameof(toolName));
        }

        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(presentation);
        if (string.IsNullOrWhiteSpace(targetFingerprint.Value))
        {
            throw new ArgumentException(
                "A target fingerprint is required.",
                nameof(targetFingerprint));
        }

        if (string.IsNullOrWhiteSpace(argumentDigest.Value))
        {
            throw new ArgumentException(
                "An argument digest is required.",
                nameof(argumentDigest));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
        if (createdAtUtc.Offset != TimeSpan.Zero
            || deadlineUtc.Offset != TimeSpan.Zero
            || deadlineUtc <= createdAtUtc
            || deadlineUtc - createdAtUtc > MaximumLifetime)
        {
            throw new ArgumentException(
                "Agent action timestamps must be ordered UTC values with a bounded lifetime.",
                nameof(deadlineUtc));
        }

        Id = id;
        RunId = runId;
        Actor = actor;
        ToolName = string.Concat(toolName);
        Target = target;
        TargetIdentity = AgentTargetIdentity.Create(target);
        TargetFingerprint = targetFingerprint;
        ArgumentDigest = argumentDigest;
        Presentation = presentation;
        PolicyGeneration = policyGeneration;
        CreatedAtUtc = createdAtUtc;
        DeadlineUtc = deadlineUtc;
    }

    public AgentActionId Id { get; }

    public AgentRunId RunId { get; }

    public ActorDescriptor Actor { get; }

    public string ToolName { get; }

    public AgentTarget Target { get; }

    public AgentActionDigest TargetIdentity { get; }

    public AgentActionDigest TargetFingerprint { get; }

    public AgentActionDigest ArgumentDigest { get; }

    public AgentApprovalPresentation Presentation { get; }

    public long PolicyGeneration { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset DeadlineUtc { get; }

    internal static AgentActionProposal FromContext(
        AgentActionId id,
        AgentRunId runId,
        ActorDescriptor actor,
        string toolName,
        AgentContextSnapshot context,
        AgentActionDigest argumentDigest,
        AgentApprovalPresentation presentation,
        long policyGeneration,
        DateTimeOffset createdAtUtc,
        DateTimeOffset deadlineUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AgentActionProposal(
            id,
            runId,
            actor,
            toolName,
            context.Target,
            context.BindingFingerprint,
            argumentDigest,
            presentation,
            policyGeneration,
            createdAtUtc,
            deadlineUtc);
    }

    private static void RequireIdentifier(string? value, string parameterName)
    {
        if (!IsBoundedText(value, 256))
        {
            throw new ArgumentException(
                "An agent action identifier must be printable and at most 256 UTF-8 bytes.",
                parameterName);
        }
    }

    private static bool IsBoundedText(string? value, int maximumBytes) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => !char.IsControl(character))
        && Encoding.UTF8.GetByteCount(value) <= maximumBytes;
}
