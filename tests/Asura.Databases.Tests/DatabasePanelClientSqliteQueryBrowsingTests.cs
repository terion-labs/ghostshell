using Asura.Application;
using Asura.Databases;
using Microsoft.Data.Sqlite;

namespace Asura.Databases.Tests;

/// <summary>
/// Exercises the query-result browsing boundary against a real provider. These
/// reads deliberately start from arbitrary SQL rather than an owned table so
/// outer filters, ordering, and paging use the same path as edited query text.
/// </summary>
public sealed class DatabasePanelClientSqliteQueryBrowsingTests : IDisposable
{
    private const string HostileName = "x%' OR 1=1; DROP TABLE query_rows;--";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"asura-query-browser-tests-{Guid.NewGuid():N}.db");

    public DatabasePanelClientSqliteQueryBrowsingTests()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE query_rows (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                score INTEGER NOT NULL,
                category TEXT NOT NULL
            );
            INSERT INTO query_rows (id, name, score, category) VALUES
                (1, 'alpha', 30, 'group-a'),
                (2, 'beta', 10, 'group-b'),
                (3, 'gamma', 20, 'group-a'),
                (4, 'delta', 40, 'group-a'),
                (5, @hostile, 50, 'group-b');
            """;
        command.Parameters.AddWithValue("hostile", HostileName);
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
    public async Task Arbitrary_query_results_support_outer_filters_both_sort_directions_and_pages()
    {
        await using var client = new DatabasePanelClient();
        const string sourceSql = """
            SELECT id, name, score, category, name || ':' || score AS summary
            FROM query_rows
            WHERE score >= 10
            ORDER BY id DESC;
            """;
        var source = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            sourceSql,
            maxRows: 20,
            CancellationToken.None);

        var ascending = await client.ReadQueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            sourceSql,
            source.Columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition(
                    "category",
                    DatabaseFilterOperator.Equal,
                    "group-a")],
                [new DatabaseSort("score")],
                Offset: 0,
                Limit: 2),
            CancellationToken.None);

        Assert.Equal((0, 2, true), (ascending.Offset, ascending.Limit, ascending.HasMore));
        Assert.Equal(3, ascending.TotalRows);
        Assert.Equal(5, ascending.TableRows);
        Assert.True(ascending.Result.Truncated);
        Assert.Equal(
            [3L, 1L],
            ascending.Result.ValueRows.Select(row => Assert.IsType<long>(row[0].RawValue)));

        var descending = await client.ReadQueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            sourceSql,
            source.Columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition(
                    "category",
                    DatabaseFilterOperator.Equal,
                    "group-a")],
                [new DatabaseSort("score", Descending: true)],
                Offset: 1,
                Limit: 2),
            CancellationToken.None);

        Assert.Equal((1, 2, false), (descending.Offset, descending.Limit, descending.HasMore));
        Assert.Equal(3, descending.TotalRows);
        Assert.False(descending.Result.Truncated);
        Assert.Equal(
            [1L, 3L],
            descending.Result.ValueRows.Select(row => Assert.IsType<long>(row[0].RawValue)));
    }

    [Fact]
    public async Task Outer_query_preserves_source_column_provenance_and_binds_hostile_filters()
    {
        await using var client = new DatabasePanelClient();
        const string sourceSql = """
            SELECT id, name, score, category, score * 2 AS doubled
            FROM query_rows;
            """;
        var source = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            sourceSql,
            maxRows: 20,
            CancellationToken.None);
        Assert.NotNull(source.Columns[0].BaseObject);
        Assert.Null(source.Columns[^1].BaseObject);

        var filtered = await client.ReadQueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            sourceSql,
            source.Columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition(
                    "name",
                    DatabaseFilterOperator.Equal,
                    HostileName)],
                [],
                Offset: 0,
                Limit: 10),
            CancellationToken.None);

        var row = Assert.Single(filtered.Result.ValueRows);
        Assert.Equal(1, filtered.TotalRows);
        Assert.Equal(5L, Assert.IsType<long>(row[0].RawValue));
        Assert.Equal(HostileName, Assert.IsType<string>(row[1].RawValue));
        Assert.Equal(
            source.Columns.Select(column => column.BaseObject),
            filtered.Result.Columns.Select(column => column.BaseObject));
        Assert.Equal(
            source.Columns.Select(column => column.BaseColumnName),
            filtered.Result.Columns.Select(column => column.BaseColumnName), StringComparer.Ordinal);

        var count = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            "SELECT COUNT(*) FROM query_rows;",
            maxRows: 1,
            CancellationToken.None);
        Assert.Equal(5L, Assert.IsType<long>(Assert.Single(count.ValueRows)[0].RawValue));
    }

    [Fact]
    public async Task Failed_outer_query_does_not_poison_the_next_query_result_read()
    {
        await using var client = new DatabasePanelClient();
        const string sourceSql = "SELECT id, name, score, category FROM query_rows;";
        var source = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            sourceSql,
            maxRows: 20,
            CancellationToken.None);

        await Assert.ThrowsAsync<SqliteException>(() => client.ReadQueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            "SELECT id, name, score, category FROM missing_query_rows;",
            source.Columns,
            DatabaseTableQuery.FirstPage(10),
            CancellationToken.None));

        var recovered = await client.ReadQueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            sourceSql,
            source.Columns,
            new DatabaseTableQuery([], [new DatabaseSort("id")], Offset: 0, Limit: 10),
            CancellationToken.None);

        Assert.Equal(5, recovered.Result.ValueRows.Count);
        Assert.Equal(
            [1L, 2L, 3L, 4L, 5L],
            recovered.Result.ValueRows.Select(row => Assert.IsType<long>(row[0].RawValue)));
    }

    [Fact]
    public async Task Trailing_line_comment_cannot_swallow_generated_outer_query_clauses()
    {
        await using var client = new DatabasePanelClient();
        const string sourceSql =
            "SELECT id, name, score, category FROM query_rows -- source comment";
        var source = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            sourceSql,
            maxRows: 20,
            CancellationToken.None);

        var result = await client.ReadQueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            sourceSql,
            source.Columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition("category", DatabaseFilterOperator.Equal, "group-a")],
                [new DatabaseSort("score", Descending: true)],
                Offset: 0,
                Limit: 2),
            CancellationToken.None);

        Assert.Equal(
            [4L, 1L],
            result.Result.ValueRows.Select(row => Assert.IsType<long>(row[0].RawValue)));
        Assert.True(result.HasMore);
    }
}
