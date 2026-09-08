using System.Buffers.Binary;
using System.Diagnostics;
using GhostShell.Application;
using GhostShell.Desktop;
using GhostShell.Files;
using GhostShell.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace GhostShell.Architecture.Tests;

public sealed class DatabaseOperationWorkerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FixedPortSqlServerCannotEnterOperationOrDiagramWorkers()
    {
        using var fixture = new Fixture();
        using var worker = fixture.Worker();
        var connection = new DatabaseWorkerConnection("sqlserver", "Server=db.internal,1433", 44001);
        await Assert.ThrowsAsync<NotSupportedException>(() => worker.QueryAsync(connection, "SELECT 1", 1, false, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => new DatabaseDiagramWorker().OpenAsync(connection, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotTransferCannotExecuteSqlBeforeCompleteImageAndExecuteAcknowledgement(bool completeImage)
    {
        using var fixture = new Fixture();
        await fixture.ExecuteAsync("CREATE TABLE people(name TEXT); INSERT INTO people VALUES ('ada');");
        var original = await File.ReadAllBytesAsync(fixture.Path, CancellationToken.None);
        var image = completeImage ? new byte[DatabaseWorkerSqliteSnapshot.MaximumBytes] : original;
        if (completeImage) { original.CopyTo(image, 0); }
        var target = GhostShell.Databases.SqliteInMemoryDatabases.Register(image);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) { root = root.Parent; }
        var start = new ProcessStartInfo(Path.Combine(root!.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(DatabaseOperationWorker).Assembly.Location);
        start.ArgumentList.Add(DatabaseOperationWorker.Marker);
        using var child = Process.Start(start)!;
        using var sampling = new CancellationTokenSource();
        long observedPeak = 0;
        var samples = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(sampling.Token))
                {
                    child.Refresh();
                    observedPeak = Math.Max(observedPeak, child.WorkingSet64);
                }
            }
            catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
            catch (InvalidOperationException) { /* The owned process has exited. */ }
        }, CancellationToken.None);
        try
        {
            var errors = child.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await using var borrowed = DatabaseWorkerSqliteSnapshot.Borrow(new("sqlite", target))!;
            GhostShell.Databases.SqliteInMemoryDatabases.Unregister(target);
            await DatabaseOperationProtocol.WriteMetadataAsync(child.StandardInput.BaseStream,
                new DatabaseOperationRequest(DatabaseWorkerOperation.Query, new("sqlite", "Data Source=:memory:"),
                    Path.GetDirectoryName(fixture.Path)!, 1, SqliteSnapshotBytes: image.Length),
                DatabaseOperationJsonContext.Default.DatabaseOperationRequest, timeout.Token);
            var watch = Stopwatch.StartNew();
            if (completeImage)
            {
                await DatabaseWorkerSqliteSnapshot.SendAsync(borrowed, child.StandardOutput.BaseStream,
                    child.StandardInput.BaseStream, timeout.Token);
                await DatabaseOperationValueProtocol.WriteParameterAsync(child.StandardInput.BaseStream,
                    "SELECT count(*) FROM people", timeout.Token);
                await DatabaseOperationProtocol.ExpectAsync(child.StandardOutput.BaseStream, "ready"u8.ToArray(), timeout.Token);
                await sampling.CancelAsync();
                await samples;
                child.Refresh();
                using var parent = Process.GetCurrentProcess();
                parent.Refresh();
                output.WriteLine($"Complete preview at pre-execute readiness: {image.Length} bytes, {watch.ElapsedMilliseconds} ms; child working set {child.WorkingSet64} bytes, observed child peak {Math.Max(observedPeak, child.WorkingSet64)} bytes at 10 ms sampling, parent working set {parent.WorkingSet64} bytes. Sampling is not a reservation or guaranteed transient peak.");
            }
            else
            {
                await DatabaseOperationProtocol.ExpectAsync(child.StandardOutput.BaseStream, "snapshot-ready"u8.ToArray(), timeout.Token);
                await DatabaseOperationProtocol.WriteFrameAsync(child.StandardInput.BaseStream, image.AsMemory(0, 4), timeout.Token);
            }
            // Closing either during transfer or after readiness must never send
            // execute; the child releases its private registration and exits.
            child.StandardInput.Close();
            await child.WaitForExitAsync(timeout.Token);
            await errors;
            Assert.Equal(70, child.ExitCode);
            Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM people"));
        }
        finally
        {
            await sampling.CancelAsync();
            await samples;
            GhostShell.Databases.SqliteInMemoryDatabases.Unregister(target);
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: false);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteSqlitePreviewImageIsCompleteAndReadOnlyInActualWorker(bool maximumImage)
    {
        using var fixture = new Fixture();
        await fixture.ExecuteAsync("CREATE TABLE people(name TEXT); INSERT INTO people VALUES ('ada'), ('lin');");
        var original = await File.ReadAllBytesAsync(fixture.Path, CancellationToken.None);
        var image = maximumImage ? new byte[DatabaseWorkerSqliteSnapshot.MaximumBytes] : original;
        if (maximumImage) { original.CopyTo(image, 0); }
        var target = GhostShell.Databases.SqliteInMemoryDatabases.Register(image);
        try
        {
            long peakChildWorkingSet = 0;
            using var parent = Process.GetCurrentProcess();
            parent.Refresh();
            var initialParentWorkingSet = parent.WorkingSet64;
            var peakParentWorkingSet = initialParentWorkingSet;
            using var worker = fixture.Worker(sample: child =>
            {
                child.Refresh();
                parent.Refresh();
                peakChildWorkingSet = Math.Max(peakChildWorkingSet, child.WorkingSet64);
                peakParentWorkingSet = Math.Max(peakParentWorkingSet, parent.WorkingSet64);
                return child.WorkingSet64;
            });
            var connection = new DatabaseWorkerConnection("sqlite", target);
            var watch = Stopwatch.StartNew();
            using var result = await worker.QueryAsync(connection, "SELECT count(*) FROM people", 1, false, CancellationToken.None);
            output.WriteLine($"Preview image transfer/query: {image.Length} bytes, {watch.ElapsedMilliseconds} ms; sampled child peak {peakChildWorkingSet} bytes, parent peak {peakParentWorkingSet} bytes, parent before transfer {initialParentWorkingSet} bytes. Sampling is not a hard peak bound.");
            Assert.Equal(2L, result.ValueRows[0][0].RawValue);
            if (!maximumImage)
            {
                var refusal = await Assert.ThrowsAsync<DatabaseProviderOperationException>(() => worker.QueryAsync(connection,
                    "INSERT INTO people VALUES ('eve')", 1, false, CancellationToken.None));
                Assert.Contains("readonly", refusal.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { GhostShell.Databases.SqliteInMemoryDatabases.Unregister(target); }
    }

    [Fact]
    public async Task RealWorkerRouteRevocationAfterExecutePreservesUnknownOutcomeWithoutRetry()
    {
        using var fixture = new Fixture();
        using var worker = fixture.Worker();
        using var routeLifetime = new CancellationTokenSource();
        var opens = 0;
        var connection = new DatabaseWorkerConnection("postgres", "Host=private-route.invalid;Port=15432;Username=fixture;Password=private-fixture")
        {
            Route = new DatabaseWorkerRoute((_, _, _) =>
            {
                ++opens;
                routeLifetime.Cancel();
                throw new ObjectDisposedException("revoked-route");
            }, routeLifetime.Token),
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<DatabaseMutationOutcomeUnknownException>(() => worker.QueryAsync(connection,
            "SELECT 1", 1, false, deadline.Token));
        Assert.Equal(1, opens);
        Assert.Contains("may have completed", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("revoked-route", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealWorkerRequestsParentEndpointAfterExecuteAndCannotFallBackOnDenial()
    {
        using var fixture = new Fixture();
        using var worker = fixture.Worker();
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
        await Assert.ThrowsAsync<DatabaseMutationOutcomeUnknownException>(() => worker.QueryAsync(connection,
            "SELECT 1", 1, false, deadline.Token));
        Assert.Equal([("private-route.invalid", 15432)], requested);
    }

    [Fact]
    public async Task CleanupAttemptsEveryStepWithoutReplacingThePrimaryFailure()
    {
        var attempted = new List<int>();
        await DatabaseOperationWorker.RunCleanupAsync(operationFailed: true,
            () => { attempted.Add(1); throw new IOException("synthetic cleanup detail"); },
            () => { attempted.Add(2); return Task.CompletedTask; },
            () => { attempted.Add(3); throw new TimeoutException(); });
        Assert.Equal([1, 2, 3], attempted);
    }

    [Fact]
    public async Task CleanupFailureAfterSuccessIsVisibleWithoutSkippingRemainingSteps()
    {
        var reaped = false;
        var error = await Assert.ThrowsAsync<IOException>(() => DatabaseOperationWorker.RunCleanupAsync(operationFailed: false,
            () => throw new IOException("synthetic secret must not become the operation message"),
            () => { reaped = true; return Task.CompletedTask; }));
        Assert.True(reaped);
        Assert.Contains("operation completed", error.Message, StringComparison.Ordinal);
        Assert.Contains("Do not repeat", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlScriptsAndFilterValuesLargerThanMetadataFramesRemainComplete()
    {
        using var fixture = new Fixture();
        using var worker = fixture.Worker();
        var text = new string('x', 2 * 1024 * 1024 + 17);
        using var result = await worker.QueryAsync(fixture.Connection,
            "SELECT '" + text + "' AS complete_text", 1, false, CancellationToken.None);
        Assert.Equal(text.Length, Assert.IsAssignableFrom<DatabaseValueContent>(result.ValueRows[0][0].RawValue).Length);
        var query = new DatabaseTableQuery([new("complete_text", DatabaseFilterOperator.Equal, text)], [], 0, 1);
        var filtered = await worker.ReadQueryAsync(fixture.Connection,
            "SELECT '" + text + "' AS complete_text", result.Columns, query, CancellationToken.None);
        using (filtered.Result)
        {
            Assert.Single(filtered.Result.Rows);
            var content = Assert.IsAssignableFrom<DatabaseValueContent>(filtered.Result.ValueRows[0][0].RawValue);
            Assert.Equal(text.Length, content.Length);
            using var reader = new StreamReader(content.OpenRead());
            Assert.Equal(text, await reader.ReadToEndAsync(CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(5000)]
    public async Task OrdinaryWidePagesRetainTheirExistingRowAndColumnLimits(int rows)
    {
        using var fixture = new Fixture();
        using var worker = fixture.Worker();
        var columns = string.Join(",", Enumerable.Range(0, 100).Select(index => $"n AS column_{index}"));
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var watch = Stopwatch.StartNew();
        using var result = await worker.QueryAsync(fixture.Connection,
            $"WITH RECURSIVE page(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM page WHERE n<{rows}) SELECT {columns} FROM page",
            rows, false, CancellationToken.None);
        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        Assert.Equal(rows, result.ValueRows.Count);
        Assert.Equal(100, result.Columns.Count);
        Assert.Equal(rows.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Rows[^1][99]);
        output.WriteLine($"rows={rows},columns=100,retainedManagedBytes={retained},elapsedMs={watch.ElapsedMilliseconds}");
    }

    [Fact]
    public async Task ExcessiveResultMetadataFailsBeforeCreatingRowsOrContent()
    {
        using var wire = new MemoryStream();
        await DatabaseOperationProtocol.WriteMetadataAsync(wire,
            new DatabaseResultShape(10000, 5000, false, 0, 1),
            DatabaseOperationJsonContext.Default.DatabaseResultShape, CancellationToken.None);
        wire.Position = 0;
        await Assert.ThrowsAsync<DatabaseResultRetentionException>(() => DatabaseOperationProtocol.ReadResultAsync(wire, 5000,
            () => throw new InvalidOperationException("No store should be created."), CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 0, 0, 268435456)]
    [InlineData(1073741824, 536870912, 268435456, 134217728)]
    [InlineData(1073741824, 268435456, 536870912, 134217728)]
    [InlineData(1073741824, 1073741824, 536870912, 0)]
    [InlineData(8589934592, 1073741824, 536870912, 268435456)]
    public void HeadroomBudgetIsConservativeWithoutDoubleCounting(long available, long load, long process, long expected) =>
        Assert.Equal(expected, DatabaseOperationProtocol.MetadataBudget(available, load, process));

    [Fact]
    public async Task LowHeadroomRejectsWholeResultRatherThanReturningPartialRows()
    {
        using var wire = new MemoryStream();
        await DatabaseOperationProtocol.WriteMetadataAsync(wire,
            new DatabaseResultShape(10, 100, false, 0, 1),
            DatabaseOperationJsonContext.Default.DatabaseResultShape, CancellationToken.None);
        wire.Position = 0;
        await Assert.ThrowsAsync<DatabaseResultRetentionException>(() => DatabaseOperationProtocol.ReadResultAsync(wire, 100,
            () => throw new InvalidOperationException("No store should be created."), CancellationToken.None, metadataBudget: 1024));
    }

    [Fact]
    public async Task RealWorkerTransfersCompleteLargeValuesAndStructuredPages()
    {
        using var fixture = new Fixture();
        await fixture.ExecuteAsync("CREATE TABLE sample(id INTEGER PRIMARY KEY, text_value TEXT); INSERT INTO sample VALUES(1,'first');");
        using var worker = fixture.Worker();
        using var result = await worker.QueryAsync(fixture.Connection,
            "SELECT printf('%.*c', 6000000, 'x') AS text_value, zeroblob(5000000) AS blob_value", 10, false, CancellationToken.None);
        var values = Assert.Single(result.ValueRows);
        var text = Assert.IsAssignableFrom<DatabaseValueContent>(values[0].RawValue);
        var binary = Assert.IsAssignableFrom<DatabaseValueContent>(values[1].RawValue);
        Assert.Equal(6_000_000, text.Length);
        Assert.Equal(5_000_000, binary.Length);
        await using (var stream = text.OpenRead())
        {
            var buffer = new byte[8192];
            long count = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer)) != 0)
            {
                Assert.True(buffer.AsSpan(0, read).IndexOfAnyExcept((byte)'x') < 0);
                count += read;
            }
            Assert.Equal(text.Length, count);
        }
        var table = new DatabaseTableDescriptor("sample", DatabaseTableKind.Table);
        var mutation = await worker.ApplyTableChangesAsync(fixture.Connection, table,
            new DatabaseTableChanges([new([new("text_value", DatabaseEditValueState.Value, "second")])], [], []), CancellationToken.None);
        Assert.Equal(1, mutation.Inserted);
        var page = await worker.ReadTableAsync(fixture.Connection, table,
            new DatabaseTableQuery([new("id", DatabaseFilterOperator.Equal, 2L)], [], 0, 10), CancellationToken.None);
        using (page.Result)
        {
            Assert.Equal(1, page.TotalRows);
            Assert.Equal("second", Assert.Single(page.Result.Rows)[1]);
        }
        using var provenance = await worker.QueryAsync(fixture.Connection, "SELECT * FROM sample", 10, true, CancellationToken.None);
        var queryPage = await worker.ReadQueryAsync(fixture.Connection, "SELECT * FROM sample", provenance.Columns,
            DatabaseTableQuery.FirstPage(1), CancellationToken.None);
        using (queryPage.Result)
        {
            Assert.Equal(2, queryPage.TotalRows);
            Assert.True(queryPage.HasMore);
            Assert.Single(queryPage.Result.Rows);
        }
    }

    [Fact]
    public async Task LossDuringResultTransferReportsUnknownWithoutRepeatingCommittedSql()
    {
        using var fixture = new Fixture();
        await fixture.ExecuteAsync("CREATE TABLE mutations(id INTEGER PRIMARY KEY);");
        using var cancellation = new CancellationTokenSource();
        using var worker = fixture.Worker(() => new CancelOnStore(cancellation));
        await Assert.ThrowsAsync<DatabaseMutationOutcomeUnknownException>(() => worker.QueryAsync(fixture.Connection,
            "INSERT INTO mutations VALUES(1) RETURNING zeroblob(1000000)", 10, false, cancellation.Token));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM mutations"));
    }

    [Fact]
    public async Task SampledBudgetKillsOnlyTheOwnedChildAndReturnsItsAdmissionSlot()
    {
        using var fixture = new Fixture();
        var ownedPid = 0;
        using (var worker = fixture.Worker(sample: process =>
        {
            ownedPid = process.Id;
            return DatabaseOperationWorker.MaximumWorkingSetBytes + 1;
        }))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var exception = await Assert.ThrowsAsync<DatabaseMutationOutcomeUnknownException>(() => worker.QueryAsync(fixture.Connection,
                "WITH RECURSIVE busy(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM busy WHERE n<1000000000) SELECT SUM(n) FROM busy",
                1, false, timeout.Token));
            Assert.Contains("2 GiB", exception.Message, StringComparison.Ordinal);
        }
        Assert.NotEqual(0, ownedPid);
        Assert.NotEqual(Environment.ProcessId, ownedPid);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(ownedPid));
        using var next = fixture.Worker();
        using var result = await next.QueryAsync(fixture.Connection, "SELECT 1", 1, false, CancellationToken.None);
        Assert.Single(result.Rows);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionDoesNotExecuteSql()
    {
        using var fixture = new Fixture();
        using var worker = fixture.Worker();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.QueryAsync(fixture.Connection,
            "CREATE TABLE not_executed(id INTEGER)", 1, false, cancellation.Token));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task ProviderFailureRetainsSyntaxFeedbackAndDoesNotClaimBatchRollback()
    {
        using var fixture = new Fixture();
        using var worker = fixture.Worker();
        var failure = await Assert.ThrowsAsync<DatabaseProviderOperationException>(() => worker.QueryAsync(fixture.Connection,
            "CREATE TABLE prior_statement(id INTEGER); SELECT missing_column FROM prior_statement", 10, false, CancellationToken.None));
        Assert.Contains("missing_column", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Database reported", failure.Message, StringComparison.Ordinal);
        Assert.Contains("may have completed", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Connection.ConnectionString, failure.Message, StringComparison.Ordinal);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name='prior_statement'"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task FrameLengthIsRejectedBeforePayloadAllocation(int length)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseOperationProtocol.ReadFrameAsync(stream, 32768, CancellationToken.None));
    }

    private sealed class CancelOnStore(CancellationTokenSource cancellation) : DatabaseValueContentStore
    {
        public override IDisposable Retain() => throw new NotSupportedException();
        public override Task<DatabaseValueContent> StoreAsync(DatabaseValueKind kind,
            Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
        protected override void Dispose(bool disposing) { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ghostshell-operation-test-");
        public string Path => System.IO.Path.Combine(_directory.FullName, "fixture.db");
        public DatabaseWorkerConnection Connection => new("sqlite",
            new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        public DatabaseOperationWorker Worker(Func<DatabaseValueContentStore>? store = null, Func<Process, long>? sample = null)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "global.json"))) { root = root.Parent; }
            var dotnet = System.IO.Path.Combine(root!.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            return new(store ?? (() => new DatabaseResultContentStore(System.IO.Path.Combine(_directory.FullName, "results"))),
                new SelfReentryLaunch(dotnet, [typeof(DatabaseOperationWorker).Assembly.Location], dotnet), sample);
        }
        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection(Connection.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(Connection.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
