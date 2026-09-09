using Asura.Application;
using Asura.Core;
using Renci.SshNet;

namespace Asura.Infrastructure;

/// <summary>Host-key and authentication probes use the same broker as workspace traffic.</summary>
internal static class WorkspaceSshConnectionInfo
{
    public static ConnectionInfo Create(
        ConnectionEndpoint.Ssh endpoint,
        string username,
        AuthenticationMethod authentication,
        IWorkspaceNetworkConnector? connector)
    {
        if (connector is null)
        {
            return new ConnectionInfo(endpoint.Host, endpoint.Port, username, authentication);
        }

        var proxy = connector.LocalProxyEndpoint;
        return new ConnectionInfo(
            endpoint.Host, endpoint.Port, username,
            ProxyTypes.Socks5, proxy.Host, proxy.Port,
            connector.LocalProxyCredentials?.Username,
            connector.LocalProxyCredentials?.Password,
            authentication);
    }
}
