using Asura.Application;

namespace Asura.Application.Tests;

public sealed class RuntimeRecoveryWriterTests
{
    [Fact]
    public async Task SealWaitsForAcceptedWritesAndRejectsLateEnqueues()
    {
        var releaseSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BlockingRecoveryStore(releaseSave.Task);
        var writer = new RuntimeRecoveryWriter(
            store,
            InitializedStartup(),
            TimeProvider.System);
        Assert.True(writer.Enqueue("desktop.main-window", 1, "{}").IsSuccess);

        var sealing = writer.SealAndFlushAsync(CancellationToken.None).AsTask();
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var lateWrite = writer.Enqueue("desktop.main-window", 1, """{"late":true}""");

        Assert.False(lateWrite.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, lateWrite.Error!.Code);
        Assert.False(sealing.IsCompleted);

        releaseSave.SetResult();
        var result = await sealing;

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task FailedWriteRemainsFailedAfterTheWriterIsSealed()
    {
        var writer = new RuntimeRecoveryWriter(
            new FailingRecoveryStore(),
            InitializedStartup(),
            TimeProvider.System);
        Assert.True(writer.Enqueue("desktop.main-window", 1, "{}").IsSuccess);

        var first = await writer.SealAndFlushAsync(CancellationToken.None);
        var second = await writer.SealAndFlushAsync(CancellationToken.None);

        Assert.False(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, first.Error!.Code);
        Assert.Equal(first.Error, second.Error);
    }

    [Fact]
    public async Task EarlierFailureCannotBeMaskedByALaterSuccessfulWrite()
    {
        var releaseFirstSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BlockingSequencedRecoveryStore(
            releaseFirstSave.Task,
            ApplicationRunResult<Unit>.Failure(new ApplicationRunError(
                ApplicationRunErrorCode.StorageFailure,
                "First write failed.")),
            ApplicationRunResult<Unit>.Success(Unit.Value));
        var writer = new RuntimeRecoveryWriter(
            store,
            InitializedStartup(),
            TimeProvider.System);

        Assert.True(writer.Enqueue("desktop.main-window", 1, """{"revision":1}""").IsSuccess);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(writer.Enqueue("desktop.main-window", 1, """{"revision":2}""").IsSuccess);
        releaseFirstSave.SetResult();
        var result = await writer.SealAndFlushAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, result.Error!.Code);
        Assert.Equal("First write failed.", result.Error.Message);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public async Task BurstUpdatesForOneKeyCoalesceToTheLatestPendingSnapshot()
    {
        var releaseFirstSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CoalescingRecoveryStore(releaseFirstSave.Task);
        var writer = new RuntimeRecoveryWriter(
            store,
            InitializedStartup(),
            TimeProvider.System);
        Assert.True(writer.Enqueue(
            "desktop.main-window",
            1,
            """{"revision":0}""").IsSuccess);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var revision = 1; revision <= 1_000; revision++)
        {
            Assert.True(writer.Enqueue(
                "desktop.main-window",
                1,
                $"{{\"revision\":{revision}}}").IsSuccess);
        }

        releaseFirstSave.SetResult();
        var result = await writer.SealAndFlushAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, store.Snapshots.Count);
        Assert.Equal("""{"revision":0}""", store.Snapshots[0].PayloadJson);
        Assert.Equal("""{"revision":1000}""", store.Snapshots[1].PayloadJson);
    }

    [Fact]
    public async Task DistinctPendingKeysAreBoundedAndOverflowKeepsTheRunDirty()
    {
        var releaseFirstSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new CoalescingRecoveryStore(releaseFirstSave.Task);
        var writer = new RuntimeRecoveryWriter(
            store,
            InitializedStartup(),
            TimeProvider.System);
        Assert.True(writer.Enqueue("snapshot-00", 1, "{}").IsSuccess);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 1; index < RuntimeRecoveryInventory.MaximumSnapshotsPerRun; index++)
        {
            Assert.True(writer.Enqueue($"snapshot-{index:D2}", 1, "{}").IsSuccess);
        }

        var overflow = writer.Enqueue("snapshot-overflow", 1, "{}");
        releaseFirstSave.SetResult();
        var result = await writer.SealAndFlushAsync(CancellationToken.None);

        Assert.Equal(ApplicationRunErrorCode.StorageFailure, overflow.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, result.Error!.Code);
        Assert.Equal(RuntimeRecoveryInventory.MaximumSnapshotsPerRun, store.Snapshots.Count);
    }

    [Fact]
    public async Task ThrowingFailureSubscriberCannotFaultTheRecoveryDrain()
    {
        var writer = new RuntimeRecoveryWriter(
            new FailingRecoveryStore(),
            InitializedStartup(),
            TimeProvider.System);
        writer.WriteFailed += (_, _) =>
            throw new InvalidOperationException("UI observer failed.");
        Assert.True(writer.Enqueue("desktop.main-window", 1, "{}").IsSuccess);

        var result = await writer.SealAndFlushAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, result.Error!.Code);
    }

    private static ApplicationStartupState InitializedStartup()
    {
        var startup = new ApplicationStartupState();
        startup.Initialize(new ApplicationRunStart(
            "current-run",
            RecoveryRequired: false,
            new ApplicationRunState(null, WasClean: true, null, null)));
        return startup;
    }

    private sealed class BlockingRecoveryStore(Task release) : IRuntimeRecoveryStore
    {
        public TaskCompletionSource SaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int SaveCount { get; private set; }

        public ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> LoadAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async ValueTask<ApplicationRunResult<Unit>> SaveAsync(
            RuntimeRecoverySnapshot snapshot,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            SaveStarted.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return ApplicationRunResult<Unit>.Success(Unit.Value);
        }

        public ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FailingRecoveryStore : IRuntimeRecoveryStore
    {
        public ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> LoadAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ApplicationRunResult<Unit>> SaveAsync(
            RuntimeRecoverySnapshot snapshot,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApplicationRunResult<Unit>.Failure(
                new ApplicationRunError(
                    ApplicationRunErrorCode.StorageFailure,
                    "Recovery persistence failed.")));

        public ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class BlockingSequencedRecoveryStore(
        Task releaseFirstSave,
        params ApplicationRunResult<Unit>[] results) : IRuntimeRecoveryStore
    {
        private readonly Queue<ApplicationRunResult<Unit>> _results = new(results);

        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int SaveCount { get; private set; }

        public ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> LoadAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async ValueTask<ApplicationRunResult<Unit>> SaveAsync(
            RuntimeRecoverySnapshot snapshot,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            if (SaveCount == 1)
            {
                FirstSaveStarted.TrySetResult();
                await releaseFirstSave.WaitAsync(cancellationToken);
            }

            return _results.Dequeue();
        }

        public ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CoalescingRecoveryStore(Task releaseFirstSave) : IRuntimeRecoveryStore
    {
        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<RuntimeRecoverySnapshot> Snapshots { get; } = [];

        public ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> LoadAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async ValueTask<ApplicationRunResult<Unit>> SaveAsync(
            RuntimeRecoverySnapshot snapshot,
            CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            if (Snapshots.Count == 1)
            {
                FirstSaveStarted.TrySetResult();
                await releaseFirstSave.WaitAsync(cancellationToken);
            }

            return ApplicationRunResult<Unit>.Success(Unit.Value);
        }

        public ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
