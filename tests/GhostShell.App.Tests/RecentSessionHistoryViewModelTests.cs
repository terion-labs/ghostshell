using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App.Tests;

public sealed class RecentSessionHistoryViewModelTests
{
    [Fact]
    public async Task Direct_instance_owns_loading_search_selection_and_reset()
    {
        var startedAt = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        var store = new TestStore(new RecentSessionRecord(
            new SessionId("direct-history"),
            new DefinitionKey(DefinitionKind.Connection, "direct-source"),
            PanelKind.Terminal,
            "Direct needle",
            startedAt,
            startedAt.AddMinutes(1),
            RecentSessionOutcome.GracefullyClosed));
        using var viewModel = CreateViewModel(store, new MutableTimeProvider(startedAt));

        viewModel.StartLoading();
        Assert.True((await viewModel.DrainAsync(CancellationToken.None)).IsSuccess);

        Assert.Single(viewModel.Sessions);
        Assert.Single(viewModel.RecentSessions);
        viewModel.SearchQuery = "needle";
        Assert.Equal(new SessionId("direct-history"), viewModel.SelectedSession!.SessionId);
        Assert.Equal("1 matched", viewModel.ResultCount);

        store.FailReadsUntilCleared = true;
        Assert.True(await viewModel.ClearAsync(
            viewModel.CaptureClearCutoff(),
            CancellationToken.None));
        Assert.True(viewModel.HasUnreadableHistory);
        Assert.Empty(viewModel.Sessions);

        Assert.True(await viewModel.ResetUnreadableAsync(CancellationToken.None));
        Assert.False(viewModel.HasFailure);
        Assert.False(viewModel.HasUnreadableHistory);
    }

    [Fact]
    public async Task Started_timestamp_is_captured_before_the_serialized_write_queue()
    {
        var firstTimestamp = new DateTimeOffset(2026, 8, 26, 11, 0, 0, TimeSpan.Zero);
        var secondTimestamp = firstTimestamp.AddMinutes(1);
        var clock = new MutableTimeProvider(firstTimestamp);
        var store = new TestStore { BlockFirstStartedWrite = true };
        using var viewModel = CreateViewModel(store, clock);

        _ = viewModel.RecordStartedAsync(
            new SessionId("first"),
            new DefinitionKey(DefinitionKind.Connection, "source"),
            PanelKind.Terminal,
            "First");
        await store.FirstStartedWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.UtcNow = secondTimestamp;
        _ = viewModel.RecordStartedAsync(
            new SessionId("second"),
            new DefinitionKey(DefinitionKind.Connection, "source"),
            PanelKind.Terminal,
            "Second");
        clock.UtcNow = secondTimestamp.AddHours(1);
        store.ReleaseFirstStartedWrite.TrySetResult();

        Assert.True((await viewModel.DrainAsync(CancellationToken.None)).IsSuccess);
        var second = Assert.Single(store.Snapshot, item => item.SessionId.Value == "second");
        Assert.Equal(secondTimestamp, second.StartedAt);
    }

    [Fact]
    public async Task Clear_uses_its_captured_cutoff_and_preserves_later_writes()
    {
        var cutoffTime = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(cutoffTime);
        var store = new TestStore(new RecentSessionRecord(
            new SessionId("old"),
            new DefinitionKey(DefinitionKind.Connection, "source"),
            PanelKind.Terminal,
            "Old",
            cutoffTime.AddMinutes(-2),
            cutoffTime.AddMinutes(-1),
            RecentSessionOutcome.GracefullyClosed))
        {
            BlockFirstStartedWrite = true,
        };
        using var viewModel = CreateViewModel(store, clock);

        _ = viewModel.RecordStartedAsync(
            new SessionId("queue-blocker"),
            new DefinitionKey(DefinitionKind.Connection, "source"),
            PanelKind.Terminal,
            "Queue blocker");
        await store.FirstStartedWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cutoff = viewModel.CaptureClearCutoff();
        var clear = viewModel.ClearAsync(cutoff, CancellationToken.None);

        clock.UtcNow = cutoffTime.AddMinutes(1);
        _ = viewModel.RecordStartedAsync(
            new SessionId("newer"),
            new DefinitionKey(DefinitionKind.Connection, "source"),
            PanelKind.Terminal,
            "Newer");
        store.ReleaseFirstStartedWrite.TrySetResult();

        Assert.True(await clear);
        Assert.True((await viewModel.DrainAsync(CancellationToken.None)).IsSuccess);
        var remaining = Assert.Single(store.Snapshot);
        Assert.Equal(new SessionId("newer"), remaining.SessionId);
        Assert.Equal(cutoffTime, cutoff.ThroughUtc);
    }

