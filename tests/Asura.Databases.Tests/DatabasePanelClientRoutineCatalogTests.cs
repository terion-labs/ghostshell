using System.Data.Common;
using Asura.Application;
using Asura.Databases;
using Microsoft.Data.Sqlite;

namespace Asura.Databases.Tests;

public sealed class DatabasePanelClientRoutineCatalogTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"asura-routines-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_databasePath};Pooling=False";

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Fact]
    public async Task GroupsRoutineParametersAndDerivesOptionalAndVariadicArity()
    {
        await CreateTableAsync();
        const string routinesSql = """
            SELECT NULL, 'main', 'viewer_join', 'scalar', 'viewer_join(text...)',
                   'text', 1, 'value', 'text', 'in', 0, 1, NULL, NULL
            UNION ALL
            SELECT NULL, 'main', 'viewer_mix', 'scalar',
                   'viewer_mix(integer, text)', 'integer',
                   2, 'suffix', 'text', 'in', 1, 0, NULL, NULL
            UNION ALL
            SELECT NULL, 'main', 'viewer_mix', 'scalar',
                   'viewer_mix(integer, text)', 'integer',
                   1, 'value', 'integer', 'in', 0, 0, NULL, NULL
            ORDER BY 3, 5, 7;
            """;
        await using var client = Client(routinesSql);

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        var join = Assert.Single(catalog.Routines, item => string.Equals(item.Id.Name, "viewer_join", StringComparison.Ordinal));
        Assert.Equal(0, join.MinimumArgumentCount);
        Assert.Null(join.MaximumArgumentCount);
        Assert.True(Assert.Single(join.Parameters).IsVariadic);
        var mix = Assert.Single(catalog.Routines, item => string.Equals(item.Id.Name, "viewer_mix", StringComparison.Ordinal));
        Assert.Equal((1, 2), (mix.MinimumArgumentCount, mix.MaximumArgumentCount));
        Assert.Equal(["value", "suffix"], mix.Parameters.Select(item => item.Name), StringComparer.Ordinal);
        Assert.True(mix.Parameters[^1].IsOptional);
        Assert.Equal(DatabaseValueKind.SignedInteger, mix.ReturnValueKind);
        Assert.Equal(SqlCatalogCoverage.Complete, catalog.RoutineCoverage);
        Assert.False(catalog.IsPartial, catalog.Limitation);
    }

    [Fact]
    public async Task KeepsOverloadsSeparateByTheirMetadataSignature()
    {
        await CreateTableAsync();
        const string routinesSql = """
            SELECT NULL, 'main', 'viewer_convert', 'scalar',
                   'viewer_convert(integer)', 'text',
                   1, 'value', 'integer', 'in', 0, 0, NULL, NULL
            UNION ALL
            SELECT NULL, 'main', 'viewer_convert', 'scalar',
                   'viewer_convert(text)', 'text',
                   1, 'value', 'text', 'in', 0, 0, NULL, NULL
            ORDER BY 5, 7;
            """;
        await using var client = Client(routinesSql);

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        var overloads = catalog.Routines
            .Where(item => string.Equals(item.Id.Name, "viewer_convert", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, overloads.Length);
        Assert.Equal(
            ["viewer_convert(integer)", "viewer_convert(text)"],
            overloads.Select(item => item.Signature), StringComparer.Ordinal);
        Assert.All(overloads, routine => Assert.Single(routine.Parameters));
    }

    [Fact]
    public async Task DeduplicatesIdenticalOverloadsAfterGroupingByServerIdentity()
    {
        await CreateTableAsync();
        const string routinesSql = """
            SELECT NULL, 'main', 'viewer_duplicate', 'aggregate',
                   'viewer_duplicate(integer)', 'integer',
                   1, 'value', 'integer', 'in', 0, 0, 1, 1, 'source-a'
            UNION ALL
            SELECT NULL, 'main', 'viewer_duplicate', 'aggregate',
                   'viewer_duplicate(integer)', 'integer',
                   1, 'value', 'integer', 'in', 0, 0, 1, 1, 'source-b';
            """;
        await using var client = Client(routinesSql);

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        var routines = catalog.Routines.Where(routine => string.Equals(routine.Id.Name, "viewer_duplicate", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(routines);
        Assert.Single(routines[0].Parameters);
        Assert.False(catalog.IsPartial, catalog.Limitation);
    }

    [Fact]
    public async Task KeepsSemanticallyDifferentOverloadsSeparatedByServerIdentity()
    {
        await CreateTableAsync();
        const string routinesSql = """
            SELECT NULL, 'main', 'viewer_duplicate', 'aggregate',
                   'viewer_duplicate(value)', 'integer',
                   1, 'value', 'integer', 'in', 0, 0, 1, 1, 'source-a'
            UNION ALL
            SELECT NULL, 'main', 'viewer_duplicate', 'aggregate',
                   'viewer_duplicate(value)', 'integer',
                   1, 'value', 'text', 'in', 0, 0, 1, 1, 'source-b';
            """;
        await using var client = Client(routinesSql);

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        var routines = catalog.Routines.Where(routine => string.Equals(routine.Id.Name, "viewer_duplicate", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, routines.Length);
        Assert.Equal(
            ["integer", "text"],
            routines.Select(routine => Assert.Single(routine.Parameters).DataTypeName), StringComparer.Ordinal);
        Assert.False(catalog.IsPartial, catalog.Limitation);
    }

    [Fact]
    public async Task SkipsRoutineMetadataOutsideTheWorkerArityBound()
    {
        await CreateTableAsync();
        const string routinesSql = """
            SELECT NULL, 'main', 'viewer_absurd', 'scalar',
                   'viewer_absurd(...)', 'integer',
                   NULL, NULL, NULL, NULL, 0, 0, 1025, 1025
            UNION ALL
            SELECT NULL, 'main', 'viewer_negative', 'scalar',
                   'viewer_negative(...)', 'integer',
                   NULL, NULL, NULL, NULL, 0, 0, -1, NULL
            UNION ALL
            SELECT NULL, 'main', 'viewer_safe', 'scalar',
                   'viewer_safe(integer)', 'integer',
                   1, 'value', 'integer', 'in', 0, 0, 1, 1;
            """;
        await using var client = Client(routinesSql);

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        Assert.True(catalog.IsPartial);
        Assert.DoesNotContain(catalog.Routines, item => string.Equals(item.Id.Name, "viewer_absurd", StringComparison.Ordinal));
        Assert.DoesNotContain(catalog.Routines, item => string.Equals(item.Id.Name, "viewer_negative", StringComparison.Ordinal));
        Assert.Contains(catalog.Routines, item => string.Equals(item.Id.Name, "viewer_safe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkipsContradictoryWorkerShapesButKeepsValidOptionalVariadics()
    {
        await CreateTableAsync();
        const string routinesSql = """
            SELECT NULL, 'main', 'viewer_logical', 'scalar',
                   'viewer_logical(text, text, integer, text[])', 'text',
                   1, 'slot_name', 'text', 'in', 0, 0, 2, NULL
            UNION ALL
            SELECT NULL, 'main', 'viewer_logical', 'scalar',
                   'viewer_logical(text, text, integer, text[])', 'text',
                   2, 'upto_lsn', 'text', 'in', 0, 0, 2, NULL
            UNION ALL
            SELECT NULL, 'main', 'viewer_logical', 'scalar',
                   'viewer_logical(text, text, integer, text[])', 'text',
                   3, 'upto_nchanges', 'integer', 'in', 1, 0, 2, NULL
            UNION ALL
            SELECT NULL, 'main', 'viewer_logical', 'scalar',
                   'viewer_logical(text, text, integer, text[])', 'text',
                   4, 'options', 'text[]', 'in', 1, 1, 2, NULL
            UNION ALL
            SELECT NULL, 'main', 'viewer_bad_arity', 'scalar',
                   'viewer_bad_arity(integer)', 'integer',
                   1, 'value', 'integer', 'in', 0, 0, 0, 0
            UNION ALL
            SELECT NULL, 'main', 'viewer_bad_order', 'scalar',
                   'viewer_bad_order(integer, integer)', 'integer',
                   1, 'optional_value', 'integer', 'in', 1, 0, NULL, NULL
            UNION ALL
            SELECT NULL, 'main', 'viewer_bad_order', 'scalar',
                   'viewer_bad_order(integer, integer)', 'integer',
                   2, 'required_value', 'integer', 'in', 0, 0, NULL, NULL
            ORDER BY 3, 5, 7;
            """;
        await using var client = Client(routinesSql);

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        var logical = Assert.Single(catalog.Routines, routine => string.Equals(routine.Id.Name, "viewer_logical", StringComparison.Ordinal));
        Assert.Equal(2, logical.MinimumArgumentCount);
        Assert.Null(logical.MaximumArgumentCount);
        Assert.True(logical.Parameters[2].IsOptional);
        Assert.True(logical.Parameters[3].IsVariadic);
        Assert.DoesNotContain(catalog.Routines, routine =>
            routine.Id.Name is "viewer_bad_arity" or "viewer_bad_order");
        Assert.True(catalog.IsPartial);
    }

    [Fact]
    public async Task CapsRoutineCountWithoutSerializingAPartialRoutine()
    {
        await CreateTableAsync();
        const string routinesSql = """
            WITH RECURSIVE routine(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM routine WHERE value < 5001
            )
            SELECT NULL, 'main', printf('routine_%04d', value), 'scalar',
                   printf('routine_%04d()', value), 'integer',
                   NULL, NULL, NULL, NULL, 0, 0, 0, 0
            FROM routine
            ORDER BY value;
            """;
        await using var client = Client(routinesSql);

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        Assert.True(catalog.IsPartial);
        Assert.Equal(5000, catalog.Routines.Count);
        Assert.Contains("routine catalog", catalog.Limitation, StringComparison.OrdinalIgnoreCase);
        Assert.All(catalog.Routines, routine => Assert.NotEmpty(routine.Signature));
    }

    [Fact]
    public async Task RoutineMetadataFailureLeavesAUsableExplicitlyLimitedCatalog()
    {
        await CreateTableAsync();
        await using var client = Client("SELECT missing FROM unavailable_routines;");

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        Assert.Empty(catalog.Routines);
        Assert.NotEmpty(catalog.Objects);
        Assert.True(catalog.IsPartial);
        Assert.Contains("Routine metadata was unavailable", catalog.Limitation);
    }

    [Fact]
    public async Task IntrinsicMetadataIsBoundedDeduplicatedAndAuthoritativeOnlyOnSuccess()
    {
        await CreateTableAsync();
        const string routinesSql = """
            SELECT NULL, 'main', 'viewer_safe', 'scalar',
                   'viewer_safe()', 'integer',
                   NULL, NULL, NULL, NULL, 0, 0, 0, 0;
            """;
        const string intrinsicsSql = """
            SELECT 'CURRENT_TIMESTAMP', 'keyword'
            UNION ALL SELECT 'current_timestamp', 'keyword'
            UNION ALL SELECT 'DATEADD', 'keyword';
            """;
        await using var client = Client(routinesSql, intrinsicsSql);

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        Assert.Equal(SqlCatalogCoverage.Complete, catalog.IntrinsicCoverage);
        Assert.Equal(2, catalog.IntrinsicSymbols.Count);
        Assert.Equal(
            ["CURRENT_TIMESTAMP", "DATEADD"],
            catalog.IntrinsicSymbols.Select(symbol => symbol.Name), StringComparer.Ordinal);
        Assert.All(catalog.IntrinsicSymbols, symbol =>
            Assert.Equal(SqlCatalogIntrinsicKind.Keyword, symbol.Kind));
    }

    [Fact]
    public async Task IntrinsicMetadataFailureIsAnExplicitPartialCoverage()
    {
        await CreateTableAsync();
        const string routinesSql = """
            SELECT NULL, 'main', 'viewer_safe', 'scalar',
                   'viewer_safe()', 'integer',
                   NULL, NULL, NULL, NULL, 0, 0, 0, 0;
            """;
        await using var client = Client(
            routinesSql,
            "SELECT missing FROM unavailable_intrinsics;");

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        Assert.Equal(SqlCatalogCoverage.Partial, catalog.IntrinsicCoverage);
        Assert.Empty(catalog.IntrinsicSymbols);
        Assert.True(catalog.IsPartial);
        Assert.Contains("Intrinsic SQL metadata was unavailable", catalog.Limitation);
    }

    [Fact]
    public async Task IntrinsicMetadataStopsAtTheProtocolSafetyBound()
    {
        await CreateTableAsync();
        const string routinesSql = """
            SELECT NULL, 'main', 'viewer_safe', 'scalar',
                   'viewer_safe()', 'integer',
                   NULL, NULL, NULL, NULL, 0, 0, 0, 0;
            """;
        const string intrinsicsSql = """
            WITH RECURSIVE symbol(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM symbol WHERE value < 5001
            )
            SELECT printf('KEYWORD_%04d', value), 'keyword'
            FROM symbol
            ORDER BY value;
            """;
        await using var client = Client(routinesSql, intrinsicsSql);

        var catalog = await client.GetSqlCatalogAsync(
            "sqlite",
            ConnectionString,
            tunnel: null,
            CancellationToken.None);

        Assert.Equal(5000, catalog.IntrinsicSymbols.Count);
        Assert.Equal(SqlCatalogCoverage.Partial, catalog.IntrinsicCoverage);
        Assert.True(catalog.IsPartial);
        Assert.Contains("intrinsic-symbol catalog", catalog.Limitation);
    }

    private DatabasePanelClient Client(
        string routinesSql,
        string? intrinsicSymbolsSql = null) =>
        new([new RoutineTestDriver(routinesSql, intrinsicSymbolsSql)]);

    private async Task CreateTableAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE viewer_rows(id INTEGER PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RoutineTestDriver(
        string routinesSql,
        string? intrinsicSymbolsSql) : IDatabaseDriver
    {
        public DatabaseDriverDescriptor Descriptor { get; } = new(
            "sqlite",
            "Routine test",
            "Data Source=…",
            IsFileBased: true);

        public string ListTablesSql => """
            SELECT NULL, 'main', name, 'table'
            FROM sqlite_master
            WHERE type = 'table'
            ORDER BY name;
            """;

        public string SqlCatalogDefaultsSql => "SELECT NULL, 'main';";

        public string ListRoutinesSql => routinesSql;

        public SqlCatalogCoverage RoutineCatalogCoverage => SqlCatalogCoverage.Complete;

        public string? ListIntrinsicSymbolsSql => intrinsicSymbolsSql;

        public SqlCatalogCoverage IntrinsicCatalogCoverage => intrinsicSymbolsSql is null
            ? SqlCatalogCoverage.None
            : SqlCatalogCoverage.Complete;

        public DbConnection CreateConnection(string connectionString) =>
            new SqliteConnection(connectionString);

        public string QuoteIdentifier(string identifier) =>
            $"\"{identifier.Replace("\"", "\"\"")}\"";

        public string BuildPreviewQuery(string tableName, int limit) =>
            $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

        public DatabaseConnectionDetails ParseDetails(string connectionString) => new();

        public string BuildConnectionString(DatabaseConnectionDetails details) => string.Empty;
    }
}
