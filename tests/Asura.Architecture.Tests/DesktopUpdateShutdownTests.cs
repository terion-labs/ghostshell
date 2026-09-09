using System.Reflection;
using Asura.Desktop;
using Avalonia.Controls.ApplicationLifetimes;

namespace Asura.Architecture.Tests;

public sealed class DesktopUpdateShutdownTests
{
    [Fact]
    public async Task UpdateRestartWaitsForBlockedWorkspacePreparationBeforeStoppingDispatcher()
    {
        Func<Task>? scheduledWork = null;
        var shutdown = new DesktopUpdateShutdown(work => scheduledWork = work);
        var lifetime = DispatchProxy.Create<
            IClassicDesktopStyleApplicationLifetime,
            RecordingDesktopLifetime>();
        var recorder = (RecordingDesktopLifetime)(object)lifetime;
        var preparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPreparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        shutdown.Attach(lifetime, QuiesceAsync);

        shutdown.Request();
        var restart = Assert.IsType<Func<Task>>(scheduledWork)();
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, recorder.ShutdownCount);
        Assert.False(restart.IsCompleted);
        allowPreparation.SetResult();
        await restart.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, recorder.ShutdownCount);
        return;

        async Task QuiesceAsync(CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            preparationEntered.SetResult();
            await allowPreparation.Task.WaitAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task UpdateRestartStillStopsDispatcherWhenQuiescenceFaults()
    {
        Func<Task>? scheduledWork = null;
        var shutdown = new DesktopUpdateShutdown(work => scheduledWork = work);
        var lifetime = DispatchProxy.Create<
            IClassicDesktopStyleApplicationLifetime,
            RecordingDesktopLifetime>();
        var recorder = (RecordingDesktopLifetime)(object)lifetime;
        shutdown.Attach(
            lifetime,
            _ => Task.FromException(new InvalidOperationException("Test quiescence failure.")));

        shutdown.Request();
        await Assert.IsType<Func<Task>>(scheduledWork)()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, recorder.ShutdownCount);
    }

    public class RecordingDesktopLifetime : DispatchProxy
    {
        private int _shutdownCount;

        public int ShutdownCount => Volatile.Read(ref _shutdownCount);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            if (string.Equals(
                targetMethod?.Name,
                nameof(IClassicDesktopStyleApplicationLifetime.Shutdown),
                StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _shutdownCount);
                return null;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
