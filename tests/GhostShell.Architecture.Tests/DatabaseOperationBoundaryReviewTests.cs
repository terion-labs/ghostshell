using System.Diagnostics;
using GhostShell.Application;
using GhostShell.DatabaseBackend;
using GhostShell.Desktop;
using GhostShell.Files;
using GhostShell.Infrastructure;
using Microsoft.Data.Sqlite;

namespace GhostShell.Architecture.Tests;

public sealed class DatabaseOperationBoundaryReviewTests
{
    [Fact]
    public async Task PinnedDuckDbEnumReachesParentAsExactStringWithoutClrTypeReconstruction()
    {
        var directory = Directory.CreateTempSubdirectory("ghostshell-operation-enum-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) { root = root.Parent; }
        Assert.NotNull(root);
        var dotnet = Path.Combine(root.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        try
        {
            using var worker = new DatabaseOperationWorker(
                () => new DatabaseResultContentStore(Path.Combine(directory.FullName, "content")),
                new SelfReentryLaunch(dotnet, [typeof(Program).Assembly.Location], dotnet));
            using var result = await worker.QueryAsync(new("duckdb", "DataSource=:memory:"),
                "SELECT 'ok'::ENUM('sad', 'ok') AS mood", 1, false, timeout.Token);
            var value = Assert.Single(Assert.Single(result.ValueRows));
            Assert.Equal("ok", Assert.IsType<string>(value.RawValue));
            Assert.Equal("ok", value.DisplayText);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task PreparedStatementCannotExecuteWhenParentClosesBeforeExecute()
    {
        var directory = Directory.CreateTempSubdirectory("ghostshell-operation-boundary-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var database = Path.Combine(directory.FullName, "must-not-exist.db");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) { root = root.Parent; }
        Assert.NotNull(root);
        var start = new ProcessStartInfo(Path.Combine(root.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(DatabaseOperationWorker.Marker);
        using var child = Process.Start(start);
        Assert.NotNull(child);
        try
        {
            var errors = child.StandardError.ReadToEndAsync(timeout.Token);
            var connection = new DatabaseWorkerConnection("sqlite",
                new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            await DatabaseOperationProtocol.WriteMetadataAsync(child.StandardInput.BaseStream,
                new DatabaseOperationRequest(DatabaseWorkerOperation.Query, connection, directory.FullName, 1),
                DatabaseOperationJsonContext.Default.DatabaseOperationRequest, timeout.Token);
            await DatabaseOperationValueProtocol.WriteParameterAsync(child.StandardInput.BaseStream,
                "CREATE TABLE forbidden(value TEXT)", timeout.Token);
            await DatabaseOperationProtocol.ExpectAsync(child.StandardOutput.BaseStream, "ready"u8.ToArray(), timeout.Token);
            Assert.False(File.Exists(database));
            child.StandardInput.Close();
            await child.WaitForExitAsync(timeout.Token);
            Assert.Equal(70, child.ExitCode);
            Assert.Empty(await errors);
            Assert.False(File.Exists(database));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: false);
                await child.WaitForExitAsync(CancellationToken.None);
            }
            directory.Delete(recursive: true);
        }
    }
}
