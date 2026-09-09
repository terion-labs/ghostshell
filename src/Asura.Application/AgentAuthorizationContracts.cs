using Asura.Core;

namespace Asura.Application;

public enum AgentAuthorizationErrorCode
{
    InvalidRequest,
    RunNotFound,
    RunAlreadyRegistered,
    RunCancelled,
    RunSuspended,
    RunActorMismatch,
    TargetOutsideRunScope,
    YoloConfirmationRequired,
    CapacityExceeded,
    DuplicateAction,
    UnknownTool,
    PolicyDenied,
    ApprovalDenied,
    ApprovalActorMismatch,
    ApprovalNotFound,
    ApprovalExpired,
    AuthorizationNotFound,
    AuthorizationExpired,
    AuthorizationMismatch,
    PolicyChanged,
    AuditUnavailable,
    AlreadyCompleted,
    Cancelled,
}

public sealed record AgentAuthorizationError(
    AgentAuthorizationErrorCode Code,
    string Message);

public enum AgentApprovalDuration
{
    Once,
}

public sealed record AgentApprovalRequest
{
    internal AgentApprovalRequest(
        AgentApprovalId id,
        AgentActionProposal proposal,
        AgentToolDescriptor tool,
        AgentPermission permission,
        DateTimeOffset expiresAtUtc)
    {
        Id = id;
        Proposal = proposal;
        Tool = tool;
        Permission = permission;
        ExpiresAtUtc = expiresAtUtc;
    }

    public AgentApprovalId Id { get; }

    public AgentActionProposal Proposal { get; }

    public AgentToolDescriptor Tool { get; }

    public AgentPermission Permission { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed record AgentApprovalDecision
{
    public AgentApprovalDecision(
        AgentApprovalId approvalId,
        ActorDescriptor actor,
        bool approved,
        AgentApprovalDuration duration,
        DateTimeOffset decidedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != ActorKind.Human
            || actor.ClientId is not { } clientId
            || !string.Equals(actor.Id.Value, clientId.Value
, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(actor.Id.Value)
            || string.IsNullOrWhiteSpace(actor.DisplayName))
        {
            throw new ArgumentException(
                "An approval decision requires an authenticated human actor.",
                nameof(actor));
        }

        if (!Enum.IsDefined(duration))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (decidedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An approval decision timestamp must be UTC.",
                nameof(decidedAtUtc));
        }

        ApprovalId = approvalId;
        Actor = actor;
        Approved = approved;
        Duration = duration;
        DecidedAtUtc = decidedAtUtc;
    }

    public AgentApprovalId ApprovalId { get; }

    public ActorDescriptor Actor { get; }

    public bool Approved { get; }

    public AgentApprovalDuration Duration { get; }

    public DateTimeOffset DecidedAtUtc { get; }
}

public sealed record AgentActionAuthorization
{
    internal AgentActionAuthorization(
        AgentAuthorizationId id,
        AgentActionProposal proposal,
        AgentToolDescriptor tool,
        AgentAuthorizationSource source,
        ClientId approvingClientId,
        DateTimeOffset expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(approvingClientId.Value))
        {
            throw new ArgumentException(
                "An action authorization requires its authenticated approving client.",
                nameof(approvingClientId));
        }

        Id = id;
        ActionId = proposal.Id;
        RunId = proposal.RunId;
        Agent = proposal.Actor;
        ActorId = proposal.Actor.Id;
        ToolName = tool.Name;
        TargetIdentity = proposal.TargetIdentity;
        TargetFingerprint = proposal.TargetFingerprint;
        ArgumentDigest = proposal.ArgumentDigest;
        PolicyGeneration = proposal.PolicyGeneration;
        Source = source;
        ApprovingClientId = approvingClientId;
        ExpiresAtUtc = expiresAtUtc;
    }

    public AgentAuthorizationId Id { get; }

    public AgentActionId ActionId { get; }

    public AgentRunId RunId { get; }

    public ActorDescriptor Agent { get; }

    public ActorId ActorId { get; }

    public string ToolName { get; }

    public AgentActionDigest TargetIdentity { get; }

    public AgentActionDigest TargetFingerprint { get; }

    public AgentActionDigest ArgumentDigest { get; }

    public long PolicyGeneration { get; }

    public AgentAuthorizationSource Source { get; }

