using Asura.Application;
using Asura.Databases;
using Microsoft.Data.Sqlite;

namespace Asura.Databases.Tests;

public sealed class DatabasePanelClientSqliteMetadataEdgeTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"asura-metadata-edge-tests-{Guid.NewGuid():N}.db");

    public DatabasePanelClientSqliteMetadataEdgeTests()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE rowid_key (
                id INTEGER PRIMARY KEY,
                value TEXT
            );
            CREATE TABLE composite_key (
                tenant_id INTEGER,
                item_id INTEGER,
                value TEXT,
                PRIMARY KEY (tenant_id, item_id)
            );
            CREATE TABLE without_rowid_key (
                id INTEGER PRIMARY KEY,
                value TEXT
            ) WITHOUT ROWID;
            CREATE TABLE keyless_items (
                id INTEGER,
                value TEXT
            );
            INSERT INTO keyless_items VALUES
                (1, 'one'), (2, 'two'), (3, 'three'), (4, 'four');
            CREATE VIEW keyless_view AS SELECT id, value FROM keyless_items;
            CREATE TABLE opaque_key (
                id MYSTERY PRIMARY KEY,
                value TEXT
            );
            CREATE TABLE binary_key (
                id BLOB PRIMARY KEY,
                value TEXT
            );
            """;
        command.ExecuteNonQuery();
    }

    private string ConnectionString => $"Data Source={_databasePath};Pooling=False";

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task Integer_identity_requires_a_single_rowid_primary_key()
    {
        await using var client = new DatabasePanelClient();

        var rowid = await DetailsAsync(client, "rowid_key");
        Assert.True(Assert.Single(rowid.PrimaryKey).IsIdentity);

        var composite = await DetailsAsync(client, "composite_key");
        Assert.Equal(2, composite.PrimaryKey.Count);
        Assert.All(composite.PrimaryKey, column => Assert.False(column.IsIdentity));

        var withoutRowId = await DetailsAsync(client, "without_rowid_key");
        Assert.False(Assert.Single(withoutRowId.PrimaryKey).IsIdentity);
    }

    [Fact]
    public async Task Unsafe_opaque_keys_disable_editing_but_detached_binary_keys_do_not()
    {
        await using var client = new DatabasePanelClient();

        var opaque = await DetailsAsync(client, "opaque_key");
        Assert.Equal(DatabaseValueKind.Other, Assert.Single(opaque.PrimaryKey).ValueKind);
        Assert.False(opaque.CanEdit);
        Assert.Contains("cannot be parameterized safely", opaque.ReadOnlyReason, StringComparison.Ordinal);

        var binary = await DetailsAsync(client, "binary_key");
        Assert.Equal(DatabaseValueKind.Binary, Assert.Single(binary.PrimaryKey).ValueKind);
        Assert.True(binary.CanEdit, binary.ReadOnlyReason);
    }

    [Theory]
    [InlineData("keyless_items")]
    [InlineData("keyless_view")]
    public async Task Keyless_objects_return_a_truncated_first_page_without_exposing_next(
        string objectName)
    {
        await using var client = new DatabasePanelClient();
        var descriptor = await FindAsync(client, objectName);

        var first = await client.ReadTableAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            descriptor,
            new DatabaseTableQuery([], [new DatabaseSort("id")], Offset: 0, Limit: 2),
            CancellationToken.None);

        Assert.Equal(2, first.Result.ValueRows.Count);
        Assert.True(first.Result.Truncated);
        Assert.False(first.HasMore);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadTableAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            descriptor,
            new DatabaseTableQuery([], [new DatabaseSort("id")], Offset: 2, Limit: 2),
            CancellationToken.None));
        Assert.Contains("only its first page", exception.Message, StringComparison.Ordinal);
    }

    private async Task<DatabaseObjectDetails> DetailsAsync(
        DatabasePanelClient client,
        string objectName)
    {
        var descriptor = await FindAsync(client, objectName);
        return await client.GetObjectDetailsAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            descriptor,
            CancellationToken.None);
    }

    private async Task<DatabaseTableDescriptor> FindAsync(
        DatabasePanelClient client,
        string objectName)
    {
        var objects = await client.ListTablesAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);
        return Assert.Single(objects, candidate => string.Equals(candidate.Name, objectName, StringComparison.Ordinal));
    }
}
