namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceServiceDiskTemplateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ghostshell-service-template-{Guid.NewGuid():N}");

    public WorkspaceServiceDiskTemplateTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Repeated_routes_clone_one_verified_template_without_reprovisioning()
    {
        var prepared = 0;
        ValueTask Prepare(string path, CancellationToken cancellationToken)
        {
            prepared++;
            return new ValueTask(File.WriteAllTextAsync(path, "clean guest filesystem", cancellationToken));
        }
        var first = Path.Combine(_directory, "first.ext4");
        var second = Path.Combine(_directory, "second.ext4");
        await CloneAsync("image+revision1", first, Prepare);
        await File.WriteAllTextAsync(first, "private state from first route", CancellationToken.None);
        await CloneAsync("image+revision1", second, Prepare);
        Assert.Equal(1, prepared);
        Assert.Equal("clean guest filesystem", await File.ReadAllTextAsync(second, CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(CacheRoot, "rootfs.ext4", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Changed_provisioning_revision_builds_a_distinct_template()
    {
        await CloneAsync("image+revision1", Path.Combine(_directory, "first.ext4"), PrepareAsync);
        await CloneAsync("image+revision2", Path.Combine(_directory, "second.ext4"), PrepareAsync);
        Assert.Equal(2, Directory.EnumerateFiles(CacheRoot, "rootfs.ext4", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Concurrent_routes_share_provisioning_but_not_mutable_disk()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        async ValueTask Prepare(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await PrepareAsync(path, cancellationToken);
        }
        var first = CloneAsync("same", Path.Combine(_directory, "first.ext4"), Prepare).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = CloneAsync("same", Path.Combine(_directory, "second.ext4"), Prepare).AsTask();
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Corrupt_template_is_rejected_before_a_new_route_disk_exists()
    {
        await CloneAsync("same", Path.Combine(_directory, "first.ext4"), PrepareAsync);
        var template = Assert.Single(Directory.EnumerateFiles(CacheRoot, "rootfs.ext4", SearchOption.AllDirectories));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(template, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        await File.WriteAllTextAsync(template, "corruption", CancellationToken.None);
        var second = Path.Combine(_directory, "second.ext4");
        await Assert.ThrowsAsync<IOException>(async () => await CloneAsync("same", second, PrepareAsync));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task Failed_bootstrap_is_not_published_and_can_be_retried()
    {
        var first = Path.Combine(_directory, "first.ext4");
        await Assert.ThrowsAsync<IOException>(async () => await CloneAsync("same", first,
            static (_, _) => throw new IOException("Bootstrap failed.")));
        Assert.Empty(Directory.EnumerateFiles(CacheRoot, "rootfs.sha256", SearchOption.AllDirectories));
        Assert.False(File.Exists(first));
        await CloneAsync("same", first, PrepareAsync);
        Assert.Equal("template", await File.ReadAllTextAsync(first, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_during_lock_wait_does_not_cancel_template_owner()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask Prepare(string path, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await PrepareAsync(path, cancellationToken);
        }
        var first = CloneAsync("same", Path.Combine(_directory, "first.ext4"), Prepare).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var second = WorkspaceServiceDiskTemplate.CloneAsync(CacheRoot, "same", Path.Combine(_directory, "second.ext4"),
            PrepareAsync, cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await second);
        release.TrySetResult();
        await first;
    }

    private string CacheRoot => Path.Combine(_directory, "cache");

    private ValueTask CloneAsync(string identity, string destination, Func<string, CancellationToken, ValueTask> prepare) =>
        WorkspaceServiceDiskTemplate.CloneAsync(CacheRoot, identity, destination, prepare, CancellationToken.None);

    private static ValueTask PrepareAsync(string path, CancellationToken cancellationToken) =>
        new(File.WriteAllTextAsync(path, "template", cancellationToken));

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
