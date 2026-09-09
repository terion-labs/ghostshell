using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Owns one provider session per running workspace. Windows attached to the same persistent
/// isolate receive leases on one process-wide session, because that isolate has only one routing
/// table. Policy changes are serialized so a replacement route cannot overlap the route it
/// supersedes.
/// </summary>
public sealed partial class WorkspaceNetworkRuntime : IWorkspaceNetworkRuntime
{
    private readonly object _sharedSessionsGate = new();
    private readonly Dictionary<IsolatedWorkspaceKey, SharedSessionEntry> _sharedSessions = [];
    private readonly IReadOnlyDictionary<NetworkConnectionKind, INetworkConnectionProvider>
        _providers;
    private readonly IWorkspaceIsolationEgressGuard? _isolationEgressGuard;
    private readonly INetworkPasswordPrompt? _passwordPrompt;
    private readonly ISecretVault? _secretVault;
    private readonly IWorkspacePacketGatewayRuntime? _packetGatewayRuntime;
    private readonly Func<TimeSpan, CancellationToken, Task> _reconnectDelay;

    public WorkspaceNetworkRuntime(
        IEnumerable<INetworkConnectionProvider> providers,
        IWorkspaceIsolationEgressGuard? isolationEgressGuard = null,
        INetworkPasswordPrompt? passwordPrompt = null,
        IWorkspacePacketGatewayRuntime? packetGatewayRuntime = null,
        ISecretVault? secretVault = null)
        : this(
            providers,
            isolationEgressGuard,
            passwordPrompt,
            packetGatewayRuntime,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            secretVault)
    {
    }

