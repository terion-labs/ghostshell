using System.Text.Json.Serialization;

namespace GhostShell.Core;

/// <summary>
/// Controls how panels use one named logical browser profile.
/// DurableMetadata is the original serialized name for a durable encrypted
/// session: Chromium state is restored between runs. PrivateSession gives each
/// panel a separate context and discards it when that panel closes.
/// </summary>
public enum BrowserProfilePersistence
{
    DurableMetadata,
    PrivateSession,
}

public enum BrowserWebContentRetention
{
    EncryptedBetweenRuns,
    EphemeralOnly,
}

public enum BrowserPermissionRetention
{
    DenyAll,
}

public enum BrowserActivityRetention
{
    DoNotRecord,
}

public enum BrowserAuthenticationScheme
{
    Basic,
    Digest,
}

/// <summary>
/// The closed set of privacy choices implemented by the current browser host.
/// Durable content is sealed into encrypted application storage. Permission
/// requests and downloads are blocked, and navigation history is not projected
/// into GhostSHELL's durable activity records.
/// </summary>
public sealed record BrowserProfilePrivacyPolicy
{
    [JsonConstructor]
    public BrowserProfilePrivacyPolicy(
        BrowserWebContentRetention webContent,
        BrowserPermissionRetention permissions,
        BrowserActivityRetention history,
        BrowserActivityRetention downloads)
    {
        if (!Enum.IsDefined(webContent)
            || !Enum.IsDefined(permissions)
            || !Enum.IsDefined(history)
            || !Enum.IsDefined(downloads))
        {
            throw new ArgumentOutOfRangeException(
                nameof(webContent),
                "The browser privacy policy contains an unsupported value.");
        }

        WebContent = webContent;
        Permissions = permissions;
        History = history;
        Downloads = downloads;
    }

    public static BrowserProfilePrivacyPolicy Strict { get; } = new(
        BrowserWebContentRetention.EncryptedBetweenRuns,
        BrowserPermissionRetention.DenyAll,
        BrowserActivityRetention.DoNotRecord,
        BrowserActivityRetention.DoNotRecord);

    public static BrowserProfilePrivacyPolicy PrivateSession { get; } = new(
        BrowserWebContentRetention.EphemeralOnly,
        BrowserPermissionRetention.DenyAll,
        BrowserActivityRetention.DoNotRecord,
        BrowserActivityRetention.DoNotRecord);

    public BrowserWebContentRetention WebContent { get; }

    public BrowserPermissionRetention Permissions { get; }

    public BrowserActivityRetention History { get; }

    public BrowserActivityRetention Downloads { get; }
}

/// <summary>
/// Optional HTTP challenge credentials. The password remains in the operating
/// system vault and can be used only for the exact bounded challenge target.
/// </summary>
public sealed record BrowserHttpAuthentication
{
    public const int MaximumHostLength = 253;
    public const int MaximumRealmLength = 256;
    public const int MaximumUsernameLength = 256;
    public const int MaximumPasswordByteLength = 4_096;

