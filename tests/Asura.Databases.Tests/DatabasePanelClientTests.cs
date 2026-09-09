using Asura.Application;
using Asura.Databases;

namespace Asura.Databases.Tests;

public sealed class DatabasePanelClientTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"asura-databases-tests-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_databasePath};Pooling=False";

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public void Built_in_drivers_have_unique_durable_ids()
    {
        var ids = BuiltInDatabaseDrivers.All
            .Select(driver => driver.Descriptor.Id)
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            [
                "sqlite", "postgres", "mysql", "mariadb", "sqlserver", "cockroach",
                "redshift", "duckdb", "oracle", "firebird", "clickhouse",
            ],
            ids);
        // The durable target format splits on the first colon, so ids must
        // never contain one — and pickers rely on non-blank display names.
        Assert.All(BuiltInDatabaseDrivers.All, driver =>
        {
            Assert.DoesNotContain(':', driver.Descriptor.Id);
            Assert.False(string.IsNullOrWhiteSpace(driver.Descriptor.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(driver.Descriptor.ConnectionStringHint));
        });
    }

    [Fact]
    public void Built_in_drivers_probe_live_sql_catalog_defaults_when_available()
    {
        var probes = BuiltInDatabaseDrivers.All.ToDictionary(
            driver => driver.Descriptor.Id,
            driver => driver.SqlCatalogDefaultsSql,
            StringComparer.Ordinal);

        Assert.Null(probes["firebird"]);
        Assert.All(
            probes.Where(item => !string.Equals(item.Key, "firebird", StringComparison.Ordinal)),
            item => Assert.False(
                string.IsNullOrWhiteSpace(item.Value),
                $"{item.Key} must report its live catalog/schema defaults."));
        Assert.Contains("current_schema", probes["postgres"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SCHEMA_NAME", probes["sqlserver"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CURRENT_SCHEMA", probes["oracle"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Built_in_routine_catalogs_are_provider_owned_and_fail_closed()
    {
        var queries = BuiltInDatabaseDrivers.All.ToDictionary(
            driver => driver.Descriptor.Id,
            driver => driver.ListRoutinesSql,
            StringComparer.Ordinal);

        Assert.Null(queries["redshift"]);
        Assert.All(
            queries.Where(item => !string.Equals(item.Key, "redshift", StringComparison.Ordinal)),
            item => Assert.False(
                string.IsNullOrWhiteSpace(item.Value),
                $"{item.Key} must own its routine-catalog query."));

        Assert.Contains("pg_catalog", queries["postgres"], StringComparison.Ordinal);
        Assert.Contains("current_schema()", queries["postgres"], StringComparison.Ordinal);
        Assert.Equal(queries["postgres"], queries["cockroach"]);
        Assert.Contains("narg < 0", queries["sqlite"], StringComparison.Ordinal);
        Assert.Contains("ORDER BY internal", queries["duckdb"], StringComparison.Ordinal);
        Assert.Contains("LISTAGG(signature_argument.data_type", queries["oracle"], StringComparison.Ordinal);
        Assert.DoesNotContain("rdb$functions function", queries["firebird"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rdb$functions routine", queries["firebird"], StringComparison.OrdinalIgnoreCase);

        var coverage = BuiltInDatabaseDrivers.All.ToDictionary(
            driver => driver.Descriptor.Id,
            driver => (driver.RoutineCatalogCoverage, driver.IntrinsicCatalogCoverage),
            StringComparer.Ordinal);
        Assert.Equal(
            (SqlCatalogCoverage.Complete, SqlCatalogCoverage.Complete),
            coverage["postgres"]);
        Assert.Equal(coverage["postgres"], coverage["sqlite"]);
        Assert.Equal(coverage["postgres"], coverage["cockroach"]);
        Assert.Equal(coverage["postgres"], coverage["clickhouse"]);
        Assert.Equal(
            (SqlCatalogCoverage.UserDefinedOnly, SqlCatalogCoverage.None),
            coverage["sqlserver"]);
        Assert.Equal(
            (SqlCatalogCoverage.None, SqlCatalogCoverage.None),
            coverage["redshift"]);
        Assert.Equal(
            (SqlCatalogCoverage.Complete, SqlCatalogCoverage.Partial),
            coverage["duckdb"]);
        Assert.Equal(
            (SqlCatalogCoverage.UserDefinedOnly, SqlCatalogCoverage.Partial),
            coverage["oracle"]);

        var intrinsicQueries = BuiltInDatabaseDrivers.All.ToDictionary(
            driver => driver.Descriptor.Id,
            driver => driver.ListIntrinsicSymbolsSql,
            StringComparer.Ordinal);
        Assert.Contains("pg_get_keywords", intrinsicQueries["postgres"], StringComparison.Ordinal);
        Assert.Contains("builtin = 1", intrinsicQueries["sqlite"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("HELP '%'", intrinsicQueries["mysql"]);
        Assert.Equal(intrinsicQueries["mysql"], intrinsicQueries["mariadb"]);
        Assert.Contains("v$sqlfn_metadata", intrinsicQueries["oracle"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("v$reserved_words", intrinsicQueries["oracle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rdb$keywords", intrinsicQueries["firebird"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("system.functions", intrinsicQueries["clickhouse"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lists_tables_and_views_and_pages_query_results()
    {
        var client = new DatabasePanelClient();
        _ = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            """
            CREATE TABLE people(id INTEGER PRIMARY KEY, name TEXT, joined TEXT);
            CREATE VIEW named_people AS SELECT name FROM people;
            """,
            maxRows: 10,
            CancellationToken.None);
        var inserted = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            "INSERT INTO people(name, joined) VALUES ('Ada', '1843'), ('Grace', '1952'), (NULL, NULL);",
            maxRows: 10,
            CancellationToken.None);

        Assert.Empty(inserted.Columns);
        Assert.Equal(3, inserted.RowsAffected);

        var tables = await client.ListTablesAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);
        Assert.Equal(
            [("named_people", DatabaseTableKind.View), ("people", DatabaseTableKind.Table)],
            tables.Select(table => (table.Name, table.Kind)));

        var page = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            client.BuildTablePreviewQuery("sqlite", "people", limit: 10),
            maxRows: 10,
            CancellationToken.None);
        Assert.Equal(["id", "name", "joined"], page.Columns.Select(column => column.Name), StringComparer.Ordinal);
        Assert.Equal(3, page.Rows.Count);
        Assert.False(page.Truncated);
        Assert.Equal("Ada", page.Rows[0][1]);
        // NULL stays null so the viewer can render it distinctly from "".
        Assert.Null(page.Rows[2][1]);

        var truncatedPage = await client.QueryAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            "SELECT * FROM people;",
            maxRows: 2,
            CancellationToken.None);
        Assert.Equal(2, truncatedPage.Rows.Count);
        Assert.True(truncatedPage.Truncated);
    }

    [Fact]
    public async Task File_engines_accept_a_bare_path_as_the_connection_string()
    {
        var client = new DatabasePanelClient();
        _ = await client.QueryAsync(
            "sqlite",
            _databasePath,
            tunnel: null,
            "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT);",
            maxRows: 10,
            CancellationToken.None);

        var tables = await client.ListTablesAsync(
            "sqlite",
            _databasePath,
            tunnel: null,
            CancellationToken.None);

        Assert.Equal("notes", Assert.Single(tables).Name);
    }

    [Fact]
    public void Bare_paths_normalize_and_connection_strings_pass_through()
    {
        var sqlite = BuiltInDatabaseDrivers.All.Single(driver => string.Equals(driver.Descriptor.Id, "sqlite", StringComparison.Ordinal));
        var duckdb = BuiltInDatabaseDrivers.All.Single(driver => string.Equals(driver.Descriptor.Id, "duckdb", StringComparison.Ordinal));
        var postgres = BuiltInDatabaseDrivers.All.Single(driver => string.Equals(driver.Descriptor.Id, "postgres", StringComparison.Ordinal));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(
            "Data Source=/data/app.db",
            sqlite.NormalizeConnectionString("/data/app.db"));
        Assert.Equal(
            "Data Source=/data/app.db",
            sqlite.NormalizeConnectionString("  /data/app.db  "));
        Assert.Equal(
            $"Data Source={Path.Combine(home, "app.db")}",
            sqlite.NormalizeConnectionString("~/app.db"));
        Assert.Equal(
            "Data Source=/data/app.db;Mode=ReadOnly",
            sqlite.NormalizeConnectionString("Data Source=/data/app.db;Mode=ReadOnly"));
        Assert.Equal(
            "Data Source=/data/analytics.duckdb",
            duckdb.NormalizeConnectionString("/data/analytics.duckdb"));
        // Server engines keep their input untouched.
        Assert.Equal(
            "Host=localhost;Database=app",
            postgres.NormalizeConnectionString("Host=localhost;Database=app"));
    }

    [Fact]
    public async Task Unknown_driver_is_rejected_before_any_connection_attempt()
    {
        var client = new DatabasePanelClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListTablesAsync(
            "db2",
            "whatever",
            tunnel: null,
            CancellationToken.None));
        Assert.Throws<ArgumentException>(() =>
            client.BuildTablePreviewQuery("db2", "t", 10));
    }

    [Fact]
    public void Preview_queries_quote_hostile_table_names()
    {
        var client = new DatabasePanelClient();

        Assert.Equal(
            "SELECT * FROM \"weird\"\"name\" LIMIT 25;",
            client.BuildTablePreviewQuery("sqlite", "weird\"name", 25));
        Assert.Equal(
            "SELECT * FROM `weird``name` LIMIT 25;",
            client.BuildTablePreviewQuery("mysql", "weird`name", 25));
        Assert.Equal(
            "SELECT TOP (25) * FROM [weird]]name];",
            client.BuildTablePreviewQuery("sqlserver", "weird]name", 25));
        Assert.Equal(
            "SELECT * FROM \"t\" FETCH FIRST 25 ROWS ONLY",
            client.BuildTablePreviewQuery("oracle", "t", 25));
        Assert.Equal(
            "SELECT FIRST 25 * FROM \"t\"",
            client.BuildTablePreviewQuery("firebird", "t", 25));
    }
}
