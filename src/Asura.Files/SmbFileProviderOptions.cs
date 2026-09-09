namespace Asura.Files;

/// <summary>Bounded SMB 2/3 share connection policy.</summary>
public sealed record SmbFileProviderOptions
{
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(15);

    public SmbFileProviderOptions(
        FileProviderProfileId profileId,
        FileAuthority authority,
        string server,
        string share,
        SmbAuthentication authentication,
        string remoteRoot = "/",
        TimeSpan? responseTimeout = null,
        RemoteMetadataReconnectPolicy reconnectPolicy = RemoteMetadataReconnectPolicy.RetryOnce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(share);
        ArgumentNullException.ThrowIfNull(authentication);
        ValidateServerOrShare(server, nameof(server), 255);
        ValidateServerOrShare(share, nameof(share), 255);
        if (!Enum.IsDefined(reconnectPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(reconnectPolicy), reconnectPolicy, null);
        }

        var resolvedTimeout = responseTimeout ?? DefaultResponseTimeout;
        if (resolvedTimeout < TimeSpan.FromSeconds(1)
            || resolvedTimeout > TimeSpan.FromMinutes(2)
            || resolvedTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(responseTimeout),
                resolvedTimeout,
                "The SMB response timeout must be between one second and two minutes.");
        }

        ProfileId = profileId;
        Authority = authority;
        Server = server;
        Share = share;
        Authentication = authentication;
        RemoteRoot = remoteRoot;
        ResponseTimeout = resolvedTimeout;
        ReconnectPolicy = reconnectPolicy;
    }

    public FileProviderProfileId ProfileId { get; }

    public FileAuthority Authority { get; }

    public string Server { get; }

    public string Share { get; }

    public SmbAuthentication Authentication { get; }

    public string RemoteRoot { get; }

    public TimeSpan ResponseTimeout { get; }

    public RemoteMetadataReconnectPolicy ReconnectPolicy { get; }

    public override string ToString() =>
        $"SMB provider {ProfileId.Value} ({Authority.Value}) for {Server}/{Share} using {Authentication}";

    private static void ValidateServerOrShare(string value, string parameterName, int maximumLength)
    {
        if (value.Length > maximumLength
            || value.Any(character => character is '/' or '\\' || char.IsControl(character)))
        {
            throw new ArgumentException(
                "An SMB server or share must be bounded and contain no path separators or control characters.",
                parameterName);
        }
    }
}
