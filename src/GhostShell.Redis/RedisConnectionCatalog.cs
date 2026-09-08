using System.Globalization;
using System.Net;
using GhostShell.Application;
using GhostShell.Core;
using StackExchange.Redis;

namespace GhostShell.Redis;

/// <summary>
/// Adds Redis connection-definition behavior to the relational catalog while
/// keeping data operations in their respective runtimes.
/// </summary>
public sealed class RedisConnectionCatalog(
    IDatabasePanelClient relational,
    IRedisPanelSessionFactory redisSessions) : IDatabaseConnectionCatalog
{
    private readonly IDatabasePanelClient _relational = relational
        ?? throw new ArgumentNullException(nameof(relational));
    private readonly IRedisPanelSessionFactory _redisSessions = redisSessions
        ?? throw new ArgumentNullException(nameof(redisSessions));

    public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
        [.. relational.Drivers, RedisDatabase.Descriptor];

    public bool IsConnectionStringValid(string driverId, string connectionString)
    {
        if (!IsRedis(driverId))
        {
            return _relational.IsConnectionStringValid(driverId, connectionString);
        }
        try
        {
            if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)
                && uri.Scheme is "redis" or "rediss" && !string.IsNullOrEmpty(uri.Query))
            {
                // The editor currently ignores Redis URI query options. That
                // permissive behavior cannot prove an export contains no secret.
                return false;
            }
            _ = Parse(connectionString);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or NotSupportedException)
        {
            return false;
        }
    }

    public async Task<DatabaseSessionInfo> DescribeSessionAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken)
    {
        if (!IsRedis(driverId))
        {
            return await _relational.DescribeSessionAsync(
                    driverId,
                    connectionString,
                    tunnel,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await using var session = await _redisSessions
            .OpenAsync(connectionString, tunnel, cancellationToken)
            .ConfigureAwait(false);
        return new DatabaseSessionInfo(
            session.Facts.Version,
            Parse(connectionString).Ssl ? "TLS" : null);
    }

    public DatabaseConnectionDetails ParseConnectionDetails(
        string driverId,
        string connectionString) =>
        IsRedis(driverId)
            ? RedisConnectionString.ParseDetails(connectionString)
            : _relational.ParseConnectionDetails(driverId, connectionString);

    public string BuildConnectionString(
        string driverId,
        DatabaseConnectionDetails details) =>
        IsRedis(driverId)
            ? RedisConnectionString.Build(details)
            : _relational.BuildConnectionString(driverId, details);

    internal static ConfigurationOptions Parse(string connectionString) =>
        RedisConnectionString.ParseConfiguration(connectionString);

    private static bool IsRedis(string driverId) =>
        string.Equals(driverId, RedisDatabase.DriverId, StringComparison.Ordinal);
}

internal static class RedisConnectionString
{
    public static DatabaseConnectionDetails ParseDetails(string value)
    {
        var options = ParseConfiguration(value);
        var endpoint = options.EndPoints.FirstOrDefault()
            ?? throw new ArgumentException("The Redis connection needs an endpoint.", nameof(value));
        var (host, port) = endpoint switch
        {
            DnsEndPoint dns => (dns.Host, dns.Port),
            IPEndPoint ip => (ip.Address.ToString(), ip.Port),
            _ => throw new ArgumentException("The Redis endpoint is not a TCP endpoint.", nameof(value)),
        };

        var preserved = new List<string>();
        if (options.Ssl)
        {
            preserved.Add("ssl=true");
        }

        if (!options.AbortOnConnectFail)
        {
            preserved.Add("abortConnect=false");
        }

        if (!string.IsNullOrWhiteSpace(options.ServiceName))
        {
            preserved.Add($"serviceName={options.ServiceName}");
        }

        return new DatabaseConnectionDetails(
            host,
            port,
            options.DefaultDatabase?.ToString(CultureInfo.InvariantCulture),
            options.User,
            options.Password,
            Options: preserved.Count == 0 ? null : string.Join(',', preserved));
    }

    public static string Build(DatabaseConnectionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var host = string.IsNullOrWhiteSpace(details.Host) ? "localhost" : details.Host.Trim();
        var port = details.Port ?? RedisDatabase.Descriptor.DefaultPort!.Value;
        var values = new List<string> { $"{host}:{port.ToString(CultureInfo.InvariantCulture)}" };
        if (!string.IsNullOrWhiteSpace(details.Username))
        {
            values.Add($"user={details.Username.Trim()}");
        }

        if (!string.IsNullOrEmpty(details.Password))
        {
            values.Add($"password={details.Password}");
        }

        if (!string.IsNullOrWhiteSpace(details.Database))
        {
            if (!int.TryParse(details.Database, NumberStyles.None, CultureInfo.InvariantCulture, out var database)
                || database < 0)
            {
                throw new ArgumentException("Redis logical database must be a non-negative number.");
            }

            values.Add($"defaultDatabase={database.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(details.Options))
        {
            values.Add(details.Options.Trim().Trim(','));
        }

        return string.Join(',', values);
    }

    public static ConfigurationOptions ParseConfiguration(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Enter a Redis connection string or URL.", nameof(value));
        }

        var normalized = value.Trim();
        var options = normalized.StartsWith("redis://", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase)
                ? ParseUri(new Uri(normalized, UriKind.Absolute))
                : ConfigurationOptions.Parse(normalized);
        if (options.EndPoints.Count == 0)
        {
            throw new ArgumentException("The Redis connection needs a host.", nameof(value));
        }

        options.AbortOnConnectFail = false;
        options.AllowAdmin = false;
        options.ClientName = "Ghostshell";
        options.ConnectTimeout = Math.Min(options.ConnectTimeout, 5000);
        options.SyncTimeout = Math.Min(options.SyncTimeout, 5000);
        return options;
    }

    private static ConfigurationOptions ParseUri(Uri uri)
    {
        var options = new ConfigurationOptions
        {
            Ssl = string.Equals(uri.Scheme, "rediss", StringComparison.OrdinalIgnoreCase),
        };
        options.EndPoints.Add(uri.Host, uri.IsDefaultPort ? 6379 : uri.Port);
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var credentials = uri.UserInfo.Split(':', 2);
            if (credentials.Length == 1)
            {
                options.Password = Uri.UnescapeDataString(credentials[0]);
            }
            else
            {
                options.User = Uri.UnescapeDataString(credentials[0]);
                options.Password = Uri.UnescapeDataString(credentials[1]);
            }
        }

        if (uri.AbsolutePath.Length > 1
            && int.TryParse(uri.AbsolutePath[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var database))
        {
            options.DefaultDatabase = database;
        }

        return options;
    }
}
