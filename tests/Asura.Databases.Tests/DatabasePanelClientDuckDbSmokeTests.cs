using Asura.Application;
using Asura.Databases;
using DuckDB.NET.Data;

namespace Asura.Databases.Tests;

/// <summary>
/// Keeps the provider-neutral browser honest against DuckDB itself. The file
/// database is intentionally reopened by every client operation, matching the
/// desktop's connection lifecycle rather than relying on an in-memory handle.
/// </summary>
public sealed class DatabasePanelClientDuckDbSmokeTests : IDisposable
{
    private const string HostileService = "billing' OR TRUE; DROP TABLE ops.deployments;--";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"asura-duckdb-tests-{Guid.NewGuid():N}");
    private readonly string _databasePath;

    public DatabasePanelClientDuckDbSmokeTests()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        _databasePath = Path.Combine(_temporaryDirectory, "analytics.duckdb");

        using var connection = new DuckDBConnection(ConnectionString);
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE SCHEMA ops;
                CREATE TABLE ops.deployments (
                    id BIGINT PRIMARY KEY,
                    service VARCHAR NOT NULL DEFAULT 'unknown',
                    status VARCHAR,
                    amount DECIMAL(12, 2),
                    deployed_at TIMESTAMP WITH TIME ZONE
                );
                CREATE INDEX ix_deployments_service
                    ON ops.deployments (service);
                """;
            schema.ExecuteNonQuery();
        }

        using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO ops.deployments
                (id, service, status, amount, deployed_at)
            VALUES
                (1, 'api', 'ready', 10.50, TIMESTAMPTZ '2026-08-08 10:00:00+00'),
                (2, $hostile, 'pending', 20.75, TIMESTAMPTZ '2026-08-08 11:00:00+00'),
                (3, 'worker', 'ready', 30.00, TIMESTAMPTZ '2026-08-08 12:00:00+00');
            """;
        var hostile = seed.CreateParameter();
        hostile.ParameterName = "hostile";
        hostile.Value = HostileService;
        seed.Parameters.Add(hostile);
        seed.ExecuteNonQuery();
    }

    private string ConnectionString => $"Data Source={_databasePath}";

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Browses_structure_indexes_typed_pages_and_updates_a_keyed_row()
    {
        await using var client = new DatabasePanelClient();

        var objects = await client.ListTablesAsync(
            "duckdb",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);
        var table = Assert.Single(objects, candidate => string.Equals(candidate.Name, "deployments", StringComparison.Ordinal) && string.Equals(candidate.Schema, "ops", StringComparison.Ordinal));

        Assert.Equal(DatabaseTableKind.Table, table.Kind);
        Assert.False(string.IsNullOrWhiteSpace(table.Catalog));
        Assert.Equal("ops.deployments", table.DisplayName);

        var details = await client.GetObjectDetailsAsync(
            "duckdb",
            ConnectionString,
            tunnel: null,
            table,
            CancellationToken.None);

        Assert.Equal(
            ["id", "service", "status", "amount", "deployed_at"],
            details.Columns.Select(column => column.Name), StringComparer.Ordinal);
        var id = details.Columns[0];
        Assert.True(id.IsPrimaryKey);
        Assert.Equal(1, id.PrimaryKeyOrdinal);
        Assert.Equal(DatabaseValueKind.SignedInteger, id.ValueKind);
        var service = details.Columns[1];
        Assert.False(service.IsNullable);
        Assert.Equal("'unknown'", service.DefaultExpression);
        Assert.Equal(DatabaseValueKind.Text, service.ValueKind);
        var index = Assert.Single(details.Indexes, candidate => string.Equals(candidate.Name, "ix_deployments_service", StringComparison.Ordinal));
        Assert.True(index.IsValid);
        Assert.Contains(
            "CREATE INDEX",
            index.Details!["Definition"],
            StringComparison.OrdinalIgnoreCase);

        var projection = await client.QueryWithProvenanceAsync(
            "duckdb",
            ConnectionString,
            tunnel: null,
            client.BuildTablePreviewQuery("duckdb", table.Id, limit: 10),
            maxRows: 10,
            CancellationToken.None);
        Assert.All(projection.Columns, column =>
        {
            Assert.Equal(table.Id, column.BaseObject);
            Assert.Equal(column.Name, column.BaseColumnName);
        });

        var quotedTable = DatabaseSqlDialect.For("duckdb").QuoteObject(table.Id);
        var transformed = await client.QueryWithProvenanceAsync(
            "duckdb",
            ConnectionString,
            tunnel: null,
            $"SELECT *, amount + 1 AS computed FROM {quotedTable};",
            maxRows: 10,
            CancellationToken.None);
        Assert.All(transformed.Columns, column => Assert.Null(column.BaseObject));

        var filtered = await client.ReadTableAsync(
            "duckdb",
            ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition("service", DatabaseFilterOperator.Equal, HostileService)],
                [new DatabaseSort("id")],
                Offset: 0,
                Limit: 10),
            CancellationToken.None);

        Assert.False(filtered.HasMore);
        var hostileRow = Assert.Single(filtered.Result.ValueRows);
        Assert.Equal(2L, Assert.IsType<long>(hostileRow[0].RawValue));
        Assert.Equal(HostileService, Assert.IsType<string>(hostileRow[1].RawValue));
        Assert.Equal(20.75m, Assert.IsType<decimal>(hostileRow[3].RawValue));
        Assert.Equal(DatabaseValueKind.TimestampWithZone, hostileRow[4].Kind);

        // A second catalog lookup proves the hostile filter stayed bound data.
        Assert.Contains(
            await client.ListTablesAsync(
                "duckdb",
                ConnectionString,
                tunnel: null,
                CancellationToken.None),
            candidate => string.Equals(candidate.Name, "deployments", StringComparison.Ordinal) && string.Equals(candidate.Schema, "ops", StringComparison.Ordinal));

        var firstPage = await client.ReadTableAsync(
            "duckdb",
            ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableQuery([], [new DatabaseSort("id")], Offset: 0, Limit: 2),
            CancellationToken.None);
        Assert.True(firstPage.HasMore);
        Assert.True(firstPage.Result.Truncated);
        Assert.Equal(
            [1L, 2L],
            firstPage.Result.ValueRows.Select(row => Assert.IsType<long>(row[0].RawValue)));

        if (!details.CanEdit)
        {
            return;
        }

        var mutation = await client.ApplyTableChangesAsync(
            "duckdb",
            ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableChanges(
                Inserts: [],
                Updates:
                [
                    new DatabaseUpdatedRow(
                        Keys: [Value("id", 1L)],
                        Changes: [Value("status", "deployed")],
                        OriginalValues: [Value("service", "api"), Value("status", "ready")]),
                ],
                Deletes: []),
            CancellationToken.None);

        Assert.False(mutation.HasConflict, mutation.Message);
        Assert.Equal(1, mutation.Updated);
        var updated = await client.ReadTableAsync(
            "duckdb",
            ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition("id", DatabaseFilterOperator.Equal, 1L)],
                [],
                Offset: 0,
                Limit: 2),
            CancellationToken.None);
        Assert.Equal("deployed", Assert.Single(updated.Result.ValueRows)[2].RawValue);
    }

    private static DatabaseColumnEdit Value(string column, object value) =>
        new(column, DatabaseEditValueState.Value, value);
}
