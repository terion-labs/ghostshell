using System.Data.Common;
using System.Diagnostics;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Databases;
using GhostShell.Files;
using GhostShell.Infrastructure;

namespace GhostShell.ConnectionBackend;

/// <summary>
/// One owned child per complete provider operation. The ready/execute handshake
/// separates parameter decoding from execution; losing an executed operation is
/// never reported as rollback and never retried here.
/// </summary>
internal sealed partial class DatabaseOperationWorker(
    Func<DatabaseValueContentStore> createStore,
    SelfReentryLaunch? selfReentry = null,
    Func<Process, long>? sampleMemory = null,
    Func<CancellationToken, Task<DatabaseWorkspaceOperationLaunch>>? workspaceLaunch = null) : IDatabaseOperationExecutor, IDisposable
{
    internal const string Marker = "--ghostshell-database-operation-worker";
    internal const long MaximumWorkingSetBytes = 2L * 1024 * 1024 * 1024;
    private static readonly SemaphoreSlim Admission = new(2, 2);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public async Task<DatabaseQueryPage> QueryAsync(DatabaseWorkerConnection connection, string sql, int maximumRows,
        bool requestProvenance, CancellationToken cancellationToken)
    {
        var page = await RunAsync(new(DatabaseWorkerOperation.Query, connection, string.Empty, maximumRows, requestProvenance),
            stream => DatabaseOperationValueProtocol.WriteParameterAsync(stream, sql, cancellationToken),
            (stream, token) => DatabaseOperationProtocol.ReadResultAsync(stream, maximumRows, createStore, token),
            cancellationToken).ConfigureAwait(false);
        return page.Result;
    }

    public Task<DatabaseTablePage> ReadQueryAsync(DatabaseWorkerConnection connection, string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns, DatabaseTableQuery query, CancellationToken cancellationToken) =>
        RunAsync(new(DatabaseWorkerOperation.ReadQuery, connection, string.Empty, SourceColumns: sourceColumns.Count), async stream =>
        {
            await DatabaseOperationValueProtocol.WriteParameterAsync(stream, sourceSql, cancellationToken).ConfigureAwait(false);
            foreach (var column in sourceColumns)
            {
                await DatabaseOperationProtocol.WriteMetadataAsync(stream, column,
                    DatabaseOperationJsonContext.Default.DatabaseColumnDescriptor, cancellationToken).ConfigureAwait(false);
            }
            await DatabaseOperationProtocol.WriteQueryAsync(stream, query, cancellationToken).ConfigureAwait(false);
        }, (stream, token) => DatabaseOperationProtocol.ReadResultAsync(stream, query.Limit, createStore, token), cancellationToken);

    public Task<DatabaseTablePage> ReadTableAsync(DatabaseWorkerConnection connection, DatabaseTableDescriptor table,
        DatabaseTableQuery query, CancellationToken cancellationToken) =>
        RunAsync(new(DatabaseWorkerOperation.ReadTable, connection, string.Empty, Table: table),
            stream => DatabaseOperationProtocol.WriteQueryAsync(stream, query, cancellationToken),
            (stream, token) => DatabaseOperationProtocol.ReadResultAsync(stream, query.Limit, createStore, token), cancellationToken);

    public Task<DatabaseMutationResult> ApplyTableChangesAsync(DatabaseWorkerConnection connection, DatabaseTableDescriptor table,
        DatabaseTableChanges changes, CancellationToken cancellationToken) =>
        RunAsync(new(DatabaseWorkerOperation.ApplyChanges, connection, string.Empty, Table: table),
            stream => DatabaseOperationProtocol.WriteChangesAsync(stream, changes, cancellationToken),
            async (stream, token) =>
            {
                var result = await DatabaseOperationProtocol.ReadMetadataAsync(stream,
                    DatabaseOperationJsonContext.Default.DatabaseMutationResult, token).ConfigureAwait(false);
                await DatabaseOperationProtocol.ExpectAsync(stream, "complete"u8.ToArray(), token).ConfigureAwait(false);
                return result;
            }, cancellationToken);

    private async Task<T> RunAsync<T>(DatabaseOperationRequest request, Func<Stream, Task> writeParameters,
        Func<Stream, CancellationToken, Task<T>> readResult, CancellationToken token)
    {
        if (string.Equals(request.Connection.DriverId, "sqlserver", StringComparison.Ordinal) && request.Connection.LocalRoutePort is not null && request.Connection.Route is null)
        {
            throw new NotSupportedException("Routed SQL Server requires a captured endpoint transport, not a fixed loopback port.");
        }
        await using var previewImage = DatabaseWorkerSqliteSnapshot.Borrow(request.Connection);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token,
            request.Connection.Route?.Lifetime ?? CancellationToken.None);
        await Admission.WaitAsync(cancellation.Token).ConfigureAwait(false);
        DirectoryInfo? directory = null;
        DatabaseWorkspaceOperationLaunch? workspaceOperation = null;
        var operationFailed = false;
        T? completedResult = default;
        try
        {
            directory = Directory.CreateTempSubdirectory("ghostshell-database-worker-");
            PrivateContentPathGuard.ValidatePrivateDirectory(directory.FullName);
            if (workspaceLaunch is not null && (request.Connection.Route is not null || request.Connection.LocalRoutePort is not null))
            {
                throw new InvalidOperationException("A workspace backend cannot use host loopback routes.");
            }
            workspaceOperation = workspaceLaunch is null ? null : await workspaceLaunch(cancellation.Token).ConfigureAwait(false);
            var start = workspaceOperation?.StartInfo ?? CreateLocalLaunch();
            start.UseShellExecute = false;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.CreateNoWindow = true;
            using var process = Process.Start(start) ?? throw new IOException("The database operation worker could not start.");
            using var terminate = cancellation.Token.Register(() => StopOwnedProcess(process));
            var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellation.Token);
            await using var monitor = new MemoryMonitor(process, sampleMemory);
            var routing = new DatabaseWorkerRouting(request.Connection.Route);
            var dispatched = false;
            try
            {
                await DatabaseOperationProtocol.WriteMetadataAsync(process.StandardInput.BaseStream,
                    request with
                    {
                        ContentDirectory = workspaceLaunch is null ? directory.FullName : string.Empty,
                        DynamicRoute = request.Connection.Route is not null,
                        SqliteSnapshotBytes = previewImage?.Length,
                        Connection = previewImage is null ? request.Connection
                            : request.Connection with { ConnectionString = "Data Source=:memory:" },
                    },
                    DatabaseOperationJsonContext.Default.DatabaseOperationRequest, cancellation.Token).ConfigureAwait(false);
                if (previewImage is not null)
                {
                    await DatabaseWorkerSqliteSnapshot.SendAsync(previewImage, process.StandardOutput.BaseStream,
                        process.StandardInput.BaseStream, cancellation.Token).ConfigureAwait(false);
                }
                await writeParameters(process.StandardInput.BaseStream).ConfigureAwait(false);
                var ready = await routing.ReadControlAsync(process.StandardOutput.BaseStream, process.StandardInput.BaseStream,
                    allowEndpointOpen: false, cancellation.Token).ConfigureAwait(false);
                if (ready.AsSpan().SequenceEqual("unsupported-array"u8))
                {
                    throw new NotSupportedException("This runtime cannot reconstruct an array with these bounds. No database statement was executed. Use a supported runtime to apply this value.");
                }
                if (!ready.AsSpan().SequenceEqual("ready"u8))
                {
                    throw new InvalidDataException("The database worker could not prepare the operation.");
                }
                cancellation.Token.ThrowIfCancellationRequested();
                // Set before writing: a partial/failed write can still reach the child.
                dispatched = true;
                await DatabaseOperationProtocol.WriteFrameAsync(process.StandardInput.BaseStream, "execute"u8.ToArray(), cancellation.Token).ConfigureAwait(false);
                var response = await routing.ReadControlAsync(process.StandardOutput.BaseStream, process.StandardInput.BaseStream,
                    allowEndpointOpen: true, cancellation.Token).ConfigureAwait(false);
                if (response.AsSpan().SequenceEqual("endpoint-budget"u8)) { throw new DatabaseRouteEndpointBudgetException(); }
                if (response.AsSpan().SequenceEqual("provider-failed"u8))
                {
                    var diagnostic = await DatabaseOperationProtocol.ReadMetadataAsync(process.StandardOutput.BaseStream,
                        DatabaseOperationJsonContext.Default.DatabaseProviderDiagnostic, cancellation.Token).ConfigureAwait(false);
                    await DatabaseOperationProtocol.ExpectAsync(process.StandardOutput.BaseStream, "complete"u8.ToArray(), cancellation.Token).ConfigureAwait(false);
                    throw new DatabaseProviderOperationException(diagnostic);
                }
                if (!response.AsSpan().SequenceEqual("result"u8)) { throw new InvalidDataException("The database operation response is invalid."); }
                completedResult = await readResult(process.StandardOutput.BaseStream, cancellation.Token).ConfigureAwait(false);
                return completedResult;
            }
            catch (Exception exception)
            {
                operationFailed = true;
                if (!dispatched || exception is DatabaseProviderOperationException) { throw; }
                var reason = monitor.Exceeded
                    ? "The database operation exceeded its 2 GiB worker budget."
                    : exception is DatabaseRouteEndpointBudgetException
                        ? "The database operation reached its 32 retained route-endpoint budget."
                    : exception is DatabaseResultRetentionException
                        ? "The result exceeded its available column and display metadata budget, capped at 256 MiB."
                        : "The database worker stopped before confirming the complete outcome.";
                throw new DatabaseMutationOutcomeUnknownException(reason
                    + " The query or mutation may have completed. Reload and verify before retrying.", exception);
            }
            finally
            {
                await RunCleanupAsync(operationFailed,
                    () => cancellation.CancelAsync(),
                    () => { StopOwnedProcess(process); return Task.CompletedTask; },
                    async () => await monitor.DisposeAsync().ConfigureAwait(false),
                    async () =>
                    {
                        try { await drain.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                        catch (IOException) when (cancellation.IsCancellationRequested) { }
                    },
                    async () => await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false),
                    async () => await routing.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
        catch
        {
            operationFailed = true;
            if (completedResult is DatabaseTablePage page) { page.Result.Dispose(); }
            throw;
        }
        finally
        {
            try
            {
                await RunCleanupAsync(operationFailed,
                    () => workspaceOperation?.CleanupAsync() ?? Task.CompletedTask,
                    () =>
                    {
                        DeleteWorkerDirectory(directory, (completedResult as DatabaseTablePage)?.Result);
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
            }
            finally { Admission.Release(); }
        }
    }

    private static void DeleteWorkerDirectory(DirectoryInfo? directory, DatabaseQueryPage? unpublishedResult)
    {
        try { directory?.Delete(recursive: true); }
        catch
        {
            unpublishedResult?.Dispose();
            throw;
        }
    }

    // Cleanup steps are independent: a failed cancellation callback or pipe close
    // must not skip reaping, nor replace a more useful execution-outcome error.
    internal static async Task RunCleanupAsync(bool operationFailed, params Func<Task>[] steps)
    {
        var cleanupFailed = false;
        foreach (var step in steps)
        {
            try { await step().ConfigureAwait(false); }
            catch
            {
                cleanupFailed = true;
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "database.operation.cleanup.failed", SecretSafeDiagnosticKind.Unexpected);
            }
        }
        if (cleanupFailed && !operationFailed)
        {
            throw new IOException("The database operation completed, but its local worker cleanup failed. Do not repeat the SQL to retry cleanup.");
        }
    }

    private sealed class MemoryMonitor : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;
        private int _disposed;
        public bool Exceeded { get; private set; }
        public MemoryMonitor(Process process, Func<Process, long>? sample) => _task = RunAsync(process, sample);

        private async Task RunAsync(Process process, Func<Process, long>? sample)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    process.Refresh();
                    if ((sample?.Invoke(process) ?? process.WorkingSet64) > MaximumWorkingSetBytes)
                    {
                        Exceeded = true;
                        StopOwnedProcess(process);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (InvalidOperationException) { /* The owned child exited between samples. */ }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            await _stop.CancelAsync().ConfigureAwait(false);
            await _task.ConfigureAwait(false);
            _stop.Dispose();
        }
    }

    internal static void StopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: false); }
        }
        catch (InvalidOperationException) { /* Already exited or disposed by its owner. */ }
        catch (System.ComponentModel.Win32Exception)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "database.operation.termination.failed", SecretSafeDiagnosticKind.Unexpected);
        }
        try { process.StandardInput.Close(); }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        try { process.StandardOutput.Close(); }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private ProcessStartInfo CreateLocalLaunch()
    {
        var launch = selfReentry ?? SelfReentryLaunch.Detect();
        var start = new ProcessStartInfo(launch.Executable);
        foreach (var prefix in launch.PrefixArguments) { start.ArgumentList.Add(prefix); }
        start.ArgumentList.Add(Marker);
        return start;
    }

    public static async Task<int> RunChildAsync(bool privateWorkspace = false, string? workspaceOperationId = null)
    {
        await using var input = Console.OpenStandardInput();
        await using var output = Console.OpenStandardOutput();
        using var workspaceScratch = privateWorkspace ? DatabaseWorkspaceScratch.Acquire(
            workspaceOperationId ?? throw new InvalidDataException("The workspace operation identifier is missing.")) : null;
        using var ownProcess = privateWorkspace ? Process.GetCurrentProcess() : null;
        await using var workspaceMemory = ownProcess is null ? null : new MemoryMonitor(ownProcess, null);
        try
        {
            var request = await DatabaseOperationProtocol.ReadMetadataAsync(input,
                DatabaseOperationJsonContext.Default.DatabaseOperationRequest, CancellationToken.None).ConfigureAwait(false);
            if (!Enum.IsDefined(request.Operation) || request.Connection is null
                || string.IsNullOrWhiteSpace(request.Connection.DriverId) || string.IsNullOrWhiteSpace(request.Connection.ConnectionString)
                || request.Connection.LocalRoutePort is < 1 or > 65535
                || (string.Equals(request.Connection.DriverId, "sqlserver", StringComparison.Ordinal) && request.Connection.LocalRoutePort is not null && !request.DynamicRoute))
            {
                return 64;
            }
            if (request.SqliteSnapshotBytes is not null && !string.Equals(request.Connection.DriverId, "sqlite", StringComparison.Ordinal))
            {
                return 64;
            }
            if (privateWorkspace && (request.DynamicRoute || request.Connection.LocalRoutePort is not null || request.ContentDirectory.Length != 0))
            {
                return 64;
            }
            using var previewImage = request.SqliteSnapshotBytes is { } length
                ? await DatabaseWorkerSqliteSnapshot.ReceiveAsync(length, input, output, CancellationToken.None).ConfigureAwait(false)
                : null;
            if (previewImage is not null)
            {
                request = request with { Connection = request.Connection with { ConnectionString = previewImage.ConnectionString } };
            }
            if (privateWorkspace)
            {
                request = request with { ContentDirectory = workspaceScratch!.ContentDirectoryPath };
            }
            PrivateContentPathGuard.ValidatePrivateDirectory(request.ContentDirectory);
            Parameters parameters;
            try { parameters = await ReadParametersAsync(input, request).ConfigureAwait(false); }
            catch (PlatformNotSupportedException)
            {
                await DatabaseOperationProtocol.WriteFrameAsync(output, "unsupported-array"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                return 65;
            }
            await DatabaseOperationProtocol.WriteFrameAsync(output, "ready"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            await DatabaseOperationProtocol.ExpectAsync(input, "execute"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            await using var parentRoute = request.DynamicRoute ? new ParentEndpointTunnelFactory(input, output) : null;
            ChildResult result;
            try { result = await ExecuteChildAsync(request, parameters, parentRoute).ConfigureAwait(false); }
            catch (DatabaseRouteEndpointBudgetException)
            {
                await DatabaseOperationProtocol.WriteFrameAsync(output, "endpoint-budget"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                return 75;
            }
            catch (DatabaseProviderOperationException failure)
            {
                await DatabaseOperationProtocol.WriteFrameAsync(output, "provider-failed"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                await DatabaseOperationProtocol.WriteMetadataAsync(output, failure.Diagnostic,
                    DatabaseOperationJsonContext.Default.DatabaseProviderDiagnostic, CancellationToken.None).ConfigureAwait(false);
                await DatabaseOperationProtocol.WriteFrameAsync(output, "complete"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                return 0;
            }
            using (result)
            {
                await DatabaseOperationProtocol.WriteFrameAsync(output, "result"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                if (result.WriteMetadata is { } writeMetadata)
                {
                    await writeMetadata(output).ConfigureAwait(false);
                }
                else if (result.Mutation is { } mutation)
                {
                    await DatabaseOperationProtocol.WriteMetadataAsync(output, mutation,
                        DatabaseOperationJsonContext.Default.DatabaseMutationResult, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await DatabaseOperationProtocol.WriteResultAsync(output, result.Result!, result.Page, CancellationToken.None).ConfigureAwait(false);
                }
            }
            await DatabaseOperationProtocol.WriteFrameAsync(output, "complete"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch
        {
            // Provider failures can contain SQL, credentials and server values.
            // No exception text crosses stderr or the protocol boundary.
            return 70;
        }
    }

    private sealed record Parameters(string? Sql, IReadOnlyList<DatabaseColumnDescriptor> Columns,
        DatabaseTableQuery? Query, DatabaseTableChanges? Changes);

    private static async Task<Parameters> ReadParametersAsync(Stream input, DatabaseOperationRequest request)
    {
        string? sql = null;
        var columns = new List<DatabaseColumnDescriptor>();
        DatabaseTableQuery? query = null;
        DatabaseTableChanges? changes = null;
        if (request.Operation is DatabaseWorkerOperation.Query or DatabaseWorkerOperation.ReadQuery or DatabaseWorkerOperation.CountQueryRows)
        {
            sql = await DatabaseOperationValueProtocol.ReadParameterAsync(input, CancellationToken.None).ConfigureAwait(false) as string
                ?? throw new InvalidDataException("The database statement is invalid.");
        }
        DatabaseOperationProtocol.ValidateCount(request.SourceColumns, DatabaseOperationProtocol.MaximumColumns);
        for (var index = 0; index < request.SourceColumns; index++)
        {
            columns.Add(await DatabaseOperationProtocol.ReadMetadataAsync(input,
                DatabaseOperationJsonContext.Default.DatabaseColumnDescriptor, CancellationToken.None).ConfigureAwait(false));
        }
        if (request.Operation is DatabaseWorkerOperation.ReadQuery or DatabaseWorkerOperation.ReadTable or DatabaseWorkerOperation.CountQueryRows)
        {
            query = await DatabaseOperationProtocol.ReadQueryAsync(input, CancellationToken.None).ConfigureAwait(false);
        }
        if (request.Operation == DatabaseWorkerOperation.ApplyChanges)
        {
            changes = await DatabaseOperationProtocol.ReadChangesAsync(input, CancellationToken.None).ConfigureAwait(false);
        }
        return new(sql, columns, query, changes);
    }

    private sealed record ChildResult(DatabaseQueryPage? Result = null, DatabaseTablePage? Page = null,
        DatabaseMutationResult? Mutation = null, Func<Stream, Task>? WriteMetadata = null) : IDisposable
    {
        public void Dispose() => Result?.Dispose();
    }

    private static async Task<ChildResult> ExecuteChildAsync(DatabaseOperationRequest request, Parameters parameters,
        IDatabaseTunnelFactory? parentRoute)
    {
        var target = request.Connection;
        await using var client = new DatabasePanelClient(
            parentRoute ?? (target.LocalRoutePort is { } port ? new ParentOwnedRoute(port) : null),
            parentRoute is not null || target.LocalRoutePort is not null ? BuiltInConnections.Local : null,
            contentStoreFactory: () => new DatabaseResultContentStore(request.ContentDirectory));
        try
        {
            if (await ExecuteMetadataAsync(client, request, parameters).ConfigureAwait(false) is { } metadata)
            {
                return metadata;
            }
            if (request.Operation == DatabaseWorkerOperation.ApplyChanges)
            {
                var mutation = await client.ApplyTableChangesAsync(target.DriverId, target.ConnectionString, null,
                    request.Table ?? throw new InvalidDataException("A table is required."), parameters.Changes!, CancellationToken.None).ConfigureAwait(false);
                return new(Mutation: mutation);
            }
            DatabaseTablePage? page = null;
            DatabaseQueryPage result;
            if (request.Operation == DatabaseWorkerOperation.Query)
            {
                result = request.Provenance
                    ? await client.QueryWithProvenanceAsync(target.DriverId, target.ConnectionString, null, parameters.Sql!, request.MaximumRows, CancellationToken.None).ConfigureAwait(false)
                    : await client.QueryAsync(target.DriverId, target.ConnectionString, null, parameters.Sql!, request.MaximumRows, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                page = request.Operation == DatabaseWorkerOperation.ReadQuery
                    ? await client.ReadQueryAsync(target.DriverId, target.ConnectionString, null, parameters.Sql!, parameters.Columns, parameters.Query!, CancellationToken.None).ConfigureAwait(false)
                    : await client.ReadTableAsync(target.DriverId, target.ConnectionString, null,
                        request.Table ?? throw new InvalidDataException("A table is required."), parameters.Query!, CancellationToken.None).ConfigureAwait(false);
                result = page.Result;
            }
            return new(result, page);
        }
        catch (Exception exception) when (DatabaseRouteEndpointBudgetException.IsCauseOf(exception))
        {
            throw new DatabaseRouteEndpointBudgetException();
        }
        catch (Exception exception) when (exception is DbException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new DatabaseProviderOperationException(DatabaseProviderDiagnostic.Create(exception, target, client));
        }
    }

    // The child can use only the initial parent-held forward. Redirect routing
    // requires a separate approved endpoint callback, never reuse of this port.
    private sealed class ParentOwnedRoute(int port) : IDatabaseTunnelFactory
    {
        public ValueTask<IDatabaseTunnelLease> OpenAsync(ConnectionProfile connection, string targetHost, int targetPort, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IDatabaseTunnelLease>(new RouteLease(port));
        }
    }
    private sealed class RouteLease(int port) : IDatabaseTunnelLease
    {
        public int LocalPort => port;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
