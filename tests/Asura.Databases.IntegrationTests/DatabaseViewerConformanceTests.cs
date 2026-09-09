using Asura.Application;
using Asura.Infrastructure;
using Xunit.Abstractions;

namespace Asura.Databases.IntegrationTests;

public sealed partial class DatabaseViewerConformanceTests(ITestOutputHelper output)
{
    private const string SqlLanguageAlias = "asura_rows";

    public static IEnumerable<object[]> Providers =>
        DatabaseProviderSelection.SelectedProviderIds();

    [DatabaseIntegrationTheory]
    [MemberData(nameof(Providers))]
    public async Task Every_database_viewer_operation_conforms(string providerId)
    {
        var provider = DatabaseProviderCatalog.Get(providerId);
        output.WriteLine($"Starting {provider.DisplayName} ({provider.Id})...");
        if (provider.Expectations.CompatibilityNote is { } note)
        {
            output.WriteLine($"Compatibility scope: {note}");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        await using var environment = await provider.StartAsync(timeout.Token);
        await using var client = new DatabasePanelClient();

        await WaitUntilReadyAsync(client, environment, timeout.Token);
        await SeedAsync(client, environment, timeout.Token);
        var objects = await AssertDriverAndCatalogAsync(client, environment, timeout.Token);
        await AssertSchemaDiagramAsync(client, environment, objects, timeout.Token);
        var sqlCatalog = await AssertSqlLanguageCatalogAsync(
            client,
            environment,
            objects,
            timeout.Token);
        await AssertSqlLanguageWorkerAsync(
            client,
            environment,
            sqlCatalog,
            objects,
            timeout.Token);
        await AssertTypedBrowsingAsync(client, environment, objects, timeout.Token);
        await AssertMutationsAsync(client, environment, objects, timeout.Token);
        await AssertViewModelJourneyAsync(client, environment, objects, timeout.Token);
        await AssertHeadlessViewJourneyAsync(
            client,
            environment,
            objects,
            timeout.Token);

        await AssertQueryFailureRecoveryAsync(client, environment, timeout.Token);
        await AssertLiveDefaultNamespaceAsync(client, environment, timeout.Token);

        output.WriteLine($"{provider.DisplayName}: complete conformance workflow passed.");
    }

    private static async Task AssertSchemaDiagramAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        CancellationToken cancellationToken)
    {
        var graph = await client.GetDatabaseSchemaGraphAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            cancellationToken);

        Assert.NotEmpty(graph.Tables);
        Assert.DoesNotContain(graph.Tables, table =>
            table.Object.Kind == DatabaseTableKind.View);
        var rows = Assert.Single(graph.Tables, table =>
            table.Object.Id == objects.Rows.Id);
        Assert.Equal(
            objects.Rows.Name,
            rows.Object.Name);
        Assert.Contains(rows.Columns, column => string.Equals(column.Name, "id", StringComparison.Ordinal));
        Assert.Contains(rows.Columns, column => string.Equals(column.Name, "title", StringComparison.Ordinal));

        if (DiagramRelationshipSeed(environment.Provider.Id).Count > 0)
        {
            var child = Assert.Single(graph.Tables, table => string.Equals(table.Object.Name, "viewer_er_child", StringComparison.Ordinal));
            var relationship = Assert.Single(child.ForeignKeys);
            Assert.False(string.IsNullOrWhiteSpace(relationship.Name));
            Assert.Equal("viewer_er_parent", relationship.ReferencedObject.Name);
            var pair = Assert.Single(relationship.Columns);
            Assert.Equal("parent_id", pair.ColumnName);
            Assert.Equal("id", pair.ReferencedColumnName);
        }

