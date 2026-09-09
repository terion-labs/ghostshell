using Npgsql;

namespace Asura.Databases;

/// <summary>
/// The URL every managed Postgres hands out. Neon, Supabase, Railway, Fly,
/// Heroku and psql itself all speak
/// <c>postgresql://user:password@host/database?sslmode=require</c>.
/// </summary>
internal static class PostgresConnectionStrings
{
    /// <summary>
    /// libpq's spelling of a parameter, where Npgsql spells it differently.
    /// Npgsql answers to a few of these itself — <c>sslmode</c> among them —
    /// but not to most, and a URL from a hosting provider is written in libpq's
    /// vocabulary because that is the vocabulary psql reads. Neon requires
    /// <c>channel_binding</c>, which was one of the ones it refused.
    /// </summary>
    private static readonly Dictionary<string, string> LibpqNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["channel_binding"] = "Channel Binding",
            ["sslnegotiation"] = "SSL Negotiation",
            ["sslcert"] = "SSL Certificate",
            ["sslkey"] = "SSL Key",
            ["sslpassword"] = "SSL Password",
            ["sslrootcert"] = "Root Certificate",
            ["application_name"] = "Application Name",
            ["client_encoding"] = "Client Encoding",
            ["connect_timeout"] = "Timeout",
            ["target_session_attrs"] = "Target Session Attributes",
            ["passfile"] = "Passfile",
            ["options"] = "Options",
        };

    public static string Normalize(string? connectionString)
    {
        if (ConnectionUrl.TryParse(connectionString, "postgresql", "postgres") is not { } url)
        {
            return connectionString?.Trim() ?? string.Empty;
        }

        var builder = new NpgsqlConnectionStringBuilder();
        if (url.Hosts.Length > 0)
        {
            builder.Host = url.Host ?? url.Hosts;
        }

        if (url.Port is { } port)
        {
            builder.Port = port;
        }

        if (url.Database is { } database)
        {
            builder.Database = database;
        }

        if (url.Username is { } username)
        {
            builder.Username = username;
        }

        if (url.Password is { } password)
        {
            builder.Password = password;
        }

        url.ApplyParameters(builder, "PostgreSQL", LibpqNames);
        return builder.ConnectionString;
    }
}
