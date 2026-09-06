using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class IsolatedVpnConnectionProviderTests
{
    private static readonly NetworkConnectionId ConnectionId = new("vpn-test");

    [Theory]
    [InlineData(NetworkConnectionKind.WireGuard)]
    [InlineData(NetworkConnectionKind.OpenVpn)]
    [InlineData(NetworkConnectionKind.AnyConnect)]
    [InlineData(NetworkConnectionKind.Tailscale)]
    public async Task Isolated_placement_requires_the_host_packet_gateway(
        NetworkConnectionKind kind)
    {
        var hostTransport = new RecordingHostTransport();
        var provider = new IsolatedVpnConnectionProvider(kind, hostTransport);

        var result = await provider.ConnectAsync(
            Request(Configuration(kind), WorkspaceNetworkPlacement.Isolated(Binding())),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.RouteUnavailable, failure.Error.Code);
        Assert.Equal("workspace_packet_gateway_required", failure.Error.StableCode);
        Assert.Contains("host packet gateway", failure.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("install", failure.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, hostTransport.ConnectCount);
    }

    [Theory]
    [InlineData(NetworkConnectionKind.WireGuard)]
    [InlineData(NetworkConnectionKind.OpenVpn)]
    [InlineData(NetworkConnectionKind.AnyConnect)]
    [InlineData(NetworkConnectionKind.Tailscale)]
    public async Task Host_placement_delegates_to_the_app_scoped_transport(
        NetworkConnectionKind kind)
    {
        var hostTransport = new RecordingHostTransport();
        var provider = new IsolatedVpnConnectionProvider(kind, hostTransport);
        var request = Request(Configuration(kind), WorkspaceNetworkPlacement.Host);

        await using var session = Success(await provider.ConnectAsync(
            request,
            progress: null,
            CancellationToken.None));

        Assert.Equal(1, hostTransport.ConnectCount);
        Assert.Same(request, hostTransport.LastRequest);
    }

    [Fact]
    public async Task Configuration_kind_mismatch_is_rejected_before_transport_selection()
    {
        var hostTransport = new RecordingHostTransport();
        var provider = new IsolatedVpnConnectionProvider(
            NetworkConnectionKind.WireGuard,
            hostTransport);

        var result = await provider.ConnectAsync(
            Request(
                Configuration(NetworkConnectionKind.OpenVpn),
                WorkspaceNetworkPlacement.Host),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.InvalidConfiguration, failure.Error.Code);
        Assert.Equal("vpn_configuration_kind_mismatch", failure.Error.StableCode);
        Assert.Equal(0, hostTransport.ConnectCount);
    }

    private static NetworkConnectionConfiguration Configuration(
        NetworkConnectionKind kind) => kind switch
        {
            NetworkConnectionKind.WireGuard =>
                new NetworkConnectionConfiguration.WireGuard(new SecretRef("wireguard-config")),
            NetworkConnectionKind.OpenVpn =>
                new NetworkConnectionConfiguration.OpenVpn(new SecretRef("openvpn-config")),
            NetworkConnectionKind.AnyConnect =>
                new NetworkConnectionConfiguration.AnyConnect(
                    new Uri("https://vpn.example.test"),
                    passwordSecret: new SecretRef("anyconnect-password")),
            NetworkConnectionKind.Tailscale =>
                new NetworkConnectionConfiguration.Tailscale("exit-node"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static NetworkConnectionStartRequest Request(
        NetworkConnectionConfiguration configuration,
        WorkspaceNetworkPlacement placement) => new(
        new WorkspaceInstanceId("running-workspace"),
        new NetworkConnectionProfile(
            ConnectionId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Test VPN",
            configuration),
        placement,
        killSwitchEnabled: false);

    private static WorkspaceIsolationBinding Binding() => new(
        new WorkspaceId("workspace"),
        new WorkspaceIsolationProviderId("test-isolation"),
        WorkspaceIsolationCapability.DedicatedNetworkNamespace,
        "test-isolate",
        [],
        Guid.NewGuid());

    private static T Success<T>(NetworkConnectionResult<T> result) =>
        Assert.IsType<NetworkConnectionResult<T>.Success>(result).Value;

    private sealed class RecordingHostTransport : IHostUserspaceVpnTransport
    {
        public int ConnectCount { get; private set; }

        public NetworkConnectionStartRequest? LastRequest { get; private set; }

        public ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
            NetworkConnectionStartRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            LastRequest = request;
            return ValueTask.FromResult(
                NetworkConnectionResult<INetworkConnectionSession>.Succeed(
                    new ConnectedSession(request.Connection.Id)));
        }
    }

    private sealed class ConnectedSession(NetworkConnectionId connectionId) :
        INetworkConnectionSession
    {
        public NetworkConnectionSnapshot Snapshot { get; } = new(
            connectionId,
            NetworkConnectionState.Connected);

        public WorkspaceNetworkEgress Egress { get; } =
            WorkspaceNetworkEgress.ViaProxy(new Uri("socks5://127.0.0.1:43821"));

        public event EventHandler<NetworkConnectionSnapshot>? Changed
        {
            add { }
            remove { }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
