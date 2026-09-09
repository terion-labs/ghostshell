using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

internal sealed record WorkspaceGatewayProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    ReadOnlyMemory<byte> StandardInput);

internal interface IWorkspaceGatewayProcess : IAsyncDisposable
{
    bool HasExited { get; }

    int? ExitCode => null;

    string Diagnostic { get; }

    event EventHandler? Exited;

    void Stop();
}

internal sealed record WorkspaceGatewayProcessStart(
    IWorkspaceGatewayProcess Process,
    string ReadinessLine);

internal sealed record WorkspaceGatewayCommandResult(
    int ExitCode,
    string Diagnostic);

internal interface IWorkspaceGatewayProcessRunner
{
    ValueTask<WorkspaceGatewayProcessStart> StartAsync(
        WorkspaceGatewayProcessRequest request,
        TimeSpan readinessTimeout,
        CancellationToken cancellationToken);

    ValueTask<WorkspaceGatewayCommandResult> RunAsync(
        WorkspaceGatewayProcessRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface IWorkspaceGuestPacketRouterLauncher
{
    ValueTask<IWorkspaceGatewayProcess> StartAsync(
        WorkspaceIsolationBinding binding,
        ReadOnlyMemory<byte> authenticationKey,
        CancellationToken cancellationToken);
}

internal interface IWorkspaceGatewayDnsSource
{
    ValueTask<IReadOnlyList<IPAddress>> GetHostDnsServersAsync(
        CancellationToken cancellationToken);
}

internal sealed class SystemWorkspaceGatewayDnsSource : IWorkspaceGatewayDnsSource
{
    private const int MaximumDnsServerCount = 4;

    public async ValueTask<IReadOnlyList<IPAddress>> GetHostDnsServersAsync(
        CancellationToken cancellationToken)
    {
        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync("/etc/resolv.conf", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SecretSafeDiagnosticProjection.WriteTrace(
                "workspace.packet-gateway.dns.read.failed",
                exception);
            return [];
        }

        var addresses = new List<IPAddress>(MaximumDnsServerCount);
        foreach (var line in lines)
        {
            var fields = line.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length >= 2
                && string.Equals(fields[0], "nameserver", StringComparison.OrdinalIgnoreCase)
                && IPAddress.TryParse(fields[1], out var address)
                && !addresses.Contains(address))
            {
                addresses.Add(address);
                if (addresses.Count == MaximumDnsServerCount)
                {
                    break;
                }
            }
        }

        return addresses;
    }
}

/// <summary>
/// Starts the root-owned guest router through Apple container exec. The authentication key is
/// written as the process's complete standard input and never appears in arguments or environment.
/// </summary>
internal sealed class AppleContainerGuestPacketRouterLauncher : IWorkspaceGuestPacketRouterLauncher
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private const string GuestPidFilePath = "/var/lib/asura/network.pid";
    private readonly string? _containerExecutable;
    private readonly IWorkspaceGatewayProcessRunner _processes;

    internal AppleContainerGuestPacketRouterLauncher(
        string? containerExecutable,
        IWorkspaceGatewayProcessRunner processes)
    {
        _containerExecutable = containerExecutable;
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
    }

