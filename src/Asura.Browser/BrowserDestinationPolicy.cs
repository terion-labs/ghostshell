using System.Net;
using System.Net.Sockets;
using Asura.Application;

namespace Asura.Browser;

/// <summary>
/// Restricts model-governed navigation. SSH-routed browsers have no remote-peer
/// attestation or separately approved private-network capability, so governed
/// HTTP traffic on that route fails closed.
/// </summary>
internal sealed class BrowserDestinationPolicy
{
    private readonly BrowserNetworkRouteKind _routeKind;
    private readonly Func<
        string,
        CancellationToken,
        ValueTask<IPAddress[]>>? _resolveHost;

    private BrowserDestinationPolicy(
        BrowserNetworkRouteKind routeKind,
        Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolveHost)
    {
        _routeKind = routeKind;
        _resolveHost = resolveHost;
    }

    public static BrowserDestinationPolicy LocalSystem { get; } =
        CreateLocal(ResolveSystemAsync);

    public static BrowserDestinationPolicy SshRouted { get; } =
        new(BrowserNetworkRouteKind.SshRouted, resolveHost: null);

    public static BrowserDestinationPolicy ForRoute(
        BrowserNetworkRouteKind routeKind) => routeKind switch
        {
            BrowserNetworkRouteKind.Local => LocalSystem,
            BrowserNetworkRouteKind.SshRouted => SshRouted,
            _ => throw new ArgumentOutOfRangeException(
                nameof(routeKind),
                routeKind,
                null),
        };

    internal static BrowserDestinationPolicy CreateLocal(
        Func<string, CancellationToken, ValueTask<IPAddress[]>> resolveHost) =>
        new(
            BrowserNetworkRouteKind.Local,
            resolveHost ?? throw new ArgumentNullException(nameof(resolveHost)));

    /// <summary>
    /// Resolves an explicit local-route destination before native dispatch.
    /// Every returned address must be globally routable; resolution failure is
    /// a policy denial, not permission to let Chromium choose a hidden target.
    /// </summary>
    public async ValueTask<bool> AllowsResolvedAsync(
        BrowserAddress address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!AllowsNavigationStart(address))
        {
            return false;
        }

        if (!IsHttp(address.Value))
        {
            return true;
        }

        if (_routeKind is BrowserNetworkRouteKind.SshRouted)
        {
            return false;
        }

        var host = address.Value.IdnHost;
        if (IPAddress.TryParse(CanonicalHost(address.Value), out _))
        {
            return true;
        }

        IPAddress[] resolved;
        try
        {
            resolved = await _resolveHost!(host, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        return resolved.Length > 0 && resolved.All(IsPublicAddress);
    }

    /// <summary>
    /// Synchronous navigation admission rejects destinations that cannot be
    /// governed at the route's actual trust boundary.
    /// </summary>
    public bool AllowsNavigationStart(BrowserAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsHttp(address.Value))
        {
            return true;
        }

        if (_routeKind is BrowserNetworkRouteKind.SshRouted)
        {
            return false;
        }

        var host = CanonicalHost(address.Value);
        if (IsLocalHostname(host))
        {
            return false;
        }

        return !IPAddress.TryParse(host, out var literal)
            || IsPublicAddress(literal);
    }

    /// <summary>
    /// CEF currently exposes URL admission but not the actual socket peer.
    /// Network requests therefore fail closed at the native request callback;
    /// non-network internal documents retain their existing policy.
    /// </summary>
    public ValueTask<bool> AllowsCefTransportAsync(
        BrowserAddress address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            !IsHttp(address.Value) && AllowsNavigationStart(address));
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicAddress(address.MapToIPv4());
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIpv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIpv6(address),
            _ => false,
        };
    }

    private static bool IsPublicIpv4(byte[] address)
    {
        var first = address[0];
        var second = address[1];
        return first is not (0 or 10 or 127)
            && first < 224
            && !(first == 100 && second is >= 64 and <= 127)
            && !(first == 169 && second == 254)
            && !(first == 172 && second is >= 16 and <= 31)
            && !(first == 192 && second == 168)
            && !(first == 192 && second == 0 && address[2] == 0)
            && !(first == 192 && second == 88 && address[2] == 99)
            && !(first == 198 && second is 18 or 19)
            && !(first == 192 && second == 0 && address[2] == 2)
            && !(first == 198 && second == 51 && address[2] == 100)
            && !(first == 203 && second == 0 && address[2] == 113);
    }

    private static bool IsPublicIpv6(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return false;
        }

        // IPv4-compatible, documentation, benchmark, and 6to4 ranges are not
        // stable globally-routable destinations for this authorization boundary.
        return !bytes[..12].All(value => value == 0)
            && !(bytes[0] == 0x20
                && bytes[1] == 0x01
                && bytes[2] == 0x0D
                && bytes[3] == 0xB8)
            && !(bytes[0] == 0x20
                && bytes[1] == 0x01
                && bytes[2] == 0x00
                && bytes[3] == 0x02)
            && !(bytes[0] == 0x20 && bytes[1] == 0x02);
    }

    private static string CanonicalHost(Uri address) =>
        address.IdnHost.TrimEnd('.');

    private static bool IsHttp(Uri address) =>
        address.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || address.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalHostname(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<IPAddress[]> ResolveSystemAsync(
        string host,
        CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}

internal enum BrowserNetworkRouteKind
{
    Local,
    SshRouted,
}
