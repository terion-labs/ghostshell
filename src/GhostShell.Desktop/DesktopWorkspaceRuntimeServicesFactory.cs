using GhostShell.App;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Databases;
using GhostShell.Docker;
using GhostShell.Files;
using GhostShell.Git;
using GhostShell.Infrastructure;
using GhostShell.Monitoring;
using GhostShell.Redis;

namespace GhostShell.Desktop;

internal sealed class DesktopWorkspaceRuntimeServicesFactory(
    IConnectionExecutableLocator executableLocator,
    TimeProvider timeProvider,
    IBrowserRendererViewFactory browserRendererViewFactory,
    WorkspaceFilePanelSessionFactory filePanelFactory,
    WorkspaceDatabasePanelSessionFactory databasePanelFactory,
    WorkspaceDockerPanelSessionFactory dockerPanelFactory,
    WorkspaceGitPanelSessionFactory gitPanelFactory,
    WorkspaceNetworkRouteRegistry networkRouteRegistry,
    WorkspaceSystemMonitorPanelSessionFactory systemMonitorFactory,
    IGitRepositoryMutationCoordinator gitMutationCoordinator,
    IDefinitionCatalog definitionCatalog,
    ISecretVault secretVault,
    ISshHostKeyTrustStore knownHosts,
    SshKnownHostStore hostKeyStore,
    PreviewContentCache previewContentCache,
    IDatabaseDiagramWorkerFactory diagramWorkers,
    Func<DatabaseValueContentStore> databaseContentStores,
    IDatabaseOperationExecutor databaseOperations) : IWorkspaceRuntimeServicesFactory
{
    public WorkspaceRuntimeServices Create(WorkspaceRuntimeServicesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsolationBinding is not { } binding)
        {
            var gateway = new HostWorkspaceSocksProxy("host-workspace");
            request.NetworkEgressState.SetLocalProxyEndpoint(
                gateway.LocalProxyEndpoint,
                gateway.LocalProxyCredentials,
                gateway.BrowserProxyEndpoint);
            var hostBackends = request.HostServices.Backends;
            var hostExecutor = new ConnectionCommandExecutor(
                request.ConnectionRuntime,
                executableLocator);
            var hostSecurity = new ConnectionSecurityRuntime(
                request.ConnectionRuntime, secretVault, hostKeyStore, timeProvider, gateway);
            var files = new CatalogFileProviderRuntime(
                definitionCatalog,
                secretVault,
                knownHosts,
                hostSecurity,
                request.ConnectionRuntime,
                previewContentCache,
                gateway);
            var tunnels = new WorkspaceNetworkDatabaseTunnelFactory(
                gateway,
                route => new SshNetDatabaseTunnelFactory(secretVault, knownHosts, request.ConnectionRuntime, route));
            var hostDatabases = new DatabasePanelClient(
                tunnels,
                GhostShell.Core.BuiltInConnections.Local,
                diagramWorkers,
                databaseContentStores,
                databaseOperations);
            var hostDocker = new DockerEngineClient(hostExecutor, timeProvider);
            var hostGit = new GitRepositoryClient(hostExecutor, timeProvider, gateway);
            var hostRedis = new RedisPanelSessionFactory(
                tunnels,
                GhostShell.Core.BuiltInConnections.Local);
            var hostMonitors = new SystemMonitorPanelSessionFactory(hostExecutor, timeProvider);
            var hostMonitorRegistration = systemMonitorFactory.Register(
                request.WorkspaceId,
                hostMonitors);
            var routedBrowserFactory = hostBackends.BrowserRendererViewFactory is { } hostBrowser
                ? new WorkspaceBrowserRendererViewFactory(
                    hostBrowser,
                    gateway)
                : null;
            var hostSessionRegistrations = RegisterSessionFactories(
                request.WorkspaceId,
                new FilePanelSessionFactory(files, files),
                new DatabasePanelSessionFactory(hostDatabases, timeProvider, hostRedis),
                new DockerPanelSessionFactory(hostDocker, timeProvider),
                new GitPanelSessionFactory(hostGit, gitMutationCoordinator, timeProvider),
                gateway,
                isolatedCommandRuntime: null);
            var hostLifetime = new HostWorkspaceRuntimeLifetime(
                files,
                hostDatabases,
                gateway,
                hostSessionRegistrations,
                hostMonitorRegistration,
                hostMonitors);
            return new WorkspaceRuntimeServices(
                new WorkspaceRuntimeBackends(
                    hostDocker,
                    hostGit,
                    files,
                    files,
                    hostDatabases,
                    hostRedis,
                    routedBrowserFactory,
                    hostSecurity),
                request.HostServices.NetworkRoute,
                hostLifetime,
                new WorkspaceNetworkEgressFanout(
                    request.NetworkEgressState,
                    gateway),
                gateway);
        }

        var executor = new ConnectionCommandExecutor(
            request.ConnectionRuntime,
            executableLocator);
        if (request.ConnectionRuntime is not IConnectionCommandRuntime commandRuntime)
        {
            throw new InvalidOperationException(
                "The workspace isolation runtime cannot plan panel commands.");
        }

        var socksProxy = new WorkspaceIsolationSocksProxy(
            commandRuntime,
            GhostShell.Core.BuiltInConnections.Local,
            $"workspace:{binding.WorkspaceId.Value}");
        var workspaceSecurity = new ConnectionSecurityRuntime(
            request.ConnectionRuntime, secretVault, hostKeyStore, timeProvider, socksProxy);
        var tunnelFactory = new WorkspaceNetworkDatabaseTunnelFactory(
            socksProxy,
            route => new SshNetDatabaseTunnelFactory(secretVault, knownHosts, request.ConnectionRuntime, route));
        var databases = new DatabasePanelClient(
            tunnelFactory,
            GhostShell.Core.BuiltInConnections.Local,
            diagramWorkers,
            databaseContentStores,
            databaseOperations);
        var docker = new DockerEngineClient(executor, timeProvider);
        var git = new GitRepositoryClient(executor, timeProvider);
        var redis = new RedisPanelSessionFactory(
            tunnelFactory,
            GhostShell.Core.BuiltInConnections.Local);
        var routedFiles = new CatalogFileProviderRuntime(
            definitionCatalog,
            secretVault,
            knownHosts,
            workspaceSecurity,
            request.ConnectionRuntime,
            previewContentCache,
            socksProxy);
        var workspaceFiles = new WorkspaceFilePanelClient(
            new IsolatedPosixFilePanelClient(executor),
            routedFiles);
        var browserFactory = new IsolatedBrowserRendererViewFactory(
            browserRendererViewFactory,
            socksProxy);
        var monitors = new SystemMonitorPanelSessionFactory(executor, timeProvider);
        var monitorRegistration = systemMonitorFactory.Register(
            request.WorkspaceId,
            monitors);
        var sessionRegistrations = RegisterSessionFactories(
            request.WorkspaceId,
            new FilePanelSessionFactory(workspaceFiles, routedFiles),
            new DatabasePanelSessionFactory(databases, timeProvider, redis),
            new DockerPanelSessionFactory(docker, timeProvider),
            new GitPanelSessionFactory(git, gitMutationCoordinator, timeProvider),
            socksProxy,
            commandRuntime);
        var lifetime = new IsolatedWorkspaceRuntimeLifetime(
            routedFiles,
            databases,
            socksProxy,
            sessionRegistrations,
            monitorRegistration,
            monitors);
        return new WorkspaceRuntimeServices(
            new WorkspaceRuntimeBackends(
                docker,
                git,
                workspaceFiles,
                routedFiles,
                databases,
                redis,
                browserFactory,
                workspaceSecurity),
            WorkspaceNetworkRoute.ViaProxy(new Uri(
                $"socks5://127.0.0.1:{socksProxy.LocalPort}",
                UriKind.Absolute)),
            lifetime,
            new WorkspaceNetworkEgressFanout(
                request.NetworkEgressState,
                socksProxy),
            socksProxy);
    }

    private WorkspaceSessionFactoryRegistrations RegisterSessionFactories(
        WorkspaceInstanceId workspaceId,
        IFilePanelSessionFactory files,
        IDatabasePanelSessionFactory databases,
        IDockerPanelSessionFactory docker,
        IGitPanelSessionFactory git,
        IWorkspaceNetworkConnector connector,
        IConnectionCommandRuntime? isolatedCommandRuntime)
    {
        List<IDisposable> registrations = [];
        try
        {
            registrations.Add(filePanelFactory.Register(workspaceId, files));
            registrations.Add(databasePanelFactory.Register(workspaceId, databases));
            registrations.Add(dockerPanelFactory.Register(workspaceId, docker));
            registrations.Add(gitPanelFactory.Register(workspaceId, git));
            registrations.Add(networkRouteRegistry.Register(
                workspaceId,
                connector,
                isolatedCommandRuntime));
            return new WorkspaceSessionFactoryRegistrations(registrations);
        }
        catch
        {
            for (var index = registrations.Count - 1; index >= 0; index--)
            {
                registrations[index].Dispose();
            }

            throw;
        }
    }

    private sealed class HostWorkspaceRuntimeLifetime(
        CatalogFileProviderRuntime files,
        DatabasePanelClient databases,
        HostWorkspaceSocksProxy socksProxy,
        IDisposable sessionRegistrations,
        IDisposable monitorRegistration,
        SystemMonitorPanelSessionFactory monitorFactory) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            List<Exception> errors = [];
            TryDispose(sessionRegistrations, errors);
            TryDispose(files, errors);
            await TryDisposeAsync(databases.DisposeAsync, errors).ConfigureAwait(false);
            await TryDisposeAsync(socksProxy.DisposeAsync, errors).ConfigureAwait(false);
            TryDispose(monitorRegistration, errors);
            TryDispose(monitorFactory, errors);
            if (errors.Count > 0)
            {
                throw new AggregateException(
                    "One or more workspace runtime services could not be disposed.",
                    errors);
            }
        }

        private static void TryDispose(IDisposable disposable, ICollection<Exception> errors)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        private static async ValueTask TryDisposeAsync(
            Func<ValueTask> disposeAsync,
            ICollection<Exception> errors)
        {
            try
            {
                await disposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
    }

    private sealed class IsolatedWorkspaceRuntimeLifetime(
        CatalogFileProviderRuntime files,
        DatabasePanelClient databasePanelClient,
        WorkspaceIsolationSocksProxy socksProxy,
        IDisposable sessionRegistrations,
        IDisposable monitorRegistration,
        SystemMonitorPanelSessionFactory monitorFactory) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _disposeGate = new(1, 1);
        private bool _filesDisposed;
        private bool _databaseDisposed;
        private bool _sessionRegistrationsDisposed;
        private bool _monitorFactoryDisposed;
        private bool _monitorRegistrationDisposed;
        private bool _socksDisposed;

        public async ValueTask DisposeAsync()
        {
            await _disposeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                List<Exception> errors = [];
                await TryDisposeAsync(
                    _sessionRegistrationsDisposed,
                    () => DisposeAsync(sessionRegistrations),
                    () => _sessionRegistrationsDisposed = true,
                    errors).ConfigureAwait(false);
                if (!_filesDisposed)
                {
                    try
                    {
                        files.Dispose();
                        _filesDisposed = true;
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
                await TryDisposeAsync(
                    _databaseDisposed,
                    databasePanelClient.DisposeAsync,
                    () => _databaseDisposed = true,
                    errors).ConfigureAwait(false);
                await TryDisposeAsync(
                    _socksDisposed,
                    socksProxy.DisposeAsync,
                    () => _socksDisposed = true,
                    errors).ConfigureAwait(false);
                await TryDisposeAsync(
                    _monitorRegistrationDisposed,
                    () => DisposeAsync(monitorRegistration),
                    () => _monitorRegistrationDisposed = true,
                    errors).ConfigureAwait(false);
                await TryDisposeAsync(
                    _monitorFactoryDisposed,
                    () => DisposeAsync(monitorFactory),
                    () => _monitorFactoryDisposed = true,
                    errors).ConfigureAwait(false);
                if (errors.Count > 0)
                {
                    throw new AggregateException(
                        "One or more workspace runtime services could not be disposed.",
                        errors);
                }
            }
            finally
            {
                _disposeGate.Release();
            }
        }

        private static ValueTask DisposeAsync(IDisposable disposable)
        {
            disposable.Dispose();
            return ValueTask.CompletedTask;
        }

        private static async ValueTask TryDisposeAsync(
            bool isDisposed,
            Func<ValueTask> disposeAsync,
            Action markDisposed,
            ICollection<Exception> errors)
        {
            if (isDisposed)
            {
                return;
            }

            try
            {
                await disposeAsync().ConfigureAwait(false);
                markDisposed();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
    }

    private sealed class WorkspaceSessionFactoryRegistrations(
        IReadOnlyList<IDisposable> registrations) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            List<Exception> errors = [];
            for (var index = registrations.Count - 1; index >= 0; index--)
            {
                try
                {
                    registrations[index].Dispose();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            if (errors.Count > 0)
            {
                throw new AggregateException(
                    "One or more workspace session routes could not be removed.",
                    errors);
            }
        }
    }

    private sealed class WorkspaceNetworkEgressFanout(
        params IWorkspaceNetworkEgressSink[] targets) : IWorkspaceNetworkEgressSink
    {
        public void Apply(WorkspaceNetworkEgress egress, string? authenticationRouteIdentity)
        {
            foreach (var target in targets)
            {
                target.Apply(egress, authenticationRouteIdentity);
            }
        }

        public void Apply(WorkspaceNetworkEgress egress)
        {
            foreach (var target in targets)
            {
                target.Apply(egress);
            }
        }
    }
}
