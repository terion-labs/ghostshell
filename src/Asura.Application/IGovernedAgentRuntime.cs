using System.Collections.Immutable;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Presentation seam for one broker-governed agent run. Provider output remains
/// inert until this runtime prepares a closed action and the capability broker
/// authorizes it.
/// </summary>
public interface IGovernedAgentRuntime : IDisposable, IAsyncDisposable
{
    event EventHandler? Changed;

    GovernedAgentSnapshot Snapshot { get; }

    ValueTask RestoreLatestConversationAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    ValueTask<bool> StartNewConversationAsync(CancellationToken cancellationToken) =>
        ClearAsync(cancellationToken);

    ValueTask<bool> OpenConversationAsync(
        AgentRunId runId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    ValueTask<bool> DeleteConversationAsync(
        AgentRunId runId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    ValueTask<bool> ForkConversationAsync(
        AgentConversationForkPoint forkPoint,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>>
        GetHistoryRetentionAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>.Failure(
                new AgentSessionCheckpointStoreError(
                    AgentSessionCheckpointStoreErrorCode.StorageFailure,
                    "Agent history retention is unavailable.")));

    ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>>
        UpdateHistoryRetentionAsync(
            AgentRunHistoryRetention expected,
            int maximumRuns,
            TimeSpan maximumAge,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>.Failure(
                new AgentSessionCheckpointStoreError(
                    AgentSessionCheckpointStoreErrorCode.StorageFailure,
                    "Agent history retention is unavailable.")));

    ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>>
        ExportHistoryAsync(
            Stream destination,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>.Failure(
                new AgentSessionCheckpointStoreError(
                    AgentSessionCheckpointStoreErrorCode.StorageFailure,
                    "Agent history export is unavailable.")));

    ValueTask<GovernedAgentSendResult> SendAsync(
        GovernedAgentPrompt request,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentSteeringResult> SteerAsync(
        GovernedAgentSteering request,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentFollowUpResult> QueueFollowUpAsync(
        GovernedAgentFollowUp request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new GovernedAgentFollowUpResult(
                false,
                "agent_follow_up_unavailable",
                "This agent runtime does not support queued follow-ups.",
                0));
    }

    ValueTask<GovernedAgentFollowUpResult> UpdateQueuedFollowUpAsync(
        AgentQueuedFollowUpId id,
        GovernedAgentFollowUp request,
        CancellationToken cancellationToken) =>
        UnsupportedQueuedFollowUpMutation(request, cancellationToken);

    ValueTask<GovernedAgentFollowUpResult> RemoveQueuedFollowUpAsync(
        AgentQueuedFollowUpId id,
        CancellationToken cancellationToken) =>
        UnsupportedQueuedFollowUpMutation(null, cancellationToken);

    ValueTask<GovernedAgentFollowUpResult> MoveQueuedFollowUpAsync(
        AgentQueuedFollowUpId id,
        int newIndex,
        CancellationToken cancellationToken) =>
        UnsupportedQueuedFollowUpMutation(null, cancellationToken);

    ValueTask<GovernedAgentFollowUpResult> SteerQueuedFollowUpAsync(
        AgentQueuedFollowUpId id,
        CancellationToken cancellationToken) =>
        UnsupportedQueuedFollowUpMutation(null, cancellationToken);

    private static ValueTask<GovernedAgentFollowUpResult>
        UnsupportedQueuedFollowUpMutation(
            GovernedAgentFollowUp? request,
            CancellationToken cancellationToken)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new GovernedAgentFollowUpResult(
                false,
                "agent_follow_up_unavailable",
                "This agent runtime does not support queued follow-up changes.",
                0));
    }

    ValueTask<GovernedAgentDecisionResult> DecideAsync(
        AgentApprovalId approvalId,
        bool approved,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentQuestionResponseResult> RespondToQuestionAsync(
        AgentQuestionId questionId,
        GovernedAgentQuestionResponse response,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentCapabilityDecisionResult> DecideCapabilityRequestAsync(
        AgentCapabilityRequestId requestId,
        GovernedAgentCapabilityDecision decision,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentActionCancellationResult> CancelActiveActionAsync(
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentStopResult> StopAsync(
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentPolicyResult> EnableYoloAsync(
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    ValueTask<GovernedAgentPolicyResult> EnableFullAccessAsync(
        CancellationToken cancellationToken) =>
        EnableYoloAsync(AgentYoloConfirmation.MaximumLifetime, cancellationToken);

    ValueTask<GovernedAgentPolicyResult> DisableYoloAsync(
        CancellationToken cancellationToken);

    ValueTask<bool> ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Trusted local-human identity bound by the desktop composition root. Agent
/// prompts and model output never get to assert an approving client identity.
/// </summary>
public interface IAgentApprovalPrincipal
{
    ActorDescriptor Actor { get; }
}

public enum GovernedAgentState
{
    Ready,
    StreamingProvider,
    AwaitingUserInput,
    AwaitingCapabilityDecision,
    AwaitingApproval,
    RunningTool,
    Cancelling,
    Failed,
    Cancelled,
}

public enum AgentApprovalMode
{
    Ask,
    FullAccess,
}

public sealed record GovernedAgentPrompt
{
    public const int MaximumMessageLength = 64 * 1024;

    /// <summary>
    /// Carries the user's approval-mode selection into initial run
    /// registration. The runtime binds full access to the run ID and inspected
    /// target; provider output cannot populate this value.
    /// </summary>
    public GovernedAgentPrompt(
        AiProviderProfileId providerId,
        string message,
        AgentTarget target,
        IReadOnlyList<AgentImageAttachment> images,
        AgentReasoningEffort reasoningEffort,
        AgentServiceTier serviceTier,
        AgentPolicy policy,
        AgentApprovalMode approvalMode)
        : this(
            providerId,
            message,
            target,
            reasoningEffort,
            serviceTier,
            policy,
            images)
    {
        if (!Enum.IsDefined(approvalMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(approvalMode));
        }

        ApprovalMode = approvalMode;
    }

    /// <summary>
    /// The policy is supplied by trusted desktop runtime provenance. Provider
    /// output and prompt text never participate in its selection.
    /// </summary>
    public GovernedAgentPrompt(
        AiProviderProfileId providerId,
        string message,
        AgentTarget target,
        AgentPolicy policy)
        : this(
            providerId,
            message,
            target,
            AgentReasoningEffort.Automatic,
            AgentServiceTier.Automatic,
            policy,
            images: null)
    {
    }

    public GovernedAgentPrompt(
        AiProviderProfileId providerId,
        string message,
        AgentTarget target,
        AgentReasoningEffort reasoningEffort,
        AgentPolicy policy)
        : this(
            providerId,
            message,
            target,
            reasoningEffort,
            AgentServiceTier.Automatic,
            policy,
            images: null)
    {
    }

    public GovernedAgentPrompt(
        AiProviderProfileId providerId,
        string message,
        AgentTarget target,
        IReadOnlyList<AgentImageAttachment> images,
        AgentReasoningEffort reasoningEffort,
        AgentPolicy policy)
        : this(
            providerId,
            message,
            target,
            reasoningEffort,
            AgentServiceTier.Automatic,
            policy,
            images)
    {
    }

    public GovernedAgentPrompt(
        AiProviderProfileId providerId,
        string message,
        AgentTarget target,
        IReadOnlyList<AgentImageAttachment> images,
        AgentReasoningEffort reasoningEffort,
        AgentServiceTier serviceTier,
        AgentPolicy policy)
        : this(
            providerId,
            message,
            target,
            reasoningEffort,
            serviceTier,
            policy,
            images)
    {
    }

    private GovernedAgentPrompt(
        AiProviderProfileId providerId,
        string message,
        AgentTarget target,
        AgentReasoningEffort reasoningEffort,
        AgentServiceTier serviceTier,
        AgentPolicy policy,
        IReadOnlyList<AgentImageAttachment>? images)
    {
        if (string.IsNullOrWhiteSpace(providerId.Value))
        {
            throw new ArgumentException(
                "An agent prompt requires an AI-provider profile.",
                nameof(providerId));
        }

        ArgumentNullException.ThrowIfNull(message);
        var imageArray = images is null
            ? ImmutableArray<AgentImageAttachment>.Empty
            : [.. images];
        if (imageArray.Any(image => image is null))
        {
            throw new ArgumentException(
                "The image collection cannot contain null values.",
                nameof(images));
        }

        if (string.IsNullOrWhiteSpace(message) && imageArray.Length == 0)
        {
            throw new ArgumentException(
                "An agent prompt requires text or an image.",
                nameof(message));
        }
        if (message.Length > MaximumMessageLength)
        {
            throw new ArgumentException(
                "The agent prompt exceeds its character limit.",
                nameof(message));
        }

        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(reasoningEffort))
        {
            throw new ArgumentOutOfRangeException(nameof(reasoningEffort));
        }
        if (!Enum.IsDefined(serviceTier))
        {
            throw new ArgumentOutOfRangeException(nameof(serviceTier));
        }

        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsValidForDurableStorage())
        {
            throw new ArgumentException(
                "A governed prompt requires a valid durable baseline policy.",
                nameof(policy));
        }

        if (!string.Equals(
                providerId.Value,
                policy.Provider,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The durable agent policy provider must be the exact AI-provider profile identifier.",
                nameof(policy));
        }

        ProviderId = providerId;
        Message = string.Concat(message);
        Target = target;
        ReasoningEffort = reasoningEffort;
        ServiceTier = serviceTier;
        Images = imageArray;
        Policy = AgentPolicyResolver.Resolve(policy);
    }

    public AiProviderProfileId ProviderId { get; }

    /// <summary>
    /// The model selected for this provider turn. Model routing remains separate
    /// from conversation identity and comes from the complete trusted policy.
    /// </summary>
    public string Model => Policy.Model;

    public string Message { get; }

    public AgentTarget Target { get; }

    public AgentReasoningEffort ReasoningEffort { get; }

    public AgentServiceTier ServiceTier { get; }

    public ImmutableArray<AgentImageAttachment> Images { get; }

    /// <summary>
    /// The complete trusted run policy. The runtime never manufactures missing
    /// provider, model, maintenance-route, or permission data.
    /// </summary>
    public AgentPolicy Policy { get; }

    public AgentApprovalMode ApprovalMode { get; } = AgentApprovalMode.Ask;

}

public sealed record GovernedAgentFollowUp
{
    public GovernedAgentFollowUp(
        string message,
        AgentReasoningEffort reasoningEffort = AgentReasoningEffort.Automatic,
        GovernedAgentFollowUpDelivery delivery = GovernedAgentFollowUpDelivery.FollowUp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > GovernedAgentPrompt.MaximumMessageLength)
        {
            throw new ArgumentException(
                "The follow-up exceeds the agent prompt character limit.",
                nameof(message));
        }

        if (!Enum.IsDefined(reasoningEffort))
        {
            throw new ArgumentOutOfRangeException(nameof(reasoningEffort));
        }

        if (!Enum.IsDefined(delivery))
        {
            throw new ArgumentOutOfRangeException(nameof(delivery));
        }

        Message = string.Concat(message);
        ReasoningEffort = reasoningEffort;
        Delivery = delivery;
    }

    public string Message { get; }

    public AgentReasoningEffort ReasoningEffort { get; }

    public GovernedAgentFollowUpDelivery Delivery { get; }
}

public enum GovernedAgentFollowUpDelivery
{
    FollowUp,
    Steering,
}

public sealed record GovernedAgentQueuedFollowUp(
    AgentQueuedFollowUpId Id,
    string Message,
    AgentReasoningEffort ReasoningEffort,
    GovernedAgentFollowUpDelivery Delivery);

public sealed record GovernedAgentApproval(
    AgentApprovalId Id,
    string ToolName,
    string ToolTitle,
    AgentActionRisk Risk,
    AgentPermission Permission,
    AgentTarget Target,
    AgentApprovalPresentation Presentation,
    DateTimeOffset ExpiresAtUtc,
    bool TemporarilyYieldsTerminalInput);

public sealed record GovernedAgentToolActivity(
    string ToolName,
    string ToolTitle,
    AgentActionRisk Risk,
    string TargetTitle,
    bool CancellationRequested = false,
    PanelInstanceId? PanelId = null);

/// <summary>
/// Presentation-safe evidence for one live, run-local YOLO authority window.
/// The capability broker remains authoritative; this value conveys no reusable
/// execution permission.
/// </summary>
public sealed record GovernedAgentYoloAuthority
{
    public GovernedAgentYoloAuthority(
        AgentRunId runId,
        AgentTarget target,
        DateTimeOffset confirmedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        AgentRunRegistration.ValidateRunId(runId);
        ArgumentNullException.ThrowIfNull(target);
        var expiresWithRun = expiresAtUtc == AgentYoloConfirmation.RunLifetimeExpiry;
        if (confirmedAtUtc.Offset != TimeSpan.Zero
            || expiresAtUtc.Offset != TimeSpan.Zero
            || expiresAtUtc <= confirmedAtUtc
            || (!expiresWithRun
                && expiresAtUtc - confirmedAtUtc
                    > AgentYoloConfirmation.MaximumLifetime))
        {
            throw new ArgumentException(
                "A visible full-access authority requires an ordered UTC lifetime.",
                nameof(expiresAtUtc));
        }

        RunId = runId;
        Target = target;
        ConfirmedAtUtc = confirmedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public AgentRunId RunId { get; }

    public AgentTarget Target { get; }

    public DateTimeOffset ConfirmedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }
}

public sealed record GovernedAgentSnapshot(
    GovernedAgentState State,
    AgentRunId? RunId,
    AiProviderProfileId? ProviderId,
    AgentTarget? Target,
    string TargetTitle,
    ImmutableArray<GovernedAgentContextItem> ContextItems,
    IReadOnlyList<AgentChatMessage> Messages,
    AgentPolicy EffectivePolicy,
    string ProvisionalAssistantText,
    string Status,
    GovernedAgentApproval? PendingApproval = null,
    GovernedAgentToolActivity? ActiveTool = null,
    bool TerminalMutationAvailable = false,
    string? CapabilityNotice = null,
    AgentPermission TerminalMutationPermission = AgentPermission.Ask,
    GovernedAgentYoloAuthority? YoloAuthority = null,
    string? ConnectionBoundary = null,
    string? WorkingDirectory = null,
    GovernedAgentProgress? CurrentProgress = null,
    GovernedAgentQuestion? PendingQuestion = null,
    GovernedAgentCapabilityRequest? PendingCapabilityRequest = null,
    bool SteeringAvailable = false,
    long? SteeringGeneration = null,
    string ProvisionalReasoningSummary = "",
    int QueuedFollowUpCount = 0,
    ImmutableArray<GovernedAgentQueuedFollowUp> QueuedFollowUps = default,
    ImmutableArray<GovernedAgentConversationSummary> Conversations = default,
    string? Model = null,
    long? ContextTokensUsed = null,
    GovernedAgentToolActivity? PanelActivity = null,
    AgentRunId? SelectedConversationRunId = null,
    AgentPolicy? BaselinePolicy = null,
    AgentPolicy? RunPolicy = null,
    long PolicyGeneration = 1)
{
    public bool IsBusy => State is
        GovernedAgentState.StreamingProvider
        or GovernedAgentState.AwaitingUserInput
        or GovernedAgentState.AwaitingCapabilityDecision
        or GovernedAgentState.AwaitingApproval
        or GovernedAgentState.RunningTool
        or GovernedAgentState.Cancelling;

    public bool CanSend =>
        State == GovernedAgentState.Ready
        && PendingApproval is null
        && PendingQuestion is null
        && PendingCapabilityRequest is null;

    public bool CanStop => RunId is not null
        && State != GovernedAgentState.Cancelled;

    public bool CanSteer =>
        SteeringAvailable
        && SteeringGeneration is > 0
        && State == GovernedAgentState.StreamingProvider
        && RunId is not null
        && PendingApproval is null
        && PendingQuestion is null
        && PendingCapabilityRequest is null
        && ActiveTool is null;

    public bool CanQueueFollowUp =>
        RunId is not null
        && State is GovernedAgentState.StreamingProvider
            or GovernedAgentState.RunningTool
            or GovernedAgentState.AwaitingApproval
            or GovernedAgentState.AwaitingUserInput
            or GovernedAgentState.AwaitingCapabilityDecision;

    public bool HasMessages =>
        Messages.Count > 0
        || ProvisionalAssistantText.Length > 0
        || ProvisionalReasoningSummary.Length > 0;

    public bool HasYoloAuthority => YoloAuthority is not null;
}

public sealed record GovernedAgentConversationSummary(
    AgentRunId RunId,
    string Title,
    AiProviderProfileId? ProviderId,
    string? Model,
    int MessageCount,
    DateTimeOffset UpdatedAt,
    GovernedAgentConversationAvailability Availability =
        GovernedAgentConversationAvailability.Available);

public enum GovernedAgentConversationAvailability
{
    Available,
    Partial,
    Corrupt,
    Unavailable,
}

public sealed record GovernedAgentSendResult(
    bool IsSuccess,
    string Code,
    string Message,
    bool InitialPromptCommitted = false,
    IReadOnlyList<GovernedAgentFollowUp>? RecoverableFollowUps = null);

public sealed record GovernedAgentSteeringResult(
    bool IsAccepted,
    string Code,
    string Message);

public sealed record GovernedAgentFollowUpResult(
    bool IsAccepted,
    string Code,
    string Message,
    int QueuedCount,
    AgentQueuedFollowUpId? ItemId = null);

public sealed record GovernedAgentDecisionResult(
    bool IsAccepted,
    string Code,
    string Message);

public sealed record GovernedAgentQuestionResponseResult(
    bool IsAccepted,
    string Code,
    string Message);

public sealed record GovernedAgentCapabilityDecisionResult(
    bool IsAccepted,
    string Code,
    string Message);

public sealed record GovernedAgentActionCancellationResult(
    bool WasRequested,
    string Code,
    string Message);

public sealed record GovernedAgentStopResult(
    bool WasRunning,
    string Code,
    string Message);

public sealed record GovernedAgentPolicyResult(
    bool IsAccepted,
    string Code,
    string Message);
