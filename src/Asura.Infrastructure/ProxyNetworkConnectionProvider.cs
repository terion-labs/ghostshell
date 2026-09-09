using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

public sealed class ProxyNetworkConnectionProvider : INetworkConnectionProvider
{
    private readonly IWorkspaceTcpConnector _connector;
    private readonly ISocksReachabilityProbe _reachabilityProbe;
    private readonly ISecretVault _secretVault;

    public ProxyNetworkConnectionProvider(
        ISecretVault secretVault,
        IWorkspaceIsolationProvider? isolationProvider = null)
        : this(
            secretVault,
            new WorkspaceTcpConnector(isolationProvider),
            new SocksReachabilityProbe())
    {
    }

    internal ProxyNetworkConnectionProvider(
        ISecretVault secretVault,
        IWorkspaceTcpConnector connector,
        ISocksReachabilityProbe reachabilityProbe)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _reachabilityProbe = reachabilityProbe
            ?? throw new ArgumentNullException(nameof(reachabilityProbe));
    }

    public NetworkConnectionKind Kind => NetworkConnectionKind.Proxy;

    public async ValueTask<NetworkConnectionResult<INetworkConnectionSession>> ConnectAsync(
        NetworkConnectionStartRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Connection.Configuration is not NetworkConnectionConfiguration.Proxy proxy)
        {
            return Fail(
                NetworkConnectionErrorCode.InvalidConfiguration,
                "proxy_configuration_invalid",
                "The selected network connection is not a proxy configuration.",
                retryable: false);
        }

        if (request.Placement is WorkspaceNetworkPlacement.IsolatedPlacement)
        {
            return Fail(
                NetworkConnectionErrorCode.RouteUnavailable,
                "proxy_isolated_routing_unavailable",
                "Proxy routing cannot yet cover all traffic from an isolated workspace.",
                retryable: false);
        }

        progress?.Report(new NetworkConnectionProgress("Preparing the proxy adapter…"));
        var password = await ResolvePasswordAsync(
                request.Connection.Id,
                proxy.PasswordSecret,
                request.TransientPassword,
                cancellationToken)
            .ConfigureAwait(false);
        if (password is NetworkConnectionResult<byte[]>.Failure secretFailure)
        {
            return NetworkConnectionResult<INetworkConnectionSession>.Fail(secretFailure.Error);
        }

        byte[]? passwordBytes = ((NetworkConnectionResult<byte[]>.Success)password).Value;
        ProxySocksAdapter? adapter = null;
        try
        {
            try
            {
                adapter = new ProxySocksAdapter(
                    _connector,
                    request.Placement,
                    proxy,
                    passwordBytes.Length == 0 ? null : passwordBytes);
                passwordBytes = null;
            }
            catch (Exception exception) when (exception is
                IOException or System.Net.Sockets.SocketException)
            {
                return Fail(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    "proxy_adapter_start_failed",
                    "The local proxy adapter could not be started.",
                    retryable: true);
            }

            progress?.Report(new NetworkConnectionProgress(
                "Verifying proxy route reachability…"));
            SocksReachabilityResult reachability;
            try
            {
                reachability = await _reachabilityProbe.ProbeAsync(
                        adapter.LocalPort,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Fail(
                    NetworkConnectionErrorCode.Cancelled,
                    "proxy_route_probe_cancelled",
                    "Proxy route testing was cancelled.",
                    retryable: false);
            }

            if (!reachability.IsReachable)
            {
                if (adapter.AuthenticationRejected)
                {
                    return Fail(NetworkConnectionErrorCode.AuthenticationRejected,
                        "proxy_authentication_rejected", "The proxy rejected the supplied password.", retryable: false);
                }

                return Fail(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    "proxy_route_probe_failed",
                    DescribeProbeFailure(reachability.Failure),
                    retryable: true);
            }

            INetworkConnectionSession session = new ProxySession(
                request.Connection.Id,
                adapter);
            adapter = null;
            return NetworkConnectionResult<INetworkConnectionSession>.Succeed(session);
        }
        finally
        {
            if (adapter is not null)
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
            }

            if (passwordBytes is not null)
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
    }

    private static string DescribeProbeFailure(SocksReachabilityFailure failure) =>
        failure switch
        {
            SocksReachabilityFailure.ListenerUnavailable =>
                "The local proxy adapter stopped before Asura could test it.",
            SocksReachabilityFailure.SocksHandshakeRejected =>
                "The proxy rejected Asura's SOCKS5 health check.",
            SocksReachabilityFailure.DestinationRejected =>
                "The proxy rejected connections to the public health-check peers. Check its allowed destinations and access policy.",
            SocksReachabilityFailure.TlsRejected =>
                "Traffic crossed the proxy, but TLS validation failed. Check for TLS interception on the route.",
            SocksReachabilityFailure.InvalidHttpResponse =>
                "Traffic crossed the proxy, but the public health-check peer returned an invalid response.",
            SocksReachabilityFailure.TimedOut =>
                "The proxy timed out while reaching the public health-check peers. Check its address, firewall, and access policy.",
            _ =>
                "The proxy could not carry the public health check. Check its address, credentials, allowed destinations, and firewall.",
        };

    private async ValueTask<NetworkConnectionResult<byte[]>> ResolvePasswordAsync(
        NetworkConnectionId connectionId,
        SecretRef? passwordReference,
        SecretMaterial? transientPassword,
        CancellationToken cancellationToken)
    {
        if (passwordReference is null)
        {
            if (transientPassword is null)
            {
                return NetworkConnectionResult<byte[]>.Succeed([]);
            }

            var transientBytes = GC.AllocateUninitializedArray<byte>(transientPassword.Length);
            transientPassword.CopyTo(transientBytes);
            return NetworkConnectionResult<byte[]>.Succeed(transientBytes);
        }

        SecretVaultResult<SecretMaterial> resolved;
        try
        {
            resolved = await _secretVault.ResolveAsync(
                    new ResolveSecretRequest(
                        passwordReference.Value,
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
            return Fail<byte[]>(
                NetworkConnectionErrorCode.Cancelled,
                "proxy_secret_cancelled",
                "Proxy credential access was cancelled.",
                retryable: false);
        }

        if (resolved is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            var authenticationRequired = failure.Error.Code is
                SecretVaultErrorCode.AuthenticationRequired or SecretVaultErrorCode.UserCancelled;
            return Fail<byte[]>(
                authenticationRequired
                    ? NetworkConnectionErrorCode.AuthenticationRequired
                    : NetworkConnectionErrorCode.InvalidConfiguration,
                "proxy_secret_unavailable",
                authenticationRequired
                    ? "Authentication is required to access the proxy credential."
                    : "The proxy credential is unavailable.",
                failure.Error.Retryable || authenticationRequired);
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)resolved).Value;
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        return NetworkConnectionResult<byte[]>.Succeed(bytes);
    }

    private static NetworkConnectionResult<INetworkConnectionSession> Fail(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) =>
        Fail<INetworkConnectionSession>(code, stableCode, message, retryable);

    private static NetworkConnectionResult<T> Fail<T>(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) =>
        NetworkConnectionResult<T>.Fail(
            new NetworkConnectionError(code, stableCode, message, retryable));

    private sealed class ProxySession(
        NetworkConnectionId connectionId,
        ProxySocksAdapter adapter) : INetworkConnectionSession
    {
        public NetworkConnectionSnapshot Snapshot { get; } = new(
            connectionId,
            NetworkConnectionState.Connected,
            "Proxy enabled");

        public WorkspaceNetworkEgress Egress { get; } =
            WorkspaceNetworkEgress.ViaProxy(
                new Uri($"socks5://127.0.0.1:{adapter.LocalPort}", UriKind.Absolute));

        public event EventHandler<NetworkConnectionSnapshot>? Changed
        {
            add { }
            remove { }
        }

        public ValueTask DisposeAsync() => adapter.DisposeAsync();
    }
}
