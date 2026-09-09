using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;

namespace Asura.Application;

/// <summary>
/// The network state the guest applies to its TUN device after the host accepts
/// the authenticated packet channel. The default routes are derived from the
/// configured address families and always point at <see cref="InterfaceName"/>.
/// </summary>
public sealed class WorkspaceGuestNetworkConfiguration
{
    private const int LinuxInterfaceNameMaximumLength = 15;
    private const int MaximumDnsServerCount = 4;

    public WorkspaceGuestNetworkConfiguration(
        string interfaceName,
        int mtu,
        IPAddress? ipv4Address,
        int? ipv4PrefixLength,
        IPAddress? ipv6Address,
        int? ipv6PrefixLength,
        IReadOnlyList<IPAddress> dnsServers)
    {
        ValidateInterfaceName(interfaceName);
        ValidateAddress(ipv4Address, ipv4PrefixLength, AddressFamily.InterNetwork, 32);
        ValidateAddress(ipv6Address, ipv6PrefixLength, AddressFamily.InterNetworkV6, 128);

        if (ipv4Address is null && ipv6Address is null)
        {
            throw new ArgumentException(
                "A guest TUN configuration requires an IPv4 or IPv6 address.",
                nameof(ipv4Address));
        }

        var minimumMtu = ipv6Address is null ? 576 : 1280;
        if (mtu is < 1 or > ushort.MaxValue || mtu < minimumMtu)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mtu),
                $"The TUN MTU must be between {minimumMtu} and {ushort.MaxValue} bytes.");
        }

        ArgumentNullException.ThrowIfNull(dnsServers);
        if (dnsServers.Count is < 1 or > MaximumDnsServerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dnsServers),
                $"Supply between 1 and {MaximumDnsServerCount} DNS servers.");
        }

        var copiedDnsServers = dnsServers.Select(CopyAddress).ToArray();
        foreach (var dnsServer in copiedDnsServers)
        {
            var configured = dnsServer.AddressFamily switch
            {
                AddressFamily.InterNetwork => ipv4Address is not null,
                AddressFamily.InterNetworkV6 => ipv6Address is not null,
                _ => false,
            };
            if (!configured)
            {
                throw new ArgumentException(
                    "Each DNS server must use an address family configured on the guest TUN device.",
                    nameof(dnsServers));
            }
        }

        InterfaceName = interfaceName;
        Mtu = mtu;
        Ipv4Address = CopyOptionalAddress(ipv4Address);
        Ipv4PrefixLength = ipv4PrefixLength;
        Ipv6Address = CopyOptionalAddress(ipv6Address);
        Ipv6PrefixLength = ipv6PrefixLength;
        DnsServers = new ReadOnlyCollection<IPAddress>(copiedDnsServers);

        var routes = new List<IPNetwork>(2);
        if (Ipv4Address is not null)
        {
            routes.Add(new IPNetwork(IPAddress.Any, 0));
        }

        if (Ipv6Address is not null)
        {
            routes.Add(new IPNetwork(IPAddress.IPv6Any, 0));
        }

        DefaultRoutes = new ReadOnlyCollection<IPNetwork>(routes);
    }

    public string InterfaceName { get; }

    public int Mtu { get; }

    public IPAddress? Ipv4Address { get; }

    public int? Ipv4PrefixLength { get; }

    public IPAddress? Ipv6Address { get; }

    public int? Ipv6PrefixLength { get; }

    public IReadOnlyList<IPAddress> DnsServers { get; }

    public IReadOnlyList<IPNetwork> DefaultRoutes { get; }

    private static void ValidateInterfaceName(string interfaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceName);
        if (interfaceName.Length > LinuxInterfaceNameMaximumLength
            || interfaceName is "." or ".."
            || interfaceName.Any(character => !IsInterfaceNameCharacter(character)))
        {
            throw new ArgumentException(
                "The TUN interface name must be 1-15 ASCII letters, digits, '.', '_' or '-'.",
                nameof(interfaceName));
        }
    }

    private static bool IsInterfaceNameCharacter(char character) =>
        character is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9'
        or '.'
        or '_'
        or '-';

    private static void ValidateAddress(
        IPAddress? address,
        int? prefixLength,
        AddressFamily expectedFamily,
        int maximumPrefixLength)
    {
        if ((address is null) != (prefixLength is null))
        {
            throw new ArgumentException(
                "An address and its prefix length must either both be set or both be absent.",
                nameof(prefixLength));
        }

        if (address is null)
        {
            return;
        }

        if (address.AddressFamily != expectedFamily)
        {
            throw new ArgumentException(
                $"Expected a {expectedFamily} address.",
                nameof(address));
        }

        if (prefixLength is < 1 || prefixLength > maximumPrefixLength)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }
    }

    private static IPAddress CopyAddress(IPAddress address) =>
        new(address.GetAddressBytes());

    private static IPAddress? CopyOptionalAddress(IPAddress? address) =>
        address is null ? null : CopyAddress(address);
}
