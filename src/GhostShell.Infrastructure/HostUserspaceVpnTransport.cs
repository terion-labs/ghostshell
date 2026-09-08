using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal interface IHostUserspaceVpnTransport
{
    ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
        NetworkConnectionStartRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Starts only userspace VPN engines that expose a loopback SOCKS5 listener. It never
/// asks an engine to create a TUN device and never changes host routes.
/// </summary>
internal sealed class HostUserspaceVpnTransport : IHostUserspaceVpnTransport
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);
    private const int LoopbackBindAttempts = 3;
    private readonly NetworkConnectionKind _kind;
    private readonly ISecretVault _secretVault;
    private readonly IConnectionExecutableLocator _executableLocator;
    private readonly IHostVpnProcessRunner _processRunner;
    private readonly string _persistentStateRoot;

    public HostUserspaceVpnTransport(
        NetworkConnectionKind kind,
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator,
        string? persistentStateRoot = null)
        : this(
            kind,
            secretVault,
            executableLocator,
            new HostUserspaceVpnProcessRunner(),
            persistentStateRoot ?? Path.Combine(GhostShellDataPaths.CreateDefault().DataDirectory, "vpn-state"))
    {
    }

    internal HostUserspaceVpnTransport(
        NetworkConnectionKind kind,
        ISecretVault secretVault,
        IConnectionExecutableLocator executableLocator,
        IHostVpnProcessRunner processRunner,
        string persistentStateRoot)
    {
        if (kind is not (
                NetworkConnectionKind.WireGuard
                or NetworkConnectionKind.OpenVpn
                or NetworkConnectionKind.AnyConnect
                or NetworkConnectionKind.Tailscale))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        _kind = kind;
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _executableLocator = executableLocator
            ?? throw new ArgumentNullException(nameof(executableLocator));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

        ArgumentException.ThrowIfNullOrWhiteSpace(persistentStateRoot);
        _persistentStateRoot = Path.GetFullPath(persistentStateRoot);
    }

    public ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
        NetworkConnectionStartRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Placement is not WorkspaceNetworkPlacement.HostPlacement)
        {
            throw new ArgumentException(
                "A host userspace VPN transport requires host placement.",
                nameof(request));
        }

        return _kind switch
        {
            NetworkConnectionKind.WireGuard or NetworkConnectionKind.OpenVpn => ConnectPacketVpnAsync(
                request,
                progress,
                cancellationToken),
            NetworkConnectionKind.AnyConnect => ConnectAnyConnectAsync(
                request,
                progress,
                cancellationToken),
            NetworkConnectionKind.Tailscale => ConnectTailscaleAsync(
                request,
                progress,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(_kind), _kind, null),
        };
    }

    private async ValueTask<NetworkConnectionResult<INetworkConnectionSession>>
        ConnectPacketVpnAsync(
            NetworkConnectionStartRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
    {
        var profile = request.Connection;
        var openVpn = profile.Configuration as NetworkConnectionConfiguration.OpenVpn;
        var name = openVpn is null ? "WireGuard" : "OpenVPN";
        var reference = profile.Configuration switch
        {
            NetworkConnectionConfiguration.WireGuard wireGuard => wireGuard.ConfigurationSecret,
            NetworkConnectionConfiguration.OpenVpn configuration => configuration.ConfigurationSecret,
            _ => throw new ArgumentException("A file-configured VPN is required.", nameof(request)),
        };
        var executable = BundledWorkspacePacketGatewayBackend.ResolveHostHelperExecutable(_executableLocator);
        var engine = openVpn is null ? null : _executableLocator.Find("ghostshell-openvpn-engine");
        if (executable is null || (openVpn is not null && engine is null))
        {
            return RuntimeMissing(
                "packet_vpn_host_runtime_missing",
                $"The bundled {name} engine is missing or invalid. Reinstall or update GhostSHELL.");
        }

        var resolved = await ResolveSecretAsync(
                profile.Id,
                reference,
                $"{name} configuration",
                isCredential: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
        {
            return NetworkConnectionResult<INetworkConnectionSession>.Fail(failure.Error);
        }

        var secret = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
        byte[]? password = null;
        byte[]? input = null;
        try
        {
            await using var temporary = SecureHostVpnDirectory.Create();
            var dnsServers = openVpn is null ? ParseWireGuardDnsServers(secret) : [];
            if (openVpn is not null)
            {
                if (openVpn.PasswordSecret is { } passwordReference)
                {
                    var credentials = await ResolveSecretAsync(profile.Id, passwordReference,
                        "OpenVPN password", isCredential: true, cancellationToken).ConfigureAwait(false);
                    if (credentials is NetworkConnectionResult<byte[]>.Failure credentialFailure)
                    {
                        return NetworkConnectionResult<INetworkConnectionSession>.Fail(credentialFailure.Error);
                    }

                    password = ((NetworkConnectionResult<byte[]>.Success)credentials).Value;
                }
                else if (request.TransientPassword is { } transient)
                {
                    password = new byte[transient.Length];
                    transient.CopyTo(password);
                }

                input = new byte[(password?.Length ?? 0) + 1];
                password?.CopyTo(input, 0);
                input[^1] = (byte)'\n';
            }

            for (var attempt = 0; attempt < LoopbackBindAttempts; attempt++)
            {
                var port = AllocateLoopbackPort();
                var configurationPath = await temporary.WriteAsync(
                        $"vpn-{attempt}.conf",
                        [secret],
                        cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(new NetworkConnectionProgress(
                    $"Connecting {name} through the app-scoped userspace network…"));
                var arguments = new List<string>
                    {
                        openVpn is null ? "wireguard" : "openvpn", "--config", configurationPath,
                        "--socks-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    };
                if (openVpn is not null)
                {
                    arguments.AddRange(["--engine", engine!]);
                    if (openVpn.Username is not null)
                    {
                        arguments.AddRange(["--username", openVpn.Username]);
                    }
                }

                var process = await StartProcessAsync(
                        new HostVpnProcessRequest(
                            executable,
                            arguments,
                            input ?? ReadOnlyMemory<byte>.Empty),
                        name,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (process is NetworkConnectionResult<IHostVpnProcess>.Failure startFailure)
                {
                    return NetworkConnectionResult<INetworkConnectionSession>.Fail(
                        startFailure.Error);
                }

                var running = ((NetworkConnectionResult<IHostVpnProcess>.Success)process).Value;
                var listener = await WaitForListenerOrDisposeAsync(
                        running,
                        port,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (listener.Ready)
                {
                    var directory = temporary.TransferOwnership();
                    return Succeed(
                        profile.Id,
                        name,
                        port,
                        [running],
                        directory,
                        dnsServers);
                }

                if (!listener.ProcessExited || !IsLoopbackAddressInUse(listener.Diagnostic)
                    || attempt == LoopbackBindAttempts - 1)
                {
                    return ConnectionFailed(name, listener.Diagnostic);
                }
            }

            throw new InvalidOperationException("The packet VPN startup retry loop did not return.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (password is not null)
            {
                CryptographicOperations.ZeroMemory(password);
            }

            if (input is not null)
            {
                CryptographicOperations.ZeroMemory(input);
            }
        }
    }

    private async ValueTask<NetworkConnectionResult<INetworkConnectionSession>>
        ConnectAnyConnectAsync(
            NetworkConnectionStartRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
    {
        var profile = request.Connection;
        if (profile.Configuration is not NetworkConnectionConfiguration.AnyConnect configuration)
        {
            return InvalidConfiguration("Cisco AnyConnect");
        }

        if (OperatingSystem.IsWindows())
        {
            return RuntimeMissing(
                "anyconnect_host_userspace_windows_unavailable",
                "App-scoped Cisco AnyConnect requires OpenConnect script-tun and the bundled packet helper, whose userspace socket transport is unavailable on Windows.");
        }

        var openconnect = _executableLocator.Find("openconnect");
        var gateway = BundledWorkspacePacketGatewayBackend.ResolveHostHelperExecutable(_executableLocator);
        if (openconnect is null || gateway is null)
        {
            return RuntimeMissing(
                "anyconnect_host_userspace_runtime_missing",
                "The bundled Cisco AnyConnect connection engine is missing or invalid. Reinstall or update GhostSHELL to restore the connection engine payload.");
        }

        byte[]? password = null;
        byte[]? certificate = null;
        byte[]? standardInput = null;
        await using var temporary = SecureHostVpnDirectory.Create();
        try
        {
            var dnsCaptureScript = await temporary.WriteAsync(
                    "capture-openconnect-dns.sh",
                    [Encoding.UTF8.GetBytes(OpenConnectDnsCaptureScript)],
                    cancellationToken)
                .ConfigureAwait(false);
            if (configuration.PasswordSecret is { } passwordReference)
            {
                var resolved = await ResolveSecretAsync(
                        profile.Id,
                        passwordReference,
                        "Cisco AnyConnect password",
                        isCredential: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
                {
                    return NetworkConnectionResult<INetworkConnectionSession>.Fail(failure.Error);
                }

                password = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
            }
            else if (request.TransientPassword is { } transientPassword)
            {
                password = GC.AllocateUninitializedArray<byte>(transientPassword.Length);
                transientPassword.CopyTo(password);
            }

            if (password is not null)
            {
                standardInput = new byte[password.Length + 1];
                password.CopyTo(standardInput, 0);
                standardInput[^1] = (byte)'\n';
            }

            string? certificatePath = null;
            if (configuration.ClientCertificateSecret is { } certificateReference)
            {
                var resolved = await ResolveSecretAsync(
                        profile.Id,
                        certificateReference,
                        "Cisco AnyConnect client certificate",
                        isCredential: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
                {
                    return NetworkConnectionResult<INetworkConnectionSession>.Fail(failure.Error);
                }

                certificate = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
                certificatePath = await temporary.WriteAsync(
                        "client-certificate",
                        [certificate],
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            for (var attempt = 0; attempt < LoopbackBindAttempts; attempt++)
            {
                var port = AllocateLoopbackPort();
                var dnsCapturePath = temporary.PathFor($"openconnect-dns-{attempt}");
                var arguments = new List<string>
                {
                    "--script-tun",
                    "--script",
                    $"/bin/sh {ShellQuote(dnsCaptureScript)} {ShellQuote(dnsCapturePath)} {ShellQuote(gateway)} {port}",
                    "--non-inter",
                    "--force-dpd=30",
                    "--reconnect-timeout=300",
                };
                if (OperatingSystem.IsMacOS())
                {
                    // The bundled OpenSSL has no install-time trust directory. Use the
                    // CA bundle maintained by macOS instead of disabling verification.
                    arguments.Add("--cafile=/etc/ssl/cert.pem");
                }

                if (configuration.Username is not null)
                {
                    arguments.Add("--user");
                    arguments.Add(configuration.Username);
                }

                if (configuration.AuthenticationGroup is not null)
                {
                    arguments.Add("--authgroup");
                    arguments.Add(configuration.AuthenticationGroup);
                }

                if (certificatePath is not null)
                {
                    arguments.Add("--certificate");
                    arguments.Add(certificatePath);
                }

                if (standardInput is not null)
                {
                    arguments.Add("--passwd-on-stdin");
                }

                arguments.Add(configuration.Gateway.AbsoluteUri);
                progress?.Report(new NetworkConnectionProgress(
                    "Connecting Cisco AnyConnect through an app-scoped SOCKS5 proxy…"));
                var process = await StartProcessAsync(
                        new HostVpnProcessRequest(
                            openconnect,
                            arguments,
                            standardInput ?? ReadOnlyMemory<byte>.Empty),
                        "Cisco AnyConnect",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (process is NetworkConnectionResult<IHostVpnProcess>.Failure startFailure)
                {
                    return NetworkConnectionResult<INetworkConnectionSession>.Fail(
                        startFailure.Error);
                }

                var running = ((NetworkConnectionResult<IHostVpnProcess>.Success)process).Value;
                var listener = await WaitForListenerOrDisposeAsync(
                        running,
                        port,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (listener.Ready)
                {
                    var dnsServers = await ReadCapturedDnsServersAsync(
                            dnsCapturePath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var directory = temporary.TransferOwnership();
                    return Succeed(
                        profile.Id,
                        "Cisco AnyConnect",
                        port,
                        [running],
                        directory,
                        dnsServers);
                }

                if (!listener.ProcessExited || !IsLoopbackAddressInUse(listener.Diagnostic)
                    || attempt == LoopbackBindAttempts - 1)
                {
                    return ConnectionFailed("Cisco AnyConnect", listener.Diagnostic);
                }
            }

            throw new InvalidOperationException(
                "The Cisco AnyConnect startup retry loop did not return.");
        }
        finally
        {
            Clear(password);
            Clear(certificate);
            Clear(standardInput);
        }
    }

    private async ValueTask<NetworkConnectionResult<INetworkConnectionSession>>
        ConnectTailscaleAsync(
            NetworkConnectionStartRequest request,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
    {
        if (request.Connection.Configuration is not NetworkConnectionConfiguration.Tailscale
            configuration)
        {
            return InvalidConfiguration("Tailscale");
        }

        if (OperatingSystem.IsWindows())
        {
            return RuntimeMissing(
                "tailscale_host_userspace_windows_unavailable",
                "A private app-scoped tailscaled control socket is not yet available on Windows in this build. GhostSHELL will not reuse the system-wide Tailscale service.");
        }

        var tailscaled = _executableLocator.Find("tailscaled");
        var tailscale = _executableLocator.Find("tailscale");
        if (tailscaled is null || tailscale is null)
        {
            return RuntimeMissing(
                "tailscale_host_userspace_runtime_missing",
                "The bundled Tailscale connection engine is missing or invalid. Reinstall or update GhostSHELL to restore the connection engine payload.");
        }

        var statePath = PersistentTailscaleStatePath(
            request.WorkspaceId,
            request.Connection.Id);
        var hasPersistentIdentity = File.Exists(statePath);
        if (configuration.AuthKeySecret is not { } && !hasPersistentIdentity)
        {
            return Fail(
                NetworkConnectionErrorCode.AuthenticationRequired,
                "tailscale_host_auth_key_required",
                "The first app-scoped Tailscale connection needs a stored reusable auth key because it does not reuse the host Tailscale login.",
                retryable: false);
        }

        byte[]? authKey = null;
        if (configuration.AuthKeySecret is { } authReference)
        {
            var resolved = await ResolveSecretAsync(
                    request.Connection.Id,
                    authReference,
                    "Tailscale auth key",
                    isCredential: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
            {
                return NetworkConnectionResult<INetworkConnectionSession>.Fail(failure.Error);
            }

            authKey = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
        }

        IHostVpnProcess? daemon = null;
        try
        {
            await using var temporary = SecureHostVpnDirectory.Create();
            string? authPath = null;
            if (authKey is not null)
            {
                authPath = await temporary.WriteAsync(
                        "auth-key",
                        [authKey],
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var socketPath = temporary.PathFor("tailscaled.sock");
            var port = AllocateLoopbackPort();
            progress?.Report(new NetworkConnectionProgress(
                "Starting a private Tailscale userspace network…"));
            var daemonResult = await StartProcessAsync(
                    new HostVpnProcessRequest(
                        tailscaled,
                        [
                            $"--state={statePath}",
                            $"--socket={socketPath}",
                            "--tun=userspace-networking",
                            $"--socks5-server=127.0.0.1:{port}",
                        ],
                        ReadOnlyMemory<byte>.Empty),
                    "Tailscale",
                    cancellationToken)
                .ConfigureAwait(false);
            if (daemonResult is NetworkConnectionResult<IHostVpnProcess>.Failure startFailure)
            {
                return NetworkConnectionResult<INetworkConnectionSession>.Fail(startFailure.Error);
            }

            daemon = ((NetworkConnectionResult<IHostVpnProcess>.Success)daemonResult).Value;
            if (!await WaitForPathAsync(
                    socketPath,
                    daemon,
                    CommandTimeout,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return Fail(NetworkConnectionErrorCode.ConnectionFailed,
                    "tailscale_control_socket_unavailable",
                    "The private Tailscale daemon did not create its local control socket.", retryable: true);
            }

            ProtectFile(statePath);

            var upArguments = new List<string>
            {
                $"--socket={socketPath}",
                "up",
                // The CLI resolves names before login, when a fresh daemon has
                // no peers. Clear any persisted selection explicitly so reconnect
                // also satisfies `up`'s requirement to restate changed preferences.
                "--exit-node=",
                "--exit-node-allow-lan-access=false",
                "--accept-routes=true",
                "--shields-up=true",
                $"--hostname=ghostshell-{TokenFor(request.WorkspaceId.Value)}",
            };
            if (authPath is not null)
            {
                upArguments.Add($"--auth-key=file:{authPath}");
            }

            if (configuration.ControlServer is not null)
            {
                upArguments.Add($"--login-server={configuration.ControlServer.AbsoluteUri}");
            }

            progress?.Report(new NetworkConnectionProgress(
                "Authenticating the private Tailscale network…"));
            var up = await RunCommandAsync(
                    new HostVpnProcessRequest(
                        tailscale,
                        upArguments,
                        ReadOnlyMemory<byte>.Empty),
                    cancellationToken)
                .ConfigureAwait(false);
            if (up is null)
            {
                return CommandTimedOut("Tailscale");
            }

            if (up.ExitCode != 0)
            {
                return IsAuthenticationFailure(up.Diagnostic)
                    ? AuthenticationFailed("Tailscale")
                    : Fail(NetworkConnectionErrorCode.ConnectionFailed,
                        "tailscale_login_failed",
                        $"The private Tailscale client could not log in (exit code {up.ExitCode}). Check the auth key, control server, and device approval.",
                        retryable: true);
            }

            // `up` waits for Running and a network map. Resolve the exit node only
            // then. No workspace session or route is published during this gap.
            progress?.Report(new NetworkConnectionProgress("Selecting the Tailscale exit node…"));
            var exitNode = await RunCommandAsync(
                    new HostVpnProcessRequest(
                        tailscale,
                        [
                            $"--socket={socketPath}",
                            "set",
                            $"--exit-node={configuration.ExitNode}",
                            "--exit-node-allow-lan-access=false",
                        ],
                        ReadOnlyMemory<byte>.Empty),
                    cancellationToken)
                .ConfigureAwait(false);
            if (exitNode is null)
            {
                return CommandTimedOut("Tailscale exit-node selection");
            }

            if (exitNode.ExitCode != 0)
            {
                // Map only known CLI error shapes. Raw output can include private
                // peer names, addresses, paths, or server-provided content.
                var diagnostic = exitNode.Diagnostic.Trim();
                var (stableCode, reason) = diagnostic switch
                {
                    _ when diagnostic.Contains("for --exit-node; must be IP or hostname", StringComparison.Ordinal)
                        || diagnostic.Contains("no node found in netmap with IP", StringComparison.Ordinal) =>
                        ("tailscale_exit_node_not_visible",
                            "The selected name or IP is absent from this client's peer map. Check device visibility and exit-node access in the tailnet policy, or try the node's Tailscale IP."),
                    _ when diagnostic.Contains("is not advertising an exit node", StringComparison.Ordinal) =>
                        ("tailscale_exit_node_not_advertised",
                            "Tailscale found the device, but it does not offer an exit node to this client. Check exit-node advertisement and route approval."),
                    _ when diagnostic.Contains("ambiguous exit node name", StringComparison.Ordinal) =>
                        ("tailscale_exit_node_ambiguous",
                            "More than one visible device matches this exit-node name. Use its Tailscale IP or full MagicDNS name."),
                    _ when diagnostic.Contains("it is a local IP address to this machine", StringComparison.Ordinal) =>
                        ("tailscale_exit_node_is_self",
                            "The selected address belongs to GhostShell's private Tailscale client itself. Select a different Tailscale device as the exit node."),
                    _ => ("tailscale_exit_node_failed",
                        "Check that its name matches a visible, approved exit node in this client's tailnet."),
                };
                return Fail(NetworkConnectionErrorCode.ConnectionFailed,
                    stableCode,
                    $"Tailscale logged in, but could not select the exit node (exit code {exitNode.ExitCode}). {reason}",
                    retryable: true);
            }

            var status = await RunCommandAsync(
                    new HostVpnProcessRequest(
                        tailscale,
                        // Read only this node's state. A full peer list can exceed
                        // the bounded process output and is not readiness evidence.
                        [$"--socket={socketPath}", "status", "--json", "--peers=false"],
                        ReadOnlyMemory<byte>.Empty),
                    cancellationToken)
                .ConfigureAwait(false);
            if (status is null || status.ExitCode != 0)
            {
                return Fail(NetworkConnectionErrorCode.ConnectionFailed,
                    "tailscale_status_failed",
                    "Tailscale setup completed, but its local status could not be read.", retryable: true);
            }

            if (!HasRunningTailscaleBackend(status.StandardOutput))
            {
                return IsAuthenticationFailure(status.StandardOutput)
                    ? AuthenticationFailed("Tailscale")
                    : Fail(NetworkConnectionErrorCode.ConnectionFailed,
                        "tailscale_backend_not_running",
                        "Tailscale has not reported a running session. Check device approval in the tailnet admin console, then retry.",
                        retryable: true);
            }

            if (!await _processRunner.WaitForTcpListenerAsync(
                    daemon,
                    port,
                    CommandTimeout,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return Fail(NetworkConnectionErrorCode.ConnectionFailed,
                    "tailscale_socks_listener_unavailable",
                    "Tailscale reports a running session, but its local SOCKS listener is unavailable.", retryable: true);
            }

            var directory = temporary.TransferOwnership();
            var connected = Succeed(
                request.Connection.Id,
                "Tailscale",
                port,
                [daemon],
                directory,
                [IPAddress.Parse("100.100.100.100")],
                supportsUdpAssociate: true);
            daemon = null;
            return connected;
        }
        finally
        {
            Clear(authKey);
            if (daemon is not null)
            {
                await daemon.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<NetworkConnectionResult<byte[]>> ResolveSecretAsync(
        NetworkConnectionId connectionId,
        SecretRef reference,
        string label,
        bool isCredential,
        CancellationToken cancellationToken)
    {
        SecretVaultResult<SecretMaterial> result;
        try
        {
            result = await _secretVault.ResolveAsync(
                    new ResolveSecretRequest(
                        reference,
                        new SecretScope(
                            SecretScopeKind.NetworkConnection,
                            connectionId.Value),
                        new SecretUsePurpose(
                            SecretUseKind.NetworkConnectionAuthentication,
                            connectionId.Value)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NetworkConnectionResult<byte[]>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.Cancelled,
                "host_userspace_vpn_secret_cancelled",
                $"Access to the {label} was cancelled.",
                retryable: false));
        }

        if (result is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            var authentication = failure.Error.Code is
                SecretVaultErrorCode.AuthenticationRequired or SecretVaultErrorCode.UserCancelled;
            return NetworkConnectionResult<byte[]>.Fail(new NetworkConnectionError(
                authentication || isCredential
                    ? NetworkConnectionErrorCode.AuthenticationRequired
                    : NetworkConnectionErrorCode.InvalidConfiguration,
                authentication || isCredential
                    ? "host_userspace_vpn_secret_access_required"
                    : "host_userspace_vpn_configuration_missing",
                authentication || isCredential
                    ? $"Authentication is required to access the {label}."
                    : $"The {label} is unavailable or invalid.",
                failure.Error.Retryable || authentication));
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = GC.AllocateUninitializedArray<byte>(material.Length);
        material.CopyTo(bytes);
        return NetworkConnectionResult<byte[]>.Succeed(bytes);
    }

    private async ValueTask<NetworkConnectionResult<IHostVpnProcess>> StartProcessAsync(
        HostVpnProcessRequest request,
        string displayName,
        CancellationToken cancellationToken)
    {
        try
        {
            var process = await _processRunner.StartAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return NetworkConnectionResult<IHostVpnProcess>.Succeed(process);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NetworkConnectionResult<IHostVpnProcess>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.Cancelled,
                "host_userspace_vpn_cancelled",
                $"Connecting {displayName} was cancelled.",
                retryable: false));
        }
        catch (IOException)
        {
            return NetworkConnectionResult<IHostVpnProcess>.Fail(new NetworkConnectionError(
                NetworkConnectionErrorCode.ConnectionFailed,
                "host_userspace_vpn_process_start_failed",
                $"The {displayName} userspace process could not be started.",
                retryable: true));
        }
    }

    private async ValueTask<HostVpnCommandResult?> RunCommandAsync(
        HostVpnProcessRequest request,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CommandTimeout);
        try
        {
            return await _processRunner.RunAsync(request, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException exception)
        {
            return new HostVpnCommandResult(-1, exception.Message);
        }
    }

    private async ValueTask<HostVpnListenerResult> WaitForListenerOrDisposeAsync(
        IHostVpnProcess process,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ConnectTimeout);
            var announced = await process.RouteReady.WaitAsync(deadline.Token).ConfigureAwait(false);

            if (!announced)
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                var failed = new HostVpnListenerResult(false, process.HasExited, process.Diagnostic);
                await process.DisposeAsync().ConfigureAwait(false);
                return failed;
            }

            var ready = await _processRunner.WaitForTcpListenerAsync(
                    process,
                    port,
                    ConnectTimeout,
                    deadline.Token)
                .ConfigureAwait(false);
            if (ready)
            {
                return new HostVpnListenerResult(true, false, string.Empty);
            }

            var result = new HostVpnListenerResult(
                false,
                process.HasExited,
                process.Diagnostic);
            await process.DisposeAsync().ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await process.DisposeAsync().ConfigureAwait(false);
            return new HostVpnListenerResult(false, false, string.Empty);
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsLoopbackAddressInUse(string diagnostic) =>
        diagnostic.Contains("address already in use", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<bool> WaitForPathAsync(
        string path,
        IHostVpnProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (!process.HasExited)
            {
                if (File.Exists(path))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private NetworkConnectionResult<INetworkConnectionSession> ConnectionFailed(
        string displayName,
        string diagnostic) => VpnAuthenticationRejection.IsReported(diagnostic)
            ? AuthenticationFailed(displayName)
            : Fail(
                NetworkConnectionErrorCode.ConnectionFailed,
                $"{_kind.ToString().ToLowerInvariant()}_host_userspace_connection_failed",
                $"{displayName} did not expose a ready app-scoped SOCKS5 route.",
                retryable: true);

    private NetworkConnectionResult<INetworkConnectionSession> AuthenticationFailed(
        string displayName) => Fail(
        _kind == NetworkConnectionKind.Tailscale
            ? NetworkConnectionErrorCode.AuthenticationRequired
            : NetworkConnectionErrorCode.AuthenticationRejected,
        $"{_kind.ToString().ToLowerInvariant()}_host_authentication_failed",
        $"{displayName} rejected the supplied authentication.",
        retryable: false);

    private NetworkConnectionResult<INetworkConnectionSession> InvalidConfiguration(
        string displayName) => Fail(
        NetworkConnectionErrorCode.InvalidConfiguration,
        $"{_kind.ToString().ToLowerInvariant()}_host_configuration_invalid",
        $"The selected connection does not contain a {displayName} configuration.",
        retryable: false);

    private NetworkConnectionResult<INetworkConnectionSession> CommandTimedOut(
        string displayName) => Fail(
        NetworkConnectionErrorCode.ConnectionFailed,
        $"{_kind.ToString().ToLowerInvariant()}_host_command_timed_out",
        $"{displayName} did not finish the current connection step in time.",
        retryable: true);

    private static NetworkConnectionResult<INetworkConnectionSession> RuntimeMissing(
        string stableCode,
        string message) => Fail(
        NetworkConnectionErrorCode.RuntimeMissing,
        stableCode,
        message,
        retryable: false);

    private NetworkConnectionResult<INetworkConnectionSession> Succeed(
        NetworkConnectionId connectionId,
        string displayName,
        int port,
        IReadOnlyList<IHostVpnProcess> processes,
        string temporaryDirectory,
        IReadOnlyList<IPAddress>? dnsServers = null,
        HostVpnProcessRequest? cleanup = null,
        bool supportsUdpAssociate = false) =>
        NetworkConnectionResult<INetworkConnectionSession>.Succeed(
            new HostUserspaceVpnSession(
                connectionId,
                displayName,
                port,
                processes,
                temporaryDirectory,
                dnsServers ?? [],
                cleanup,
                _processRunner,
                supportsUdpAssociate));

    private const string OpenConnectDnsCaptureScript = """
        #!/bin/sh
        dns_file=$1
        proxy=$2
        port=$3
        temporary_file="${dns_file}.$$"
        umask 077
        printf '%s\n%s\n' "${INTERNAL_IP4_DNS:-}" "${INTERNAL_IP6_DNS:-}" > "$temporary_file" || exit 1
        mv -f "$temporary_file" "$dns_file" || exit 1
        exec "$proxy" openconnect-socks --port "$port"
        """;

    private static async ValueTask<IReadOnlyList<IPAddress>> ReadCapturedDnsServersAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            return ParseDnsServers(content);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<IPAddress> ParseWireGuardDnsServers(
        ReadOnlySpan<byte> configuration)
    {
        string text;
        try
        {
            text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(configuration);
        }
        catch (DecoderFallbackException)
        {
            return [];
        }

        var inInterface = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                inInterface = string.Equals(
                    trimmed,
                    "[Interface]",
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inInterface)
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator < 0
                || !string.Equals(
                    trimmed[..separator].Trim(),
                    "DNS",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return ParseDnsServers(trimmed[(separator + 1)..]);
        }

        return [];
    }

    private static IReadOnlyList<IPAddress> ParseDnsServers(string value)
    {
        const int maximumDnsServerCount = 4;
        var addresses = new List<IPAddress>(maximumDnsServerCount);
        foreach (var field in value.Split(
                     [',', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(field, out var address) && !addresses.Contains(address))
            {
                addresses.Add(address);
                if (addresses.Count == maximumDnsServerCount)
                {
                    break;
                }
            }
        }

        return addresses;
    }

    private static NetworkConnectionResult<INetworkConnectionSession> Fail(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) =>
        NetworkConnectionResult<INetworkConnectionSession>.Fail(
            new NetworkConnectionError(code, stableCode, message, retryable));

    private static bool IsAuthenticationFailure(string diagnostic) =>
        diagnostic.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("login failed", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("needslogin", StringComparison.OrdinalIgnoreCase)
        || diagnostic.Contains("invalid auth", StringComparison.OrdinalIgnoreCase);

    private static bool HasRunningTailscaleBackend(string diagnostic)
    {
        try
        {
            using var document = JsonDocument.Parse(diagnostic);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("BackendState", out var state)
                && state.ValueKind == JsonValueKind.String
                && string.Equals(state.GetString(), "Running", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string TokenFor(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private string PersistentTailscaleStatePath(
        WorkspaceInstanceId workspaceId,
        NetworkConnectionId connectionId)
    {
        var directory = Directory.CreateDirectory(Path.Combine(
            _persistentStateRoot,
            TokenFor(workspaceId.Value)));
        if (!OperatingSystem.IsWindows())
        {
            directory.UnixFileMode = UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute;
        }

        return Path.Combine(directory.FullName, $"{TokenFor(connectionId.Value)}.state");
    }

    private static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void Clear(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private sealed record HostVpnListenerResult(
        bool Ready,
        bool ProcessExited,
        string Diagnostic);

    private sealed class HostUserspaceVpnSession : INetworkConnectionSession
    {
        private readonly IReadOnlyList<IHostVpnProcess> _processes;
        private readonly string _temporaryDirectory;
        private readonly HostVpnProcessRequest? _cleanup;
        private readonly IHostVpnProcessRunner _processRunner;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _gate = new();
        private readonly Task _processMonitor;
        private NetworkConnectionSnapshot _snapshot;
        private int _disposed;

        public HostUserspaceVpnSession(
            NetworkConnectionId connectionId,
            string displayName,
            int port,
            IReadOnlyList<IHostVpnProcess> processes,
            string temporaryDirectory,
            IReadOnlyList<IPAddress> dnsServers,
            HostVpnProcessRequest? cleanup,
            IHostVpnProcessRunner processRunner,
            bool supportsUdpAssociate)
        {
            _processes = processes;
            _temporaryDirectory = temporaryDirectory;
            _cleanup = cleanup;
            _processRunner = processRunner;
            SupportsUdpAssociate = supportsUdpAssociate;
            _snapshot = new NetworkConnectionSnapshot(
                connectionId,
                NetworkConnectionState.Connected,
                $"{displayName} established its app-scoped VPN session.");
            Egress = WorkspaceNetworkEgress.ViaProxy(
                new Uri($"socks5://127.0.0.1:{port}", UriKind.Absolute));
            DnsServers = [.. dnsServers.Select(
                address => new IPAddress(address.GetAddressBytes()))];
            _processMonitor = MonitorProcessesAsync();
        }

        public NetworkConnectionSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return _snapshot;
                }
            }
        }

        public WorkspaceNetworkEgress Egress { get; }

        public IReadOnlyList<IPAddress> DnsServers { get; }

        public bool SupportsUdpAssociate { get; }

        public event EventHandler<NetworkConnectionSnapshot>? Changed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _processMonitor.ConfigureAwait(false);

            if (_cleanup is not null)
            {
                using var deadline = new CancellationTokenSource(CommandTimeout);
                try
                {
                    _ = await _processRunner.RunAsync(_cleanup, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException)
                {
                }
            }

            for (var index = _processes.Count - 1; index >= 0; index--)
            {
                await _processes[index].DisposeAsync().ConfigureAwait(false);
            }

            SecureHostVpnDirectory.DeleteOwned(_temporaryDirectory);
            Publish(new NetworkConnectionSnapshot(
                Snapshot.ConnectionId,
                NetworkConnectionState.Disconnected));
            _lifetime.Dispose();
        }

        private async Task MonitorProcessesAsync()
        {
            try
            {
                var monitors = _processes
                    .Select(process => process.WaitForExitAsync(_lifetime.Token))
                    .ToArray();
                await Task.WhenAny(monitors).ConfigureAwait(false);
                if (_lifetime.IsCancellationRequested)
                {
                    return;
                }

                Publish(new NetworkConnectionSnapshot(
                    Snapshot.ConnectionId,
                    NetworkConnectionState.Failed,
                    "The app-scoped VPN process stopped unexpectedly."));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                Publish(new NetworkConnectionSnapshot(
                    Snapshot.ConnectionId,
                    NetworkConnectionState.Failed,
                    "The app-scoped VPN process could no longer be monitored."));
            }
        }

        private void Publish(NetworkConnectionSnapshot snapshot)
        {
            lock (_gate)
            {
                _snapshot = snapshot;
            }

            Changed?.Invoke(this, snapshot);
        }
    }

    private sealed class SecureHostVpnDirectory : IAsyncDisposable
    {
        private const string Prefix = "ghostshell-vpn-";
        private bool _owned = true;

        private SecureHostVpnDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static SecureHostVpnDirectory Create()
        {
            var directory = Directory.CreateTempSubdirectory(Prefix);
            if (!OperatingSystem.IsWindows())
            {
                directory.UnixFileMode = UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute;
            }

            return new SecureHostVpnDirectory(directory.FullName);
        }

        public string PathFor(string fileName) => System.IO.Path.Combine(Path, fileName);

        public async ValueTask<string> WriteAsync(
            string fileName,
            IReadOnlyList<ReadOnlyMemory<byte>> content,
            CancellationToken cancellationToken)
        {
            var path = PathFor(fileName);
            await using (var stream = new FileStream(
                             path,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                foreach (var part in content)
                {
                    await stream.WriteAsync(part, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return path;
        }

        public string TransferOwnership()
        {
            _owned = false;
            return Path;
        }

        public ValueTask DisposeAsync()
        {
            if (_owned)
            {
                DeleteOwned(Path);
            }

            return ValueTask.CompletedTask;
        }

        public static void DeleteOwned(string path)
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            var temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!fullPath.StartsWith(temporaryRoot, StringComparison.Ordinal)
                || !System.IO.Path.GetFileName(fullPath).StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a directory not created by the host VPN transport.");
            }

            try
            {
                Directory.Delete(fullPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
