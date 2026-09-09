using System.Text;
using Asura.Core;

namespace Asura.Files;

/// <summary>Explicit FTP/FTPS connection policy. There is deliberately no TLS auto-downgrade mode.</summary>
public sealed record FtpFileProviderOptions
{
    private readonly Encoding _controlEncoding;

    public FtpFileProviderOptions(
        FileProviderProfileId profileId,
        FileAuthority authority,
        string host,
        string username,
        SecretRef? passwordSecret,
        FtpTransportSecurity transportSecurity,
        FtpDataConnectionMode dataConnectionMode = FtpDataConnectionMode.Passive,
        int? port = null,
        string remoteRoot = "/",
        string encodingWebName = "utf-8",
        RemoteMetadataReconnectPolicy reconnectPolicy = RemoteMetadataReconnectPolicy.RetryOnce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(encodingWebName);
        if (host.Length > 255 || host.Any(char.IsControl))
        {
            throw new ArgumentException("An FTP host must be bounded and printable.", nameof(host));
        }

        if (username.Length > 256 || username.Any(char.IsControl))
        {
            throw new ArgumentException("An FTP username must be bounded and printable.", nameof(username));
        }

        if (!Enum.IsDefined(transportSecurity))
        {
            throw new ArgumentOutOfRangeException(nameof(transportSecurity), transportSecurity, null);
        }

        if (!Enum.IsDefined(dataConnectionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(dataConnectionMode), dataConnectionMode, null);
        }

        if (!Enum.IsDefined(reconnectPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(reconnectPolicy), reconnectPolicy, null);
        }

        var resolvedPort = port ?? (transportSecurity == FtpTransportSecurity.ImplicitTls ? 990 : 21);
        if (resolvedPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), resolvedPort, "An FTP port must be between 1 and 65535.");
        }

        Encoding encoding;
        try
        {
            var requestedEncoding = Encoding.GetEncoding(encodingWebName);
            encoding = Encoding.GetEncoding(
                requestedEncoding.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The configured FTP control-channel encoding is unavailable.", nameof(encodingWebName), exception);
        }

        if (!CanRoundTrip(encoding, username, maximumCharacters: 256, maximumBytes: 4_096))
        {
            throw new ArgumentException(
                "The FTP username cannot be represented by the configured control-channel encoding.",
                nameof(username));
        }

        ProfileId = profileId;
        Authority = authority;
        Host = host.Trim();
        Port = resolvedPort;
        Username = username;
        PasswordSecret = passwordSecret;
        TransportSecurity = transportSecurity;
        DataConnectionMode = dataConnectionMode;
        RemoteRoot = remoteRoot;
        EncodingWebName = encoding.WebName;
        _controlEncoding = encoding;
        ReconnectPolicy = reconnectPolicy;
    }

    public FileProviderProfileId ProfileId { get; }

    public FileAuthority Authority { get; }

    public string Host { get; }

    public int Port { get; }

    public string Username { get; }

    public SecretRef? PasswordSecret { get; }

    public FtpTransportSecurity TransportSecurity { get; }

    public FtpDataConnectionMode DataConnectionMode { get; }

    public string RemoteRoot { get; }

    public string EncodingWebName { get; }

    internal Encoding ControlEncoding => _controlEncoding;

    internal bool CanEncodeName(string value) =>
        CanRoundTrip(_controlEncoding, value, maximumCharacters: 1_024, maximumBytes: 4_096);

    internal bool CanEncodeCredential(string value) =>
        CanRoundTrip(_controlEncoding, value, maximumCharacters: 4_096, maximumBytes: 16_384);

    public RemoteMetadataReconnectPolicy ReconnectPolicy { get; }

    private static bool CanRoundTrip(
        Encoding encoding,
        string value,
        int maximumCharacters,
        int maximumBytes)
    {
        if (value.Length > maximumCharacters)
        {
            return false;
        }

        try
        {
            if (encoding.GetByteCount(value) > maximumBytes)
            {
                return false;
            }

            var encoded = encoding.GetBytes(value);
            return string.Equals(encoding.GetString(encoded), value, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