    public async ValueTask<IWorkspaceGatewayProcess> StartAsync(
        WorkspaceIsolationBinding binding,
        ReadOnlyMemory<byte> authenticationKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var network = binding.Network
            ?? throw new InvalidOperationException("A guest packet router requires host-only networking.");
        var guestHelperPath = network.GuestHelperPath
            ?? throw new InvalidOperationException("The guest packet router executable is unavailable.");
        var guestSocketPath = network.GuestSocketPath
            ?? throw new InvalidOperationException("The guest packet router socket is unavailable.");
        if (string.IsNullOrWhiteSpace(_containerExecutable))
        {
            throw new FileNotFoundException("The Apple container runtime is unavailable.");
        }

        var stopRequest = StopRequest(
            binding.ResourceName,
            guestHelperPath,
            guestSocketPath);
        var staleCleanup = await _processes.RunAsync(
                stopRequest,
                StopTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (staleCleanup.ExitCode != 0)
        {
            if (staleCleanup.Diagnostic.Contains(
                    $"failed to find target executable {guestHelperPath}",
                    StringComparison.Ordinal))
            {
                // A replaced host bundle can leave a running VirtioFS mount stale.
                // Do not bypass cleanup or restart a guest with active processes here.
                throw new IOException(
                    "The workspace gateway helper is missing from the running isolate. "
                    + "Restart the isolated workspace to refresh its app-bundle mount, then reconnect. "
                    + "Workspace files are preserved; running processes will stop.");
            }

            throw new IOException(
                string.IsNullOrWhiteSpace(staleCleanup.Diagnostic)
                    ? "The previous workspace guest router could not be stopped."
                    : $"The previous workspace guest router could not be stopped: {staleCleanup.Diagnostic}");
        }

        WorkspaceGatewayProcessStart started;
        try
        {
            started = await _processes.StartAsync(
                    new WorkspaceGatewayProcessRequest(
                        _containerExecutable,
                        [
                            "exec",
                            "--interactive",
                            "--user",
                            "0",
                            binding.ResourceName,
                            guestHelperPath,
                            "guest",
                            "--socket",
                            guestSocketPath,
                            "--pid-file",
                            GuestPidFilePath,
                            "--gateway",
                            network.Ipv4Gateway,
                        ],
                        authenticationKey),
                    ReadinessTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await StopAfterFailedStartAsync(stopRequest).ConfigureAwait(false);
            throw;
        }

        var ownedProcess = new ContainerOwnedGuestProcess(
            started.Process,
            _processes,
            stopRequest,
            StopTimeout);
        if (!string.Equals(started.ReadinessLine, "READY v1", StringComparison.Ordinal))
        {
            await ownedProcess.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException("The workspace guest router returned an invalid readiness response.");
        }

        return ownedProcess;
    }

    private async ValueTask StopAfterFailedStartAsync(WorkspaceGatewayProcessRequest stopRequest)
    {
        try
        {
            _ = await _processes.RunAsync(
                    stopRequest,
                    StopTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            SecretSafeDiagnosticProjection.WriteTrace(
                "workspace.packet-gateway.guest.stop.failed",
                exception);
        }
    }

    private WorkspaceGatewayProcessRequest StopRequest(
        string resourceName,
        string guestHelperPath,
        string guestSocketPath) =>
        new(
            _containerExecutable!,
            [
                "exec",
                "--user",
                "0",
                resourceName,
                guestHelperPath,
                "guest-stop",
                "--socket",
                guestSocketPath,
                "--pid-file",
                GuestPidFilePath,
            ],
            ReadOnlyMemory<byte>.Empty);

    private sealed class ContainerOwnedGuestProcess : IWorkspaceGatewayProcess
    {
        private readonly IWorkspaceGatewayProcess _inner;
        private readonly IWorkspaceGatewayProcessRunner _processes;
        private readonly WorkspaceGatewayProcessRequest _stopRequest;
        private readonly TimeSpan _stopTimeout;
        private readonly object _stopGate = new();
        private Task<WorkspaceGatewayCommandResult>? _stopTask;
        private int _disposed;

        internal ContainerOwnedGuestProcess(
            IWorkspaceGatewayProcess inner,
            IWorkspaceGatewayProcessRunner processes,
            WorkspaceGatewayProcessRequest stopRequest,
            TimeSpan stopTimeout)
        {
            _inner = inner;
            _processes = processes;
            _stopRequest = stopRequest;
            _stopTimeout = stopTimeout;
        }

        public bool HasExited => _inner.HasExited;

        public int? ExitCode => _inner.ExitCode;

        public string Diagnostic => _inner.Diagnostic;

        public event EventHandler? Exited
        {
            add => _inner.Exited += value;
            remove => _inner.Exited -= value;
        }

        public void Stop()
        {
            try
            {
                CompleteContainerStopAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                SecretSafeDiagnosticProjection.WriteTrace(
                    "workspace.packet-gateway.guest.stop.failed",
                    exception);
            }
            finally
            {
                _inner.Stop();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await CompleteContainerStopAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                SecretSafeDiagnosticProjection.WriteTrace(
                    "workspace.packet-gateway.guest.stop.failed",
                    exception);
            }
            finally
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task CompleteContainerStopAsync()
        {
            Task<WorkspaceGatewayCommandResult> stopTask;
            lock (_stopGate)
            {
                _stopTask ??= _processes.RunAsync(
                        _stopRequest,
                        _stopTimeout,
                        CancellationToken.None)
                    .AsTask();
                stopTask = _stopTask;
            }

            var stopped = await stopTask.ConfigureAwait(false);
            if (stopped.ExitCode != 0)
            {
                throw new IOException(
                    string.IsNullOrWhiteSpace(stopped.Diagnostic)
                        ? "The workspace guest router could not be stopped inside its container."
                        : $"The workspace guest router could not be stopped inside its container: {stopped.Diagnostic}");
            }
        }
    }
}

/// <summary>
/// Starts one bundled userspace gateway and one guest router per isolated workspace. Selected
/// providers run on the host and may expose only an authenticated loopback proxy to the helper.
/// </summary>
internal sealed class BundledWorkspacePacketGatewayBackend : IHostWorkspacePacketGatewayBackend
{
    private const string HostHelperBaseName = "asura-workspace-gateway";
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private readonly string? _hostHelperExecutable;
    private readonly IReadOnlyDictionary<NetworkConnectionKind, INetworkConnectionProvider> _providers;
    private readonly IWorkspaceGuestPacketRouterLauncher _guestLauncher;
    private readonly IWorkspaceGatewayProcessRunner _processes;
    private readonly IWorkspaceGatewayDnsSource _dnsSource;
    private readonly IWorkspaceVpnPacketRouteLauncher? _openConnectLauncher;

    internal BundledWorkspacePacketGatewayBackend(
        IEnumerable<INetworkConnectionProvider> providers,
        IWorkspaceGuestPacketRouterLauncher guestLauncher,
        IWorkspaceGatewayProcessRunner processes,
        string? hostHelperExecutable,
        IWorkspaceGatewayDnsSource? dnsSource = null,
        IWorkspaceVpnPacketRouteLauncher? openConnectLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _guestLauncher = guestLauncher ?? throw new ArgumentNullException(nameof(guestLauncher));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _dnsSource = dnsSource ?? new SystemWorkspaceGatewayDnsSource();
        _hostHelperExecutable = hostHelperExecutable;
        _openConnectLauncher = openConnectLauncher;
        _providers = providers.ToDictionary(provider => provider.Kind);
    }

    public async ValueTask<NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>>
        OpenAsync(
            WorkspacePacketGatewayOpenRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var network = request.Isolation.Network;
        if (network?.PacketSocketPath is not { } packetSocketPath
            || !Path.IsPathFullyQualified(packetSocketPath))
        {
            return Fail(
                NetworkConnectionErrorCode.InvalidConfiguration,
                "workspace_packet_gateway_socket_missing",
                "The workspace environment does not expose its packet gateway socket.",
                retryable: false);
        }

        if (string.IsNullOrWhiteSpace(_hostHelperExecutable))
        {
            return Fail(
                NetworkConnectionErrorCode.RuntimeMissing,
                "workspace_packet_gateway_helper_missing",
                "The workspace network helper is missing from this Asura installation.",
                retryable: false);
        }

        if (request.Connection?.ConnectionKind is NetworkConnectionKind.AnyConnect
                or NetworkConnectionKind.WireGuard or NetworkConnectionKind.OpenVpn
            && _openConnectLauncher is null)
        {
            return Fail(
                NetworkConnectionErrorCode.RuntimeMissing,
                "workspace_packet_gateway_anyconnect_raw_adapter_missing",
                "Cisco AnyConnect currently exposes only a host SOCKS route. Isolated workspaces require the OpenConnect raw-packet adapter, which this build does not include.",
                retryable: false);
        }

        var authenticationKey = WorkspacePacketChannel.CreateAuthenticationKey();
        IWorkspaceGatewayProcess? guest = null;
        INetworkConnectionSession? providerSession = null;
        IWorkspaceGatewayProcess? host = null;
        try
        {
            progress?.Report(new NetworkConnectionProgress("Starting the workspace packet router…"));
            guest = await _guestLauncher.StartAsync(
                    request.Isolation,
                    authenticationKey,
                    cancellationToken)
                .ConfigureAwait(false);

            if (request.Connection is
                {
                    ConnectionKind: NetworkConnectionKind.AnyConnect
                        or NetworkConnectionKind.WireGuard or NetworkConnectionKind.OpenVpn,
                } anyConnect)
            {
                var rawRoute = await _openConnectLauncher!.StartAsync(
                        new WorkspaceVpnPacketRouteRequest(
                            request.Isolation,
                            anyConnect,
                            request.TransientPassword,
                            authenticationKey),
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (rawRoute is NetworkConnectionResult<WorkspaceGatewayProcessStart>.Failure
                    rawFailure)
                {
                    return NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Fail(
                        rawFailure.Error);
                }

                var startedRawRoute =
                    ((NetworkConnectionResult<WorkspaceGatewayProcessStart>.Success)rawRoute).Value;
                host = startedRawRoute.Process;
                var rawCapabilities = CapabilitiesForAttachment(ParseCapabilities(startedRawRoute.ReadinessLine), network);
                if (guest.HasExited || host.HasExited)
                {
                    return Fail(
                        NetworkConnectionErrorCode.ConnectionFailed,
                        "workspace_packet_gateway_start_race",
                        "The workspace packet route stopped while it was starting.",
                        retryable: true);
                }

                IHostWorkspacePacketGatewayBackendSession rawSession = new ProcessBackedSession(
                    rawCapabilities,
                    host,
                    guest,
                    provider: null,
                    connectionKind: anyConnect.ConnectionKind);
                host = null;
                guest = null;
                return NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Succeed(
                    rawSession);
            }

            Uri? upstream = null;
            IReadOnlyList<IPAddress> dnsServers;
            if (request.ServiceProxy is { } serviceProxy)
            {
                // This route was authenticated by the workspace/SSH owner. Probing a
                // public address here would incorrectly reject private-only SSH routes.
                upstream = serviceProxy.Endpoint;
                dnsServers = [];
            }
            else if (request.Connection is { } connection)
            {
                var providerResult = await OpenProviderAsync(
                        request,
                        connection,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (providerResult is NetworkConnectionResult<INetworkConnectionSession>.Failure providerFailure)
                {
                    return NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Fail(
                        providerFailure.Error);
                }

                providerSession =
                    ((NetworkConnectionResult<INetworkConnectionSession>.Success)providerResult).Value;
                upstream = providerSession.Egress.ProxyEndpoint;
                if (!IsUsableLoopbackUpstream(upstream))
                {
                    return Fail(
                        NetworkConnectionErrorCode.RouteUnavailable,
                        "workspace_packet_gateway_provider_route_unsupported",
                        "The selected connection does not expose a host route the workspace gateway can use.",
                        retryable: false);
                }

                dnsServers = connection.ConnectionKind == NetworkConnectionKind.Proxy
                    ? [IPAddress.Parse("1.1.1.1")]
                    : providerSession.DnsServers;
                if (dnsServers.Count == 0)
                {
                    return Fail(
                        NetworkConnectionErrorCode.RouteUnavailable,
                        "workspace_packet_gateway_provider_dns_missing",
                        "The selected connection did not provide DNS settings for the isolated workspace.",
                        retryable: false);
                }
            }
            else
            {
                dnsServers = await _dnsSource.GetHostDnsServersAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (dnsServers.Count == 0)
                {
                    return Fail(
                        NetworkConnectionErrorCode.RouteUnavailable,
                        "workspace_packet_gateway_host_dns_missing",
                        "Asura could not read the host DNS settings for the isolated workspace.",
                        retryable: true);
                }
            }

            progress?.Report(new NetworkConnectionProgress("Connecting the host packet gateway…"));
            var hostInput = CreateHostInput(authenticationKey, request.ServiceProxy);
            WorkspaceGatewayProcessStart started;
            try
            {
                started = await _processes.StartAsync(
                    new WorkspaceGatewayProcessRequest(
                        _hostHelperExecutable,
                        HostArguments(packetSocketPath, upstream, dnsServers,
                            request.Connection?.ConnectionKind == NetworkConnectionKind.Proxy,
                            request.Connection?.ConnectionKind == NetworkConnectionKind.Tailscale
                                && providerSession?.SupportsUdpAssociate == true,
                            request.ServiceProxy is not null),
                        hostInput),
                    ReadinessTimeout,
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(hostInput);
            }
            host = started.Process;
            var capabilities = CapabilitiesForAttachment(ParseCapabilities(started.ReadinessLine), network);
            if (guest.HasExited || host.HasExited || ProviderHasFailed(providerSession))
            {
                return Fail(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    "workspace_packet_gateway_start_race",
                    "The workspace packet route stopped while it was starting.",
                    retryable: true);
            }

            IHostWorkspacePacketGatewayBackendSession session = new ProcessBackedSession(
                capabilities,
                host,
                guest,
                providerSession,
                request.Connection?.ConnectionKind);
            host = null;
            guest = null;
            providerSession = null;
            return NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Succeed(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(
                NetworkConnectionErrorCode.Cancelled,
                "workspace_packet_gateway_start_cancelled",
                "Starting the workspace packet route was cancelled.",
                retryable: false);
        }
        catch (FileNotFoundException exception)
        {
            SecretSafeDiagnosticProjection.WriteTrace("workspace.packet-gateway.helper.missing", exception);
            return Fail(
                NetworkConnectionErrorCode.RuntimeMissing,
                "workspace_packet_gateway_runtime_missing",
                "The workspace packet router could not be started because a required helper is missing.",
                retryable: false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            SecretSafeDiagnosticProjection.WriteTrace("workspace.packet-gateway.helper.failed", exception);
            var guestDiagnostic = await ReadFailureDiagnosticAsync(guest).ConfigureAwait(false);
            return Fail(
                NetworkConnectionErrorCode.ConnectionFailed,
                "workspace_packet_gateway_helper_failed",
                $"The workspace packet route could not be started. {exception.Message}"
                + (string.IsNullOrWhiteSpace(guestDiagnostic)
                    ? string.Empty
                    : $" Guest router: {guestDiagnostic}"),
                retryable: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authenticationKey);
            if (host is not null)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }

            if (providerSession is not null)
            {
                await providerSession.DisposeAsync().ConfigureAwait(false);
            }

            if (guest is not null)
            {
                await guest.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static string? ResolveHostHelperExecutable(IConnectionExecutableLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        var runtimeIdentifier = (OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, Architecture.Arm64) => "osx-arm64",
            (true, Architecture.X64) => "osx-x64",
            (false, Architecture.Arm64) when OperatingSystem.IsLinux() => "linux-arm64",
            (false, Architecture.X64) when OperatingSystem.IsLinux() => "linux-x64",
            _ => null,
        };
        if (runtimeIdentifier is null)
        {
            return null;
        }

        var operatingSystem = OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var artifactName = $"{HostHelperBaseName}-{operatingSystem}-{(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64")}";
        var bundled = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            runtimeIdentifier,
            "native",
            artifactName);
        return File.Exists(bundled)
            ? bundled
            : locator.Find(artifactName) ?? locator.Find("asura-workspace-network");
    }

    private async ValueTask<NetworkConnectionResult<INetworkConnectionSession>> OpenProviderAsync(
        WorkspacePacketGatewayOpenRequest request,
        NetworkConnectionProfile connection,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(connection.ConnectionKind, out var provider))
        {
            return NetworkConnectionResult<INetworkConnectionSession>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.RuntimeMissing,
                "workspace_packet_gateway_provider_missing",
                $"The {connection.ConnectionKind} network provider is unavailable.",
                retryable: false));
        }

        return await provider.ConnectAsync(
                new NetworkConnectionStartRequest(
                    request.WorkspaceId,
                    connection,
                    WorkspaceNetworkPlacement.Host,
                    killSwitchEnabled: true,
                    request.TransientPassword),
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<string> HostArguments(
        string socketPath,
        Uri? upstream,
        IReadOnlyList<IPAddress> dnsServers,
        bool dnsOverHttps,
        bool allowUdpAssociate,
        bool resolveProxyNames)
    {
        var arguments = new List<string>
        {
            "host",
            "--socket",
            socketPath,
            "--mode",
            upstream?.Scheme ?? "direct",
            "--mtu",
            WorkspacePacketRouteCapabilities.MinimumIpv6Mtu.ToString(CultureInfo.InvariantCulture),
        };
        if (upstream is not null)
        {
            arguments.Add("--upstream-host");
            arguments.Add(upstream.Host);
            arguments.Add("--upstream-port");
            arguments.Add(upstream.Port.ToString(CultureInfo.InvariantCulture));
        }

        if (dnsOverHttps)
        {
            arguments.Add("--dns-over-https");
        }

        if (resolveProxyNames)
        {
            arguments.Add("--resolve-proxy-names");
        }

        if (allowUdpAssociate)
        {
            arguments.Add("--allow-udp-associate");
        }

        foreach (var dnsServer in dnsServers.Take(4))
        {
            arguments.Add("--dns");
            arguments.Add(dnsServer.ToString());
        }

        return arguments;
    }

    internal static byte[] CreateHostInput(ReadOnlySpan<byte> authenticationKey, WorkspacePacketGatewayServiceProxy? proxy)
    {
        if (proxy is null) { return authenticationKey.ToArray(); }
        var usernameBytes = Encoding.UTF8.GetByteCount(proxy.Credentials.Username);
        var passwordBytes = Encoding.UTF8.GetByteCount(proxy.Credentials.Password);
        var result = new byte[authenticationKey.Length + 2 + usernameBytes + passwordBytes];
        authenticationKey.CopyTo(result);
        var offset = authenticationKey.Length;
        result[offset++] = checked((byte)usernameBytes);
        offset += Encoding.UTF8.GetBytes(proxy.Credentials.Username, result.AsSpan(offset));
        result[offset++] = checked((byte)passwordBytes);
        _ = Encoding.UTF8.GetBytes(proxy.Credentials.Password, result.AsSpan(offset));
        return result;
    }

    private static bool IsUsableLoopbackUpstream(Uri? endpoint) =>
        endpoint is { IsAbsoluteUri: true, Port: > 0 }
        && endpoint.Scheme is "socks5" or "http" or "https"
        && IPAddress.TryParse(endpoint.Host, out var address)
        && IPAddress.IsLoopback(address);

    private static bool ProviderHasFailed(INetworkConnectionSession? session) =>
        session is not null && session.Snapshot.State != NetworkConnectionState.Connected;

    private static async ValueTask<string> ReadFailureDiagnosticAsync(
        IWorkspaceGatewayProcess? process)
    {
        if (process is null)
        {
            return string.Empty;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var diagnostic = process.Diagnostic;
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                return diagnostic;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }

        return process.Diagnostic;
    }

    internal static WorkspacePacketRouteCapabilities CapabilitiesForAttachment(
        WorkspacePacketRouteCapabilities provider,
        WorkspaceIsolationNetworkBinding network) => network.HostAttachment is null
        ? provider
        : new WorkspacePacketRouteCapabilities(
            provider.AddressFamilies,
            provider.Protocols & ~WorkspaceIpProtocolCapabilities.Other,
            Math.Min(provider.MaximumPacketSize, WorkspacePacketRouteCapabilities.MinimumIpv6Mtu));

    private static WorkspacePacketRouteCapabilities ParseCapabilities(string readinessLine)
    {
        const string prefix = "READY v1 families=";
        if (!readinessLine.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The host gateway returned an invalid readiness response.");
        }

        var parts = readinessLine[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !parts[1].StartsWith("protocols=", StringComparison.Ordinal)
            || !parts[2].StartsWith("mtu=", StringComparison.Ordinal)
            || !int.TryParse(parts[2]["mtu=".Length..], CultureInfo.InvariantCulture, out var mtu))
        {
            throw new InvalidDataException("The host gateway returned malformed route capabilities.");
        }

        var families = ParseFlags(
            parts[0],
            ("ipv4", WorkspaceIpAddressFamilies.Ipv4),
            ("ipv6", WorkspaceIpAddressFamilies.Ipv6));
        var protocols = ParseFlags(
            parts[1]["protocols=".Length..],
            ("tcp", WorkspaceIpProtocolCapabilities.Tcp),
            ("udp", WorkspaceIpProtocolCapabilities.Udp),
            ("control", WorkspaceIpProtocolCapabilities.ControlMessages),
            ("other", WorkspaceIpProtocolCapabilities.Other));
        return new WorkspacePacketRouteCapabilities(families, protocols, mtu);
    }

    private static TFlags ParseFlags<TFlags>(
        string text,
        params (string Name, TFlags Flag)[] known)
        where TFlags : struct, Enum
    {
        long result = 0;
        foreach (var name in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = known.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (match.Name is null)
            {
                throw new InvalidDataException("The host gateway reported an unknown route capability.");
            }

            result |= Convert.ToInt64(match.Flag, CultureInfo.InvariantCulture);
        }

        return (TFlags)Enum.ToObject(typeof(TFlags), result);
    }

    private static NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession> Fail(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) =>
        NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Fail(
            new NetworkConnectionError(code, stableCode, message, retryable));

    private sealed class ProcessBackedSession : IHostWorkspacePacketGatewayBackendSession
    {
        private readonly IWorkspaceGatewayProcess _host;
        private readonly IWorkspaceGatewayProcess _guest;
        private readonly INetworkConnectionSession? _provider;
        private readonly NetworkConnectionKind? _connectionKind;
        private NetworkConnectionError? _failure;
        private int _failed;
        private int _disposed;

        public ProcessBackedSession(
            WorkspacePacketRouteCapabilities capabilities,
            IWorkspaceGatewayProcess host,
            IWorkspaceGatewayProcess guest,
            INetworkConnectionSession? provider,
            NetworkConnectionKind? connectionKind)
        {
            Capabilities = capabilities;
            _host = host;
            _guest = guest;
            _provider = provider;
            _connectionKind = connectionKind;
            _host.Exited += OnHostExited;
            _guest.Exited += OnGuestExited;
            if (_provider is { } existingProvider)
            {
                existingProvider.Changed += OnProviderChanged;
            }

            if (_host.HasExited || _guest.HasExited || ProviderHasFailed(_provider))
            {
                FailClosed(
                    "workspace_packet_gateway_start_race",
                    "The workspace packet route stopped while it was starting.");
            }
        }

        public WorkspacePacketRouteCapabilities Capabilities { get; }

        public NetworkConnectionError? Failure => Volatile.Read(ref _failure);

        public event EventHandler<NetworkConnectionError>? Failed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _host.Exited -= OnHostExited;
            _guest.Exited -= OnGuestExited;
            if (_provider is { } provider)
            {
                provider.Changed -= OnProviderChanged;
            }

            _host.Stop();
            await _host.DisposeAsync().ConfigureAwait(false);
            if (_provider is { } providerToDispose)
            {
                await providerToDispose.DisposeAsync().ConfigureAwait(false);
            }

            _guest.Stop();
            await _guest.DisposeAsync().ConfigureAwait(false);
            Failed = null;
        }

        private void OnGuestExited(object? sender, EventArgs args)
        {
            var failure = DescribeStoppedProcess("The workspace packet router", _guest);
            FailClosed(
                "workspace_packet_gateway_guest_exited",
                failure.Message,
                $"workspace.packet-gateway.guest.{failure.Code}");
        }

        private void OnHostExited(object? sender, EventArgs args)
        {
            var component = _connectionKind == NetworkConnectionKind.AnyConnect
                ? "Cisco AnyConnect"
                : "The host packet gateway";
            var failure = DescribeStoppedProcess(component, _host);
            FailClosed(
                _connectionKind == NetworkConnectionKind.AnyConnect
                    ? "workspace_anyconnect_process_exited"
                    : "workspace_packet_gateway_host_exited",
                failure.Message,
                _connectionKind == NetworkConnectionKind.AnyConnect
                    ? $"workspace.anyconnect.process.{failure.Code}"
                    : $"workspace.packet-gateway.host.{failure.Code}");
        }

        private void OnProviderChanged(object? sender, NetworkConnectionSnapshot snapshot)
        {
            if (snapshot.State is NetworkConnectionState.Disconnecting
                or NetworkConnectionState.Disconnected
                or NetworkConnectionState.Failed)
            {
                FailClosed(
                    "workspace_packet_gateway_provider_exited",
                    "The selected workspace network connection stopped unexpectedly.");
            }
        }

        private void FailClosed(string stableCode, string message, string? diagnosticCode = null)
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _failed, 1) != 0)
            {
                return;
            }

            // Killing the data-plane process is synchronous so observers cannot see a failed
            // session while packets are still able to leave through its previous upstream.
            var error = new NetworkConnectionError(
                NetworkConnectionErrorCode.ConnectionFailed,
                stableCode,
                message,
                retryable: true);
            Volatile.Write(ref _failure, error);
            if (diagnosticCode is not null)
            {
                // Log only the winning failure, before cleanup stops the peer.
                // Exit codes and reason tokens are closed metadata, never raw stderr.
                SecretSafeDiagnosticProjection.WriteStandardError(
                    diagnosticCode,
                    SecretSafeDiagnosticKind.Unexpected);
            }

            _host.Stop();
            Failed?.Invoke(this, error);
        }

        private static (string Message, string Code) DescribeStoppedProcess(
            string component,
            IWorkspaceGatewayProcess process)
        {
            var message = process.ExitCode is { } exitCode
                ? $"{component} stopped unexpectedly. Exit code: {exitCode}."
                : $"{component} stopped unexpectedly.";
            // Project only known failure categories. OpenConnect output can
            // contain credentials and private gateway addresses.
            var diagnostic = process.Diagnostic;
            var (reasonCode, reason) = diagnostic switch
            {
                _ when diagnostic.Contains("ASURA_GATEWAY_EXIT_REASON=provider-channel-closed", StringComparison.Ordinal) =>
                    ("provider-channel-closed", "The VPN-to-workspace packet channel closed."),
                _ when diagnostic.Contains("ASURA_GATEWAY_EXIT_REASON=provider-channel-failed", StringComparison.Ordinal) =>
                    ("provider-channel-failed", "The VPN-to-workspace packet channel failed."),
                _ when diagnostic.Contains("ASURA_GATEWAY_EXIT_REASON=provider-handshake-failed", StringComparison.Ordinal) =>
                    ("provider-handshake-failed", "The VPN packet channel could not be established."),
                _ when diagnostic.Contains("ASURA_GATEWAY_EXIT_REASON=workspace-nic-congestion", StringComparison.Ordinal) =>
                    ("workspace-nic-congestion", "The host workspace network interface ran out of buffer space."),
                _ when diagnostic.Contains("ASURA_GATEWAY_EXIT_REASON=workspace-nic-failed", StringComparison.Ordinal) =>
                    ("workspace-nic-failed", "The host workspace network interface failed."),
                _ when diagnostic.Contains("ASURA_GATEWAY_EXIT_REASON=protocol-authentication", StringComparison.Ordinal) =>
                    ("protocol-authentication", "The packet channel failed authentication."),
                _ when diagnostic.Contains("ASURA_GATEWAY_EXIT_REASON=protocol-malformed", StringComparison.Ordinal) =>
                    ("protocol-malformed", "The packet channel received a malformed packet."),
                _ when diagnostic.Contains("ASURA_GATEWAY_EXIT_REASON=protocol-sequence", StringComparison.Ordinal) =>
                    ("protocol-sequence", "The packet channel lost packet ordering."),
                _ when diagnostic.Contains("ASURA_GATEWAY_EXIT_REASON=runtime-panic", StringComparison.Ordinal) =>
                    ("runtime-panic", "The host Ethernet gateway crashed."),
                _ when diagnostic.Contains("write guest TUN:", StringComparison.Ordinal) =>
                    ("tun-write", "The guest rejected an incoming network packet."),
                _ when diagnostic.Contains("read guest TUN:", StringComparison.Ordinal) =>
                    ("tun-read", "The guest network interface stopped responding."),
                _ when diagnostic.Contains("packet channel authentication failed", StringComparison.Ordinal) =>
                    ("authentication", "The packet channel failed authentication."),
                _ when diagnostic.Contains("packet channel frame is malformed", StringComparison.Ordinal) =>
                    ("malformed", "The packet channel received a malformed packet."),
                _ when diagnostic.Contains("packet channel sequence is invalid", StringComparison.Ordinal) =>
                    ("sequence", "The packet channel lost packet ordering."),
                _ when diagnostic.Contains("short buffer", StringComparison.Ordinal) =>
                    ("short-buffer", "A network packet exceeded the route's buffer size."),
                _ when diagnostic.Contains("receive host packet:", StringComparison.Ordinal)
                    || diagnostic.Contains("receive guest packet for OpenConnect:", StringComparison.Ordinal) =>
                    ("channel-closed", "The host-to-workspace packet channel closed."),
                _ when diagnostic.Contains("no buffer space available", StringComparison.Ordinal) =>
                    ("socket-congestion", "The VPN packet socket ran out of buffer space."),
                _ when diagnostic.Contains("read OpenConnect VPNFD:", StringComparison.Ordinal)
                    || diagnostic.Contains("write OpenConnect VPNFD:", StringComparison.Ordinal) =>
                    ("vpn-socket", "The VPN packet socket stopped responding."),
                _ => ("unknown", (string?)null),
            };
            var exit = process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            return (
                reason is null ? message : $"{message} {reason}",
                $"exited.exit-{exit}.{reasonCode}");
        }
    }
}

public static class WorkspacePacketGatewayRuntimeFactory
{
    public static IWorkspacePacketGatewayRuntime Create(
        IEnumerable<INetworkConnectionProvider> providers,
        IConnectionExecutableLocator executableLocator,
        ISecretVault secretVault)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(executableLocator);
        ArgumentNullException.ThrowIfNull(secretVault);
        var providerSnapshot = providers.ToArray();
        var processes = new WorkspaceGatewayProcessRunner();
        var hostHelper = BundledWorkspacePacketGatewayBackend.ResolveHostHelperExecutable(
            executableLocator);
        var guest = new WorkspaceHostNetworkRouteLauncher(
            WorkspaceSdkIsolationProvider.FindBundledRuntime(),
            processes);
        var openConnect = new WorkspaceVpnPacketRouteLauncher(
            secretVault,
            executableLocator,
            processes,
            hostHelper);
        var backend = new BundledWorkspacePacketGatewayBackend(
            providerSnapshot,
            guest,
            processes,
            hostHelper,
            new SystemWorkspaceGatewayDnsSource(),
            openConnect);
        return new HostWorkspacePacketGatewayRuntime(backend);
    }
}

internal sealed class WorkspaceGatewayProcessRunner : IWorkspaceGatewayProcessRunner
{
    private const int MaximumDiagnosticCharacters = 32 * 1024;

