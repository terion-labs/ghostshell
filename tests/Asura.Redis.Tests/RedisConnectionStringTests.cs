using Asura.Application;

namespace Asura.Redis.Tests;

public sealed class RedisConnectionStringTests
{
    [Fact]
    public void BuildAndParseRoundTripStructuredRedisDetails()
    {
        var details = new DatabaseConnectionDetails(
            "cache.internal",
            6380,
            "3",
            "operator",
            "secret",
            Options: "ssl=true");

        var connectionString = RedisConnectionString.Build(details);
        var parsed = RedisConnectionString.ParseDetails(connectionString);

        Assert.Equal("cache.internal", parsed.Host);
        Assert.Equal(6380, parsed.Port);
        Assert.Equal("3", parsed.Database);
        Assert.Equal("operator", parsed.Username);
        Assert.Equal("secret", parsed.Password);
        Assert.Contains("ssl=true", parsed.Options, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("redis://user:password@localhost:6379/2", false)]
    [InlineData("rediss://user:password@localhost:6380/0", true)]
    public void ParseAcceptsRedisUrls(string value, bool expectedTls)
    {
        var options = RedisConnectionString.ParseConfiguration(value);

        Assert.Equal("user", options.User);
        Assert.Equal("password", options.Password);
        Assert.Equal(expectedTls, options.Ssl);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void BuildRejectsInvalidLogicalDatabase(string database)
    {
        var details = new DatabaseConnectionDetails("localhost", 6379, database);

        Assert.Throws<ArgumentException>(() => RedisConnectionString.Build(details));
    }
}
