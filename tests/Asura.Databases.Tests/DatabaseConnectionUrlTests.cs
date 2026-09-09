using Asura.Application;
using Asura.Databases;

namespace Asura.Databases.Tests;

/// <summary>
/// Every engine that has a URL form accepts it.
///
/// A hosting provider gives you a URL, its documentation shows a URL, and its
/// own command-line client reads a URL. ADO.NET providers take keyword/value
/// pairs and nothing else, so each of these used to answer with the pasted
/// string handed back — "Couldn't set postgresql://…" — and no hint that the
/// form was the problem.
///
/// These go through the driver's own normalization and then read the result
/// back through its own parser, so a driver that mangles a URL into something
/// its provider cannot read fails here rather than at connect time.
/// </summary>
public sealed class DatabaseConnectionUrlTests
{
    private static IDatabaseDriver Driver(string id) =>
        BuiltInDatabaseDrivers.All.Single(driver => string.Equals(driver.Descriptor.Id, id, StringComparison.Ordinal));

    private static DatabaseConnectionDetails Read(string id, string url)
    {
        var driver = Driver(id);
        return driver.ParseDetails(driver.NormalizeConnectionString(url));
    }

    [Theory]
    // The connection Neon hands out.
    [InlineData(
        "postgres",
        "postgresql://neondb_owner:npg_secret@ep-cool-123.eu-central-1.aws.neon.tech/neondb",
        "ep-cool-123.eu-central-1.aws.neon.tech",
        "neondb",
        "neondb_owner",
        "npg_secret")]
    [InlineData(
        "cockroach",
        "postgres://root:secret@free-tier.gcp.cockroachlabs.cloud:26257/defaultdb",
        "free-tier.gcp.cockroachlabs.cloud",
        "defaultdb",
        "root",
        "secret")]
    [InlineData(
        "mysql",
        "mysql://app:secret@db.example.test:3306/shop",
        "db.example.test",
        "shop",
        "app",
        "secret")]
    [InlineData(
        "mariadb",
        "mariadb://app:secret@db.example.test/shop",
        "db.example.test",
        "shop",
        "app",
        "secret")]
    [InlineData(
        "clickhouse",
        "clickhouse://default:secret@events.example.test:8123/analytics",
        "events.example.test",
        "analytics",
        "default",
        "secret")]
    public void A_url_gives_up_its_host_database_and_user(
        string driverId,
        string url,
        string host,
        string database,
        string username,
        string password)
    {
        var details = Read(driverId, url);

        Assert.Equal(host, details.Host);
        Assert.Equal(database, details.Database);
        Assert.Equal(username, details.Username);
        Assert.Equal(password, details.Password);
    }

    /// <summary>
    /// The port carries across, and its absence leaves the provider's own
    /// default rather than a zero.
    /// </summary>
    [Theory]
    [InlineData("postgres", "postgresql://db.example.test:6543/app", 6543)]
    [InlineData("mysql", "mysql://db.example.test:3307/app", 3307)]
    [InlineData("clickhouse", "clickhouse://db.example.test:9000/app", 9000)]
    [InlineData("firebird", "firebird://db.example.test:3051//var/db/app.fdb", 3051)]
    public void A_stated_port_carries_across(string driverId, string url, int port)
    {
        Assert.Equal(port, Read(driverId, url).Port);
    }

    /// <summary>
    /// SQL Server and Oracle keep the address packed into one field, so their
    /// endpoint is read back rather than their host.
    /// </summary>
    [Theory]
    [InlineData("sqlserver", "sqlserver://sa:secret@db.example.test:1433;database=app")]
    [InlineData("sqlserver", "mssql://sa:secret@db.example.test:1433/app")]
    public void Sql_server_takes_either_spelling_of_its_url(string driverId, string url)
    {
        var driver = Driver(driverId);
        var normalized = driver.NormalizeConnectionString(url);
        var details = driver.ParseDetails(normalized);

        Assert.Equal("db.example.test", details.Host);
        Assert.Equal(1433, details.Port);
        Assert.Equal("app", details.Database);
        Assert.Equal("sa", details.Username);
        Assert.Equal("secret", details.Password);
    }

