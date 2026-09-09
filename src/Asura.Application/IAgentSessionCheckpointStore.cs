using Asura.Core;

namespace Asura.Application;

public sealed record AgentSessionCheckpointSummary(
    AgentRunId RunId,
    int SchemaVersion,
    long Generation,
    long Revision,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable storage for versioned, idle native-agent checkpoints. This port
/// deliberately stores no provider client, approval, authority, or secret
/// material; the kernel-owned payload is data only.
/// </summary>
public interface IAgentSessionCheckpointStore
{
    ValueTask<AgentSessionCheckpointStoreResult<Unit>> SaveAsync(
        AgentSessionCheckpoint checkpoint,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<AgentSessionCheckpoint>> LoadAsync(
        AgentRunId runId,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<bool>> DeleteAsync(
        AgentRunId runId,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<IReadOnlyList<AgentSessionCheckpointSummary>>>
        ListAsync(int maximumCount, CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<Unit>> SaveAsync(
        AgentConversationScopeId conversationScopeId,
        AgentSessionCheckpoint checkpoint,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<AgentSessionCheckpoint>> LoadAsync(
        AgentConversationScopeId conversationScopeId,
        AgentRunId runId,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<bool>> DeleteAsync(
        AgentConversationScopeId conversationScopeId,
        AgentRunId runId,
        CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<IReadOnlyList<AgentSessionCheckpointSummary>>>
        ListAsync(
            AgentConversationScopeId conversationScopeId,
            int maximumCount,
            CancellationToken cancellationToken);

    ValueTask<AgentSessionCheckpointStoreResult<Unit>> SaveHistoryMetadataAsync(
        AgentConversationScopeId? conversationScopeId,
        AgentRunHistoryMetadata metadata,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(AgentSessionCheckpointStoreResult<Unit>.Success(Unit.Value));

    ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryMetadata>>
        LoadHistoryMetadataAsync(
            AgentConversationScopeId? conversationScopeId,
            AgentRunId runId,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AgentSessionCheckpointStoreResult<AgentRunHistoryMetadata>.Failure(
                new AgentSessionCheckpointStoreError(
                    AgentSessionCheckpointStoreErrorCode.NotFound,
                    "Agent history metadata was not found.")));

    ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>>
        GetHistoryRetentionAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>.Failure(
                new AgentSessionCheckpointStoreError(
                    AgentSessionCheckpointStoreErrorCode.StorageFailure,
                    "Agent history retention is unavailable.")));

    ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>>
        UpdateHistoryRetentionAsync(
            AgentConversationScopeId? conversationScopeId,
            AgentRunHistoryRetention expected,
            int maximumRuns,
            TimeSpan maximumAge,
            AgentRunId? protectedRunId,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AgentSessionCheckpointStoreResult<AgentRunHistoryRetention>.Failure(
                new AgentSessionCheckpointStoreError(
                    AgentSessionCheckpointStoreErrorCode.StorageFailure,
                    "Agent history retention is unavailable.")));

    ValueTask<AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>>
        ExportHistoryAsync(
            AgentConversationScopeId? conversationScopeId,
            Stream destination,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            AgentSessionCheckpointStoreResult<AgentRunHistoryExportReceipt>.Failure(
                new AgentSessionCheckpointStoreError(
                    AgentSessionCheckpointStoreErrorCode.StorageFailure,
                    "Agent history export is unavailable.")));
}
