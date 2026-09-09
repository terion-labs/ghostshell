using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class WorkspaceSdkNativeIntegrationTests
{
    [SdkRuntimeFact]
    public Task Fresh_sdk_workspace_bootstraps_routes_DNS_HTTPS_and_restarts_persistently() => VerifyAsync(null);

    [SdkRuntimeFact("ASURA_TEST_WIREGUARD_CONFIG")]
    public Task SDK_WireGuard_routes_packets_DNS_HTTPS_and_blocks_after_disconnect() =>
        VerifyAsync(Environment.GetEnvironmentVariable("ASURA_TEST_WIREGUARD_CONFIG"));

    private static async Task VerifyAsync(string? wireGuardConfiguration)
    {
        var assets = Environment.GetEnvironmentVariable("ASURA_TEST_SDK_RUNTIME_ROOT")!;
        var gateway = Path.Combine(Path.GetDirectoryName(assets)!, "asura-workspace-gateway-darwin-arm64");
        Assert.True(File.Exists(gateway), "Build the host network gateway before the SDK integration test.");
        var executable = Path.Combine(assets, "workspace-runtime");
        var directory = Path.Combine(Path.GetTempPath(), $"gs-sdk-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var workspace = new WorkspaceId($"sdk-test-{Guid.NewGuid():N}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var processes = new WorkspaceGatewayProcessRunner();
        var provider = new WorkspaceSdkIsolationProvider(executable, Path.Combine(directory, "state"),
            gateway, processes, 1000, 1000);
        using var vault = new InMemorySecretVault();
        WorkspaceIsolationBinding? binding = null;
        IWorkspacePacketGatewaySession? session = null;
        try
        {
            binding = Prepared(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(workspace), deadline.Token));
            Assert.NotNull(binding.Network!.HostAttachment);
            Assert.Null(binding.Network.GuestHelperPath);
            Assert.Equal("blocked", await ExecuteAsync(provider, binding,
                "if curl -4 --silent --max-time 3 https://example.com >/dev/null 2>&1; then exit 1; fi; printf blocked", deadline.Token));

            NetworkConnectionProfile? connection = null;
            if (wireGuardConfiguration is not null)
            {
                var connectionId = new NetworkConnectionId("sdk-wireguard-test");
                var reference = new SecretRef("sdk-wireguard-config");
                var bytes = await File.ReadAllBytesAsync(wireGuardConfiguration, deadline.Token);
                try
                {
                    using var secret = SecretMaterial.CopyFrom(bytes);
                    _ = Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(await vault.CreateAsync(
                        new CreateSecretRequest(reference, "SDK integration VPN", SecretKind.Password,
                            new SecretScope(SecretScopeKind.NetworkConnection, connectionId.Value),
                            new SecretUsePurpose(SecretUseKind.UserManagement, connectionId.Value)), secret, deadline.Token));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }

                connection = new NetworkConnectionProfile(connectionId, NetworkConnectionProfile.CurrentSchemaVersion,
                    "SDK test VPN", new NetworkConnectionConfiguration.WireGuard(reference));
            }

            var locator = new PathConnectionExecutableLocator();
            var backend = new BundledWorkspacePacketGatewayBackend([], new WorkspaceHostNetworkRouteLauncher(executable, processes), processes, gateway,
                openConnectLauncher: new WorkspaceVpnPacketRouteLauncher(vault, locator, processes, gateway));
            var runtime = new HostWorkspacePacketGatewayRuntime(backend);
            var opened = await runtime.OpenAsync(new WorkspacePacketGatewayOpenRequest(
                new WorkspaceInstanceId($"{workspace.Value}-instance"), binding, connection), null, deadline.Token);
            Assert.True(opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success,
                opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure failure ? failure.Error.Message : "No route returned.");
            session = ((NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success)opened).Value;
            Assert.Equal("routed", await ExecuteAsync(provider, binding,
                "set -eu; test ! -e /opt/asura/bin/workspace-gateway; "
                + "getent ahostsv4 example.com >/dev/null; curl -4 --fail --silent --show-error --max-time 25 https://example.com >/dev/null; "
                + "printf persistent > /home/asura/sdk-persistence-test; printf routed", deadline.Token));
            await session.DisposeAsync();
            session = null;
            Assert.Equal("blocked", await ExecuteAsync(provider, binding,
                "if curl -4 --silent --max-time 3 https://example.com >/dev/null 2>&1; then exit 1; fi; "
                + "test ! -e /var/lib/asura/network.pid; printf blocked", deadline.Token));

            _ = Prepared(await provider.StopAsync(binding, deadline.Token));
            binding = null;
            binding = Prepared(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(workspace), deadline.Token));
            Assert.Equal("persistent", await ExecuteAsync(provider, binding, "cat /home/asura/sdk-persistence-test", deadline.Token));
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            if (binding is not null)
            {
                _ = Prepared(await provider.StopAsync(binding, CancellationToken.None));
            }

            // Only this test's fresh disks are removed; never an existing workspace.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WorkspaceIsolationBinding Prepared(WorkspaceIsolationResult<WorkspaceIsolationBinding> result)
    {
        Assert.True(result is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success,
            result is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure failure ? failure.Error.Message : "No workspace returned.");
        return ((WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success)result).Value;
    }

    private static async Task<string> ExecuteAsync(WorkspaceSdkIsolationProvider provider, WorkspaceIsolationBinding binding, string script, CancellationToken cancellationToken)
    {
        var launch = Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success>(provider.CreateExecLaunch(binding,
            new WorkspaceIsolationProcessRequest(ConnectionKind.Local, "/bin/sh", ["-c", script]))).Value;
        var result = await new WorkspaceIsolationCommandRunner().RunAsync(launch, ReadOnlyMemory<byte>.Empty, cancellationToken);
        Assert.True(result.ExitCode == 0, $"Guest command failed ({result.ExitCode}): {result.StandardError}");
        return result.StandardOutput;
    }

    private sealed class SdkRuntimeFactAttribute : FactAttribute
    {
        public SdkRuntimeFactAttribute(string? credentialVariable = null)
        {
            if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASURA_TEST_SDK_RUNTIME_ROOT")))
            {
                Skip = "Requires an explicitly configured signed SDK runtime payload on macOS.";
            }
            else if (credentialVariable is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(credentialVariable)))
            {
                Skip = "Requires an explicitly supplied test VPN configuration.";
            }
        }
    }
}
