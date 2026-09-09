namespace Asura.Application.Tests;

public sealed class SessionRestoreCoordinatorTests
{
    [Fact]
    public async Task LoadsTheMostRecentlyUpdatedInactiveRun()
    {
        var older = new RuntimeRecoveryRunSummary(
            "older",
            snapshotCount: 1,
            payloadBytes: 20,
            new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero));
        var latest = new RuntimeRecoveryRunSummary(
            "latest",
            snapshotCount: 1,
            payloadBytes: 30,
            new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero));
        var expected = new RuntimeRecoverySnapshot(
            latest.RunId,
            "runtime-workspace",
            1,
            "{}",
            latest.LastUpdatedAt);
        var recoveryStore = new RecoveryStore([expected]);
        var coordinator = new SessionRestoreCoordinator(
            new PreferenceStore(),
            recoveryStore,
            new RecoveryDataControl(new RuntimeRecoveryInventory(
                [older, latest],
                hasAdditionalRuns: false)));

        var result = await coordinator.LoadLatestSessionAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("latest", recoveryStore.LoadedRunId);
        Assert.Equal([expected], result.Value);
    }

    [Fact]
    public async Task EmptyInventoryDoesNotAttemptToLoadSnapshots()
    {
        var recoveryStore = new RecoveryStore([]);
        var coordinator = new SessionRestoreCoordinator(
            new PreferenceStore(),
            recoveryStore,
            new RecoveryDataControl(new RuntimeRecoveryInventory(
                [],
                hasAdditionalRuns: false)));

        var result = await coordinator.LoadLatestSessionAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value!);
        Assert.Null(recoveryStore.LoadedRunId);
    }

    [Fact]
    public async Task PreferenceReadsAndWritesUseTheDedicatedStore()
    {
        var preferenceStore = new PreferenceStore();
        var coordinator = new SessionRestoreCoordinator(
            preferenceStore,
            new RecoveryStore([]),
            new RecoveryDataControl(new RuntimeRecoveryInventory(
                [],
                hasAdditionalRuns: false)));

        var initial = await coordinator.ReadPreferenceAsync(CancellationToken.None);
        var write = await coordinator.WritePreferenceAsync(false, CancellationToken.None);
        var updated = await coordinator.ReadPreferenceAsync(CancellationToken.None);

        Assert.True(initial.Value);
        Assert.True(write.IsSuccess, write.Error?.Message);
        Assert.False(updated.Value);
    }

    private sealed class PreferenceStore : ISessionRestorePreferenceStore
    {
        private bool _value = true;

        public ValueTask<ApplicationRunResult<bool>> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ApplicationRunResult<bool>.Success(_value));
        }

        public ValueTask<ApplicationRunResult<Unit>> WriteAsync(
            bool restoreSessionsOnStart,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _value = restoreSessionsOnStart;
            return ValueTask.FromResult(
                ApplicationRunResult<Unit>.Success(Unit.Value));
        }
    }

    private sealed class RecoveryStore(
        IReadOnlyList<RuntimeRecoverySnapshot> snapshots) : IRuntimeRecoveryStore
    {
        public string? LoadedRunId { get; private set; }

        public ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>>
            LoadAsync(string runId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedRunId = runId;
            return ValueTask.FromResult(ApplicationRunResult<
                IReadOnlyList<RuntimeRecoverySnapshot>>.Success(snapshots));
        }

        public ValueTask<ApplicationRunResult<Unit>> SaveAsync(
            RuntimeRecoverySnapshot snapshot,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
            string runId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecoveryDataControl(
        RuntimeRecoveryInventory inventory) : IRuntimeRecoveryDataControl
    {
        public ValueTask<ApplicationRunResult<RuntimeRecoveryInventory>> ListAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                ApplicationRunResult<RuntimeRecoveryInventory>.Success(inventory));
        }

        public ValueTask<ApplicationRunResult<long>> DiscardRunAsync(
            string runId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ApplicationRunResult<long>> DiscardAllAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
