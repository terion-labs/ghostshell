using Asura.Databases;

namespace Asura.Databases.Tests;

public sealed class ConnectionStringValidationTests
{
    [Theory]
    [InlineData("sqlite", "Data Source=/nonexistent/asura-export.db;Mode=ReadOnly")]
    [InlineData("postgres", "Host=fixture.invalid;Database=app;SSL Mode=Require")]
    [InlineData("mysql", "Server=fixture.invalid;Database=app;SslMode=Required")]
    [InlineData("mariadb", "Server=fixture.invalid;Database=app")]
    [InlineData("sqlserver", "Server=fixture.invalid;Database=app;Integrated Security=true")]
    [InlineData("cockroach", "Host=fixture.invalid;Database=app")]
    [InlineData("redshift", "Host=fixture.invalid;Database=app")]
    [InlineData("duckdb", "Data Source=/nonexistent/asura-export.duckdb")]
    [InlineData("oracle", "Data Source=fixture.invalid:1521/service;User Id=fixture")]
    [InlineData("firebird", "DataSource=fixture.invalid;Database=app")]
    [InlineData("clickhouse", "Host=fixture.invalid;Database=app")]
    [InlineData("postgres", "postgresql://fixture:password@fixture.invalid/app?sslmode=require")]
    [InlineData("mysql", "mysql://fixture:password@fixture.invalid/app")]
    public async Task Supported_provider_options_validate_without_opening_connections(string driver, string target)
    {
        await using var client = new DatabasePanelClient();
        Assert.True(client.IsConnectionStringValid(driver, target));
        Assert.False(File.Exists("/nonexistent/asura-export.db"));
        Assert.False(File.Exists("/nonexistent/asura-export.duckdb"));
    }

    [Theory]
    [InlineData("unknown", "Host=fixture.invalid")]
    [InlineData("postgres", "Host=fixture.invalid;UnknownCredential=fixture")]
    [InlineData("postgres", "postgresql://fixture.invalid/app?unknowncredential=fixture")]
    [InlineData("postgres", "Host='unterminated")]
    public async Task Unsupported_or_malformed_options_fail_without_changing_permissive_editor_parsing(string driver, string target)
    {
        await using var client = new DatabasePanelClient();
        Assert.False(client.IsConnectionStringValid(driver, target));
        if (driver != "unknown")
        {
            Assert.NotNull(client.ParseConnectionDetails(driver, target));
        }
    }
}
