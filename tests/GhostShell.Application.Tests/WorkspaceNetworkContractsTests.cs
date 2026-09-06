using GhostShell.Core;

namespace GhostShell.Application.Tests;

public sealed class WorkspaceNetworkContractsTests
{
    private static readonly NetworkConnectionId ConnectionId = new("network-contract");

    [Theory]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://127.0.0.1:8443")]
    public void Proxy_egress_accepts_supported_credential_free_endpoints(string endpoint)
    {
        var egress = WorkspaceNetworkEgress.ViaProxy(new Uri(endpoint));

        Assert.Equal(new Uri(endpoint), egress.ProxyEndpoint);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("socks5://alice:secret@127.0.0.1:1080")]
    [InlineData("relative")]
    public void Proxy_egress_rejects_unsupported_or_credential_bearing_endpoints(string endpoint)
    {
        _ = Assert.Throws<ArgumentException>(() =>
            WorkspaceNetworkEgress.ViaProxy(new Uri(endpoint, UriKind.RelativeOrAbsolute)));
    }

    [Fact]
    public void Policy_update_requires_every_referenced_profile()
    {
        var policy = new NetworkPolicy([ConnectionId], ConnectionId, true, true);

        _ = Assert.Throws<ArgumentException>(() => new WorkspaceNetworkPolicyUpdate(policy, []));
    }