    public async ValueTask<WorkspaceGatewayProcessStart> StartAsync(
        WorkspaceGatewayProcessRequest request,
        TimeSpan readinessTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (readinessTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readinessTimeout));
        }

        var startInfo = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new IOException("The workspace gateway helper could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new IOException("The workspace gateway helper could not be started.", exception);
        }

        var running = new RunningProcess(process);
        try
        {
            await WriteKeyAsync(process, request.StandardInput, cancellationToken)
                .ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(readinessTimeout);
            string? readiness = null;
            try
            {
                var ignoredCharacters = 0;
                while (ignoredCharacters <= 32 * 1024)
                {
                    var line = await process.StandardOutput.ReadLineAsync(deadline.Token)
                        .ConfigureAwait(false);
                    if (line is null || line.StartsWith("READY ", StringComparison.Ordinal))
                    {
                        readiness = line;
                        break;
                    }

                    ignoredCharacters += line.Length;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The workspace gateway helper did not become ready in time.");
            }

            if (readiness is null || process.HasExited)
            {
                running.Stop();
                var diagnostic = await running.ReadDiagnosticAsync().ConfigureAwait(false);
                throw new IOException(
                    string.IsNullOrWhiteSpace(diagnostic)
                        ? "The workspace gateway helper stopped before it became ready."
                        : $"The workspace gateway helper stopped before it became ready: {diagnostic}");
            }

            running.DrainRemainingOutput();
            return new WorkspaceGatewayProcessStart(running, readiness);
        }
        catch
        {
            await running.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<WorkspaceGatewayCommandResult> RunAsync(
        WorkspaceGatewayProcessRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startInfo = CreateStartInfo(request);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new IOException("The workspace gateway command could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new IOException("The workspace gateway command could not be started.", exception);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // Drain both pipes before writing: a child may emit output before it reads stdin.
            var standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var standardError = process.StandardError.ReadToEndAsync(deadline.Token);
            await WriteKeyAsync(process, request.StandardInput, deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var diagnostic = string.Join(
                    Environment.NewLine,
                    new[] { await standardError.ConfigureAwait(false), await standardOutput.ConfigureAwait(false) }
                        .Where(value => !string.IsNullOrWhiteSpace(value)))
                .Trim();
            return new WorkspaceGatewayCommandResult(
                process.ExitCode,
                diagnostic.Length <= MaximumDiagnosticCharacters
                    ? diagnostic
                    : diagnostic[..MaximumDiagnosticCharacters]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new IOException("The workspace gateway command did not finish in time.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(WorkspaceGatewayProcessRequest request)
    {
        var startInfo = new ProcessStartInfo(request.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
        }
    }

    private static async Task WriteKeyAsync(
        Process process,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(key, cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private sealed class RunningProcess : IWorkspaceGatewayProcess
    {
        private const int MaximumDiagnosticCharacters = 32 * 1024;
        private readonly Process _process;
        private readonly StringBuilder _diagnostic = new(MaximumDiagnosticCharacters);
        private readonly object _diagnosticGate = new();
        private readonly CancellationTokenSource _drainLifetime = new();
        private readonly Task _standardError;
        private Task _standardOutput = Task.CompletedTask;
        private int _disposed;

        public RunningProcess(Process process)
        {
            _process = process;
            _standardError = DrainAsync(process.StandardError);
            _process.Exited += OnExited;
        }

        public bool HasExited => _process.HasExited;

        public int? ExitCode
        {
            get
            {
                try
                {
                    return _process.HasExited ? _process.ExitCode : null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        public string Diagnostic
        {
            get
            {
                lock (_diagnosticGate)
                {
                    return _diagnostic.ToString().Trim();
                }
            }
        }

        public event EventHandler? Exited;

        public void DrainRemainingOutput() =>
            _standardOutput = DrainAsync(_process.StandardOutput);

        public async Task<string> ReadDiagnosticAsync()
        {
            try
            {
                await _standardError.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A reparented helper may retain this pipe after the VPN process exits.
            }
            lock (_diagnosticGate)
            {
                return _diagnostic.ToString().Trim();
            }
        }

        public void Stop()
        {
            if (_process.HasExited)
            {
                return;
            }

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _process.Exited -= OnExited;
            Stop();
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
                // Reaping the parent does not close descriptors inherited by orphaned
                // children. Diagnostic collection must not prevent route replacement.
                _drainLifetime.CancelAfter(TimeSpan.FromMilliseconds(250));
                await Task.WhenAll(_standardOutput, _standardError).ConfigureAwait(false);
            }
            finally
            {
                _process.Dispose();
                _drainLifetime.Dispose();
                Exited = null;
            }
        }

        private async void OnExited(object? sender, EventArgs args)
        {
            try
            {
                // The OS exit event can precede the final stderr read. Give the
                // drain a bounded opportunity to capture the helper's reason.
                await _standardError.WaitAsync(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException
                or ObjectDisposedException)
            {
            }

            if (Volatile.Read(ref _disposed) == 0)
            {
                Exited?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[2048];
            while (true)
            {
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer.AsMemory(), _drainLifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_drainLifetime.IsCancellationRequested)
                {
                    return;
                }
                if (read == 0)
                {
                    return;
                }

                lock (_diagnosticGate)
                {
                    var remaining = MaximumDiagnosticCharacters - _diagnostic.Length;
                    if (remaining > 0)
                    {
                        _diagnostic.Append(buffer, 0, Math.Min(read, remaining));
                    }
                }
            }
        }
    }
}
