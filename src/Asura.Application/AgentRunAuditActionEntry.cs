using System.Collections.ObjectModel;
using Asura.Core;

namespace Asura.Application;

public sealed record AgentRunAuditActionEntry : AgentRunAuditEntry
{
    public AgentRunAuditActionEntry(
        AgentActionDigest entryId,
        string toolName,
        AgentCapability capability,
        AgentActionRisk risk,
        AgentPermission permission,
        AgentPolicyDecision decision,
        AgentAuthorizationSource? authorizationSource,
        AgentAuthorizationErrorCode? errorCode,
        string? resultCode,
        long? policyGeneration,
        AgentActionDigest targetIdentity,
        long? executionDurationMilliseconds,
        int? resultCount,
        IEnumerable<AgentRunAuditPhase> phases)
        : this(
            entryId,
            toolName,
            capability,
            risk,
            permission,
            decision,
            authorizationSource,
            errorCode,
            resultCode,
            policyGeneration,
            targetIdentity,
            executionDurationMilliseconds,
            resultCount,
            RequirePhases(phases))
    {
    }

    private AgentRunAuditActionEntry(
        AgentActionDigest entryId,
        string toolName,
        AgentCapability capability,
        AgentActionRisk risk,
        AgentPermission permission,
        AgentPolicyDecision decision,
        AgentAuthorizationSource? authorizationSource,
        AgentAuthorizationErrorCode? errorCode,
        string? resultCode,
        long? policyGeneration,
        AgentActionDigest targetIdentity,
        long? executionDurationMilliseconds,
        int? resultCount,
        AgentRunAuditPhase[] phases)
        : base(entryId, phases[^1].OccurredAtUtc)
    {
        RequireToolName(toolName);
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

        RequireStableCode(resultCode, nameof(resultCode));
        if (policyGeneration is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policyGeneration));
        }

        if (string.IsNullOrWhiteSpace(targetIdentity.Value))
        {
            throw new ArgumentException(
                "An audited action requires a target-identity digest.",
                nameof(targetIdentity));
        }

        if (executionDurationMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(executionDurationMilliseconds));
        }

        if (resultCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resultCount));
        }

        ToolName = string.Concat(toolName);
        Capability = capability;
        Risk = risk;
        Permission = permission;
        Decision = decision;
        AuthorizationSource = authorizationSource;
        ErrorCode = errorCode;
        ResultCode = resultCode is null ? null : string.Concat(resultCode);
        PolicyGeneration = policyGeneration;
        TargetIdentity = targetIdentity;
        ExecutionDurationMilliseconds = executionDurationMilliseconds;
        ResultCount = resultCount;
        Phases = new ReadOnlyCollection<AgentRunAuditPhase>(phases);
    }

    public string ToolName { get; }

    public AgentCapability Capability { get; }

    public AgentActionRisk Risk { get; }

    public AgentPermission Permission { get; }

    public AgentPolicyDecision Decision { get; }

    public AgentAuthorizationSource? AuthorizationSource { get; }

    public AgentAuthorizationErrorCode? ErrorCode { get; }

    public string? ResultCode { get; }

    public long? PolicyGeneration { get; }

    public AgentActionDigest TargetIdentity { get; }

    public long? ExecutionDurationMilliseconds { get; }

    public int? ResultCount { get; }

    public IReadOnlyList<AgentRunAuditPhase> Phases { get; }

    public AuditOutcome LatestOutcome => Phases[^1].Outcome;

    private static AgentRunAuditPhase[] RequirePhases(
        IEnumerable<AgentRunAuditPhase> phases)
    {
        ArgumentNullException.ThrowIfNull(phases);
        var values = phases
            .Select(phase => phase ?? throw new ArgumentException(
                "An audit action cannot contain a null phase.",
                nameof(phases)))
            .ToArray();
        if (values.Length is < 1 or > 4
            || values[0].Outcome != AuditOutcome.Requested)
        {
            throw new ArgumentException(
                "An audit action requires a bounded sequence beginning with Requested.",
                nameof(phases));
        }

        for (var index = 1; index < values.Length; index++)
        {
            if (values[index].OccurredAtUtc < values[index - 1].OccurredAtUtc
                || !CanTransition(
                    values[index - 1].Outcome,
                    values[index].Outcome))
            {
                throw new ArgumentException(
                    "The audit action phases are not a valid ordered transition sequence.",
                    nameof(phases));
            }
        }

        return values;
    }

    private static bool CanTransition(AuditOutcome current, AuditOutcome next) =>
        current switch
        {
            AuditOutcome.Requested =>
                next is AuditOutcome.Approved or AuditOutcome.Denied,
            AuditOutcome.Approved =>
                next is AuditOutcome.Started or AuditOutcome.Denied,
            AuditOutcome.Started =>
                next is AuditOutcome.Succeeded
                    or AuditOutcome.Failed
                    or AuditOutcome.Cancelled,
            _ => false,
        };

    private static void RequireToolName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || value[0] == '.'
            || value[^1] == '.'
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '.'
                    and not '_'
                    and not '-'))
        {
            throw new ArgumentException(
                "An audit tool name must be a bounded lowercase identifier.",
                nameof(value));
        }
    }

    private static void RequireStableCode(string? value, string parameterName)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length is < 1 or > 128
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'
                    and not '-'))
        {
            throw new ArgumentException(
                "An audit result code must be a bounded stable identifier.",
                parameterName);
        }
    }
}
