using System.Data.Common;
using System.Text;
using GhostShell.Application;
using GhostShell.ConnectionBackend;
using GhostShell.Databases;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class DatabaseProviderDiagnosticTests
{
    private const string Canary = "private+/fixture";

    [Theory]
    [InlineData("sqlite", "Data Source=/nonexistent/fixture.db;Password=private+/fixture")]
    [InlineData("postgres", "Host=fixture.invalid;Password=private+/fixture")]
    [InlineData("postgres", "Host=fixture.invalid;SSL Password=private+/fixture")]
    [InlineData("postgres", "postgresql://user:private%2B%2Ffixture@fixture.invalid/app")]
    [InlineData("postgres", "postgresql://fixture.invalid/app?%73slpassword=private%2B%2Ffixture")]
    [InlineData("mysql", "Server=fixture.invalid;Password=private+/fixture")]
    [InlineData("mysql", "mysql://user:private%2B%2Ffixture@fixture.invalid/app")]
    [InlineData("mariadb", "Server=fixture.invalid;Password=private+/fixture")]
    [InlineData("sqlserver", "Server=fixture.invalid;Pwd=private+/fixture")]
    [InlineData("cockroach", "Host=fixture.invalid;Password=private+/fixture")]
    [InlineData("redshift", "Host=fixture.invalid;Password=private+/fixture")]
    [InlineData("oracle", "Data Source=fixture.invalid/service;Password=private+/fixture")]
    [InlineData("firebird", "DataSource=fixture.invalid;Database=app;Password=private+/fixture")]
    [InlineData("clickhouse", "Host=fixture.invalid;Password=private+/fixture")]
    public async Task KnownConnectionSecretsAreRemovedButQueryFeedbackRemains(string driver, string connection)
    {
        await using var catalog = new DatabasePanelClient();
        var encoded = Uri.EscapeDataString(Canary).ToLowerInvariant();
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Canary));
        var failure = new ReportedError($"unknown column missing_field; {connection}; {Canary}; {encoded}; {base64}", "42703");
        var diagnostic = DatabaseProviderDiagnostic.Create(failure, new(driver, connection), catalog);
        Assert.Contains("unknown column missing_field", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(encoded, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(base64, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(connection, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("42703", diagnostic.SqlState);
        Assert.Contains("may have completed", diagnostic.DisplayMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileDriverSyntaxFeedbackRemainsUsefulWithoutEmbeddingItsConnectionString()
    {
        await using var catalog = new DatabasePanelClient();
        const string connection = "Data Source=/nonexistent/fixture.duckdb";
        var diagnostic = DatabaseProviderDiagnostic.Create(new ReportedError("syntax error at SELECT; " + connection, "42601"),
            new("duckdb", connection), catalog);
        Assert.Contains("syntax error at SELECT", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(connection, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownSyntaxAndOversizedTextHaveExplicitOmissionWithoutRawFallback()
    {
        await using var catalog = new DatabasePanelClient();
        var unknown = DatabaseProviderDiagnostic.Create(new ReportedError(Canary, "42601"), new("unknown", Canary), catalog);
        Assert.Contains("omitted", unknown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, unknown.Message, StringComparison.Ordinal);
        Assert.Equal("42601", unknown.SqlState);
        var oversized = DatabaseProviderDiagnostic.Create(new ReportedError(new string('x', 9000), "42601"),
            new("sqlite", "Data Source=/nonexistent/fixture.db"), catalog);
        Assert.Contains("exceeds 8 KiB", oversized.Message, StringComparison.Ordinal);
        Assert.Equal("42601", oversized.SqlState);
    }

    [Fact]
    public async Task PostgreSqlPositionAndMessageTextRemainSeparateFromConnectionDetails()
    {
        await using var catalog = new DatabasePanelClient();
        var error = new Npgsql.PostgresException("syntax error at SELECT", "ERROR", "ERROR", "42601",
            position: 17);
        var diagnostic = DatabaseProviderDiagnostic.Create(error, new("postgres", "Host=fixture.invalid;Password=" + Canary), catalog);
        Assert.Equal(17, diagnostic.Position);
        Assert.Equal("42601", diagnostic.SqlState);
        Assert.Equal("syntax error at SELECT", diagnostic.Message);
    }

    private sealed class ReportedError(string message, string state) : DbException(message)
    {
        public override string? SqlState => state;
    }
}