    [Fact]
    public async Task Write_failure_remains_sticky_across_repeated_drains_and_disposal_is_idempotent()
    {
        var store = new TestStore { FailStartedWrites = true };
        var viewModel = CreateViewModel(store, new MutableTimeProvider(DateTimeOffset.UnixEpoch));

        _ = viewModel.RecordStartedAsync(
            new SessionId("failed"),
            new DefinitionKey(DefinitionKind.Connection, "source"),
            PanelKind.Terminal,
            "Failed");

        var first = await viewModel.DrainAsync(CancellationToken.None);
        store.FailStartedWrites = false;
        var second = await viewModel.DrainAsync(CancellationToken.None);

        Assert.False(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Contains("write failed", second.Error!.Message, StringComparison.OrdinalIgnoreCase);
        viewModel.Dispose();
        viewModel.Dispose();
    }

    [Fact]
    public async Task Availability_refresh_preserves_the_observation_timestamp()
    {
        var observedAt = new DateTimeOffset(2026, 8, 26, 13, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(observedAt);
        var store = new TestStore(new RecentSessionRecord(
            new SessionId("availability"),
            new DefinitionKey(DefinitionKind.Connection, "source"),
            PanelKind.Terminal,
            "Availability",
            observedAt.AddMinutes(-5),
            observedAt.AddMinutes(-1),
            RecentSessionOutcome.GracefullyClosed));
        using var viewModel = CreateViewModel(store, clock);
        viewModel.StartLoading();
        Assert.True((await viewModel.DrainAsync(CancellationToken.None)).IsSuccess);
        var original = Assert.Single(viewModel.Sessions);

        clock.UtcNow = observedAt.AddHours(3);
        viewModel.RefreshAvailability(static (record, timestamp) =>
            new RecentSessionHistoryItemViewModel(record, CanOpen: false, timestamp));

        var refreshed = Assert.Single(viewModel.Sessions);
        Assert.Equal(original.ObservedAt, refreshed.ObservedAt);
        Assert.Equal(original.LastUsed, refreshed.LastUsed);
        Assert.False(refreshed.CanOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_queued_before_shutdown_persists_without_publishing_after_shutdown(bool failWrite)
    {
        var store = new TestStore { BlockCompletionWrite = true, FailCompletionWrites = failWrite };
        using var viewModel = CreateViewModel(store, new MutableTimeProvider(DateTimeOffset.UnixEpoch));
        var snapshots = 0;
        viewModel.SnapshotChanged += (_, _) => snapshots++;
        var queued = viewModel.RecordCompletionsAsync(
            [(new SessionId("ending"), RecentSessionOutcome.GracefullyClosed)]);
        await store.CompletionWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.StopPresentationUpdates();
        store.ReleaseCompletionWrite.TrySetResult();
        await queued.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, store.CompletionWrites);
        Assert.Equal(0, store.ListReads);
        Assert.Equal(0, snapshots);
        Assert.False(viewModel.HasFailure);
        Assert.Equal(!failWrite, (await viewModel.DrainAsync(CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Completion_refreshes_history_while_the_presentation_is_alive()
    {
        var store = new TestStore();
        using var viewModel = CreateViewModel(store, new MutableTimeProvider(DateTimeOffset.UnixEpoch));
        var snapshots = 0;
        viewModel.SnapshotChanged += (_, _) => snapshots++;

        await viewModel.RecordCompletionsAsync(
            [(new SessionId("ending"), RecentSessionOutcome.GracefullyClosed)]);

        Assert.Equal(1, store.CompletionWrites);
        Assert.Equal(1, store.ListReads);
        Assert.Equal(1, snapshots);
    }

    [Fact]
    public async Task Shutdown_during_history_refresh_does_not_publish_the_delayed_read()
    {
        var store = new TestStore { BlockListRead = true };
        using var viewModel = CreateViewModel(store, new MutableTimeProvider(DateTimeOffset.UnixEpoch));
        var snapshots = 0;
        viewModel.SnapshotChanged += (_, _) => snapshots++;
        var queued = viewModel.RecordCompletionsAsync(
            [(new SessionId("ending"), RecentSessionOutcome.GracefullyClosed)]);
        await store.ListReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.StopPresentationUpdates();
        store.ReleaseListRead.TrySetResult();
        await queued.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, store.CompletionWrites);
        Assert.Equal(1, store.ListReads);
        Assert.Equal(0, snapshots);
        Assert.True((await viewModel.DrainAsync(CancellationToken.None)).IsSuccess);
    }

    private static RecentSessionHistoryViewModel CreateViewModel(
        TestStore store,
        TimeProvider clock) =>
        new(
            new RecentSessionHistory(store, clock),
            clock,
            static (record, observedAt) =>
                new RecentSessionHistoryItemViewModel(record, CanOpen: true, observedAt));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TestStore : IRecentSessionStore, IRecentSessionRetentionStore
    {
        private readonly object _gate = new();
        private readonly List<RecentSessionRecord> _records;
        private StoredRecentSessionRetentionPolicy _retention = new(
            RecentSessionRetentionPolicy.Default,
            revision: 1);
        private int _startedWrites;

        public TestStore(params RecentSessionRecord[] records)
        {
            _records = [.. records];
        }

        public bool BlockFirstStartedWrite { get; init; }

        public bool FailStartedWrites { get; set; }

        public bool FailReadsUntilCleared { get; set; }

        public bool BlockCompletionWrite { get; init; }

        public bool FailCompletionWrites { get; init; }

        public bool BlockListRead { get; init; }

        public int CompletionWrites { get; private set; }

        public int ListReads { get; private set; }

        public TaskCompletionSource CompletionWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCompletionWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ListReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseListRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstStartedWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstStartedWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<RecentSessionRecord> Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records];
                }
            }
        }

        public ValueTask<RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>>
            GetRetentionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>.Success(
                    _retention));

        public ValueTask<RecentSessionStoreResult<RecentSessionRetentionUpdateResult>>
            UpdateRetentionAsync(
                RecentSessionRetentionPolicy policy,
                long expectedRevision,
                CancellationToken cancellationToken)
        {
            _retention = new StoredRecentSessionRetentionPolicy(policy, _retention.Revision + 1);
            return ValueTask.FromResult(
                RecentSessionStoreResult<RecentSessionRetentionUpdateResult>.Success(
                    new RecentSessionRetentionUpdateResult(_retention, 0)));
        }

        public async ValueTask<RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>>
            ListRecentAsync(RecentSessionQuery query, CancellationToken cancellationToken)
        {
            ListReads++;
            if (BlockListRead)
            {
                ListReadEntered.TrySetResult();
                await ReleaseListRead.Task.WaitAsync(cancellationToken);
            }

            if (FailReadsUntilCleared)
            {
                return RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Failure(
                        new RecentSessionStoreError(
                            RecentSessionStoreErrorCode.InvalidHistoryData,
                            "Unreadable history."));
            }

            lock (_gate)
            {
                IReadOnlyList<RecentSessionRecord> snapshot =
                    [.. _records.OrderByDescending(item => item.LastUsedAt).Take(query.Limit)];
                return RecentSessionStoreResult<IReadOnlyList<RecentSessionRecord>>.Success(snapshot);
            }
        }

        public async ValueTask<RecentSessionStoreResult<Unit>> RecordStartedAsync(
            RecentSessionRecord recentSession,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _startedWrites) == 1 && BlockFirstStartedWrite)
            {
                FirstStartedWriteEntered.TrySetResult();
                await ReleaseFirstStartedWrite.Task.WaitAsync(cancellationToken);
            }

            if (FailStartedWrites)
            {
                return RecentSessionStoreResult<Unit>.Failure(new RecentSessionStoreError(
                    RecentSessionStoreErrorCode.StorageFailure,
                    "Started write failed."));
            }

            lock (_gate)
            {
                _records.Add(recentSession);
            }

            return RecentSessionStoreResult<Unit>.Success(Unit.Value);
        }

