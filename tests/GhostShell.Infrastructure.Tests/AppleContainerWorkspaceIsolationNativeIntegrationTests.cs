using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Terminal;

namespace GhostShell.Infrastructure.Tests;

public sealed class AppleContainerWorkspaceIsolationNativeIntegrationTests
{
    private const string EnableVariable = "GHOSTSHELL_RUN_APPLE_CONTAINER_NATIVE";
    private const string TerminalRuntimePathVariable = "GHOSTSHELL_GHOSTTY_VT_PATH";

    [NativeAppleContainerFact]
    public async Task Native_provider_reconfigures_mounts_without_losing_the_persistent_root()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var workspaceId = new WorkspaceId($"native-smoke-{Guid.NewGuid():N}");
        var resourceName = AppleContainerWorkspaceIsolationProvider.ResourceName(workspaceId);
        var networkName = AppleContainerWorkspaceIsolationProvider.NetworkResourceName(workspaceId);
        var snapshotImage = $"{resourceName}-state:latest";
        var hostMount = Directory.CreateTempSubdirectory("ghostshell-native-mount-");
        var replacementHostMount = Directory.CreateTempSubdirectory(
            "ghostshell-native-replacement-mount-");
        await File.WriteAllTextAsync(
            Path.Combine(hostMount.FullName, "host-marker"),
            "mounted",
            timeout.Token);
        await File.WriteAllTextAsync(
            Path.Combine(replacementHostMount.FullName, "replacement-marker"),
            "replacement",
            timeout.Token);
        var provider = new AppleContainerWorkspaceIsolationProvider(
            useHostOnlyNetwork: true);
        var request = new WorkspaceIsolationPrepareRequest(workspaceId);
        var progress = new RecordingProgress<WorkspaceIsolationProgress>();
        const string marker = "ghostshell-native-persistence-ok";

