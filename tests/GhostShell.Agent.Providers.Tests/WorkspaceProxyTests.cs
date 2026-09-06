using System.Net;

namespace GhostShell.Agent.Providers.Tests;

public sealed class WorkspaceProxyTests
{
    [Fact]
    public void Routed_provider_extracts_credentials_from_proxy_address()
    {
        var proxy = CatalogAiProviderRuntime.CreateWebProxy(
            new Uri(
                "socks5://workspace:secret@127.0.0.1:45123",
                UriKind.Absolute));
        var credentials = Assert.IsAssignableFrom<ICredentials>(proxy.Credentials)
            .GetCredential(proxy.Address!, "basic");

        Assert.Equal("socks5://127.0.0.1:45123/", proxy.Address!.AbsoluteUri);
        Assert.NotNull(credentials);
        Assert.Equal("workspace", credentials.UserName);
        Assert.Equal("secret", credentials.Password);
    }
}
