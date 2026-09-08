using System.Data.Common;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Databases;

namespace GhostShell.Databases.Tests;

public sealed class DatabaseMetadataExecutorTests
{
    private const string ConnectionString = "private-target";
    private const string SourceSql = "SELECT value FROM entries";
    private static readonly DatabaseTableDescriptor Table = new("entries", DatabaseTableKind.Table);
    private static readonly IReadOnlyList<DatabaseColumnDescriptor> SourceColumns = [new("value", "text")];
    private static readonly IReadOnlyList<DatabaseFilterCondition> Filters = [];

    [Theory]
    [InlineData("tables")]
    [InlineData("schema")]
    [InlineData("catalog")]
    [InlineData("databases")]
    [InlineData("session")]
    [InlineData("details")]
    [InlineData("count")]
    public async Task Selected_service_executor_receives_original_connection_and_never_captures_host_route(string operation)
    {
        var driver = new RejectingHostDriver();
        var host = new RecordingExecutor();
        var selected = new RecordingExecutor();
        var profile = new ConnectionProfile(new ConnectionId("fixture-ssh"), ConnectionProfile.CurrentSchemaVersion,
            "Fixture SSH", new ConnectionEndpoint.Ssh("ssh.internal", username: "fixture"),
            new ConnectionAuthentication.None(), ConnectionStartup.Default, ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.AcceptNew);
        using var cancellation = new CancellationTokenSource();
        var selections = 0;
        await using var client = new DatabasePanelClient([driver], operationExecutor: host,
            operationExecutorSelector: (descriptor, route, token) =>
            {
                Assert.Same(driver.Descriptor, descriptor);
                Assert.Same(profile, route);
                Assert.Equal(cancellation.Token, token);
                selections++;
                return ValueTask.FromResult<IDatabaseOperationExecutor>(selected);
            });

        await InvokeAsync(client, operation, cancellation.Token, profile);

        Assert.Equal(1, selections);
        Assert.Equal(new DatabaseWorkerConnection("sqlite", "normalized:" + ConnectionString), selected.Connection);
        Assert.Equal(0, host.CallCount);
        Assert.Equal(0, driver.CreateCount);
    }

    [Fact]
    public async Task Failed_service_selection_does_not_fall_back_to_host_executor()
    {
        var driver = new RejectingHostDriver();
        var host = new RecordingExecutor();
        await using var client = new DatabasePanelClient([driver], operationExecutor: host,
            operationExecutorSelector: (_, _, _) => ValueTask.FromException<IDatabaseOperationExecutor>(
                new IOException("The service route is unavailable.")));

        await Assert.ThrowsAsync<IOException>(() => client.ListTablesAsync("sqlite", ConnectionString, null, CancellationToken.None));

        Assert.Equal(0, host.CallCount);
        Assert.Equal(0, driver.CreateCount);
    }

    [Theory]
    [InlineData("tables")]
    [InlineData("schema")]
    [InlineData("catalog")]
    [InlineData("databases")]
    [InlineData("session")]
    [InlineData("details")]
    [InlineData("count")]
    public async Task Selected_guest_metadata_receives_cancellation_without_host_fallback(string operation)
    {
        foreach (var explicitLocal in new[] { false, true })
        {
            var driver = new RejectingHostDriver();
            var host = new RecordingExecutor();
            var guest = new RecordingExecutor();
            using var lifetime = new CancellationTokenSource();
            await using var client = new DatabasePanelClient([driver], operationExecutor: host,
                operationExecutorSelector: (_, _, _) => ValueTask.FromResult<IDatabaseOperationExecutor>(guest));
            var profile = explicitLocal ? BuiltInConnections.Local : null;

            await InvokeAsync(client, operation, lifetime.Token, profile);

            Assert.Equal(new DatabaseWorkerConnection("sqlite", "normalized:" + ConnectionString), guest.Connection);
            Assert.Equal(0, host.CallCount);
            Assert.Equal(0, driver.CreateCount);
            guest.BeforeResult = async token =>
            {
                await lifetime.CancelAsync();
                token.ThrowIfCancellationRequested();
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(client, operation, lifetime.Token, profile));
            Assert.True(guest.Token.IsCancellationRequested);
            Assert.Equal(0, host.CallCount);
        }
    }

