using Asura.Application;
using Asura.Core;

namespace Asura.App;

/// <summary>
/// Presents recent-session persistence as a process-lifecycle API. It owns timestamps, reconciles
/// sessions left active by the previous process, and accepts only the closed metadata shape that
/// is safe to retain as history.
/// </summary>
public sealed class RecentSessionHistory
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly IRecentSessionStore _store;
    private readonly IRecentSessionRetentionStore? _retentionStore;
    private readonly TimeProvider _timeProvider;
    private RecentSessionStoreResult<int>? _successfulInitialization;

    public RecentSessionHistory(
        IRecentSessionStore store,
        TimeProvider? timeProvider = null,
        IRecentSessionRetentionStore? retentionStore = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retentionStore = retentionStore ?? store as IRecentSessionRetentionStore;
    }

    public bool SupportsRetentionSettings => _retentionStore is not null;

    /// <summary>
    /// Marks stale active rows from the previous process as interrupted. A successful result is
    /// cached for this adapter instance; failures remain retryable.
    /// </summary>
    public async ValueTask<RecentSessionStoreResult<int>> InitializeAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _successfulInitialization) is { } initialized)
        {
            return initialized;
        }

        try
        {
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RecentSessionStoreResult<int>.Failure(new RecentSessionStoreError(
                RecentSessionStoreErrorCode.Cancelled,
                "Reconciling interrupted recent sessions was cancelled."));
        }

        try
        {
            if (_successfulInitialization is { } completed)
            {
                return completed;
            }

            var result = await _store.MarkActiveSessionsInterruptedAsync(cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Volatile.Write(ref _successfulInitialization, result);
            }

            return result;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    /// <summary>
    /// Records a definition-backed session. The title must be the durable definition's display
    /// name, never a terminal title, command, output fragment, credential, or secret value.
    /// </summary>
    public RecentSessionRecord CaptureStarted(
        SessionId sessionId,
        DefinitionKey sourceDefinition,
        PanelKind kind,
        string durableDefinitionTitle) =>
        new(
            sessionId,
            sourceDefinition,
            kind,
            durableDefinitionTitle,
            _timeProvider.GetUtcNow(),
            endedAt: null,
            RecentSessionOutcome.Active);

    public ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
        SessionId sessionId,
        DefinitionKey sourceDefinition,
        PanelKind kind,
        string durableDefinitionTitle,
        CancellationToken cancellationToken) =>
        RecordStartedAsync(
            CaptureStarted(sessionId, sourceDefinition, kind, durableDefinitionTitle),
            cancellationToken);

    public async ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
        RecentSessionRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Outcome != RecentSessionOutcome.Active || record.EndedAt is not null)
        {
            throw new ArgumentException(
                "A started recent-session record must be active and have no end timestamp.",
                nameof(record));
        }

        if (!await IsRetainingAsync(cancellationToken).ConfigureAwait(false))
        {
            return RecentSessionStoreResult<Unit>.Success(default);
        }

        var initialization = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!initialization.IsSuccess)
        {
            return RecentSessionStoreResult<Unit>.Failure(initialization.Error!);
        }

        return await _store.RecordStartedAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether session metadata is being retained at all.
    ///
    /// Retention of zero records is not "write it and prune it afterwards" — it
    /// is "do not write it". A row that exists between the insert and the next
    /// prune is a row that was on disk, which is the thing someone turning this
    /// off is asking not to happen.
    ///
    /// A store that carries no retention settings has none to disobey, and its
    /// own enforcement is the only policy there is; refusing to record for it
    /// would turn a missing capability into silent data loss.
    /// </summary>
    private async ValueTask<bool> IsRetainingAsync(CancellationToken cancellationToken)
    {
        if (_retentionStore is null)
        {
            return true;
        }

        var retention = await GetRetentionAsync(cancellationToken).ConfigureAwait(false);
        return !retention.IsSuccess || retention.Value!.Policy.IsEnabled;
    }

    public RecentSessionCompletion CaptureCompletion(
        SessionId sessionId,
        RecentSessionOutcome outcome)
    {
        if (!IsAllowedCompletion(outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                "A current session can only be completed with an allowlisted terminal outcome.");
        }

        return new RecentSessionCompletion(sessionId, _timeProvider.GetUtcNow(), outcome);
    }

    public ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
        SessionId sessionId,
        RecentSessionOutcome outcome,
        CancellationToken cancellationToken) =>
        RecordCompletedAsync(
            CaptureCompletion(sessionId, outcome),
            cancellationToken);

    public async ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
        RecentSessionCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (!IsAllowedCompletion(completion.Outcome))
        {
            throw new ArgumentOutOfRangeException(
                nameof(completion),
                "A current session can only be completed with an allowlisted terminal outcome.");
        }

        // Nothing was written when it started, so there is nothing to complete.
        if (!await IsRetainingAsync(cancellationToken).ConfigureAwait(false))
        {
            return RecentSessionStoreResult<Unit>.Success(default);
        }

        return await _store.RecordCompletedAsync(completion, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>>
        ListRecentAsync(
            int limit,
            CancellationToken cancellationToken)
    {
        var query = new RecentSessionQuery(limit);
        var initialization = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!initialization.IsSuccess)
        {
            return RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Failure(
                initialization.Error!);
        }

        return await _store.ListRecentAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Capture when the user confirms a selective clear, then retain this value until the clear
    /// reaches persistence. This prevents a delayed operation from deleting newer completions.
    /// </summary>
    public RecentSessionClearCutoff CaptureClearCutoff() =>
        new(_timeProvider.GetUtcNow());

    public ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
        RecentSessionClearCutoff cutoff,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cutoff);
        return _store.ClearThroughAsync(cutoff.ThroughUtc, cancellationToken);
    }

    /// <summary>
    /// Unconditionally purges retained history, including malformed rows that cannot be listed.
    /// Callers must present this as a distinct recovery action rather than a selective clear.
    /// </summary>
    public ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
        CancellationToken cancellationToken) =>
        _store.ClearAllAsync(cancellationToken);

    public ValueTask<RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>>
        GetRetentionAsync(CancellationToken cancellationToken) =>
        _retentionStore is { } retentionStore
            ? retentionStore.GetRetentionAsync(cancellationToken)
            : ValueTask.FromResult(RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>
                .Failure(new RecentSessionStoreError(
                    RecentSessionStoreErrorCode.StorageUnavailable,
                    "Recent-session retention settings are unavailable.")));

    public ValueTask<RecentSessionStoreResult<RecentSessionRetentionUpdateResult>>
        UpdateRetentionAsync(
            RecentSessionRetentionPolicy policy,
            long expectedRevision,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return _retentionStore is { } retentionStore
            ? retentionStore.UpdateRetentionAsync(
                policy,
                expectedRevision,
                cancellationToken)
            : ValueTask.FromResult(RecentSessionStoreResult<RecentSessionRetentionUpdateResult>
                .Failure(new RecentSessionStoreError(
                    RecentSessionStoreErrorCode.StorageUnavailable,
                    "Recent-session retention settings are unavailable.")));
    }

    private static bool IsAllowedCompletion(RecentSessionOutcome outcome) => outcome is
        RecentSessionOutcome.GracefullyClosed
        or RecentSessionOutcome.ForceTerminated
        or RecentSessionOutcome.Failed
        or RecentSessionOutcome.Cancelled;
}
