using Asura.Databases;
using Npgsql;

namespace Asura.Databases.Tests;

/// <summary>
/// The URL every managed Postgres hands out.
///
/// Neon, Supabase, Railway, Fly and psql all give you
/// <c>postgresql://user:password@host/database?sslmode=require</c>, and that is
/// what somebody pastes. Npgsql takes keyword/value pairs and nothing else: it
/// read the whole URL as one unknown keyword and answered "Couldn't set
/// postgresql://…", which is the string handed back to the person who pasted
/// it with no hint that the form was the problem.
/// </summary>
public sealed class PostgresConnectionStringsTests
{
    private static NpgsqlConnectionStringBuilder Parse(string url) =>
        new(PostgresConnectionStrings.Normalize(url));

    /// <summary>The connection Neon gives you, verbatim.</summary>
    [Fact]
    public void A_managed_postgres_url_becomes_a_connection_the_driver_reads()
    {
        var built = Parse(
            "postgresql://neondb_owner:npg_S3cret@ep-cool-forest-123456."
            + "eu-central-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require");

        Assert.Equal("ep-cool-forest-123456.eu-central-1.aws.neon.tech", built.Host);
        Assert.Equal("neondb", built.Database);
        Assert.Equal("neondb_owner", built.Username);
        Assert.Equal("npg_S3cret", built.Password);
        Assert.Equal(SslMode.Require, built.SslMode);
        Assert.Equal(ChannelBinding.Require, built.ChannelBinding);
        // Not stated in the URL, so the driver's own default stands.
        Assert.Equal(5432, built.Port);
    }

    [Theory]
    [InlineData("postgres://")]
    [InlineData("POSTGRESQL://")]
    public void Either_spelling_of_the_scheme_is_understood(string scheme)
    {
        var built = Parse($"{scheme}alice:secret@db.example.test:6543/shop");

        Assert.Equal("db.example.test", built.Host);
        Assert.Equal(6543, built.Port);
        Assert.Equal("shop", built.Database);
        Assert.Equal("alice", built.Username);
    }

    /// <summary>
    /// A password is generated, not chosen, and generated passwords contain
    /// the characters a URL reserves. Both halves of the credentials are
    /// percent-decoded, and the split is on the first colon and the last '@' —
    /// a password may contain either, a username and a host may not.
    /// </summary>
    [Fact]
    public void A_password_carrying_reserved_characters_survives_intact()
    {
        var built = Parse(
            "postgresql://us%40er:p%40ss%3Aw%2Frd%231@db.example.test/app");

        Assert.Equal("us@er", built.Username);
        Assert.Equal("p@ss:w/rd#1", built.Password);
        Assert.Equal("db.example.test", built.Host);
        Assert.Equal("app", built.Database);
    }

    [Fact]
    public void A_url_with_nothing_but_a_host_still_connects_somewhere()
    {
        var built = Parse("postgresql://localhost");

        Assert.Equal("localhost", built.Host);
        Assert.Null(built.Database);
        Assert.Null(built.Username);
    }

    /// <summary>
    /// libpq's failover list goes across whole: with several hosts the port
    /// belongs to each entry rather than to the connection, and Npgsql reads
    /// the list the same way.
    /// </summary>
    [Fact]
    public void A_failover_list_is_passed_on_as_a_list()
    {
        var built = Parse("postgresql://alice@first.example.test,second.example.test/app");

        Assert.Equal("first.example.test,second.example.test", built.Host);
    }

    /// <summary>
    /// Anything already in keyword/value form is somebody's working connection
    /// string. It is not reformatted, reordered or otherwise improved.
    /// </summary>
    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=app;Username=postgres")]
    [InlineData("")]
    public void What_is_not_a_url_is_left_exactly_as_it_is(string original)
    {
        Assert.Equal(original, PostgresConnectionStrings.Normalize(original));
    }

    /// <summary>
    /// A parameter this build cannot honour is refused by name rather than
    /// dropped. Quietly discarding one that turns verification on would weaken
    /// the connection and say nothing about it.
    /// </summary>
    [Fact]
    public void A_parameter_the_driver_cannot_honour_is_named_rather_than_dropped()
    {
        var refusal = Assert.Throws<ArgumentException>(() => PostgresConnectionStrings.Normalize(
            "postgresql://alice@db.example.test/app?sslmode=require&gssencmode=require"));

        Assert.Contains("gssencmode", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("keyword=value", refusal.Message, StringComparison.Ordinal);
    }
}
