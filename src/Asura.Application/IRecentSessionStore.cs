namespace Asura.Application;

public interface IRecentSessionStore
{
    /// <summary>
    /// Records metadata captured from a durable definition when its runtime session starts.
    /// Replaying the same record is idempotent.
    /// </summary>
    ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
        RecentSessionRecord recentSession,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a terminal outcome without recreating history that the user already cleared.
    /// </summary>
    ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
        RecentSessionCompletion completion,
        CancellationToken cancellationToken);

    ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>> ListRecentAsync(
        RecentSessionQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reconciles active records left by an earlier host process. Call before recording
    /// sessions for the new process so current sessions are never marked interrupted.
    /// </summary>
    ValueTask<RecentSessionStoreResult<int>> MarkActiveSessionsInterruptedAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears only records visible at or before a caller-captured confirmation timestamp,
    /// so sessions completed concurrently after confirmation are retained.
    /// </summary>
    ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
        DateTimeOffset through,
        CancellationToken cancellationToken);

    /// <summary>
    /// Unconditionally removes the history table's contents, including malformed rows
    /// that cannot be selected safely. This does not close or otherwise mutate sessions.
    /// </summary>
    ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
        CancellationToken cancellationToken);
}
