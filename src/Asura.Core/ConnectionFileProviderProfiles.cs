namespace Asura.Core;

/// <summary>
/// Deterministically projects a saved SSH connection into its transient SFTP
/// provider identity. Recovery can therefore restore the binding without
/// persisting a redundant file-provider definition.
/// </summary>
public static class ConnectionFileProviderProfiles
{
    private const string Prefix = "builtin.files.connection.";

    public static FileProviderProfileId Id(ConnectionId connectionId) =>
        new(Prefix + connectionId.Value);

    public static FileProviderProfile Create(ConnectionProfile connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Endpoint is not ConnectionEndpoint.Ssh)
        {
            throw new ArgumentException(
                "Only SSH connections expose a connection-backed SFTP provider.",
                nameof(connection));
        }

        return new FileProviderProfile(
            Id(connection.Id),
            FileProviderProfile.CurrentSchemaVersion,
            connection.Name,
            new FileProviderConfiguration.Sftp(
                connection.Id,
                connection.Startup.Directory ?? "/"));
    }
}
