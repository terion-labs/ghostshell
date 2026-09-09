
using Asura.Core;

namespace Asura.Application;
/// <summary>
/// Audit details are a closed set of value-only shapes. Adding a new shape requires an explicit
/// storage mapping, so callers cannot smuggle arbitrary or secret-bearing JSON into the audit log.
/// </summary>
public abstract record AuditDetails
{
    private AuditDetails()
    {
    }

    public static AuditDetails None { get; } = new EmptyDetails();

    public static AuditDetails ForSecretAccess(
        SecretUseKind purposeKind,
        SecretVaultErrorCode? errorCode = null)
    {
        if (!Enum.IsDefined(purposeKind))
        {
            throw new ArgumentOutOfRangeException(nameof(purposeKind));
        }

        if (errorCode is { } code && !Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }

        return new SecretAccessDetails(purposeKind, errorCode);
    }

    public static AuditDetails ForTerminalStartupCommands(
        int commandCount,
        TerminalStartupCommandDispatchErrorCode? errorCode = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandCount);
        if (errorCode is { } code && !Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }

        return new TerminalStartupCommandDetails(commandCount, errorCode);
    }

    public static AuditDetails ForAgentAction(
        AgentRunId runId,
        AgentCapability capability,
        AgentActionRisk risk,
        AgentPermission permission,
        AgentPolicyDecision decision,
        AgentActionDigest argumentDigest,
        AgentAuthorizationSource? authorizationSource = null,
        AgentAuthorizationErrorCode? errorCode = null,
        string? resultCode = null,
        AgentActionAuditBinding? binding = null)
    {
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        if (!Enum.IsDefined(permission))
        {
            throw new ArgumentOutOfRangeException(nameof(permission));
        }

        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        if (authorizationSource is { } source && !Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationSource));
        }

        if (errorCode is { } code && !Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }

        if (resultCode is { Length: > 128 }
            || resultCode?.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'
                    and not '-') == true)
        {
            throw new ArgumentException(
                "An audit result code must be a bounded stable identifier.",
                nameof(resultCode));
        }

        return new AgentActionDetails(
            runId,
            capability,
            risk,
            permission,
            decision,
            argumentDigest,
            authorizationSource,
            errorCode,
            resultCode,
            binding ?? AgentActionAuditBinding.Empty);
    }

    public static AuditDetails ForAgentRunPolicyTransition(
        AgentRunId runId,
        AgentRunPolicyTransition transition,
        long policyGeneration,
        AgentActionDigest targetIdentityDigest,
        DateTimeOffset? yoloExpiresAtUtc = null,
        AgentCapabilityRequestId? capabilityRequestId = null)
    {
        AgentRunRegistration.ValidateRunId(runId);
        if (!Enum.IsDefined(transition))
        {
            throw new ArgumentOutOfRangeException(nameof(transition));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
        if (string.IsNullOrWhiteSpace(targetIdentityDigest.Value))
        {
            throw new ArgumentException(
                "A policy-transition audit record requires a target-identity digest.",
                nameof(targetIdentityDigest));
        }

        if (yoloExpiresAtUtc is { Offset: var offset }
            && offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An audited YOLO expiry must be UTC.",
                nameof(yoloExpiresAtUtc));
        }

        if (capabilityRequestId is { } requestId
            && (string.IsNullOrWhiteSpace(requestId.Value)
                || requestId.Value.Any(char.IsControl)
                || requestId.Value.Length > 256))
        {
            throw new ArgumentException(
                "A capability request identifier must be printable and bounded.",
                nameof(capabilityRequestId));
        }

        return new AgentRunPolicyTransitionDetails(
            runId,
            transition,
            policyGeneration,
            targetIdentityDigest,
            yoloExpiresAtUtc,
            capabilityRequestId);
    }

    public static AuditDetails ForAgentCapabilityRequest(
        AgentRunId runId,
        AgentCapability capability,
        long policyGeneration,
        AgentActionDigest targetIdentityDigest,
        AgentCapabilityRequestAuditDecision? decision = null)
    {
        AgentRunRegistration.ValidateRunId(runId);
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
        if (string.IsNullOrWhiteSpace(targetIdentityDigest.Value))
        {
            throw new ArgumentException(
                "A capability-request audit record requires a target digest.",
                nameof(targetIdentityDigest));
        }

        if (decision is { } value && !Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        return new AgentCapabilityRequestDetails(
            runId,
            capability,
            policyGeneration,
            targetIdentityDigest,
            decision);
    }

    public sealed record EmptyDetails : AuditDetails
    {
        internal EmptyDetails()
        {
        }
    }

    public sealed record SecretAccessDetails : AuditDetails
    {
        internal SecretAccessDetails(
            SecretUseKind purposeKind,
            SecretVaultErrorCode? errorCode)
        {
            PurposeKind = purposeKind;
            ErrorCode = errorCode;
        }

        public SecretUseKind PurposeKind { get; }

        public SecretVaultErrorCode? ErrorCode { get; }
    }

    public sealed record TerminalStartupCommandDetails : AuditDetails
    {
        internal TerminalStartupCommandDetails(
            int commandCount,
            TerminalStartupCommandDispatchErrorCode? errorCode)
        {
            CommandCount = commandCount;
            ErrorCode = errorCode;
        }

        public int CommandCount { get; }

        public TerminalStartupCommandDispatchErrorCode? ErrorCode { get; }
    }

    public sealed record AgentActionDetails : AuditDetails
    {
        internal AgentActionDetails(
            AgentRunId runId,
            AgentCapability capability,
            AgentActionRisk risk,
            AgentPermission permission,
            AgentPolicyDecision decision,
            AgentActionDigest argumentDigest,
            AgentAuthorizationSource? authorizationSource,
            AgentAuthorizationErrorCode? errorCode,
            string? resultCode,
            AgentActionAuditBinding binding)
        {
            RunId = runId;
            Capability = capability;
            Risk = risk;
            Permission = permission;
            Decision = decision;
            ArgumentDigest = argumentDigest;
            AuthorizationSource = authorizationSource;
            ErrorCode = errorCode;
            ResultCode = resultCode;
            Binding = binding;
        }

        public AgentRunId RunId { get; }

        public AgentCapability Capability { get; }

        public AgentActionRisk Risk { get; }

        public AgentPermission Permission { get; }

        public AgentPolicyDecision Decision { get; }

        public AgentActionDigest ArgumentDigest { get; }

        public AgentAuthorizationSource? AuthorizationSource { get; }

        public AgentAuthorizationErrorCode? ErrorCode { get; }

        public string? ResultCode { get; }

        public AgentActionAuditBinding Binding { get; }
    }

    public sealed record AgentRunPolicyTransitionDetails : AuditDetails
    {
        internal AgentRunPolicyTransitionDetails(
            AgentRunId runId,
            AgentRunPolicyTransition transition,
            long policyGeneration,
            AgentActionDigest targetIdentityDigest,
            DateTimeOffset? yoloExpiresAtUtc,
            AgentCapabilityRequestId? capabilityRequestId)
        {
            RunId = runId;
            Transition = transition;
            PolicyGeneration = policyGeneration;
            TargetIdentityDigest = targetIdentityDigest;
            YoloExpiresAtUtc = yoloExpiresAtUtc;
            CapabilityRequestId = capabilityRequestId;
        }

        public AgentRunId RunId { get; }

        public AgentRunPolicyTransition Transition { get; }

        public long PolicyGeneration { get; }

        public AgentActionDigest TargetIdentityDigest { get; }

        public DateTimeOffset? YoloExpiresAtUtc { get; }

        public AgentCapabilityRequestId? CapabilityRequestId { get; }
    }

    public sealed record AgentCapabilityRequestDetails : AuditDetails
    {
        internal AgentCapabilityRequestDetails(
            AgentRunId runId,
            AgentCapability capability,
            long policyGeneration,
            AgentActionDigest targetIdentityDigest,
            AgentCapabilityRequestAuditDecision? decision)
        {
            RunId = runId;
            Capability = capability;
            PolicyGeneration = policyGeneration;
            TargetIdentityDigest = targetIdentityDigest;
            Decision = decision;
        }

        public AgentRunId RunId { get; }

        public AgentCapability Capability { get; }

        public long PolicyGeneration { get; }

        public AgentActionDigest TargetIdentityDigest { get; }

        public AgentCapabilityRequestAuditDecision? Decision { get; }
    }
}

