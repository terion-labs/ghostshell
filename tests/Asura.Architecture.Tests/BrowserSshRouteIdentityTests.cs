using Asura.Application;
using Asura.Core;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class BrowserSshRouteIdentityTests
{
    [Fact]
    public void Live_route_cache_tracks_security_edits_but_not_presentation_edits()
    {
        var endpoint = new ConnectionEndpoint.Ssh("bastion.example", 22, "alice");
        var authentication = new ConnectionAuthentication.Password(new SecretRef("credential-one"));
        var profile = BrowserProfileBinding.Legacy(BrowserProfileKey.Global).Selection;
        var original = Key(endpoint, authentication, SshHostKeyPolicy.Strict);

        Assert.Equal(original, Key(endpoint, authentication, SshHostKeyPolicy.Strict, "Renamed"));
        Assert.NotEqual(original, Key(new ConnectionEndpoint.Ssh("other.example", 22, "alice"), authentication, SshHostKeyPolicy.Strict));
        Assert.NotEqual(original, Key(new ConnectionEndpoint.Ssh("bastion.example", 2222, "alice"), authentication, SshHostKeyPolicy.Strict));
        Assert.NotEqual(original, Key(new ConnectionEndpoint.Ssh("bastion.example", 22, "bob"), authentication, SshHostKeyPolicy.Strict));
        Assert.NotEqual(original, Key(endpoint, new ConnectionAuthentication.Password(new SecretRef("credential-two")), SshHostKeyPolicy.Strict));
        Assert.NotEqual(original, Key(endpoint, authentication, SshHostKeyPolicy.AcceptNew));

        DesktopBrowserRendererViewFactory.RemoteRouteKey Key(
            ConnectionEndpoint selectedEndpoint,
            ConnectionAuthentication selectedAuthentication,
            SshHostKeyPolicy policy,
            string name = "Browser route") => DesktopBrowserRendererViewFactory.CreateRemoteRouteKey(
                profile,
                new ConnectionProfile(new ConnectionId("route"), 1, name,
                    selectedEndpoint, selectedAuthentication, ConnectionStartup.Default,
                    ConnectionKeepAlive.Disabled, policy),
                null);
    }
}
