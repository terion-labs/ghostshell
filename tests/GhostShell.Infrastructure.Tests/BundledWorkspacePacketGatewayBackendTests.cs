using System.Net;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class BundledWorkspacePacketGatewayBackendTests
{
    [Fact]
    public async Task Service_proxy_skips_provider_and_external_dns_and_keeps_authentication_on_stdin()
    {
        var guest = new RecordingGuestLauncher();
        var processes = new RecordingProcessRunner(FullReadiness());
        var backend = new BundledWorkspacePacketGatewayBackend([], guest, processes, "/bundle/helper", new StaticDnsSource());
        var baseRequest = Request();
        var proxy = new WorkspacePacketGatewayServiceProxy(new Uri("socks5://127.0.0.1:4321"),
            new WorkspaceNetworkProxyCredentials("usér", "pässword"));
        await using var session = Success(await backend.OpenAsync(new WorkspacePacketGatewayOpenRequest(
            baseRequest.WorkspaceId, baseRequest.Isolation, serviceProxy: proxy), null, CancellationToken.None));
        Assert.Contains("--resolve-proxy-names", processes.Request!.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("--dns", processes.Request.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("--dns-over-https", processes.Request.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("usér", processes.Request.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("pässword", processes.Request.Arguments, StringComparer.Ordinal);
        var expected = BundledWorkspacePacketGatewayBackend.CreateHostInput(guest.AuthenticationKey, proxy);
        Assert.Equal(expected, processes.AuthenticationKey);
        Assert.Equal(5, expected[32]);
        Assert.Equal(9, expected[38]);
    }

    [Fact]
    public async Task Direct_route_starts_guest_and_host_with_one_stdin_only_key()
    {
        var guest = new RecordingGuestLauncher();
        var processes = new RecordingProcessRunner(FullReadiness());
        var backend = new BundledWorkspacePacketGatewayBackend(
            [],
            guest,
            processes,
            "/bundle/ghostshell-workspace-gateway-darwin-arm64",
            new StaticDnsSource());

        await using var session = Success(await backend.OpenAsync(
            Request(),
            progress: null,
            CancellationToken.None));

        Assert.Equal(guest.AuthenticationKey, processes.AuthenticationKey);
        Assert.Equal(32, guest.AuthenticationKey.Length);
        Assert.Contains(guest.AuthenticationKey, value => value != 0);
        Assert.DoesNotContain(
            processes.Request!.Arguments,
            argument => argument.Contains(Convert.ToHexString(guest.AuthenticationKey), StringComparison.Ordinal));
        AssertArguments(processes.Request.Arguments, "--mode", "direct");
        Assert.DoesNotContain(
            "--upstream-host",
            processes.Request.Arguments,
            StringComparer.Ordinal);
        AssertArguments(processes.Request.Arguments, "--dns", "192.0.2.53");
        Assert.True(session.Capabilities.IsUsableWorkspaceRoute);
    }

    [Fact]
    public async Task Selected_provider_runs_on_host_and_supplies_only_its_loopback_endpoint()
    {
        var provider = new RecordingProvider();
        var processes = new RecordingProcessRunner(FullReadiness());
        var backend = new BundledWorkspacePacketGatewayBackend(
            [provider],
            new RecordingGuestLauncher(),
            processes,
            "/bundle/helper",
            new StaticDnsSource());

        await using var session = Success(await backend.OpenAsync(
            Request(Profile()),
            progress: null,
            CancellationToken.None));

        Assert.IsType<WorkspaceNetworkPlacement.HostPlacement>(provider.Request?.Placement);
        Assert.True(provider.Request?.KillSwitchEnabled);
        AssertArguments(processes.Request!.Arguments, "--mode", "socks5");
        AssertArguments(processes.Request.Arguments, "--upstream-host", "127.0.0.1");
        AssertArguments(processes.Request.Arguments, "--upstream-port", "43123");
        Assert.DoesNotContain("--allow-udp-associate", processes.Request.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Provider_failure_synchronously_stops_data_plane_and_reports_failure()
    {
        var provider = new RecordingProvider();
        var processes = new RecordingProcessRunner(FullReadiness());
        var backend = new BundledWorkspacePacketGatewayBackend(
            [provider],
            new RecordingGuestLauncher(),
            processes,
            "/bundle/helper",
            new StaticDnsSource());
        await using var session = Success(await backend.OpenAsync(
            Request(Profile()),
            progress: null,
            CancellationToken.None));
        NetworkConnectionError? observed = null;
        session.Failed += (_, error) => observed = error;

        provider.Session.Fail();

        Assert.True(processes.Process.Stopped);
        Assert.Equal("workspace_packet_gateway_provider_exited", observed?.StableCode);
    }

    [Fact]
    public async Task Non_loopback_provider_endpoint_is_rejected_and_every_process_is_closed()
    {
        var provider = new RecordingProvider(
            WorkspaceNetworkEgress.ViaProxy(new Uri("socks5://192.0.2.1:1080")));
        var guest = new RecordingGuestLauncher();
        var processes = new RecordingProcessRunner(FullReadiness());
        var backend = new BundledWorkspacePacketGatewayBackend(
            [provider],
            guest,
            processes,
            "/bundle/helper",
            new StaticDnsSource());

        var result = await backend.OpenAsync(
            Request(Profile()),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<
            NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Failure>(result);
        Assert.Equal("workspace_packet_gateway_provider_route_unsupported", failure.Error.StableCode);
        Assert.True(guest.Process.Stopped);
        Assert.True(provider.Session.Disposed);
        Assert.Null(processes.Request);
    }

    [Fact]
    public async Task Proxy_without_negotiated_dns_uses_routed_https_dns_instead_of_host_dns()
    {
        var provider = new RecordingProvider(dnsServers: []);
        var guest = new RecordingGuestLauncher();
        var processes = new RecordingProcessRunner(FullReadiness());
        var backend = new BundledWorkspacePacketGatewayBackend(
            [provider],
            guest,
            processes,
            "/bundle/helper",
            new StaticDnsSource());

        await using var session = Success(await backend.OpenAsync(
            Request(Profile()),
            progress: null,
            CancellationToken.None));

        AssertArguments(processes.Request!.Arguments, "--dns", "1.1.1.1");
        Assert.Contains("--dns-over-https", processes.Request.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("192.0.2.53", processes.Request.Arguments, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tailscale_dns_stays_private_and_udp_requires_an_explicit_provider_capability(bool supportsUdpAssociate)
    {
        var provider = new RecordingProvider(
            dnsServers: [IPAddress.Parse("100.100.100.100")],
            kind: NetworkConnectionKind.Tailscale,
            supportsUdpAssociate: supportsUdpAssociate);
        var protocols = supportsUdpAssociate ? "tcp,udp" : "tcp";
        var processes = new RecordingProcessRunner($"READY v1 families=ipv4,ipv6 protocols={protocols} mtu=1280");
        var backend = new BundledWorkspacePacketGatewayBackend(
            [provider], new RecordingGuestLauncher(), processes, "/bundle/helper", new StaticDnsSource());
        var profile = new NetworkConnectionProfile(
            new NetworkConnectionId("tailscale"), NetworkConnectionProfile.CurrentSchemaVersion,
            "Tailnet", new NetworkConnectionConfiguration.Tailscale("exit-node"));

        await using var session = Success(await backend.OpenAsync(Request(profile), null, CancellationToken.None));

        AssertArguments(processes.Request!.Arguments, "--dns", "100.100.100.100");
        Assert.DoesNotContain("--dns-over-https", processes.Request.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("192.0.2.53", processes.Request.Arguments, StringComparer.Ordinal);
        Assert.Equal(supportsUdpAssociate, processes.Request.Arguments.Contains("--allow-udp-associate", StringComparer.Ordinal));
        Assert.Equal(supportsUdpAssociate, session.Capabilities.Protocols.HasFlag(WorkspaceIpProtocolCapabilities.Udp));
    }

    [Fact]
    public async Task AnyConnect_is_rejected_before_guest_start_without_a_raw_packet_adapter()
    {
        var guest = new RecordingGuestLauncher();
        var processes = new RecordingProcessRunner(FullReadiness());
        var backend = new BundledWorkspacePacketGatewayBackend(
            [],
            guest,
            processes,
            "/bundle/helper",
            new StaticDnsSource());

        var result = await backend.OpenAsync(
            Request(new NetworkConnectionProfile(
                new NetworkConnectionId("anyconnect"),
                NetworkConnectionProfile.CurrentSchemaVersion,
                "Office VPN",
                new NetworkConnectionConfiguration.AnyConnect(
                    new Uri("https://vpn.example.test")))),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<
            NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Failure>(result);
        Assert.Equal(
            "workspace_packet_gateway_anyconnect_raw_adapter_missing",
            failure.Error.StableCode);
        Assert.False(guest.Process.Stopped);
        Assert.Null(processes.Request);
    }

    [Fact]
    public async Task AnyConnect_uses_raw_launcher_instead_of_a_Socks_provider()
    {
        var guest = new RecordingGuestLauncher();
        var processes = new RecordingProcessRunner(FullReadiness());
        var rawLauncher = new RecordingOpenConnectLauncher(
            "READY v1 families=ipv4 protocols=tcp,udp,control,other mtu=576");
        var backend = new BundledWorkspacePacketGatewayBackend(
            [],
            guest,
            processes,
            "/bundle/helper",
            new StaticDnsSource(),
            rawLauncher);
        var profile = new NetworkConnectionProfile(
            new NetworkConnectionId("anyconnect"),
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Office VPN",
            new NetworkConnectionConfiguration.AnyConnect(
                new Uri("https://vpn.example.test")));

        await using var session = Success(await backend.OpenAsync(
            Request(profile),
            progress: null,
            CancellationToken.None));

        Assert.Equal(profile, rawLauncher.Request?.Connection);
        Assert.Equal(guest.AuthenticationKey, rawLauncher.AuthenticationKey);
        Assert.Null(processes.Request);
        Assert.True(session.Capabilities.IsUsableWorkspaceRoute);
    }

    [Fact]
    public async Task AnyConnect_process_exit_reports_the_component_and_exit_code()
    {
        var rawLauncher = new RecordingOpenConnectLauncher(
            "READY v1 families=ipv4 protocols=tcp,udp,control,other mtu=576");
        var backend = new BundledWorkspacePacketGatewayBackend(
            [],
            new RecordingGuestLauncher(),
            new RecordingProcessRunner(FullReadiness()),
            "/bundle/helper",
            new StaticDnsSource(),
            rawLauncher);
        await using var session = Success(await backend.OpenAsync(
            Request(new NetworkConnectionProfile(
                new NetworkConnectionId("anyconnect"),
                NetworkConnectionProfile.CurrentSchemaVersion,
                "Office VPN",
                new NetworkConnectionConfiguration.AnyConnect(
                    new Uri("https://vpn.example.test")))),
            progress: null,
            CancellationToken.None));
        NetworkConnectionError? observed = null;
        session.Failed += (_, error) => observed = error;

        rawLauncher.Process.Exit(17);

        Assert.Equal("workspace_anyconnect_process_exited", observed?.StableCode);
        Assert.Equal(
            "Cisco AnyConnect stopped unexpectedly. Exit code: 17.",
            observed?.Message);
    }

    [Fact]
    public async Task Forwarded_guest_exit_is_not_misidentified_as_AnyConnect()
    {
        var guest = new RecordingGuestLauncher();
        var rawLauncher = new RecordingOpenConnectLauncher(
            "READY v1 families=ipv4 protocols=tcp,udp,control,other mtu=576");
        var backend = new BundledWorkspacePacketGatewayBackend(
            [], guest, new RecordingProcessRunner(FullReadiness()),
            "/bundle/helper", new StaticDnsSource(), rawLauncher);
        await using var session = Success(await backend.OpenAsync(
            Request(new NetworkConnectionProfile(
                new NetworkConnectionId("anyconnect"),
                NetworkConnectionProfile.CurrentSchemaVersion,
                "Office VPN",
                new NetworkConnectionConfiguration.AnyConnect(new Uri("https://vpn.example.test")))),
            progress: null,
            CancellationToken.None));
        NetworkConnectionError? observed = null;
        session.Failed += (_, error) => observed = error;
        guest.Process.Diagnostic = "write guest TUN: invalid argument private-secret";

        guest.Process.Exit(1, new object());

        Assert.Equal("workspace_packet_gateway_guest_exited", observed?.StableCode);
        Assert.Equal(
            "The workspace packet router stopped unexpectedly. Exit code: 1. The guest rejected an incoming network packet.",
            observed?.Message);
        Assert.True(rawLauncher.Process.Stopped);
        Assert.DoesNotContain("private-secret", observed!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("provider-channel-closed", "The VPN-to-workspace packet channel closed.")]
    [InlineData("provider-channel-failed", "The VPN-to-workspace packet channel failed.")]
    [InlineData("provider-handshake-failed", "The VPN packet channel could not be established.")]
    [InlineData("workspace-nic-congestion", "The host workspace network interface ran out of buffer space.")]
    [InlineData("workspace-nic-failed", "The host workspace network interface failed.")]
    [InlineData("protocol-authentication", "The packet channel failed authentication.")]
    [InlineData("protocol-malformed", "The packet channel received a malformed packet.")]
    [InlineData("protocol-sequence", "The packet channel lost packet ordering.")]
    [InlineData("runtime-panic", "The host Ethernet gateway crashed.")]
    public async Task SDK_exit_reason_and_signal_code_are_projected_without_raw_stderr(string reason, string message)
    {
        var guest = new RecordingGuestLauncher();
        var backend = new BundledWorkspacePacketGatewayBackend(
            [], guest, new RecordingProcessRunner(FullReadiness()), "/bundle/helper", new StaticDnsSource());
        await using var session = Success(await backend.OpenAsync(Request(), null, CancellationToken.None));
        NetworkConnectionError? observed = null;
        session.Failed += (_, error) => observed = error;
        guest.Process.Diagnostic = $"GHOSTSHELL_GATEWAY_EXIT_REASON={reason}\nprivate-secret";

        guest.Process.Exit(137, new object());

        Assert.Equal($"The workspace packet router stopped unexpectedly. Exit code: 137. {message}", observed?.Message);
        Assert.DoesNotContain("private-secret", observed!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Helper_capabilities_are_authoritative_and_tcp_only_route_retains_its_limitation()
    {
        var backend = new BundledWorkspacePacketGatewayBackend(
            [],
            new RecordingGuestLauncher(),
            new RecordingProcessRunner(
                "READY v1 families=ipv4,ipv6 protocols=tcp mtu=1280"),
            "/bundle/helper",
            new StaticDnsSource());
        var runtime = new HostWorkspacePacketGatewayRuntime(backend);

        var result = await runtime.OpenAsync(
            Request(),
            progress: null,
            CancellationToken.None);

        await using var session = Assert.IsType<NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success>(
            result).Value;
        Assert.Equal(WorkspaceIpProtocolCapabilities.Tcp, session.Snapshot.Capabilities?.Protocols);
    }

    private static IHostWorkspacePacketGatewayBackendSession Success(
        NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession> result) =>
        Assert.IsType<NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Success>(
            result).Value;

    private static WorkspacePacketGatewayOpenRequest Request(
        NetworkConnectionProfile? profile = null) =>
        new(
            new WorkspaceInstanceId("running-workspace"),
            new WorkspaceIsolationBinding(
                new WorkspaceId("workspace"),
                new WorkspaceIsolationProviderId("apple-container"),
                WorkspaceIsolationCapability.DedicatedNetworkNamespace,
                "ghostshell-workspace",
                [],
                Guid.NewGuid(),
                network: new WorkspaceIsolationNetworkBinding(
                    "ghostshell-workspace-network",
                    "192.168.64.1",
                    "192.168.64.0/24",
                    "/tmp/ghostshell/network.sock",
                    "/run/ghostshell/network.sock",
                    "/opt/ghostshell/bin/workspace-gateway")),
            profile);

    private static NetworkConnectionProfile Profile() => new(
        new NetworkConnectionId("proxy"),
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Proxy",
        new NetworkConnectionConfiguration.Proxy(
            NetworkProxyProtocol.Socks5,
            "proxy.example.test",
            1080));

    private static string FullReadiness() =>
        "READY v1 families=ipv4,ipv6 protocols=tcp,udp,control,other mtu=65535";

    private static void AssertArguments(
        IReadOnlyList<string> arguments,
        string name,
        string value)
    {
        var index = -1;
        for (var candidate = 0; candidate < arguments.Count; candidate++)
        {
            if (string.Equals(arguments[candidate], name, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }
        Assert.True(index >= 0);
        Assert.Equal(value, arguments[index + 1]);
    }

    private sealed class RecordingGuestLauncher : IWorkspaceGuestPacketRouterLauncher
    {
        public byte[] AuthenticationKey { get; private set; } = [];

        public RecordingProcess Process { get; } = new();

        public ValueTask<IWorkspaceGatewayProcess> StartAsync(
            WorkspaceIsolationBinding binding,
            ReadOnlyMemory<byte> authenticationKey,
            CancellationToken cancellationToken)
        {
            AuthenticationKey = authenticationKey.ToArray();
            return ValueTask.FromResult<IWorkspaceGatewayProcess>(Process);
        }
    }

    private sealed class StaticDnsSource : IWorkspaceGatewayDnsSource
    {
        public ValueTask<IReadOnlyList<IPAddress>> GetHostDnsServersAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("192.0.2.53")]);
    }

    private sealed class RecordingProcessRunner(string readinessLine) : IWorkspaceGatewayProcessRunner
    {
        public WorkspaceGatewayProcessRequest? Request { get; private set; }

        public byte[] AuthenticationKey { get; private set; } = [];

        public RecordingProcess Process { get; } = new();

        public ValueTask<WorkspaceGatewayProcessStart> StartAsync(
            WorkspaceGatewayProcessRequest request,
            TimeSpan readinessTimeout,
            CancellationToken cancellationToken)
        {
            Request = request with { StandardInput = request.StandardInput.ToArray() };
            AuthenticationKey = request.StandardInput.ToArray();
            return ValueTask.FromResult(new WorkspaceGatewayProcessStart(Process, readinessLine));
        }

        public ValueTask<WorkspaceGatewayCommandResult> RunAsync(
            WorkspaceGatewayProcessRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WorkspaceGatewayCommandResult(0, string.Empty));
    }

    private sealed class RecordingProcess : IWorkspaceGatewayProcess
    {
        public bool Stopped { get; private set; }

        public bool HasExited => Stopped;

        public int? ExitCode { get; private set; }

        public string Diagnostic { get; set; } = string.Empty;

        public event EventHandler? Exited;

        public void Stop()
        {
            if (!Stopped)
            {
                // Process cleanup can synchronously re-enter the session's
                // exit callback. It must not replace or log a second failure.
                Exit(-9);
            }
        }

        public void Exit(int exitCode, object? sender = null)
        {
            ExitCode = exitCode;
            Stopped = true;
            Exited?.Invoke(sender ?? this, EventArgs.Empty);
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingProvider : INetworkConnectionProvider
    {
        private readonly WorkspaceNetworkEgress _egress;
        private readonly IReadOnlyList<IPAddress> _dnsServers;
        private readonly bool _supportsUdpAssociate;

        public RecordingProvider(
            WorkspaceNetworkEgress? egress = null,
            IReadOnlyList<IPAddress>? dnsServers = null,
            NetworkConnectionKind kind = NetworkConnectionKind.Proxy,
            bool supportsUdpAssociate = false)
        {
            _egress = egress ?? WorkspaceNetworkEgress.ViaProxy(
                new Uri("socks5://127.0.0.1:43123"));
            _dnsServers = dnsServers ?? [IPAddress.Parse("192.0.2.53")];
            Kind = kind;
            _supportsUdpAssociate = supportsUdpAssociate;
        }

        public NetworkConnectionKind Kind { get; }

        public NetworkConnectionStartRequest? Request { get; private set; }

        public RecordingProviderSession Session { get; private set; } = null!;

        public ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
            NetworkConnectionStartRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            Request = request;
            Session = new RecordingProviderSession(
                request.Connection.Id,
                _egress,
                _dnsServers,
                _supportsUdpAssociate);
            return ValueTask.FromResult(
                NetworkConnectionResult<INetworkConnectionSession>.Succeed(Session));
        }
    }

    private sealed class RecordingOpenConnectLauncher(string readinessLine) :
        IWorkspaceVpnPacketRouteLauncher
    {
        public WorkspaceVpnPacketRouteRequest? Request { get; private set; }

        public byte[] AuthenticationKey { get; private set; } = [];

        public RecordingProcess Process { get; } = new();

        public ValueTask<NetworkConnectionResult<WorkspaceGatewayProcessStart>> StartAsync(
            WorkspaceVpnPacketRouteRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            Request = request;
            AuthenticationKey = request.AuthenticationKey.ToArray();
            return ValueTask.FromResult(
                NetworkConnectionResult<WorkspaceGatewayProcessStart>.Succeed(
                    new WorkspaceGatewayProcessStart(
                        Process,
                        readinessLine)));
        }
    }

    private sealed class RecordingProviderSession(
        NetworkConnectionId connectionId,
        WorkspaceNetworkEgress egress,
        IReadOnlyList<IPAddress> dnsServers,
        bool supportsUdpAssociate) : INetworkConnectionSession
    {
        public NetworkConnectionSnapshot Snapshot { get; private set; } = new(
            connectionId,
            NetworkConnectionState.Connected);

        public WorkspaceNetworkEgress Egress { get; } = egress;

        public IReadOnlyList<IPAddress> DnsServers { get; } = dnsServers;

        public bool SupportsUdpAssociate { get; } = supportsUdpAssociate;

        public bool Disposed { get; private set; }

        public event EventHandler<NetworkConnectionSnapshot>? Changed;

        public void Fail()
        {
            Snapshot = new NetworkConnectionSnapshot(
                connectionId,
                NetworkConnectionState.Failed,
                "failed");
            Changed?.Invoke(this, Snapshot);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Changed = null;
            return ValueTask.CompletedTask;
        }
    }
}
