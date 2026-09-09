using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class HostUserspaceVpnTransportTests
{
    private static readonly NetworkConnectionId ConnectionId = new("host-vpn-test");
    private static readonly WorkspaceInstanceId WorkspaceId = new("workspace-instance");

    [Fact]
    public async Task WireGuard_exposes_only_a_loopback_Socks_route_and_cleans_up_secrets()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var configuration = new SecretRef("wireguard-configuration");
        await StoreSecretAsync(vault, configuration, "[Interface]\nPrivateKey = secret");
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.WireGuard,
            vault,
            processes,
            state.Path,
            ("ghostshell-workspace-network", "/tools/workspace-gateway"));

        var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.WireGuard(configuration)),
            progress: null,
            CancellationToken.None));

        Assert.NotNull(session.Egress.ProxyEndpoint);
        Assert.Equal("socks5", session.Egress.ProxyEndpoint.Scheme);
        Assert.Empty(processes.Commands);
        var started = Assert.Single(processes.Starts);
        Assert.Equal(["wireguard", "--config", started.Arguments[2], "--socks-port", started.Arguments[4]], started.Arguments);
        Assert.DoesNotContain(
            started.Arguments,
            argument => argument.Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain("[Socks5]", await File.ReadAllTextAsync(started.Arguments[2]), StringComparison.Ordinal);
        var temporaryDirectory = Path.GetDirectoryName(started.Arguments[2])!;

        await session.DisposeAsync();

        Assert.False(Directory.Exists(temporaryDirectory));
    }

    [Fact]
    public async Task WireGuard_retries_when_the_first_userspace_process_loses_the_port_race()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var configuration = new SecretRef("wireguard-retry-configuration");
        await StoreSecretAsync(vault, configuration, "[Interface]\nPrivateKey = secret");
        var processes = new RecordingHostVpnProcessRunner();
        processes.ListenerResults.Enqueue(false);
        processes.ListenerResults.Enqueue(true);
        var transport = Create(
            NetworkConnectionKind.WireGuard,
            vault,
            processes,
            state.Path,
            ("ghostshell-workspace-network", "/tools/workspace-gateway"));

        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.WireGuard(configuration)),
            progress: null,
            CancellationToken.None));

        Assert.Equal(2, processes.Starts.Count);
        Assert.Empty(processes.Commands);
    }

    [Fact]
    public async Task AnyConnect_uses_script_tun_and_keeps_the_password_out_of_arguments()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var password = new SecretRef("anyconnect-password");
        await StoreSecretAsync(vault, password, "secret-password");
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.AnyConnect,
            vault,
            processes,
            state.Path,
            ("openconnect", "/tools/openconnect"),
            ("ghostshell-workspace-network", "/tools/workspace-gateway"));

        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.AnyConnect(
                new Uri("https://vpn.example.test"),
                username: "user",
                passwordSecret: password)),
            progress: null,
            CancellationToken.None));

        var started = Assert.Single(processes.Starts);
        Assert.Contains("--script-tun", started.Arguments, StringComparer.Ordinal);
        Assert.Contains("--force-dpd=30", started.Arguments, StringComparer.Ordinal);
        Assert.Contains("--reconnect-timeout=300", started.Arguments, StringComparer.Ordinal);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains("--cafile=/etc/ssl/cert.pem", started.Arguments, StringComparer.Ordinal);
        }

        Assert.Contains(
            started.Arguments,
            argument => argument.Contains(
                "capture-openconnect-dns.sh",
                StringComparison.Ordinal)
                && argument.Contains("'/tools/workspace-gateway'", StringComparison.Ordinal));
        Assert.Contains("--passwd-on-stdin", started.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("--interface", started.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(
            started.Arguments,
            argument => argument.Contains("secret-password", StringComparison.Ordinal));
        Assert.Equal("secret-password\n", Encoding.UTF8.GetString(started.StandardInput.Span));
    }

    [Fact]
    public async Task AnyConnect_accepts_a_session_only_password()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        using var password = SecretMaterial.CopyFrom("session-password"u8);
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.AnyConnect,
            vault,
            processes,
            state.Path,
            ("openconnect", "/tools/openconnect"),
            ("ghostshell-workspace-network", "/tools/workspace-gateway"));

        await using var session = Success(await transport.ConnectAsync(
            Request(
                new NetworkConnectionConfiguration.AnyConnect(
                    new Uri("https://vpn.example.test"),
                    username: "user"),
                password),
            progress: null,
            CancellationToken.None));

        var started = Assert.Single(processes.Starts);
        Assert.Contains("--passwd-on-stdin", started.Arguments, StringComparer.Ordinal);
        Assert.Equal("session-password\n", Encoding.UTF8.GetString(started.StandardInput.Span));
    }

    [Fact]
    public async Task AnyConnect_exposes_only_valid_negotiated_dns_addresses()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var processes = new RecordingHostVpnProcessRunner
        {
            OnStart = request =>
            {
                var scriptCommand = request.Arguments[
                    request.Arguments.IndexOf("--script") + 1];
                var quoted = scriptCommand.Split('\'');
                Assert.DoesNotContain(
                    "reason",
                    File.ReadAllText(quoted[1]),
                    StringComparison.Ordinal);
                Assert.Contains(
                    "openconnect-socks --port",
                    File.ReadAllText(quoted[1]),
                    StringComparison.Ordinal);
                File.WriteAllText(
                    quoted[3],
                    "10.20.0.53 10.20.0.54\nfd00::53\ninvalid\n10.20.0.53");
            },
        };
        var transport = Create(
            NetworkConnectionKind.AnyConnect,
            vault,
            processes,
            state.Path,
            ("openconnect", "/tools/openconnect"),
            ("ghostshell-workspace-network", "/tools/workspace-gateway"));

        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.AnyConnect(
                new Uri("https://vpn.example.test"),
                username: "user")),
            progress: null,
            CancellationToken.None));

        Assert.Equal(
            [
                IPAddress.Parse("10.20.0.53"),
                IPAddress.Parse("10.20.0.54"),
                IPAddress.Parse("fd00::53"),
            ],
            session.DnsServers);
    }

    [Fact]
    public async Task WireGuard_exposes_dns_addresses_from_the_interface_configuration()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var configuration = new SecretRef("wireguard-dns-configuration");
        await StoreSecretAsync(
            vault,
            configuration,
            "[Interface]\nPrivateKey = secret\nDNS = 10.30.0.53, fd00:30::53\n"
            + "[Peer]\nDNS = 192.0.2.1");
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.WireGuard,
            vault,
            processes,
            state.Path,
            ("ghostshell-workspace-network", "/tools/workspace-gateway"));

        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.WireGuard(configuration)),
            progress: null,
            CancellationToken.None));

        Assert.Equal(
            [IPAddress.Parse("10.30.0.53"), IPAddress.Parse("fd00:30::53")],
            session.DnsServers);
    }

    [Fact]
    public async Task Tailscale_uses_a_private_userspace_daemon_and_preserves_its_identity()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var authKey = new SecretRef("tailscale-auth-key");
        await StoreSecretAsync(vault, authKey, "tskey-auth-secret");
        var firstProcesses = new RecordingHostVpnProcessRunner();
        firstProcesses.CommandResults.Enqueue(new HostVpnCommandResult(0, string.Empty));
        firstProcesses.CommandResults.Enqueue(new HostVpnCommandResult(0, string.Empty));
        firstProcesses.CommandResults.Enqueue(new HostVpnCommandResult(
            0,
            "{\"BackendState\":\"Running\"}",
            "Warning: fixture CLI version differs from daemon"));
        var firstTransport = Create(
            NetworkConnectionKind.Tailscale,
            vault,
            firstProcesses,
            state.Path,
            ("tailscaled", "/tools/tailscaled"),
            ("tailscale", "/tools/tailscale"));

        var firstSession = Success(await firstTransport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.Tailscale(
                "exit-node",
                authKeySecret: authKey)),
            progress: null,
            CancellationToken.None));
        var daemon = Assert.Single(firstProcesses.Starts);
        Assert.Equal(IPAddress.Parse("100.100.100.100"), Assert.Single(firstSession.DnsServers));
        Assert.True(firstSession.SupportsUdpAssociate);
        Assert.Equal(3, firstProcesses.Commands.Count);
        var login = firstProcesses.Commands[0];
        Assert.Contains("up", login.Arguments, StringComparer.Ordinal);
        Assert.Contains("--exit-node=", login.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("--exit-node=exit-node", login.Arguments, StringComparer.Ordinal);
        Assert.Contains(login.Arguments, argument => argument.StartsWith("--auth-key=file:", StringComparison.Ordinal));
        var selection = firstProcesses.Commands[1];
        Assert.Contains("set", selection.Arguments, StringComparer.Ordinal);
        Assert.Contains("--exit-node=exit-node", selection.Arguments, StringComparer.Ordinal);
        Assert.Contains("--exit-node-allow-lan-access=false", selection.Arguments, StringComparer.Ordinal);
        Assert.Equal(login.Arguments[0], selection.Arguments[0]);
        Assert.DoesNotContain(selection.Arguments, argument => argument.StartsWith("--auth-key=", StringComparison.Ordinal));
        var statusCommand = Assert.Single(firstProcesses.Commands,
            command => command.Arguments.Contains("status", StringComparer.Ordinal));
        Assert.Contains("--peers=false", statusCommand.Arguments, StringComparer.Ordinal);
        Assert.Contains("--tun=userspace-networking", daemon.Arguments, StringComparer.Ordinal);
        Assert.Contains(
            daemon.Arguments,
            argument => argument.StartsWith("--socks5-server=127.0.0.1:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            daemon.Arguments,
            argument => argument.StartsWith("--tun=", StringComparison.Ordinal)
                && !string.Equals(argument, "--tun=userspace-networking", StringComparison.Ordinal));
        var stateArgument = Assert.Single(
            daemon.Arguments,
            argument => argument.StartsWith("--state=", StringComparison.Ordinal));
        var statePath = stateArgument["--state=".Length..];
        await firstSession.DisposeAsync();

        Assert.True(File.Exists(statePath));
        Assert.DoesNotContain(
            firstProcesses.Commands,
            request => request.Arguments.Contains("logout", StringComparer.Ordinal));

        var secondProcesses = new RecordingHostVpnProcessRunner();
        secondProcesses.CommandResults.Enqueue(new HostVpnCommandResult(0, string.Empty));
        secondProcesses.CommandResults.Enqueue(new HostVpnCommandResult(0, string.Empty));
        secondProcesses.CommandResults.Enqueue(new HostVpnCommandResult(
            0,
            "{\"BackendState\": \"Running\"}"));
        var secondTransport = Create(
            NetworkConnectionKind.Tailscale,
            vault,
            secondProcesses,
            state.Path,
            ("tailscaled", "/tools/tailscaled"),
            ("tailscale", "/tools/tailscale"));

        await using var secondSession = Success(await secondTransport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.Tailscale("exit-node")),
            progress: null,
            CancellationToken.None));

        Assert.DoesNotContain(
            secondProcesses.Commands.SelectMany(request => request.Arguments),
            argument => argument.StartsWith("--auth-key=", StringComparison.Ordinal));
        Assert.Contains("--exit-node=", secondProcesses.Commands[0].Arguments, StringComparer.Ordinal);
        Assert.Contains("--exit-node=exit-node", secondProcesses.Commands[1].Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task OpenVpn_fails_explicitly_without_invoking_the_system_client()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.OpenVpn,
            vault,
            processes,
            state.Path,
            ("openvpn", "/tools/openvpn"));

        var result = await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.OpenVpn(new SecretRef("profile"))),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(NetworkConnectionErrorCode.RuntimeMissing, failure.Error.Code);
        Assert.Equal("packet_vpn_host_runtime_missing", failure.Error.StableCode);
        Assert.Empty(processes.Starts);
        Assert.Empty(processes.Commands);
    }

    [Fact]
    public async Task OpenVpn_uses_bundled_packet_engine_with_session_password_on_stdin()
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var configuration = new SecretRef("openvpn-configuration");
        await StoreSecretAsync(vault, configuration, "private-ovpn-profile");
        using var password = SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes("session-password"));
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(NetworkConnectionKind.OpenVpn, vault, processes, state.Path,
            ("ghostshell-workspace-network", "/bundle/workspace-gateway"),
            ("ghostshell-openvpn-engine", "/bundle/openvpn-engine"));
        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.OpenVpn(configuration, username: "user"), password),
            null, CancellationToken.None));
        var started = Assert.Single(processes.Starts);
        Assert.Equal("openvpn", started.Arguments[0]);
        Assert.Contains("/bundle/openvpn-engine", started.Arguments, StringComparer.Ordinal);
        Assert.Equal("session-password\n", Encoding.UTF8.GetString(started.StandardInput.Span));
        Assert.DoesNotContain(started.Arguments, value => value.Contains("session-password", StringComparison.Ordinal)
            || value.Contains("private-ovpn-profile", StringComparison.Ordinal));
        Assert.Empty(processes.Commands);
    }

    [Theory]
    [InlineData(NetworkConnectionKind.OpenVpn, "AUTH_FAILED", NetworkConnectionErrorCode.AuthenticationRejected)]
    [InlineData(NetworkConnectionKind.OpenVpn, "OpenVPN authentication failed", NetworkConnectionErrorCode.AuthenticationRejected)]
    [InlineData(NetworkConnectionKind.AnyConnect, "authentication failed", NetworkConnectionErrorCode.AuthenticationRejected)]
    [InlineData(NetworkConnectionKind.AnyConnect, "Login failed.", NetworkConnectionErrorCode.AuthenticationRejected)]
    [InlineData(NetworkConnectionKind.AnyConnect, "TLS authentication failed", NetworkConnectionErrorCode.ConnectionFailed)]
    [InlineData(NetworkConnectionKind.AnyConnect, "Connection timed out", NetworkConnectionErrorCode.ConnectionFailed)]
    [InlineData(NetworkConnectionKind.WireGuard, "invalid configuration", NetworkConnectionErrorCode.ConnectionFailed)]
    public async Task Vpn_does_not_retry_non_bind_failures_or_probe_an_unannounced_route(
        NetworkConnectionKind kind, string diagnostic, NetworkConnectionErrorCode expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var reference = new SecretRef("non-bind-profile");
        await StoreSecretAsync(vault, reference, "private-profile");
        var processes = new RecordingHostVpnProcessRunner
        {
            ExitDiagnostic = diagnostic,
            AnnounceRouteReady = false,
        };
        var transport = Create(kind, vault, processes, state.Path,
            ("ghostshell-workspace-network", "/bundle/workspace-gateway"),
            ("ghostshell-openvpn-engine", "/bundle/openvpn-engine"),
            ("openconnect", "/bundle/openconnect"));
        NetworkConnectionConfiguration configuration = kind switch
        {
            NetworkConnectionKind.OpenVpn => new NetworkConnectionConfiguration.OpenVpn(reference, username: "user"),
            NetworkConnectionKind.WireGuard => new NetworkConnectionConfiguration.WireGuard(reference),
            _ => new NetworkConnectionConfiguration.AnyConnect(new Uri("https://vpn.example.test")),
        };
        using var password = SecretMaterial.CopyFrom("session-password"u8);
        var result = await transport.ConnectAsync(Request(configuration, password), null, CancellationToken.None);
        Assert.Equal(expected, Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(result).Error.Code);
        Assert.Single(processes.Starts);
        Assert.Equal(0, processes.ListenerChecks);
    }

    [Theory]
    [InlineData("not json", NetworkConnectionErrorCode.ConnectionFailed)]
    [InlineData(
        "{\"BackendState\":\"NeedsLogin\",\"Message\":\"Running\"}",
        NetworkConnectionErrorCode.AuthenticationRequired)]
    [InlineData("{\"Other\":\"Running\"}", NetworkConnectionErrorCode.ConnectionFailed)]
    public async Task Tailscale_rejects_malformed_or_non_running_structured_status(
        string status,
        NetworkConnectionErrorCode expectedError)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var authKey = new SecretRef("tailscale-status-auth-key");
        await StoreSecretAsync(vault, authKey, "tskey-auth-test");
        var processes = new RecordingHostVpnProcessRunner();
        processes.CommandResults.Enqueue(new HostVpnCommandResult(0, string.Empty));
        processes.CommandResults.Enqueue(new HostVpnCommandResult(0, string.Empty));
        processes.CommandResults.Enqueue(new HostVpnCommandResult(0, status));
        var transport = Create(
            NetworkConnectionKind.Tailscale,
            vault,
            processes,
            state.Path,
            ("tailscaled", "/tools/tailscaled"),
            ("tailscale", "/tools/tailscale"));

        var result = await transport.ConnectAsync(
            Request(
                new NetworkConnectionConfiguration.Tailscale(
                    "exit-node",
                    authKeySecret: authKey),
                transientPassword: null),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(
            result);
        Assert.Equal(expectedError, failure.Error.Code);
    }

    [Theory]
    [InlineData(1, 0, "could not log in", 1, "sensitive server output", "tailscale_login_failed")]
    [InlineData(0, 1, "could not select the exit node", 2, "sensitive peer output", "tailscale_exit_node_failed")]
    [InlineData(0, 1, "absent from this client's peer map", 2,
        "invalid value \"sensitive-node\" for --exit-node; must be IP or hostname", "tailscale_exit_node_not_visible")]
    [InlineData(0, 1, "absent from this client's peer map", 2,
        "no node found in netmap with IP 100.64.0.9", "tailscale_exit_node_not_visible")]
    [InlineData(0, 1, "does not offer an exit node", 2,
        "node \"sensitive-node\" is not advertising an exit node", "tailscale_exit_node_not_advertised")]
    [InlineData(0, 1, "More than one visible device", 2,
        "ambiguous exit node name \"sensitive-node\"", "tailscale_exit_node_ambiguous")]
    [InlineData(0, 1, "client itself", 2,
        "cannot use sensitive-node as an exit node as it is a local IP address to this machine; did you mean --advertise-exit-node?",
        "tailscale_exit_node_is_self")]
    public async Task Tailscale_setup_failure_does_not_publish_a_route_and_identifies_the_failed_stage(
        int loginExitCode,
        int selectionExitCode,
        string expectedMessage,
        int expectedCommands,
        string diagnostic,
        string expectedStableCode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var authKey = new SecretRef("tailscale-setup-auth-key");
        await StoreSecretAsync(vault, authKey, "tskey-auth-test");
        var processes = new RecordingHostVpnProcessRunner();
        processes.CommandResults.Enqueue(new HostVpnCommandResult(loginExitCode, string.Empty, diagnostic));
        processes.CommandResults.Enqueue(new HostVpnCommandResult(selectionExitCode, string.Empty, diagnostic));
        var transport = Create(NetworkConnectionKind.Tailscale, vault, processes, state.Path,
            ("tailscaled", "/tools/tailscaled"), ("tailscale", "/tools/tailscale"));

        var result = await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.Tailscale("exit-node", authKeySecret: authKey)),
            progress: null,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<INetworkConnectionSession>.Failure>(result);
        Assert.Contains(expectedMessage, failure.Error.Message, StringComparison.Ordinal);
        Assert.Equal(expectedStableCode, failure.Error.StableCode);
        Assert.DoesNotContain("sensitive", failure.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("100.64.0.9", failure.Error.Message, StringComparison.Ordinal);
        Assert.Equal(expectedCommands, processes.Commands.Count);
        Assert.Equal(0, processes.ListenerChecks);
        Assert.True(Assert.Single(processes.Processes).HasExited);
    }

    [Theory]
    [InlineData("secret-cookie=private; https://private-gateway.test/path", false)]
    [InlineData("Login failed. password=private", true)]
    public async Task Unexpected_process_exit_marks_the_session_failed_without_exposing_output(
        string diagnostic, bool authenticationRejected)
    {
        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var configuration = new SecretRef("wireguard-health-configuration");
        await StoreSecretAsync(vault, configuration, "[Interface]\nPrivateKey = secret");
        var processes = new RecordingHostVpnProcessRunner { ExitDiagnostic = diagnostic };
        var transport = Create(
            NetworkConnectionKind.WireGuard,
            vault,
            processes,
            state.Path,
            ("ghostshell-workspace-network", "/tools/workspace-gateway"));
        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.WireGuard(configuration)),
            progress: null,
            CancellationToken.None));
        var failed = new TaskCompletionSource<NetworkConnectionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, snapshot) =>
        {
            if (snapshot.State == NetworkConnectionState.Failed)
            {
                failed.TrySetResult(snapshot);
            }
        };

        Assert.Single(processes.Processes).Exit();
        var snapshot = await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(NetworkConnectionState.Failed, snapshot.State);
        Assert.Contains("stopped", snapshot.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Exit code: 1.", snapshot.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("private", snapshot.Status, StringComparison.Ordinal);
        Assert.Equal(authenticationRejected, snapshot.Status!.Contains("rejected authentication", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnyConnect_is_connected_when_the_vpn_session_and_local_route_are_ready()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var state = new TemporaryDirectory();
        using var vault = new InMemorySecretVault();
        var processes = new RecordingHostVpnProcessRunner();
        var transport = Create(
            NetworkConnectionKind.AnyConnect,
            vault,
            processes,
            state.Path,
            ("openconnect", "/tools/openconnect"),
            ("ghostshell-workspace-network", "/tools/workspace-gateway"));

        await using var session = Success(await transport.ConnectAsync(
            Request(new NetworkConnectionConfiguration.AnyConnect(
                new Uri("https://vpn.example.test"),
                username: "user")),
            progress: null,
            CancellationToken.None));

        Assert.Equal(NetworkConnectionState.Connected, session.Snapshot.State);
        Assert.Contains("established", session.Snapshot.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(Assert.Single(processes.Processes).HasExited);
    }

    [Fact]
    public async Task Production_reachability_probe_sends_only_literal_peers_through_Socks()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var destinations = new List<(IPAddress Address, int Port)>();
        var server = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var greeting = new byte[3];
                await stream.ReadExactlyAsync(greeting);
                Assert.Equal(new byte[] { 5, 1, 0 }, greeting);
                await stream.WriteAsync(new byte[] { 5, 0 });
                var request = new byte[10];
                await stream.ReadExactlyAsync(request);
                Assert.Equal(5, request[0]);
                Assert.Equal(1, request[1]);
                Assert.Equal(1, request[3]);
                destinations.Add((
                    new IPAddress(request.AsSpan(4, 4)),
                    BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8))));
                await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 });
            }
        });
        var probe = new SocksReachabilityProbe();

        var reachable = await probe.ProbeAsync(
            ((IPEndPoint)listener.LocalEndpoint).Port,
            CancellationToken.None);
        await server;

        Assert.False(reachable.IsReachable);
        Assert.Equal(SocksReachabilityFailure.DestinationRejected, reachable.Failure);
        Assert.Equal(
            [(IPAddress.Parse("1.1.1.1"), 443), (IPAddress.Parse("1.0.0.1"), 443)],
            destinations);
    }

    private static HostUserspaceVpnTransport Create(
        NetworkConnectionKind kind,
        ISecretVault vault,
        IHostVpnProcessRunner processes,
        string stateRoot,
        params (string Name, string Path)[] executables) =>
        new(
            kind,
            vault,
            new DictionaryExecutableLocator(executables),
            processes,
            stateRoot);

    private static NetworkConnectionStartRequest Request(
        NetworkConnectionConfiguration configuration,
        SecretMaterial? transientPassword = null) => new(
        WorkspaceId,
        new NetworkConnectionProfile(
            ConnectionId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Host VPN",
            configuration),
        WorkspaceNetworkPlacement.Host,
        killSwitchEnabled: false,
        transientPassword);

    private static async Task StoreSecretAsync(
        InMemorySecretVault vault,
        SecretRef reference,
        string value)
    {
        using var material = SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(value));
        _ = Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "Host VPN test secret",
                SecretKind.Other,
                new SecretScope(SecretScopeKind.NetworkConnection, ConnectionId.Value),
                new SecretUsePurpose(SecretUseKind.UserManagement, ConnectionId.Value)),
            material,
            CancellationToken.None));
    }

    private static T Success<T>(NetworkConnectionResult<T> result) =>
        Assert.IsType<NetworkConnectionResult<T>.Success>(result).Value;

    private sealed class DictionaryExecutableLocator(
        IEnumerable<(string Name, string Path)> executables) : IConnectionExecutableLocator
    {
        private readonly IReadOnlyDictionary<string, string> _executables =
            executables.ToDictionary(item => item.Name, item => item.Path, StringComparer.Ordinal);

        public string? Find(string executable) =>
            _executables.GetValueOrDefault(executable);
    }

    private sealed class RecordingHostVpnProcessRunner : IHostVpnProcessRunner
    {
        public List<HostVpnProcessRequest> Starts { get; } = [];

        public List<HostVpnProcessRequest> Commands { get; } = [];

        public List<RecordingHostVpnProcess> Processes { get; } = [];

        public Queue<bool> ListenerResults { get; } = [];

        public string ExitDiagnostic { get; init; } = "address already in use";

        public bool AnnounceRouteReady { get; init; } = true;

        public int ListenerChecks { get; private set; }

        public Queue<HostVpnCommandResult> CommandResults { get; } = [];

        public Action<HostVpnProcessRequest>? OnStart { get; init; }

        public ValueTask<IHostVpnProcess> StartAsync(
            HostVpnProcessRequest request,
            CancellationToken cancellationToken)
        {
            Starts.Add(request with { StandardInput = request.StandardInput.ToArray() });
            OnStart?.Invoke(request);
            var process = new RecordingHostVpnProcess(AnnounceRouteReady, ExitDiagnostic);
            Processes.Add(process);
            var state = request.Arguments.FirstOrDefault(
                argument => argument.StartsWith("--state=", StringComparison.Ordinal));
            if (state is not null)
            {
                var path = state["--state=".Length..];
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "private-tailscale-state");
            }

            var socket = request.Arguments.FirstOrDefault(
                argument => argument.StartsWith("--socket=", StringComparison.Ordinal));
            if (socket is not null)
            {
                File.WriteAllText(socket["--socket=".Length..], string.Empty);
            }

            return ValueTask.FromResult<IHostVpnProcess>(process);
        }

        public ValueTask<HostVpnCommandResult> RunAsync(
            HostVpnProcessRequest request,
            CancellationToken cancellationToken)
        {
            Commands.Add(request);
            return ValueTask.FromResult(CommandResults.Count == 0
                ? new HostVpnCommandResult(0, string.Empty)
                : CommandResults.Dequeue());
        }

        public ValueTask<bool> WaitForTcpListenerAsync(
            IHostVpnProcess process,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ListenerChecks++;
            var result = ListenerResults.Count == 0 || ListenerResults.Dequeue();
            if (!result)
            {
                ((RecordingHostVpnProcess)process).Exit();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingHostVpnProcess : IHostVpnProcess
    {
        private readonly string _diagnostic;
        private readonly TaskCompletionSource _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited { get; private set; }

        public int? ExitCode => HasExited ? 1 : null;

        public RecordingHostVpnProcess(bool announceRouteReady, string diagnostic)
        {
            _diagnostic = diagnostic;
            RouteReady = Task.FromResult(announceRouteReady);
            if (!announceRouteReady)
            {
                Exit();
            }
        }

        public Task<bool> RouteReady { get; }

        public string Diagnostic => HasExited ? _diagnostic : string.Empty;

        public string StandardOutput => Diagnostic;

        public string StandardError => string.Empty;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public void Exit()
        {
            HasExited = true;
            _exit.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Exit();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("ghostshell-vpn-test-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