        public async ValueTask<RecentSessionStoreResult<Unit>> RecordCompletedAsync(
            RecentSessionCompletion completion,
            CancellationToken cancellationToken)
        {
            if (BlockCompletionWrite)
            {
                CompletionWriteEntered.TrySetResult();
                await ReleaseCompletionWrite.Task.WaitAsync(cancellationToken);
            }

            CompletionWrites++;
            return FailCompletionWrites
                ? RecentSessionStoreResult<Unit>.Failure(new RecentSessionStoreError(
                    RecentSessionStoreErrorCode.StorageFailure, "Completion write failed."))
                : RecentSessionStoreResult<Unit>.Success(Unit.Value);
        }

        public ValueTask<RecentSessionStoreResult<int>> MarkActiveSessionsInterruptedAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(RecentSessionStoreResult<int>.Success(0));

        public ValueTask<RecentSessionStoreResult<int>> ClearThroughAsync(
            DateTimeOffset through,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var removed = _records.RemoveAll(item => item.LastUsedAt <= through);
                return ValueTask.FromResult(RecentSessionStoreResult<int>.Success(removed));
            }
        }

        public ValueTask<RecentSessionStoreResult<int>> ClearAllAsync(
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var removed = _records.Count;
                _records.Clear();
                FailReadsUntilCleared = false;
                return ValueTask.FromResult(RecentSessionStoreResult<int>.Success(removed));
            }
        }
    }
}