    [Theory]
    [InlineData("tables")]
    [InlineData("schema")]
    [InlineData("catalog")]
    [InlineData("databases")]
    [InlineData("session")]
    [InlineData("details")]
    [InlineData("count")]
    public async Task Explicit_SSH_without_a_backend_selector_fails_before_host_provider_or_executor(string operation)
    {
        var driver = new RejectingHostDriver();
        var host = new RecordingExecutor();
        var profile = new ConnectionProfile(new ConnectionId("fixture-ssh"), ConnectionProfile.CurrentSchemaVersion,
            "Fixture SSH", new ConnectionEndpoint.Ssh("ssh.internal", username: "fixture"),
            new ConnectionAuthentication.None(), ConnectionStartup.Default, ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.AcceptNew);
        await using var client = new DatabasePanelClient([driver], operationExecutor: host);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(client, operation, CancellationToken.None, profile));
        Assert.Equal(0, host.CallCount);
        Assert.Equal(0, driver.CreateCount);
    }

    [Theory]
    [InlineData("tables")]
    [InlineData("schema")]
    [InlineData("catalog")]
    [InlineData("databases")]
    [InlineData("session")]
    [InlineData("details")]
    [InlineData("count")]
    public async Task Metadata_operations_use_executor_without_creating_a_host_provider(string operation)
    {
        var driver = new RejectingHostDriver();
        var executor = new RecordingExecutor();
        await using var client = new DatabasePanelClient([driver], operationExecutor: executor);
        using var cancellation = new CancellationTokenSource();

        var result = await InvokeAsync(client, operation, cancellation.Token);

        Assert.Equal(executor.Result, result);
        Assert.Equal(operation, executor.Operation);
        Assert.Equal(new DatabaseWorkerConnection("sqlite", "normalized:" + ConnectionString), executor.Connection);
        Assert.Equal(cancellation.Token, executor.Token);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(0, driver.CreateCount);

        executor.Failure = new IOException("Worker unavailable.");
        var failure = await Assert.ThrowsAsync<IOException>(() => InvokeAsync(client, operation, cancellation.Token));
        Assert.Same(executor.Failure, failure);
        Assert.Equal(2, executor.CallCount);
        Assert.Equal(0, driver.CreateCount);
    }

    [Theory]
    [InlineData("tables")]
    [InlineData("schema")]
    [InlineData("catalog")]
    [InlineData("databases")]
    [InlineData("session")]
    [InlineData("details")]
    [InlineData("count")]
    public async Task Metadata_executor_receives_cancellation_without_host_fallback(string operation)
    {
        var driver = new RejectingHostDriver();
        var executor = new RecordingExecutor();
        await using var client = new DatabasePanelClient([driver], operationExecutor: executor);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(client, operation, cancellation.Token));

        Assert.Equal(cancellation.Token, executor.Token);
        Assert.Equal(0, driver.CreateCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagram_renderer_receives_only_executor_metadata(bool sourceExport)
    {
        var driver = new RejectingHostDriver();
        var executor = new RecordingExecutor();
        var renderer = new RecordingDiagramWorkers();
        await using var client = new DatabasePanelClient([driver], diagramWorkers: renderer, operationExecutor: executor);
        using var cancellation = new CancellationTokenSource();

        if (sourceExport)
        {
            using var destination = new MemoryStream();
            await client.ExportDatabaseSchemaSourceAsync("sqlite", ConnectionString, null, destination, cancellation.Token);
            Assert.True(renderer.Session.Exported);
        }
        else
        {
            await using var session = await client.OpenDatabaseDiagramAsync("sqlite", ConnectionString, null, cancellation.Token);
            Assert.Same(renderer.Session, session);
        }

        Assert.Equal("schema", executor.Operation);
        Assert.Same(executor.Result, renderer.Graph);
        Assert.Equal(cancellation.Token, renderer.Token);
        Assert.Equal(sourceExport ? DatabaseDiagramPurpose.SourceExport : DatabaseDiagramPurpose.Display, renderer.Purpose);
        Assert.True(renderer.Session.Disposed);
        Assert.Equal(0, driver.CreateCount);
    }

    private static async Task<object> InvokeAsync(DatabasePanelClient client, string operation, CancellationToken token,
        ConnectionProfile? tunnel = null) =>
        operation switch
        {
            "tables" => await client.ListTablesAsync("sqlite", ConnectionString, tunnel, token),
            "schema" => await client.GetDatabaseSchemaGraphAsync("sqlite", ConnectionString, tunnel, token),
            "catalog" => await client.GetSqlCatalogAsync("sqlite", ConnectionString, tunnel, token),
            "databases" => await client.ListDatabasesAsync("sqlite", ConnectionString, tunnel, token),
            "session" => await client.DescribeSessionAsync("sqlite", ConnectionString, tunnel, token),
            "details" => await client.GetObjectDetailsAsync("sqlite", ConnectionString, tunnel, Table, token),
            "count" => await client.CountQueryRowsAsync("sqlite", ConnectionString, tunnel, SourceSql, SourceColumns, Filters, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private sealed class RecordingExecutor : IDatabaseOperationExecutor
    {
        public string? Operation { get; private set; }
        public DatabaseWorkerConnection? Connection { get; private set; }
        public CancellationToken Token { get; private set; }
        public object? Result { get; private set; }
        public int CallCount { get; private set; }
        public IOException? Failure { get; set; }
        public Func<CancellationToken, Task>? BeforeResult { get; set; }

        public Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken) =>
            Record<IReadOnlyList<DatabaseTableDescriptor>>("tables", connection, cancellationToken, [Table]);

        public Task<DatabaseSchemaGraph> GetDatabaseSchemaGraphAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken) =>
            Record("schema", connection, cancellationToken, new DatabaseSchemaGraph([]));

        public Task<SqlCatalogSnapshot> GetSqlCatalogAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken) =>
            Record("catalog", connection, cancellationToken, new SqlCatalogSnapshot("sqlite", null, "main", []));

        public Task<IReadOnlyList<string>> ListDatabasesAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken) =>
            Record<IReadOnlyList<string>>("databases", connection, cancellationToken, ["worker-database"]);

        public Task<DatabaseSessionInfo> DescribeSessionAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken) =>
            Record("session", connection, cancellationToken, new DatabaseSessionInfo("worker-version"));

        public Task<DatabaseObjectDetails> GetObjectDetailsAsync(DatabaseWorkerConnection connection,
            DatabaseTableDescriptor databaseObject, CancellationToken cancellationToken)
        {
            Assert.Same(Table, databaseObject);
            return Record("details", connection, cancellationToken, new DatabaseObjectDetails(Table, [], [], false));
        }

        public Task<long> CountQueryRowsAsync(DatabaseWorkerConnection connection, string sourceSql,
            IReadOnlyList<DatabaseColumnDescriptor> sourceColumns, IReadOnlyList<DatabaseFilterCondition> filters,
            CancellationToken cancellationToken)
        {
            Assert.Equal(SourceSql, sourceSql);
            Assert.Same(SourceColumns, sourceColumns);
            Assert.Same(Filters, filters);
            return Record("count", connection, cancellationToken, 42L);
        }

        private async Task<T> Record<T>(string operation, DatabaseWorkerConnection connection, CancellationToken token, T result)
        {
            Operation = operation;
            Connection = connection;
            Token = token;
            Result = result;
            CallCount++;
            token.ThrowIfCancellationRequested();
            if (BeforeResult is { } beforeResult) { await beforeResult(token); }
            if (Failure is { } failure) { throw failure; }
            return result;
        }

        public Task<DatabaseQueryPage> QueryAsync(DatabaseWorkerConnection connection, string sql,
            int maximumRows, bool requestProvenance, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DatabaseTablePage> ReadQueryAsync(DatabaseWorkerConnection connection, string sourceSql,
            IReadOnlyList<DatabaseColumnDescriptor> sourceColumns, DatabaseTableQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DatabaseTablePage> ReadTableAsync(DatabaseWorkerConnection connection, DatabaseTableDescriptor table,
            DatabaseTableQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DatabaseMutationResult> ApplyTableChangesAsync(DatabaseWorkerConnection connection,
            DatabaseTableDescriptor table, DatabaseTableChanges changes,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RejectingHostDriver : IDatabaseDriver
    {
        public int CreateCount { get; private set; }
        public DatabaseDriverDescriptor Descriptor { get; } = new("sqlite", "Fixture", "", IsFileBased: true);
        public string ListTablesSql => throw new InvalidOperationException("Host catalog SQL must not be read.");
        public string? ListDatabasesSql => throw new InvalidOperationException("Host catalog SQL must not be read.");

        public DbConnection CreateConnection(string connectionString)
        {
            CreateCount++;
            throw new InvalidOperationException("Host provider must not be created.");
        }

        public string NormalizeConnectionString(string connectionString) => "normalized:" + connectionString;
        public string QuoteIdentifier(string identifier) => throw new NotSupportedException();
        public string BuildPreviewQuery(string tableName, int limit) => throw new NotSupportedException();
        public DatabaseConnectionDetails ParseDetails(string connectionString) => throw new NotSupportedException();
        public string BuildConnectionString(DatabaseConnectionDetails details) => throw new NotSupportedException();
    }


    private sealed class RecordingDiagramWorkers : IDatabaseDiagramWorkerFactory
    {
        public DatabaseSchemaGraph? Graph { get; private set; }
        public CancellationToken Token { get; private set; }
        public DatabaseDiagramPurpose Purpose { get; private set; }
        public RecordingDiagramSession Session { get; } = new();

        public Task<IDatabaseDiagramSession> OpenAsync(DatabaseSchemaGraph graph, CancellationToken cancellationToken,
            DatabaseDiagramPurpose purpose = DatabaseDiagramPurpose.Display)
        {
            Graph = graph;
            Token = cancellationToken;
            Purpose = purpose;
            return Task.FromResult<IDatabaseDiagramSession>(Session);
        }

        public Task<IDatabaseDiagramSession> OpenAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken,
            DatabaseDiagramPurpose purpose = DatabaseDiagramPurpose.Display) =>
            throw new InvalidOperationException("Renderer must not receive a database connection.");
    }

    private sealed class RecordingDiagramSession : IDatabaseDiagramSession
    {
        public bool Disposed { get; private set; }
        public bool Exported { get; private set; }

        public Task<byte[]> RenderViewportAsync(DatabaseDiagramViewport viewport, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ExportAsync(Stream destination, DatabaseDiagramExport format, CancellationToken cancellationToken)
        {
            Assert.Equal(DatabaseDiagramExport.MermaidMarkdown, format);
            Exported = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
