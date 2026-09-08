using System.Buffers.Binary;
using System.Text;
using GhostShell.Application;
using GhostShell.Desktop;
using GhostShell.Infrastructure;
using Microsoft.Data.Sqlite;

namespace GhostShell.Architecture.Tests;

public sealed class DatabaseDiagramWorkerTests
{
    [Fact]
    public async Task RealDiagramWorkerUsesParentEndpointDuringSchemaLoadAndDenialDoesNotFallBack()
    {
        var dotnet = Path.Combine(FindRepositoryRoot(), ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var factory = new DatabaseDiagramWorker(new SelfReentryLaunch(dotnet,
            [typeof(DatabaseDiagramWorker).Assembly.Location], dotnet));
        var requested = new List<(string Host, int Port)>();
        var connection = new DatabaseWorkerConnection("postgres", "Host=private-route.invalid;Port=15432;Username=fixture;Password=private-fixture")
        {
            Route = new DatabaseWorkerRoute((host, port, _) =>
            {
                requested.Add((host, port));
                throw new IOException("Parent denied fixture endpoint");
            }, CancellationToken.None),
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() => factory.OpenAsync(connection, deadline.Token));
        Assert.Equal([("private-route.invalid", 15432)], requested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_schema_is_rendered_and_exported_in_the_owned_worker(bool remotePreview)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ghostshell-diagram-{Guid.NewGuid():N}.db");
        string? registration = null;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContextToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE parent (id INTEGER PRIMARY KEY); CREATE TABLE child (id INTEGER, parent_id INTEGER REFERENCES parent(id));";
                await command.ExecuteNonQueryAsync(TestContextToken);
            }

            if (remotePreview)
            {
                registration = GhostShell.Databases.SqliteInMemoryDatabases.Register(await File.ReadAllBytesAsync(path, TestContextToken));
                connectionString = registration;
            }

            var root = FindRepositoryRoot();
            var dotnet = Path.Combine(root, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            var factory = new DatabaseDiagramWorker(new SelfReentryLaunch(dotnet,
                [typeof(DatabaseDiagramWorker).Assembly.Location], dotnet));
            await using var session = await factory.OpenAsync(new DatabaseWorkerConnection("sqlite", connectionString), TestContextToken);
            var first = await session.RenderViewportAsync(new DatabaseDiagramViewport(640, 480, 1, 0, 0), TestContextToken);
            Assert.True(first.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.Equal(640, BinaryPrimitives.ReadInt32BigEndian(first.AsSpan(16, 4)));
            Assert.Equal(480, BinaryPrimitives.ReadInt32BigEndian(first.AsSpan(20, 4)));
            var zoomed = await session.RenderViewportAsync(new DatabaseDiagramViewport(640, 480, 2, 50, -20), TestContextToken);
            Assert.NotEqual(first, zoomed);
            var light = await session.RenderViewportAsync(new DatabaseDiagramViewport(640, 480, 1, 0, 0, DarkTheme: false), TestContextToken);
            Assert.NotEqual(first, light);
            using var source = new MemoryStream();
            await session.ExportAsync(source, DatabaseDiagramExport.MermaidMarkdown, TestContextToken);
            var text = Encoding.UTF8.GetString(source.ToArray());
            Assert.Contains("parent", text, StringComparison.Ordinal);
            Assert.Contains("child", text, StringComparison.Ordinal);
            Assert.Contains("parent_id", text, StringComparison.Ordinal);
            using var svg = new MemoryStream();
            await session.ExportAsync(svg, DatabaseDiagramExport.Svg, TestContextToken);
            Assert.Contains("<svg", Encoding.UTF8.GetString(svg.ToArray()), StringComparison.Ordinal);
            await using var sourceOnly = await factory.OpenAsync(new DatabaseWorkerConnection("sqlite", connectionString),
                TestContextToken, DatabaseDiagramPurpose.SourceExport);
            using var sourceWithoutLayout = new MemoryStream();
            await sourceOnly.ExportAsync(sourceWithoutLayout, DatabaseDiagramExport.MermaidMarkdown, TestContextToken);
            Assert.Equal(source.ToArray(), sourceWithoutLayout.ToArray());
            using var admissionTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.OpenAsync(
                new DatabaseWorkerConnection("sqlite", connectionString), admissionTimeout.Token));
        }
        finally
        {
            if (registration is not null) { GhostShell.Databases.SqliteInMemoryDatabases.Unregister(registration); }
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Sampled_memory_watchdog_terminates_only_its_owned_worker()
    {
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var watchdog = new DiagramMemoryWatchdog(
            () => DiagramMemoryWatchdog.MaximumWorkingSetBytes + 1,
            () => terminated.TrySetResult(),
            TimeSpan.FromMilliseconds(10));
        await terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(watchdog.Exceeded);
    }

    [Fact]
    public async Task Disposing_the_watchdog_stops_future_samples_and_termination()
    {
        var stopped = 0;
        var watchdog = new DiagramMemoryWatchdog(
            () => DiagramMemoryWatchdog.MaximumWorkingSetBytes + 1,
            () => Interlocked.Increment(ref stopped),
            TimeSpan.FromHours(1));
        await watchdog.DisposeAsync();
        Assert.Equal(0, stopped);
    }

    [Fact]
    public async Task Protocol_rejects_oversized_frames_before_allocating_payload()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        using var input = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseDiagramProtocol.ReadFrameAsync(input, 1024, TestContextToken));
    }

    [Theory]
    [InlineData(0, 400, 1)]
    [InlineData(8192, 8192, 1)]
    [InlineData(640, 480, double.NaN)]
    public void Viewport_resource_bounds_are_explicit(int width, int height, double zoom) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseDiagramViewport(width, height, zoom, 0, 0).Validate());

    private static CancellationToken TestContextToken => CancellationToken.None;

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "global.json")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
