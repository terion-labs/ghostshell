using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

internal sealed partial class WorkspaceVpnPacketRouteLauncher
{
    private async ValueTask<NetworkConnectionResult<WorkspaceGatewayProcessStart>> StartFileConfiguredAsync(
        WorkspaceVpnPacketRouteRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var openVpn = request.Connection.Configuration as NetworkConnectionConfiguration.OpenVpn;
        var configurationReference = request.Connection.Configuration switch
        {
            NetworkConnectionConfiguration.WireGuard wireGuard => wireGuard.ConfigurationSecret,
            NetworkConnectionConfiguration.OpenVpn configuration => configuration.ConfigurationSecret,
            _ => throw new ArgumentException("A file-configured VPN is required.", nameof(request)),
        };
        var name = openVpn is null ? "WireGuard" : "OpenVPN";
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(_gatewayExecutable)
            || (openVpn is not null && string.IsNullOrWhiteSpace(_openVpnExecutable)))
        {
            return Fail(NetworkConnectionErrorCode.RuntimeMissing, "workspace_packet_vpn_engine_missing",
                $"The bundled {name} engine is missing. Reinstall or update Asura.", retryable: false);
        }

        var socket = request.Isolation.Network?.PacketSocketPath;
        if (string.IsNullOrWhiteSpace(socket) || !Path.IsPathFullyQualified(socket))
        {
            return Fail(NetworkConnectionErrorCode.InvalidConfiguration, "workspace_packet_vpn_socket_missing",
                "The workspace environment does not expose its packet gateway socket.", retryable: false);
        }

        byte[]? configurationBytes = null;
        byte[]? password = null;
        byte[]? input = null;
        await using var directory = SecureRouteDirectory.Create();
        try
        {
            var resolved = await ResolveSecretAsync(request.Connection.Id, configurationReference,
                $"{name} configuration", cancellationToken).ConfigureAwait(false);
            if (resolved is NetworkConnectionResult<byte[]>.Failure failure)
            {
                return NetworkConnectionResult<WorkspaceGatewayProcessStart>.Fail(failure.Error);
            }

            configurationBytes = ((NetworkConnectionResult<byte[]>.Success)resolved).Value;
            var configurationPath = await directory.WriteAsync("vpn.conf", configurationBytes, cancellationToken)
                .ConfigureAwait(false);
            var keyPath = await directory.WriteAsync("packet-channel-key", request.AuthenticationKey, cancellationToken)
                .ConfigureAwait(false);
            var arguments = new List<string>
            {
                openVpn is null ? "wireguard" : "openvpn", "--config", configurationPath,
                "--socket", socket, "--key-file", keyPath,
            };
            if (openVpn is not null)
            {
                arguments.AddRange(["--engine", _openVpnExecutable!]);
                if (openVpn.Username is not null)
                {
                    arguments.AddRange(["--username", openVpn.Username]);
                }

                var credentials = await ResolvePasswordAsync(request.Connection.Id, openVpn.PasswordSecret,
                    request.TransientPassword, "OpenVPN password", cancellationToken).ConfigureAwait(false);
                if (credentials is NetworkConnectionResult<byte[]>.Failure credentialFailure)
                {
                    return NetworkConnectionResult<WorkspaceGatewayProcessStart>.Fail(credentialFailure.Error);
                }

                password = ((NetworkConnectionResult<byte[]>.Success)credentials).Value;
                input = new byte[password.Length + 1];
                password.CopyTo(input, 0);
                input[^1] = (byte)'\n';
            }

            progress?.Report(new NetworkConnectionProgress($"Connecting {name} to the workspace packet route…"));
            WorkspaceGatewayProcessStart started;
            try
            {
                started = await _processes.StartAsync(
                    new WorkspaceGatewayProcessRequest(_gatewayExecutable, arguments, input ?? ReadOnlyMemory<byte>.Empty),
                    ReadinessTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                SecretSafeDiagnosticProjection.WriteTrace("workspace.packet-vpn.start.failed", exception);
                if (openVpn is not null && VpnAuthenticationRejection.IsReported(exception.Message))
                {
                    return Fail(NetworkConnectionErrorCode.AuthenticationRejected, "workspace_openvpn_authentication_rejected",
                        "OpenVPN rejected the supplied authentication.", retryable: false);
                }

                return Fail(NetworkConnectionErrorCode.ConnectionFailed, "workspace_packet_vpn_start_failed",
                    $"{name} could not establish the workspace packet route.", retryable: true);
            }
            finally
            {
                directory.DeleteFile("packet-channel-key");
            }

            return NetworkConnectionResult<WorkspaceGatewayProcessStart>.Succeed(
                started with { Process = directory.TransferOwnership(started.Process) });
        }
        finally
        {
            Clear(configurationBytes);
            Clear(password);
            Clear(input);
        }
    }
}
