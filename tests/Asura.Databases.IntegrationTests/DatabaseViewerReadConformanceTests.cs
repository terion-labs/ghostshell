using System.Globalization;
using System.Text.Json;
using Asura.Application;

namespace Asura.Databases.IntegrationTests;

public sealed partial class DatabaseViewerConformanceTests
{
    private static async Task AssertTypedBrowsingAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseObjects objects,
        CancellationToken cancellationToken)
    {
        var firstPage = await ReadRowsAsync(
            client,
            environment,
            objects.Rows,
            filters: [],
            sorts: [new DatabaseSort("id")],
            offset: 0,
            limit: 2,
            cancellationToken);
        Assert.Equal(2, firstPage.Result.ValueRows.Count);
        Assert.True(firstPage.HasMore);
        Assert.Equal(205, firstPage.TotalRows);
        Assert.Equal(["alpha", "beta"], ColumnText(firstPage.Result, "code"));

        var actualKinds = firstPage.Result.Columns
            .Select(column => column.ValueKind)
            .ToHashSet();
        var missingKinds = environment.Provider.Expectations.RequiredValueKinds
            .Where(kind => !actualKinds.Contains(kind))
            .ToArray();
        Assert.True(
            missingKinds.Length == 0,
            $"Missing expected value kinds [{string.Join(", ", missingKinds)}]. " +
            $"Materialized columns: {string.Join(", ", firstPage.Result.Columns.Select(column => $"{column.Name}={column.ValueKind}/{column.DataTypeName}"))}. " +
            $"First row: {string.Join(", ", firstPage.Result.ValueRows[0].Select((value, ordinal) => $"{firstPage.Result.Columns[ordinal].Name}={value.Kind}/{value.RawValue?.GetType().FullName ?? "NULL"}/{value.DisplayText}"))}");
        Assert.All(
            firstPage.Result.ValueRows.SelectMany(row => row),
            value => AssertDetachedValue(value.RawValue));
        AssertCanonicalAlphaValues(
            firstPage.Result,
            environment.Provider.Expectations);

        var secondPage = await ReadRowsAsync(
            client,
            environment,
            objects.Rows,
            filters: [],
            sorts: [new DatabaseSort("id")],
            offset: 2,
            limit: 2,
            cancellationToken);
        Assert.Equal(["literal", "omega-a"], ColumnText(secondPage.Result, "code"));
        Assert.Equal(205, secondPage.TotalRows);

        var duplicateSortValues = await ReadRowsAsync(
            client,
            environment,
            objects.Rows,
            filters: [new DatabaseFilterCondition(
                "score",
                DatabaseFilterOperator.LessThanOrEqual,
                300m)],
            sorts: [new DatabaseSort("score", Descending: true)],
            offset: 0,
            limit: 2,
            cancellationToken);
        Assert.Equal(["omega-a", "omega-b"], ColumnText(duplicateSortValues.Result, "code"));

        await AssertAllFiltersAsync(client, environment, objects.Rows, cancellationToken);

        var keyless = await ReadRowsAsync(
            client,
            environment,
            objects.Keyless,
            filters: [],
            sorts: [],
            offset: 0,
            limit: 200,
            cancellationToken);
        Assert.Equal(200, keyless.Result.ValueRows.Count);
        Assert.True(keyless.Result.Truncated);
        Assert.False(keyless.HasMore);
        Assert.Equal(205, keyless.TotalRows);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadRowsAsync(
            client,
            environment,
            objects.Keyless,
            filters: [],
            sorts: [],
            offset: 200,
            limit: 200,
            cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() => ReadRowsAsync(
            client,
            environment,
            objects.Rows,
            filters: [new DatabaseFilterCondition(
                "missing_column",
                DatabaseFilterOperator.Equal,
                "value")],
            sorts: [],
            offset: 0,
            limit: 10,
            cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => ReadRowsAsync(
            client,
            environment,
            objects.Rows,
            filters: [],
            sorts: [new DatabaseSort("missing_column")],
            offset: 0,
            limit: 10,
            cancellationToken));
    }

    private static async Task AssertAllFiltersAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseTableDescriptor rows,
        CancellationToken cancellationToken)
    {
        await AssertFilterCodesAsync(
            client, environment, rows,
            new DatabaseFilterCondition("code", DatabaseFilterOperator.Equal, "alpha"),
            ["alpha"], cancellationToken);

        var notEqual = await FilterAsync(
            client, environment, rows,
            new DatabaseFilterCondition("code", DatabaseFilterOperator.NotEqual, "alpha"),
            cancellationToken);
        Assert.Equal(204, notEqual.Result.ValueRows.Count);
        Assert.Equal(204, notEqual.TotalRows);
        Assert.DoesNotContain("alpha", ColumnText(notEqual.Result, "code"), StringComparer.Ordinal);

        await AssertFilterCodesAsync(
            client, environment, rows,
            new DatabaseFilterCondition("score", DatabaseFilterOperator.LessThan, 0m),
            ["alpha"], cancellationToken);
        await AssertFilterCodesAsync(
            client, environment, rows,
            new DatabaseFilterCondition("score", DatabaseFilterOperator.LessThanOrEqual, 0m),
            ["alpha", "beta"], cancellationToken);

        var greater = await FilterAsync(
            client, environment, rows,
            new DatabaseFilterCondition("score", DatabaseFilterOperator.GreaterThan, 300m),
            cancellationToken);
        Assert.Equal(200, greater.Result.ValueRows.Count);
        Assert.Equal(200, greater.TotalRows);

        var greaterOrEqual = await FilterAsync(
            client, environment, rows,
            new DatabaseFilterCondition("score", DatabaseFilterOperator.GreaterThanOrEqual, 300m),
            cancellationToken);
        Assert.Equal(202, greaterOrEqual.Result.ValueRows.Count);
        Assert.Equal(202, greaterOrEqual.TotalRows);

        await AssertFilterCodesAsync(
            client, environment, rows,
            new DatabaseFilterCondition("score", DatabaseFilterOperator.Equal, 300m),
            ["omega-a", "omega-b"], cancellationToken);

        var numericNotEqual = await FilterAsync(
            client, environment, rows,
            new DatabaseFilterCondition("score", DatabaseFilterOperator.NotEqual, 300m),
            cancellationToken);
        Assert.Equal(203, numericNotEqual.Result.ValueRows.Count);
        Assert.Equal(203, numericNotEqual.TotalRows);

        await AssertFilterCodesAsync(
            client, environment, rows,
            new DatabaseFilterCondition("title", DatabaseFilterOperator.Contains, "%_!"),
            ["literal"], cancellationToken);
        var notContains = await FilterAsync(
            client, environment, rows,
            new DatabaseFilterCondition(
                "title",
                DatabaseFilterOperator.NotContains,
                "%_!"),
            cancellationToken);
        Assert.Equal(204, notContains.Result.ValueRows.Count);
        Assert.Equal(204, notContains.TotalRows);
        Assert.DoesNotContain("literal", ColumnText(notContains.Result, "code"), StringComparer.Ordinal);
        await AssertFilterCodesAsync(
            client, environment, rows,
            new DatabaseFilterCondition("title", DatabaseFilterOperator.StartsWith, "literal%_!"),
            ["literal"], cancellationToken);
        await AssertFilterCodesAsync(
            client, environment, rows,
            new DatabaseFilterCondition("title", DatabaseFilterOperator.EndsWith, "%_!needle"),
            ["literal"], cancellationToken);

        await AssertFilterCodesAsync(
            client, environment, rows,
            new DatabaseFilterCondition(
                "code",
                DatabaseFilterOperator.In,
                new[] { "alpha" }),
            ["alpha"], cancellationToken);
        await AssertFilterCodesAsync(
            client, environment, rows,
            new DatabaseFilterCondition(
                "code",
                DatabaseFilterOperator.In,
                new[] { "alpha", "omega-b" }),
            ["alpha", "omega-b"], cancellationToken);

        var notIn = await FilterAsync(
            client, environment, rows,
            new DatabaseFilterCondition(
                "code",
                DatabaseFilterOperator.NotIn,
                new[] { "alpha", "beta" }),
            cancellationToken);
        Assert.Equal(203, notIn.Result.ValueRows.Count);
        Assert.Equal(203, notIn.TotalRows);
        var notInCodes = ColumnText(notIn.Result, "code");
        Assert.DoesNotContain("alpha", notInCodes, StringComparer.Ordinal);
        Assert.DoesNotContain("beta", notInCodes, StringComparer.Ordinal);

        var isNull = await FilterAsync(
            client, environment, rows,
            new DatabaseFilterCondition("note", DatabaseFilterOperator.IsNull),
            cancellationToken);
        Assert.Equal(68, isNull.Result.ValueRows.Count);
        Assert.Equal(68, isNull.TotalRows);
        Assert.Contains("literal", ColumnText(isNull.Result, "code"), StringComparer.Ordinal);
        var noteOrdinal = FindColumn(isNull.Result, "note");
        Assert.All(isNull.Result.ValueRows, row => Assert.True(row[noteOrdinal].IsNull));

        var isNotNull = await FilterAsync(
            client, environment, rows,
            new DatabaseFilterCondition("note", DatabaseFilterOperator.IsNotNull),
            cancellationToken);
        Assert.Equal(137, isNotNull.Result.ValueRows.Count);
        Assert.Equal(137, isNotNull.TotalRows);
        Assert.Contains("alpha", ColumnText(isNotNull.Result, "code"), StringComparer.Ordinal);
        noteOrdinal = FindColumn(isNotNull.Result, "note");
        Assert.All(isNotNull.Result.ValueRows, row => Assert.False(row[noteOrdinal].IsNull));

        await AssertFilterCodesAsync(
            client, environment, rows,
            new DatabaseFilterCondition(
                "note",
                DatabaseFilterOperator.Equal,
                "Robert'); DROP TABLE viewer_rows;--"),
            ["beta"], cancellationToken);

        var combined = await ReadRowsAsync(
            client,
            environment,
            rows,
            filters:
            [
                new DatabaseFilterCondition("code", DatabaseFilterOperator.Equal, "beta"),
                new DatabaseFilterCondition("enabled", DatabaseFilterOperator.Equal, false),
            ],
            sorts: [new DatabaseSort("id")],
            offset: 0,
            limit: 500,
            cancellationToken);
        Assert.Equal(["beta"], ColumnText(combined.Result, "code"));

        var objectsAfterInjection = await client.ListTablesAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            cancellationToken);
        Assert.Contains(objectsAfterInjection, item => string.Equals(item.Name, environment.Provider.Seed.RowsTable, StringComparison.Ordinal));
    }

    private static async Task AssertFilterCodesAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseTableDescriptor rows,
        DatabaseFilterCondition filter,
        IReadOnlyList<string> expected,
        CancellationToken cancellationToken)
    {
        var page = await FilterAsync(client, environment, rows, filter, cancellationToken);
        Assert.Equal(expected, ColumnText(page.Result, "code"));
        Assert.Equal(expected.Count, page.TotalRows);
    }

    private static Task<DatabaseTablePage> FilterAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseTableDescriptor rows,
        DatabaseFilterCondition filter,
        CancellationToken cancellationToken) =>
        ReadRowsAsync(
            client,
            environment,
            rows,
            filters: [filter],
            sorts: [new DatabaseSort("id")],
            offset: 0,
            limit: 500,
            cancellationToken);

    private static Task<DatabaseTablePage> ReadRowsAsync(
        DatabasePanelClient client,
        DatabaseTestEnvironment environment,
        DatabaseTableDescriptor table,
        IReadOnlyList<DatabaseFilterCondition> filters,
        IReadOnlyList<DatabaseSort> sorts,
        int offset,
        int limit,
        CancellationToken cancellationToken) =>
        client.ReadTableAsync(
            environment.Provider.Id,
            environment.ConnectionString,
            tunnel: null,
            table,
            new DatabaseTableQuery(filters, sorts, offset, limit),
            cancellationToken);

    private static IReadOnlyList<string> ColumnText(
        DatabaseQueryPage page,
        string columnName)
    {
        var ordinal = FindColumn(page, columnName);
        return [.. page.ValueRows.Select(row => row[ordinal].DisplayText)];
    }

    private static int FindColumn(DatabaseQueryPage page, string columnName) =>
        page.Columns
            .Select((column, ordinal) => (column, ordinal))
            .Single(pair => string.Equals(pair.column.Name, columnName, StringComparison.Ordinal))
            .ordinal;

    private static void AssertCanonicalAlphaValues(
        DatabaseQueryPage page,
        DatabaseProviderExpectations expectations)
    {
        AssertBooleanValue(page, "enabled", expected: true);
        AssertDecimalValue(page, "score", expected: -100m);

        if (expectations.RequiredValueKinds.Contains(DatabaseValueKind.TimestampWithZone))
        {
            AssertTimestampValue(
                page,
                "created_at",
                DatabaseValueKind.TimestampWithZone,
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }
        else if (expectations.RequiredValueKinds.Contains(DatabaseValueKind.Timestamp))
        {
            AssertTimestampValue(
                page,
                "created_at",
                DatabaseValueKind.Timestamp,
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }

        if (expectations.RequiredValueKinds.Contains(DatabaseValueKind.Binary))
        {
            AssertBinaryValue(page, "blob_value", [0x01, 0x02]);
        }

        if (expectations.RequiredValueKinds.Contains(DatabaseValueKind.Json))
        {
            AssertJsonValue(page, "payload", expectedSlot: 1);
        }
    }

    private static void AssertBooleanValue(
        DatabaseQueryPage page,
        string columnName,
        bool expected)
    {
        var value = CanonicalValue(page, columnName, DatabaseValueKind.Boolean);
        Assert.True(
            value.RawValue is bool
                or byte or sbyte
                or short or ushort
                or int or uint
                or long or ulong,
            $"{columnName} returned unexpected CLR type {value.RawValue?.GetType().FullName ?? "NULL"}.");
        Assert.Equal(expected, Convert.ToInt64(value.RawValue, CultureInfo.InvariantCulture) != 0);

        var displayed = bool.TryParse(value.DisplayText, out var flag)
            ? flag
            : long.TryParse(
                value.DisplayText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var integer)
                ? integer != 0
                : (bool?)null;
        Assert.Equal(expected, displayed);
    }

    private static void AssertDecimalValue(
        DatabaseQueryPage page,
        string columnName,
        decimal expected)
    {
        var value = CanonicalValue(page, columnName, DatabaseValueKind.Decimal);
        Assert.NotNull(value.RawValue);
        Assert.IsAssignableFrom<IConvertible>(value.RawValue);
        Assert.False(value.RawValue is string);
        Assert.Equal(expected, Convert.ToDecimal(value.RawValue, CultureInfo.InvariantCulture));
        Assert.True(decimal.TryParse(
            value.DisplayText,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var displayed));
        Assert.Equal(expected, displayed);
    }

    private static void AssertTimestampValue(
        DatabaseQueryPage page,
        string columnName,
        DatabaseValueKind expectedKind,
        DateTimeOffset expected)
    {
        var value = CanonicalValue(page, columnName, expectedKind);
        Assert.True(
            value.RawValue is DateTimeOffset or DateTime or string,
            $"{columnName} returned unexpected CLR type {value.RawValue?.GetType().FullName ?? "NULL"}.");
        var instant = value.RawValue switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime when dateTime.Kind == DateTimeKind.Unspecified =>
                new DateTimeOffset(dateTime, TimeSpan.Zero),
            DateTime dateTime => new DateTimeOffset(dateTime).ToUniversalTime(),
            string text when DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) => parsed,
            _ => throw new InvalidOperationException("Timestamp CLR type was asserted above."),
        };
        Assert.Equal(expected, instant);

        Assert.True(DateTimeOffset.TryParse(
            value.DisplayText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var displayed));
        Assert.Equal(expected, displayed);
    }

    private static void AssertBinaryValue(
        DatabaseQueryPage page,
        string columnName,
        byte[] expected)
    {
        var value = CanonicalValue(page, columnName, DatabaseValueKind.Binary);
        var bytes = Assert.IsType<byte[]>(value.RawValue);
        Assert.Equal(expected, bytes);
        Assert.Equal("0x0102", value.DisplayText);
    }

    private static void AssertJsonValue(
        DatabaseQueryPage page,
        string columnName,
        int expectedSlot)
    {
        var value = CanonicalValue(page, columnName, DatabaseValueKind.Json);
        Assert.True(
            value.RawValue is string or JsonElement,
            $"{columnName} returned unexpected CLR type {value.RawValue?.GetType().FullName ?? "NULL"}.");
        var rawJson = value.RawValue switch
        {
            string text => text,
            JsonElement element => element.GetRawText(),
            _ => throw new InvalidOperationException("JSON CLR type was asserted above."),
        };

        AssertJsonSlot(rawJson, expectedSlot);
        AssertJsonSlot(value.DisplayText, expectedSlot);
    }

    private static DatabaseValue CanonicalValue(
        DatabaseQueryPage page,
        string columnName,
        DatabaseValueKind expectedKind)
    {
        var ordinal = FindColumn(page, columnName);
        Assert.Equal(expectedKind, page.Columns[ordinal].ValueKind);
        var value = page.ValueRows[0][ordinal];
        Assert.Equal(expectedKind, value.Kind);
        Assert.False(value.IsNull);
        Assert.False(value.IsTruncated);
        return value;
    }

    private static void AssertJsonSlot(string json, int expectedSlot)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expectedSlot, document.RootElement.GetProperty("slot").GetInt32());
    }

    private static void AssertDetachedValue(object? value)
    {
        if (value is null)
        {
            return;
        }

        Assert.False(value is System.Data.Common.DbDataReader);
        Assert.False(value is Stream);
        Assert.False(value is TextReader);
    }
}