    public ClientId ApprovingClientId { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}

public abstract record AgentAuthorizationResult
{
    private AgentAuthorizationResult()
    {
    }

    public sealed record Authorized(AgentActionAuthorization Authorization)
        : AgentAuthorizationResult;

    public sealed record ApprovalRequired(AgentApprovalRequest Approval)
        : AgentAuthorizationResult;

    public sealed record Denied(AgentAuthorizationError Error)
        : AgentAuthorizationResult;
}

public sealed record AgentActionPermit
{
    internal AgentActionPermit(
        AgentActionAuthorization authorization,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken,
        DateTimeOffset? executionDeadlineUtc = null)
    {
        var deadline = executionDeadlineUtc ?? authorization.ExpiresAtUtc;
        if (startedAtUtc.Offset != TimeSpan.Zero
            || deadline.Offset != TimeSpan.Zero
            || deadline <= startedAtUtc)
        {
            throw new ArgumentException(
                "An action permit requires an ordered UTC execution window.",
                nameof(executionDeadlineUtc));
        }

        Authorization = authorization;
        StartedAtUtc = startedAtUtc;
        ExecutionDeadlineUtc = deadline;
        CancellationToken = cancellationToken;
    }

    public AgentActionAuthorization Authorization { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset ExecutionDeadlineUtc { get; }

    /// <summary>
    /// Cancelled when the run stops, its policy generation changes, or the
    /// broker explicitly revokes this in-flight action.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}

public abstract record AgentPermitResult
{
    private AgentPermitResult()
    {
    }

    public sealed record Granted(AgentActionPermit Permit) : AgentPermitResult;

    public sealed record Denied(AgentAuthorizationError Error) : AgentPermitResult;
}

public enum AgentActionOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record AgentActionCompletion
{
    public AgentActionCompletion(
        AgentActionOutcome outcome,
        string? stableCode,
        DateTimeOffset finishedAtUtc,
        int? resultCount = null)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (stableCode is { Length: > 128 }
            || stableCode?.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'
                    and not '-') == true)
        {
            throw new ArgumentException(
                "An agent action result code must be a bounded stable identifier.",
                nameof(stableCode));
        }

        if (finishedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An action completion timestamp must be UTC.",
                nameof(finishedAtUtc));
        }

        if (resultCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resultCount));
        }

        Outcome = outcome;
        StableCode = stableCode;
        FinishedAtUtc = finishedAtUtc;
        ResultCount = resultCount;
    }

    public AgentActionOutcome Outcome { get; }

    public string? StableCode { get; }

    public DateTimeOffset FinishedAtUtc { get; }

    public int? ResultCount { get; }
}

/// <summary>
/// The narrow capability held by the trusted session-host execution bridge.
/// A provider runtime receives neither this capability nor an execution host.
/// </summary>
public interface IAgentAuthorizationConsumer
{
    ValueTask<AgentPermitResult> ConsumeAsync(
        AgentAuthorizationId authorizationId,
        AgentActionExecutionBinding currentBinding,
        CancellationToken cancellationToken);

    ValueTask<AgentAuthorizationError?> CompleteAsync(
        AgentActionPermit permit,
        AgentActionCompletion completion,
        CancellationToken cancellationToken);
}

public interface IAgentCapabilityBroker : IAgentAuthorizationConsumer
{
    ValueTask<AgentAuthorizationError?> RegisterRunAsync(
        AgentRunRegistration registration,
        CancellationToken cancellationToken);

    ValueTask<AgentAuthorizationError?> UpdateRunPolicyAsync(
        AgentRunPolicyUpdate update,
        CancellationToken cancellationToken);

    ValueTask<AgentAuthorizationError?> RecordCapabilityRequestAuditAsync(
        AgentCapabilityRequestAuditEvent auditEvent,
        CancellationToken cancellationToken);

    ValueTask<AgentAuthorizationError?> CancelRunAsync(
        AgentRunCancellation cancellation,
        CancellationToken cancellationToken);

    ValueTask<AgentAuthorizationResult> RequestAsync(
        AgentActionProposal proposal,
        CancellationToken cancellationToken);

    ValueTask<AgentAuthorizationResult> DecideAsync(
        AgentApprovalDecision decision,
        CancellationToken cancellationToken);

}
