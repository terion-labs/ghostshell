using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Establishes the live authorization state for one in-process agent run.
/// The broker owns the supplied policy after registration; action callers do
/// not get to present a policy at request or execution time.
/// </summary>
public sealed record AgentRunRegistration
{
    public AgentRunRegistration(
        AgentRunId runId,
        ActorDescriptor agent,
        ClientId approvingClientId,
        AgentTarget target,
        AgentPolicy policy,
        long policyGeneration,
        AgentYoloConfirmation? yoloConfirmation = null)
    {
        ValidateRunId(runId);
        Agent = ValidateAgent(agent);
        if (string.IsNullOrWhiteSpace(approvingClientId.Value))
        {
            throw new ArgumentException(
                "A registered agent run requires an authenticated approving client.",
                nameof(approvingClientId));
        }

        ApprovingClientId = approvingClientId;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Policy = ValidatePolicy(policy);
        ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
        RunId = runId;
        PolicyGeneration = policyGeneration;
        YoloConfirmation = yoloConfirmation;
    }

    public AgentRunId RunId { get; }

    public ActorDescriptor Agent { get; }

    public ClientId ApprovingClientId { get; }

    public AgentTarget Target { get; }

    public AgentPolicy Policy { get; }

    public long PolicyGeneration { get; }

    public AgentYoloConfirmation? YoloConfirmation { get; }

    internal static AgentPolicy ValidatePolicy(AgentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsStructurallyValid())
        {
            throw new ArgumentException(
                "A registered agent run requires a structurally valid policy.",
                nameof(policy));
        }

        return policy;
    }

    internal static ActorDescriptor ValidateAgent(ActorDescriptor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != ActorKind.Agent
            || !IsBoundedText(actor.Id.Value, 256)
            || !IsBoundedText(actor.DisplayName, 256))
        {
            throw new ArgumentException(
                "A registered agent run requires an authenticated agent actor.",
                nameof(actor));
        }

        return actor;
    }

    internal static void ValidateRunId(AgentRunId runId)
    {
        if (!IsBoundedText(runId.Value, 256))
        {
            throw new ArgumentException(
                "An agent run identifier must be printable and bounded.",
                nameof(runId));
        }
    }

    private static bool IsBoundedText(string? value, int maximumBytes) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => !char.IsControl(character))
        && Encoding.UTF8.GetByteCount(value) <= maximumBytes;
}

/// <summary>
/// Replaces the effective policy for an already registered run. Generations
/// must increase so pending and issued one-action authority can be revoked
/// deterministically.
/// </summary>
public sealed record AgentRunPolicyUpdate
{
    public AgentRunPolicyUpdate(
        AgentRunId runId,
        AgentPolicy policy,
        long policyGeneration,
        ActorDescriptor changedBy,
        AgentYoloConfirmation? yoloConfirmation = null,
        AgentCapabilityRequestId? capabilityRequestId = null)
    {
        AgentRunRegistration.ValidateRunId(runId);
        Policy = AgentRunRegistration.ValidatePolicy(policy);
        ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
        ChangedBy = ValidateHuman(changedBy, nameof(changedBy));
        RunId = runId;
        PolicyGeneration = policyGeneration;
        YoloConfirmation = yoloConfirmation;
        CapabilityRequestId = capabilityRequestId;
    }

    public AgentRunId RunId { get; }

    public AgentPolicy Policy { get; }

    public long PolicyGeneration { get; }

    public ActorDescriptor ChangedBy { get; }

    public AgentYoloConfirmation? YoloConfirmation { get; }

    public AgentCapabilityRequestId? CapabilityRequestId { get; }

    internal static ActorDescriptor ValidateHuman(
        ActorDescriptor actor,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(actor, parameterName);
        if (actor.Kind != ActorKind.Human
            || actor.ClientId is not { } clientId
            || !string.Equals(actor.Id.Value, clientId.Value
, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(actor.Id.Value)
            || string.IsNullOrWhiteSpace(actor.DisplayName)
            || actor.Id.Value.Any(char.IsControl)
            || actor.DisplayName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A policy change requires an authenticated human client.",
                parameterName);
        }

        return actor;
    }
}

public sealed record AgentRunCancellation
{
    public AgentRunCancellation(
        AgentRunId runId,
        ActorDescriptor actor,
        string stableReasonCode,
        DateTimeOffset requestedAtUtc)
    {
        AgentRunRegistration.ValidateRunId(runId);
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind is not (ActorKind.Human or ActorKind.Agent)
            || actor.Kind == ActorKind.Human
                && (actor.ClientId is not { } clientId
                    || !string.Equals(actor.Id.Value, clientId.Value, StringComparison.Ordinal))
            || string.IsNullOrWhiteSpace(actor.Id.Value)
            || string.IsNullOrWhiteSpace(actor.DisplayName))
        {
            throw new ArgumentException(
                "Run cancellation requires an authenticated actor.",
                nameof(actor));
        }

        RequireStableCode(stableReasonCode, nameof(stableReasonCode));
        if (requestedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A run-cancellation timestamp must be UTC.",
                nameof(requestedAtUtc));
        }

        RunId = runId;
        Actor = actor;
        StableReasonCode = stableReasonCode;
        RequestedAtUtc = requestedAtUtc;
    }

    public AgentRunId RunId { get; }

    public ActorDescriptor Actor { get; }

    public string StableReasonCode { get; }

    public DateTimeOffset RequestedAtUtc { get; }

    private static void RequireStableCode(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'
                    and not '-'))
        {
            throw new ArgumentException(
                "A cancellation reason must be a bounded stable identifier.",
                parameterName);
        }
    }
}