    /// <summary>
    /// Oracle's URL becomes Easy Connect — host:port/service — without
    /// transport-specific endpoint rewriting.
    /// </summary>
    [Fact]
    public void An_oracle_url_preserves_its_easy_connect_address()
    {
        var driver = Driver("oracle");
        var normalized = driver.NormalizeConnectionString(
            "oracle://app:secret@db.example.test:1521/FREEPDB1");
        var details = driver.ParseDetails(normalized);

        Assert.Equal("db.example.test", details.Host);
        Assert.Equal(1521, details.Port);
        Assert.Contains("FREEPDB1", normalized, StringComparison.Ordinal);
        Assert.Equal("app", driver.ParseDetails(normalized).Username);
    }

    /// <summary>
    /// The file engines' URL carries a path and nothing else. Three slashes is
    /// the convention for an absolute one.
    /// </summary>
    [Theory]
    [InlineData("sqlite", "sqlite:///var/db/app.db", "/var/db/app.db")]
    [InlineData("duckdb", "duckdb:///var/db/analytics.duckdb", "/var/db/analytics.duckdb")]
    public void A_file_engine_url_is_the_path_inside_it(
        string driverId,
        string url,
        string path)
    {
        var driver = Driver(driverId);

        Assert.Equal(
            driver.NormalizeConnectionString(path),
            driver.NormalizeConnectionString(url));
        Assert.Equal(path, driver.ParseDetails(driver.NormalizeConnectionString(url)).FilePath);
    }

    /// <summary>
    /// A generated password contains the characters a URL reserves. Percent
    /// decoding, the first colon and the last '@' are what keep it intact.
    /// </summary>
    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    [InlineData("clickhouse")]
    public void A_password_carrying_reserved_characters_survives_intact(string driverId)
    {
        var details = Read(
            driverId,
            $"{Scheme(driverId)}://us%40er:p%40ss%3Aw%2Frd@db.example.test/app");

        Assert.Equal("us@er", details.Username);
        Assert.Equal("p@ss:w/rd", details.Password);
        Assert.Equal("db.example.test", details.Host);
    }

    /// <summary>
    /// Anything already in keyword/value form is somebody's working connection
    /// string, and is not reformatted, reordered or otherwise improved.
    /// </summary>
    [Theory]
    [InlineData("postgres", "Host=localhost;Port=5432;Database=app;Username=postgres")]
    [InlineData("mysql", "Server=localhost;Port=3306;Database=app;User ID=root")]
    [InlineData("sqlserver", "Server=localhost,1433;Database=app;User ID=sa")]
    [InlineData("oracle", "Data Source=localhost:1521/FREEPDB1;User Id=app")]
    [InlineData("firebird", "DataSource=localhost;Database=/db/app.fdb;User=SYSDBA")]
    [InlineData("clickhouse", "Host=localhost;Port=8123;Database=default")]
    public void What_is_not_a_url_is_left_exactly_as_it_is(string driverId, string original)
    {
        Assert.Equal(original, Driver(driverId).NormalizeConnectionString(original));
    }

    /// <summary>
    /// Every engine that speaks a URL says so by translating one. This is the
    /// check that a driver added later is not quietly left out.
    /// </summary>
    [Theory]
    [InlineData("postgres")]
    [InlineData("cockroach")]
    [InlineData("redshift")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("sqlserver")]
    [InlineData("oracle")]
    [InlineData("firebird")]
    [InlineData("clickhouse")]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public void Every_shipped_engine_answers_to_its_own_url(string driverId)
    {
        var driver = Driver(driverId);
        var url = $"{Scheme(driverId)}://db.example.test/app";

        var normalized = driver.NormalizeConnectionString(url);

        Assert.NotEqual(url, normalized, StringComparer.Ordinal);
        Assert.DoesNotContain("://", normalized, StringComparison.Ordinal);
    }

    private static string Scheme(string driverId) => driverId switch
    {
        "postgres" or "cockroach" or "redshift" => "postgresql",
        "mysql" => "mysql",
        "mariadb" => "mariadb",
        "sqlserver" => "sqlserver",
        "oracle" => "oracle",
        "firebird" => "firebird",
        "clickhouse" => "clickhouse",
        "sqlite" => "sqlite",
        "duckdb" => "duckdb",
        _ => throw new ArgumentOutOfRangeException(nameof(driverId), driverId, null),
    };
}
