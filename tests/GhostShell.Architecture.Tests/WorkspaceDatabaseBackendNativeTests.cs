using System.Diagnostics;
using GhostShell.Application;
using GhostShell.ConnectionBackend;
using GhostShell.Core;
using GhostShell.Desktop;
using GhostShell.Files;
using GhostShell.Infrastructure;
using Xunit.Abstractions;

namespace GhostShell.Architecture.Tests;

public sealed partial class WorkspaceDatabaseBackendNativeTests(ITestOutputHelper output)
{
    private const string GuestDatabase = "/home/ghostshell/workspace-backend-test.db";

    [WorkspaceBackendFact]
    public async Task Fresh_sdk_guest_installs_and_executes_database_operations_while_network_is_blocked()
    {
        var assets = Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_SDK_RUNTIME_ROOT")!;
        var archive = Environment.GetEnvironmentVariable("GHOSTSHELL_WORKSPACE_BACKEND_ARCHIVE")!;
        var descriptor = Path.Combine(Path.GetDirectoryName(archive)!, "backend-assets.json");
        Assert.True(File.Exists(archive), "Build the explicitly supplied Linux backend archive first.");
        Assert.True(File.Exists(descriptor), "The backend archive must have its pinned descriptor alongside it.");
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-arm64", "native",
            "ghostshell-workspace-gateway-darwin-arm64")), "Build and copy the host workspace gateway before running this fixture.");
        Assert.False(File.Exists(GuestDatabase));
        var directory = Directory.CreateTempSubdirectory("ghostshell-backend-sdk-test-");
        var provider = new WorkspaceSdkIsolationProvider(Path.Combine(assets, "workspace-runtime"),
            Path.Combine(directory.FullName, "sdk-state"));
        var workspaceId = new WorkspaceId($"backend-test-{Guid.NewGuid():N}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        WorkspaceIsolationBinding? binding = null;
        try
        {
            binding = Prepared(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(workspaceId), deadline.Token));
            var commands = new GuestCommands(provider, binding);
            output.WriteLine(await RunShellAsync(commands, "sed -n '/^MemTotal:/p' /proc/meminfo", [], deadline.Token));
            Assert.Equal("blocked", await RunShellAsync(commands, """
                if timeout 3 /bin/bash -c 'exec 3<>/dev/tcp/1.1.1.1/443'; then exit 1; fi
                printf blocked
                """, [], deadline.Token));
            await using var backend = new WorkspaceDatabaseBackend(commands, descriptor,
                Path.Combine(directory.FullName, "archive-cache"));
            var launch = await backend.PlanAsync(deadline.Token);
            await VerifyGuestProcessAsync(commands, launch, deadline.Token);
            using var worker = new DatabaseOperationWorker(
                () => new DatabaseResultContentStore(Path.Combine(directory.FullName, "results")),
                workspaceLaunch: backend.PlanAsync);
            await VerifyDatabaseAsync(worker, deadline.Token);
            await VerifyDuckDbAsync(worker, deadline.Token);
            await VerifyGuestFileProviderAsync(backend, commands, deadline.Token);
            await ProvisionGuestNetworkFixturesAsync(assets, binding, deadline.Token);
            await VerifyGuestSftpAsync(backend, commands, deadline.Token);
            await VerifyGuestRedisAndHttpAsync(backend, commands, deadline.Token);
            await VerifyCanceledSpillCleanupAsync(backend, commands, directory.FullName, deadline.Token);
            Assert.Equal("guest-file", await RunShellAsync(commands,
                "test -f \"$1\" && printf guest-file", [GuestDatabase], deadline.Token));
            Assert.False(File.Exists(GuestDatabase));
            Assert.True(commands.DuplexCalls >= 12);
            Assert.Equal("still-blocked", await RunShellAsync(commands, """
                if timeout 3 /bin/bash -c 'exec 3<>/dev/tcp/1.1.1.1/443'; then exit 1; fi
                printf still-blocked
                """, [], deadline.Token));
        }
        finally
        {
            if (binding is not null)
            {
                _ = Prepared(await provider.StopAsync(binding, CancellationToken.None));
            }
            // Only this test's fresh workspace and caches are removed.
            directory.Delete(recursive: true);
        }
    }

    private static async Task VerifyGuestProcessAsync(GuestCommands commands, DatabaseWorkspaceOperationLaunch launch, CancellationToken token)
    {
        var executable = commands.LastBackendExecutable;
        Assert.NotNull(executable);
        Assert.False(File.Exists(executable));
        using var process = Process.Start(launch.StartInfo) ?? throw new IOException("The guest backend test process did not start.");
        using var stop = token.Register(() => DatabaseOperationWorker.StopOwnedProcess(process));
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            // The backend is waiting for framed input. Inspect that exact executable
            // in the guest, rather than confusing the host exec bridge's PID for it.
            var identity = await RunShellAsync(commands, """
                set -eu
                for attempt in $(seq 1 50); do
                    for entry in /proc/[0-9]*/exe; do
                        if [ "$(readlink "$entry" 2>/dev/null || true)" = "$1" ]; then
                            uname -s
                            printf '%s\n' "$entry" "$1"
                            exit 0
                        fi
                    done
                    sleep 0.1
                done
                exit 1
                """, [executable], token);
            var lines = identity.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Linux", lines[0]);
            Assert.StartsWith("/proc/", lines[1], StringComparison.Ordinal);
            Assert.EndsWith("/exe", lines[1], StringComparison.Ordinal);
            Assert.Equal(executable, lines[2]);
        }
        finally
        {
            DatabaseOperationWorker.StopOwnedProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); }
            catch (IOException) when (process.HasExited)
            {
                // Killing the owned host bridge closes its redirected pipe handles.
            }
            await launch.CleanupAsync();
        }
    }

    private static async Task VerifyCanceledSpillCleanupAsync(WorkspaceDatabaseBackend backend, GuestCommands commands,
        string directory, CancellationToken token)
    {
        foreach (var disposeWorker in new[] { false, true })
        {
            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            commands.BackendOperationStarted = operationId => started.TrySetResult(operationId);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var worker = new DatabaseOperationWorker(
                () => new DatabaseResultContentStore(Path.Combine(directory, "cancel-results")), workspaceLaunch: backend.PlanAsync);
            // The first row spills a BLOB; producing the second row takes long
            // enough to cancel while the guest owns that actual spill file.
            var pending = worker.QueryAsync(new DatabaseWorkerConnection("sqlite", $"Data Source={GuestDatabase};Pooling=False"), """
                WITH RECURSIVE counter(value) AS (
                    VALUES(0) UNION ALL SELECT value + 1 FROM counter WHERE value < 1000000000
                )
                SELECT zeroblob(8388608) AS payload
                UNION ALL SELECT CAST(sum(value) AS BLOB) FROM counter
                """, 10, false, cancellation.Token);
            try
            {
                var operationId = await started.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                var scratch = "/tmp/ghostshell-database-operations/" + operationId;
                Assert.Equal("spilled", await RunShellAsync(commands, """
                    set -eu
                    for attempt in $(seq 1 200); do
                        for file in "$1"/content/session-*; do
                            if [ -f "$file" ] && [ "$(stat -c %s "$file")" -gt 1048576 ]; then
                                printf spilled
                                exit 0
                            fi
                        done
                        sleep 0.05
                    done
                    exit 1
                    """, [scratch], token));
                Assert.False(pending.IsCompleted, "The operation must still own its spill file when canceled.");
                if (disposeWorker)
                {
                    worker.Dispose();
                    await backend.DisposeAsync();
                    Assert.Equal("clean", await RunShellAsync(commands,
                        "test ! -e \"$1\" && printf clean", [scratch], token));
                }
                else { await cancellation.CancelAsync(); }
                await Assert.ThrowsAsync<DatabaseMutationOutcomeUnknownException>(async () =>
                {
                    using var result = await pending;
                });
                Assert.Equal("clean", await RunShellAsync(commands,
                    "test ! -e \"$1\" && printf clean", [scratch], token));
            }
            finally
            {
                commands.BackendOperationStarted = null;
                await cancellation.CancelAsync();
                try { using var result = await pending; }
                catch (Exception exception) when (exception is OperationCanceledException or DatabaseMutationOutcomeUnknownException) { }
            }
        }
    }

    private static async Task VerifyDatabaseAsync(IDatabaseOperationExecutor worker, CancellationToken token)
    {
        var connection = new DatabaseWorkerConnection("sqlite", $"Data Source={GuestDatabase};Pooling=False");
        var table = new DatabaseTableDescriptor("sample", DatabaseTableKind.Table, Schema: "main");
        using (await worker.QueryAsync(connection,
            "CREATE TABLE sample (id INTEGER PRIMARY KEY, text_value TEXT NOT NULL, blob_value BLOB)", 1, false, token)) { }
        var bytes = new byte[128 * 1024];
        for (var index = 0; index < bytes.Length; index++) { bytes[index] = (byte)(index % 251); }
        var inserted = await worker.ApplyTableChangesAsync(connection, table,
            new DatabaseTableChanges([new([
                new("text_value", DatabaseEditValueState.Value, "first"),
                new("blob_value", DatabaseEditValueState.Value, bytes)])], [], []), token);
        Assert.Equal(1, inserted.Inserted);
        var tables = await worker.ListTablesAsync(connection, token);
        Assert.Contains(tables, candidate => candidate.Name == table.Name);
        var details = await worker.GetObjectDetailsAsync(connection, table, token);
        Assert.True(details.CanEdit);
        Assert.Equal("id", Assert.Single(details.PrimaryKey).Name);
        Assert.Single((await worker.GetDatabaseSchemaGraphAsync(connection, token)).Tables);
        Assert.Single((await worker.GetSqlCatalogAsync(connection, token)).Objects);
        Assert.Empty(await worker.ListDatabasesAsync(connection, token));
        Assert.False(string.IsNullOrWhiteSpace((await worker.DescribeSessionAsync(connection, token)).ServerVersion));

        using var query = await worker.QueryAsync(connection, "SELECT * FROM sample", 10, true, token);
        var row = Assert.Single(query.ValueRows);
        Assert.Equal(1L, row[0].RawValue);
        Assert.Equal("first", row[1].RawValue);
        var binary = Assert.IsAssignableFrom<DatabaseValueContent>(row[2].RawValue);
        Assert.Equal(bytes.Length, binary.Length);
        await using (var stream = binary.OpenRead())
        {
            var actual = new byte[bytes.Length];
            await stream.ReadExactlyAsync(actual, token);
            Assert.Equal(bytes, actual);
        }
        Assert.Equal(1, await worker.CountQueryRowsAsync(connection, "SELECT * FROM sample", query.Columns, [], token));
        var queryPage = await worker.ReadQueryAsync(connection, "SELECT * FROM sample", query.Columns, DatabaseTableQuery.FirstPage(10), token);
        using (queryPage.Result) { Assert.Single(queryPage.Result.Rows); }
        var tablePage = await worker.ReadTableAsync(connection, table, DatabaseTableQuery.FirstPage(10), token);
        using (tablePage.Result) { Assert.Single(tablePage.Result.Rows); }
        var updated = await worker.ApplyTableChangesAsync(connection, table, new DatabaseTableChanges([], [new(
            [new("id", DatabaseEditValueState.Value, 1L)],
            [new("text_value", DatabaseEditValueState.Value, "second")],
            [new("text_value", DatabaseEditValueState.Value, "first")])], []), token);
        Assert.Equal(1, updated.Updated);
        var deleted = await worker.ApplyTableChangesAsync(connection, table, new DatabaseTableChanges([], [], [new(
            [new("id", DatabaseEditValueState.Value, 1L)],
            [new("text_value", DatabaseEditValueState.Value, "second")])]), token);
        Assert.Equal(1, deleted.Deleted);
        using var empty = await worker.QueryAsync(connection, "SELECT * FROM sample", 10, false, token);
        Assert.Empty(empty.Rows);
    }

    private static async Task VerifyDuckDbAsync(IDatabaseOperationExecutor worker, CancellationToken token)
    {
        const string guestFile = "/home/ghostshell/workspace-backend-test.duckdb";
        Assert.False(File.Exists(guestFile));
        var connection = new DatabaseWorkerConnection("duckdb", $"Data Source={guestFile}");
        using (await worker.QueryAsync(connection, "CREATE TABLE sample (value INTEGER)", 1, false, token)) { }
        using (await worker.QueryAsync(connection, "INSERT INTO sample VALUES (42)", 1, false, token)) { }
        using var result = await worker.QueryAsync(connection, "SELECT value FROM sample", 10, false, token);
        Assert.Equal(42, Assert.Single(result.ValueRows)[0].RawValue);
        Assert.False(File.Exists(guestFile));
    }

    private static async Task<string> RunShellAsync(GuestCommands commands, string script,
        IReadOnlyList<string> arguments, CancellationToken token)
    {
        var planned = await commands.PlanCommandAsync(BuiltInConnections.Local, "/bin/sh", ["-c", script, "backend-test", .. arguments], token);
        var launch = Assert.IsType<ConnectionRuntimeResult<TerminalLaunchRequest>.Success>(planned).Value;
        var start = new ProcessStartInfo(launch.Executable!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in launch.Arguments) { start.ArgumentList.Add(argument); }
        foreach (var (name, value) in launch.Environment) { start.Environment[name] = value; }
        using var process = Process.Start(start) ?? throw new IOException("The guest fixture command did not start.");
        using var stop = token.Register(() => DatabaseOperationWorker.StopOwnedProcess(process));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, $"Guest fixture command failed: {await error}");
        return await output;
    }

    private static WorkspaceIsolationBinding Prepared(WorkspaceIsolationResult<WorkspaceIsolationBinding> result)
    {
        Assert.True(result is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success,
            result is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure failure ? failure.Error.Message : "No workspace returned.");
        return ((WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success)result).Value;
    }

    private sealed class GuestCommands(IWorkspaceIsolationProvider provider, WorkspaceIsolationBinding binding) : IConnectionCommandRuntime
    {
        public int DuplexCalls { get; private set; }
        public string? LastBackendExecutable { get; private set; }
        public Action<string>? BackendOperationStarted { get; set; }

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(ConnectionProfile connection,
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            Plan(executable, arguments, WorkspaceProcessMode.None, cancellationToken);

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(ConnectionProfile connection,
            string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            DuplexCalls++;
            if (executable.EndsWith("/GhostShell.Backend", StringComparison.Ordinal))
            {
                LastBackendExecutable = executable;
                if (arguments is ["database", var operationId]) { BackendOperationStarted?.Invoke(operationId); }
            }
            return Plan(executable, arguments, WorkspaceProcessMode.Interactive, cancellationToken);
        }

        private ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> Plan(string executable,
            IReadOnlyList<string> arguments, WorkspaceProcessMode mode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var launch = Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success>(provider.CreateExecLaunch(binding,
                new WorkspaceIsolationProcessRequest(ConnectionKind.Local, executable, arguments, mode: mode))).Value;
            Assert.Equal(WorkspaceProcessMode.None, mode & WorkspaceProcessMode.AllocateTerminal);
            return ValueTask.FromResult(ConnectionRuntimeResult<TerminalLaunchRequest>.Succeed(new TerminalLaunchRequest(
                launch.HostWorkingDirectory, launch.Executable, launch.Arguments, launch.Environment)));
        }
    }

    private sealed class WorkspaceBackendFactAttribute : FactAttribute
    {
        public WorkspaceBackendFactAttribute()
        {
            if (!OperatingSystem.IsMacOS()
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_SDK_RUNTIME_ROOT"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GHOSTSHELL_WORKSPACE_BACKEND_ARCHIVE")))
            {
                Skip = "Requires an explicitly configured signed SDK runtime and packaged Linux backend on macOS.";
            }
        }
    }
}
