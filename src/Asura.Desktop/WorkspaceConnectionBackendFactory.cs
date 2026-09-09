using Asura.Application;
using Asura.ConnectionBackend;
using Asura.Core;
using Asura.Files;
using Asura.Infrastructure;

namespace Asura.Desktop;

/// <summary>
/// Selects execution location, not driver sockets. Each SSH identity and captured
/// workspace route gets an owned service VM; its DNS map cannot outlive that VM.
/// Host-local files/terminals are deliberately outside this connection service.
/// </summary>
internal sealed class WorkspaceConnectionBackendFactory(
    Func<IWorkspaceConnectionServiceProvider?> serviceProvider,
    IWorkspacePacketGatewayRuntime gateways,
    ISecretVault vault,
    ISshHostKeyTrustStore knownHosts,
    Func<DatabaseValueContentStore> contentStores,
    IDatabaseOperationExecutor hostDatabaseOperations,
    SelfReentryLaunch? hostDirectLaunch = null)
{
    private readonly Func<IWorkspaceConnectionServiceProvider?> _serviceProvider = serviceProvider;
    private readonly IWorkspacePacketGatewayRuntime _gateways = gateways;
    private readonly ISecretVault _vault = vault;
    private readonly ISshHostKeyTrustStore _knownHosts = knownHosts;
    private readonly Func<DatabaseValueContentStore> _contentStores = contentStores;
    private readonly IDatabaseOperationExecutor _hostDatabaseOperations = hostDatabaseOperations;
    private readonly SelfReentryLaunch? _hostDirectLaunch = hostDirectLaunch;

    public Session Create(WorkspaceInstanceId workspaceId, IWorkspaceNetworkConnector connector,
        IConnectionRuntime connectionRuntime, IConnectionCommandRuntime? workspaceCommands) =>
        new(this, workspaceId, connector, connectionRuntime, workspaceCommands);

    // Every release must run even when an earlier cancellation callback or native
    // stop fails. Preserve the failures so the owner can retry cleanup, not the SQL.
    internal static async Task StopOwnedResourcesAsync(params Func<ValueTask>[] releases)
    {
        List<Exception> failures = [];
        foreach (var release in releases)
        {
            try { await release().ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count != 0)
        {
            throw new AggregateException("Connection backend cleanup did not complete.", failures);
        }
    }

    internal sealed class Session : IAsyncDisposable
    {
        private readonly WorkspaceConnectionBackendFactory _owner;
        private readonly WorkspaceInstanceId _workspaceId;
        private readonly IWorkspaceNetworkConnector _connector;
        private readonly SshNetBrowserTunnelFactory _ssh;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<Service> _services = [];
        private readonly List<HostConnectionBackend> _direct = [];
        private readonly List<IAsyncDisposable> _failedStarts = [];
        private readonly WorkspaceDatabaseBackend? _workspace;
        private readonly DatabaseOperationWorker? _workspaceDatabases;
        private Task? _dispose;
        private readonly object _disposeGate = new();

        internal Session(WorkspaceConnectionBackendFactory owner, WorkspaceInstanceId workspaceId,
            IWorkspaceNetworkConnector connector, IConnectionRuntime connectionRuntime,
            IConnectionCommandRuntime? workspaceCommands)
        {
            _owner = owner;
            _workspaceId = workspaceId;
            _connector = connector;
            _ssh = new(owner._vault, owner._knownHosts, connectionRuntime);
            if (workspaceCommands is not null)
            {
                _workspace = new(workspaceCommands);
                _workspaceDatabases = new(owner._contentStores, workspaceLaunch: token => PlanAsync("database", null, token));
            }
        }

        public async ValueTask<IDatabaseOperationExecutor> SelectDatabaseAsync(DatabaseDriverDescriptor driver,
            ConnectionProfile? hop, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(driver);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (driver.IsFileBased && hop?.Endpoint is ConnectionEndpoint.Ssh)
            {
                throw new NotSupportedException("An SSH database hop provides network access, not the remote SSH filesystem. Open file databases from the host or workspace filesystem without an SSH hop.");
            }
            if (hop?.Endpoint is not ConnectionEndpoint.Ssh)
            {
                if (_workspaceDatabases is not null) { return _workspaceDatabases; }
                // SQLite cannot open network sockets or extensions. Every other
                // host driver borrows an immutable Direct generation instead.
                if (driver.IsFileBased && string.Equals(driver.Id, "sqlite", StringComparison.Ordinal))
                {
                    return _owner._hostDatabaseOperations;
                }
                var route = _connector.CaptureRoute();
                if (driver.IsFileBased && route.Egress != WorkspaceNetworkEgress.Direct)
                {
                    throw new NotSupportedException("A network-enabled file database on the Mac cannot inherit an isolated route. Open it in an isolated workspace to use custom networking; no host-network fallback was attempted.");
                }
                if (route.Egress == WorkspaceNetworkEgress.Direct)
                {
                    return (await GetDirectAsync(route, linked.Token).ConfigureAwait(false)).Databases;
                }
            }
            return (await GetServiceAsync(hop, linked.Token).ConfigureAwait(false)).Databases;
        }

        public async Task<DatabaseWorkspaceOperationLaunch> PlanAsync(string capability,
            ConnectionProfile? hop, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            if (hop?.Endpoint is not ConnectionEndpoint.Ssh && _workspace is not null)
            {
                var route = _connector.CaptureRoute();
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, route.RouteLifetime);
                var launch = await _workspace.PlanAsync(capability, startup.Token).ConfigureAwait(false);
                return WithLifetime(launch, route.RouteLifetime, _lifetime.Token);
            }
            if (hop?.Endpoint is not ConnectionEndpoint.Ssh)
            {
                var route = _connector.CaptureRoute();
                if (route.Egress == WorkspaceNetworkEgress.Direct)
                {
                    var direct = await GetDirectAsync(route, linked.Token).ConfigureAwait(false);
                    return await direct.PlanAsync(capability, linked.Token).ConfigureAwait(false);
                }
            }
            var service = await GetServiceAsync(hop, linked.Token).ConfigureAwait(false);
            return await service.PlanAsync(capability, linked.Token).ConfigureAwait(false);
        }

        private async Task<HostConnectionBackend> GetDirectAsync(IWorkspaceNetworkConnector route, CancellationToken token)
        {
            if (route.Egress != WorkspaceNetworkEgress.Direct)
            {
                throw new InvalidOperationException("Only an explicitly captured Direct route can start a host backend.");
            }
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, route.RouteLifetime, _lifetime.Token);
            await _gate.WaitAsync(startup.Token).ConfigureAwait(false);
            try
            {
                startup.Token.ThrowIfCancellationRequested();
                for (var index = _direct.Count - 1; index >= 0; index--)
                {
                    if (!_direct[index].Lifetime.IsCancellationRequested) { continue; }
                    await _direct[index].DisposeAsync().ConfigureAwait(false);
                    _direct.RemoveAt(index);
                }
                var existing = _direct.FirstOrDefault(backend => backend.Route == route.RouteLifetime);
                if (existing is not null) { return existing; }
                var launch = _owner._hostDirectLaunch
                    ?? throw new PlatformNotSupportedException("The owned Direct connection backend is not configured.");
                var backend = new HostConnectionBackend(launch, _owner._contentStores, route.RouteLifetime, _lifetime.Token);
                _direct.Add(backend);
                return backend;
            }
            finally { _gate.Release(); }
        }

        private static DatabaseWorkspaceOperationLaunch WithLifetime(DatabaseWorkspaceOperationLaunch launch,
            CancellationToken route, CancellationToken owner)
        {
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(route, owner);
            return launch with
            {
                Lifetime = lifetime.Token,
                CleanupAsync = async () =>
            {
                try { await launch.CleanupAsync().ConfigureAwait(false); }
                finally { lifetime.Dispose(); }
            }
            };
        }

        private async Task<Service> GetServiceAsync(ConnectionProfile? hop, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                // Do not create more VMs while a previous failed stop still owns
                // one. Retry its cleanup first, retaining the evidence on failure.
                await RetryFailedStartsAsync().ConfigureAwait(false);
                foreach (var failed in _services.Where(static service => service.RetirementFailed))
                {
                    await failed.DisposeAsync().ConfigureAwait(false);
                }
                _services.RemoveAll(static service => service.IsRetired);
                var route = _connector.CaptureRoute();
                if (route.Egress == WorkspaceNetworkEgress.Blocked) { throw new WorkspaceNetworkBlockedException(); }
                hop = hop?.Endpoint is ConnectionEndpoint.Ssh ? hop : null;
                foreach (var existing in _services)
                {
                    if (!existing.Lifetime.IsCancellationRequested && existing.Route == route.RouteLifetime && existing.Hop == hop)
                    {
                        return existing;
                    }
                }
                var provider = _owner._serviceProvider()
                    ?? throw new PlatformNotSupportedException("This connection needs the bundled workspace service runtime. Install a supported build; no host-network fallback was attempted.");
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, route.RouteLifetime, _lifetime.Token);
                SshNetBrowserTunnelFactory.SshBrowserTunnel? tunnel = null;
                WorkspaceConnectionServiceIsolate? isolate = null;
                CancellationTokenSource? serviceLifetime = null;
                Service? service = null;
                try
                {
                    var endpoint = route.LocalProxyEndpoint;
                    var credentials = route.LocalProxyCredentials;
                    if (hop is not null)
                    {
                        tunnel = await _ssh.OpenAsync(hop, route, startup.Token).ConfigureAwait(false);
                        endpoint = new Uri($"socks5://127.0.0.1:{tunnel.LocalPort}");
                        credentials = tunnel.ProxyCredentials;
                    }
                    serviceLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, route.RouteLifetime,
                        tunnel?.Lifetime ?? CancellationToken.None);
                    isolate = await WorkspaceConnectionServiceIsolate.OpenAsync(provider, _owner._gateways,
                        _workspaceId, new WorkspacePacketGatewayServiceProxy(endpoint,
                            credentials ?? throw new InvalidOperationException("The captured connection route has no private proxy authority.")), serviceLifetime.Token, startup.Token).ConfigureAwait(false);
                    service = new Service(hop, route.RouteLifetime, isolate, tunnel, serviceLifetime, _owner._contentStores);
                    // Retain ownership before the final setup step. If setup and
                    // teardown both fail, a later close can retry this exact lease.
                    _services.Add(service);
                    using var ready = CancellationTokenSource.CreateLinkedTokenSource(startup.Token, isolate.Lifetime);
                    await provider.ConfigureServiceNetworkingAsync(isolate.Binding, ready.Token).ConfigureAwait(false);
                    service.ObserveRoute();
                    return service;
                }
                catch (Exception startupFailure)
                {
                    if (startupFailure is WorkspaceConnectionServiceStartException pending)
                    {
                        _failedStarts.Add(pending.Cleanup);
                    }
                    try
                    {
                        if (service is not null)
                        {
                            await service.DisposeAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            await StopOwnedResourcesAsync(
                                () => isolate?.DisposeAsync() ?? ValueTask.CompletedTask,
                                () => { tunnel?.Dispose(); return ValueTask.CompletedTask; },
                                () => { serviceLifetime?.Dispose(); return ValueTask.CompletedTask; }).ConfigureAwait(false);
                        }
                    }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException("The connection backend failed to start and could not release every resource.",
                            startupFailure, cleanupFailure);
                    }
                    throw;
                }
            }
            finally { _gate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                if (_dispose is null || _dispose.IsFaulted || _dispose.IsCanceled) { _dispose = DisposeCoreAsync(); }
                return new(_dispose);
            }
        }

        private async Task DisposeCoreAsync()
        {
            List<Exception> failures = [];
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                try { _workspaceDatabases?.Dispose(); }
                catch (Exception exception) { failures.Add(exception); }
                if (_workspace is not null)
                {
                    try { await _workspace.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) { failures.Add(exception); }
                }
                foreach (var service in _services)
                {
                    try { await service.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) { failures.Add(exception); }
                }
                _services.RemoveAll(static service => service.IsRetired);
                for (var index = _direct.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        await _direct[index].DisposeAsync().ConfigureAwait(false);
                        _direct.RemoveAt(index);
                    }
                    catch (Exception exception) { failures.Add(exception); }
                }
                try { await RetryFailedStartsAsync().ConfigureAwait(false); }
                catch (Exception exception) { failures.Add(exception); }
                if (failures.Count != 0) { throw new AggregateException("Connection backends could not all be stopped.", failures); }
            }
            finally { _gate.Release(); }
        }

        private async Task RetryFailedStartsAsync()
        {
            List<Exception> failures = [];
            for (var index = _failedStarts.Count - 1; index >= 0; index--)
            {
                try
                {
                    await _failedStarts[index].DisposeAsync().ConfigureAwait(false);
                    _failedStarts.RemoveAt(index);
                }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (failures.Count != 0)
            {
                throw new AggregateException("A previous connection service still needs cleanup; another VM was not started.", failures);
            }
        }

        internal sealed class Service : IAsyncDisposable
        {
            private readonly WorkspaceConnectionServiceIsolate _isolate;
            private readonly SshNetBrowserTunnelFactory.SshBrowserTunnel? _tunnel;
            private readonly CancellationTokenSource _authority;
            private readonly WorkspaceDatabaseBackend _backend;
            private readonly object _gate = new();
            private Task? _dispose;
            private CancellationTokenRegistration _changed;
            private int _retired;

            internal Service(ConnectionProfile? hop, CancellationToken route, WorkspaceConnectionServiceIsolate isolate,
                SshNetBrowserTunnelFactory.SshBrowserTunnel? tunnel, CancellationTokenSource authority,
                Func<DatabaseValueContentStore> stores)
            {
                Hop = hop; Route = route; _isolate = isolate; _tunnel = tunnel; _authority = authority;
                _backend = new(isolate.Commands, architecture: isolate.Binding.Network?.RelayAttachment?.Architecture ?? "arm64");
                Databases = new(stores, workspaceLaunch: token => PlanAsync("database", token), importHostConnectionFiles: true);
            }

            internal ConnectionProfile? Hop { get; }
            internal CancellationToken Route { get; }
            internal CancellationToken Lifetime => _isolate.Lifetime;
            internal DatabaseOperationWorker Databases { get; }
            internal bool IsRetired => Volatile.Read(ref _retired) != 0;
            internal bool RetirementFailed
            {
                get { lock (_gate) { return _dispose?.IsFaulted == true || _dispose?.IsCanceled == true; } }
            }

            internal void ObserveRoute() => _changed = Lifetime.Register(() =>
            {
                // Leave the cancellation callback before disposing its own
                // registration. Cleanup still owns the live private SDK channel.
                _ = Task.Run(async () =>
                {
                    try { await DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) { SecretSafeDiagnosticProjection.WriteTrace("workspace.backend.retire.failed", exception); }
                });
            });

            internal async Task<DatabaseWorkspaceOperationLaunch> PlanAsync(string capability, CancellationToken token)
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime);
                startup.Token.ThrowIfCancellationRequested();
                var launch = await _backend.PlanAsync(capability, startup.Token).ConfigureAwait(false);
                return launch with { Lifetime = Lifetime };
            }

            public ValueTask DisposeAsync()
            {
                lock (_gate)
                {
                    if (_dispose is null || _dispose.IsFaulted || _dispose.IsCanceled) { _dispose = DisposeCoreAsync(); }
                    return new(_dispose);
                }
            }

            private async Task DisposeCoreAsync()
            {
                await StopOwnedResourcesAsync(
                    () => _changed.DisposeAsync(),
                    () => new ValueTask(_authority.CancelAsync()),
                    () => { Databases.Dispose(); return ValueTask.CompletedTask; },
                    () => _backend.DisposeAsync(),
                    () => _isolate.DisposeAsync(),
                    () => { _tunnel?.Dispose(); return ValueTask.CompletedTask; }).ConfigureAwait(false);
                // Keep the canceled authority alive after failure so a subsequent
                // disposal can retry the retained SDK lease rather than lose it.
                _authority.Dispose();
                Volatile.Write(ref _retired, 1);
            }
        }
    }
}
