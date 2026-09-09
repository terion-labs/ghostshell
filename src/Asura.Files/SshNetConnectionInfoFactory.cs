using Asura.Application;
using Asura.Core;
using Renci.SshNet;

namespace Asura.Files;

internal static class SshNetConnectionInfoFactory
{
    public static ConnectionInfo Create(
        ConnectionEndpoint.Ssh endpoint,
        string username,
        AuthenticationMethod authentication,
        IWorkspaceNetworkConnector? networkConnector)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(authentication);
        if (networkConnector is null)
        {
            return new ConnectionInfo(
                endpoint.Host,
                endpoint.Port,
                username,
                authentication);
        }

        var proxy = networkConnector.LocalProxyEndpoint;
        var proxyCredentials = networkConnector.LocalProxyCredentials;
        return new ConnectionInfo(
            endpoint.Host,
            endpoint.Port,
            username,
            ProxyTypes.Socks5,
            proxy.Host,
            proxy.Port,
            proxyUsername: proxyCredentials?.Username,
            proxyPassword: proxyCredentials?.Password,
            authentication);
    }
}
