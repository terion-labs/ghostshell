using System.Text.Json.Serialization;

namespace Asura.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(NetworkConnectionConfiguration.Proxy), "proxy")]
[JsonDerivedType(typeof(NetworkConnectionConfiguration.WireGuard), "wireguard")]
[JsonDerivedType(typeof(NetworkConnectionConfiguration.OpenVpn), "openvpn")]
[JsonDerivedType(typeof(NetworkConnectionConfiguration.AnyConnect), "anyconnect")]
[JsonDerivedType(typeof(NetworkConnectionConfiguration.Tailscale), "tailscale")]
public abstract record NetworkConnectionConfiguration
{
    private NetworkConnectionConfiguration()
    {
    }

    [JsonIgnore]
    public abstract NetworkConnectionKind Kind { get; }

    public sealed record Proxy : NetworkConnectionConfiguration
    {
        [JsonConstructor]
        public Proxy(
            NetworkProxyProtocol protocol,
            string host,
            int port,
            string? username = null,
            SecretRef? passwordSecret = null)
        {
            if (!Enum.IsDefined(protocol))
            {
                throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null);
            }

            Protocol = protocol;
            Host = NormalizeHost(host);
            Port = RequirePort(port);
            Username = NormalizeOptional(username);
            if (passwordSecret is { } secret)
            {
                RuntimeId.Require(secret.Value, nameof(passwordSecret));
                if (Username is null)
                {
                    throw new ArgumentException(
                        "A proxy password requires a username.",
                        nameof(passwordSecret));
                }
            }

            PasswordSecret = passwordSecret;
        }

        public NetworkProxyProtocol Protocol { get; }

        public string Host { get; }

        public int Port { get; }

        public string? Username { get; }

        public SecretRef? PasswordSecret { get; }

        [JsonIgnore]
        public Uri Endpoint => new($"{SchemeFor(Protocol)}://{HostForUri(Host)}:{Port}");

        public override NetworkConnectionKind Kind => NetworkConnectionKind.Proxy;
    }

    public sealed record WireGuard : NetworkConnectionConfiguration
    {
        [JsonConstructor]
        public WireGuard(SecretRef configurationSecret)
        {
            RuntimeId.Require(configurationSecret.Value, nameof(configurationSecret));
            ConfigurationSecret = configurationSecret;
        }

        public SecretRef ConfigurationSecret { get; }

        public override NetworkConnectionKind Kind => NetworkConnectionKind.WireGuard;
    }

    public sealed record OpenVpn : NetworkConnectionConfiguration
    {
        [JsonConstructor]
        public OpenVpn(
            SecretRef configurationSecret,
            string? username = null,
            SecretRef? passwordSecret = null)
        {
            RuntimeId.Require(configurationSecret.Value, nameof(configurationSecret));
            ConfigurationSecret = configurationSecret;
            Username = NormalizeOptional(username);
            PasswordSecret = RequireOptionalSecret(passwordSecret, nameof(passwordSecret));
            if (PasswordSecret is not null && Username is null)
            {
                throw new ArgumentException("An OpenVPN password requires a username.", nameof(passwordSecret));
            }
        }

        public SecretRef ConfigurationSecret { get; }

        public string? Username { get; }

        public SecretRef? PasswordSecret { get; }

        public override NetworkConnectionKind Kind => NetworkConnectionKind.OpenVpn;
    }

    public sealed record AnyConnect : NetworkConnectionConfiguration
    {
        [JsonConstructor]
        public AnyConnect(
            Uri gateway,
            string? username = null,
            SecretRef? passwordSecret = null,
            string? authenticationGroup = null,
            SecretRef? clientCertificateSecret = null)
        {
            Gateway = RequireHttpsUri(gateway, nameof(gateway));
            Username = NormalizeOptional(username);
            AuthenticationGroup = NormalizeOptional(authenticationGroup);
            PasswordSecret = RequireOptionalSecret(passwordSecret, nameof(passwordSecret));
            ClientCertificateSecret = RequireOptionalSecret(
                clientCertificateSecret,
                nameof(clientCertificateSecret));
        }

        public Uri Gateway { get; }

        public string? Username { get; }

        public SecretRef? PasswordSecret { get; }

        public string? AuthenticationGroup { get; }

        public SecretRef? ClientCertificateSecret { get; }

        public override NetworkConnectionKind Kind => NetworkConnectionKind.AnyConnect;
    }

    public sealed record Tailscale : NetworkConnectionConfiguration
    {
        [JsonConstructor]
        public Tailscale(
            string exitNode,
            Uri? controlServer = null,
            SecretRef? authKeySecret = null)
        {
            ExitNode = RuntimeId.Require(exitNode, nameof(exitNode)).Trim();
            ControlServer = controlServer is null
                ? null
                : RequireHttpsUri(controlServer, nameof(controlServer));
            AuthKeySecret = RequireOptionalSecret(authKeySecret, nameof(authKeySecret));
        }

        public string ExitNode { get; }

        public Uri? ControlServer { get; }

        public SecretRef? AuthKeySecret { get; }

        public override NetworkConnectionKind Kind => NetworkConnectionKind.Tailscale;
    }

    private static int RequirePort(int port) => port is >= 1 and <= 65_535
        ? port
        : throw new ArgumentOutOfRangeException(
            nameof(port),
            port,
            "A network proxy port must be between 1 and 65535.");

    private static string NormalizeHost(string value)
    {
        var host = RuntimeId.Require(value, nameof(value)).Trim();
        if (host.Length > 253
            || host.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || host.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("A network proxy host is invalid.", nameof(value));
        }

        return host;
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("A network setting cannot contain control characters.", nameof(value));
        }

        return normalized;
    }

    private static SecretRef? RequireOptionalSecret(SecretRef? value, string parameterName)
    {
        if (value is { } secret)
        {
            RuntimeId.Require(secret.Value, parameterName);
        }

        return value;
    }

    private static Uri RequireHttpsUri(Uri value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!value.IsAbsoluteUri
            || !string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(value.UserInfo))
        {
            throw new ArgumentException(
                "A network gateway must use an absolute HTTPS URL without embedded credentials.",
                parameterName);
        }

        return value;
    }

    private static string SchemeFor(NetworkProxyProtocol protocol) => protocol switch
    {
        NetworkProxyProtocol.Socks5 => "socks5",
        NetworkProxyProtocol.Http => Uri.UriSchemeHttp,
        NetworkProxyProtocol.Https => Uri.UriSchemeHttps,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };

    private static string HostForUri(string host) =>
        host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
}
