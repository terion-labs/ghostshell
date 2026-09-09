using Asura.Core;

namespace Asura.Files;

/// <summary>
/// Binds an SFTP view to the same durable SSH profile used by terminal connections. Credentials
/// remain opaque <see cref="SecretRef"/> values and host-key policy is not duplicated or weakened.
/// </summary>
public sealed record SftpFileProviderOptions
{
    public SftpFileProviderOptions(
        FileProviderProfileId profileId,
        ConnectionProfile connection,
        string remoteRoot = "/",
        RemoteMetadataReconnectPolicy reconnectPolicy = RemoteMetadataReconnectPolicy.RetryOnce)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Endpoint is not ConnectionEndpoint.Ssh)
        {
            throw new ArgumentException("An SFTP provider requires an SSH connection profile.", nameof(connection));
        }

        if (!Enum.IsDefined(reconnectPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(reconnectPolicy), reconnectPolicy, null);
        }

        ProfileId = profileId;
        Connection = connection;
        Authority = new FileAuthority(connection.Id.Value);
        RemoteRoot = remoteRoot;
        ReconnectPolicy = reconnectPolicy;
    }

    public FileProviderProfileId ProfileId { get; }

    public FileAuthority Authority { get; }

    public ConnectionProfile Connection { get; }

    public string RemoteRoot { get; }

    public RemoteMetadataReconnectPolicy ReconnectPolicy { get; }
}
