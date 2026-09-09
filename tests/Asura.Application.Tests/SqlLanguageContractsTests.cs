using Asura.Application;

namespace Asura.Application.Tests;

public sealed class SqlLanguageContractsTests
{
    [Fact]
    public void CatalogSnapshotRetainsExactProviderMetadataWithoutAConnection()
    {
        var snapshot = new SqlCatalogSnapshot(
            "postgres",
            "warehouse",
            "Sales",
            [
                new SqlCatalogObject(
                    new DatabaseObjectId("warehouse", "Sales", "Order Items"),
                    DatabaseTableKind.Table,
                    [
                        new SqlCatalogColumn(
                            "Unit Price",
                            "numeric(12,2)",
                            DatabaseValueKind.Decimal,
                            false),
                    ]),
            ]);

        var table = Assert.Single(snapshot.Objects);
        var column = Assert.Single(table.Columns);
        Assert.Equal("postgres", snapshot.DriverId);
        Assert.Equal("Sales", snapshot.DefaultSchema);
        Assert.Equal("Order Items", table.Id.Name);
        Assert.Equal("Unit Price", column.Name);
        Assert.Equal(DatabaseValueKind.Decimal, column.ValueKind);
    }

    [Fact]
    public void CompletionResultUsesAReplacementRangeAndImmutableItemValues()
    {
        var result = new SqlCompletionResult(
            7,
            2,
            [new SqlCompletionItem(
                "people",
                SqlCompletionItemKind.Table,
                "public.people",
                "people")]);

        Assert.Equal(7, result.ReplacementStart);
        Assert.Equal(2, result.ReplacementLength);
        Assert.Equal(SqlCompletionItemKind.Table, Assert.Single(result.Items).Kind);
    }

    [Fact]
    public void CatalogSnapshotDefaultsToNoRoutinesAndRetainsCallableArity()
    {
        var empty = new SqlCatalogSnapshot("sqlite", null, "main", []);
        Assert.Empty(empty.Routines);
        Assert.Empty(empty.IntrinsicSymbols);
        Assert.Equal(SqlCatalogCoverage.None, empty.RoutineCoverage);
        Assert.Equal(SqlCatalogCoverage.None, empty.IntrinsicCoverage);

        var snapshot = empty with
        {
            Routines =
            [
                new SqlCatalogRoutine(
                    new DatabaseObjectId("app", "public", "date_add"),
                    SqlCatalogRoutineKind.Scalar,
                    "date_add(timestamptz, interval, text)",
                    [
                        new SqlCatalogRoutineParameter(
                            "value",
                            "timestamptz",
                            DatabaseValueKind.TimestampWithZone,
                            SqlCatalogRoutineParameterMode.In),
                        new SqlCatalogRoutineParameter(
                            "amount",
                            "interval",
                            DatabaseValueKind.Duration,
                            SqlCatalogRoutineParameterMode.In),
                        new SqlCatalogRoutineParameter(
                            "timezone",
                            "text",
                            DatabaseValueKind.Text,
                            SqlCatalogRoutineParameterMode.In,
                            IsOptional: true),
                    ],
                    "timestamptz",
                    DatabaseValueKind.TimestampWithZone,
                    MinimumArgumentCount: 2,
                    MaximumArgumentCount: 3),
            ],
            RoutineCoverage = SqlCatalogCoverage.Complete,
            IntrinsicSymbols =
            [
                new SqlCatalogIntrinsicSymbol(
                    "CURRENT_TIMESTAMP",
                    SqlCatalogIntrinsicKind.Keyword),
            ],
            IntrinsicCoverage = SqlCatalogCoverage.Complete,
        };

        var routine = Assert.Single(snapshot.Routines);
        Assert.Equal("date_add", routine.Id.Name);
        Assert.Equal((2, 3),
            (routine.MinimumArgumentCount, routine.MaximumArgumentCount));
        Assert.True(routine.Parameters[^1].IsOptional);
        Assert.False(routine.Parameters[^1].IsVariadic);
        Assert.Equal(SqlCatalogCoverage.Complete, snapshot.RoutineCoverage);
        Assert.Equal("CURRENT_TIMESTAMP", Assert.Single(snapshot.IntrinsicSymbols).Name);
    }
}
