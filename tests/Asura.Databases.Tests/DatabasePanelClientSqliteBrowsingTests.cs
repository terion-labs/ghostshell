using Asura.Application;
using Asura.Databases;
using Microsoft.Data.Sqlite;

namespace Asura.Databases.Tests;

/// <summary>
/// Exercises the browser boundary against SQLite itself: catalog metadata,
/// owned table reads, native values, and transactional row mutations all cross
/// the same connection-per-call path used by the desktop panel.
/// </summary>
public sealed class DatabasePanelClientSqliteBrowsingTests : IDisposable
{
    private const string TableName = "odd.table\"name";
    private const string HostileName = "Robert'); DROP TABLE \"odd.table\"\"name\";--";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"asura-browser-tests-{Guid.NewGuid():N}.db");

    public DatabasePanelClientSqliteBrowsingTests()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE "odd.table""name" (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL DEFAULT 'guest',
                    note TEXT,
                    name_upper TEXT GENERATED ALWAYS AS (upper(name)) STORED
                );
                CREATE UNIQUE INDEX ix_odd_name
                    ON "odd.table""name" (name DESC);
                CREATE INDEX ix_odd_note_partial
                    ON "odd.table""name" (note)
                    WHERE note IS NOT NULL;
                CREATE VIEW "odd.view" AS
                    SELECT id, name FROM "odd.table""name";
                CREATE TABLE composite_key_rows (
                    left_id INTEGER NOT NULL,
                    right_id INTEGER NOT NULL,
                    value TEXT,
                    PRIMARY KEY (left_id, right_id)
                );
                CREATE TABLE without_rowid_rows (
                    id INTEGER PRIMARY KEY,
                    value TEXT
                ) WITHOUT ROWID;
                CREATE TABLE spaced_identifier_rows (
                    " key " INTEGER PRIMARY KEY,
                    " value " TEXT
                );
                CREATE TABLE authors (
                    tenant_id INTEGER NOT NULL,
                    id INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, id)
                );
                CREATE TABLE article_links (
                    id INTEGER PRIMARY KEY,
                    tenant_id INTEGER NOT NULL,
                    author_id INTEGER NOT NULL,
                    backup_author_id INTEGER,
                    CONSTRAINT fk_article_author
                        FOREIGN KEY (tenant_id, author_id)
                        REFERENCES authors (tenant_id, id),
                    CONSTRAINT fk_article_backup
                        FOREIGN KEY (tenant_id, backup_author_id)
                        REFERENCES authors (tenant_id, id)
                );
                """;
            schema.ExecuteNonQuery();
        }

        using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO "odd.table""name" (id, name, note) VALUES
                (1, @alpha, 'one'),
                (2, @hostile, 'two'),
                (3, @gamma, NULL),
                (4, @delta, 'four');
            INSERT INTO spaced_identifier_rows (" key ", " value ")
                VALUES (11, 'preserved');
            """;
        seed.Parameters.AddWithValue("alpha", "alpha");
        seed.Parameters.AddWithValue("hostile", HostileName);
        seed.Parameters.AddWithValue("gamma", "gamma");
        seed.Parameters.AddWithValue("delta", "delta");
        seed.ExecuteNonQuery();
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
    public async Task Reads_database_schema_graph_with_composite_and_nullable_foreign_keys()
    {
        await using var client = new DatabasePanelClient();

        var graph = await client.GetDatabaseSchemaGraphAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        Assert.DoesNotContain(graph.Tables, table => string.Equals(table.Object.Name, "odd.view", StringComparison.Ordinal));
        var child = Assert.Single(graph.Tables, table => string.Equals(table.Object.Name, "article_links", StringComparison.Ordinal));
        Assert.Equal(
            ["id", "tenant_id", "author_id", "backup_author_id"],
            child.Columns.Select(column => column.Name), StringComparer.Ordinal);
        var required = Assert.Single(child.ForeignKeys, key => string.Equals(key.Name, "fk_0", StringComparison.Ordinal));
        Assert.Equal("authors", required.ReferencedObject.Name);
        Assert.Equal(
            [("tenant_id", "tenant_id"), ("backup_author_id", "id")],
            required.Columns.Select(column =>
                (column.ColumnName, column.ReferencedColumnName)));
        var second = Assert.Single(child.ForeignKeys, key => string.Equals(key.Name, "fk_1", StringComparison.Ordinal));
        Assert.Equal(
            [("tenant_id", "tenant_id"), ("author_id", "id")],
            second.Columns.Select(column =>
                (column.ColumnName, column.ReferencedColumnName)));

        var mermaid = DatabaseMermaidErDiagram.Create(graph);
        Assert.Contains("fk_0", mermaid, StringComparison.Ordinal);
        Assert.Contains("fk_1", mermaid, StringComparison.Ordinal);
        Assert.Contains("authors", mermaid, StringComparison.Ordinal);
        Assert.Contains("article_links", mermaid, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_language_catalog_with_tables_views_and_exact_column_types()
    {
        await using var client = new DatabasePanelClient();

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        Assert.Equal("sqlite", catalog.DriverId);
        Assert.Null(catalog.DefaultCatalog);
        Assert.Equal("main", catalog.DefaultSchema);
        var table = Assert.Single(catalog.Objects, item => string.Equals(item.Id.Name, TableName, StringComparison.Ordinal));
        Assert.Equal(DatabaseTableKind.Table, table.Kind);
        Assert.Equal(
            ["id", "name", "note", "name_upper"],
            table.Columns.Select(column => column.Name), StringComparer.Ordinal);
        Assert.Equal(DatabaseValueKind.SignedInteger, table.Columns[0].ValueKind);
        Assert.Equal(DatabaseValueKind.Text, table.Columns[1].ValueKind);
        Assert.False(table.Columns[1].IsNullable);

        var view = Assert.Single(catalog.Objects, item => string.Equals(item.Id.Name, "odd.view", StringComparison.Ordinal));
        Assert.Equal(DatabaseTableKind.View, view.Kind);
        Assert.Equal(["id", "name"], view.Columns.Select(column => column.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Language_catalog_caps_large_schemas_without_partial_objects()
    {
        using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = string.Join(
                Environment.NewLine,
                Enumerable.Range(0, 1005).Select(index =>
                    $"CREATE TABLE catalog_limit_{index:D4} (id INTEGER);"));
            await command.ExecuteNonQueryAsync();
        }

        await using var client = new DatabasePanelClient();
        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        Assert.True(catalog.IsPartial);
        Assert.Equal(1000, catalog.Objects.Count);
        Assert.Contains("first 1000", catalog.Limitation, StringComparison.OrdinalIgnoreCase);
        Assert.All(catalog.Objects, item => Assert.NotEmpty(item.Columns));
    }

    [Fact]
    public async Task Lists_qualified_objects_and_reads_columns_keys_defaults_generated_values_and_indexes()
    {
        await using var client = new DatabasePanelClient();

        var objects = await client.ListTablesAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);
        var table = Assert.Single(objects, candidate => string.Equals(candidate.Name, TableName, StringComparison.Ordinal));

        Assert.Equal(DatabaseTableKind.Table, table.Kind);
        Assert.Equal(new DatabaseObjectId(null, null, TableName), table.Id);
        Assert.Equal(TableName, table.DisplayName);
        Assert.Contains(objects, candidate => string.Equals(candidate.Name, "odd.view", StringComparison.Ordinal) && candidate.Kind == DatabaseTableKind.View);

        var details = await client.GetObjectDetailsAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            CancellationToken.None);

        Assert.True(details.CanEdit, details.ReadOnlyReason);
        Assert.Equal(["id", "name", "note", "name_upper"], details.Columns.Select(column => column.Name), StringComparer.Ordinal);
        var id = details.Columns[0];
        Assert.True(id.IsPrimaryKey);
        Assert.Equal(1, id.PrimaryKeyOrdinal);
        Assert.True(id.IsIdentity);
        Assert.True(id.IsReadOnly);
        Assert.Equal(DatabaseValueKind.SignedInteger, id.ValueKind);

        var name = details.Columns[1];
        Assert.False(name.IsNullable);
        Assert.Equal("'guest'", name.DefaultExpression);
        Assert.True(name.CanEdit);

        var generated = details.Columns[3];
        Assert.True(generated.IsGenerated);
        Assert.True(generated.IsReadOnly);
        Assert.False(generated.CanEdit);

        var unique = Assert.Single(details.Indexes, index => string.Equals(index.Name, "ix_odd_name", StringComparison.Ordinal));
        Assert.True(unique.IsUnique);
        var uniqueColumn = Assert.Single(unique.Columns, column => string.Equals(column.Name, "name", StringComparison.Ordinal));
        Assert.True(uniqueColumn.IsDescending);
        Assert.Contains(
            "CREATE UNIQUE INDEX",
            unique.Details!["Definition"],
            StringComparison.OrdinalIgnoreCase);

        var partial = Assert.Single(details.Indexes, index => string.Equals(index.Name, "ix_odd_note_partial", StringComparison.Ordinal));
        Assert.False(partial.IsUnique);
        Assert.Contains(
            "WHERE note IS NOT NULL",
            partial.Details!["Definition"],
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("composite_key_rows", 2)]
    [InlineData("without_rowid_rows", 1)]
    public async Task Integer_primary_keys_that_are_not_rowid_aliases_remain_editable(
        string tableName,
        int primaryKeyColumns)
    {
        await using var client = new DatabasePanelClient();
        var objects = await client.ListTablesAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);
        var table = Assert.Single(objects, candidate => string.Equals(candidate.Name, tableName, StringComparison.Ordinal));

        var details = await client.GetObjectDetailsAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            CancellationToken.None);

        Assert.Equal(primaryKeyColumns, details.PrimaryKey.Count);
        Assert.All(details.PrimaryKey, column =>
        {
            Assert.False(column.IsIdentity);
            Assert.True(column.CanEdit);
        });
    }

    [Fact]
    public async Task Keyless_objects_do_not_advertise_unsafe_offset_pages()
    {
        await using var client = new DatabasePanelClient();
        var objects = await client.ListTablesAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);
        var view = Assert.Single(objects, candidate => string.Equals(candidate.Name, "odd.view", StringComparison.Ordinal));

        var firstPage = await client.ReadTableAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            view,
            new DatabaseTableQuery(
                [],
                [new DatabaseSort("id")],
                Offset: 0,
                Limit: 2),
            CancellationToken.None);

        Assert.Equal(2, firstPage.Result.ValueRows.Count);
        Assert.False(firstPage.HasMore);
    }

    [Fact]
    public async Task Quoted_identifiers_keep_leading_and_trailing_spaces_across_metadata_and_reads()
    {
        await using var client = new DatabasePanelClient();
        var objects = await client.ListTablesAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);
        var table = Assert.Single(objects, candidate => string.Equals(candidate.Name, "spaced_identifier_rows", StringComparison.Ordinal));

        var details = await client.GetObjectDetailsAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            CancellationToken.None);
        var page = await client.ReadTableAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition(
                    " value ",
                    DatabaseFilterOperator.Equal,
                    "preserved")],
                [new DatabaseSort(" key ")],
                Offset: 0,
                Limit: 10),
            CancellationToken.None);

        Assert.Equal([" key ", " value "], details.Columns.Select(column => column.Name), StringComparer.Ordinal);
        Assert.Equal([" key ", " value "], page.Result.Columns.Select(column => column.Name), StringComparer.Ordinal);
        var row = Assert.Single(page.Result.ValueRows);
        Assert.Equal(11L, Assert.IsType<long>(row[0].RawValue));
        Assert.Equal("preserved", Assert.IsType<string>(row[1].RawValue));
    }

    [Fact]
    public async Task Reads_typed_filtered_pages_and_treats_a_hostile_filter_as_data()
    {
        await using var client = new DatabasePanelClient();
        var table = await GetTableAsync(client);
        var filtered = await client.ReadTableAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition("name", DatabaseFilterOperator.Equal, HostileName)],
                [new DatabaseSort("id")],
                Offset: 0,
                Limit: 10),
            CancellationToken.None);

        Assert.False(filtered.HasMore);
        Assert.Equal(1, filtered.TotalRows);
        Assert.Equal(4, filtered.TableRows);
        Assert.Equal(["id", "name", "note", "name_upper"], filtered.Result.Columns.Select(column => column.Name), StringComparer.Ordinal);
        var hostileRow = Assert.Single(filtered.Result.ValueRows);
        Assert.Equal(2L, Assert.IsType<long>(hostileRow[0].RawValue));
        Assert.Equal(DatabaseValueKind.SignedInteger, hostileRow[0].Kind);
        Assert.Equal(HostileName, Assert.IsType<string>(hostileRow[1].RawValue));
        Assert.Equal(DatabaseValueKind.Text, hostileRow[1].Kind);

        var projected = await client.ReadTableAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableQuery(
                [],
                [new DatabaseSort("id")],
                Offset: 0,
                Limit: 2,
                Columns: ["id", "name"]),
            CancellationToken.None);
        Assert.Equal(["id", "name"], projected.Result.Columns.Select(column => column.Name), StringComparer.Ordinal);
        Assert.All(projected.Result.ValueRows, row => Assert.Equal(2, row.Count));

        // The payload contains a complete DROP statement. A second catalog read
        // proves it remained a bound value rather than becoming SQL text.
        Assert.Equal(TableName, (await GetTableAsync(client)).Name);

        var middle = await client.ReadTableAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableQuery([], [new DatabaseSort("id")], Offset: 1, Limit: 2),
            CancellationToken.None);
        Assert.Equal(1, middle.Offset);
        Assert.Equal(2, middle.Limit);
        Assert.True(middle.HasMore);
        Assert.Equal(4, middle.TotalRows);
        Assert.True(middle.Result.Truncated);
        Assert.Equal(
            [2L, 3L],
            middle.Result.ValueRows.Select(row => Assert.IsType<long>(row[0].RawValue)));

        var last = await client.ReadTableAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableQuery([], [new DatabaseSort("id")], Offset: 3, Limit: 2),
            CancellationToken.None);
        Assert.False(last.HasMore);
        Assert.Equal(4, last.TotalRows);
        Assert.Equal(4L, Assert.IsType<long>(Assert.Single(last.Result.ValueRows)[0].RawValue));
    }

    [Fact]
    public async Task Applies_insert_update_and_delete_in_one_transaction()
    {
        await using var client = new DatabasePanelClient();
        var table = await GetTableAsync(client);
        var changes = new DatabaseTableChanges(
            Inserts:
            [
                new DatabaseInsertedRow(
                [
                    Value("name", "inserted"),
                    Null("note"),
                ]),
            ],
            Updates:
            [
                new DatabaseUpdatedRow(
                    Keys: [Value("id", 1L)],
                    Changes: [Value("note", "updated")],
                    OriginalValues: [Value("name", "alpha"), Value("note", "one")]),
            ],
            Deletes:
            [
                new DatabaseDeletedRow(
                    Keys: [Value("id", 4L)],
                    OriginalValues: [Value("name", "delta"), Value("note", "four")]),
            ]);

        var result = await client.ApplyTableChangesAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            changes,
            CancellationToken.None);

        Assert.False(result.HasConflict, result.Message);
        Assert.Equal((1, 1, 1, 3), (result.Inserted, result.Updated, result.Deleted, result.TotalAffected));
        var rows = await ReadAllAsync(client, table);
        Assert.Equal([1L, 2L, 3L, 5L], rows.Select(row => Assert.IsType<long>(row[0].RawValue)));
        Assert.Equal("updated", rows.Single(row => Equals(row[0].RawValue, 1L))[2].RawValue);
        var inserted = rows.Single(row => Equals(row[1].RawValue, "inserted"));
        Assert.Null(inserted[2].RawValue);
        Assert.Equal("INSERTED", inserted[3].RawValue);
    }

    [Fact]
    public async Task Optimistic_conflict_rolls_back_earlier_changes_in_the_batch()
    {
        await using var client = new DatabasePanelClient();
        var table = await GetTableAsync(client);
        var changes = new DatabaseTableChanges(
            Inserts:
            [
                new DatabaseInsertedRow([Value("name", "must-roll-back")]),
            ],
            Updates:
            [
                new DatabaseUpdatedRow(
                    Keys: [Value("id", 1L)],
                    Changes: [Value("note", "must-not-commit")],
                    OriginalValues: [Value("name", "stale-original")]),
            ],
            Deletes: []);

        var result = await client.ApplyTableChangesAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            changes,
            CancellationToken.None);

        Assert.True(result.HasConflict);
        Assert.Equal(0, result.TotalAffected);
        var rows = await ReadAllAsync(client, table);
        Assert.DoesNotContain(rows, row => Equals(row[1].RawValue, "must-roll-back"));
        Assert.Equal("one", rows.Single(row => Equals(row[0].RawValue, 1L))[2].RawValue);
    }

    private async Task<DatabaseTableDescriptor> GetTableAsync(DatabasePanelClient client)
    {
        var objects = await client.ListTablesAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);
        return Assert.Single(objects, candidate => string.Equals(candidate.Name, TableName, StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<IReadOnlyList<DatabaseValue>>> ReadAllAsync(
        DatabasePanelClient client,
        DatabaseTableDescriptor table)
    {
        var page = await client.ReadTableAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableQuery([], [new DatabaseSort("id")], Offset: 0, Limit: 20),
            CancellationToken.None);
        return page.Result.ValueRows;
    }

    private static DatabaseColumnEdit Value(string column, object value) =>
        new(column, DatabaseEditValueState.Value, value);

    private static DatabaseColumnEdit Null(string column) =>
        new(column, DatabaseEditValueState.Null);
}
