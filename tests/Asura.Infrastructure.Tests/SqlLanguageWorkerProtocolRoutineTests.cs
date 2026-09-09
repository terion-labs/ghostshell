using Asura.Application;
using Asura.Infrastructure;

namespace Asura.Infrastructure.Tests;

public sealed class SqlLanguageWorkerProtocolRoutineTests
{
    [Fact]
    public void CatalogMapsRoutineIdentityTypesArityAndParameterFacts()
    {
        var snapshot = new SqlCatalogSnapshot("postgres", "app", "public", [])
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
                            "timezone",
                            "text",
                            DatabaseValueKind.Text,
                            SqlCatalogRoutineParameterMode.In,
                            IsOptional: true,
                            IsVariadic: false),
                    ],
                    "timestamptz",
                    DatabaseValueKind.TimestampWithZone,
                    MinimumArgumentCount: 1,
                    MaximumArgumentCount: 2),
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

        var catalog = SqlLanguageWorkerProtocol.Catalog(snapshot);

        var routine = Assert.Single(catalog.Routines);
        Assert.Equal("app", routine.Id.Catalog);
        Assert.Equal("public", routine.Id.Schema);
        Assert.Equal("date_add", routine.Id.Name);
        Assert.Equal("scalar", routine.Kind);
        Assert.Equal("timestampwithzone", routine.ReturnValueKind);
        Assert.Equal(1, routine.MinimumArgumentCount);
        Assert.Equal(2, routine.MaximumArgumentCount);
        var optional = routine.Parameters[^1];
        Assert.Equal("text", optional.ValueKind);
        Assert.Equal("in", optional.Mode);
        Assert.True(optional.IsOptional);
        Assert.False(optional.IsVariadic);
        Assert.Equal("complete", catalog.RoutineCoverage);
        Assert.Equal("complete", catalog.IntrinsicCoverage);
        var intrinsic = Assert.Single(catalog.IntrinsicSymbols);
        Assert.Equal("CURRENT_TIMESTAMP", intrinsic.Name);
        Assert.Equal("keyword", intrinsic.Kind);

        var bytes = SqlLanguageWorkerProtocol.Serialize(new WorkerRequestEnvelope(
            SqlLanguageWorkerProtocol.Version,
            1,
            "initialize",
            new WorkerRequestParameters(Catalog: catalog)));
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"routines\"", json, StringComparison.Ordinal);
        Assert.Contains("\"minimumArgumentCount\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"isOptional\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"routineCoverage\":\"complete\"", json, StringComparison.Ordinal);
        Assert.Contains("\"intrinsicSymbols\"", json, StringComparison.Ordinal);
    }
}
