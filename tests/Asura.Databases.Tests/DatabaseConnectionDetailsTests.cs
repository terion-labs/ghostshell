using Asura.Application;
using Asura.Databases;

namespace Asura.Databases.Tests;

public sealed class DatabaseConnectionDetailsTests
{
    [Fact]
    public void Postgres_details_round_trip_and_preserve_unknown_options()
    {
        var driver = Driver("postgres");

        var details = driver.ParseDetails(
            "Host=db.internal;Port=5433;Database=app;Username=ops;Password=s3cret;SSL Mode=Require");

        Assert.Equal("db.internal", details.Host);
        Assert.Equal(5433, details.Port);
        Assert.Equal("app", details.Database);
        Assert.Equal("ops", details.Username);
        Assert.Equal("s3cret", details.Password);
        Assert.Contains("SSL Mode=Require", details.Options, StringComparison.OrdinalIgnoreCase);

        var rebuilt = driver.BuildConnectionString(details);
        var reparsed = driver.ParseDetails(rebuilt);
        Assert.Equal(details, reparsed);
    }

    [Fact]
    public void Mysql_synonyms_map_onto_canonical_keys()
    {
        var driver = Driver("mysql");

        var details = driver.ParseDetails("Host=db;Uid=root;Pwd=x;Database=app");

        Assert.Equal("db", details.Host);
        Assert.Equal("root", details.Username);
        Assert.Equal("x", details.Password);
        Assert.Null(details.Options);
    }

    [Fact]
    public void Sql_server_packs_host_and_port_into_the_data_source()
    {
        var driver = Driver("sqlserver");

        var details = driver.ParseDetails(
            "Server=db.internal,14330;Database=app;User ID=sa;Password=x;TrustServerCertificate=True");
        Assert.Equal("db.internal", details.Host);
        Assert.Equal(14330, details.Port);

        var rebuilt = driver.BuildConnectionString(details);
        Assert.Contains("db.internal,14330", rebuilt, StringComparison.Ordinal);
        Assert.Contains("trustservercertificate", rebuilt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(details, driver.ParseDetails(rebuilt));
    }

    [Fact]
    public void Oracle_maps_the_service_name_onto_the_database_field()
    {
        var driver = Driver("oracle");

        var details = driver.ParseDetails(
            "Data Source=db.internal:1522/FREEPDB1;User Id=app;Password=x");
        Assert.Equal("db.internal", details.Host);
        Assert.Equal(1522, details.Port);
        Assert.Equal("FREEPDB1", details.Database);

        var rebuilt = driver.BuildConnectionString(details);
        Assert.Contains("db.internal:1522/FREEPDB1", rebuilt, StringComparison.Ordinal);
        Assert.Equal(details, driver.ParseDetails(rebuilt));
    }

    [Fact]
    public void File_engines_expose_the_path_and_keep_extra_options()
    {
        var driver = Driver("sqlite");

        Assert.Equal(
            "/data/app.db",
            driver.ParseDetails("/data/app.db").FilePath);

        var withOptions = driver.ParseDetails("Data Source=/data/app.db;Mode=ReadOnly");
        Assert.Equal("/data/app.db", withOptions.FilePath);
        Assert.Contains("mode=ReadOnly", withOptions.Options, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            "/data/app.db",
            driver.BuildConnectionString(new DatabaseConnectionDetails(FilePath: "/data/app.db")));
        var rebuilt = driver.BuildConnectionString(withOptions);
        Assert.Contains("data source=/data/app.db", rebuilt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mode=ReadOnly", rebuilt, StringComparison.OrdinalIgnoreCase);
    }

    private static IDatabaseDriver Driver(string id) =>
        BuiltInDatabaseDrivers.All.Single(driver => string.Equals(driver.Descriptor.Id, id, StringComparison.Ordinal));
}