        try
        {
            var first = Success(await provider.PrepareAsync(request, progress, timeout.Token));
            Assert.Equal(resourceName, first.ResourceName);
            Assert.Equal(networkName, first.Network?.ResourceName);
            Assert.Equal(
                AppleContainerWorkspaceIsolationProvider.DefaultImageReference,
                first.RuntimeImageReference);
            Assert.Contains(
                progress.Values,
                item => item.Status ==
                    $"Checking the prepared {AppleContainerWorkspaceIsolationProvider.DefaultImageReference} workspace image…");
            Assert.Contains(
                progress.Values,
                item => item.Status == "Creating the persistent workspace isolate…");
            await VerifyIdlePromptAsync(provider, first, timeout.Token);
            var commandOutput = await RunCommandAsync(
                provider,
                first,
                "/bin/sh",
                ["-c", "printf '%s' workspace-command-ok"],
                timeout.Token);
            Assert.Equal("workspace-command-ok", commandOutput);
            var interactiveOutput = await RunInteractiveCommandAsync(
                provider,
                first,
                timeout.Token);
            Assert.Equal("relay:workspace-browser", interactiveOutput);
            await RunShellAsync(
                provider,
                first,
                "test \"$(. /etc/os-release && printf '%s' \"$ID\")\" = ubuntu\n"
                + "test \"$(cat /proc/1/comm)\" = systemd\n"
                + "test \"$(systemctl is-system-running)\" = running\n"
                + "test -z \"$(ip -4 route show default)\"\n"
                + $"ip -4 route show '{first.Network?.Ipv4Gateway}/32' | grep -F 'scope link'\n"
                + "test \"$(id -un)\" = ghostshell\n"
                + "id -Gn | tr ' ' '\\n' | grep -Fx docker\n"
                + "sudo -n true\n"
                + "sudo install -o root -g docker -m 660 /dev/null /tmp/ghostshell-docker-access\n"
                + "printf docker-access-ok > /tmp/ghostshell-docker-access\n"
                + $"printf '%s' '{marker}' > \"$HOME/.ghostshell-native-smoke\"\n"
                + "printf '#!/bin/sh\\nexit 0\\n' | sudo tee /usr/local/bin/ghostshell-native-smoke >/dev/null\n"
                + "sudo chmod +x /usr/local/bin/ghostshell-native-smoke\nexit\n",
                timeout.Token);
            _ = Success(await provider.StopAsync(first, timeout.Token));

            var second = Success(await provider.PrepareAsync(request, timeout.Token));
            Assert.NotEqual(first.LeaseId, second.LeaseId);
            await RunShellAsync(
                provider,
                second,
                $"test \"$(cat \"$HOME/.ghostshell-native-smoke\")\" = '{marker}'\nexit\n",
                timeout.Token);
            _ = Success(await provider.StopAsync(second, timeout.Token));

            var reconfigureProgress = new RecordingProgress<WorkspaceIsolationProgress>();
            var thirdResult = await provider.PrepareAsync(
                new WorkspaceIsolationPrepareRequest(
                    workspaceId,
                    [new WorkspaceIsolationMount(hostMount.FullName, "/workspace", true)]),
                reconfigureProgress,
                timeout.Token);
            if (thirdResult is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure)
            {
                Assert.Fail(
                    "Reconfiguration failed after: "
                    + string.Join(" | ", reconfigureProgress.Values.Select(item => item.Status)));
            }

            var third = Success(thirdResult);
            Assert.Contains(
                reconfigureProgress.Values,
                item => item.Status == "Saving installed packages and guest files…");
            Assert.Contains(
                reconfigureProgress.Values,
                item => item.Status == "Building the preserved workspace image…");
            await RunShellAsync(
                provider,
                third,
                $"test \"$(cat \"$HOME/.ghostshell-native-smoke\")\" = '{marker}'\n"
                + "ghostshell-native-smoke\n"
                + "test \"$(cat /workspace/host-marker)\" = mounted\nexit\n",
                timeout.Token);
            _ = Success(await provider.StopAsync(third, timeout.Token));

            var fourth = Success(await provider.PrepareAsync(
                new WorkspaceIsolationPrepareRequest(
                    workspaceId,
                    [
                        new WorkspaceIsolationMount(
                            replacementHostMount.FullName,
                            "/workspace",
                            true),
                    ]),
                timeout.Token));
            await RunShellAsync(
                provider,
                fourth,
                "ghostshell-native-smoke\n"
                + "test ! -e /workspace/host-marker\n"
                + "test \"$(cat /workspace/replacement-marker)\" = replacement\nexit\n",
                timeout.Token);
            _ = Success(await provider.StopAsync(fourth, timeout.Token));

            var recreateProgress = new RecordingProgress<WorkspaceIsolationProgress>();
            _ = Success(await provider.RecreateAsync(
                request,
                recreateProgress,
                timeout.Token));
            Assert.Contains(
                recreateProgress.Values,
                item => item.Status == "Removing the existing workspace environment…");
            var recreated = Success(await provider.PrepareAsync(request, timeout.Token));
            await RunShellAsync(
                provider,
                recreated,
                "test ! -e \"$HOME/.ghostshell-native-smoke\"\nexit\n",
                timeout.Token);
            _ = Success(await provider.StopAsync(recreated, timeout.Token));
        }
        finally
        {
            _ = await RunProcessAsync(
                AppleContainerWorkspaceIsolationProvider.DefaultContainerExecutablePath,
                ["delete", "--force", resourceName],
                standardInput: null,
                CancellationToken.None);
            _ = await RunProcessAsync(
                AppleContainerWorkspaceIsolationProvider.DefaultContainerExecutablePath,
                ["image", "delete", snapshotImage],
                standardInput: null,
                CancellationToken.None);
            _ = await RunProcessAsync(
                AppleContainerWorkspaceIsolationProvider.DefaultContainerExecutablePath,
                ["network", "delete", networkName],
                standardInput: null,
                CancellationToken.None);
            hostMount.Delete(recursive: true);
            replacementHostMount.Delete(recursive: true);
        }
    }

    [NativeAppleContainerFact]
    public Task Native_packet_gateway_routes_guest_dns_and_https_then_closes_fail_closed() =>
        VerifyNativePacketGatewayAsync(configurationPath: null);

    [NativeAppleContainerFact("GHOSTSHELL_TEST_WIREGUARD_CONFIG")]
    public Task Native_WireGuard_routes_guest_IP_DNS_and_https_then_closes_fail_closed() =>
        VerifyNativePacketGatewayAsync(Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_WIREGUARD_CONFIG"));

    [NativeAppleContainerFact("GHOSTSHELL_TEST_OPENVPN_CONFIG")]
    public Task Native_OpenVPN_routes_guest_IP_DNS_and_https_then_closes_fail_closed() =>
        VerifyNativePacketGatewayAsync(Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_OPENVPN_CONFIG"), NetworkConnectionKind.OpenVpn);

    [NativeAppleContainerFact("GHOSTSHELL_TEST_PROXY_URL_FILE")]
    public Task Native_authenticated_proxy_routes_guest_DNS_and_https_then_closes_fail_closed() =>
        VerifyNativePacketGatewayAsync(Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_PROXY_URL_FILE"), NetworkConnectionKind.Proxy);

    private static async Task VerifyNativePacketGatewayAsync(string? configurationPath, NetworkConnectionKind kind = NetworkConnectionKind.WireGuard)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var vault = new InMemorySecretVault();
        SecretMaterial? password = null;
        NetworkConnectionProfile? connection = null;
        if (configurationPath is not null && kind == NetworkConnectionKind.Proxy)
        {
            var uri = new Uri((await File.ReadAllTextAsync(configurationPath, timeout.Token)).Trim());
            var userInfo = uri.UserInfo.Split(':', 2);
            Assert.Equal(2, userInfo.Length);
            password = SecretMaterial.CopyFrom(System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(userInfo[1])));
            connection = new NetworkConnectionProfile(new NetworkConnectionId("native-proxy"),
                NetworkConnectionProfile.CurrentSchemaVersion, "Native Proxy",
                new NetworkConnectionConfiguration.Proxy(NetworkProxyProtocol.Http, uri.Host, uri.Port,
                    Uri.UnescapeDataString(userInfo[0])));
        }
        else if (configurationPath is not null)
        {
            var connectionId = new NetworkConnectionId("native-wireguard");
            var reference = new SecretRef("native-wireguard-config");
            var content = await File.ReadAllBytesAsync(configurationPath, timeout.Token);
            try
            {
                using var secret = SecretMaterial.CopyFrom(content);
                _ = Assert.IsType<SecretVaultResult<SecretMetadata>.Success>(await vault.CreateAsync(
                    new CreateSecretRequest(reference, "Native VPN test", SecretKind.Password,
                        new SecretScope(SecretScopeKind.NetworkConnection, connectionId.Value),
                        new SecretUsePurpose(SecretUseKind.UserManagement, connectionId.Value)), secret, timeout.Token));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(content);
            }

            NetworkConnectionConfiguration configuration = new NetworkConnectionConfiguration.WireGuard(reference);
            if (kind == NetworkConnectionKind.OpenVpn)
            {
                var credentialPath = Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_OPENVPN_CREDENTIAL_FILE");
                Assert.False(string.IsNullOrWhiteSpace(credentialPath));
                var credentials = await File.ReadAllBytesAsync(credentialPath, timeout.Token);
                try
                {
                    var separator = Array.IndexOf(credentials, (byte)'\n');
                    Assert.True(separator > 0);
                    var username = System.Text.Encoding.UTF8.GetString(credentials, 0, separator).TrimEnd('\r');
                    password = SecretMaterial.CopyFrom(credentials.AsSpan(separator + 1).TrimEnd((byte)'\n'));
                    configuration = new NetworkConnectionConfiguration.OpenVpn(reference, username);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(credentials);
                }
            }

            connection = new NetworkConnectionProfile(connectionId, NetworkConnectionProfile.CurrentSchemaVersion,
                "Native VPN", configuration);
        }

        var workspaceId = new WorkspaceId($"native-gateway-{Guid.NewGuid():N}");
        var workspaceInstanceId = new WorkspaceInstanceId($"{workspaceId.Value}-instance");
        var resourceName = AppleContainerWorkspaceIsolationProvider.ResourceName(workspaceId);
        var networkName = AppleContainerWorkspaceIsolationProvider.NetworkResourceName(workspaceId);
        var provider = new AppleContainerWorkspaceIsolationProvider(useHostOnlyNetwork: true);
        IWorkspacePacketGatewaySession? session = null;

        try
        {
            var prepared = await provider.PrepareAsync(
                new WorkspaceIsolationPrepareRequest(workspaceId),
                timeout.Token);
            if (prepared is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure prepareFailure)
            {
                var inspect = await RunProcessAsync(
                    AppleContainerWorkspaceIsolationProvider.DefaultContainerExecutablePath,
                    ["inspect", resourceName],
                    standardInput: null,
                    timeout.Token);
                Assert.Fail(
                    $"Workspace isolation failed: {prepareFailure.Error.StableCode}: "
                    + $"{prepareFailure.Error.Message}\nContainer inspect:\n{inspect.StandardOutput}\n"
                    + inspect.StandardError);
            }

            var binding = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success>(
                prepared).Value;
            Assert.NotNull(binding.Network);

            var processes = new WorkspaceGatewayProcessRunner();
            var backend = new BundledWorkspacePacketGatewayBackend(
                [new ProxyNetworkConnectionProvider(vault)],
                new AppleContainerGuestPacketRouterLauncher(
                    AppleContainerWorkspaceIsolationProvider.DefaultContainerExecutablePath,
                    processes),
                processes,
                StagedWorkspaceGatewayHostPath(),
                openConnectLauncher: new WorkspaceVpnPacketRouteLauncher(vault, new NativeExecutableLocator(),
                    processes, StagedWorkspaceGatewayHostPath()));
            var runtime = new HostWorkspacePacketGatewayRuntime(backend);
            var gatewayProgress = new RecordingProgress<NetworkConnectionProgress>();
            var opened = await runtime.OpenAsync(
                new WorkspacePacketGatewayOpenRequest(workspaceInstanceId, binding, connection, password),
                gatewayProgress,
                timeout.Token);
            if (opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure failure)
            {
                Assert.Fail(
                    $"Workspace packet gateway failed: {failure.Error.StableCode}: "
                    + failure.Error.Message + "\nProgress: "
                    + string.Join(" | ", gatewayProgress.Values.Select(item => item.Status)));
            }

            session = Assert.IsType<NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success>(
                opened).Value;
            Assert.Equal(WorkspacePacketGatewayState.Ready, session.Snapshot.State);
            Assert.True(session.Snapshot.Capabilities?.IsUsableWorkspaceRoute);

            var routed = await RunCommandAsync(
                provider,
                binding,
                "/bin/sh",
                [
                    "-c",
                    "set -eu; "
                    + "ip -4 route show default | grep -F 'dev gsnet0'; "
                    + (kind == NetworkConnectionKind.Proxy ? string.Empty : "ping -4 -c 1 -W 5 1.1.1.1 >/dev/null; ")
                    + "getent ahostsv4 example.com >/dev/null; "
                    + "curl --fail --silent --show-error --max-time 20 https://example.com >/dev/null; "
                    + "printf routed",
                ],
                timeout.Token);
            Assert.Contains("routed", routed, StringComparison.Ordinal);

            if (connection?.ConnectionKind == NetworkConnectionKind.WireGuard
                && session.Snapshot.Capabilities!.AddressFamilies.HasFlag(WorkspaceIpAddressFamilies.Ipv6))
            {
                var ipv6 = await RunCommandAsync(provider, binding, "/bin/sh",
                    ["-c", "set -eu; ping -6 -c 1 -W 5 2606:4700:4700::1111 >/dev/null; "
                        + "curl -6 --fail --silent --show-error --max-time 20 https://example.com >/dev/null; printf ipv6-routed"],
                    timeout.Token);
                Assert.Equal("ipv6-routed", ipv6);
            }

            var guestPid = (await RunCommandAsync(
                    provider,
                    binding,
                    "/usr/bin/sudo",
                    ["-n", "cat", "/var/lib/ghostshell/network.pid"],
                    timeout.Token))
                .Trim();
            Assert.True(
                int.TryParse(
                    guestPid,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedGuestPid)
                && parsedGuestPid > 1);

            await session.DisposeAsync();
            session = null;
            var closed = await RunCommandAsync(
                provider,
                binding,
                "/bin/sh",
                ["-c", "test -z \"$(ip -4 route show default)\"; printf blocked"],
                timeout.Token);
            Assert.Equal("blocked", closed);
            var guestStopped = await RunCommandAsync(
                provider,
                binding,
                "/usr/bin/sudo",
                [
                    "-n",
                    "/bin/sh",
                    "-c",
                    $"test ! -e /var/lib/ghostshell/network.pid; "
                    + "test ! -e /var/lib/ghostshell/network.sock; "
                    + $"test ! -e /proc/{parsedGuestPid}; printf stopped",
                ],
                timeout.Token);
            Assert.Equal("stopped", guestStopped);
        }
        finally
        {
            password?.Dispose();
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            _ = await RunProcessAsync(
                AppleContainerWorkspaceIsolationProvider.DefaultContainerExecutablePath,
                ["delete", "--force", resourceName],
                standardInput: null,
                CancellationToken.None);
            _ = await RunProcessAsync(
                AppleContainerWorkspaceIsolationProvider.DefaultContainerExecutablePath,
                ["network", "delete", networkName],
                standardInput: null,
                CancellationToken.None);
        }
    }

    private static async Task VerifyIdlePromptAsync(
        AppleContainerWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        var process = Success(provider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                mode: WorkspaceProcessMode.Interactive
                    | WorkspaceProcessMode.AllocateTerminal)));
        var launch = new TerminalLaunchRequest(
            process.HostWorkingDirectory,
            process.Executable,
            process.Arguments,
            process.Environment,
            shellActivityFallback: TerminalShellActivityFallback.PromptShape);
        var configuredRuntime = Environment.GetEnvironmentVariable(
            TerminalRuntimePathVariable);
        if (string.IsNullOrWhiteSpace(configuredRuntime))
        {
            Environment.SetEnvironmentVariable(
                TerminalRuntimePathVariable,
                StagedTerminalRuntimePath());
        }

        try
        {
            var factory = new GhosttyVtTerminalSessionFactory();
            await using var session = await factory.CreateAsync(
                SessionId.New(),
                launch,
                cancellationToken);
            try
            {
                using var promptTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                promptTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                TerminalScreenSnapshot screen;
                do
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), promptTimeout.Token);
                    screen = await session.ReadScreenAsync(promptTimeout.Token);
                }
                while (!screen.PlainText.Contains('$', StringComparison.Ordinal));

                var snapshot = await session.SnapshotAsync(promptTimeout.Token);
                Assert.False(
                    snapshot.HasActiveWork,
                    $"The real Apple container shell prompt was classified as active: {screen.PlainText}");
            }
            finally
            {
                _ = await session.CloseAsync(PanelCloseMode.Force, CancellationToken.None);
            }
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(configuredRuntime))
            {
                Environment.SetEnvironmentVariable(TerminalRuntimePathVariable, null);
            }
        }
    }

    private static string StagedTerminalRuntimePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                continue;
            }

            var runtime = Path.Combine(
                directory.FullName,
                "native",
                "artifacts",
                "osx-arm64",
                "libghostty-vt.dylib");
            Assert.True(File.Exists(runtime), $"The staged terminal runtime is missing: {runtime}");
            return runtime;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the GhostSHELL repository above {AppContext.BaseDirectory}.");
    }

    private static string StagedWorkspaceGatewayHostPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                continue;
            }

            var helper = Path.Combine(
                directory.FullName,
                "native",
                "artifacts",
                "osx-arm64",
                "ghostshell-workspace-gateway-darwin-arm64");
            Assert.True(File.Exists(helper), $"The staged workspace gateway is missing: {helper}");
            return helper;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the GhostSHELL repository above {AppContext.BaseDirectory}.");
    }

    private static async Task RunShellAsync(
        AppleContainerWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding,
        string input,
        CancellationToken cancellationToken)
    {
        var launch = Success(provider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                mode: WorkspaceProcessMode.Interactive)));
        var result = await RunProcessAsync(
            launch.Executable,
            launch.Arguments,
            input,
            cancellationToken);

        Assert.True(
            result.ExitCode == 0,
            $"Apple container shell exited with {result.ExitCode}: {result.StandardError}");
    }

    private static async Task<string> RunCommandAsync(
        AppleContainerWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var launch = Success(provider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                executable,
                arguments)));
        var result = await RunProcessAsync(
            launch.Executable,
            launch.Arguments,
            standardInput: null,
            cancellationToken);
        Assert.True(
            result.ExitCode == 0,
            $"Apple container command exited with {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }

    private static async Task<string> RunInteractiveCommandAsync(
        AppleContainerWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        var launch = Success(provider.CreateExecLaunch(
            binding,
            new WorkspaceIsolationProcessRequest(
                ConnectionKind.Local,
                "/bin/sh",
                ["-c", "read value; printf 'relay:%s' \"$value\""],
                mode: WorkspaceProcessMode.Interactive)));
        var result = await RunProcessAsync(
            launch.Executable,
            launch.Arguments,
            "workspace-browser\n",
            cancellationToken);
        Assert.True(
            result.ExitCode == 0,
            $"Apple container relay exited with {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), $"Failed to start '{executable}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(
            process.ExitCode,
            await stdout,
            await stderr);
    }

    private static T Success<T>(WorkspaceIsolationResult<T> result)
    {
        if (result is WorkspaceIsolationResult<T>.Failure failure)
        {
            Assert.Fail(
                $"Workspace isolation failed: {failure.Error.StableCode}: {failure.Error.Message}");
        }

        return Assert.IsType<WorkspaceIsolationResult<T>.Success>(result).Value;
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class NativeAppleContainerFactAttribute : FactAttribute
    {
        public NativeAppleContainerFactAttribute(string? configurationVariable = null)
        {
            if (configurationVariable is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(configurationVariable)))
            {
                Skip = $"Set {configurationVariable} to a private test configuration file.";
                return;
            }

            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EnableVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {EnableVariable}=1 to exercise the installed Apple container runtime.";
                return;
            }

            if (!OperatingSystem.IsMacOS()
                || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            {
                Skip = "The Apple container native test requires Apple-silicon macOS.";
            }
        }
    }

    private sealed class NativeExecutableLocator : IConnectionExecutableLocator
    {
        public string? Find(string executable) => executable == "ghostshell-openvpn-engine"
            ? Path.Combine(Path.GetDirectoryName(StagedWorkspaceGatewayHostPath())!, "openvpn-engine", executable)
            : null;
    }
}