    internal WorkspaceNetworkRuntime(
        IEnumerable<INetworkConnectionProvider> providers,
        IWorkspaceIsolationEgressGuard? isolationEgressGuard,
        INetworkPasswordPrompt? passwordPrompt,
        IWorkspacePacketGatewayRuntime? packetGatewayRuntime,
        Func<TimeSpan, CancellationToken, Task> reconnectDelay,
        ISecretVault? secretVault = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var byKind = new Dictionary<NetworkConnectionKind, INetworkConnectionProvider>();
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!byKind.TryAdd(provider.Kind, provider))
            {
                throw new ArgumentException(
                    $"More than one network provider was registered for {provider.Kind}.",
                    nameof(providers));
            }
        }

        _providers = byKind;
        _isolationEgressGuard = isolationEgressGuard;
        _passwordPrompt = passwordPrompt;
        _secretVault = secretVault;
        _packetGatewayRuntime = packetGatewayRuntime;
        _reconnectDelay = reconnectDelay
            ?? throw new ArgumentNullException(nameof(reconnectDelay));
    }

    public async ValueTask<IWorkspaceNetworkSession> OpenAsync(
        WorkspaceNetworkOpenRequest request,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Placement is WorkspaceNetworkPlacement.IsolatedPlacement isolated)
        {
            return await OpenSharedIsolatedAsync(
                    request,
                    isolated,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var session = new WorkspaceSession(
            request.WorkspaceId,
            request.Placement,
            _providers,
            _isolationEgressGuard,
            _passwordPrompt,
            reconnectDelay: _reconnectDelay,
            secretVault: _secretVault);
        _ = await session.ApplyAsync(
                request.InitialPolicy,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return session;
    }

    private async ValueTask<IWorkspaceNetworkSession> OpenSharedIsolatedAsync(
        WorkspaceNetworkOpenRequest request,
        WorkspaceNetworkPlacement.IsolatedPlacement isolated,
        IProgress<NetworkConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var key = new IsolatedWorkspaceKey(
            isolated.Binding.Provider,
            isolated.Binding.ResourceName);
        while (true)
        {
            SharedSessionEntry? entry = null;
            Task? closing = null;
            var initialize = false;
            lock (_sharedSessionsGate)
            {
                if (_sharedSessions.TryGetValue(key, out var existing))
                {
                    if (existing.IsClosing)
                    {
                        closing = existing.Closed;
                    }
                    else
                    {
                        entry = existing;
                    }
                }
                else
                {
                    entry = new SharedSessionEntry(
                        key,
                        new WorkspaceSession(
                            request.WorkspaceId,
                            request.Placement,
                            _providers,
                            _isolationEgressGuard,
                            _passwordPrompt,
                            _packetGatewayRuntime,
                            _reconnectDelay,
                            _secretVault));
                    _sharedSessions.Add(key, entry);
                    initialize = true;
                }

                entry?.AddLease();
            }

            if (closing is not null)
            {
                await closing.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (initialize)
                {
                    try
                    {
                        _ = await entry!.Session.ApplyAsync(
                                request.InitialPolicy,
                                progress,
                                cancellationToken)
                            .ConfigureAwait(false);
                        entry.CompleteInitialization();
                    }
                    catch (Exception exception)
                    {
                        entry!.FailInitialization(exception);
                        throw;
                    }
                }
                else
                {
                    await entry!.Initialized.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return new SharedSessionLease(entry.Session, () => ReleaseSharedAsync(entry));
            }
            catch
            {
                await ReleaseSharedAsync(entry!).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async ValueTask ReleaseSharedAsync(SharedSessionEntry entry)
    {
        var dispose = false;
        lock (_sharedSessionsGate)
        {
            if (entry.ReleaseLease() == 0)
            {
                entry.BeginClosing();
                dispose = true;
            }
        }

        if (dispose)
        {
            try
            {
                await entry.Session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_sharedSessionsGate)
                {
                    _sharedSessions.Remove(entry.Key);
                }

                entry.CompleteClosing();
            }
        }
    }

    private readonly record struct IsolatedWorkspaceKey(
        WorkspaceIsolationProviderId Provider,
        string ResourceName);

    private sealed class SharedSessionEntry(
        IsolatedWorkspaceKey key,
        WorkspaceSession session)
    {
        private readonly TaskCompletionSource _initialized = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _leases;

        public IsolatedWorkspaceKey Key { get; } = key;

        public WorkspaceSession Session { get; } = session;

        public Task Initialized => _initialized.Task;

        public bool IsClosing { get; private set; }

        public Task Closed => _closed.Task;

        public void AddLease() => _leases++;

        public int ReleaseLease() => --_leases;

        public void BeginClosing() => IsClosing = true;

        public void CompleteClosing() => _closed.TrySetResult();

        public void CompleteInitialization() => _initialized.TrySetResult();

        public void FailInitialization(Exception exception) =>
            _initialized.TrySetException(exception);
    }

    private sealed class SharedSessionLease : IWorkspaceNetworkSession
    {
        private readonly WorkspaceSession _session;
        private readonly Func<ValueTask> _release;
        private int _disposed;

        public SharedSessionLease(WorkspaceSession session, Func<ValueTask> release)
        {
            _session = session;
            _release = release;
            _session.Changed += OnChanged;
        }

        public WorkspaceNetworkSnapshot Snapshot => _session.Snapshot;

        public event EventHandler<WorkspaceNetworkSnapshot>? Changed;

        public ValueTask<NetworkConnectionResult<WorkspaceNetworkSnapshot>> ApplyAsync(
            WorkspaceNetworkPolicyUpdate update,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _session.ApplyAsync(update, progress, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _session.Changed -= OnChanged;
            Changed = null;
            await _release().ConfigureAwait(false);
        }

        private void OnChanged(object? sender, WorkspaceNetworkSnapshot snapshot) =>
            Changed?.Invoke(this, snapshot);
    }

    private sealed partial class WorkspaceSession : IWorkspaceNetworkSession
    {
        private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(30);
        private readonly SemaphoreSlim _changeGate = new(1, 1);
        private readonly object _stateGate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly WorkspaceInstanceId _workspaceId;
        private readonly WorkspaceNetworkPlacement _placement;
        private readonly IReadOnlyDictionary<NetworkConnectionKind, INetworkConnectionProvider>
            _providers;
        private readonly IWorkspaceIsolationEgressGuard? _isolationEgressGuard;
        private readonly INetworkPasswordPrompt? _passwordPrompt;
        private readonly ISecretVault? _secretVault;
        private readonly IWorkspacePacketGatewayRuntime? _packetGatewayRuntime;
        private readonly Func<TimeSpan, CancellationToken, Task> _reconnectDelay;
        private INetworkConnectionSession? _connection;
        private string? _connectionAuthenticationRouteIdentity;
        private IWorkspacePacketGatewaySession? _packetGateway;
        private WorkspaceNetworkPolicyUpdate? _appliedUpdate;
        private CancellationTokenSource? _automaticReconnect;
        private Task _automaticReconnectTask = Task.CompletedTask;
        private ConnectionCleanupFailure? _unresolvedCleanupFailure;
        private NetworkPolicy _policy = NetworkPolicy.Direct;
        private WorkspaceNetworkSnapshot _snapshot = WorkspaceNetworkSnapshot.Direct;
        private bool _guardArmed;
        private bool _guardCleanupRequired;
        private bool _disposed;

        public WorkspaceSession(
            WorkspaceInstanceId workspaceId,
            WorkspaceNetworkPlacement placement,
            IReadOnlyDictionary<NetworkConnectionKind, INetworkConnectionProvider> providers,
            IWorkspaceIsolationEgressGuard? isolationEgressGuard,
            INetworkPasswordPrompt? passwordPrompt,
            IWorkspacePacketGatewayRuntime? packetGatewayRuntime = null,
            Func<TimeSpan, CancellationToken, Task>? reconnectDelay = null,
            ISecretVault? secretVault = null)
        {
            _workspaceId = workspaceId;
            _placement = placement;
            _providers = providers;
            _isolationEgressGuard = isolationEgressGuard;
            _passwordPrompt = passwordPrompt;
            _secretVault = secretVault;
            _packetGatewayRuntime = packetGatewayRuntime;
            _reconnectDelay = reconnectDelay
                ?? (static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        }

        public WorkspaceNetworkSnapshot Snapshot
        {
            get
            {
                lock (_stateGate)
                {
                    return _snapshot;
                }
            }
        }

        public event EventHandler<WorkspaceNetworkSnapshot>? Changed;

        public async ValueTask<NetworkConnectionResult<WorkspaceNetworkSnapshot>> ApplyAsync(
            WorkspaceNetworkPolicyUpdate update,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(update);
            cancellationToken.ThrowIfCancellationRequested();
            await CancelAutomaticReconnectAsync().ConfigureAwait(false);
            await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _policy = update.Policy;
                _appliedUpdate = update;
                return await ApplyWithPasswordRecoveryAsync(update, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _changeGate.Release();
            }
        }

        private async ValueTask<NetworkConnectionResult<WorkspaceNetworkSnapshot>> ApplyAttemptAsync(
            WorkspaceNetworkPolicyUpdate update,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken,
            SecretMaterial? replacementPassword = null)
        {
            if (_placement is WorkspaceNetworkPlacement.IsolatedPlacement isolated)
            {
                return await ApplyIsolatedGatewayAsync(
                        update,
                        isolated.Binding,
                        progress,
                        cancellationToken,
                        replacementPassword)
                    .ConfigureAwait(false);
            }

            if (!update.Policy.IsEnabled)
            {
                var cleanupFailure = await StopCurrentAsync().ConfigureAwait(false);
                if (cleanupFailure is not null)
                {
                    return FailCleanup(cleanupFailure);
                }

                var disarmed = await DisarmGuardAsync(cancellationToken).ConfigureAwait(false);
                if (disarmed is NetworkConnectionResult<Unit>.Failure disarmFailure)
                {
                    return FailGuardRelease(disarmFailure.Error);
                }

                var direct = WorkspaceNetworkSnapshot.Direct;
                Publish(direct);
                return NetworkConnectionResult<WorkspaceNetworkSnapshot>.Succeed(direct);
            }

            var selectedId = update.Policy.SelectedConnectionId!.Value;
            var selected = update.Connections.Single(connection => connection.Id == selectedId);
            var transitionEgress = update.Policy.KillSwitchEnabled
                ? WorkspaceNetworkEgress.Blocked
                : WorkspaceNetworkEgress.Direct;
            Publish(new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Connecting,
                transitionEgress,
                selectedId));
            var replacementCleanupFailure = await StopCurrentAsync().ConfigureAwait(false);
            if (replacementCleanupFailure is not null)
            {
                return FailCleanup(replacementCleanupFailure);
            }

            using var transientPassword = replacementPassword is not null
                ? new TransientPasswordResult(null, replacementPassword.Clone())
                : await RequestPasswordAsync(
                    selected,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (transientPassword.Result is
                NetworkConnectionResult<SecretMaterial>.Failure passwordFailure)
            {
                return Fail(selectedId, passwordFailure.Error);
            }

            var isolatedProxy = IsIsolatedProxy(selected);
            var needsGuard = isolatedProxy
                || (update.Policy.KillSwitchEnabled
                    && IsIsolatedVpn(selected.ConnectionKind));
            if (!needsGuard)
            {
                var disarmed = await DisarmGuardAsync(cancellationToken).ConfigureAwait(false);
                if (disarmed is NetworkConnectionResult<Unit>.Failure disarmFailure)
                {
                    return FailGuardRelease(disarmFailure.Error);
                }
            }
            else
            {
                var armed = await ArmGuardAsync(
                        selected,
                        transientPassword.Material,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (armed is NetworkConnectionResult<Unit>.Failure armFailure)
                {
                    if (isolatedProxy && !update.Policy.KillSwitchEnabled)
                    {
                        var disarmed = await DisarmGuardAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (disarmed is NetworkConnectionResult<Unit>.Failure disarmFailure)
                        {
                            return FailGuardRelease(disarmFailure.Error);
                        }
                    }

                    return Fail(selectedId, armFailure.Error);
                }
            }

            NetworkConnectionResult<INetworkConnectionSession> connected;
            if (isolatedProxy)
            {
                connected = NetworkConnectionResult<INetworkConnectionSession>.Succeed(
                    new GuardAttachedConnectionSession(selectedId));
            }
            else if (!_providers.TryGetValue(selected.ConnectionKind, out var provider))
            {
                return Fail(
                    selectedId,
                    new NetworkConnectionError(
                        NetworkConnectionErrorCode.RuntimeMissing,
                        "network_provider_missing",
                        $"The {selected.ConnectionKind} network provider is not available.",
                        retryable: false));
            }
            else
            {
                try
                {
                    connected = await provider.ConnectAsync(
                            new NetworkConnectionStartRequest(
                                _workspaceId,
                                selected,
                                _placement,
                                update.Policy.KillSwitchEnabled,
                                transientPassword.Material),
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Fail(
                        selectedId,
                        new NetworkConnectionError(
                            NetworkConnectionErrorCode.Cancelled,
                            "network_connection_cancelled",
                            "The network connection was cancelled.",
                            retryable: false));
                }
            }

            if (connected is NetworkConnectionResult<INetworkConnectionSession>.Failure failure)
            {
                return Fail(selectedId, failure.Error);
            }

            var connection =
                ((NetworkConnectionResult<INetworkConnectionSession>.Success)connected).Value;
            if (connection.Snapshot.ConnectionId != selectedId
                || connection.Snapshot.State != NetworkConnectionState.Connected
                || connection.Egress is null
                || connection.Egress == WorkspaceNetworkEgress.Direct
                || connection.Egress == WorkspaceNetworkEgress.Blocked)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                if (connection.Snapshot.State == NetworkConnectionState.Failed)
                {
                    return FailCleanup(CleanupFailureFor(connection));
                }

                return Fail(
                    selectedId,
                    new NetworkConnectionError(
                        NetworkConnectionErrorCode.RouteUnavailable,
                        "network_provider_route_invalid",
                        "The network provider did not return a usable workspace route.",
                        retryable: true));
            }

            lock (_stateGate)
            {
                _connection = connection;
                _connectionAuthenticationRouteIdentity = BrowserHttpAuthentication.NetworkRouteIdentity(selected);
                connection.Changed += OnConnectionChanged;
            }

            var snapshot = new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Connected,
                connection.Egress,
                selectedId,
                authenticationRouteIdentity: BrowserHttpAuthentication.NetworkRouteIdentity(selected));
            Publish(snapshot);
            return NetworkConnectionResult<WorkspaceNetworkSnapshot>.Succeed(snapshot);
        }

        private async ValueTask<NetworkConnectionResult<WorkspaceNetworkSnapshot>>
            ApplyIsolatedGatewayAsync(
                WorkspaceNetworkPolicyUpdate update,
                WorkspaceIsolationBinding binding,
                IProgress<NetworkConnectionProgress>? progress,
                CancellationToken cancellationToken,
                SecretMaterial? replacementPassword = null)
        {
            var selected = update.Policy.IsEnabled
                ? update.Connections.Single(
                    connection => connection.Id == update.Policy.SelectedConnectionId!.Value)
                : null;
            var selectedId = selected?.Id;
            Publish(new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Connecting,
                WorkspaceNetworkEgress.Blocked,
                selectedId));

            var cleanupFailure = await StopCurrentAsync().ConfigureAwait(false);
            if (cleanupFailure is not null)
            {
                return FailCleanup(cleanupFailure);
            }

            if (_packetGatewayRuntime is null)
            {
                return FailIsolatedGateway(
                    selectedId,
                    new NetworkConnectionError(
                        NetworkConnectionErrorCode.RuntimeMissing,
                        "workspace_packet_gateway_unavailable",
                        "This installation cannot start the isolated workspace network gateway.",
                        retryable: false));
            }

            using var transientPassword = replacementPassword is not null
                ? new TransientPasswordResult(null, replacementPassword.Clone())
                : selected is null
                ? new TransientPasswordResult(null, null)
                : await RequestPasswordAsync(selected, progress, cancellationToken)
                    .ConfigureAwait(false);
            if (transientPassword.Result is
                NetworkConnectionResult<SecretMaterial>.Failure passwordFailure)
            {
                return FailIsolatedGateway(selectedId, passwordFailure.Error);
            }

            var opened = await _packetGatewayRuntime.OpenAsync(
                    new WorkspacePacketGatewayOpenRequest(
                        _workspaceId,
                        binding,
                        selected,
                        transientPassword.Material),
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (opened is NetworkConnectionResult<IWorkspacePacketGatewaySession>.Failure failure)
            {
                return FailIsolatedGateway(selectedId, failure.Error);
            }

            var gateway =
                ((NetworkConnectionResult<IWorkspacePacketGatewaySession>.Success)opened).Value;
            WorkspaceNetworkSnapshot snapshot;
            NetworkConnectionError? handoffFailure = null;
            EventHandler<WorkspaceNetworkSnapshot>? changed;
            lock (_stateGate)
            {
                _packetGateway = gateway;
                gateway.Changed += OnPacketGatewayChanged;
                var gatewaySnapshot = gateway.Snapshot;
                if (gatewaySnapshot.State != WorkspacePacketGatewayState.Ready)
                {
                    handoffFailure = gatewaySnapshot.Error ?? new NetworkConnectionError(
                        NetworkConnectionErrorCode.RouteUnavailable,
                        "workspace_packet_gateway_not_ready",
                        "The isolated workspace network gateway did not become ready.",
                        retryable: true);
                }

                snapshot = handoffFailure is not null
                    ? new WorkspaceNetworkSnapshot(
                        WorkspaceNetworkState.Blocked,
                        WorkspaceNetworkEgress.Blocked,
                        selectedId,
                        handoffFailure)
                    : selected is null
                        ? new WorkspaceNetworkSnapshot(
                            WorkspaceNetworkState.Direct,
                            WorkspaceNetworkEgress.Attached,
                            selectedConnectionId: null)
                        : new WorkspaceNetworkSnapshot(
                            WorkspaceNetworkState.Connected,
                            WorkspaceNetworkEgress.Attached,
                            selectedId,
                            routeCapabilities: gatewaySnapshot.Capabilities,
                            authenticationRouteIdentity: BrowserHttpAuthentication.NetworkRouteIdentity(selected));
                _snapshot = snapshot;
                changed = Changed;
            }

            changed?.Invoke(this, snapshot);
            if (handoffFailure is not null)
            {
                _ = await StopCurrentAsync().ConfigureAwait(false);
                return NetworkConnectionResult<WorkspaceNetworkSnapshot>.Fail(handoffFailure);
            }

            return NetworkConnectionResult<WorkspaceNetworkSnapshot>.Succeed(snapshot);
        }

        private NetworkConnectionResult<WorkspaceNetworkSnapshot> FailIsolatedGateway(
            NetworkConnectionId? selectedId,
            NetworkConnectionError error)
        {
            var snapshot = new WorkspaceNetworkSnapshot(
                WorkspaceNetworkState.Blocked,
                WorkspaceNetworkEgress.Blocked,
                selectedId,
                error);
            Publish(snapshot);
            return NetworkConnectionResult<WorkspaceNetworkSnapshot>.Fail(error);
        }

        private async ValueTask<TransientPasswordResult> RequestPasswordAsync(
            NetworkConnectionProfile connection,
            IProgress<NetworkConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (!RequiresPasswordPrompt(connection.Configuration))
            {
                return new(null, null);
            }

            if (_passwordPrompt is null)
            {
                return new(
                    NetworkConnectionResult<SecretMaterial>.Fail(
                        new NetworkConnectionError(
                            NetworkConnectionErrorCode.AuthenticationRequired,
                            "network_password_prompt_unavailable",
                            $"{connection.Name} needs a password for this connection.",
                            retryable: true)),
                    null);
            }

            progress?.Report(new NetworkConnectionProgress("Waiting for password…"));
            var result = await _passwordPrompt.RequestPasswordAsync(
                    new NetworkPasswordPromptRequest(connection.Id, connection.Name),
                    cancellationToken)
                .ConfigureAwait(false);
            return result switch
            {
                NetworkConnectionResult<SecretMaterial>.Success success =>
                    new(result, success.Value),
                _ => new(result, null),
            };
        }

        private static bool RequiresPasswordPrompt(
            NetworkConnectionConfiguration configuration) =>
            configuration switch
            {
                NetworkConnectionConfiguration.Proxy { Username: not null, PasswordSecret: null } => true,
                NetworkConnectionConfiguration.AnyConnect { PasswordSecret: null } => true,
                NetworkConnectionConfiguration.OpenVpn { Username: not null, PasswordSecret: null } => true,
                _ => false,
            };

        private sealed class TransientPasswordResult(
            NetworkConnectionResult<SecretMaterial>? result,
            SecretMaterial? material) : IDisposable
        {
            public NetworkConnectionResult<SecretMaterial>? Result { get; } = result;

            public SecretMaterial? Material { get; } = material;

            public void Dispose() => Material?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            await CancelAutomaticReconnectAsync().ConfigureAwait(false);
            await _changeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                var cleanupFailure = await StopCurrentAsync().ConfigureAwait(false);
                if (cleanupFailure is null)
                {
                    _ = await DisarmGuardAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    _ = FailCleanup(cleanupFailure);
                }
            }
            finally
            {
                _changeGate.Release();
                _changeGate.Dispose();
                _lifetime.Dispose();
            }
        }

        private NetworkConnectionResult<WorkspaceNetworkSnapshot> Fail(
            NetworkConnectionId selectedId,
            NetworkConnectionError error)
        {
            var snapshot = _guardArmed || ShouldPublishBlockedEgress()
                ? new WorkspaceNetworkSnapshot(
                    WorkspaceNetworkState.Blocked,
                    WorkspaceNetworkEgress.Blocked,
                    selectedId,
                    error)
                : new WorkspaceNetworkSnapshot(
                    WorkspaceNetworkState.Failed,
                    CleanupMayRemainActive(error)
                        ? WorkspaceNetworkEgress.Attached
                        : WorkspaceNetworkEgress.Direct,
                    selectedId,
                    error);
            Publish(snapshot);
            return NetworkConnectionResult<WorkspaceNetworkSnapshot>.Fail(error);
        }

        private NetworkConnectionResult<WorkspaceNetworkSnapshot> FailGuardRelease(
            NetworkConnectionError error)
        {
            var snapshot = _guardArmed
                ? new WorkspaceNetworkSnapshot(
                    WorkspaceNetworkState.Blocked,
                    WorkspaceNetworkEgress.Blocked,
                    selectedConnectionId: null,
                    error: error)
                : new WorkspaceNetworkSnapshot(
                    WorkspaceNetworkState.Failed,
                    WorkspaceNetworkEgress.Direct,
                    selectedConnectionId: null,
                    error: error);
            Publish(snapshot);
            return NetworkConnectionResult<WorkspaceNetworkSnapshot>.Fail(error);
        }

        private NetworkConnectionResult<WorkspaceNetworkSnapshot> FailCleanup(
            ConnectionCleanupFailure failure)
        {
            var snapshot = _guardArmed || ShouldPublishBlockedEgress()
                ? new WorkspaceNetworkSnapshot(
                    WorkspaceNetworkState.Blocked,
                    WorkspaceNetworkEgress.Blocked,
                    failure.ConnectionId,
                    failure.Error)
                : new WorkspaceNetworkSnapshot(
                    WorkspaceNetworkState.Failed,
                    failure.Egress,
                    failure.ConnectionId,
                    failure.Error);
            Publish(snapshot);
            return NetworkConnectionResult<WorkspaceNetworkSnapshot>.Fail(failure.Error);
        }

        private async ValueTask<NetworkConnectionResult<Unit>> ArmGuardAsync(
            NetworkConnectionProfile selected,
            SecretMaterial? transientPassword,
            CancellationToken cancellationToken)
        {
            if (_placement is not WorkspaceNetworkPlacement.IsolatedPlacement isolated)
            {
                return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    "workspace_network_kill_switch_requires_isolation",
                    "A VPN kill switch requires workspace isolation; host-wide routing is never changed.",
                    retryable: false));
            }

            if (_isolationEgressGuard is null)
            {
                return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                    NetworkConnectionErrorCode.RuntimeMissing,
                    "workspace_network_kill_switch_unavailable",
                    "This workspace isolation runtime cannot enforce the VPN kill switch.",
                    retryable: false));
            }

            _guardCleanupRequired = true;
            var result = await _isolationEgressGuard.ArmAsync(
                    _workspaceId,
                    isolated.Binding,
                    selected,
                    cancellationToken,
                    transientPassword)
                .ConfigureAwait(false);
            _guardArmed = result.IsEnforced;
            return result.Error is null
                ? NetworkConnectionResult<Unit>.Succeed(Unit.Value)
                : NetworkConnectionResult<Unit>.Fail(result.Error);
        }

        private async ValueTask<NetworkConnectionResult<Unit>> DisarmGuardAsync(
            CancellationToken cancellationToken)
        {
            if (!_guardCleanupRequired)
            {
                return NetworkConnectionResult<Unit>.Succeed(Unit.Value);
            }

            if (_placement is not WorkspaceNetworkPlacement.IsolatedPlacement isolated
                || _isolationEgressGuard is null)
            {
                return NetworkConnectionResult<Unit>.Fail(new NetworkConnectionError(
                    NetworkConnectionErrorCode.RouteUnavailable,
                    "workspace_network_kill_switch_disarm_unavailable",
                    "The workspace VPN kill switch could not restore direct isolate egress.",
                    retryable: true));
            }

            var result = await _isolationEgressGuard.DisarmAsync(
                    _workspaceId,
                    isolated.Binding,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result is NetworkConnectionResult<Unit>.Success)
            {
                _guardArmed = false;
                _guardCleanupRequired = false;
            }

            return result;
        }

        private static bool IsIsolatedVpn(NetworkConnectionKind kind) => kind is
            NetworkConnectionKind.WireGuard
            or NetworkConnectionKind.OpenVpn
            or NetworkConnectionKind.AnyConnect
            or NetworkConnectionKind.Tailscale;

        private bool IsIsolatedProxy(NetworkConnectionProfile connection) =>
            _placement is WorkspaceNetworkPlacement.IsolatedPlacement
            && connection.Configuration is NetworkConnectionConfiguration.Proxy;

        private async ValueTask<ConnectionCleanupFailure?> StopCurrentAsync()
        {
            INetworkConnectionSession? connection;
            IWorkspacePacketGatewaySession? packetGateway;
            lock (_stateGate)
            {
                connection = _connection;
                if (connection is not null)
                {
                    _connection = null;
                    connection.Changed -= OnConnectionChanged;
                }

                packetGateway = _packetGateway;
                if (packetGateway is not null)
                {
                    _packetGateway = null;
                    packetGateway.Changed -= OnPacketGatewayChanged;
                }
            }

            if (packetGateway is not null)
            {
                await packetGateway.DisposeAsync().ConfigureAwait(false);
            }

            if (connection is null)
            {
                return _unresolvedCleanupFailure;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            _unresolvedCleanupFailure = connection.Snapshot.State == NetworkConnectionState.Failed
                ? CleanupFailureFor(connection)
                : null;
            return _unresolvedCleanupFailure;
        }

        private static ConnectionCleanupFailure CleanupFailureFor(
            INetworkConnectionSession connection) => new(
            connection.Snapshot.ConnectionId,
            connection.Egress,
            new NetworkConnectionError(
                NetworkConnectionErrorCode.RouteUnavailable,
                "network_connection_cleanup_failed",
                connection.Snapshot.Status
                    ?? "The previous network connection could not be removed safely.",
                retryable: true));

        private sealed record ConnectionCleanupFailure(
            NetworkConnectionId ConnectionId,
            WorkspaceNetworkEgress Egress,
            NetworkConnectionError Error);

        private void OnConnectionChanged(
            object? sender,
            NetworkConnectionSnapshot providerSnapshot)
        {
            EventHandler<WorkspaceNetworkSnapshot>? changed;
            WorkspaceNetworkSnapshot snapshot;
            WorkspaceNetworkPolicyUpdate? reconnectUpdate = null;
            lock (_stateGate)
            {
                var connection = _connection;
                if (_disposed || connection is null || !ReferenceEquals(sender, connection))
                {
                    return;
                }

                NetworkConnectionError? error = null;
                if (providerSnapshot.State is NetworkConnectionState.Disconnecting
                    or NetworkConnectionState.Disconnected or NetworkConnectionState.Failed)
                {
                    error = LostConnectionError(providerSnapshot);
                    reconnectUpdate = CanAutomaticallyReconnect(_appliedUpdate)
                        ? _appliedUpdate
                        : null;
                    error = reconnectUpdate is not null
                        ? RetryScheduled(error)
                        : ManualReconnectRequired(error);
                }

                snapshot = providerSnapshot.State switch
                {
                    NetworkConnectionState.Connecting => new WorkspaceNetworkSnapshot(
                        WorkspaceNetworkState.Connecting,
                        ShouldPublishBlockedEgress()
                            ? WorkspaceNetworkEgress.Blocked
                            : WorkspaceNetworkEgress.Direct,
                        providerSnapshot.ConnectionId),
                    NetworkConnectionState.Connected => new WorkspaceNetworkSnapshot(
                        WorkspaceNetworkState.Connected,
                        connection.Egress,
                        providerSnapshot.ConnectionId,
                        authenticationRouteIdentity: _connectionAuthenticationRouteIdentity),
                    NetworkConnectionState.Disconnecting
                        or NetworkConnectionState.Disconnected
                        or NetworkConnectionState.Failed =>
                        ShouldPublishBlockedEgress()
                        ? new WorkspaceNetworkSnapshot(
                            WorkspaceNetworkState.Blocked,
                            WorkspaceNetworkEgress.Blocked,
                            providerSnapshot.ConnectionId,
                            error)
                        : new WorkspaceNetworkSnapshot(
                            WorkspaceNetworkState.Failed,
                            connection.Egress,
                            providerSnapshot.ConnectionId,
                            error),
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(providerSnapshot),
                        providerSnapshot.State,
                        null),
                };
                _snapshot = snapshot;
                changed = Changed;
            }

            changed?.Invoke(this, snapshot);
            if (reconnectUpdate is not null)
            {
                StartAutomaticReconnect(reconnectUpdate);
            }
        }

        private void OnPacketGatewayChanged(
            object? sender,
            WorkspacePacketGatewaySnapshot gatewaySnapshot)
        {
            if (gatewaySnapshot.State != WorkspacePacketGatewayState.Blocked
                || gatewaySnapshot.Error is null)
            {
                return;
            }

            EventHandler<WorkspaceNetworkSnapshot>? changed;
            WorkspaceNetworkSnapshot snapshot;
            WorkspaceNetworkPolicyUpdate? reconnectUpdate;
            lock (_stateGate)
            {
                if (_disposed || !ReferenceEquals(sender, _packetGateway))
                {
                    return;
                }

                reconnectUpdate = gatewaySnapshot.Error.Retryable
                    && CanAutomaticallyReconnect(_appliedUpdate)
                    ? _appliedUpdate
                    : null;
                var error = reconnectUpdate is not null
                    ? RetryScheduled(gatewaySnapshot.Error)
                    : ManualReconnectRequired(gatewaySnapshot.Error);
                snapshot = new WorkspaceNetworkSnapshot(
                    WorkspaceNetworkState.Blocked,
                    WorkspaceNetworkEgress.Blocked,
                    _policy.IsEnabled ? _policy.SelectedConnectionId : null,
                    error);
                _snapshot = snapshot;
                changed = Changed;
            }

            changed?.Invoke(this, snapshot);
            if (reconnectUpdate is not null)
            {
                StartAutomaticReconnect(reconnectUpdate);
            }
        }

        private void StartAutomaticReconnect(WorkspaceNetworkPolicyUpdate update)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            lock (_stateGate)
            {
                if (_disposed
                    || _automaticReconnect is not null
                    || !ReferenceEquals(_appliedUpdate, update))
                {
                    cancellation.Dispose();
                    return;
                }

                _automaticReconnect = cancellation;
                _automaticReconnectTask = ReconnectAsync(update, cancellation.Token);
            }
        }

        private async Task ReconnectAsync(
            WorkspaceNetworkPolicyUpdate update,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            var delay = InitialReconnectDelay;
            var reconnected = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await _reconnectDelay(delay, cancellationToken).ConfigureAwait(false);
                    await _changeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (!IsCurrentReconnect(update, cancellationToken))
                        {
                            return;
                        }

                        var result = await ApplyWithPasswordRecoveryAsync(
                                update,
                                progress: null,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (result is NetworkConnectionResult<WorkspaceNetworkSnapshot>.Success)
                        {
                            reconnected = true;
                            return;
                        }

                        var error = ((NetworkConnectionResult<WorkspaceNetworkSnapshot>.Failure)result)
                            .Error;
                        if (!error.Retryable)
                        {
                            return;
                        }

                        Publish(new WorkspaceNetworkSnapshot(
                            WorkspaceNetworkState.Blocked,
                            WorkspaceNetworkEgress.Blocked,
                            update.Policy.IsEnabled
                                ? update.Policy.SelectedConnectionId
                                : null,
                            RetryScheduled(error)));
                    }
                    finally
                    {
                        _changeGate.Release();
                    }

                    delay = TimeSpan.FromSeconds(Math.Min(
                        delay.TotalSeconds * 2,
                        MaximumReconnectDelay.TotalSeconds));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                SecretSafeDiagnosticProjection.WriteTraceAndStandardError(
                    "workspace.network.reconnect.failed",
                    exception);
                if (IsCurrentReconnect(update, cancellationToken))
                {
                    // An exception does not establish whether replacement or cleanup completed.
                    // Stop automatic retries, keep traffic blocked, and leave a visible recovery action.
                    _ = FailIsolatedGateway(
                        update.Policy.IsEnabled ? update.Policy.SelectedConnectionId : null,
                        new NetworkConnectionError(
                            NetworkConnectionErrorCode.ConnectionFailed,
                            "workspace_network_reconnect_failed",
                            "Automatic network reconnection stopped unexpectedly. Select the connection again to retry.",
                            retryable: false));
                }
            }
            finally
            {
                CancellationTokenSource? completed = null;
                var retryReplacement = false;
                lock (_stateGate)
                {
                    if (_automaticReconnect?.Token == cancellationToken)
                    {
                        completed = _automaticReconnect;
                        _automaticReconnect = null;
                        // The replacement can fail while Connected is being published. Its event
                        // cannot start a second loop until this one relinquishes ownership.
                        retryReplacement = reconnected
                            && ReferenceEquals(_appliedUpdate, update)
                            && _snapshot is
                            {
                                State: WorkspaceNetworkState.Blocked or WorkspaceNetworkState.Failed,
                                Error.Retryable: true,
                            };
                    }
                }

                completed?.Dispose();
                if (retryReplacement)
                {
                    StartAutomaticReconnect(update);
                }
            }
        }

        private bool IsCurrentReconnect(
            WorkspaceNetworkPolicyUpdate update,
            CancellationToken cancellationToken)
        {
            lock (_stateGate)
            {
                return !_disposed
                    && _automaticReconnect?.Token == cancellationToken
                    && ReferenceEquals(_appliedUpdate, update);
            }
        }

        private async Task CancelAutomaticReconnectAsync()
        {
            Task reconnectTask;
            lock (_stateGate)
            {
                _automaticReconnect?.Cancel();
                _appliedUpdate = null;
                reconnectTask = _automaticReconnectTask;
            }

            await reconnectTask.ConfigureAwait(false);
        }

        private static bool CanAutomaticallyReconnect(
            WorkspaceNetworkPolicyUpdate? update)
        {
            if (update is null || !update.Policy.IsEnabled)
            {
                return update is not null;
            }

            var selected = update.Connections.Single(
                connection => connection.Id == update.Policy.SelectedConnectionId!.Value);
            return !RequiresPasswordPrompt(selected.Configuration);
        }

        private static NetworkConnectionError RetryScheduled(NetworkConnectionError error) =>
            new(
                error.Code,
                error.StableCode,
                $"{error.Message} Reconnecting automatically.",
                error.Retryable);

        private NetworkConnectionError ManualReconnectRequired(NetworkConnectionError error) =>
            error.Retryable && _policy.IsEnabled
                ? new NetworkConnectionError(
                    error.Code,
                    error.StableCode,
                    $"{error.Message} Select the connection again to reconnect.",
                    error.Retryable)
                : error;

        private void Publish(WorkspaceNetworkSnapshot snapshot)
        {
            EventHandler<WorkspaceNetworkSnapshot>? changed;
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                _snapshot = snapshot;
                changed = Changed;
            }

            changed?.Invoke(this, snapshot);
        }

        private static bool CleanupMayRemainActive(NetworkConnectionError error) =>
            error.StableCode.EndsWith("_cleanup_failed", StringComparison.Ordinal);

        private bool ShouldPublishBlockedEgress() =>
            _policy.KillSwitchEnabled
            && (_placement is WorkspaceNetworkPlacement.HostPlacement || _guardArmed);

        private static NetworkConnectionError LostConnectionError(
            NetworkConnectionSnapshot providerSnapshot) => new(
            NetworkConnectionErrorCode.ConnectionFailed,
            "network_connection_lost",
            providerSnapshot.Status ?? "The network connection was lost.",
            retryable: true);

        private sealed class GuardAttachedConnectionSession(
            NetworkConnectionId connectionId) : INetworkConnectionSession
        {
            private NetworkConnectionSnapshot _snapshot = new(
                connectionId,
                NetworkConnectionState.Connected,
                "The proxy is enforced inside the workspace environment.");
            private int _disposed;

            public NetworkConnectionSnapshot Snapshot => _snapshot;

            public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Attached;

            public event EventHandler<NetworkConnectionSnapshot>? Changed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _snapshot = new NetworkConnectionSnapshot(
                        connectionId,
                        NetworkConnectionState.Disconnected);
                    Changed?.Invoke(this, _snapshot);
                    Changed = null;
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
