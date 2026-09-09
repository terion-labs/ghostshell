namespace Asura.Databases.IntegrationTests;

internal static class DatabaseProviderCatalog
{
    public static IReadOnlyList<DatabaseProviderCase> All { get; } =
    [
        FileDatabaseProviderCases.Sqlite,
        FileDatabaseProviderCases.DuckDb,
        PostgreSqlProviderCases.PostgreSql,
        PostgreSqlProviderCases.CockroachDb,
        PostgreSqlProviderCases.RedshiftProtocol,
        MySqlProviderCases.MySql,
        MySqlProviderCases.MariaDb,
        SqlServerProviderCase.Definition,
        OracleFirebirdProviderCases.Oracle,
        OracleFirebirdProviderCases.Firebird,
        ClickHouseProviderCase.Definition,
    ];

    public static DatabaseProviderCase Get(string id) => All.Single(provider =>
        string.Equals(provider.Id, id, StringComparison.Ordinal));
}
