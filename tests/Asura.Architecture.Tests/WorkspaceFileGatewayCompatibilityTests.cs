using Asura.ConnectionBackend;
using Asura.Core;
using Asura.Files;
using Asura.Infrastructure;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceFileGatewayCompatibilityTests
{
    [Fact]
    public void Active_ftp_fails_before_dispatch_with_passive_mode_guidance_in_a_guest()
    {
        var failure = Assert.Throws<NotSupportedException>(() => WorkspaceFileProvider.ValidateExecutionLocation(
            Profile(FtpConnectionMode.Active), BackendExecutionLocation.Guest));
        Assert.Contains("Passive", failure.Message, StringComparison.Ordinal);
        Assert.Contains("inbound", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Active_ftp_preserves_existing_host_direct_support() => WorkspaceFileProvider.ValidateExecutionLocation(
        Profile(FtpConnectionMode.Active), BackendExecutionLocation.HostDirect);

    [Theory]
    [InlineData(FtpConnectionMode.Passive)]
    [InlineData(FtpConnectionMode.AutoPassive)]
    public async Task Passive_ftp_still_creates_a_lazy_guest_provider(FtpConnectionMode mode)
    {
        var vault = new InMemorySecretVault();
        var factory = new WorkspaceFileProviderFactory(vault, new InMemorySftpKnownHostStore(), null,
            _ => throw new InvalidOperationException("Provider creation must remain lazy."));
        using var registration = await factory.CreateAsync(Profile(mode), new Dictionary<ConnectionId, ConnectionProfile>(), CancellationToken.None);
        Assert.IsType<WorkspaceFileProvider>(registration.Registration.Provider);
    }

    private static FileProviderProfile Profile(FtpConnectionMode mode) => new(new("ftp-fixture"), 1, "FTP fixture",
        new FileProviderConfiguration.Ftp("ftp.private", 21, null, null, FtpSecurityMode.ExplicitTls, mode));
}
