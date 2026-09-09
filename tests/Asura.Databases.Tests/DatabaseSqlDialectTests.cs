using Asura.Application;

namespace Asura.Databases.Tests;

public sealed class DatabaseSqlDialectTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("cockroach")]
    [InlineData("redshift")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("sqlserver")]
    [InlineData("duckdb")]
    [InlineData("oracle")]
    [InlineData("firebird")]
    [InlineData("clickhouse")]
    public void InsertBudgetMatchesExactDialectBytesAtTheBoundary(string driver)
    {
        var dialect = DatabaseSqlDialect.For(driver);
        var table = new DatabaseTableDescriptor("tä\"`]ble", DatabaseTableKind.Table, Catalog: "cät", Schema: "sch\"`]ema");
        var details = new DatabaseObjectDetails(table,
            [new DatabaseColumnSchema("tä\"`]xt", 1, "jsonb", DatabaseValueKind.Text, IsNullable: true),
             new DatabaseColumnSchema("bin", 2, "BLOB", DatabaseValueKind.Binary),
             new DatabaseColumnSchema("flag", 3, "BOOLEAN", DatabaseValueKind.Boolean)], [], CanEdit: true);
        DatabaseInsertedRow[] rows = [new([
            new("tä\"`]xt", DatabaseEditValueState.Value, "🌐 O'Hara\\\r\n"),
            new("bin", DatabaseEditValueState.Value, new byte[] { 0, 197, 255 }),
            new("flag", DatabaseEditValueState.Value, true)]), new([])];
        foreach (var row in rows)
        {
            var original = dialect.BuildInsertStatement(table.Id, details, row);
            var exact = System.Text.Encoding.UTF8.GetByteCount(original);
            Assert.Equal(original, dialect.BuildInsertStatement(table.Id, details, row, exact));
            Assert.Throws<InvalidDataException>(() => dialect.BuildInsertStatement(table.Id, details, row, exact - 1));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertBudgetRejectsLargeLiteralOrIdentifierBeforeExpandedAllocation(bool identifier)
    {
        var large = new string('\'', 1_000_000);
        var table = new DatabaseTableDescriptor(identifier ? large : "items", DatabaseTableKind.Table);
        var details = new DatabaseObjectDetails(table,
            [new DatabaseColumnSchema("value", 1, "TEXT", DatabaseValueKind.Text)], [], CanEdit: true);
        var row = new DatabaseInsertedRow([new("value", DatabaseEditValueState.Value, identifier ? "text" : large)]);
        var dialect = DatabaseSqlDialect.For("postgres");
        Assert.Throws<InvalidDataException>(() => dialect.BuildInsertStatement(table.Id, details, row, 128));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => dialect.BuildInsertStatement(table.Id, details, row, 128));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 1024);
    }

    [Theory]
    [InlineData("sqlite", "Sqlite", true)]
    [InlineData("postgres", "PostgreSql", true)]
    [InlineData("cockroach", "PostgreSql", true)]
    [InlineData("redshift", "PostgreSql", false)]
    [InlineData("mysql", "MySql", true)]
    [InlineData("mariadb", "MySql", true)]
    [InlineData("sqlserver", "SqlServer", true)]
    [InlineData("duckdb", "DuckDb", true)]
    [InlineData("oracle", "Oracle", true)]
    [InlineData("firebird", "Firebird", true)]
    [InlineData("clickhouse", "ClickHouse", false)]
    public void Every_driver_maps_to_an_explicit_family_and_edit_capability(
        string driverId,
        string family,
        bool canEdit)
    {
        var dialect = DatabaseSqlDialect.For(driverId);

        Assert.Equal(family, dialect.Family.ToString());
        Assert.Equal(canEdit, dialect.CanEdit);
    }

    [Theory]
    [InlineData("sqlite", "\"app\".\"odd table\"", "'O''Hara\\path'", "X'00FF'", "1")]
    [InlineData("postgres", "\"app\".\"odd table\"", "convert_from(decode(", "decode('00FF', 'hex')", "TRUE")]
    [InlineData("cockroach", "\"app\".\"odd table\"", "convert_from(decode(", "decode('00FF', 'hex')", "TRUE")]
    [InlineData("mysql", "`app`.`odd table`", "CONVERT(X'", "X'00FF'", "TRUE")]
    [InlineData("mariadb", "`app`.`odd table`", "CONVERT(X'", "X'00FF'", "TRUE")]
    [InlineData("sqlserver", "[catalog].[app].[odd table]", "N'O''Hara\\path'", "0x00FF", "1")]
    [InlineData("duckdb", "\"catalog\".\"app\".\"odd table\"", "'O''Hara\\path'", "from_hex('00FF')", "TRUE")]
    [InlineData("oracle", "\"app\".\"odd table\"", "'O''Hara\\path'", "HEXTORAW('00FF')", "TRUE")]
    [InlineData("firebird", "\"odd table\"", "'O''Hara\\path'", "X'00FF'", "TRUE")]
    public void Insert_copy_uses_driver_identifiers_and_safe_literals(
        string driverId,
        string qualifiedTable,
        string textFragment,
        string binaryFragment,
        string booleanFragment)
    {
        var descriptor = new DatabaseTableDescriptor(
            "odd table",
            DatabaseTableKind.Table,
            Catalog: "catalog",
            Schema: "app");
        var details = new DatabaseObjectDetails(
            descriptor,
            [
                new DatabaseColumnSchema("title", 1, "TEXT", DatabaseValueKind.Text),
                new DatabaseColumnSchema("payload", 2, "BLOB", DatabaseValueKind.Binary),
                new DatabaseColumnSchema("enabled", 3, "BOOLEAN", DatabaseValueKind.Boolean),
            ],
            [],
            CanEdit: true);

        var statement = DatabaseSqlDialect.For(driverId).BuildInsertStatement(
            descriptor.Id,
            details,
            new DatabaseInsertedRow(
            [
                new DatabaseColumnEdit("title", DatabaseEditValueState.Value, "O'Hara\\path"),
                new DatabaseColumnEdit("payload", DatabaseEditValueState.Value, new byte[] { 0, 255 }),
                new DatabaseColumnEdit("enabled", DatabaseEditValueState.Value, true),
            ]));

        Assert.StartsWith($"INSERT INTO {qualifiedTable}", statement, StringComparison.Ordinal);
        Assert.Contains(textFragment, statement, StringComparison.Ordinal);
        Assert.Contains(binaryFragment, statement, StringComparison.Ordinal);
        Assert.Contains(booleanFragment, statement, StringComparison.Ordinal);
        Assert.EndsWith(");", statement, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgres", "\"archive\".\"odd\"\"table\"")]
    [InlineData("mysql", "`archive`.`odd\"table`")]
    [InlineData("sqlserver", "[catalog].[archive].[odd\"table]")]
    [InlineData("duckdb", "\"catalog\".\"archive\".\"odd\"\"table\"")]
    [InlineData("oracle", "\"archive\".\"odd\"\"table\"")]
    [InlineData("firebird", "\"odd\"\"table\"")]
    [InlineData("clickhouse", "`archive`.`odd\"table`")]
    public void Qualified_names_are_quoted_component_by_component(
        string driverId,
        string expected)
    {
        var dialect = DatabaseSqlDialect.For(driverId);

        Assert.Equal(expected, dialect.QuoteObject(new DatabaseObjectId(
            "catalog",
            "archive",
            "odd\"table")));
    }

    [Fact]
    public void User_filter_values_never_enter_generated_sql()
    {
        var dialect = DatabaseSqlDialect.For("sqlite");
        var columns = new[]
        {
            new DatabaseColumnSchema("id", 1, "INTEGER", DatabaseValueKind.SignedInteger, IsPrimaryKey: true),
            new DatabaseColumnSchema("name", 2, "TEXT", DatabaseValueKind.Text),
        };
        const string hostile = "x%' OR 1=1 --";

        var command = dialect.BuildSelect(
            new DatabaseObjectId(null, null, "people"),
            columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition("name", DatabaseFilterOperator.Contains, hostile)],
                [],
                Offset: 20,
                Limit: 10));

        Assert.DoesNotContain(hostile, command.Sql, StringComparison.Ordinal);
        Assert.Contains("\"name\" LIKE @p0 ESCAPE '!'", command.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"id\" ASC", command.Sql, StringComparison.Ordinal);
        Assert.EndsWith("LIMIT 10 OFFSET 20;", command.Sql, StringComparison.Ordinal);
        Assert.Equal("%x!%' OR 1=1 --%", Assert.Single(command.Parameters).Value);
    }

    [Fact]
    public void Table_projection_selects_only_requested_columns_and_rejects_unknowns()
    {
        var dialect = DatabaseSqlDialect.For("sqlite");
        var columns = new[]
        {
            new DatabaseColumnSchema("id", 0, "INTEGER", DatabaseValueKind.SignedInteger),
            new DatabaseColumnSchema("topic", 1, "TEXT", DatabaseValueKind.Text),
            new DatabaseColumnSchema("body", 2, "TEXT", DatabaseValueKind.Text),
        };
        var command = dialect.BuildSelect(
            new DatabaseObjectId(null, null, "articles"),
            columns,
            new DatabaseTableQuery([], [], 0, 10, Columns: ["id", "topic"]));

        Assert.StartsWith(
            "SELECT \"id\", \"topic\" FROM ",
            command.Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"body\"", command.Sql, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => dialect.BuildSelect(
            new DatabaseObjectId(null, null, "articles"),
            columns,
            new DatabaseTableQuery([], [], 0, 10, Columns: ["missing"])));
    }

    [Theory]
    [InlineData("sqlite", "@p0")]
    [InlineData("postgres", "@p0")]
    [InlineData("cockroach", "@p0")]
    [InlineData("redshift", "@p0")]
    [InlineData("mysql", "@p0")]
    [InlineData("mariadb", "@p0")]
    [InlineData("sqlserver", "@p0")]
    [InlineData("duckdb", "$p0")]
    [InlineData("oracle", ":p0")]
    [InlineData("firebird", "@p0")]
    [InlineData("clickhouse", "@p0")]
    public void Every_dialect_builds_a_parameterized_filtered_table_count(
        string driverId,
        string marker)
    {
        const string hostile = "x' OR 1=1 --";
        var command = DatabaseSqlDialect.For(driverId).BuildCount(
            new DatabaseObjectId("catalog", "app", "people"),
            [new DatabaseColumnSchema("name", 0, "TEXT", DatabaseValueKind.Text)],
            [new DatabaseFilterCondition("name", DatabaseFilterOperator.Equal, hostile)]);

        Assert.StartsWith("SELECT COUNT(*) FROM ", command.Sql, StringComparison.Ordinal);
        Assert.Contains($" = {marker}", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(hostile, command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LIMIT", command.Sql, StringComparison.OrdinalIgnoreCase);
        var parameter = Assert.Single(command.Parameters);
        Assert.Equal("p0", parameter.Name);
        Assert.Equal(hostile, parameter.Value);
    }

    [Fact]
    public void Text_match_operators_treat_wildcard_characters_as_literal_text()
    {
        var columns = new[]
        {
            new DatabaseColumnSchema("name", 1, "TEXT", DatabaseValueKind.Text),
        };
        var standard = DatabaseSqlDialect.For("postgres").BuildSelect(
            new DatabaseObjectId(null, "app", "people"),
            columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition("name", DatabaseFilterOperator.Contains, "50%_off!")],
                [],
                0,
                10));

        Assert.Contains("\"name\" LIKE @p0 ESCAPE '!'", standard.Sql, StringComparison.Ordinal);
        Assert.Equal("%50!%!_off!!%", Assert.Single(standard.Parameters).Value);

        var clickHouse = DatabaseSqlDialect.For("clickhouse").BuildSelect(
            new DatabaseObjectId(null, "app", "people"),
            columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition("name", DatabaseFilterOperator.Contains, "50%_off!")],
                [],
                0,
                10));

        Assert.Contains("position(`name`, @p0) > 0", clickHouse.Sql, StringComparison.Ordinal);
        Assert.Equal("50%_off!", Assert.Single(clickHouse.Parameters).Value);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("cockroach")]
    [InlineData("redshift")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("sqlserver")]
    [InlineData("duckdb")]
    [InlineData("oracle")]
    [InlineData("firebird")]
    [InlineData("clickhouse")]
    public void Every_dialect_parameterizes_not_contains_in_and_not_in(string driverId)
    {
        var dialect = DatabaseSqlDialect.For(driverId);
        var columns = new[]
        {
            new DatabaseColumnSchema("name", 1, "TEXT", DatabaseValueKind.Text),
        };
        var table = new DatabaseObjectId(null, "app", "people");
        const string hostile = "x%_!') OR 1=1 --";

        var notContains = dialect.BuildSelect(
            table,
            columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition(
                    "name",
                    DatabaseFilterOperator.NotContains,
                    hostile)],
                [],
                0,
                10));
        var identifier = dialect.QuoteIdentifier("name");
        var firstMarker = dialect.ParameterMarker("p0");
        Assert.DoesNotContain(hostile, notContains.Sql, StringComparison.Ordinal);
        if (string.Equals(driverId, "clickhouse", StringComparison.Ordinal))
        {
            Assert.Contains(
                $"position({identifier}, {firstMarker}) = 0",
                notContains.Sql,
                StringComparison.Ordinal);
            Assert.Equal(hostile, Assert.Single(notContains.Parameters).Value);
        }
        else
        {
            Assert.Contains(
                $"{identifier} NOT LIKE {firstMarker} ESCAPE '!'",
                notContains.Sql,
                StringComparison.Ordinal);
            Assert.Equal("%x!%!_!!') OR 1=1 --%", Assert.Single(notContains.Parameters).Value);
        }

        var included = dialect.BuildSelect(
            table,
            columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition(
                    "name",
                    DatabaseFilterOperator.In,
                    new object[] { "alpha", hostile })],
                [],
                0,
                10));
        Assert.DoesNotContain(hostile, included.Sql, StringComparison.Ordinal);
        Assert.Contains(
            $"{identifier} IN ({dialect.ParameterMarker("p0")}, {dialect.ParameterMarker("p1")})",
            included.Sql,
            StringComparison.Ordinal);
        Assert.Equal(["alpha", hostile], included.Parameters.Select(parameter => parameter.Value));

        var excluded = dialect.BuildSelect(
            table,
            columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition(
                    "name",
                    DatabaseFilterOperator.NotIn,
                    new[] { "alpha", "beta" })],
                [],
                0,
                10));
        Assert.Contains(
            $"{identifier} NOT IN ({dialect.ParameterMarker("p0")}, {dialect.ParameterMarker("p1")})",
            excluded.Sql,
            StringComparison.Ordinal);
        Assert.Equal(["alpha", "beta"], excluded.Parameters.Select(parameter => parameter.Value));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("cockroach")]
    [InlineData("redshift")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("sqlserver")]
    [InlineData("duckdb")]
    [InlineData("oracle")]
    [InlineData("firebird")]
    [InlineData("clickhouse")]
    public void Filter_lists_require_a_non_null_collection_of_at_most_500_values(
        string driverId)
    {
        var dialect = DatabaseSqlDialect.For(driverId);
        var columns = new[]
        {
            new DatabaseColumnSchema("id", 1, "INTEGER", DatabaseValueKind.SignedInteger),
        };
        var table = new DatabaseObjectId(null, null, "people");

        DatabaseSqlCommand Build(object? value) => dialect.BuildSelect(
            table,
            columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition("id", DatabaseFilterOperator.In, value)],
                [],
                0,
                10));

        Assert.Throws<ArgumentException>(() => Build("1,2"));
        Assert.Throws<ArgumentException>(() => Build(Array.Empty<object>()));
        Assert.Throws<ArgumentException>(() => Build(new object?[] { 1L, null }));
        Assert.Throws<ArgumentException>(() => Build(Enumerable.Range(1, 501).ToArray()));

        var maximum = Build(Enumerable.Range(1, 500).ToArray());
        Assert.Equal(500, maximum.Parameters.Count);
        Assert.All(maximum.Parameters, parameter => Assert.NotNull(parameter.Value));
    }

    [Theory]
    [InlineData("sqlserver", "ORDER BY [id] ASC OFFSET 5 ROWS FETCH NEXT 25 ROWS ONLY;")]
    [InlineData("oracle", "ORDER BY \"id\" ASC OFFSET 5 ROWS FETCH NEXT 25 ROWS ONLY;")]
    [InlineData("firebird", "ORDER BY \"id\" ASC ROWS 6 TO 30;")]
    [InlineData("postgres", "ORDER BY \"id\" ASC LIMIT 25 OFFSET 5;")]
    public void Paging_is_rendered_by_family(string driverId, string ending)
    {
        var dialect = DatabaseSqlDialect.For(driverId);
        var columns = new[]
        {
            new DatabaseColumnSchema(
                "id",
                1,
                "INTEGER",
                DatabaseValueKind.SignedInteger,
                IsPrimaryKey: true,
                PrimaryKeyOrdinal: 1),
        };

        var command = dialect.BuildSelect(
            new DatabaseObjectId(null, "app", "people"),
            columns,
            new DatabaseTableQuery([], [], 5, 25));

        Assert.EndsWith(ending, command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Quoted_case_distinct_columns_remain_independently_addressable()
    {
        var dialect = DatabaseSqlDialect.For("postgres");
        var descriptor = new DatabaseTableDescriptor(
            "people",
            DatabaseTableKind.Table,
            Schema: "app");
        var columns = new[]
        {
            new DatabaseColumnSchema(
                "id",
                1,
                "BIGINT",
                DatabaseValueKind.SignedInteger,
                IsPrimaryKey: true,
                PrimaryKeyOrdinal: 1),
            new DatabaseColumnSchema("name", 2, "TEXT", DatabaseValueKind.Text),
            new DatabaseColumnSchema("Name", 3, "TEXT", DatabaseValueKind.Text),
        };

        var select = dialect.BuildSelect(
            descriptor.Id,
            columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition("Name", DatabaseFilterOperator.Equal, "upper")],
                [],
                Offset: 0,
                Limit: 10));

        Assert.Contains("WHERE \"Name\" = @p0", select.Sql, StringComparison.Ordinal);

        var details = new DatabaseObjectDetails(descriptor, columns, [], CanEdit: true);
        var update = dialect.BuildUpdate(
            descriptor.Id,
            details,
            new DatabaseUpdatedRow(
                Keys: [new DatabaseColumnEdit("id", DatabaseEditValueState.Value, 7L)],
                Changes: [new DatabaseColumnEdit("Name", DatabaseEditValueState.Value, "upper changed")],
                OriginalValues: [new DatabaseColumnEdit("name", DatabaseEditValueState.Value, "lower")]));

        Assert.Equal(
            "UPDATE \"app\".\"people\" SET \"Name\" = @p0 WHERE \"id\" = @p1 AND \"name\" = @p2;",
            update.Sql);
    }

    [Fact]
    public void Oracle_all_default_insert_uses_a_values_clause()
    {
        var dialect = DatabaseSqlDialect.For("oracle");
        var descriptor = new DatabaseTableDescriptor(
            "EVENTS",
            DatabaseTableKind.Table,
            Schema: "APP");
        var details = new DatabaseObjectDetails(
            descriptor,
            [
                new DatabaseColumnSchema(
                    "ID",
                    1,
                    "NUMBER",
                    DatabaseValueKind.Decimal,
                    IsPrimaryKey: true,
                    IsIdentity: true,
                    IsReadOnly: true),
                new DatabaseColumnSchema(
                    "PAYLOAD",
                    2,
                    "VARCHAR2",
                    DatabaseValueKind.Text,
                    DefaultExpression: "'queued'"),
            ],
            [],
            CanEdit: true);

        var command = dialect.BuildInsert(
            descriptor.Id,
            details,
            new DatabaseInsertedRow(
                [new DatabaseColumnEdit("PAYLOAD", DatabaseEditValueState.Default)]));

        Assert.Equal(
            "INSERT INTO \"APP\".\"EVENTS\" (\"PAYLOAD\") VALUES (DEFAULT);",
            command.Sql);
        Assert.Empty(command.Parameters);
    }

    [Fact]
    public void Generated_columns_and_nulls_are_enforced_before_sql_generation()
    {
        var dialect = DatabaseSqlDialect.For("sqlite");
        var descriptor = new DatabaseTableDescriptor("people", DatabaseTableKind.Table);
        var details = new DatabaseObjectDetails(
            descriptor,
            [
                new DatabaseColumnSchema("id", 1, "INTEGER", DatabaseValueKind.SignedInteger, IsPrimaryKey: true),
                new DatabaseColumnSchema("name", 2, "TEXT", DatabaseValueKind.Text, IsNullable: false),
                new DatabaseColumnSchema("slug", 3, "TEXT", DatabaseValueKind.Text, IsGenerated: true),
            ],
            [],
            CanEdit: true);

        Assert.Throws<ArgumentException>(() => dialect.BuildInsert(
            descriptor.Id,
            details,
            new DatabaseInsertedRow(
                [new DatabaseColumnEdit("slug", DatabaseEditValueState.Value, "generated")])));
        Assert.Throws<ArgumentException>(() => dialect.BuildInsert(
            descriptor.Id,
            details,
            new DatabaseInsertedRow(
                [new DatabaseColumnEdit("name", DatabaseEditValueState.Null)])));
    }

    [Fact]
    public void Null_mutation_values_are_sql_literals_not_untyped_parameters()
    {
        var dialect = DatabaseSqlDialect.For("sqlserver");
        var descriptor = new DatabaseTableDescriptor(
            "documents",
            DatabaseTableKind.Table,
            Catalog: "app",
            Schema: "dbo");
        var details = new DatabaseObjectDetails(
            descriptor,
            [
                new DatabaseColumnSchema(
                    "id",
                    1,
                    "BIGINT",
                    DatabaseValueKind.SignedInteger,
                    IsPrimaryKey: true,
                    PrimaryKeyOrdinal: 1),
                new DatabaseColumnSchema(
                    "payload",
                    2,
                    "VARBINARY(32)",
                    DatabaseValueKind.Binary,
                    IsNullable: true),
            ],
            [],
            CanEdit: true);

        var insert = dialect.BuildInsert(
            descriptor.Id,
            details,
            new DatabaseInsertedRow(
                [new DatabaseColumnEdit("payload", DatabaseEditValueState.Null)]));
        var update = dialect.BuildUpdate(
            descriptor.Id,
            details,
            new DatabaseUpdatedRow(
                [new DatabaseColumnEdit("id", DatabaseEditValueState.Value, 1L)],
                [new DatabaseColumnEdit("payload", DatabaseEditValueState.Null)],
                []));

        Assert.Contains("([payload]) VALUES (NULL)", insert.Sql, StringComparison.Ordinal);
        Assert.Empty(insert.Parameters);
        Assert.Contains("SET [payload] = NULL WHERE [id] = @p0", update.Sql, StringComparison.Ordinal);
        Assert.Single(update.Parameters);
    }

    [Fact]
    public void PostgreSql_json_mutations_cast_bound_text_to_the_declared_type()
    {
        var dialect = DatabaseSqlDialect.For("postgres");
        var descriptor = new DatabaseTableDescriptor(
            "documents",
            DatabaseTableKind.Table,
            Schema: "public");
        var details = new DatabaseObjectDetails(
            descriptor,
            [
                new DatabaseColumnSchema(
                    "id",
                    1,
                    "bigint",
                    DatabaseValueKind.SignedInteger,
                    IsPrimaryKey: true,
                    PrimaryKeyOrdinal: 1),
                new DatabaseColumnSchema(
                    "payload",
                    2,
                    "jsonb",
                    DatabaseValueKind.Json),
            ],
            [],
            CanEdit: true);
        const string hostileJson = "{\"text\":\"'); DROP TABLE documents; --\"}";

        var insert = dialect.BuildInsert(
            descriptor.Id,
            details,
            new DatabaseInsertedRow(
                [new DatabaseColumnEdit(
                    "payload",
                    DatabaseEditValueState.Value,
                    hostileJson)]));
        var update = dialect.BuildUpdate(
            descriptor.Id,
            details,
            new DatabaseUpdatedRow(
                [new DatabaseColumnEdit("id", DatabaseEditValueState.Value, 1L)],
                [new DatabaseColumnEdit(
                    "payload",
                    DatabaseEditValueState.Value,
                    hostileJson)],
                []));

        Assert.Contains("CAST(@v0 AS jsonb)", insert.Sql, StringComparison.Ordinal);
        Assert.Contains("CAST(@p0 AS jsonb)", update.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(hostileJson, insert.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(hostileJson, update.Sql, StringComparison.Ordinal);
        Assert.Equal(hostileJson, Assert.Single(insert.Parameters).Value);
        Assert.Equal(hostileJson, update.Parameters[0].Value);
    }

    [Fact]
    public void Every_composite_key_component_is_required_for_mutations()
    {
        var dialect = DatabaseSqlDialect.For("postgres");
        var descriptor = new DatabaseTableDescriptor("memberships", DatabaseTableKind.Table);
        var details = new DatabaseObjectDetails(
            descriptor,
            [
                new DatabaseColumnSchema(
                    "account_id",
                    1,
                    "BIGINT",
                    DatabaseValueKind.SignedInteger,
                    IsPrimaryKey: true,
                    PrimaryKeyOrdinal: 1),
                new DatabaseColumnSchema(
                    "user_id",
                    2,
                    "BIGINT",
                    DatabaseValueKind.SignedInteger,
                    IsPrimaryKey: true,
                    PrimaryKeyOrdinal: 2),
                new DatabaseColumnSchema("role", 3, "TEXT", DatabaseValueKind.Text),
            ],
            [],
            CanEdit: true);

        var incomplete = new DatabaseUpdatedRow(
            [new DatabaseColumnEdit("account_id", DatabaseEditValueState.Value, 4L)],
            [new DatabaseColumnEdit("role", DatabaseEditValueState.Value, "owner")],
            []);

        var exception = Assert.Throws<ArgumentException>(() => dialect.BuildUpdate(
            descriptor.Id,
            details,
            incomplete));

        Assert.Contains("Every primary-key column", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Requested_sort_is_stabilized_with_every_primary_key_column()
    {
        var dialect = DatabaseSqlDialect.For("postgres");
        var columns = new[]
        {
            new DatabaseColumnSchema(
                "account_id",
                1,
                "BIGINT",
                DatabaseValueKind.SignedInteger,
                IsPrimaryKey: true,
                PrimaryKeyOrdinal: 1),
            new DatabaseColumnSchema(
                "user_id",
                2,
                "BIGINT",
                DatabaseValueKind.SignedInteger,
                IsPrimaryKey: true,
                PrimaryKeyOrdinal: 2),
            new DatabaseColumnSchema("role", 3, "TEXT", DatabaseValueKind.Text),
        };

        var command = dialect.BuildSelect(
            new DatabaseObjectId(null, "app", "memberships"),
            columns,
            new DatabaseTableQuery([], [new DatabaseSort("role", Descending: true)], 10, 25));

        Assert.Contains(
            "ORDER BY \"role\" DESC, \"account_id\" ASC, \"user_id\" ASC",
            command.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Case_distinct_quoted_columns_remain_distinct()
    {
        var dialect = DatabaseSqlDialect.For("postgres");
        var columns = new[]
        {
            new DatabaseColumnSchema("Name", 1, "TEXT", DatabaseValueKind.Text),
            new DatabaseColumnSchema("name", 2, "TEXT", DatabaseValueKind.Text),
        };

        var command = dialect.BuildSelect(
            new DatabaseObjectId(null, "app", "people"),
            columns,
            new DatabaseTableQuery(
                [new DatabaseFilterCondition("Name", DatabaseFilterOperator.Equal, "Ada")],
                [new DatabaseSort("name", Descending: true)],
                0,
                25));

        Assert.Contains("\"Name\" = @p0", command.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"name\" DESC", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_comparable_original_values_are_omitted_from_concurrency_predicate()
    {
        var dialect = DatabaseSqlDialect.For("postgres");
        var descriptor = new DatabaseTableDescriptor(
            "documents",
            DatabaseTableKind.Table,
            Schema: "public");
        var details = new DatabaseObjectDetails(
            descriptor,
            [
                new DatabaseColumnSchema(
                    "id",
                    1,
                    "BIGINT",
                    DatabaseValueKind.SignedInteger,
                    IsPrimaryKey: true,
                    PrimaryKeyOrdinal: 1),
                new DatabaseColumnSchema("title", 2, "TEXT", DatabaseValueKind.Text),
                new DatabaseColumnSchema("payload", 3, "JSON", DatabaseValueKind.Json),
            ],
            [],
            CanEdit: true);

        var command = dialect.BuildUpdate(
            descriptor.Id,
            details,
            new DatabaseUpdatedRow(
                [new DatabaseColumnEdit("id", DatabaseEditValueState.Value, 7L)],
                [new DatabaseColumnEdit("title", DatabaseEditValueState.Value, "new")],
                [new DatabaseColumnEdit("payload", DatabaseEditValueState.Value, "{\"old\":true}")]));

        Assert.DoesNotContain("\"payload\" =", command.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"id\" = @p1", command.Sql, StringComparison.Ordinal);
        Assert.Equal(2, command.Parameters.Count);
    }

    [Fact]
    public void Oracle_default_row_names_a_defaultable_column()
    {
        var dialect = DatabaseSqlDialect.For("oracle");
        var descriptor = new DatabaseTableDescriptor(
            "Events",
            DatabaseTableKind.Table,
            Schema: "MixedCase");
        var details = new DatabaseObjectDetails(
            descriptor,
            [
                new DatabaseColumnSchema(
                    "required_value",
                    1,
                    "VARCHAR2",
                    DatabaseValueKind.Text,
                    IsNullable: false),
                new DatabaseColumnSchema(
                    "optional_value",
                    2,
                    "VARCHAR2",
                    DatabaseValueKind.Text,
                    IsNullable: true),
            ],
            [],
            CanEdit: true);

        var command = dialect.BuildInsert(
            descriptor.Id,
            details,
            new DatabaseInsertedRow([]));

        Assert.Equal(
            "INSERT INTO \"MixedCase\".\"Events\" (\"optional_value\") VALUES (DEFAULT);",
            command.Sql);
    }

    [Fact]
    public void Oracle_identity_only_table_can_insert_a_default_row()
    {
        var dialect = DatabaseSqlDialect.For("oracle");
        var descriptor = new DatabaseTableDescriptor("events", DatabaseTableKind.Table, Schema: "APP");
        var details = new DatabaseObjectDetails(
            descriptor,
            [
                new DatabaseColumnSchema(
                    "id",
                    1,
                    "NUMBER",
                    DatabaseValueKind.Decimal,
                    IsIdentity: true,
                    IsReadOnly: true),
            ],
            [],
            CanEdit: true);

        var command = dialect.BuildInsert(
            descriptor.Id,
            details,
            new DatabaseInsertedRow([]));

        Assert.Equal("INSERT INTO \"APP\".\"events\" (\"id\") VALUES (DEFAULT);", command.Sql);
    }

    [Fact]
    public void Firebird_paging_does_not_overflow_at_a_large_offset()
    {
        var command = DatabaseSqlDialect.For("firebird").BuildSelect(
            new DatabaseObjectId(null, null, "events"),
            [new DatabaseColumnSchema("id", 1, "INTEGER", DatabaseValueKind.SignedInteger)],
            new DatabaseTableQuery([], [], int.MaxValue, 1));

        Assert.EndsWith("ROWS 2147483648 TO 2147483648;", command.Sql, StringComparison.Ordinal);
    }
}
