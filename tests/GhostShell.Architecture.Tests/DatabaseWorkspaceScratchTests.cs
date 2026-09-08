using System.Diagnostics;
using System.Reflection;
using GhostShell.Application;
using GhostShell.DatabaseBackend;
using GhostShell.Files;

namespace GhostShell.Architecture.Tests;

public sealed class DatabaseWorkspaceScratchTests
{
    [Theory]
    [InlineData("")]
    [InlineData("../other")]
    [InlineData("/tmp")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    public async Task RejectsAnythingExceptOpaqueLowercaseOperationId(string operationId)
    {
        Assert.Throws<InvalidDataException>(() => DatabaseWorkspaceScratch.Prepare(operationId));
        Assert.Throws<InvalidDataException>(() => DatabaseWorkspaceScratch.Acquire(operationId));
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseWorkspaceScratch.CleanupAsync(operationId, CancellationToken.None));
    }

    [Fact]
    public async Task CleanupReclaimsInterruptedPreparationWithoutLease()
    {
        var operationId = Guid.NewGuid().ToString("N");
        DatabaseWorkspaceScratch.Prepare(operationId);
        string scratchPath;
        using (var scratch = DatabaseWorkspaceScratch.Acquire(operationId)) { scratchPath = scratch.DirectoryPath; }
        File.Delete(Path.Combine(scratchPath, "lease"));
        await DatabaseWorkspaceScratch.CleanupAsync(operationId, CancellationToken.None);
        Assert.False(Directory.Exists(scratchPath));
    }

    [Fact]
    public async Task CleanupWaitsForLiveLeaseBeforeDeletingSpilledContent()
    {
        var operationId = Guid.NewGuid().ToString("N");
        DatabaseWorkspaceScratch.Prepare(operationId);
        using var scratch = DatabaseWorkspaceScratch.Acquire(operationId);
        await File.WriteAllBytesAsync(Path.Combine(scratch.DirectoryPath, "spill"), new byte[128 * 1024], CancellationToken.None);
        var cleanup = DatabaseWorkspaceScratch.CleanupAsync(operationId, CancellationToken.None);
        try
        {
            await Task.Delay(100, CancellationToken.None);
            Assert.False(cleanup.IsCompleted);
            Assert.True(Directory.Exists(scratch.DirectoryPath));
        }
        finally { scratch.Dispose(); }
        await cleanup;
        Assert.False(Directory.Exists(scratch.DirectoryPath));
        Assert.ThrowsAny<IOException>(() => DatabaseWorkspaceScratch.Acquire(operationId));
    }

    [Fact]
    public async Task CleanupWaitsForKilledChildAndRemovesActualResultSpill()
    {
        var operationId = Guid.NewGuid().ToString("N");
        DatabaseWorkspaceScratch.Prepare(operationId);
        string scratchPath;
        using (var scratch = DatabaseWorkspaceScratch.Acquire(operationId)) { scratchPath = scratch.DirectoryPath; }
        var start = BackendStart(operationId);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await DatabaseOperationProtocol.WriteMetadataAsync(process.StandardInput.BaseStream,
                new DatabaseOperationRequest(DatabaseWorkerOperation.Query, new("sqlite", "Data Source=:memory:"), string.Empty, MaximumRows: 1),
                DatabaseOperationJsonContext.Default.DatabaseOperationRequest, timeout.Token);
            await DatabaseOperationValueProtocol.WriteParameterAsync(process.StandardInput.BaseStream,
                "SELECT zeroblob(1048576)", timeout.Token);
            await DatabaseOperationProtocol.ExpectAsync(process.StandardOutput.BaseStream, "ready"u8.ToArray(), timeout.Token);
            await DatabaseOperationProtocol.WriteFrameAsync(process.StandardInput.BaseStream, "execute"u8.ToArray(), timeout.Token);
            await DatabaseOperationProtocol.ExpectAsync(process.StandardOutput.BaseStream, "result"u8.ToArray(), timeout.Token);
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(scratchPath, "content"), "session-*.db"));
            var cleanup = DatabaseWorkspaceScratch.CleanupAsync(operationId, timeout.Token);
            await Task.Delay(100, timeout.Token);
            Assert.False(cleanup.IsCompleted);
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync(timeout.Token);
            await cleanup;
            Assert.False(Directory.Exists(scratchPath));
        }
        finally
        {
            DatabaseOperationWorker.StopOwnedProcess(process);
            await process.WaitForExitAsync(CancellationToken.None);
            _ = await errors;
            await DatabaseWorkspaceScratch.CleanupAsync(operationId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailedProcessStartStillRunsOwnedCleanup()
    {
        var operationId = Guid.NewGuid().ToString("N");
        DatabaseWorkspaceScratch.Prepare(operationId);
        string scratchPath;
        using (var scratch = DatabaseWorkspaceScratch.Acquire(operationId)) { scratchPath = scratch.DirectoryPath; }
        using var worker = new DatabaseOperationWorker(() => new DatabaseResultContentStore(scratchPath),
            workspaceLaunch: _ => Task.FromResult(new DatabaseWorkspaceOperationLaunch(
                new ProcessStartInfo(Path.Combine(scratchPath, "missing-executable")),
                () => DatabaseWorkspaceScratch.CleanupAsync(operationId, CancellationToken.None))));
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => worker.ListTablesAsync(new("sqlite", "Data Source=:memory:"), CancellationToken.None));
        Assert.False(Directory.Exists(scratchPath));
    }

    [Fact]
    public async Task CleanupFailureDoesNotReplacePrimaryOperationFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-backend-{Guid.NewGuid():N}");
        using var worker = new DatabaseOperationWorker(() => throw new InvalidOperationException("No results expected."),
            workspaceLaunch: _ => Task.FromResult(new DatabaseWorkspaceOperationLaunch(new ProcessStartInfo(missing),
                () => throw new IOException("Cleanup also failed."))));
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => worker.ListTablesAsync(new("sqlite", "Data Source=:memory:"), CancellationToken.None));
    }

    private static ProcessStartInfo BackendStart(string operationId)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) { root = root.Parent; }
        var backend = typeof(DatabaseWorkspaceScratchTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => string.Equals(attribute.Key, "DatabaseBackendPath", StringComparison.Ordinal)).Value;
        var start = new ProcessStartInfo(Path.Combine(root!.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(backend!);
        start.ArgumentList.Add("database");
        start.ArgumentList.Add(operationId);
        return start;
    }
}
