using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceRuntimeLifetimeTests
{
    [Fact]
    public async Task Failed_backend_cleanup_remains_retryable_without_repeating_successful_releases()
    {
        var files = new Resource();
        var database = new Resource();
        var backend = new Resource { Fail = true };
        var proxy = new Resource { Fail = true };
        var sessions = new Resource();
        var monitorRegistration = new Resource();
        var monitor = new Resource();
        var lifetime = new DesktopWorkspaceRuntimeServicesFactory.WorkspaceRuntimeLifetime(
            files, database, backend, proxy, sessions, monitorRegistration, monitor);

        var failure = await Assert.ThrowsAsync<AggregateException>(async () => await lifetime.DisposeAsync());
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.All<Resource>([files, database, backend, proxy, sessions, monitorRegistration, monitor],
            resource => Assert.Equal(1, resource.Disposals));

        backend.Fail = false;
        proxy.Fail = false;
        await lifetime.DisposeAsync();
        await lifetime.DisposeAsync();
        Assert.Equal(2, backend.Disposals);
        Assert.Equal(2, proxy.Disposals);
        Assert.All<Resource>([files, database, sessions, monitorRegistration, monitor],
            resource => Assert.Equal(1, resource.Disposals));
    }

    [Fact]
    public async Task Concurrent_disposal_waits_for_the_same_owned_release()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Resource { Wait = async () => { entered.TrySetResult(); await release.Task; } };
        var files = new Resource();
        var database = new Resource();
        var proxy = new Resource();
        var sessions = new Resource();
        var monitorRegistration = new Resource();
        var monitor = new Resource();
        var lifetime = new DesktopWorkspaceRuntimeServicesFactory.WorkspaceRuntimeLifetime(
            files, database, backend, proxy, sessions, monitorRegistration, monitor);

        var first = lifetime.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = lifetime.DisposeAsync().AsTask();
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.All<Resource>([files, database, backend, proxy, sessions, monitorRegistration, monitor],
            resource => Assert.Equal(1, resource.Disposals));
    }

    private sealed class Resource : IDisposable, IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        internal bool Fail { get; set; }
        internal Func<Task>? Wait { get; init; }

        public void Dispose()
        {
            Disposals++;
            if (Fail) { throw new IOException("Fixture owned resource could not stop."); }
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            if (Wait is { } wait) { await wait(); }
        }
    }
}
