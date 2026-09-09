namespace Asura.Core;

public static class BuiltInConnections
{
    public static ConnectionProfile Local { get; } = new(
        new ConnectionId("builtin.local"),
        ConnectionProfile.CurrentSchemaVersion,
        "Local",
        new ConnectionEndpoint.Local(),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.NotApplicable);
}
