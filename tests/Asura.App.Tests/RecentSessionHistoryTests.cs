using System.Collections.Concurrent;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class RecentSessionHistoryTests
{
    private static readonly DateTimeOffset LocalNow = new(
        2026,
        7,
        22,
        15,
        30,
        0,
        TimeSpan.FromHours(3));

    [Fact]
    public async Task Concurrent_starts_reconcile_once_before_recording_safe_metadata()
    {
        var store = new RecordingStore { HoldReconciliation = true };
        var time = new MutableTimeProvider(LocalNow);
        var history = new RecentSessionHistory(store, time);
        var firstId = new SessionId("session-first");
        var secondId = new SessionId("session-second");

        var first = history.RecordStartedAsync(
            firstId,
            new DefinitionKey(DefinitionKind.Connection, "connection-production"),
            PanelKind.Terminal,
            "  Production shell  ",
            CancellationToken.None).AsTask();
        await store.ReconciliationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = history.RecordStartedAsync(
            secondId,
            new DefinitionKey(DefinitionKind.Screen, "screen-operations"),
            PanelKind.FileViewer,
            "Operations screen",
            CancellationToken.None).AsTask();

        Assert.False(second.IsCompleted);
        store.ReleaseReconciliation.TrySetResult();
        var results = await Task.WhenAll(first, second);
        var repeatedInitialization = await history.InitializeAsync(CancellationToken.None);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Same(store.SuccessfulReconciliation, repeatedInitialization);
        Assert.Equal(1, store.ReconciliationCalls);
        Assert.Equal("reconcile", store.Calls.First());
        Assert.Equal(2, store.Started.Count);
        var recordedFirst = Assert.Single(
            store.Started,
            record => record.SessionId == firstId);
        Assert.Equal("Production shell", recordedFirst.Title);
        Assert.Equal(PanelKind.Terminal, recordedFirst.Kind);
        Assert.Equal(DefinitionKind.Connection, recordedFirst.SourceDefinition.Kind);
        Assert.Equal(LocalNow.ToUniversalTime(), recordedFirst.StartedAt);
        Assert.Null(recordedFirst.EndedAt);
        Assert.Equal(RecentSessionOutcome.Active, recordedFirst.Outcome);
    }

    [Fact]
    public async Task Failed_reconciliation_is_returned_unchanged_and_remains_retryable()
    {
        var store = new RecordingStore();
        var reconciliationError = new RecentSessionStoreError(
            RecentSessionStoreErrorCode.StorageUnavailable,
            "History is temporarily unavailable.");
        store.EnqueueReconciliation(
            RecentSessionStoreResult<int>.Failure(reconciliationError));
        var history = new RecentSessionHistory(store, new MutableTimeProvider(LocalNow));

        var failed = await history.RecordStartedAsync(
            new SessionId("session-retry"),
            new DefinitionKey(DefinitionKind.Workspace, "workspace-1"),
            PanelKind.Terminal,
            "Workspace",
            CancellationToken.None);
        var succeeded = await history.RecordStartedAsync(
            new SessionId("session-retry"),
            new DefinitionKey(DefinitionKind.Workspace, "workspace-1"),
            PanelKind.Terminal,
            "Workspace",
            CancellationToken.None);

        Assert.False(failed.IsSuccess);
        Assert.Same(reconciliationError, failed.Error);
        Assert.True(succeeded.IsSuccess);
        Assert.Equal(2, store.ReconciliationCalls);
        Assert.Single(store.Started);
    }

    [Fact]
    public async Task Start_store_failures_cross_the_adapter_unchanged()
    {
        var store = new RecordingStore();
        var expected = RecentSessionStoreResult<Unit>.Failure(new RecentSessionStoreError(
            RecentSessionStoreErrorCode.Conflict,
            "The runtime identifier was already used."));
        store.StartResult = expected;
        var history = new RecentSessionHistory(store, new MutableTimeProvider(LocalNow));

        var result = await history.RecordStartedAsync(
            new SessionId("session-conflict"),
            new DefinitionKey(DefinitionKind.Connection, "connection-1"),
            PanelKind.Terminal,
            "Connection",
            CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Theory]
    [InlineData(RecentSessionOutcome.GracefullyClosed)]
    [InlineData(RecentSessionOutcome.ForceTerminated)]
    [InlineData(RecentSessionOutcome.Failed)]
    [InlineData(RecentSessionOutcome.Cancelled)]
    public async Task Completion_accepts_only_current_process_terminal_outcomes(
        RecentSessionOutcome outcome)
    {
        var store = new RecordingStore();
        var expected = RecentSessionStoreResult<Unit>.Failure(new RecentSessionStoreError(
            RecentSessionStoreErrorCode.StorageFailure,
            "Completion failed."));
        store.CompletionResult = expected;
        var history = new RecentSessionHistory(store, new MutableTimeProvider(LocalNow));
        var sessionId = new SessionId("session-complete");

        var result = await history.RecordCompletedAsync(
            sessionId,
            outcome,
            CancellationToken.None);

        Assert.Same(expected, result);
        var completion = Assert.Single(store.Completed);
        Assert.Equal(sessionId, completion.SessionId);
        Assert.Equal(LocalNow.ToUniversalTime(), completion.EndedAt);
        Assert.Equal(outcome, completion.Outcome);
    }

    [Theory]
    [InlineData(RecentSessionOutcome.Active)]
    [InlineData(RecentSessionOutcome.Interrupted)]
    [InlineData((RecentSessionOutcome)999)]
    public async Task Completion_rejects_non_allowlisted_outcomes_before_persistence(
        RecentSessionOutcome outcome)
    {
        var store = new RecordingStore();
        var history = new RecentSessionHistory(store, new MutableTimeProvider(LocalNow));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await history.RecordCompletedAsync(
                new SessionId("session-invalid-outcome"),
                outcome,
                CancellationToken.None));

        Assert.Empty(store.Completed);
    }

    [Fact]
    public async Task Listing_reconciles_then_forwards_a_bounded_query_and_store_error()
    {
        var store = new RecordingStore();
        var expected = RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Failure(
            new RecentSessionStoreError(
                RecentSessionStoreErrorCode.StorageUnavailable,
                "History cannot be read."));
        store.ListResult = expected;
        var history = new RecentSessionHistory(store, new MutableTimeProvider(LocalNow));

        var result = await history.ListRecentAsync(
            RecentSessionQuery.MaximumLimit,
            CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Equal(1, store.ReconciliationCalls);
        Assert.Equal(RecentSessionQuery.MaximumLimit, Assert.Single(store.Queries).Limit);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await history.ListRecentAsync(
                RecentSessionQuery.MaximumLimit + 1,
                CancellationToken.None));
        Assert.Single(store.Queries);
    }

    [Fact]
    public async Task Clear_uses_the_confirmation_cutoff_even_when_execution_is_delayed()
    {
        var store = new RecordingStore();
        var time = new MutableTimeProvider(LocalNow);
        var expected = RecentSessionStoreResult<int>.Failure(new RecentSessionStoreError(
            RecentSessionStoreErrorCode.StorageFailure,
            "Clear failed."));
        store.ClearResult = expected;
        var history = new RecentSessionHistory(store, time);

        var cutoff = history.CaptureClearCutoff();
        time.UtcNow = LocalNow.AddHours(2);
        var result = await history.ClearThroughAsync(cutoff, CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Equal(LocalNow.ToUniversalTime(), cutoff.ThroughUtc);
        Assert.Equal(cutoff.ThroughUtc, Assert.Single(store.ClearCutoffs));
        Assert.Equal(0, store.ReconciliationCalls);
    }

    [Fact]
    public async Task Lifecycle_timestamps_are_captured_before_queued_persistence_runs()
    {
        var store = new RecordingStore();
        var time = new MutableTimeProvider(LocalNow);
        var history = new RecentSessionHistory(store, time);
        var sessionId = new SessionId("session-delayed-write");
        var started = history.CaptureStarted(
            sessionId,
            new DefinitionKey(DefinitionKind.Connection, "connection-delayed-write"),
            PanelKind.Terminal,
            "Delayed write");
        time.UtcNow = LocalNow.AddMinutes(5);
        var completed = history.CaptureCompletion(
            sessionId,
            RecentSessionOutcome.GracefullyClosed);
        time.UtcNow = LocalNow.AddHours(2);

        await history.RecordStartedAsync(started, CancellationToken.None);
        await history.RecordCompletedAsync(completed, CancellationToken.None);

        Assert.Equal(LocalNow.ToUniversalTime(), Assert.Single(store.Started).StartedAt);
        Assert.Equal(
            LocalNow.AddMinutes(5).ToUniversalTime(),
            Assert.Single(store.Completed).EndedAt);
    }

    [Fact]
    public async Task Unconditional_clear_is_forwarded_for_unreadable_history_recovery()
    {
        var store = new RecordingStore
        {
            ClearAllResult = RecentSessionStoreResult<int>.Success(2),
        };
        var history = new RecentSessionHistory(store, new MutableTimeProvider(LocalNow));

        var result = await history.ClearAllAsync(CancellationToken.None);

        Assert.Equal(2, result.Value);
        Assert.Contains("clear-all", store.Calls, StringComparer.Ordinal);
        Assert.Empty(store.ClearCutoffs);
    }

    [Fact]
    public async Task Retention_reads_and_updates_are_forwarded_with_revision_and_policy()
    {
        var store = new RecordingStore();
        var retained = new StoredRecentSessionRetentionPolicy(
            RecentSessionRetentionPolicy.Default,
            7);
        var updatedPolicy = new RecentSessionRetentionPolicy(20, TimeSpan.FromDays(7));
        var updated = new RecentSessionRetentionUpdateResult(
            new StoredRecentSessionRetentionPolicy(updatedPolicy, 8),
            4);
        var retention = new RecordingRetentionStore(
            RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>.Success(retained),
            RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Success(updated));
        var history = new RecentSessionHistory(
            store,
            new MutableTimeProvider(LocalNow),
            retention);

        var read = await history.GetRetentionAsync(CancellationToken.None);
        var saved = await history.UpdateRetentionAsync(
            updatedPolicy,
            retained.Revision,
            CancellationToken.None);

        Assert.True(history.SupportsRetentionSettings);
        Assert.Same(retained, read.Value);
        Assert.Same(updated, saved.Value);
        Assert.Equal(updatedPolicy, retention.LastPolicy);
        Assert.Equal(7, retention.LastExpectedRevision);
    }

    [Fact]
    public async Task Cancelling_while_reconciliation_is_owned_by_another_caller_is_typed()
    {
        var store = new RecordingStore { HoldReconciliation = true };
        var history = new RecentSessionHistory(store, new MutableTimeProvider(LocalNow));
        var owner = history.InitializeAsync(CancellationToken.None).AsTask();
        await store.ReconciliationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await history.InitializeAsync(cancellation.Token);
        store.ReleaseReconciliation.TrySetResult();
        var initialized = await owner;

        Assert.Equal(RecentSessionStoreErrorCode.Cancelled, cancelled.Error!.Code);
        Assert.Same(store.SuccessfulReconciliation, initialized);
        Assert.Equal(1, store.ReconciliationCalls);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class RecordingStore : IRecentSessionStore
    {
        private readonly ConcurrentQueue<RecentSessionStoreResult<int>> _reconciliations = [];
        private int _reconciliationCalls;

        public RecordingStore()
        {
            SuccessfulReconciliation = RecentSessionStoreResult<int>.Success(3);
            StartResult = RecentSessionStoreResult<Unit>.Success(Unit.Value);
            CompletionResult = RecentSessionStoreResult<Unit>.Success(Unit.Value);
            ListResult = RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Success(
                []);
            ClearResult = RecentSessionStoreResult<int>.Success(0);
            ClearAllResult = RecentSessionStoreResult<int>.Success(0);
        }

        public ConcurrentQueue<string> Calls { get; } = [];

        public ConcurrentQueue<RecentSessionRecord> Started { get; } = [];

        public ConcurrentQueue<RecentSessionCompletion> Completed { get; } = [];

        public ConcurrentQueue<RecentSessionQuery> Queries { get; } = [];

        public ConcurrentQueue<DateTimeOffset> ClearCutoffs { get; } = [];

        public TaskCompletionSource ReconciliationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseReconciliation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HoldReconciliation { get; init; }

        public int ReconciliationCalls => Volatile.Read(ref _reconciliationCalls);

        public RecentSessionStoreResult<int> SuccessfulReconciliation { get; }

        public RecentSessionStoreResult<Unit> StartResult { get; set; }

        public RecentSessionStoreResult<Unit> CompletionResult { get; set; }

        public RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>> ListResult { get; set; }

        public RecentSessionStoreResult<int> ClearResult { get; set; }

        public RecentSessionStoreResult<int> ClearAllResult { get; set; }

        public void EnqueueReconciliation(RecentSessionStoreResult<int> result) =>
            _reconciliations.Enqueue(result);

        public ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
            RecentSessionRecord recentSession,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue("start");
            Started.Enqueue(recentSession);
            return ValueTask.FromResult(StartResult);
        }

        public ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
            RecentSessionCompletion completion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue("complete");
            Completed.Enqueue(completion);
            return ValueTask.FromResult(CompletionResult);
        }

        public ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>>
            ListRecentAsync(
                RecentSessionQuery query,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue("list");
            Queries.Enqueue(query);
            return ValueTask.FromResult(ListResult);
        }

        public async ValueTask<RecentSessionStoreResult<int>> MarkActiveSessionsInterruptedAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reconciliationCalls);
            Calls.Enqueue("reconcile");
            ReconciliationEntered.TrySetResult();
            if (HoldReconciliation)
            {
                await ReleaseReconciliation.Task.WaitAsync(cancellationToken);
            }

            return _reconciliations.TryDequeue(out var result)
                ? result
                : SuccessfulReconciliation;
        }

        public ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
            DateTimeOffset through,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue("clear");
            ClearCutoffs.Enqueue(through);
            return ValueTask.FromResult(ClearResult);
        }

        public ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue("clear-all");
            return ValueTask.FromResult(ClearAllResult);
        }
    }

    private sealed class RecordingRetentionStore(
        RecentSessionStoreResult<StoredRecentSessionRetentionPolicy> readResult,
        RecentSessionStoreResult<RecentSessionRetentionUpdateResult> updateResult)
        : IRecentSessionRetentionStore
    {
        public RecentSessionRetentionPolicy? LastPolicy { get; private set; }

        public long? LastExpectedRevision { get; private set; }

        public ValueTask<RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>>
            GetRetentionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(readResult);
        }

        public ValueTask<RecentSessionStoreResult<RecentSessionRetentionUpdateResult>>
            UpdateRetentionAsync(
                RecentSessionRetentionPolicy policy,
                long expectedRevision,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPolicy = policy;
            LastExpectedRevision = expectedRevision;
            return ValueTask.FromResult(updateResult);
        }
    }
}
