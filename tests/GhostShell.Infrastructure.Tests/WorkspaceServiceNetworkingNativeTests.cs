using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GhostShell.Application;
using GhostShell.Core;
using Xunit.Abstractions;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceServiceNetworkingNativeTests(ITestOutputHelper output)
{
    [ContainerRelayFact]
    public async Task Container_relay_preserves_remote_names_and_loopback_and_blocks_after_channel_loss()
    {
        var archive = Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_RELAY_ARCHIVE")!;
        var gateway = Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_RELAY_GATEWAY")!;
        var provider = new ContainerRelayIsolationProvider(async token =>
        {
            var engine = await RelayContainerEngine.DiscoverAsync(new RelayTestLocator(), new WorkspaceIsolationCommandRunner(), token);
            return Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_RELAY_ARCHITECTURE") is { } architecture ? engine with { Architecture = architecture } : engine;
        }, (_, _) => Task.FromResult(archive));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        await using var socks = new RecordingSocksEndpoint();
        WorkspaceIsolationBinding? binding = null;
        IWorkspacePacketGatewaySession? session = null;
        try
        {
            binding = Prepared(await provider.PrepareAsync(new(new WorkspaceId($"service-{Guid.NewGuid():N}")), deadline.Token));
            Assert.Empty(binding.Mounts);
            Assert.Equal("blocked", await ExecuteAsync(provider, binding,
                "if curl --noproxy '*' -s --max-time 2 http://198.51.100.20:18080/; then exit 1; fi; printf blocked", deadline.Token));
            var processes = new RelayDiagnosticRunner(output);
            var runtime = new HostWorkspacePacketGatewayRuntime(new BundledWorkspacePacketGatewayBackend([], new WorkspaceHostNetworkRouteLauncher(null, processes, gateway), processes, gateway));
            var opened = await runtime.OpenAsync(new(new WorkspaceInstanceId("container-relay-test"), binding,
                serviceProxy: new(socks.Endpoint, new("test-user", "test-password"))), null, deadline.Token);
            Assert.True(opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success,
                opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure failure ? failure.Error.Message : "No gateway.");
            session = ((NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success)opened).Value;
            await provider.ConfigureServiceNetworkingAsync(binding, deadline.Token);
            await provider.ConfigureServiceNetworkingAsync(binding, deadline.Token);
            foreach (var host in new[] { "127.0.0.1", "127.12.34.56", "::1", "private-database.invalid", "2001:db8::20", "198.51.100.20" })
            {
                var urlHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
                var response = await ExecuteAsync(provider, binding, $"curl --noproxy '*' -fsS --max-time 10 'http://{urlHost}:18080/'", deadline.Token);
                Assert.Equal(host, await socks.Targets.Reader.ReadAsync(deadline.Token));
                Assert.Equal("remote:" + host, response);
                output.WriteLine($"Container relay reached {host} through authenticated host SOCKS.");
            }
            var privileges = await ExecuteAsync(provider, binding, "id -u; grep -E 'CapEff|NoNewPrivs' /proc/self/status; ip -o link show", deadline.Token);
            Assert.Contains("1000", privileges, StringComparison.Ordinal);
            Assert.Contains("0000000000000000", privileges, StringComparison.Ordinal);
            Assert.DoesNotContain("eth0", privileges, StringComparison.Ordinal);
            await session.DisposeAsync();
            session = null;
            foreach (var host in new[] { "198.51.100.20", "[2001:db8::20]", "127.0.0.1" })
            {
                Assert.Equal("blocked", await ExecuteAsync(provider, binding,
                    $"if curl --noproxy '*' -s --max-time 2 'http://{host}:18080/'; then exit 1; fi; printf blocked", deadline.Token));
            }
            output.WriteLine("No direct NIC, no worker capabilities, IPv4/IPv6 fail closed before startup and after route disposal.");
        }
        finally
        {
            if (session is not null) { await session.DisposeAsync(); }
            if (binding is not null) { await provider.StopAsync(binding, CancellationToken.None); }
        }
    }

    [ServiceRuntimeFact]
    public async Task Service_guest_preserves_remote_loopback_and_private_names_through_host_SOCKS_gateway()
    {
        var assets = Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_SDK_RUNTIME_ROOT")!;
        var gateway = Path.Combine(Path.GetDirectoryName(assets)!, "ghostshell-workspace-gateway-darwin-arm64");
        var executable = Path.Combine(assets, "workspace-runtime");
        var directory = Directory.CreateTempSubdirectory("ghostshell-service-network-native-");
        var processes = new WorkspaceGatewayProcessRunner();
        var provider = new WorkspaceSdkIsolationProvider(executable, Path.Combine(directory.FullName, "state"),
            gateway, processes, 1000, 1000, serviceIsolate: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        await using var socks = new RecordingSocksEndpoint();
        WorkspaceIsolationBinding? binding = null;
        IWorkspacePacketGatewaySession? session = null;
        try
        {
            binding = Prepared(await provider.PrepareAsync(
                new WorkspaceIsolationPrepareRequest(new WorkspaceId($"network-test-{Guid.NewGuid():N}")), deadline.Token));
            await provider.ConfigureServiceNetworkingAsync(binding, deadline.Token);
            // Reconfiguration must be idempotent, without accumulating competing NAT chains.
            await provider.ConfigureServiceNetworkingAsync(binding, deadline.Token);
            output.WriteLine("Fresh service VM provisioned nftables; IPv4/IPv6 rules accepted by the bundled kernel.");
            var backend = new BundledWorkspacePacketGatewayBackend([], new WorkspaceHostNetworkRouteLauncher(executable, processes),
                processes, gateway);
            var runtime = new HostWorkspacePacketGatewayRuntime(backend);
            var opened = await runtime.OpenAsync(new WorkspacePacketGatewayOpenRequest(new WorkspaceInstanceId("service-network-test"),
                binding, serviceProxy: new WorkspacePacketGatewayServiceProxy(socks.Endpoint,
                    new WorkspaceNetworkProxyCredentials("test-user", "test-password"))), null, deadline.Token);
            Assert.True(opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success,
                opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure failure ? failure.Error.Message : "No gateway returned.");
            session = Assert.IsType<NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success>(opened).Value;

            foreach (var host in new[] { "127.0.0.1", "127.12.34.56", "2001:db8::20", "::1", "localhost", "private-database.invalid", "198.51.100.20" })
            {
                var urlHost = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
                string result;
                try
                {
                    result = await ExecuteAsync(provider, binding,
                        $"curl --noproxy '*' --fail --silent --show-error --max-time 10 'http://{urlHost}:18080/'", deadline.Token);
                }
                catch
                {
                    var request = new WorkspaceSdkExecRequest(["/usr/bin/systemd-run", "--wait", "--pipe", "--collect", "--quiet",
                        "--property=CapabilityBoundingSet=CAP_NET_ADMIN", "/bin/sh", "-c",
                        "ip -6 addr; ip -6 route show table all; nft list table inet ghostshell_service; cat /proc/net/snmp6"],
                        new Dictionary<string, string>(StringComparer.Ordinal), "/", 0, 0);
                    var diagnostic = await processes.RunAsync(new WorkspaceGatewayProcessRequest(executable,
                        ["exec", "--socket", binding.Network!.HostAttachment!.ControlSocketPath, "--request",
                            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request, WorkspaceSdkJsonContext.Default.WorkspaceSdkExecRequest))],
                        ReadOnlyMemory<byte>.Empty), TimeSpan.FromSeconds(10), deadline.Token);
                    output.WriteLine(diagnostic.Diagnostic);
                    throw;
                }
                var target = await socks.Targets.Reader.ReadAsync(deadline.Token);
                if (string.Equals(host, "localhost", StringComparison.Ordinal))
                {
                    Assert.Contains(target, ["127.0.0.1", "::1"], StringComparer.Ordinal);
                }
                else { Assert.Equal(host, target); }
                Assert.Equal($"remote:{target}", result);
                output.WriteLine($"Original {host}:18080 reached SOCKS as {target}:18080; response returned through guest conntrack.");
            }

            await session.DisposeAsync();
            session = null;
            Assert.Equal("blocked", await ExecuteAsync(provider, binding,
                "if curl --noproxy '*' --silent --max-time 2 http://127.0.0.1:18080/ >/dev/null 2>&1; then exit 1; fi; printf blocked",
                deadline.Token));
            var firstDisk = Path.Combine(directory.FullName, "state",
                AppleContainerWorkspaceIsolationProvider.ResourceName(binding.WorkspaceId), "rootfs.ext4");
            Assert.Equal(4096L * 1024 * 1024, new FileInfo(firstDisk).Length);
            _ = Prepared(await provider.StopAsync(binding, deadline.Token));
            binding = null;
            var messages = new List<string>();
            var elapsed = Stopwatch.StartNew();
            binding = Prepared(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(new WorkspaceId($"warm-test-{Guid.NewGuid():N}")),
                new RecordingProgress(messages), deadline.Token));
            output.WriteLine($"Second private 4-GiB service VM cloned and booted in {elapsed.Elapsed.TotalSeconds:F2}s, without apt provisioning.");
            Assert.DoesNotContain(messages, static message => message.Contains("Installing", StringComparison.Ordinal));
            await provider.ConfigureServiceNetworkingAsync(binding, deadline.Token);
        }
        finally
        {
            if (session is not null) { await session.DisposeAsync(); }
            if (binding is not null) { _ = Prepared(await provider.StopAsync(binding, CancellationToken.None)); }
            directory.Delete(recursive: true);
        }
    }

    private static WorkspaceIsolationBinding Prepared(WorkspaceIsolationResult<WorkspaceIsolationBinding> result)
    {
        Assert.True(result is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success,
            result is WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure failure ? failure.Error.Message : "No VM returned.");
        return ((WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success)result).Value;
    }

    private static async Task<string> ExecuteAsync(IWorkspaceIsolationProvider provider, WorkspaceIsolationBinding binding,
        string script, CancellationToken cancellationToken)
    {
        var launch = Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success>(provider.CreateExecLaunch(binding,
            new WorkspaceIsolationProcessRequest(ConnectionKind.Local, "/bin/sh", ["-c", script]))).Value;
        var result = await new WorkspaceIsolationCommandRunner().RunAsync(launch, ReadOnlyMemory<byte>.Empty, cancellationToken);
        Assert.True(result.ExitCode == 0, $"Guest command failed ({result.ExitCode}): {result.StandardError}");
        return result.StandardOutput;
    }

    private sealed class ContainerRelayFactAttribute : FactAttribute
    {
        public ContainerRelayFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_RELAY_ARCHIVE") is null
                || Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_RELAY_GATEWAY") is null)
            {
                Skip = "Requires explicit disposable-container relay integration assets.";
            }
        }
    }

    private sealed class RelayTestLocator : IConnectionExecutableLocator
    {
        public string? Find(string executable) => Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_RELAY_ENGINE") is { } selected
            && !string.Equals(selected, executable, StringComparison.Ordinal) ? null : new PathConnectionExecutableLocator().Find(executable);
    }

    private sealed class RelayDiagnosticRunner(ITestOutputHelper output) : IWorkspaceGatewayProcessRunner
    {
        private readonly WorkspaceGatewayProcessRunner _runner = new();
        public async ValueTask<WorkspaceGatewayProcessStart> StartAsync(WorkspaceGatewayProcessRequest request, TimeSpan timeout, CancellationToken token)
        {
            var started = await _runner.StartAsync(request, timeout, token);
            started.Process.Exited += (_, _) => output.WriteLine("Relay test process exit: " + started.Process.Diagnostic);
            return started;
        }
        public ValueTask<WorkspaceGatewayCommandResult> RunAsync(WorkspaceGatewayProcessRequest request, TimeSpan timeout, CancellationToken token) =>
            _runner.RunAsync(request, timeout, token);
    }

    private sealed class RecordingSocksEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        public Channel<string> Targets { get; } = Channel.CreateUnbounded<string>();
        public Uri Endpoint { get; }

        public RecordingSocksEndpoint()
        {
            _listener.Start();
            Endpoint = new Uri($"socks5://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            var clients = new List<Task>();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    clients.Add(RespondAsync(client, _stop.Token));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { await Task.WhenAll(clients); }
        }

        private async Task RespondAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var hello = await ReadAsync(stream, 2, token);
                    Assert.Equal(5, hello[0]);
                    _ = await ReadAsync(stream, hello[1], token);
                    await stream.WriteAsync(new byte[] { 5, 2 }, token);
                    var auth = await ReadAsync(stream, 2, token);
                    Assert.Equal("test-user", Encoding.UTF8.GetString(await ReadAsync(stream, auth[1], token)));
                    var length = (await ReadAsync(stream, 1, token))[0];
                    Assert.Equal("test-password", Encoding.UTF8.GetString(await ReadAsync(stream, length, token)));
                    await stream.WriteAsync(new byte[] { 1, 0 }, token);
                    var connect = await ReadAsync(stream, 4, token);
                    var target = connect[3] switch
                    {
                        1 => new IPAddress(await ReadAsync(stream, 4, token)).ToString(),
                        4 => new IPAddress(await ReadAsync(stream, 16, token)).ToString(),
                        3 => Encoding.ASCII.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1, token))[0], token)),
                        _ => throw new InvalidDataException("Unknown SOCKS address family."),
                    };
                    var port = await ReadAsync(stream, 2, token);
                    Assert.Equal(18080, (port[0] << 8) | port[1]);
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
                    var request = new StringBuilder();
                    while (request.Length < 65536 && !request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        request.Append((char)(await ReadAsync(stream, 1, token))[0]);
                    }
                    var body = "remote:" + target;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), token);
                    await Targets.Writer.WriteAsync(target, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }

        private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken token)
        {
            var value = new byte[count];
            await stream.ReadExactlyAsync(value, token);
            return value;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accept;
            _stop.Dispose();
        }
    }

    private sealed class ServiceRuntimeFactAttribute : FactAttribute
    {
        public ServiceRuntimeFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GHOSTSHELL_TEST_SDK_RUNTIME_ROOT")))
            {
                Skip = "Requires an explicitly configured signed SDK runtime payload on macOS.";
            }
        }
    }

    private sealed class RecordingProgress(List<string> messages) : IProgress<WorkspaceIsolationProgress>
    {
        public void Report(WorkspaceIsolationProgress value) => messages.Add(value.Status);
    }
}
