using GhostShell.Core;

namespace GhostShell.Application;

[Flags]
public enum WorkspaceIpAddressFamilies
{
    None = 0,
    Ipv4 = 1 << 0,
    Ipv6 = 1 << 1,
}

[Flags]
public enum WorkspaceIpProtocolCapabilities
{
    None = 0,
    Tcp = 1 << 0,
    Udp = 1 << 1,
    ControlMessages = 1 << 2,
    Other = 1 << 3,
    All = Tcp | Udp | ControlMessages | Other,
}

/// <summary>
/// Describes what a host packet route actually carries. A workspace route is usable when it
/// carries TCP for at least one negotiated address family. TCP-only routes translate DNS
/// through the selected upstream and explicitly report their UDP limitation. Families unavailable from
/// the selected upstream remain unrouted, so they cannot bypass the workspace boundary.
/// </summary>
public sealed record WorkspacePacketRouteCapabilities
{
    public const int MinimumIpv4Mtu = 576;
    public const int MinimumIpv6Mtu = 1280;
    public const int MaximumIpPacketSize = ushort.MaxValue;

    public WorkspacePacketRouteCapabilities(
        WorkspaceIpAddressFamilies addressFamilies,
        WorkspaceIpProtocolCapabilities protocols,
        int maximumPacketSize)
    {
        if (addressFamilies == WorkspaceIpAddressFamilies.None
            || (addressFamilies & ~(WorkspaceIpAddressFamilies.Ipv4
                | WorkspaceIpAddressFamilies.Ipv6)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(addressFamilies),
                addressFamilies,
                "At least one known IP address family is required.");
        }

        if (protocols == WorkspaceIpProtocolCapabilities.None
            || (protocols & ~WorkspaceIpProtocolCapabilities.All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(protocols),
                protocols,
                "At least one known IP protocol capability is required.");
        }

        var minimumPacketSize = addressFamilies.HasFlag(WorkspaceIpAddressFamilies.Ipv6)
            ? MinimumIpv6Mtu
            : MinimumIpv4Mtu;
        if (maximumPacketSize < minimumPacketSize || maximumPacketSize > MaximumIpPacketSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPacketSize),
                maximumPacketSize,
                $"A host packet route must carry between {minimumPacketSize} and {MaximumIpPacketSize} bytes.");
        }

        AddressFamilies = addressFamilies;
        Protocols = protocols;
        MaximumPacketSize = maximumPacketSize;
    }

    public WorkspaceIpAddressFamilies AddressFamilies { get; }

    public WorkspaceIpProtocolCapabilities Protocols { get; }

    public int MaximumPacketSize { get; }

    public bool IsUsableWorkspaceRoute =>
        Protocols.HasFlag(WorkspaceIpProtocolCapabilities.Tcp);

    public bool CanCarry(WorkspaceIpPacketFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var requiredFamily = frame.AddressFamily == WorkspaceIpAddressFamilies.Ipv4
            ? WorkspaceIpAddressFamilies.Ipv4
            : WorkspaceIpAddressFamilies.Ipv6;
        return AddressFamilies.HasFlag(requiredFamily)
            && frame.Length <= MaximumPacketSize;
    }
}

/// <summary>
/// One complete layer-three packet. The constructor copies caller-owned bytes so packet
/// boundaries and contents remain stable across asynchronous host routing.
/// </summary>
public sealed record WorkspaceIpPacketFrame
{
    private const int MinimumIpv4PacketLength = 20;
    private const int MinimumIpv6PacketLength = 40;
    private readonly byte[] _packet;

    public WorkspaceIpPacketFrame(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty)
        {
            throw new ArgumentException("An IP packet cannot be empty.", nameof(packet));
        }

        AddressFamily = (packet[0] >> 4) switch
        {
            4 => ParseIpv4(packet),
            6 => ParseIpv6(packet),
            _ => throw new ArgumentException(
                "A packet frame must contain an IPv4 or IPv6 packet.",
                nameof(packet)),
        };
        _packet = packet.ToArray();
    }

    public WorkspaceIpAddressFamilies AddressFamily { get; }

    public int Length => _packet.Length;

    public ReadOnlyMemory<byte> Packet => _packet;

    private static WorkspaceIpAddressFamilies ParseIpv4(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < MinimumIpv4PacketLength)
        {
            throw new ArgumentException("An IPv4 packet is shorter than its minimum header.");
        }

        var headerLength = (packet[0] & 0x0F) * 4;
        var totalLength = (packet[2] << 8) | packet[3];
        if (headerLength < MinimumIpv4PacketLength
            || headerLength > packet.Length
            || totalLength != packet.Length)
        {
            throw new ArgumentException("The IPv4 packet header or framed length is invalid.");
        }

        return WorkspaceIpAddressFamilies.Ipv4;
    }

    private static WorkspaceIpAddressFamilies ParseIpv6(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < MinimumIpv6PacketLength)
        {
            throw new ArgumentException("An IPv6 packet is shorter than its fixed header.");
        }

        var payloadLength = (packet[4] << 8) | packet[5];
        if (payloadLength != packet.Length - MinimumIpv6PacketLength)
        {
            throw new ArgumentException("The IPv6 packet framed length is invalid.");
        }

        return WorkspaceIpAddressFamilies.Ipv6;
    }
}