/// <summary>
/// Secret-free evidence tying an audit phase to the exact policy, target,
/// approval, authorization, and execution interval involved.
/// </summary>
public sealed record AgentActionAuditBinding
{
    public static AgentActionAuditBinding Empty { get; } = new();

    public AgentActionAuditBinding(
        long? policyGeneration = null,
        AgentActionDigest? targetIdentity = null,
        AgentActionDigest? approvalIdDigest = null,
        AgentApprovalDuration? approvalDuration = null,
        AgentActionDigest? authorizationIdDigest = null,
        DateTimeOffset? authorityExpiresAtUtc = null,
        long? executionDurationMilliseconds = null,
        int? resultCount = null,
        string? artifactReference = null)
    {
        if (policyGeneration is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policyGeneration));
        }

        if (approvalDuration is { } duration && !Enum.IsDefined(duration))
        {
            throw new ArgumentOutOfRangeException(nameof(approvalDuration));
        }

        if (authorityExpiresAtUtc is { Offset: var offset }
            && offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An audited authority expiry must be UTC.",
                nameof(authorityExpiresAtUtc));
        }

        if (executionDurationMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(executionDurationMilliseconds));
        }

        if (resultCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resultCount));
        }

        if (artifactReference is { } reference
            && (reference.Length is 0 or > 256
                || reference.Any(character =>
                    character is not (>= 'a' and <= 'z')
                        and not (>= '0' and <= '9')
                        and not '.'
                        and not '_'
                        and not '-')))
        {
            throw new ArgumentException(
                "An audit artifact reference must be a bounded stable identifier.",
                nameof(artifactReference));
        }

        PolicyGeneration = policyGeneration;
        TargetIdentity = targetIdentity;
        ApprovalIdDigest = approvalIdDigest;
        ApprovalDuration = approvalDuration;
        AuthorizationIdDigest = authorizationIdDigest;
        AuthorityExpiresAtUtc = authorityExpiresAtUtc;
        ExecutionDurationMilliseconds = executionDurationMilliseconds;
        ResultCount = resultCount;
        ArtifactReference = artifactReference;
    }

    public long? PolicyGeneration { get; }

    public AgentActionDigest? TargetIdentity { get; }

    public AgentActionDigest? ApprovalIdDigest { get; }

    public AgentApprovalDuration? ApprovalDuration { get; }

    public AgentActionDigest? AuthorizationIdDigest { get; }

    public DateTimeOffset? AuthorityExpiresAtUtc { get; }

    public long? ExecutionDurationMilliseconds { get; }

    public int? ResultCount { get; }

    public string? ArtifactReference { get; }

    public AgentActionAuditBinding WithExecutionDuration(TimeSpan duration) =>
        new(
            PolicyGeneration,
            TargetIdentity,
            ApprovalIdDigest,
            ApprovalDuration,
            AuthorizationIdDigest,
            AuthorityExpiresAtUtc,
            checked((long)Math.Max(0, duration.TotalMilliseconds)),
            ResultCount,
            ArtifactReference);

    public AgentActionAuditBinding WithResultCount(int? resultCount) =>
        new(
            PolicyGeneration,
            TargetIdentity,
            ApprovalIdDigest,
            ApprovalDuration,
            AuthorizationIdDigest,
            AuthorityExpiresAtUtc,
            ExecutionDurationMilliseconds,
            resultCount,
            ArtifactReference);
}
