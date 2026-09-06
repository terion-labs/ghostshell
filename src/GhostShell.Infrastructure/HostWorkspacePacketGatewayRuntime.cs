using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

internal interface IHostWorkspacePacketGatewayBackend
{
    ValueTask<NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>> OpenAsync(
        WorkspacePacketGatewayOpenRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken);
}

internal interface IHostWorkspacePacketGatewayBackendSession : IAsyncDisposable
{
    WorkspacePacketRouteCapabilities Capabilities { get; }

    NetworkConnectionError? Failure { get; }

    event EventHandler<NetworkConnectionError>? Failed;
}

/// <summary>
/// Owns at most one packet gateway for each persistent isolate. It accepts TCP and UDP routes
/// for the upstream's negotiated address families and blocks permanently after route failure.
/// </summary>
internal sealed class HostWorkspacePacketGatewayRuntime : IWorkspacePacketGatewayRuntime
{
    private readonly object _gate = new();
    private readonly HashSet<GatewayKey> _activeGateways = [];
    private readonly IHostWorkspacePacketGatewayBackend _backend;

    internal HostWorkspacePacketGatewayRuntime(IHostWorkspacePacketGatewayBackend backend) =>
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public async ValueTask<NetworkConnectionResult<IWorkspacePacketGatewaySession>> OpenAsync(
        WorkspacePacketGatewayOpenRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Isolation.Network is null)
        {
            return NetworkConnectionResult<IWorkspacePacketGatewaySession>.Fail(
                Error(
                    NetworkConnectionErrorCode.InvalidConfiguration,
                    "workspace_packet_gateway_host_only_network_missing",
                    "The workspace environment has no host-only network for its packet gateway.",
                    retryable: false));
        }

        var key = GatewayKey.From(request.Isolation);
        lock (_gate)
        {
            if (!_activeGateways.Add(key))
            {
                return NetworkConnectionResult<IWorkspacePacketGatewaySession>.Fail(
                    Error(
                        NetworkConnectionErrorCode.RouteUnavailable,
                        "workspace_packet_gateway_already_active",
                        "This workspace environment already has an active host packet gateway.",
                        retryable: true));
            }
        }

        try
        {
            progress?.Report(new NetworkConnectionProgress("Starting host packet gateway…"));
            var opened = await _backend.OpenAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
            if (opened is NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Failure
                failure)
            {
                Release(key);
                return NetworkConnectionResult<IWorkspacePacketGatewaySession>.Fail(failure.Error);
            }

            var backendSession =
                ((NetworkConnectionResult<IHostWorkspacePacketGatewayBackendSession>.Success)opened)
                .Value;
            if (!backendSession.Capabilities.IsUsableWorkspaceRoute)
            {
                await backendSession.DisposeAsync().ConfigureAwait(false);
                Release(key);
                return NetworkConnectionResult<IWorkspacePacketGatewaySession>.Fail(
                    Error(
                        NetworkConnectionErrorCode.RouteUnavailable,
                        "workspace_packet_gateway_route_incomplete",
                        "The host route must carry TCP traffic for every negotiated workspace address family.",
                        retryable: false));
            }

            var session = new GatewaySession(
                backendSession,
                () => Release(key));
            if (session.Snapshot is
                {
                    State: WorkspacePacketGatewayState.Blocked,
                    Error: { } startFailure,
                })
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return NetworkConnectionResult<IWorkspacePacketGatewaySession>.Fail(startFailure);
            }

            return NetworkConnectionResult<IWorkspacePacketGatewaySession>.Succeed(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Release(key);
            return NetworkConnectionResult<IWorkspacePacketGatewaySession>.Fail(
                Error(
                    NetworkConnectionErrorCode.Cancelled,
                    "workspace_packet_gateway_cancelled",
                    "Starting the host packet gateway was cancelled.",
                    retryable: false));
        }
        catch (Exception exception)
        {
            Release(key);
            SecretSafeDiagnosticProjection.WriteTrace(
                "workspace.packet-gateway.open.failed",
                exception);
            return NetworkConnectionResult<IWorkspacePacketGatewaySession>.Fail(
                Error(
                    NetworkConnectionErrorCode.ConnectionFailed,
                    "workspace_packet_gateway_start_failed",
                    "The host packet gateway could not be started. No packet route was opened.",
                    retryable: true));
        }
    }

    private void Release(GatewayKey key)
    {
        lock (_gate)
        {
            _activeGateways.Remove(key);
        }
    }

    private static NetworkConnectionError Error(
        NetworkConnectionErrorCode code,
        string stableCode,
        string message,
        bool retryable) =>
        new(code, stableCode, message, retryable);

    private readonly record struct GatewayKey(
        WorkspaceIsolationProviderId Provider,
        string ResourceName)
    {
        public static GatewayKey From(WorkspaceIsolationBinding binding) =>
            new(binding.Provider, binding.ResourceName);
    }

    private sealed class GatewaySession : IWorkspacePacketGatewaySession
    {
        private readonly object _gate = new();
        private readonly IHostWorkspacePacketGatewayBackendSession _backend;
        private readonly Action _release;
        private WorkspacePacketGatewaySnapshot _snapshot;
        private bool _disposed;

        public GatewaySession(
            IHostWorkspacePacketGatewayBackendSession backend,
            Action release)
        {
            _backend = backend;
            _release = release;
            _snapshot = new WorkspacePacketGatewaySnapshot(
                WorkspacePacketGatewayState.Ready,
                backend.Capabilities);
            _backend.Failed += OnBackendFailed;
            if (_backend.Failure is { } failure)
            {
                TransitionToBlocked(failure);
            }
        }

        public WorkspacePacketGatewaySnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return _snapshot;
                }
            }
        }

        public event EventHandler<WorkspacePacketGatewaySnapshot>? Changed;

        public async ValueTask DisposeAsync()
        {
            WorkspacePacketGatewaySnapshot stopped;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                stopped = new WorkspacePacketGatewaySnapshot(WorkspacePacketGatewayState.Stopped);
                _snapshot = stopped;
            }

            _backend.Failed -= OnBackendFailed;
            try
            {
                NotifyChanged(stopped);
                await _backend.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Changed = null;
                _release();
            }
        }

        private void OnBackendFailed(object? sender, NetworkConnectionError error) =>
            TransitionToBlocked(error);

        private void TransitionToBlocked(NetworkConnectionError error)
        {
            WorkspacePacketGatewaySnapshot blocked;
            lock (_gate)
            {
                if (_disposed || _snapshot.State != WorkspacePacketGatewayState.Ready)
                {
                    return;
                }

                blocked = new WorkspacePacketGatewaySnapshot(
                    WorkspacePacketGatewayState.Blocked,
                    error: error);
                _snapshot = blocked;
            }
            NotifyChanged(blocked);
        }

        private void NotifyChanged(WorkspacePacketGatewaySnapshot snapshot)
        {
            try
            {
                Changed?.Invoke(this, snapshot);
            }
            catch (Exception exception)
            {
                SecretSafeDiagnosticProjection.WriteTrace(
                    "workspace.packet-gateway.observer.failed",
                    exception);
            }
        }

    }
}
