using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Files.Tests;

public sealed class SshNetBrowserTunnelFactoryTests
{
    [Fact]
    public void Browser_state_identity_is_stable_for_reconnects_but_changes_with_ssh_authority()
    {
        var id = new ConnectionId("route");
        var endpoint = new ConnectionEndpoint.Ssh("bastion.example", 22, "alice");
        var hostKey = new SshHostKeyIdentity("ssh-ed25519", "SHA256:" + new string('A', 43));
        var original = SshNetBrowserTunnelFactory.CreateProfileRouteIdentity(id, endpoint, hostKey);

        Assert.Equal(original, SshNetBrowserTunnelFactory.CreateProfileRouteIdentity(
            id, new ConnectionEndpoint.Ssh("BASTION.EXAMPLE.", 22, "alice"), hostKey));
        foreach (var changed in new[]
        {
            new ConnectionEndpoint.Ssh("other.example", 22, "alice"),
            new ConnectionEndpoint.Ssh("bastion.example", 2222, "alice"),
            new ConnectionEndpoint.Ssh("bastion.example", 22, "bob"),
        })
        {
            Assert.NotEqual(original, SshNetBrowserTunnelFactory.CreateProfileRouteIdentity(id, changed, hostKey), StringComparer.Ordinal);
        }

        Assert.NotEqual(original, SshNetBrowserTunnelFactory.CreateProfileRouteIdentity(
            id, endpoint, new SshHostKeyIdentity("ssh-ed25519", "SHA256:" + new string('B', 43))), StringComparer.Ordinal);
        Assert.NotEqual(original, SshNetBrowserTunnelFactory.CreateProfileRouteIdentity(
            new ConnectionId("another-route"), endpoint, hostKey), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Non_ssh_connections_are_rejected_before_credentials_are_used()
    {
        var factory = new SshNetBrowserTunnelFactory(null!, null!);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await factory.OpenAsync(BuiltInConnections.Local, CancellationToken.None));

        Assert.Contains("not an SSH connection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ssh_routes_require_an_explicit_username()
    {
        var factory = new SshNetBrowserTunnelFactory(null!, null!);
        var connection = new ConnectionProfile(
            new ConnectionId("browser-route"),
            ConnectionProfile.CurrentSchemaVersion,
            "Browser route",
            new ConnectionEndpoint.Ssh("bastion.example.test"),
            new ConnectionAuthentication.SshAgent(),
            ConnectionStartup.Default,
            ConnectionKeepAlive.Disabled,
            SshHostKeyPolicy.Strict);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await factory.OpenAsync(connection, CancellationToken.None));

        Assert.Contains("explicit username", exception.Message, StringComparison.Ordinal);
    }
}
