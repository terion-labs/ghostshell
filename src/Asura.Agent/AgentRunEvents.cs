using System.Collections.Immutable;
using System.Text.Json;
using Asura.Core;

namespace Asura.Agent;

public enum NativeAgentSessionState
{
    Ready,
    Streaming,
    AwaitingToolDecision,
    AwaitingProviderContinuation,
    Failed,
    Cancelled,
}

public enum AgentTurnErrorCode
{
    AlreadyRunning,
    ProviderOperationLimit,
    PendingToolDecision,
    Cancelled,
    ProviderFailure,
    InvalidProviderStream,
    LimitExceeded,
    ConversationConflict,
    NoPendingToolDecision,
    StaleToolResults,
    ToolResultMismatch,
}

public sealed class AgentToolProposal
{
    internal AgentToolProposal(
        string id,
        long generation,
        string providerCallId,
        string toolName,
        JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(arguments));
        }

        Id = id;
        Generation = generation;
        ProviderCallId = providerCallId;
        ProviderName = AgentToolDefinition.GetProviderName(toolName);
        ToolName = toolName;
        Arguments = arguments.Clone();
    }

    public string Id { get; }

    public long Generation { get; }

    public string ProviderCallId { get; }

    /// <summary>
    /// The provider-facing alias retained for structured transcript replay.
    /// Runtime dispatch and audit identity use <see cref="ToolName"/>.
    /// </summary>
    public string ProviderName { get; }

    public string ToolName { get; }

    public JsonElement Arguments { get; }

    public bool ContainsUntrustedContent => true;
}

public sealed record AgentTurnResult
{
    private AgentTurnResult(
        bool succeeded,
        AgentTurnErrorCode? errorCode,
        AgentProviderStopReason? stopReason,
        ImmutableArray<AgentToolProposal> toolProposals,
        AgentProviderFailure? providerFailure)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        StopReason = stopReason;
        ToolProposals = toolProposals;
        ProviderFailure = providerFailure;
    }

    public bool Succeeded { get; }

    public AgentTurnErrorCode? ErrorCode { get; }

    public AgentProviderStopReason? StopReason { get; }

    public ImmutableArray<AgentToolProposal> ToolProposals { get; }

    public AgentProviderFailure? ProviderFailure { get; }

    internal static AgentTurnResult Success(
        AgentProviderStopReason stopReason,
        ImmutableArray<AgentToolProposal> toolProposals) =>
        new(true, null, stopReason, toolProposals, null);

    internal static AgentTurnResult Failure(
        AgentTurnErrorCode errorCode,
        AgentProviderException? providerException = null) =>
        new(
            false,
            errorCode,
            null,
            [],
            providerException is null
                ? null
                : new AgentProviderFailure(
                    providerException.StableCode,
                    providerException.PublicMessage));
}

public enum AgentRunEventKind
{
    TurnStarted,
    ProvisionalText,
    ProvisionalReasoningSummary,
    TurnCommitted,
    TurnFailed,
    TurnCancelled,
    ToolProposalsDiscarded,
    ToolResultsCommitted,
    ConversationCompacted,
    ConversationTitleChanged,
    TurnSteered,
    SystemPromptRebased,
    RecoveryCheckpointCaptured,
}

public sealed record AgentRunEvent
{
    internal AgentRunEvent(
        AgentRunId runId,
        long sequence,
        long revision,
        long generation,
        AgentRunEventKind kind,
        DateTimeOffset occurredAt,
        string? provisionalText = null,
        string? provisionalReasoningSummary = null,
        AgentTurnErrorCode? errorCode = null,
        int toolProposalCount = 0)
    {
        RunId = runId;
        Sequence = sequence;
        Revision = revision;
        Generation = generation;
        Kind = kind;
        OccurredAt = occurredAt;
        ProvisionalText = provisionalText;
        ProvisionalReasoningSummary = provisionalReasoningSummary;
        ErrorCode = errorCode;
        ToolProposalCount = toolProposalCount;
    }

    public AgentRunId RunId { get; }

    public long Sequence { get; }

    public long Revision { get; }

    public long Generation { get; }

    public AgentRunEventKind Kind { get; }

    public DateTimeOffset OccurredAt { get; }

    public string? ProvisionalText { get; }

    public string? ProvisionalReasoningSummary { get; }

    public AgentTurnErrorCode? ErrorCode { get; }

    public int ToolProposalCount { get; }

    public bool ContainsUntrustedContent =>
        ProvisionalText is not null || ProvisionalReasoningSummary is not null;
}

public sealed record AgentSessionSnapshot(
    AgentRunId RunId,
    NativeAgentSessionState State,
    long Revision,
    long LastSequence,
    long Generation,
    ImmutableArray<AgentMessage> Conversation,
    ImmutableArray<AgentMessage> Transcript,
    ImmutableArray<AgentToolProposal> PendingToolProposals);

public sealed record AgentEventWatchRequest
{
    public AgentEventWatchRequest(long afterSequence, int maximumBatchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBatchSize);
        AfterSequence = afterSequence;
        MaximumBatchSize = maximumBatchSize;
    }

    public long AfterSequence { get; }

    public int MaximumBatchSize { get; }
}

public abstract record AgentRunStreamItem
{
    private AgentRunStreamItem()
    {
    }

    public sealed record EventBatch(
        ImmutableArray<AgentRunEvent> Events) : AgentRunStreamItem;

    public sealed record ResynchronizationRequired(
        AgentSessionSnapshot Snapshot,
        long ResumeAfterSequence) : AgentRunStreamItem;
}
