using System.Net;
using System.Net.Sockets;

namespace GhostShell.Application;

/// <summary>
/// Identifies the host-only network that carries all traffic for an isolated workspace.
/// Addresses are canonicalized at the CLI boundary so routing code never consumes raw text.
/// </summary>
public sealed record WorkspaceIsolationNetworkBinding
{
    public WorkspaceIsolationNetworkBinding(
        string resourceName,
        string ipv4Gateway,
        string ipv4Subnet,
        string? packetSocketPath = null,
        string? guestSocketPath = null,
        string? guestHelperPath = null,
        WorkspaceHostNetworkAttachment? hostAttachment = null,
        WorkspaceRelayNetworkAttachment? relayAttachment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        if (resourceName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A workspace isolation network name cannot contain NUL characters.",
                nameof(resourceName));
        }

        if (!IPAddress.TryParse(ipv4Gateway, out var gateway)
            || gateway.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                "A workspace isolation network requires an IPv4 gateway.",
                nameof(ipv4Gateway));
        }

        if (!IPNetwork.TryParse(ipv4Subnet, out var subnet)
            || subnet.BaseAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                "A workspace isolation network requires an IPv4 subnet.",
                nameof(ipv4Subnet));
        }

        if (!subnet.Contains(gateway))
        {
            throw new ArgumentException(
                "The workspace isolation gateway must belong to its IPv4 subnet.",
                nameof(ipv4Gateway));
        }

        var transportValues = new[]
        {
            packetSocketPath,
            guestSocketPath,
            guestHelperPath,
        };
        if (relayAttachment is not null && (hostAttachment is not null || packetSocketPath is null
            || !Path.IsPathFullyQualified(packetSocketPath) || packetSocketPath.Contains('\0', StringComparison.Ordinal)
            || guestSocketPath is not null || guestHelperPath is not null))
        {
            throw new ArgumentException("Relay networking requires only a private host packet socket and exec attachment.", nameof(relayAttachment));
        }
        if (hostAttachment is not null && transportValues.Any(static value => value is not null))
        {
            throw new ArgumentException(
                "Choose a host NIC attachment or a legacy guest packet transport, not both.",
                nameof(hostAttachment));
        }
        if (relayAttachment is null && transportValues.Any(static value => value is not null)
            && transportValues.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Workspace packet transport metadata must be supplied as a complete set.",
                nameof(packetSocketPath));
        }

        if (relayAttachment is null && packetSocketPath is not null
            && (!Path.IsPathFullyQualified(packetSocketPath)
                || !Path.IsPathFullyQualified(guestSocketPath!)
                || !Path.IsPathFullyQualified(guestHelperPath!)
                || transportValues.Any(static value => value!.Contains('\0', StringComparison.Ordinal))))
        {
            throw new ArgumentException(
                "Workspace packet transport paths must be absolute and cannot contain NUL characters.",
                nameof(packetSocketPath));
        }

        ResourceName = resourceName.Trim();
        Ipv4Gateway = gateway.ToString();
        Ipv4Subnet = subnet.ToString();
        PacketSocketPath = hostAttachment?.PacketSocketPath ?? packetSocketPath;
        GuestSocketPath = guestSocketPath;
        GuestHelperPath = guestHelperPath;
        HostAttachment = hostAttachment;
        RelayAttachment = relayAttachment;
    }

    public string ResourceName { get; }

    public string Ipv4Gateway { get; }

    public string Ipv4Subnet { get; }

    /// <summary>The absolute host Unix socket published into the workspace guest.</summary>
    public string? PacketSocketPath { get; }

    /// <summary>The absolute Unix socket path visible to the guest packet router.</summary>
    public string? GuestSocketPath { get; }

    /// <summary>The absolute read-only helper executable path visible inside the guest.</summary>
    public string? GuestHelperPath { get; }

    /// <summary>A host-owned virtual NIC; no GhostShell networking process runs in the guest.</summary>
    public WorkspaceHostNetworkAttachment? HostAttachment { get; }

    public WorkspaceRelayNetworkAttachment? RelayAttachment { get; }
}
