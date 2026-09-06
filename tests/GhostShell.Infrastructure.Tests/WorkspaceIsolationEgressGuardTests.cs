using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceIsolationEgressGuardTests
{
    private static readonly WorkspaceInstanceId WorkspaceInstanceId = new("running-workspace");
    private static readonly NetworkConnectionId ConnectionId = new("guarded-wireguard");

    [Fact]
    public async Task Arm_builds_an_isolate_local_namespace_with_a_fail_closed_forward_policy()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            Profile(),
            CancellationToken.None);

        Assert.True(result.IsEnforced);
        Assert.Null(result.Error);
        var launch = Assert.Single(commands.Launches);
        var script = launch.Arguments[1];
        Assert.Contains("ip netns add", script, StringComparison.Ordinal);
        Assert.Contains("policy drop", script, StringComparison.Ordinal);
        Assert.Contains("table inet ghostshell_guard", script, StringComparison.Ordinal);
        Assert.Contains("meta mark set 0x4753", script, StringComparison.Ordinal);
        Assert.Contains("policy drop", script, StringComparison.Ordinal);
        Assert.Contains(
            "delete table inet ghostshell_guard",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ip route replace table 4242 default via 169.254.254.2 dev \"$main_veth\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "iifname \"gs-vpn\" oifname \"%s\" accept",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "iifname \"%s\" oifname \"gs-vpn\" ct state established,related accept",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "                    ct state established,related accept",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ip saddr 169.254.254.0/30 oifname != \"gs-main\" masquerade",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ip netns exec \"$namespace\" nft -f -",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            WorkspaceIsolationNetworkNames.TunnelInterface(ConnectionId),
            launch.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Missing_firewall_runtime_reports_the_required_workspace_packages()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Result = new WorkspaceIsolationCommandResult(69, string.Empty, string.Empty),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            Profile(),
            CancellationToken.None);

        Assert.False(result.IsEnforced);
        var failure = Assert.IsType<NetworkConnectionError>(result.Error);
        Assert.Equal(NetworkConnectionErrorCode.RuntimeMissing, failure.Code);
        Assert.Equal(
            "workspace_network_guard_runtime_missing",
            failure.StableCode,
            StringComparer.Ordinal);
        Assert.Contains("iproute2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("nftables", failure.Message, StringComparison.Ordinal);
        Assert.Contains("procps", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_after_output_lockdown_reports_that_egress_is_still_enforced()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Result = new WorkspaceIsolationCommandResult(78, string.Empty, string.Empty),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            Profile(),
            CancellationToken.None);

        Assert.True(result.IsEnforced);
        Assert.Equal("workspace_network_kill_switch_arm_failed", result.Error?.StableCode);
    }

    [Fact]
    public async Task Uncertain_vpn_guard_launch_failure_is_treated_as_enforced()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Exception = new IOException("lost isolate command transport"),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            Profile(),
            CancellationToken.None);

        Assert.True(result.IsEnforced);
        Assert.Equal("workspace_network_kill_switch_arm_failed", result.Error?.StableCode);
        Assert.Contains(
            "treated as blocked",
            result.Error?.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxy_arm_intercepts_guest_tcp_and_handles_dns_without_allowing_other_udp()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            ProxyProfile(NetworkProxyProtocol.Socks5),
            CancellationToken.None);

        Assert.True(result.IsEnforced);
        Assert.Null(result.Error);
        var launch = Assert.Single(commands.Launches);
        var script = launch.Arguments[1];
        Assert.Contains("meta l4proto tcp redirect to :10080", script, StringComparison.Ordinal);
        Assert.Contains("udp dport 53 redirect to :10053", script, StringComparison.Ordinal);
        Assert.Contains("meta l4proto udp reject", script, StringComparison.Ordinal);
        Assert.Contains("meta skuid %s return", script, StringComparison.Ordinal);
        Assert.Contains("nameserver 1.1.1.1", script, StringComparison.Ordinal);
        Assert.Contains("runuser -u ghostshell-net", script, StringComparison.Ordinal);
        Assert.Contains("-p \"$proxy_state/redsocks.pid\"", script, StringComparison.Ordinal);
        var configuration = Encoding.UTF8.GetString(Assert.Single(commands.StandardInputs));
        Assert.Contains("type = socks5;", configuration, StringComparison.Ordinal);
        Assert.Contains("ip = GHOSTSHELL_PROXY_IP;", configuration, StringComparison.Ordinal);
        Assert.Contains("dnstc {", configuration, StringComparison.Ordinal);
        Assert.Contains("local_port = 10053;", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("pidfile", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_https_proxy_passes_credentials_only_over_standard_input()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        using var vault = new InMemorySecretVault();
        var passwordReference = new SecretRef("proxy-password");
        var password = Encoding.UTF8.GetBytes("secret\\\"value");
        using (var material = SecretMaterial.CopyFrom(password))
        {
            var stored = await vault.CreateAsync(
                new CreateSecretRequest(
                    passwordReference,
                    "Proxy password",
                    SecretKind.Password,
                    new SecretScope(SecretScopeKind.NetworkConnection, ConnectionId.Value),
                    new SecretUsePurpose(
                        SecretUseKind.NetworkConnectionAuthentication,
                        ConnectionId.Value)),
                material,
                CancellationToken.None);
            Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(stored);
        }

        var guard = new WorkspaceIsolationEgressGuard(isolation, commands, vault);
        var profile = new NetworkConnectionProfile(
            ConnectionId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Authenticated HTTPS proxy",
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Https,
                "proxy.example.com",
                443,
                "proxy-user",
                passwordReference));

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            profile,
            CancellationToken.None);

        Assert.True(result.IsEnforced);
        var launch = Assert.Single(commands.Launches);
        var launchText = string.Join('\n', launch.Arguments);
        Assert.DoesNotContain("secret", launchText, StringComparison.Ordinal);
        Assert.Contains("OPENSSL-CONNECT", launchText, StringComparison.Ordinal);
        Assert.Contains("verify=1", launchText, StringComparison.Ordinal);
        Assert.Contains("commonname=$proxy_host", launchText, StringComparison.Ordinal);
        var configuration = Encoding.UTF8.GetString(Assert.Single(commands.StandardInputs));
        Assert.Contains("type = http-connect;", configuration, StringComparison.Ordinal);
        Assert.Contains("ip = 127.0.0.1;", configuration, StringComparison.Ordinal);
        Assert.Contains("login = \"proxy-user\";", configuration, StringComparison.Ordinal);
        Assert.Contains("password = \"secret\\\\\\\"value\";", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_proxy_accepts_a_session_only_password()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);
        using var password = SecretMaterial.CopyFrom("session-password"u8);
        var profile = new NetworkConnectionProfile(
            ConnectionId,
            NetworkConnectionProfile.CurrentSchemaVersion,
            "Authenticated proxy",
            new NetworkConnectionConfiguration.Proxy(
                NetworkProxyProtocol.Socks5,
                "proxy.example.com",
                1080,
                username: "proxy-user"));

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            profile,
            CancellationToken.None,
            password);

        Assert.True(result.IsEnforced);
        var configuration = Encoding.UTF8.GetString(Assert.Single(commands.StandardInputs));
        Assert.Contains("login = \"proxy-user\";", configuration, StringComparison.Ordinal);
        Assert.Contains("password = \"session-password\";", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_proxy_runtime_names_the_required_guest_packages()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Result = new WorkspaceIsolationCommandResult(69, string.Empty, string.Empty),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            ProxyProfile(NetworkProxyProtocol.Http),
            CancellationToken.None);

        Assert.False(result.IsEnforced);
        Assert.Equal(NetworkConnectionErrorCode.RuntimeMissing, result.Error?.Code);
        Assert.Equal("workspace_proxy_runtime_missing", result.Error?.StableCode);
        Assert.Contains("redsocks", result.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("nftables", result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxy_failure_after_lockdown_reports_direct_egress_remains_blocked()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Result = new WorkspaceIsolationCommandResult(171, string.Empty, string.Empty),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            ProxyProfile(NetworkProxyProtocol.Http),
            CancellationToken.None);

        Assert.True(result.IsEnforced);
        Assert.Equal("workspace_proxy_sidecar_start_failed", result.Error?.StableCode);
        Assert.Contains("Direct egress remains blocked", result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proxy_dns_probe_failure_explains_the_upstream_port_requirement()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Result = new WorkspaceIsolationCommandResult(173, string.Empty, string.Empty),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            ProxyProfile(NetworkProxyProtocol.Http),
            CancellationToken.None);

        Assert.True(result.IsEnforced);
        Assert.Equal("workspace_proxy_dns_probe_failed", result.Error?.StableCode);
        Assert.Contains("port 53", result.Error?.Message, StringComparison.Ordinal);
        Assert.Contains("Direct egress remains blocked", result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uncertain_proxy_launch_failure_is_treated_as_enforced()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Exception = new IOException("lost isolate command transport"),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.ArmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            ProxyProfile(NetworkProxyProtocol.Http),
            CancellationToken.None);

        Assert.True(result.IsEnforced);
        Assert.Equal("workspace_proxy_route_setup_failed", result.Error?.StableCode);
        Assert.Contains("could not be confirmed", result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disarm_removes_the_blocking_table_after_network_cleanup()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner();
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.DisarmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            CancellationToken.None);

        Assert.IsType<NetworkConnectionResult<Unit>.Success>(result);
        var script = Assert.Single(commands.Launches).Arguments[1];
        var routeCleanup = script.IndexOf("ip route flush table", StringComparison.Ordinal);
        var guardCleanup = script.IndexOf(
            "nft delete table inet ghostshell_guard",
            StringComparison.Ordinal);
        var cleanupFailureCheck = script.IndexOf(
            "if [ \"$cleanup_failed\" -ne 0 ]; then exit 70; fi",
            StringComparison.Ordinal);
        var stateCleanup = script.IndexOf("rm -rf -- \"$state\"", StringComparison.Ordinal);
        var proxyCleanup = script.IndexOf(
            "nft delete table ip ghostshell_proxy",
            StringComparison.Ordinal);
        var resolverRestore = script.IndexOf(
            "ghostshell-proxy-resolver",
            StringComparison.Ordinal);
        Assert.True(routeCleanup >= 0);
        Assert.True(proxyCleanup >= 0);
        Assert.True(resolverRestore > proxyCleanup);
        Assert.True(cleanupFailureCheck > routeCleanup);
        Assert.True(guardCleanup > cleanupFailureCheck);
        Assert.True(guardCleanup > stateCleanup);
        Assert.True(guardCleanup > routeCleanup);
    }

    [Fact]
    public async Task Incomplete_disarm_is_a_typed_route_failure()
    {
        var isolation = new RecordingIsolationProvider();
        var commands = new RecordingCommandRunner
        {
            Result = new WorkspaceIsolationCommandResult(70, string.Empty, string.Empty),
        };
        var guard = new WorkspaceIsolationEgressGuard(isolation, commands);

        var result = await guard.DisarmAsync(
            WorkspaceInstanceId,
            isolation.Binding,
            CancellationToken.None);

        var failure = Assert.IsType<NetworkConnectionResult<Unit>.Failure>(result);
        Assert.Equal(NetworkConnectionErrorCode.RouteUnavailable, failure.Error.Code);
        Assert.Equal("workspace_network_kill_switch_disarm_failed", failure.Error.StableCode);
    }

    private static NetworkConnectionProfile Profile() => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "WireGuard",
        new NetworkConnectionConfiguration.WireGuard(new SecretRef("wireguard-config")));

    private static NetworkConnectionProfile ProxyProfile(NetworkProxyProtocol protocol) => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Proxy",
        new NetworkConnectionConfiguration.Proxy(protocol, "proxy.example.com", 1080));

    private sealed class RecordingCommandRunner : IWorkspaceIsolationCommandRunner
    {
        public WorkspaceIsolationCommandResult Result { get; init; } =
            new(0, string.Empty, string.Empty);

        public Exception? Exception { get; init; }

        public List<WorkspaceProcessLaunch> Launches { get; } = [];

        public List<byte[]> StandardInputs { get; } = [];

        public ValueTask<WorkspaceIsolationCommandResult> RunAsync(
            WorkspaceProcessLaunch launch,
            ReadOnlyMemory<byte> standardInput,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Launches.Add(launch);
            StandardInputs.Add(standardInput.ToArray());
            if (Exception is not null)
            {
                throw Exception;
            }

            return ValueTask.FromResult(Result);
        }
    }

    private sealed class RecordingIsolationProvider : IWorkspaceIsolationProvider
    {
        public WorkspaceIsolationProviderDescriptor Descriptor { get; } = new(
            new WorkspaceIsolationProviderId("test-isolation"),
            "Test isolation",
            WorkspaceIsolationCapability.DedicatedNetworkNamespace
            | WorkspaceIsolationCapability.StructuredProcessExecution);

        public WorkspaceIsolationBinding Binding { get; }

        public RecordingIsolationProvider()
        {
            Binding = new WorkspaceIsolationBinding(
                new WorkspaceId("workspace-definition"),
                Descriptor.Id,
                Descriptor.Capabilities,
                "test-isolate",
                [],
                Guid.NewGuid());
        }

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
            WorkspaceIsolationPrepareRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(Binding));

        public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(
            WorkspaceIsolationBinding binding,
            WorkspaceIsolationProcessRequest request)
        {
            Assert.Same(Binding, binding);
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(
                new WorkspaceProcessLaunch(
                    "fake-isolation-runtime",
                    request.Arguments,
                    request.Environment,
                    hostWorkingDirectory: null));
        }

        public ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(
            WorkspaceIsolationBinding binding,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding));
    }
}
