using Asura.Application;
using Asura.ConnectionBackend;
using Asura.Core;
using Asura.Files;
using Asura.Infrastructure;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceRemoteFileRouteBoundaryTests
{
    [Theory]
    [InlineData(FileProviderKind.Smb)]
    [InlineData(FileProviderKind.Ftp)]
    [InlineData(FileProviderKind.S3)]
    [InlineData(FileProviderKind.WebDav)]
    public async Task Blocked_backend_never_opens_a_host_adapter(FileProviderKind kind)
    {
        using var vault = new InMemorySecretVault();
        var launches = 0;
        var factory = new WorkspaceFileProviderFactory(vault, new InMemorySftpKnownHostStore(), null,
            _ => { launches++; throw new WorkspaceNetworkBlockedException(); });
        var profile = new FileProviderProfile(new("remote-fixture"), 1, "Remote fixture", Configuration(kind));
        using var registration = await factory.CreateAsync(profile, new Dictionary<ConnectionId, ConnectionProfile>(), CancellationToken.None);
        Assert.Equal(0, launches);
        var provider = registration.Registration.Provider;
        Assert.IsType<WorkspaceFileProvider>(provider);
        await Assert.ThrowsAsync<WorkspaceNetworkBlockedException>(async () =>
            await provider.StatAsync(new(registration.Registration.Root), CancellationToken.None));
        Assert.Equal(1, launches);
    }

    private static FileProviderConfiguration Configuration(FileProviderKind kind) => kind switch
    {
        FileProviderKind.Smb => new FileProviderConfiguration.Smb("files.synthetic.invalid", "fixture", SmbCredentialMode.Guest),
        FileProviderKind.Ftp => new FileProviderConfiguration.Ftp("files.synthetic.invalid", 21, null, null,
            FtpSecurityMode.ExplicitTls, FtpConnectionMode.Passive),
        FileProviderKind.S3 => new FileProviderConfiguration.S3("fixture", serviceUri: new Uri("https://files.synthetic.invalid")),
        FileProviderKind.WebDav => new FileProviderConfiguration.WebDav(new Uri("https://files.synthetic.invalid")),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
