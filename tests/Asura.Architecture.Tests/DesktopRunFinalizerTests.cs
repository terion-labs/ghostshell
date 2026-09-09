using Asura.Application;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class DesktopRunFinalizerTests
{
    [Fact]
    public async Task RecoveryFlushFailureLeavesTheRunMarkerDirty()
    {
        var startupState = InitializedStartup();
        var recoveryWriter = new RuntimeRecoveryWriter(
            new FailingRecoveryStore(),
            startupState,
            TimeProvider.System);
        Assert.True(recoveryWriter.Enqueue("desktop.main-window", 1, "{}").IsSuccess);
        var runStore = new RecordingRunStore([]);
        var finalizer = new DesktopRunFinalizer(recoveryWriter, runStore);
        var historyFlushed = false;

        var result = await finalizer.FinalizeAsync(
            _ => Task.CompletedTask,
            _ =>
            {
                historyFlushed = true;
                return SuccessfulHistoryFlush();
            },
            _ => ValueTask.CompletedTask,
            "current-run",
            CancellationToken.None);

        Assert.True(historyFlushed);
        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, result.Error!.Code);
        Assert.Equal(0, runStore.CompletionCount);
    }

    [Fact]
    public async Task RunMarkerCompletesAfterHistoryAndQueuedRecovery()
    {
        var order = new List<string>();
        var releaseRecovery = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startupState = InitializedStartup();
        var recoveryWriter = new RuntimeRecoveryWriter(
            new BlockingRecoveryStore(order, releaseRecovery.Task),
            startupState,
            TimeProvider.System);
        Assert.True(recoveryWriter.Enqueue("desktop.main-window", 1, "{}").IsSuccess);
        var runStore = new RecordingRunStore(order);
        var finalizer = new DesktopRunFinalizer(recoveryWriter, runStore);

        var result = await finalizer.FinalizeAsync(
            _ =>
            {
                order.Add("presentation-quiesced");
                return Task.CompletedTask;
            },
            _ =>
            {
                order.Add("history");
                releaseRecovery.SetResult();
                return SuccessfulHistoryFlush();
            },
            _ =>
            {
                order.Add("session-host-stopped");
                return ValueTask.CompletedTask;
            },
            "current-run",
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(6, order.Count);
        Assert.Equal(6, order.Distinct(StringComparer.Ordinal).Count());
        Assert.True(IndexOf("recovery-started") < IndexOf("recovery-completed"));
        Assert.True(IndexOf("presentation-quiesced") < IndexOf("history"));
        Assert.True(IndexOf("history") < IndexOf("session-host-stopped"));
        Assert.True(IndexOf("session-host-stopped") < IndexOf("run-marker-completed"));
        Assert.True(IndexOf("recovery-completed") < IndexOf("run-marker-completed"));
        Assert.Equal(1, runStore.CompletionCount);
        Assert.False(recoveryWriter.Enqueue("desktop.main-window", 1, "{}").IsSuccess);

        int IndexOf(string step) => order.IndexOf(step);
    }

    [Fact]
    public async Task SessionHostFailureLeavesRunMarkerDirtyAndRecoveryWriterOpen()
    {
        var startupState = InitializedStartup();
        var recoveryWriter = new RuntimeRecoveryWriter(
            new SuccessfulRecoveryStore(),
            startupState,
            TimeProvider.System);
        var runStore = new RecordingRunStore([]);
        var finalizer = new DesktopRunFinalizer(recoveryWriter, runStore);

        var result = await finalizer.FinalizeAsync(
            _ => Task.CompletedTask,
            _ => SuccessfulHistoryFlush(),
            _ => throw new InvalidOperationException("Session shutdown failed."),
            "current-run",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, result.Error!.Code);
        Assert.Equal(0, runStore.CompletionCount);
        Assert.True(recoveryWriter.Enqueue("desktop.main-window", 1, "{}").IsSuccess);
    }

    [Fact]
    public async Task HistoryFlushFailureLeavesRunMarkerDirtyAndDoesNotStopTheHost()
    {
        var recoveryWriter = new RuntimeRecoveryWriter(
            new SuccessfulRecoveryStore(),
            InitializedStartup(),
            TimeProvider.System);
        var runStore = new RecordingRunStore([]);
        var finalizer = new DesktopRunFinalizer(recoveryWriter, runStore);
        var hostStopped = false;

        var result = await finalizer.FinalizeAsync(
            _ => Task.CompletedTask,
            _ => Task.FromResult(ApplicationRunResult<Unit>.Failure(
                new ApplicationRunError(
                    ApplicationRunErrorCode.StorageFailure,
                    "History persistence failed."))),
            _ =>
            {
                hostStopped = true;
                return ValueTask.CompletedTask;
            },
            "current-run",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, result.Error!.Code);
        Assert.False(hostStopped);
        Assert.Equal(0, runStore.CompletionCount);
    }

    private static Task<ApplicationRunResult<Unit>> SuccessfulHistoryFlush() =>
        Task.FromResult(ApplicationRunResult<Unit>.Success(Unit.Value));

    private static ApplicationStartupState InitializedStartup()
    {
        var startup = new ApplicationStartupState();
        startup.Initialize(new ApplicationRunStart(
            "current-run",
            RecoveryRequired: false,
            new ApplicationRunState(null, WasClean: true, null, null)));
        return startup;
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

    private sealed class BlockingRecoveryStore(
        List<string> order,
        Task release) : IRuntimeRecoveryStore
    {
        public ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> LoadAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async ValueTask<ApplicationRunResult<Unit>> SaveAsync(
            RuntimeRecoverySnapshot snapshot,
            CancellationToken cancellationToken)
        {
            order.Add("recovery-started");
            await release.WaitAsync(cancellationToken);
            order.Add("recovery-completed");
            return ApplicationRunResult<Unit>.Success(Unit.Value);
        }

        public ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SuccessfulRecoveryStore : IRuntimeRecoveryStore
    {
        public ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> LoadAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ApplicationRunResult<Unit>> SaveAsync(
            RuntimeRecoverySnapshot snapshot,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApplicationRunResult<Unit>.Success(Unit.Value));

        public ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
            string runId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingRunStore(List<string> order) : IApplicationRunStore
    {
        public int CompletionCount { get; private set; }

        public ValueTask<ApplicationRunResult<ApplicationRunStart>> BeginRunAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ApplicationRunResult<Unit>> CompleteRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            CompletionCount++;
            order.Add("run-marker-completed");
            return ValueTask.FromResult(ApplicationRunResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<ApplicationRunResult<ApplicationRunState>> GetStateAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
