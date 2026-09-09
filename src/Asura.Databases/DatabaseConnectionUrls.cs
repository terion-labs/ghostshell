using System.Data.Common;

namespace Asura.Databases;

/// <summary>
/// The URL form of a connection, taken apart.
///
/// Every engine that has one writes it the same way —
/// <c>scheme://user:password@host:port/database?parameters</c> — and it is what
/// a hosting provider hands you, what its documentation shows, and what its own
/// command-line client reads. ADO.NET providers take keyword/value pairs and
/// nothing else, and given a URL they read the whole thing as one unknown
/// keyword: "Couldn't set postgresql://…", the string handed back to the person
/// who pasted it with no hint that the form was the problem.
///
/// The taking-apart is shared because it is identical everywhere and full of
/// small traps. What each engine calls a host, and where it wants the port, is
/// not shared: those genuinely differ, and a table of exceptions would be
/// harder to read than the eight lines each driver spends saying it plainly.
/// </summary>
internal sealed record ConnectionUrl(
    string? Username,
    string? Password,
    string? Host,
    int? Port,
    string Hosts,
    string? Database,
    IReadOnlyList<KeyValuePair<string, string>> Parameters)
{
    /// <summary>
    /// The URL, or null when this is already a keyword/value connection string
    /// — which is somebody's working configuration and is left exactly as it is.
    /// </summary>
    public static ConnectionUrl? TryParse(string? connectionString, params string[] schemes)
    {
        var text = connectionString?.Trim() ?? string.Empty;
        var scheme = schemes.FirstOrDefault(candidate =>
            text.StartsWith(candidate + "://", StringComparison.OrdinalIgnoreCase));
        if (scheme is null)
        {
            return null;
        }

        var rest = text[(scheme.Length + 3)..];
        // '?' is the usual separator; ';' is how JDBC and the tools that follow
        // it write the same thing, and no engine's URL uses ';' for anything
        // else.
        var separator = rest.IndexOfAny(['?', ';']);
        var query = separator < 0 ? string.Empty : rest[(separator + 1)..];
        var authority = separator < 0 ? rest : rest[..separator];
        var (address, database) = SplitAt(authority, '/');

        // The last '@', not the first: a generated password may contain one,
        // and a host may not.
        var credentials = address.LastIndexOf('@');
        var hosts = credentials < 0 ? address : address[(credentials + 1)..];
        var (user, password) = credentials < 0
            ? (null, null)
            : SplitCredentials(address[..credentials]);

        var (host, port) = SplitHost(hosts);
        return new ConnectionUrl(
            user,
            password,
            host,
            port,
            Decode(hosts),
            Decode(database) is { Length: > 0 } name ? name : null,
            ParseParameters(query));
    }

    /// <summary>
    /// Each parameter under whichever name the provider knows it by. A name it
    /// does not know at all is refused by name rather than dropped: quietly
    /// discarding, say, an sslrootcert would weaken the connection without
    /// saying so.
    /// </summary>
    public void ApplyParameters(
        DbConnectionStringBuilder builder,
        string engine,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var unsupported = new List<string>();
        foreach (var (key, value) in Parameters)
        {
            try
            {
                builder[aliases?.GetValueOrDefault(key) ?? key] = value;
            }
            catch (Exception exception) when (exception is ArgumentException
                or FormatException)
            {
                unsupported.Add(key);
            }
        }

        if (unsupported.Count > 0)
        {
            throw new ArgumentException(
                $"This build's {engine} driver does not understand "
                + $"{string.Join(", ", unsupported)}. Remove "
                + (unsupported.Count == 1 ? "it" : "them")
                + " from the URL, or write the connection out as keyword=value "
                + "pairs instead.");
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ParseParameters(string query)
    {
        if (query.Length == 0)
        {
            return [];
        }

        var parameters = new List<KeyValuePair<string, string>>();
        foreach (var pair in query.Split(['&', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = SplitAt(pair, '=');
            if (Decode(key) is { Length: > 0 } name)
            {
                parameters.Add(new KeyValuePair<string, string>(name, Decode(value)));
            }
        }

        return parameters;
    }

    private static (string? User, string? Password) SplitCredentials(string credentials)
    {
        if (credentials.Length == 0)
        {
            return (null, null);
        }

        // The first ':' — a password may contain one, a username may not.
        var separator = credentials.IndexOf(':');
        return separator < 0
            ? (Decode(credentials), null)
            : (Decode(credentials[..separator]), Decode(credentials[(separator + 1)..]));
    }

    /// <summary>
    /// One host and its port, where there is exactly one. libpq and the drivers
    /// that follow it allow a comma-separated list for failover, and there the
    /// port belongs to each entry rather than to the connection — so a list is
    /// left whole in <see cref="Hosts"/> for whoever can use it.
    /// </summary>
    private static (string? Host, int? Port) SplitHost(string hosts)
    {
        if (hosts.Length == 0 || hosts.Contains(',', StringComparison.Ordinal))
        {
            return (null, null);
        }

        var colon = hosts.LastIndexOf(':');
        return colon >= 0 && int.TryParse(hosts[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var port) ? (Decode(hosts[..colon]), port)
            : (Decode(hosts), null);
    }

    private static (string Before, string After) SplitAt(string text, char separator)
    {
        var index = text.IndexOf(separator);
        return index < 0
            ? (text, string.Empty)
            : (text[..index], text[(index + 1)..]);
    }

    private static string Decode(string text) =>
        text.Length == 0 ? text : Uri.UnescapeDataString(text);
}

/// <summary>
/// The file engines' URL form, which carries a path and nothing else.
/// <c>sqlite:///var/db/app.db</c> is the three-slash convention for an absolute
/// path; two slashes and a relative path is the other spelling in the wild.
/// </summary>
internal static class FileConnectionUrls
{
    public static string StripScheme(string connectionString, params string[] schemes)
    {
        var text = connectionString?.Trim() ?? string.Empty;
        var scheme = schemes.FirstOrDefault(candidate =>
            text.StartsWith(candidate + "://", StringComparison.OrdinalIgnoreCase));
        return scheme is null
            ? text
            : Uri.UnescapeDataString(text[(scheme.Length + 3)..]);
    }
}
