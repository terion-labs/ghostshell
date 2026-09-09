using System.Text.Json;
using Asura.Core;

namespace Asura.Core.Tests;

public sealed class FileProviderProfileTests
{
    [Fact]
    public void RemoteCredentialsAreRepresentedOnlyByOpaqueSecretReferences()
    {
        var reference = new SecretRef("vault-file-password");
        var profile = new FileProviderProfile(
            new FileProviderProfileId("files.production"),
            FileProviderProfile.CurrentSchemaVersion,
            "Production WebDAV",
            new FileProviderConfiguration.WebDav(
                new Uri("https://files.example.test/root/"),
                "operator",
                reference));

        var json = JsonSerializer.Serialize(profile);

        Assert.Contains(reference.Value, json, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordValue", json, StringComparison.Ordinal);
        Assert.Equal(FileProviderKind.WebDav, profile.ProviderKind);
    }

    [Fact]
    public void InsecureTransportsRequireExplicitAcknowledgement()
    {
        Assert.Throws<ArgumentException>(() => new FileProviderConfiguration.WebDav(
            new Uri("http://files.example.test/")));
        Assert.Throws<ArgumentException>(() => new FileProviderConfiguration.Ftp(
            "ftp.example.test",
            21,
            username: null,
            passwordSecret: null,
            FtpSecurityMode.Plaintext,
            FtpConnectionMode.AutoPassive));

        var acknowledged = new FileProviderConfiguration.Ftp(
            "ftp.example.test",
            21,
            username: null,
            passwordSecret: null,
            FtpSecurityMode.Plaintext,
            FtpConnectionMode.AutoPassive,
            allowPlaintext: true);

        Assert.True(acknowledged.AllowPlaintext);
    }

    [Fact]
    public void EmbeddedUriCredentialsAndPartialCredentialsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new FileProviderConfiguration.WebDav(
            new Uri("https://user:password@files.example.test/")));
        Assert.Throws<ArgumentException>(() => new FileProviderConfiguration.Smb(
            "server",
            "share",
            SmbCredentialMode.UsernamePassword,
            username: "operator"));
        Assert.Throws<ArgumentException>(() => new FileProviderConfiguration.Ftp(
            "ftp.example.test",
            990,
            "operator",
            passwordSecret: null,
            FtpSecurityMode.ImplicitTls,
            FtpConnectionMode.Passive));
    }

    [Fact]
    public void SftpConfigurationKeepsAStableConnectionReference()
    {
        var configuration = new FileProviderConfiguration.Sftp(
            new ConnectionId("ssh-production"),
            "/srv/releases");

        Assert.Equal(new ConnectionId("ssh-production"), configuration.ConnectionId);
        Assert.Equal("/srv/releases", configuration.RemoteRoot);
        Assert.Throws<ArgumentException>(() => new FileProviderConfiguration.Sftp(
            new ConnectionId("ssh-production"),
            "relative/path"));
    }
}
