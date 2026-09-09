using Asura.Core;

namespace Asura.Core.Tests;

public sealed class ConnectionFileProviderProfilesTests
{
    [Fact]
    public void SshConnectionProjectsToStableSftpProvider()
    {
        var connection = new ConnectionProfile(
            new ConnectionId("production"),
            ConnectionProfile.CurrentSchemaVersion,
            "Production",
            new ConnectionEndpoint.Ssh("prod.example", username: "operator"),
            new ConnectionAuthentication.SshAgent(),
            new ConnectionStartup("/srv/app"),
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);

        var profile = ConnectionFileProviderProfiles.Create(connection);

        Assert.Equal("builtin.files.connection.production", profile.Id.Value);
        Assert.Equal("Production", profile.Name);
        var sftp = Assert.IsType<FileProviderConfiguration.Sftp>(profile.Configuration);
        Assert.Equal(connection.Id, sftp.ConnectionId);
        Assert.Equal("/srv/app", sftp.RemoteRoot);
    }

    [Fact]
    public void NonSshConnectionCannotProjectToSftp()
    {
        Assert.Throws<ArgumentException>(() =>
            ConnectionFileProviderProfiles.Create(BuiltInConnections.Local));
    }
}