    [JsonConstructor]
    public BrowserHttpAuthentication(
        string host,
        int? port,
        string? realm,
        BrowserAuthenticationScheme scheme,
        string username,
        SecretRef passwordSecret,
        string originScheme = "https",
        string routeIdentity = "local")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalizedHost.Length > MaximumHostLength
            || Uri.CheckHostName(normalizedHost) == UriHostNameType.Unknown)
        {
            throw new ArgumentException(
                "Browser authentication requires a bounded DNS name or IP address.",
                nameof(host));
        }

        if (port is <= 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (!Enum.IsDefined(scheme))
        {
            throw new ArgumentOutOfRangeException(nameof(scheme));
        }

        var normalizedRealm = string.IsNullOrWhiteSpace(realm) ? null : realm.Trim();
        if (normalizedRealm?.Length > MaximumRealmLength
            || normalizedRealm?.Any(char.IsControl) is true)
        {
            throw new ArgumentException(
                "The authentication realm must be bounded printable text.",
                nameof(realm));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var normalizedUsername = username.Trim();
        if (normalizedUsername.Length > MaximumUsernameLength
            || normalizedUsername.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The authentication username must be bounded printable text.",
                nameof(username));
        }

        RuntimeId.Require(passwordSecret.Value, nameof(passwordSecret));
        if (originScheme is not ("https" or "http"))
        {
            throw new ArgumentException("Authentication requires an explicit HTTP or HTTPS origin.", nameof(originScheme));
        }
        RuntimeId.Require(routeIdentity, nameof(routeIdentity));
        Host = normalizedHost;
        Port = port;
        Realm = normalizedRealm;
        Scheme = scheme;
        Username = normalizedUsername;
        PasswordSecret = passwordSecret;
        OriginScheme = originScheme;
        RouteIdentity = routeIdentity;
    }

    public string Host { get; }

    public int? Port { get; }

    public string? Realm { get; }

    public BrowserAuthenticationScheme Scheme { get; }

    public string Username { get; }

    public SecretRef PasswordSecret { get; }

    public string OriginScheme { get; }

    public string RouteIdentity { get; }

    public static string SshRouteIdentity(ConnectionProfile connection)
    {
        if (connection.Endpoint is not ConnectionEndpoint.Ssh endpoint)
        {
            throw new ArgumentException("An SSH authentication route requires an SSH endpoint.", nameof(connection));
        }

        var fields = new[] { connection.Id.Value, endpoint.Host.TrimEnd('.').ToLowerInvariant(),
            endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), endpoint.Username };
        var identity = System.Text.Encoding.UTF8.GetBytes(string.Concat(fields.Select(value =>
            (value?.Length ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value)));
        return "ssh-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(identity));
    }

    public static string NetworkRouteIdentity(NetworkConnectionProfile connection)
    {
        string?[] route = connection.Configuration switch
        {
            NetworkConnectionConfiguration.Proxy proxy => ["proxy", proxy.Endpoint.AbsoluteUri, proxy.Username],
            NetworkConnectionConfiguration.AnyConnect vpn => ["anyconnect", vpn.Gateway.AbsoluteUri, vpn.Username, vpn.AuthenticationGroup, vpn.ClientCertificateSecret?.Value],
            NetworkConnectionConfiguration.OpenVpn vpn => ["openvpn", vpn.ConfigurationSecret.Value, vpn.Username],
            NetworkConnectionConfiguration.WireGuard vpn => ["wireguard", vpn.ConfigurationSecret.Value],
            NetworkConnectionConfiguration.Tailscale vpn => ["tailscale", vpn.ControlServer?.AbsoluteUri, vpn.ExitNode, vpn.AuthKeySecret?.Value],
            _ => throw new ArgumentException("Unsupported browser authentication route.", nameof(connection)),
        };
        var fields = route.Prepend(connection.Id.Value);
        var identity = System.Text.Encoding.UTF8.GetBytes(string.Concat(fields.Select(value =>
            (value?.Length ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value)));
        return "network-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(identity));
    }
}

/// <summary>
/// Versioned browser-profile policy metadata. Chromium web content lives in a
/// separate encrypted state archive and never enters this definition.
/// </summary>
public sealed record BrowserProfileDefinition : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumNameLength = 128;

    [JsonConstructor]
    public BrowserProfileDefinition(
        BrowserProfileId id,
        int schemaVersion,
        string name,
        BrowserProfilePersistence persistence,
        BrowserProfilePrivacyPolicy privacy,
        BrowserHttpAuthentication? authentication = null,
        bool isEnabled = true)
    {
        RuntimeId.Require(id.Value, nameof(id));
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                "The browser-profile schema version is not supported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedName = name.Trim();
        if (normalizedName.Length > MaximumNameLength
            || normalizedName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The browser profile name must be bounded printable text.",
                nameof(name));
        }

        if (!Enum.IsDefined(persistence))
        {
            throw new ArgumentOutOfRangeException(nameof(persistence));
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Name = normalizedName;
        Persistence = persistence;
        Privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
        Authentication = authentication;
        IsEnabled = isEnabled;
    }

    public static DefinitionKind Kind => DefinitionKind.BrowserProfile;

    public BrowserProfileId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    public BrowserProfilePersistence Persistence { get; }

    public BrowserProfilePrivacyPolicy Privacy { get; }

    public BrowserHttpAuthentication? Authentication { get; }

    public bool IsEnabled { get; }
}

public static class BuiltInBrowserProfiles
{
    public const string DefaultProfileId = "builtin.browser.default";

    public static BrowserProfileDefinition Default { get; } = new(
        new BrowserProfileId(DefaultProfileId),
        BrowserProfileDefinition.CurrentSchemaVersion,
        "Default browser",
        BrowserProfilePersistence.DurableMetadata,
        BrowserProfilePrivacyPolicy.Strict);
}
