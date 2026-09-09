using Asura.Application;
using Asura.ConnectionBackend;
using Asura.Desktop;
using Asura.Infrastructure;

namespace Asura.Architecture.Tests;

public sealed class HostConnectionBackendTests
{
    [Fact]
    public async Task Actual_desktop_child_executes_private_backend_before_ui_or_profile_startup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) { root = root.Parent; }
        Assert.NotNull(root);
        var dotnet = Path.Combine(root.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var reentry = new SelfReentryLaunch(dotnet, [typeof(Program).Assembly.Location], dotnet);
        await using var backend = new HostConnectionBackend(reentry,
            static () => throw new InvalidOperationException("Metadata must not create a value-content store."),
            timeout.Token, CancellationToken.None);

        var tables = await backend.Databases.ListTablesAsync(new DatabaseWorkerConnection("sqlite", "Data Source=:memory:"), timeout.Token);

        Assert.Empty(tables);
    }

    [Fact]
    public async Task Direct_launch_has_only_fixed_capability_and_opaque_scratch_identity_and_cleanup_is_idempotent()
    {
        using var route = new CancellationTokenSource();
        await using var backend = new HostConnectionBackend(new SelfReentryLaunch("fixture-host", [], "fixture-host"),
            static () => throw new InvalidOperationException("No result store expected."), route.Token, CancellationToken.None);
        var launch = await backend.PlanAsync("files", CancellationToken.None);
        Assert.Equal(BackendExecutionLocation.HostDirect, launch.Location);
        Assert.Equal(3, launch.StartInfo.ArgumentList.Count);
        Assert.Equal(ConnectionBackendCommand.Marker, launch.StartInfo.ArgumentList[0]);
        var id = launch.StartInfo.ArgumentList[2];
        string directory;
        using (var lease = DatabaseWorkspaceScratch.Acquire(id)) { directory = lease.DirectoryPath; }
        await route.CancelAsync();
        Assert.True(launch.Lifetime.IsCancellationRequested);
        await launch.CleanupAsync();
        await launch.CleanupAsync();
        Assert.False(Directory.Exists(directory));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.PlanAsync("files", CancellationToken.None));
    }
}