    [Fact]
    public void Snapshot_rejects_contradictory_state()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Direct,
                WorkspaceNetworkEgress.Blocked,
                null));
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Connected,
                WorkspaceNetworkEgress.Attached,
                null));
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Failed,
                WorkspaceNetworkEgress.Direct,
                ConnectionId));
    }

    [Fact]
    public void Provider_contract_receives_the_workspace_placement()
    {
        var profile = Profile();
        using var password = SecretMaterial.CopyFrom("session-password"u8);
        var request = new NetworkConnectionStartRequest(
            new WorkspaceInstanceId("running-workspace"),
            profile,
            WorkspaceNetworkPlacement.Host,
            killSwitchEnabled: true,
            password);

        Assert.Same(profile, request.Connection);
        Assert.IsType<WorkspaceNetworkPlacement.HostPlacement>(request.Placement);
        Assert.True(request.KillSwitchEnabled);
        Assert.Same(password, request.TransientPassword);
    }

    [Fact]
    public void Password_prompt_request_normalizes_its_display_name()
    {
        var request = new NetworkPasswordPromptRequest(ConnectionId, "  Work VPN  ");

        Assert.Equal(ConnectionId, request.ConnectionId);
        Assert.Equal("Work VPN", request.ConnectionName);
    }

    [Fact]
    public void Packet_frame_copies_and_parses_one_complete_ip_packet()
    {
        var source = Ipv4Packet();

        var frame = new WorkspaceIpPacketFrame(source);
        source[0] = 0;

        Assert.Equal(WorkspaceIpAddressFamilies.Ipv4, frame.AddressFamily);
        Assert.Equal(20, frame.Length);
        Assert.Equal(0x45, frame.Packet.Span[0]);
    }

    [Fact]
    public void Packet_frame_rejects_unframed_or_malformed_input()
    {
        var mismatchedIpv4 = Ipv4Packet();
        mismatchedIpv4[3] = 21;
        var mismatchedIpv6 = Ipv6Packet();
        mismatchedIpv6[5] = 1;
        var unknownVersion = Ipv6Packet();
        unknownVersion[0] = 0x70;

        AssertInvalidPacket([]);
        AssertInvalidPacket(new byte[19]);
        AssertInvalidPacket(mismatchedIpv4);
        AssertInvalidPacket(mismatchedIpv6);
        AssertInvalidPacket(unknownVersion);
    }

    [Fact]
    public void Packet_route_accepts_tcp_only_without_claiming_udp()
    {
        var streamProxy = new WorkspacePacketRouteCapabilities(
            WorkspaceIpAddressFamilies.Ipv4 | WorkspaceIpAddressFamilies.Ipv6,
            WorkspaceIpProtocolCapabilities.Tcp,
            1500);
        var fullRoute = new WorkspacePacketRouteCapabilities(
            WorkspaceIpAddressFamilies.Ipv4 | WorkspaceIpAddressFamilies.Ipv6,
            WorkspaceIpProtocolCapabilities.Tcp | WorkspaceIpProtocolCapabilities.Udp,
            1500);
        var ipv4OnlyRoute = new WorkspacePacketRouteCapabilities(
            WorkspaceIpAddressFamilies.Ipv4,
            WorkspaceIpProtocolCapabilities.Tcp | WorkspaceIpProtocolCapabilities.Udp,
            WorkspacePacketRouteCapabilities.MinimumIpv4Mtu);

        Assert.True(streamProxy.IsUsableWorkspaceRoute);
        Assert.False(streamProxy.Protocols.HasFlag(WorkspaceIpProtocolCapabilities.Udp));
        Assert.True(fullRoute.IsUsableWorkspaceRoute);
        Assert.True(ipv4OnlyRoute.IsUsableWorkspaceRoute);
        Assert.True(fullRoute.CanCarry(new WorkspaceIpPacketFrame(Ipv4Packet())));
        Assert.True(fullRoute.CanCarry(new WorkspaceIpPacketFrame(Ipv6Packet())));
    }

    [Fact]
    public void Ready_packet_gateway_snapshot_requires_a_tcp_capable_route()
    {
        var streamProxy = new WorkspacePacketRouteCapabilities(
            WorkspaceIpAddressFamilies.Ipv4,
            WorkspaceIpProtocolCapabilities.Udp,
            1500);

        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspacePacketGatewaySnapshot(
                WorkspacePacketGatewayState.Ready,
                streamProxy));
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspacePacketGatewaySnapshot(WorkspacePacketGatewayState.Blocked));
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspacePacketGatewaySnapshot(
                WorkspacePacketGatewayState.Blocked,
                new WorkspacePacketRouteCapabilities(
                    WorkspaceIpAddressFamilies.Ipv4 | WorkspaceIpAddressFamilies.Ipv6,
                    WorkspaceIpProtocolCapabilities.All,
                    1500),
                new NetworkConnectionError(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    "packet_route_failed",
                    "The packet route failed.",
                    retryable: true)));
    }

    [Fact]
    public void Packet_gateway_request_models_direct_host_routing_without_a_connection()
    {
        var request = new WorkspacePacketGatewayOpenRequest(
            new WorkspaceInstanceId("running-workspace"),
            new WorkspaceIsolationBinding(
                new WorkspaceId("persistent-workspace"),
                new WorkspaceIsolationProviderId("test-isolation"),
                WorkspaceIsolationCapability.StructuredProcessExecution,
                "test-resource",
                [],
                Guid.NewGuid()));

        Assert.Null(request.Connection);
        Assert.True(request.IsDirect);
    }

    [Fact]
    public void Direct_packet_gateway_rejects_connection_credentials()
    {
        using var password = SecretMaterial.CopyFrom("password"u8);
        var binding = new WorkspaceIsolationBinding(
            new WorkspaceId("persistent-workspace"),
            new WorkspaceIsolationProviderId("test-isolation"),
            WorkspaceIsolationCapability.StructuredProcessExecution,
            "test-resource",
            [],
            Guid.NewGuid());

        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspacePacketGatewayOpenRequest(
                new WorkspaceInstanceId("running-workspace"),
                binding,
                connection: null,
                password));
    }

    private static void AssertInvalidPacket(byte[] packet) =>
        _ = Assert.Throws<ArgumentException>(() => new WorkspaceIpPacketFrame(packet));

    private static byte[] Ipv4Packet() =>
        [0x45, 0, 0, 20, .. new byte[16]];

    private static byte[] Ipv6Packet() =>
        [0x60, 0, 0, 0, 0, 0, .. new byte[34]];

    private static NetworkConnectionProfile Profile() => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Proxy",
        new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));
}
