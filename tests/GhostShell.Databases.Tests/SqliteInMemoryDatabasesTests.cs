using GhostShell.Application;
using Microsoft.Data.Sqlite;

namespace GhostShell.Databases.Tests;

/// <summary>
/// A previewed remote database is served to the engine from memory. These
/// prove the whole path — registration, the driver resolving the token, the
/// deserialized image answering queries — and the two safety edges: the image
/// is read-only, and closing the preview mid-query frees nothing early.
/// </summary>
public sealed class SqliteInMemoryDatabasesTests
{
    [Fact]
    public async Task SnapshotBorrowRemainsReadableAfterPreviewUnregisterWithoutAParentCopy()
    {
        var image = BuildDatabaseImage();
        var target = SqliteInMemoryDatabases.Register(image);
        await using var snapshot = Assert.IsAssignableFrom<Stream>(SqliteInMemoryDatabases.OpenSnapshot(target));
        SqliteInMemoryDatabases.Unregister(target);
        Assert.False(snapshot.CanWrite);
        Assert.Equal(image.Length, snapshot.Length);
        Assert.Throws<InvalidOperationException>(() => SqliteInMemoryDatabases.OpenSnapshot(target));
        var restored = new byte[image.Length];
        await snapshot.ReadExactlyAsync(restored, CancellationToken.None);
        Assert.Equal(image, restored);
    }

    [Fact]
    public void SnapshotBorrowDistinguishesOrdinaryFilesFromUnknownTokens()
    {
        Assert.Null(SqliteInMemoryDatabases.OpenSnapshot("Data Source=ordinary.db"));
        Assert.Throws<InvalidOperationException>(() => SqliteInMemoryDatabases.OpenSnapshot("Data Source=ghostshell-memory:unknown"));
    }

    private static byte[] BuildDatabaseImage()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"ghostshell-dbimage-{Guid.NewGuid():n}.db");
        try
        {
            using (var build = new SqliteConnection(
                $"Data Source={path};Pooling=False"))
            {
                build.Open();
                using var create = build.CreateCommand();
                create.CommandText =
                    "CREATE TABLE people(name TEXT);"
                    + "INSERT INTO people VALUES ('ada'), ('lin');";
                create.ExecuteNonQuery();
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static IDatabaseDriver SqliteDriver() =>
        BuiltInDatabaseDrivers.All.Single(driver => string.Equals(driver.Descriptor.Id, "sqlite", StringComparison.Ordinal));

    [Fact]
    public void A_registered_image_answers_queries_through_the_ordinary_driver()
    {
        IInMemoryDatabaseRegistry registry = new SqliteInMemoryDatabaseRegistry();
        var connectionString = registry.Register(BuildDatabaseImage());
        try
        {
            var driver = SqliteDriver();
            using var connection = driver.CreateConnection(
                driver.NormalizeConnectionString(connectionString));
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT count(*) FROM people";
            Assert.Equal(2L, query.ExecuteScalar());
        }
        finally
        {
            registry.Unregister(connectionString);
        }
    }

    [Fact]
    public void The_image_refuses_writes()
    {
        IInMemoryDatabaseRegistry registry = new SqliteInMemoryDatabaseRegistry();
        var connectionString = registry.Register(BuildDatabaseImage());
        try
        {
            var driver = SqliteDriver();
            using var connection = driver.CreateConnection(connectionString);
            connection.Open();
            using var write = connection.CreateCommand();
            write.CommandText = "INSERT INTO people VALUES ('eve')";
            var refusal = Assert.Throws<SqliteException>(() => write.ExecuteNonQuery());
            Assert.Equal(8, refusal.SqliteErrorCode); // SQLITE_READONLY
        }
        finally
        {
            registry.Unregister(connectionString);
        }
    }

    [Fact]
    public void An_unregistered_connection_string_stops_opening()
    {
        IInMemoryDatabaseRegistry registry = new SqliteInMemoryDatabaseRegistry();
        var connectionString = registry.Register(BuildDatabaseImage());
        registry.Unregister(connectionString);

        using var connection = SqliteDriver().CreateConnection(connectionString);
        Assert.ThrowsAny<Exception>(connection.Open);
    }

    [Fact]
    public void Closing_the_preview_does_not_pull_the_image_from_under_an_open_connection()
    {
        IInMemoryDatabaseRegistry registry = new SqliteInMemoryDatabaseRegistry();
        var connectionString = registry.Register(BuildDatabaseImage());
        var driver = SqliteDriver();
        using var connection = driver.CreateConnection(connectionString);
        connection.Open();

        // The preview closes while this connection is still serving a query.
        registry.Unregister(connectionString);

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT group_concat(name) FROM people";
        Assert.Equal("ada,lin", query.ExecuteScalar());
    }

    [Fact]
    public void An_ordinary_connection_string_is_untouched_by_the_registry_hook()
    {
        var driver = SqliteDriver();
        var normalized = driver.NormalizeConnectionString("/tmp/plain.db");
        Assert.Equal("Data Source=/tmp/plain.db", normalized);
        using var connection = driver.CreateConnection("Data Source=:memory:");
        connection.Open();
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1";
        Assert.Equal(1L, probe.ExecuteScalar());
    }
}