        var mermaid = DatabaseMermaidErDiagram.Create(graph);
        Assert.StartsWith("```mermaid", mermaid, StringComparison.Ordinal);
        Assert.Contains(objects.Rows.DisplayName, mermaid, StringComparison.Ordinal);
        if (DiagramRelationshipSeed(environment.Provider.Id).Count > 0)
        {
            Assert.Contains("viewer_er_parent", mermaid, StringComparison.Ordinal);
            Assert.Contains("viewer_er_child", mermaid, StringComparison.Ordinal);
        }
        Assert.EndsWith("```\n", mermaid, StringComparison.Ordinal);
    }

    private static async Task<SqlCatalogSnapshot> AssertSqlLanguageCatalogAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        CancellationToken cancellationToken)
    {
        var driver = Assert.Single(
            BuiltInDatabaseDrivers.All,
            candidate => string.Equals(candidate.Descriptor.Id, environment.Provider.Id, StringComparison.Ordinal));
        if (driver.ListRoutinesSql is { } routinesSql)
        {
            // Product metadata is deliberately fail-soft. Execute the provider
            // query directly here so syntax/catalog regressions cannot masquerade
            // as a valid empty optional routine catalog.
            var routineRows = await client.QueryAsync(
                environment.Provider.Id,
                environment.ConnectionString,
                tunnel: null,
                routinesSql,
                maxRows: 10,
                cancellationToken);
            Assert.InRange(routineRows.Columns.Count, 14, 15);
            Assert.NotEmpty(routineRows.Rows);
        }

        if (driver.ListIntrinsicSymbolsSql is { } intrinsicSql)
        {
            var intrinsicRows = await client.QueryAsync(
                environment.Provider.Id,
                environment.ConnectionString,
                tunnel: null,
                intrinsicSql,
                maxRows: 10,
                cancellationToken);
            Assert.NotEmpty(intrinsicRows.Columns);
            Assert.NotEmpty(intrinsicRows.Rows);
        }

        var catalog = await client.GetSqlCatalogAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            cancellationToken);

        Assert.Equal(environment.Provider.Id, catalog.DriverId);
        var rows = Assert.Single(catalog.Objects, item => string.Equals(item.Id.Name, objects.Rows.Name, StringComparison.Ordinal) && item.Kind == DatabaseTableKind.Table);
        Assert.Equal(DatabaseTableKind.Table, rows.Kind);
        Assert.Contains(rows.Columns, column => string.Equals(column.Name, "id"
, StringComparison.Ordinal) && column.ValueKind is DatabaseValueKind.SignedInteger
                or DatabaseValueKind.UnsignedInteger
                or DatabaseValueKind.Decimal);
        Assert.Contains(rows.Columns, column => string.Equals(column.Name, "title", StringComparison.Ordinal));

        var view = Assert.Single(catalog.Objects, item => string.Equals(item.Id.Name, objects.View.Name, StringComparison.Ordinal) && item.Kind == DatabaseTableKind.View);
        Assert.Equal(DatabaseTableKind.View, view.Kind);
        Assert.Contains(view.Columns, column => string.Equals(column.Name, "title", StringComparison.Ordinal));
        Assert.All(catalog.Objects, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id.Name));
            Assert.NotEmpty(item.Columns);
            Assert.All(item.Columns, column =>
            {
                Assert.False(string.IsNullOrWhiteSpace(column.Name));
                Assert.False(string.IsNullOrWhiteSpace(column.DataTypeName));
            });
        });

        if (driver.ListRoutinesSql is null)
        {
            Assert.Empty(catalog.Routines);
        }
        else
        {
            Assert.NotEmpty(catalog.Routines);
            Assert.All(catalog.Routines, routine =>
            {
                Assert.False(string.IsNullOrWhiteSpace(routine.Id.Name));
                Assert.False(string.IsNullOrWhiteSpace(routine.Signature));
                Assert.InRange(routine.MinimumArgumentCount, 0, 1024);
                if (routine.MaximumArgumentCount is { } maximum)
                {
                    Assert.InRange(maximum, routine.MinimumArgumentCount, 1024);
                }

                Assert.InRange(routine.Parameters.Count, 0, 1024);
                Assert.All(routine.Parameters, parameter =>
                    Assert.False(string.IsNullOrWhiteSpace(parameter.DataTypeName)));
            });
        }

        AssertSeededRoutineMetadata(environment.Provider.Id, catalog);
        AssertCatalogCoverage(environment.Provider.Id, catalog, driver);

        return catalog;
    }

    private static void AssertCatalogCoverage(
        string providerId,
        SqlCatalogSnapshot catalog,
        IDatabaseDriver driver)
    {
        Assert.Equal(driver.RoutineCatalogCoverage, catalog.RoutineCoverage);
        Assert.Equal(driver.IntrinsicCatalogCoverage, catalog.IntrinsicCoverage);
        if (driver.ListIntrinsicSymbolsSql is null)
        {
            Assert.Empty(catalog.IntrinsicSymbols);
        }
        else
        {
            Assert.NotEmpty(catalog.IntrinsicSymbols);
            Assert.All(catalog.IntrinsicSymbols, symbol =>
            {
                Assert.False(string.IsNullOrWhiteSpace(symbol.Name));
                Assert.Equal(SqlCatalogIntrinsicKind.Keyword, symbol.Kind);
            });
        }

        if (providerId is "sqlite" or "postgres" or "cockroach" or "clickhouse")
        {
            Assert.Contains(catalog.IntrinsicSymbols, symbol =>
                symbol.Name.Equals(
                    "current_timestamp",
                    StringComparison.OrdinalIgnoreCase));
        }
        else if (string.Equals(providerId, "firebird", StringComparison.Ordinal))
        {
            Assert.Contains(catalog.IntrinsicSymbols, symbol => string.Equals(symbol.Name, "CURRENT_TIMESTAMP", StringComparison.Ordinal));
            Assert.Contains(catalog.IntrinsicSymbols, symbol => string.Equals(symbol.Name, "DATEADD", StringComparison.Ordinal));
        }
        else if (providerId is "mysql" or "mariadb")
        {
            Assert.Contains(catalog.IntrinsicSymbols, symbol =>
                symbol.Name.Equals("ABS", StringComparison.OrdinalIgnoreCase));
        }
        else if (string.Equals(providerId, "oracle", StringComparison.Ordinal))
        {
            Assert.Contains(catalog.IntrinsicSymbols, symbol =>
                symbol.Name.Equals(
                    "concat",
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertSeededRoutineMetadata(
        string providerId,
        SqlCatalogSnapshot catalog)
    {
        if (string.Equals(providerId, "postgres", StringComparison.Ordinal))
        {
            Assert.False(catalog.IsPartial, catalog.Limitation);
            var dateAddOverloads = catalog.Routines.Where(routine => string.Equals(routine.Id.Schema, "pg_catalog"
, StringComparison.Ordinal) && string.Equals(routine.Id.Name, "date_add", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, dateAddOverloads.Length);
            Assert.All(dateAddOverloads, dateAdd =>
            {
                Assert.Equal(SqlCatalogRoutineKind.Scalar, dateAdd.Kind);
                Assert.Contains(
                    "interval",
                    dateAdd.Signature,
                    StringComparison.OrdinalIgnoreCase);
            });
            Assert.Contains(dateAddOverloads, dateAdd =>
                dateAdd.MinimumArgumentCount == 2
                && dateAdd.MaximumArgumentCount == 2
                && dateAdd.Parameters.Count == 2);
            Assert.Contains(dateAddOverloads, dateAdd =>
                dateAdd.MinimumArgumentCount == 3
                && dateAdd.MaximumArgumentCount == 3
                && dateAdd.Parameters.Count == 3);
            Assert.Contains(catalog.Routines, routine => string.Equals(routine.Id.Schema, "pg_catalog"
, StringComparison.Ordinal) && string.Equals(routine.Id.Name, "to_timestamp", StringComparison.Ordinal));
            var logicalSlotChanges = Assert.Single(catalog.Routines, routine => string.Equals(routine.Id.Schema, "pg_catalog"
, StringComparison.Ordinal) && string.Equals(routine.Id.Name, "pg_logical_slot_get_binary_changes", StringComparison.Ordinal));
            Assert.Equal(2, logicalSlotChanges.MinimumArgumentCount);
            Assert.Null(logicalSlotChanges.MaximumArgumentCount);
            Assert.True(Assert.Single(logicalSlotChanges.Parameters, parameter => string.Equals(parameter.Name, "upto_nchanges", StringComparison.Ordinal)).IsOptional);
            Assert.True(Assert.Single(logicalSlotChanges.Parameters, parameter => string.Equals(parameter.Name, "options", StringComparison.Ordinal)).IsVariadic);
            var extraSchemaIdentity = Assert.Single(catalog.Routines, routine => string.Equals(routine.Id.Schema, "asura_extra"
, StringComparison.Ordinal) && string.Equals(routine.Id.Name, "viewer_identity", StringComparison.Ordinal));
            Assert.Equal((1, 1),
                (extraSchemaIdentity.MinimumArgumentCount,
                    extraSchemaIdentity.MaximumArgumentCount));
            return;
        }

        if (string.Equals(providerId, "cockroach", StringComparison.Ordinal))
        {
            Assert.False(catalog.IsPartial, catalog.Limitation);
            Assert.Contains(catalog.Routines, routine => string.Equals(routine.Id.Schema, "pg_catalog"
, StringComparison.Ordinal) && string.Equals(routine.Id.Name, "unique_rowid"
, StringComparison.Ordinal) && routine.MinimumArgumentCount == 0
                && routine.MaximumArgumentCount == 0);
            return;
        }

        if (providerId is "mysql" or "mariadb" or "sqlserver" or "oracle" or "firebird")
        {
            var identity = Assert.Single(catalog.Routines, routine =>
                string.Equals(
                    routine.Id.Name,
                    "viewer_identity",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Equal(SqlCatalogRoutineKind.Scalar, identity.Kind);
            Assert.Equal((1, 1),
                (identity.MinimumArgumentCount, identity.MaximumArgumentCount));
            Assert.Single(identity.Parameters);
        }
    }

    private static async Task AssertSqlLanguageWorkerAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        SqlCatalogSnapshot catalog,
        DatabaseObjects objects,
        CancellationToken cancellationToken)
    {
        ISqlLanguageService service = new CalciteSqlLanguageService();
        if (!service.IsAvailable)
        {
            Assert.False(
                string.Equals(
                    Environment.GetEnvironmentVariable(
                        "ASURA_RUN_SQL_LANGUAGE_NATIVE"),
                    "1",
                    StringComparison.Ordinal),
                "Native SQL language coverage was required, but the worker executable "
                    + "could not be resolved from ASURA_SQL_LANGUAGE_WORKER.");
            return;
        }

        await using var session = await service.OpenSessionAsync(catalog, cancellationToken);
        Assert.True(
            session.IsAvailable,
            $"The native SQL language worker did not initialize for '{catalog.DriverId}': "
                + session.UnavailableReason);

        var table = Assert.Single(catalog.Objects, item => string.Equals(item.Id.Name, objects.Rows.Name, StringComparison.Ordinal) && item.Kind == DatabaseTableKind.Table);
        var objectName = QuoteSqlLanguageObject(catalog.DriverId, table.Id);
        var quote = BuiltInDatabaseDrivers.All
            .Single(driver => string.Equals(driver.Descriptor.Id, catalog.DriverId, StringComparison.Ordinal))
            .QuoteIdentifier;
        await AssertSqlAliasIntelligenceAsync(
            session,
            catalog.DriverId,
            objectName,
            quote,
            cancellationToken);
        // A null default pair means metadata could not prove an unqualified
        // resolution path. Do not make the test (or editor) invent one.
        if (catalog.DefaultCatalog is not null || catalog.DefaultSchema is not null)
        {
            await AssertSqlAliasIntelligenceAsync(
                session,
                catalog.DriverId,
                quote(table.Id.Name),
                quote,
                cancellationToken);
        }

        var productionPreview = client.BuildTablePreviewQuery(
            catalog.DriverId,
            objects.Rows.Id,
            limit: 200);
        var productionDiagnostics = await session.DiagnoseAsync(
            productionPreview,
            cancellationToken);
        Assert.DoesNotContain(productionDiagnostics, diagnostic =>
            diagnostic.Severity == SqlDiagnosticSeverity.Error);

        const string missingColumn = "asura_column_that_does_not_exist";
        var invalidSql = $"SELECT {SqlLanguageAlias}.{quote(missingColumn)} "
            + $"FROM {objectName} {SqlLanguageAlias}";
        var invalid = await session.DiagnoseAsync(invalidSql, cancellationToken);
        Assert.Contains(invalid, diagnostic =>
            diagnostic.Severity == SqlDiagnosticSeverity.Error
            && diagnostic.Message.Contains(missingColumn, StringComparison.OrdinalIgnoreCase));

        await AssertProviderExtensionIntelligenceAsync(
            session,
            catalog.DriverId,
            objectName,
            quote,
            cancellationToken);
        await AssertServerRoutineIntelligenceAsync(
            client,
            environment,
            session,
            catalog,
            table,
            objectName,
            quote,
            cancellationToken);
    }

    private static async Task AssertServerRoutineIntelligenceAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        ISqlLanguageSession session,
        SqlCatalogSnapshot catalog,
        SqlCatalogObject table,
        string objectName,
        Func<string, string> quote,
        CancellationToken cancellationToken)
    {
        if (string.Equals(catalog.DriverId, "sqlite", StringComparison.Ordinal))
        {
            Assert.Contains(catalog.Routines, routine => string.Equals(routine.Id.Schema, "main"
, StringComparison.Ordinal) && string.Equals(routine.Id.Name, "json_array_length", StringComparison.Ordinal));
            await AssertRoutineCompletionAsync(
                session,
                $"SELECT json_array_l FROM {objectName}",
                "json_array_l",
                "json_array_length",
                "json_array_length(",
                cancellationToken);
            var sqliteResult = await client.QueryAsync(
                environment.Provider.Id,
                environment.ConnectionString,
                tunnel: null,
                $"SELECT json_array_length('[]') FROM {objectName}",
                maxRows: 1,
                cancellationToken);
            Assert.Single(sqliteResult.Rows);
            return;
        }

        if (string.Equals(catalog.DriverId, "sqlserver", StringComparison.Ordinal))
        {
            await AssertRoutineCompletionAsync(
                session,
                $"SELECT viewer_i FROM {objectName}",
                "viewer_i",
                "viewer_identity",
                "dbo.viewer_identity(",
                cancellationToken);
            return;
        }

        if (string.Equals(catalog.DriverId, "cockroach", StringComparison.Ordinal))
        {
            await AssertRoutineCompletionAsync(
                session,
                $"SELECT unique_r FROM {objectName}",
                "unique_r",
                "unique_rowid",
                "unique_rowid(",
                cancellationToken);
            return;
        }

        if (!string.Equals(catalog.DriverId, "postgres", StringComparison.Ordinal))
        {
            return;
        }

        await AssertRoutineCompletionAsync(
            session,
            $"SELECT viewer_i FROM {objectName}",
            "viewer_i",
            "viewer_identity",
            "asura_extra.viewer_identity(",
            cancellationToken);

        var timestamp = Assert.Single(table.Columns, column => string.Equals(column.Name, "created_at", StringComparison.Ordinal));
        var expressionPrefix = $"SELECT * FROM {objectName} WHERE "
            + $"{quote(timestamp.Name)} < ";
        await AssertRoutineCompletionAsync(
            session,
            $"{expressionPrefix}date_",
            "date_",
            "date_add",
            "date_add(",
            cancellationToken);
        await AssertRoutineCompletionAsync(
            session,
            $"{expressionPrefix}date_add(CURRENT_TIMESTAMP, '1 day'::int",
            "int",
            "INTERVAL",
            "INTERVAL",
            cancellationToken,
            SqlCompletionItemKind.DataType);

        var userExpression = $"{expressionPrefix}"
            + "date_add(CURRENT_TIMESTAMP, '1 day'::INTERVAL)";
        var diagnostics = await session.DiagnoseAsync(
            userExpression,
            cancellationToken);
        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Severity == SqlDiagnosticSeverity.Error);
        var result = await client.QueryAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            userExpression,
            maxRows: 1,
            cancellationToken);
        Assert.Single(result.Rows);
    }

    private static async Task AssertRoutineCompletionAsync(
        ISqlLanguageSession session,
        string sql,
        string typedPrefix,
        string expectedLabel,
        string expectedInsertText,
        CancellationToken cancellationToken,
        SqlCompletionItemKind expectedKind = SqlCompletionItemKind.Function)
    {
        var prefixStart = sql.LastIndexOf(typedPrefix, StringComparison.Ordinal);
        Assert.True(prefixStart >= 0, $"'{typedPrefix}' is not present in '{sql}'.");
        var cursorOffset = prefixStart + typedPrefix.Length;
        var completion = await session.CompleteAsync(
            sql,
            cursorOffset,
            cancellationToken);
        Assert.Equal(cursorOffset - typedPrefix.Length, completion.ReplacementStart);
        Assert.Equal(typedPrefix.Length, completion.ReplacementLength);
        _ = Assert.Single(completion.Items, candidate => string.Equals(candidate.Label, expectedLabel
, StringComparison.Ordinal) && candidate.Kind == expectedKind
            && string.Equals(candidate.InsertText, expectedInsertText, StringComparison.Ordinal));
    }

    private static async Task AssertSqlAliasIntelligenceAsync(
        ISqlLanguageSession session,
        string driverId,
        string objectName,
        Func<string, string> quote,
        CancellationToken cancellationToken)
    {
        var completionSql =
            $"SELECT {SqlLanguageAlias}. FROM {objectName} {SqlLanguageAlias}";
        var completion = await session.CompleteAsync(
            completionSql,
            completionSql.IndexOf(
                $"{SqlLanguageAlias}.",
                StringComparison.Ordinal) + SqlLanguageAlias.Length + 1,
            cancellationToken);
        Assert.True(
            completion.Items.Any(item =>
                item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "id", StringComparison.Ordinal)),
            $"{driverId} did not complete 'id' through {objectName}.");
        Assert.True(
            completion.Items.Any(item =>
                item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "title", StringComparison.Ordinal)),
            $"{driverId} did not complete 'title' through {objectName}.");

        var validSql = $"SELECT {SqlLanguageAlias}.{quote("id")}, "
            + $"{SqlLanguageAlias}.{quote("title")} "
            + $"FROM {objectName} {SqlLanguageAlias}";
        var valid = await session.DiagnoseAsync(validSql, cancellationToken);
        Assert.True(
            valid.Count == 0,
            $"{driverId} valid SQL through {objectName} produced diagnostics: "
                + string.Join(" | ", valid.Select(item => item.Message)));
    }

    private static async Task AssertProviderExtensionIntelligenceAsync(
        ISqlLanguageSession session,
        string driverId,
        string objectName,
        Func<string, string> quote,
        CancellationToken cancellationToken)
    {
        var completionSql = ProviderExtensionQuery(
            driverId,
            $"{SqlLanguageAlias}.",
            objectName);
        if (completionSql is null)
        {
            return;
        }

        var cursorOffset = completionSql.IndexOf(
            $"{SqlLanguageAlias}.",
            StringComparison.Ordinal) + SqlLanguageAlias.Length + 1;
        var completion = await session.CompleteAsync(
            completionSql,
            cursorOffset,
            cancellationToken);
        Assert.True(
            completion.Items.Any(item =>
                item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "id", StringComparison.Ordinal)),
            $"{driverId} provider extension discarded alias completion for 'id'.");
        Assert.True(
            completion.Items.Any(item =>
                item.Kind == SqlCompletionItemKind.Column && string.Equals(item.Label, "title", StringComparison.Ordinal)),
            $"{driverId} provider extension discarded alias completion for 'title'.");

        var validSql = ProviderExtensionQuery(
            driverId,
            $"{SqlLanguageAlias}.{quote("id")}",
            objectName)!;
        var valid = await session.DiagnoseAsync(validSql, cancellationToken);
        Assert.DoesNotContain(valid, diagnostic =>
            diagnostic.Severity == SqlDiagnosticSeverity.Error);

        const string missingColumn = "asura_extension_column_that_does_not_exist";
        var invalidSql = ProviderExtensionQuery(
            driverId,
            $"{SqlLanguageAlias}.{quote(missingColumn)}",
            objectName)!;
        var invalid = await session.DiagnoseAsync(invalidSql, cancellationToken);
        Assert.Contains(invalid, diagnostic =>
            diagnostic.Severity == SqlDiagnosticSeverity.Error
            && diagnostic.Message.Contains(missingColumn, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ProviderExtensionQuery(
        string driverId,
        string projection,
        string objectName) => driverId switch
        {
            "sqlserver" => $"SELECT TOP (10) {projection} "
                + $"FROM {objectName} {SqlLanguageAlias}",
            "firebird" => $"SELECT FIRST 10 {projection} "
                + $"FROM {objectName} {SqlLanguageAlias}",
            "clickhouse" => $"SELECT {projection} "
                + $"FROM {objectName} {SqlLanguageAlias} SETTINGS max_threads = 1",
            _ => null,
        };

    private static string QuoteSqlLanguageObject(string driverId, DatabaseObjectId objectId)
    {
        var quote = BuiltInDatabaseDrivers.All
            .Single(driver => string.Equals(driver.Descriptor.Id, driverId, StringComparison.Ordinal))
            .QuoteIdentifier;
        var components = driverId switch
        {
            "sqlserver" or "duckdb" =>
                Present(objectId.Catalog, objectId.Schema, objectId.Name),
            "sqlite" => Present(objectId.Schema ?? "main", objectId.Name),
            "postgres" or "cockroach" or "redshift" or "mysql" or "mariadb"
                or "oracle" or "clickhouse" => Present(objectId.Schema, objectId.Name),
            _ => Present(objectId.Name),
        };
        return string.Join('.', components.Select(quote));
    }

    private static IReadOnlyList<string> Present(params string?[] values) => [.. values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)];

    private static async Task WaitUntilReadyAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = await client.QueryAsync(
                    environment.Provider.Id,
                    environment.ConnectionString,
                    tunnel: null,
                    environment.Provider.ReadySql,
                    maxRows: 1,
                    cancellationToken);
                return;
            }
            catch (Exception exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = exception;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"{environment.Provider.DisplayName} opened its port but never accepted the production driver connection.",
            lastFailure);
    }

    private static async Task SeedAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        foreach (var statement in environment.Provider.Seed.Statements)
        {
            _ = await client.QueryAsync(
                environment.Provider.Id,
                environment.ConnectionString,
                tunnel: null,
                statement,
                maxRows: 1,
                cancellationToken);
        }

        foreach (var statement in DiagramRelationshipSeed(environment.Provider.Id))
        {
            _ = await client.QueryAsync(
                environment.Provider.Id,
                environment.ConnectionString,
                tunnel: null,
                statement,
                maxRows: 1,
                cancellationToken);
        }
    }

    private static IReadOnlyList<string> DiagramRelationshipSeed(string providerId) =>
        providerId switch
        {
            "sqlite" or "duckdb" or "postgres" or "cockroach" =>
            [
                "CREATE TABLE viewer_er_parent (id INTEGER PRIMARY KEY)",
                "CREATE TABLE viewer_er_child (id INTEGER PRIMARY KEY, parent_id INTEGER, "
                    + "CONSTRAINT fk_viewer_er_parent FOREIGN KEY (parent_id) "
                    + "REFERENCES viewer_er_parent (id))",
            ],
            "mysql" or "mariadb" =>
            [
                "CREATE TABLE viewer_er_parent (id INT PRIMARY KEY) ENGINE=InnoDB",
                "CREATE TABLE viewer_er_child (id INT PRIMARY KEY, parent_id INT, "
                    + "CONSTRAINT fk_viewer_er_parent FOREIGN KEY (parent_id) "
                    + "REFERENCES viewer_er_parent (id)) ENGINE=InnoDB",
            ],
            "sqlserver" =>
            [
                "CREATE TABLE [viewer_er_parent] ([id] INT PRIMARY KEY)",
                "CREATE TABLE [viewer_er_child] ([id] INT PRIMARY KEY, [parent_id] INT, "
                    + "CONSTRAINT [fk_viewer_er_parent] FOREIGN KEY ([parent_id]) "
                    + "REFERENCES [viewer_er_parent] ([id]))",
            ],
            "oracle" or "firebird" =>
            [
                "CREATE TABLE \"viewer_er_parent\" (\"id\" INTEGER PRIMARY KEY)",
                "CREATE TABLE \"viewer_er_child\" (\"id\" INTEGER PRIMARY KEY, \"parent_id\" INTEGER, "
                    + "CONSTRAINT \"fk_viewer_er_parent\" FOREIGN KEY (\"parent_id\") "
                    + "REFERENCES \"viewer_er_parent\" (\"id\"))",
            ],
            _ => [],
        };

    private static async Task<DatabaseObjects> AssertDriverAndCatalogAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        var provider = environment.Provider;
        var driver = Assert.Single(client.Drivers, candidate => string.Equals(candidate.Id, provider.Id, StringComparison.Ordinal));
        Assert.Equal(provider.Id is "sqlite" or "duckdb", driver.IsFileBased);

        var connectionDetails = client.ParseConnectionDetails(
            provider.Id,
            environment.ConnectionString);
        var rebuiltConnectionString = client.BuildConnectionString(provider.Id, connectionDetails);
        var tables = await client.ListTablesAsync(
            provider.Id,
            rebuiltConnectionString,
            tunnel: null,
            cancellationToken);

        var rows = FindObject(tables, provider.Seed.RowsTable, DatabaseTableKind.Table);
        var keyless = FindObject(tables, provider.Seed.KeylessTable, provider.Seed.KeylessKind);
        var view = FindObject(tables, provider.Seed.View, DatabaseTableKind.View);
        var hostile = FindObject(tables, provider.Seed.HostileTable, DatabaseTableKind.Table);

        var rowsDetails = await client.GetObjectDetailsAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            rows,
            cancellationToken);
        var id = Assert.Single(rowsDetails.Columns, column => string.Equals(column.Name, "id", StringComparison.Ordinal));
        Assert.True(id.IsPrimaryKey);
        Assert.Equal(1, id.PrimaryKeyOrdinal);
        Assert.Equal(provider.Expectations.HasIdentity, id.IsIdentity);
        var code = Assert.Single(rowsDetails.Columns, column => string.Equals(column.Name, "code", StringComparison.Ordinal));
        var score = Assert.Single(rowsDetails.Columns, column => string.Equals(column.Name, "score", StringComparison.Ordinal));
        var noteColumn = Assert.Single(rowsDetails.Columns, column => string.Equals(column.Name, "note", StringComparison.Ordinal));
        var status = Assert.Single(rowsDetails.Columns, column => string.Equals(column.Name, "status", StringComparison.Ordinal));
        Assert.True(noteColumn.IsNullable);
        Assert.False(status.IsNullable);
        Assert.NotNull(status.DefaultExpression);
        Assert.Contains(
            "draft",
            status.DefaultExpression,
            StringComparison.OrdinalIgnoreCase);
        if (provider.Expectations.ExpectedCodeLength is { } codeLength)
        {
            Assert.Equal(codeLength, code.Length);
        }

        if (provider.Expectations.ExpectedScorePrecision is { } scorePrecision)
        {
            Assert.Equal(scorePrecision, score.Precision);
        }

        if (provider.Expectations.ExpectedScoreScale is { } scoreScale)
        {
            Assert.Equal(scoreScale, score.Scale);
        }

        var generated = Assert.Single(
            rowsDetails.Columns,
            column => string.Equals(column.Name, "computed_label", StringComparison.Ordinal));
        Assert.Equal(provider.Expectations.HasGeneratedColumn, generated.IsGenerated);
        Assert.Equal(provider.Expectations.HasGeneratedColumn, generated.IsReadOnly);
        if (provider.Expectations.HasGeneratedColumn)
        {
            Assert.False(generated.CanEdit);
        }

        Assert.Equal(provider.Expectations.CanEdit, rowsDetails.CanEdit);
        if (provider.Expectations.HasIndexes)
        {
            var scoreIndex = Assert.Single(
                rowsDetails.Indexes,
                index => string.Equals(index.Name, "idx_viewer_rows_score", StringComparison.Ordinal));
            Assert.NotNull(provider.Expectations.ScoreIndex);
            AssertScoreIndex(scoreIndex, provider.Expectations.ScoreIndex);
        }
        else
        {
            Assert.Empty(rowsDetails.Indexes);
        }

        var viewDetails = await client.GetObjectDetailsAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            view,
            cancellationToken);
        Assert.False(viewDetails.CanEdit);
        Assert.NotEmpty(viewDetails.Columns);
        Assert.Contains("read-only", viewDetails.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);

        var keylessDetails = await client.GetObjectDetailsAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            keyless,
            cancellationToken);
        Assert.False(keylessDetails.CanEdit);
        Assert.NotEmpty(keylessDetails.Columns);
        Assert.False(string.IsNullOrWhiteSpace(keylessDetails.ReadOnlyReason));
        if (provider.Expectations.CanEdit && keyless.Kind == DatabaseTableKind.Table)
        {
            Assert.Contains(
                "primary key",
                keylessDetails.ReadOnlyReason,
                StringComparison.OrdinalIgnoreCase);
        }

        var qualifiedPreview = client.BuildTablePreviewQuery(provider.Id, rows.Id, limit: 3);
        var preview = await client.QueryAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            qualifiedPreview,
            maxRows: 2,
            cancellationToken);
        Assert.Equal(2, preview.ValueRows.Count);
        Assert.True(preview.Truncated);

        var legacyPreview = client.BuildTablePreviewQuery(provider.Id, rows.Name, limit: 1);
        var legacyPage = await client.QueryAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            legacyPreview,
            maxRows: 1,
            cancellationToken);
        Assert.Single(legacyPage.ValueRows);

        var hostilePreview = client.BuildTablePreviewQuery(provider.Id, hostile.Id, limit: 1);
        var hostilePage = await client.QueryAsync(
            provider.Id,
            environment.ConnectionString,
            tunnel: null,
            hostilePreview,
            maxRows: 1,
            cancellationToken);
        Assert.Equal("42", Assert.Single(Assert.Single(hostilePage.ValueRows)).DisplayText);

        return new DatabaseObjects(rows, rowsDetails, keyless, view, hostile);
    }

    private static void AssertScoreIndex(
        DatabaseIndexSchema index,
        DatabaseIndexExpectations expectations)
    {
        Assert.False(index.IsUnique);
        Assert.False(index.IsPrimary);
        Assert.True(index.IsValid);
        var firstColumn = Assert.Single(
            index.Columns,
            column => column.Ordinal == index.Columns.Min(candidate => candidate.Ordinal));
        Assert.Equal(expectations.FirstColumnDescending, firstColumn.IsDescending);
        Assert.Contains(
            "score",
            firstColumn.Name ?? firstColumn.Expression ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        var includedColumns = index.Columns.Where(column => column.IsIncluded).ToArray();
        if (expectations.IncludedColumn is { } includedColumn)
        {
            var included = Assert.Single(includedColumns);
            Assert.Equal(includedColumn, included.Name, ignoreCase: true);
        }
        else
        {
            Assert.Empty(includedColumns);
        }

        if (expectations.PredicateFragment is { } predicateFragment)
        {
            var predicate = index.Predicate
                ?? index.Details?.GetValueOrDefault("Definition")
                ?? string.Empty;
            Assert.Contains(predicateFragment, predicate, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task AssertQueryFailureRecoveryAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => client.QueryAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            "SELECT * FROM asura_table_that_does_not_exist",
            maxRows: 1,
            cancellationToken));

        var recovered = await client.QueryAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            environment.Provider.ReadySql,
            maxRows: 1,
            cancellationToken);
        Assert.Single(recovered.ValueRows);
    }

    private static async Task AssertLiveDefaultNamespaceAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(environment.Provider.Id, "postgres", StringComparison.Ordinal))
        {
            return;
        }

        _ = await client.QueryAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            """
            CREATE SCHEMA asura_tenant;
            CREATE TABLE public.asura_namespace_probe (public_value INTEGER);
            CREATE TABLE asura_tenant.asura_namespace_probe (tenant_value INTEGER);
            """,
            maxRows: 1,
            cancellationToken);
        var tenantConnectionString = environment.ConnectionString
            + ";Search Path=asura_tenant,public";
        var catalog = await client.GetSqlCatalogAsync(
            environment.Provider.Id,
            tenantConnectionString,
            tunnel: null,
            cancellationToken);

        Assert.Equal("asura", catalog.DefaultCatalog);
        Assert.Equal("asura_tenant", catalog.DefaultSchema);
        Assert.Equal(
            2,
            catalog.Objects.Count(item => string.Equals(item.Id.Name, "asura_namespace_probe", StringComparison.Ordinal)));

        ISqlLanguageService service = new CalciteSqlLanguageService();
        if (!service.IsAvailable)
        {
            return;
        }

        await using var session = await service.OpenSessionAsync(catalog, cancellationToken);
        Assert.True(session.IsAvailable, session.UnavailableReason);
        const string completionSql =
            "SELECT p. FROM asura_namespace_probe p";
        var completion = await session.CompleteAsync(
            completionSql,
            completionSql.IndexOf('.', StringComparison.Ordinal) + 1,
            cancellationToken);
        Assert.Contains(completion.Items, item => string.Equals(item.Label, "tenant_value", StringComparison.Ordinal));
        Assert.DoesNotContain(completion.Items, item => string.Equals(item.Label, "public_value", StringComparison.Ordinal));

        var valid = await session.DiagnoseAsync(
            "SELECT p.tenant_value FROM asura_namespace_probe p",
            cancellationToken);
        Assert.DoesNotContain(valid, item => item.Severity == SqlDiagnosticSeverity.Error);
        var wrongSchema = await session.DiagnoseAsync(
            "SELECT p.public_value FROM asura_namespace_probe p",
            cancellationToken);
        Assert.Contains(wrongSchema, item =>
            item.Severity == SqlDiagnosticSeverity.Error
            && item.Message.Contains("public_value", StringComparison.OrdinalIgnoreCase));
    }

    private static DatabaseTableDescriptor FindObject(
        IReadOnlyList<DatabaseTableDescriptor> objects,
        string name,
        DatabaseTableKind kind) =>
        Assert.Single(objects, databaseObject => string.Equals(databaseObject.Name, name, StringComparison.Ordinal) && databaseObject.Kind == kind);

    private sealed record DatabaseObjects(
        DatabaseTableDescriptor Rows,
        DatabaseObjectDetails RowsDetails,
        DatabaseTableDescriptor Keyless,
        DatabaseTableDescriptor View,
        DatabaseTableDescriptor Hostile);
}
