using Asura.Core;

namespace Asura.Files.Tests;

internal static class RemoteProviderTestProfiles
{
    public static SftpFileProviderOptions SftpOptions(
        SshHostKeyPolicy hostKeyPolicy = SshHostKeyPolicy.AcceptNew,
        RemoteMetadataReconnectPolicy reconnectPolicy = RemoteMetadataReconnectPolicy.RetryOnce)
    {
        var connection = new ConnectionProfile(
            new ConnectionId("sftp-fixture"),
            ConnectionProfile.CurrentSchemaVersion,
            "SFTP fixture",
            new ConnectionEndpoint.Ssh("sftp.example.test", username: "fixture"),
            new ConnectionAuthentication.None(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            hostKeyPolicy);
        return new SftpFileProviderOptions(
            new FileProviderProfileId("sftp-test"),
            connection,
            reconnectPolicy: reconnectPolicy);
    }

    public static FtpFileProviderOptions FtpOptions(
        FtpTransportSecurity security = FtpTransportSecurity.ExplicitTls,
        FtpDataConnectionMode dataMode = FtpDataConnectionMode.Passive,
        RemoteMetadataReconnectPolicy reconnectPolicy = RemoteMetadataReconnectPolicy.RetryOnce) =>
        new(
            new FileProviderProfileId("ftp-test"),
            new FileAuthority("ftp-fixture"),
            "ftp.example.test",
            "fixture",
            passwordSecret: null,
            security,
            dataMode,
            reconnectPolicy: reconnectPolicy);

    public static SmbFileProviderOptions SmbOptions(
        SmbAuthentication? authentication = null,
        RemoteMetadataReconnectPolicy reconnectPolicy = RemoteMetadataReconnectPolicy.RetryOnce) =>
        new(
            new FileProviderProfileId("smb-test"),
            new FileAuthority("smb-fixture"),
            "smb.example.test",
            "fixture-share",
            authentication ?? new SmbAuthentication.Password(
                "TEST",
                "fixture",
                new SecretRef("smb-test-password")),
            reconnectPolicy: reconnectPolicy);
}