/// <summary>
/// Evidence that the local user explicitly enabled high-risk YOLO authority
/// for one run, one target scope, and one policy generation. Authority ends
/// with the run or an explicitly requested earlier expiry.
/// </summary>
public sealed record AgentYoloConfirmation
{
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(1);
    public static readonly DateTimeOffset RunLifetimeExpiry = DateTimeOffset.MaxValue;

    public AgentYoloConfirmation(
        AgentRunId runId,
        AgentTarget target,
        long policyGeneration,
        ActorDescriptor confirmedBy,
        DateTimeOffset confirmedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        AgentRunRegistration.ValidateRunId(runId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegative(policyGeneration);
        ConfirmedBy = ValidateHuman(confirmedBy);
        var expiresWithRun = expiresAtUtc == RunLifetimeExpiry;
        if (confirmedAtUtc.Offset != TimeSpan.Zero
            || expiresAtUtc.Offset != TimeSpan.Zero
            || expiresAtUtc <= confirmedAtUtc
            || (!expiresWithRun
                && expiresAtUtc - confirmedAtUtc > MaximumLifetime))
        {
            throw new ArgumentException(
                "A full-access confirmation requires ordered UTC timestamps within its run or bounded lifetime.",
                nameof(expiresAtUtc));
        }

        RunId = runId;
        TargetIdentity = AgentTargetIdentity.Create(target);
        PolicyGeneration = policyGeneration;
        ConfirmedAtUtc = confirmedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public AgentRunId RunId { get; }

    public AgentActionDigest TargetIdentity { get; }

    public long PolicyGeneration { get; }

    public ActorDescriptor ConfirmedBy { get; }

    public DateTimeOffset ConfirmedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    internal void ValidateFor(
        AgentRunId runId,
        AgentTarget target,
        long policyGeneration,
        DateTimeOffset now,
        ClientId approvingClientId)
    {
        if (RunId != runId
            || TargetIdentity != AgentTargetIdentity.Create(target)
            || PolicyGeneration != policyGeneration
            || ConfirmedBy.ClientId != approvingClientId
            || ExpiresAtUtc <= now
            || ConfirmedAtUtc > now)
        {
            throw new ArgumentException(
                "The YOLO confirmation does not match the live run, target, generation, and time window.");
        }
    }

    private static ActorDescriptor ValidateHuman(ActorDescriptor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != ActorKind.Human
            || actor.ClientId is not { } clientId
            || !string.Equals(actor.Id.Value, clientId.Value
, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(actor.Id.Value)
            || string.IsNullOrWhiteSpace(actor.DisplayName))
        {
            throw new ArgumentException(
                "YOLO requires explicit confirmation by an authenticated human.",
                nameof(actor));
        }

        return actor;
    }
}

internal static class AgentTargetScope
{
    public static bool Contains(AgentTarget scope, AgentTarget requested)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(requested);
        return scope switch
        {
            AgentTarget.Panel panel => requested is AgentTarget.Panel candidate
                && candidate == panel,
            AgentTarget.ConnectionSession session =>
                requested is AgentTarget.ConnectionSession candidate
                && candidate == session,
            AgentTarget.OpenTab tab => Contains(tab, requested),
            AgentTarget.Workspace workspace => Contains(workspace, requested),
            AgentTarget.SelectedPanels selected => Contains(selected, requested),
            _ => false,
        };
    }

    private static bool Contains(AgentTarget.OpenTab scope, AgentTarget requested) =>
        requested switch
        {
            AgentTarget.OpenTab tab => tab == scope,
            AgentTarget.Panel panel =>
                panel.WindowId == scope.WindowId
                && panel.WorkspaceId == scope.WorkspaceId
                && panel.TabId == scope.TabId,
            AgentTarget.SelectedPanels selected => selected.Panels.All(panel =>
                panel.WindowId == scope.WindowId
                && panel.WorkspaceId == scope.WorkspaceId
                && panel.TabId == scope.TabId),
            _ => false,
        };

    private static bool Contains(AgentTarget.Workspace scope, AgentTarget requested) =>
        requested switch
        {
            AgentTarget.Workspace workspace => workspace == scope,
            AgentTarget.OpenTab tab =>
                tab.WindowId == scope.WindowId
                && tab.WorkspaceId == scope.WorkspaceId,
            AgentTarget.Panel panel =>
                panel.WindowId == scope.WindowId
                && panel.WorkspaceId == scope.WorkspaceId,
            AgentTarget.SelectedPanels selected => selected.Panels.All(panel =>
                panel.WindowId == scope.WindowId
                && panel.WorkspaceId == scope.WorkspaceId),
            _ => false,
        };

    private static bool Contains(AgentTarget.SelectedPanels scope, AgentTarget requested)
    {
        var allowed = scope.Panels
            .Select(panel => panel.PanelId)
            .ToHashSet();
        return requested switch
        {
            AgentTarget.Panel panel => allowed.Contains(panel.PanelId)
                && scope.Panels.Any(candidate => candidate == panel),
            AgentTarget.SelectedPanels selected => selected.Panels.All(panel =>
                allowed.Contains(panel.PanelId)
                && scope.Panels.Any(candidate => candidate == panel)),
            _ => false,
        };
    }
}
