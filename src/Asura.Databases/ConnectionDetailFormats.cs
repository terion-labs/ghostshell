using System.Data.Common;
using System.Globalization;
using Asura.Application;

namespace Asura.Databases;

/// <summary>
/// The generic decompose/recompose between a key-value connection string and
/// <see cref="DatabaseConnectionDetails"/>. Each field lists its accepted key
/// synonyms — the first is canonical for building — and engines with packed
/// endpoints (SQL Server, Oracle) post-process the result. Unrecognized
/// parameters round-trip verbatim through Options.
/// </summary>
internal sealed record ConnectionDetailKeys(
    string[] Host,
    string[] Port,
    string[] Database,
    string[] Username,
    string[] Password)
{
    public DatabaseConnectionDetails Parse(string connectionString)
    {
        var builder = TryCreateBuilder(connectionString);
        if (builder is null)
        {
            return new DatabaseConnectionDetails(Options: connectionString);
        }

        var host = Take(builder, Host);
        var port = Take(builder, Port);
        return new DatabaseConnectionDetails(
            host,
            int.TryParse(port, System.Globalization.CultureInfo.InvariantCulture, out var parsedPort) ? parsedPort : null,
            Take(builder, Database),
            Take(builder, Username),
            Take(builder, Password),
            FilePath: null,
            Options: builder.Count == 0 ? null : builder.ConnectionString);
    }

    public string Build(DatabaseConnectionDetails details)
    {
        var builder = TryCreateBuilder(details.Options ?? string.Empty)
            ?? [];
        Set(builder, Host, details.Host);
        Set(builder, Port, details.Port?.ToString(CultureInfo.InvariantCulture));
        Set(builder, Database, details.Database);
        Set(builder, Username, details.Username);
        Set(builder, Password, details.Password);
        return builder.ConnectionString;
    }

    internal static DbConnectionStringBuilder? TryCreateBuilder(string connectionString)
    {
        try
        {
            return new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Reads the first synonym present and removes every synonym.</summary>
    internal static string? Take(DbConnectionStringBuilder builder, string[] synonyms)
    {
        string? found = null;
        foreach (var key in synonyms)
        {
            if (builder.TryGetValue(key, out var value))
            {
                found ??= value as string ?? value?.ToString();
                builder.Remove(key);
            }
        }

        return string.IsNullOrWhiteSpace(found) ? null : found;
    }

    /// <summary>Writes the canonical synonym, clearing every other spelling.</summary>
    internal static void Set(
        DbConnectionStringBuilder builder,
        string[] synonyms,
        string? value)
    {
        foreach (var key in synonyms)
        {
            builder.Remove(key);
        }

        if (!string.IsNullOrWhiteSpace(value) && synonyms.Length > 0)
        {
            builder[synonyms[0]] = value;
        }
    }
}
