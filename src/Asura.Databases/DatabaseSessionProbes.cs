using System.Data.Common;

namespace Asura.Databases;

/// <summary>
/// Reads optional facts from an open connection. A session fact — server
/// version, negotiated TLS — is decoration on a working connection, so a
/// probe the engine cannot answer returns null rather than surfacing an
/// error the user cannot act on.
/// </summary>
internal static class DatabaseSessionProbes
{
    public static string? TryGetServerVersion(DbConnection connection)
    {
        try
        {
            var version = connection.ServerVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception exception)
            when (exception is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// One value from one row, by ordinal — SHOW-style statements answer in
    /// (name, value) pairs, so the caller says which column carries the fact.
    /// </summary>
    public static async ValueTask<string?> TryQueryScalarAsync(
        DbConnection connection,
        string sql,
        int ordinal,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.IsDBNull(ordinal))
            {
                return null;
            }

            var value = reader.GetValue(ordinal)?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (DbException)
        {
            // The engine variant lacks the view (Redshift and CockroachDB
            // answer to Postgres but not to pg_stat_ssl) or the principal
            // cannot read it. Either way the fact is simply unavailable.
            return null;
        }
    }
}