public enum WorkspacePacketGatewayState
{
    Ready,
    Blocked,
    Stopped,
}

public sealed record WorkspacePacketGatewaySnapshot
{
    public WorkspacePacketGatewaySnapshot(
        WorkspacePacketGatewayState state,
        WorkspacePacketRouteCapabilities? capabilities = null,
        NetworkConnectionError? error = null)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        if (state == WorkspacePacketGatewayState.Ready
            && (capabilities is null || !capabilities.IsUsableWorkspaceRoute || error is not null))
        {
            throw new ArgumentException(
                "A ready packet gateway requires a complete guest route without an error.");
        }

        if (state == WorkspacePacketGatewayState.Blocked
            && (error is null || capabilities is not null))
        {
            throw new ArgumentException(
                "A blocked packet gateway requires an error and cannot publish route capabilities.");
        }

        if (state == WorkspacePacketGatewayState.Stopped
            && (capabilities is not null || error is not null))
        {
            throw new ArgumentException(
                "A packet gateway that is not routing cannot publish capabilities or an error.");
        }

        State = state;
        Capabilities = capabilities;
        Error = error;
    }

    public WorkspacePacketGatewayState State { get; }

    public WorkspacePacketRouteCapabilities? Capabilities { get; }

    public NetworkConnectionError? Error { get; }
}

public sealed record WorkspacePacketGatewayOpenRequest
{
    public WorkspacePacketGatewayOpenRequest(
        WorkspaceInstanceId workspaceId,
        WorkspaceIsolationBinding isolation,
        NetworkConnectionProfile? connection = null,
        SecretMaterial? transientPassword = null,
        WorkspacePacketGatewayServiceProxy? serviceProxy = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId.Value))
        {
            throw new ArgumentException("A workspace instance ID is required.", nameof(workspaceId));
        }

        WorkspaceId = workspaceId;
        Isolation = isolation ?? throw new ArgumentNullException(nameof(isolation));
        if (connection is null && transientPassword is not null)
        {
            throw new ArgumentException(
                "A direct host route cannot consume a connection password.",
                nameof(transientPassword));
        }

        if (serviceProxy is not null && (connection is not null || transientPassword is not null))
        {
            throw new ArgumentException("A service proxy cannot also start a network provider.", nameof(serviceProxy));
        }

        Connection = connection;
        TransientPassword = transientPassword;
        ServiceProxy = serviceProxy;
    }

    public WorkspaceInstanceId WorkspaceId { get; }

    public WorkspaceIsolationBinding Isolation { get; }

    /// <summary>
    /// The selected host-owned upstream. Null means that the gateway uses the host's direct
    /// route; the guest still remains attached only to the host-only packet gateway.
    /// </summary>
    public NetworkConnectionProfile? Connection { get; }

    public bool IsDirect => Connection is null && ServiceProxy is null;

    /// <summary>A captured host-owned SOCKS route for backend service isolates. Never persisted.</summary>
    public WorkspacePacketGatewayServiceProxy? ServiceProxy { get; }

    /// <summary>
    /// Session-only password owned by the caller. The gateway may read it only while
    /// <see cref="IWorkspacePacketGatewayRuntime.OpenAsync"/> is running.
    /// </summary>
    public SecretMaterial? TransientPassword { get; }
}

/// <summary>
/// The lifecycle of a host-owned packet route for one isolated workspace. The native gateway
/// owns the authenticated raw-packet data plane; application code observes only route state.
/// </summary>
public interface IWorkspacePacketGatewaySession : IAsyncDisposable
{
    WorkspacePacketGatewaySnapshot Snapshot { get; }

    event EventHandler<WorkspacePacketGatewaySnapshot>? Changed;
}

public interface IWorkspacePacketGatewayRuntime
{
    ValueTask<NetworkConnectionResult<IWorkspacePacketGatewaySession>> OpenAsync(
        WorkspacePacketGatewayOpenRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken);
}
