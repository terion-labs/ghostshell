using System.Net;
using System.Text;

namespace Asura.Application;

/// <summary>
/// A pre-authenticated host route, not a VPN definition. Its credentials are sent only
/// to the owned native gateway over stdin, and names resolve at the SOCKS destination.
/// </summary>
public sealed class WorkspacePacketGatewayServiceProxy
{
    public WorkspacePacketGatewayServiceProxy(Uri endpoint, WorkspaceNetworkProxyCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credentials);
        if (!endpoint.IsAbsoluteUri || !string.Equals(endpoint.Scheme, "socks5", StringComparison.Ordinal)
            || !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address)
            || endpoint.Port is <= 0 or > ushort.MaxValue || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || !string.Equals(endpoint.AbsolutePath, "/", StringComparison.Ordinal))
        {
            throw new ArgumentException("A service gateway requires a credential-free loopback SOCKS5 endpoint.", nameof(endpoint));
        }
        if (Encoding.UTF8.GetByteCount(credentials.Username) is < 1 or > byte.MaxValue
            || Encoding.UTF8.GetByteCount(credentials.Password) is < 1 or > byte.MaxValue)
        {
            throw new ArgumentException("SOCKS credentials must contain 1–255 UTF-8 bytes.", nameof(credentials));
        }
        Endpoint = endpoint;
        Credentials = credentials;
    }

    public Uri Endpoint { get; }

    public WorkspaceNetworkProxyCredentials Credentials { get; }
}
