using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Starts one app-scoped VPN transport on the host. Isolated workspaces reach it only through
/// <see cref="IWorkspacePacketGatewayRuntime"/>; this provider never starts VPN software in the
/// guest image.
/// </summary>
public sealed class IsolatedVpnConnectionProvider : INetworkConnectionProvider
{
    private readonly IHostUserspaceVpnTransport _hostTransport;

    public IsolatedVpnConnectionProvider(
        NetworkConnectionKind kind,
        ISecretVault secretVault,
        IWorkspaceIsolationProvider? isolationProvider,
        IConnectionExecutableLocator executableLocator,
        string? persistentStateRoot = null)
        : this(
            kind,
            new HostUserspaceVpnTransport(
                kind,
                secretVault,
                executableLocator,
                persistentStateRoot))
    {
        // Kept until desktop registration is renamed with the provider. Isolation must not flow
        // into the host transport or tempt another guest-execution fallback.
        _ = isolationProvider;
    }

    internal IsolatedVpnConnectionProvider(
        NetworkConnectionKind kind,
        IHostUserspaceVpnTransport hostTransport)
    {
        if (kind is not (
                NetworkConnectionKind.WireGuard
                or NetworkConnectionKind.OpenVpn
                or NetworkConnectionKind.AnyConnect
                or NetworkConnectionKind.Tailscale))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        Kind = kind;
        _hostTransport = hostTransport ?? throw new ArgumentNullException(nameof(hostTransport));
    }

    public NetworkConnectionKind Kind { get; }

    public ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
        NetworkConnectionStartRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Connection.ConnectionKind != Kind)
        {
            return ValueTask.FromResult(Fail(
                NetworkConnectionErrorCode.InvalidConfiguration,
                "vpn_configuration_kind_mismatch",
                $"The selected connection does not contain a {Kind} configuration.",
                retryable: false));
        }

        if (request.Placement is WorkspaceNetworkPlacement.IsolatedPlacement)
        {
            return ValueTask.FromResult(Fail(
                NetworkConnectionErrorCode.RouteUnavailable,
                "workspace_packet_gateway_required",
                "Isolated workspace connections must use the host packet gateway. No VPN software is required inside the workspace image.",
                retryable: false));
        }

        if (request.Placement is not WorkspaceNetworkPlacement.HostPlacement)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Placement, null);
        }

        return _hostTransport.ConnectAsync(request, progress, cancellationToken);
    }

    private static NetworkConnectionResult<INetworkConnectionSession> Fail(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) =>
        NetworkConnectionResult<INetworkConnectionSession>.Fail(
            new NetworkConnectionError(code, stableCode, message, retryable));
}
