using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using GhostShell.Application;

namespace GhostShell.ConnectionBackend;

internal sealed record DatabaseProviderDiagnostic(string Message, string? SqlState, int? Number, int? Line, int? Position)
{
    internal static DatabaseProviderDiagnostic Create(Exception exception, DatabaseWorkerConnection target,
        IDatabaseConnectionCatalog catalog)
    {
        var state = (exception as DbException)?.SqlState;
        if (state?.Length != 5 || !state.All(char.IsAsciiLetterOrDigit)) { state = null; }
        var number = exception switch
        {
            Microsoft.Data.SqlClient.SqlException sql => sql.Number,
            Microsoft.Data.Sqlite.SqliteException sqlite => sqlite.SqliteExtendedErrorCode,
            MySqlConnector.MySqlException mysql => mysql.Number,
            DbException database => (int?)database.ErrorCode,
            _ => null,
        };
        var line = exception is Microsoft.Data.SqlClient.SqlException server && server.LineNumber > 0 ? server.LineNumber : (int?)null;
        var position = exception is Npgsql.PostgresException postgres && postgres.Position > 0 ? postgres.Position : (int?)null;
        var message = exception is Npgsql.PostgresException reported ? reported.MessageText : exception.Message;
        return new(Sanitize(message, target, catalog), state, number, line, position);
    }

    private static string Sanitize(string message, DatabaseWorkerConnection target, IDatabaseConnectionCatalog catalog)
    {
        // A database-reported message is useful query feedback, not a closed log
        // record. Unknown connection syntax has no raw-message fallback.
        if (!TryGetSecrets(target, catalog, out var secrets))
        {
            return "Diagnostic text omitted because connection-secret extraction is unavailable for this target.";
        }
        if (message.Length > 8192 || Encoding.UTF8.GetByteCount(message) > 8192)
        {
            return "Diagnostic text omitted because the database message exceeds 8 KiB.";
        }
        foreach (var secret in secrets.OrderByDescending(value => value.Length))
        {
            message = message.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }
        return message;
    }

    private static bool TryGetSecrets(DatabaseWorkerConnection target, IDatabaseConnectionCatalog catalog,
        out HashSet<string> secrets)
    {
        secrets = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!catalog.IsConnectionStringValid(target.DriverId, target.ConnectionString)) { return false; }
            AddSecret(secrets, target.ConnectionString);
            AddSecret(secrets, catalog.ParseConnectionDetails(target.DriverId, target.ConnectionString).Password);
            if (target.ConnectionString.Contains("://", StringComparison.Ordinal)
                && Uri.TryCreate(target.ConnectionString, UriKind.Absolute, out var uri))
            {
                var separator = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
                if (separator >= 0) { AddSecret(secrets, Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..])); }
                foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length != 2) { return false; }
                    if (IsSecretKey(Uri.UnescapeDataString(pair[0]))) { AddSecret(secrets, Uri.UnescapeDataString(pair[1])); }
                }
                return true;
            }
            var builder = new DbConnectionStringBuilder { ConnectionString = target.ConnectionString };
            foreach (string key in builder.Keys)
            {
                if (IsSecretKey(key)) { AddSecret(secrets, Convert.ToString(builder[key], CultureInfo.InvariantCulture)); }
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or NotSupportedException or InvalidOperationException or DbException)
        {
            return false;
        }
    }

    private static bool IsSecretKey(string key)
    {
        var normalized = string.Concat(key.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized is "pwd" or "key"
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("passphrase", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("accesskey", StringComparison.Ordinal);
    }

    private static void AddSecret(HashSet<string> secrets, string? value)
    {
        if (string.IsNullOrEmpty(value)) { return; }
        secrets.Add(value);
        secrets.Add(Uri.EscapeDataString(value));
        secrets.Add(WebUtility.UrlEncode(value));
        secrets.Add(System.Text.Json.JsonEncodedText.Encode(value).ToString());
        secrets.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
    }

    internal string DisplayMessage()
    {
        var fields = new List<string>();
        if (SqlState is not null) { fields.Add("SQLSTATE " + SqlState); }
        if (Number is not null) { fields.Add("code " + Number.Value.ToString(CultureInfo.InvariantCulture)); }
        if (Line is not null) { fields.Add("line " + Line.Value.ToString(CultureInfo.InvariantCulture)); }
        if (Position is not null) { fields.Add("position " + Position.Value.ToString(CultureInfo.InvariantCulture)); }
        return "Database reported: " + Message + (fields.Count == 0 ? string.Empty : "\n" + string.Join(", ", fields))
            + "\nEarlier statements or side effects may have completed. Verify the database before retrying.";
    }
}

internal sealed class DatabaseProviderOperationException(DatabaseProviderDiagnostic diagnostic) : IOException(diagnostic.DisplayMessage())
{
    internal DatabaseProviderDiagnostic Diagnostic { get; } = diagnostic;
}
