using Asura.Application;

namespace Asura.Databases.IntegrationTests;

public sealed partial class DatabaseViewerConformanceTests
{
    private static async Task AssertMutationsAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        CancellationToken cancellationToken)
    {
        var empty = await client.ApplyTableChangesAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            objects.Rows,
            new DatabaseTableChanges([], [], []),
            cancellationToken);
        Assert.Equal(0, empty.TotalAffected);

        var rejectedChange = new DatabaseTableChanges(
            [new DatabaseInsertedRow([])],
            [],
            []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyTableChangesAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            objects.View,
            rejectedChange,
            cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyTableChangesAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            objects.Keyless,
            rejectedChange,
            cancellationToken));

        if (!environment.Provider.Expectations.CanEdit)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyTableChangesAsync(
                environment.Provider.Id,
                environment.ConnectionString,
                tunnel: null,
                objects.Rows,
                rejectedChange,
                cancellationToken));
            return;
        }

        if (environment.Provider.Id is "mysql" or "mariadb")
        {
            await AssertMySqlNonTransactionalTableIsReadOnlyAsync(
                client,
                environment,
                cancellationToken);
        }

        await AssertCopiedInsertExecutesAsync(
            client,
            environment,
            objects,
            cancellationToken);

        var token = Guid.NewGuid().ToString("N");
        var code = $"mutation-{token}";
        var insert = NewRow(code, "Mutation row");
        var inserted = await ApplyAsync(
            client,
            environment,
            objects.Rows,
            new DatabaseTableChanges([insert], [], []),
            cancellationToken);
        Assert.Equal((1, 0, 0), (inserted.Inserted, inserted.Updated, inserted.Deleted));

        var loaded = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            code,
            cancellationToken);
        Assert.True(Value(loaded, "note").IsNull);
        Assert.Equal("draft", Value(loaded, "status").DisplayText);

        var firstUpdate = BuildUpdate(
            objects.RowsDetails,
            loaded,
            "title",
            "Mutation row updated");
        var updated = await ApplyAsync(
            client,
            environment,
            objects.Rows,
            new DatabaseTableChanges([], [firstUpdate], []),
            cancellationToken);
        Assert.Equal(1, updated.Updated);

        var staleSnapshot = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            code,
            cancellationToken);
        var winnerUpdate = BuildUpdate(
            objects.RowsDetails,
            staleSnapshot,
            "title",
            "Winner");
        Assert.Equal(
            1,
            (await ApplyAsync(
                client,
                environment,
                objects.Rows,
                new DatabaseTableChanges([], [winnerUpdate], []),
                cancellationToken)).Updated);

        var staleUpdate = BuildUpdate(
            objects.RowsDetails,
            staleSnapshot,
            "title",
            "Stale loser");
        var conflict = await ApplyAsync(
            client,
            environment,
            objects.Rows,
            new DatabaseTableChanges([], [staleUpdate], []),
            cancellationToken);
        Assert.True(conflict.HasConflict);
        Assert.Equal(0, conflict.TotalAffected);

        var rolledBackCode = $"rolled-back-{token}";
        var rolledBack = await ApplyAsync(
            client,
            environment,
            objects.Rows,
            new DatabaseTableChanges(
                [NewRow(rolledBackCode, "Must roll back")],
                [staleUpdate],
                []),
            cancellationToken);
        Assert.True(rolledBack.HasConflict);
        Assert.Empty((await LoadRowsByCodeAsync(
            client,
            environment,
            objects.Rows,
            rolledBackCode,
            cancellationToken)).ValueRows);

        var winner = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            code,
            cancellationToken);
        Assert.Equal("Winner", Value(winner, "title").DisplayText);
        var deleted = await ApplyAsync(
            client,
            environment,
            objects.Rows,
            new DatabaseTableChanges(
                [],
                [],
                [BuildDelete(objects.RowsDetails, winner)]),
            cancellationToken);
        Assert.Equal(1, deleted.Deleted);
        Assert.Empty((await LoadRowsByCodeAsync(
            client,
            environment,
            objects.Rows,
            code,
            cancellationToken)).ValueRows);

        await AssertInvalidMutationsAreRejectedAsync(
            client,
            environment,
            objects,
            cancellationToken);
    }

    private static async Task AssertCopiedInsertExecutesAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var code = $"insert-copy-{token}";
        var title = "Copied O'Hara\\path 🧪";
        var statement = client.BuildInsertStatement(
            environment.Provider.Id,
            objects.RowsDetails,
            new DatabaseInsertedRow(
            [
                new DatabaseColumnEdit("code", DatabaseEditValueState.Value, code),
                new DatabaseColumnEdit("title", DatabaseEditValueState.Value, title),
                new DatabaseColumnEdit("score", DatabaseEditValueState.Value, 72.50m),
                new DatabaseColumnEdit("enabled", DatabaseEditValueState.Value, true),
                new DatabaseColumnEdit("note", DatabaseEditValueState.Value, "line one\nline 'two'\\tail"),
                new DatabaseColumnEdit("status", DatabaseEditValueState.Default),
                new DatabaseColumnEdit("payload", DatabaseEditValueState.Value, "{\"copied\":true}"),
                new DatabaseColumnEdit(
                    "blob_value",
                    DatabaseEditValueState.Value,
                    new byte[] { 0, 1, 127, 255 }),
            ]));

        Assert.EndsWith(";", statement, StringComparison.Ordinal);
        await client.QueryAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            statement,
            maxRows: 1,
            cancellationToken);

        var loaded = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            code,
            cancellationToken);
        Assert.Equal(title, Value(loaded, "title").RawValue);
        Assert.Equal(new byte[] { 0, 1, 127, 255 }, Assert.IsType<byte[]>(
            Value(loaded, "blob_value").RawValue));

        var deleted = await ApplyAsync(
            client,
            environment,
            objects.Rows,
            new DatabaseTableChanges(
                [],
                [],
                [BuildDelete(objects.RowsDetails, loaded)]),
            cancellationToken);
        Assert.Equal(1, deleted.Deleted);
    }

    private static async Task AssertInvalidMutationsAreRejectedAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        CancellationToken cancellationToken)
    {
        var alpha = await LoadSingleRowAsync(
            client,
            environment,
            objects.Rows,
            "alpha",
            cancellationToken);
        var keys = BuildKeys(objects.RowsDetails, alpha);
        var titleOriginal = Original(alpha, "title");

        if (environment.Provider.Expectations.HasIdentity)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => ApplyAsync(
                client,
                environment,
                objects.Rows,
                new DatabaseTableChanges(
                    [new DatabaseInsertedRow(
                    [
                        new DatabaseColumnEdit("id", DatabaseEditValueState.Value, 999999),
                    ])],
                    [],
                    []),
                cancellationToken));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => ApplyAsync(
            client,
            environment,
            objects.Rows,
            new DatabaseTableChanges(
                [],
                [new DatabaseUpdatedRow(
                    keys,
                    [new DatabaseColumnEdit("title", DatabaseEditValueState.Null)],
                    [titleOriginal])],
                []),
            cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() => ApplyAsync(
            client,
            environment,
            objects.Rows,
            new DatabaseTableChanges(
                [],
                [new DatabaseUpdatedRow(
                    keys,
                    [new DatabaseColumnEdit("title", DatabaseEditValueState.Default)],
                    [titleOriginal])],
                []),
            cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() => ApplyAsync(
            client,
            environment,
            objects.Rows,
            new DatabaseTableChanges(
                [],
                [new DatabaseUpdatedRow(
                    keys,
                    [new DatabaseColumnEdit(
                        "computed_label",
                        DatabaseEditValueState.Value,
                        "forbidden")],
                    [titleOriginal])],
                []),
            cancellationToken));
    }

    private static async Task AssertMySqlNonTransactionalTableIsReadOnlyAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        CancellationToken cancellationToken)
    {
        var tables = await client.ListTablesAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            cancellationToken);
        var table = FindObject(tables, "viewer_nontransactional", DatabaseTableKind.Table);
        var details = await client.GetObjectDetailsAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            table,
            cancellationToken);
        Assert.False(details.CanEdit);
        Assert.Contains("InnoDB", details.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyTableChangesAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableChanges([new DatabaseInsertedRow([])], [], []),
            cancellationToken));
    }

    private static DatabaseInsertedRow NewRow(string code, string title) => new(
    [
        new DatabaseColumnEdit("code", DatabaseEditValueState.Value, code),
        new DatabaseColumnEdit("title", DatabaseEditValueState.Value, title),
        new DatabaseColumnEdit("score", DatabaseEditValueState.Value, 41.25m),
        new DatabaseColumnEdit("enabled", DatabaseEditValueState.Value, true),
        new DatabaseColumnEdit("note", DatabaseEditValueState.Null),
        new DatabaseColumnEdit("status", DatabaseEditValueState.Default),
    ]);

    private static DatabaseUpdatedRow BuildUpdate(
        DatabaseObjectDetails details,
        LoadedRow row,
        string columnName,
        object value) => new(
        BuildKeys(details, row),
        [new DatabaseColumnEdit(columnName, DatabaseEditValueState.Value, value)],
        [Original(row, columnName)]);

    private static DatabaseDeletedRow BuildDelete(
        DatabaseObjectDetails details,
        LoadedRow row) => new(
        BuildKeys(details, row),
        [Original(row, "title")]);

    private static IReadOnlyList<DatabaseColumnEdit> BuildKeys(
        DatabaseObjectDetails details,
        LoadedRow row) =>
        [.. details.PrimaryKey.Select(column => FromValue(column.Name, Value(row, column.Name)))];

    private static DatabaseColumnEdit Original(LoadedRow row, string columnName) =>
        FromValue(columnName, Value(row, columnName));

    private static DatabaseColumnEdit FromValue(string columnName, DatabaseValue value) =>
        value.IsNull
            ? new DatabaseColumnEdit(columnName, DatabaseEditValueState.Null)
            : new DatabaseColumnEdit(
                columnName,
                DatabaseEditValueState.Value,
                value.RawValue);

    private static DatabaseValue Value(LoadedRow row, string columnName)
    {
        var ordinal = FindColumn(row.Page, columnName);
        return row.Values[ordinal];
    }

    private static async Task<LoadedRow> LoadSingleRowAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseTableDescriptor table,
        string code,
        CancellationToken cancellationToken)
    {
        var page = await LoadRowsByCodeAsync(
            client,
            environment,
            table,
            code,
            cancellationToken);
        return new LoadedRow(page, Assert.Single(page.ValueRows));
    }

    private static async Task<DatabaseQueryPage> LoadRowsByCodeAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseTableDescriptor table,
        string code,
        CancellationToken cancellationToken)
    {
        var page = await ReadRowsAsync(
            client,
            environment,
            table,
            filters: [new DatabaseFilterCondition("code", DatabaseFilterOperator.Equal, code)],
            sorts: [],
            offset: 0,
            limit: 10,
            cancellationToken);
        return page.Result;
    }

    private static Task<DatabaseMutationResult> ApplyAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseTableDescriptor table,
        DatabaseTableChanges changes,
        CancellationToken cancellationToken) =>
        client.ApplyTableChangesAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            table,
            changes,
            cancellationToken);

    private sealed record LoadedRow(
        DatabaseQueryPage Page,
        IReadOnlyList<DatabaseValue> Values);
}
