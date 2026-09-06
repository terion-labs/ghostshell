using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceVpnPacketRouteLauncherTests
{
    private static readonly NetworkConnectionId ConnectionId = new("anyconnect-raw-test");

    [Theory]
    [InlineData(false, "Login failed.", NetworkConnectionErrorCode.AuthenticationRejected)]
    [InlineData(true, "OpenVPN authentication failed", NetworkConnectionErrorCode.AuthenticationRejected)]
    [InlineData(false, "TLS authentication failed", NetworkConnectionErrorCode.ConnectionFailed)]
    [InlineData(false, "Connection timed out", NetworkConnectionErrorCode.ConnectionFailed)]
    [InlineData(true, "TLS negotiation failed", NetworkConnectionErrorCode.ConnectionFailed)]
    public async Task Only_explicit_authentication_rejections_are_password_recovery_candidates(
        bool openVpn, string diagnostic, NetworkConnectionErrorCode expected)
    {
        using var vault = new InMemorySecretVault();
        var reference = new SecretRef("vpn-test-secret");
        await StoreSecretAsync(vault, reference, "private-configuration");
        using var password = SecretMaterial.CopyFrom("test-password"u8);
        var runner = new RecordingProcessRunner { StartupFailure = diagnostic };
        var launcher = new WorkspaceVpnPacketRouteLauncher(vault,
            new DictionaryExecutableLocator(("openconnect", "/bundle/openconnect"),
                ("ghostshell-openvpn-engine", "/bundle/openvpn-engine")), runner, "/bundle/workspace-gateway");
        var profile = openVpn
            ? new NetworkConnectionProfile(ConnectionId, NetworkConnectionProfile.CurrentSchemaVersion, "VPN",
                new NetworkConnectionConfiguration.OpenVpn(reference, "alice"))
            : Profile(reference);

        var result = await launcher.StartAsync(new WorkspaceVpnPacketRouteRequest(Binding(), profile, password, new byte[32]), null, default);

        var failure = Assert.IsType<NetworkConnectionResult<WorkspaceGatewayProcessStart>.Failure>(result);
        Assert.Equal(expected, failure.Error.Code);
        Assert.DoesNotContain("test-password", failure.Error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(runner.OwningDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_configured_VPN_uses_host_packet_engine_and_private_inputs(bool openVpn)
    {
        using var vault = new InMemorySecretVault();
        var configurationReference = new SecretRef("packet-vpn-config");
        await StoreSecretAsync(vault, configurationReference, "private-configuration");
        using var password = SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes("session-only-password"));
        var processes = new RecordingProcessRunner();
        var launcher = new WorkspaceVpnPacketRouteLauncher(vault,
            new DictionaryExecutableLocator(("ghostshell-openvpn-engine", "/bundle/openvpn-engine")),
            processes, "/bundle/workspace-gateway");
        NetworkConnectionConfiguration configuration = openVpn
            ? new NetworkConnectionConfiguration.OpenVpn(configurationReference, username: "user")
            : new NetworkConnectionConfiguration.WireGuard(configurationReference);
        var started = Success(await launcher.StartAsync(
            new WorkspaceVpnPacketRouteRequest(Binding(),
                new NetworkConnectionProfile(ConnectionId, NetworkConnectionProfile.CurrentSchemaVersion, "Test VPN", configuration),
                openVpn ? password : null, new byte[32]), null, CancellationToken.None));
        var request = Assert.IsType<WorkspaceGatewayProcessRequest>(processes.Request);
        Assert.Equal("/bundle/workspace-gateway", request.Executable);
        Assert.Equal(openVpn ? "openvpn" : "wireguard", request.Arguments[0]);
        Assert.DoesNotContain(request.Arguments, value => value.Contains("private-configuration", StringComparison.Ordinal)
            || value.Contains("session-only-password", StringComparison.Ordinal));
        Assert.Equal(openVpn ? "session-only-password\n" : string.Empty, Encoding.UTF8.GetString(request.StandardInput.Span));
        Assert.False(File.Exists(processes.KeyPath));
        Assert.Equal("private-configuration", await File.ReadAllTextAsync(request.Arguments[2]));
        await started.Process.DisposeAsync();
        Assert.False(Directory.Exists(processes.OwningDirectory));
    }

    [Fact]
    public async Task Launches_OpenConnect_with_VpnFd_helper_and_one_shot_owner_only_key_file()
    {
        using var vault = new InMemorySecretVault();
        var passwordReference = new SecretRef("anyconnect-raw-password");
        await StoreSecretAsync(vault, passwordReference, "secret-password");
        var processes = new RecordingProcessRunner();
        var launcher = new WorkspaceVpnPacketRouteLauncher(
            vault,
            new DictionaryExecutableLocator(("openconnect", "/tools/openconnect")),
            processes,
            "/bundle/workspace-gateway");
        var authenticationKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        var started = Success(await launcher.StartAsync(
            new WorkspaceVpnPacketRouteRequest(
                Binding(),
                Profile(passwordReference),
                TransientPassword: null,
                authenticationKey),
            progress: null,
            CancellationToken.None));

        Assert.Equal("/tools/openconnect", processes.Request?.Executable);
        Assert.Contains("--protocol=anyconnect", processes.Request!.Arguments, StringComparer.Ordinal);
        Assert.Contains("--script-tun", processes.Request.Arguments, StringComparer.Ordinal);
        Assert.Contains("--passwd-on-stdin", processes.Request.Arguments, StringComparer.Ordinal);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(
                "--cafile=/etc/ssl/cert.pem",
                processes.Request.Arguments,
                StringComparer.Ordinal);
        }

        Assert.Equal("secret-password\n", Encoding.UTF8.GetString(
            processes.Request.StandardInput.Span));
        Assert.Contains(
            "'/bundle/workspace-gateway' openconnect-vpnfd",
            processes.ScriptCommand,
            StringComparison.Ordinal);
        Assert.Contains(
            "--socket '/tmp/ghostshell/guest.sock'",
            processes.ScriptCommand,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(authenticationKey),
            processes.ScriptCommand,
            StringComparison.Ordinal);
        Assert.Equal(authenticationKey, processes.AuthenticationKey);
        Assert.False(File.Exists(processes.KeyPath));
        Assert.True(Directory.Exists(processes.OwningDirectory));

        await started.Process.DisposeAsync();

        Assert.False(Directory.Exists(processes.OwningDirectory));
    }

    private static WorkspaceIsolationBinding Binding() => new(
        new WorkspaceId("workspace"),
        new WorkspaceIsolationProviderId("apple-container"),
        WorkspaceIsolationCapability.DedicatedNetworkNamespace,
        "ghostshell-workspace",
        [],
        Guid.NewGuid(),
        network: new WorkspaceIsolationNetworkBinding(
            "host-only",
            "192.168.64.1",
            "192.168.64.0/24",
            "/tmp/ghostshell/guest.sock",
            "/run/ghostshell/network.sock",
            "/opt/ghostshell/bin/workspace-gateway"));

    private static NetworkConnectionProfile Profile(SecretRef password) => new(
        ConnectionId,
        NetworkConnectionProfile.CurrentSchemaVersion,
        "Office VPN",
        new NetworkConnectionConfiguration.AnyConnect(
            new Uri("https://vpn.example.test"),
            username: "user",
            passwordSecret: password));

    private static async Task StoreSecretAsync(
        InMemorySecretVault vault,
        SecretRef reference,
        string value)
    {
        using var material = SecretMaterial.CopyFrom(Encoding.UTF8.GetBytes(value));
        _ = Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(await vault.CreateAsync(
            new CreateSecretRequest(
                reference,
                "AnyConnect password",
                SecretKind.Password,
                new SecretScope(SecretScopeKind.NetworkConnection, ConnectionId.Value),
                new SecretUsePurpose(SecretUseKind.UserManagement, ConnectionId.Value)),
            material,
            CancellationToken.None));
    }

    private static WorkspaceGatewayProcessStart Success(
        NetworkConnectionResult<WorkspaceGatewayProcessStart> result) =>
        Assert.IsType<NetworkConnectionResult<WorkspaceGatewayProcessStart>.Success>(result).Value;

    private sealed class DictionaryExecutableLocator(
        params (string Name, string Path)[] executables) : IConnectionExecutableLocator
    {
        private readonly IReadOnlyDictionary<string, string> _paths =
            executables.ToDictionary(item => item.Name, item => item.Path, StringComparer.Ordinal);

        public string? Find(string executable) => _paths.GetValueOrDefault(executable);
    }

    private sealed class RecordingProcessRunner : IWorkspaceGatewayProcessRunner
    {
        public string? StartupFailure { get; init; }
        public WorkspaceGatewayProcessRequest? Request { get; private set; }

        public string ScriptCommand { get; private set; } = string.Empty;

        public string KeyPath { get; private set; } = string.Empty;

        public string OwningDirectory { get; private set; } = string.Empty;

        public byte[] AuthenticationKey { get; private set; } = [];

        public ValueTask<WorkspaceGatewayProcessStart> StartAsync(
            WorkspaceGatewayProcessRequest request,
            TimeSpan readinessTimeout,
            CancellationToken cancellationToken)
        {
            Request = request with { StandardInput = request.StandardInput.ToArray() };
            var scriptIndex = request.Arguments.IndexOf("--script");
            if (scriptIndex < 0)
            {
                KeyPath = request.Arguments[request.Arguments.IndexOf("--key-file") + 1];
            }
            else
            {
                ScriptCommand = request.Arguments[scriptIndex + 1];
                const string marker = "--key-file '";
                var keyStart = ScriptCommand.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                var keyEnd = ScriptCommand.IndexOf('\'', keyStart);
                KeyPath = ScriptCommand[keyStart..keyEnd];
            }
            OwningDirectory = Path.GetDirectoryName(KeyPath)!;
            AuthenticationKey = File.ReadAllBytes(KeyPath);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(KeyPath));
            }

            if (StartupFailure is not null)
            {
                throw new IOException($"The workspace gateway helper stopped before it became ready: {StartupFailure}");
            }

            return ValueTask.FromResult(new WorkspaceGatewayProcessStart(
                new RecordingProcess(),
                "READY v1 families=ipv4,ipv6 protocols=tcp,udp,control,other mtu=1280"));
        }

        public ValueTask<WorkspaceGatewayCommandResult> RunAsync(
            WorkspaceGatewayProcessRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WorkspaceGatewayCommandResult(0, string.Empty));
    }

    private sealed class RecordingProcess : IWorkspaceGatewayProcess
    {
        public bool HasExited { get; private set; }

        public string Diagnostic => string.Empty;

        public event EventHandler? Exited;

        public void Stop()
        {
            HasExited = true;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask DisposeAsync()
        {
            HasExited = true;
            Exited = null;
            return ValueTask.CompletedTask;
        }
    }
}
