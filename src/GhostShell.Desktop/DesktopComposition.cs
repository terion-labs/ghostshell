using GhostShell.Agent.Providers;
using GhostShell.Agent.Runtime;
using GhostShell.App;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Application.ApplicationUpdates;
using GhostShell.Application.Previews;
using GhostShell.Browser;
using GhostShell.ConnectionBackend;
using GhostShell.Core;
using GhostShell.Databases;
using GhostShell.Docker;
using GhostShell.Files;
using GhostShell.Git;
using GhostShell.Infrastructure;
using GhostShell.Mcp;
using GhostShell.Monitoring;
using GhostShell.Previews;
using GhostShell.Redis;
using GhostShell.SessionHost;
using GhostShell.Terminal;
using GhostShell.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.Desktop;

public static class DesktopComposition
{
    public static ServiceProvider CreateServiceProvider(DesktopProfileConfiguration? profile = null)
    {
        profile ??= DesktopProfileConfiguration.CreateDefault();
        var storage = new SqliteStorageOptions(profile.Data.DatabasePath);
        var services = new ServiceCollection();
        services.AddSingleton(profile);
        services.AddSingleton(profile.Data);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DesktopUpdateShutdown>();
        services.AddSingleton<IApplicationUpdateService>(provider =>
            InstalledApplicationUpdates.Create(
                provider.GetRequiredService<DesktopUpdateShutdown>().Request));
        services.AddSingleton(_ => ThemePreference.DefaultFor(CurrentOperatingSystem()));
        services.AddSingleton<INativeNotificationService>(_ =>
            NativeNotificationServiceSelector.CreateForCurrentPlatform());
        services.AddSingleton<IHostAccessibilityPreferencesSource>(_ =>
            HostAccessibilityPreferencesSourceSelector.CreateForCurrentPlatform());
        services.AddSingleton<IActiveWindowBoundsSource>(_ =>
            ActiveWindowBoundsSourceSelector.CreateForCurrentPlatform());
        // A dedicated, un-audited vault for application-security material,
        // deliberately: the audit sink writes into the very database these
        // keys unlock, so auditing the key fetch would need the key it is
        // fetching. Nothing of the user's credentials goes through it.
        services.AddSingleton(_ => new ApplicationSecurityVault(
            PlatformSecretVaultFactory.Create(new SecretVaultFactoryOptions
            {
                DataDirectory = profile.Data.DataDirectory,
                ServiceName = profile.SecretServiceName,
            }).Vault));
        services.AddSingleton(provider => new ApplicationEncryptionRuntime(
            provider.GetRequiredService<ApplicationSecurityVault>().Vault,
            storage.DatabasePath,
            // Resolved at re-key time, not construction time: the runtime is
            // built before the database so the very first open has its key
            // in hand.
            () => provider.GetRequiredService<GhostShellDatabase>()));
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IBiometricAuthenticator, MacOsBiometricAuthenticator>();
        }
        services.AddSingleton<IStartupProtection>(provider => new StartupProtectionRuntime(
            provider.GetRequiredService<ApplicationSecurityVault>().Vault,
            profile.Data.DataDirectory,
            timeProvider: null,
            provider.GetRequiredService<ApplicationEncryptionRuntime>()));
        services.AddSingleton<IApplicationEncryption>(provider =>
            provider.GetRequiredService<ApplicationEncryptionRuntime>());
        services.AddSingleton(provider =>
        {
            // Resolved once here rather than inside the provider delegate:
            // that delegate also runs while the container is tearing down,
            // when nothing may be resolved any more.
            var encryption = provider.GetRequiredService<ApplicationEncryptionRuntime>();
            return storage with
            {
                PasswordProvider = () => encryption.ConfigDatabasePassword,
            };
        });
        services.AddSingleton(profile.Artifacts);
        services.AddSingleton(profile.Browser);
        services.AddSingleton<GhostShellDatabase>();
        services.AddSingleton<IAuditStore, SqliteAuditStore>();
        services.AddSingleton<IAgentRunAuditReader, SqliteAgentRunAuditReader>();
        services.AddSingleton<AgentAuditRecovery>();
        services.AddSingleton(BuiltInAgentTools.Catalog);
        services.AddSingleton<AgentCapabilityBroker>();
        services.AddSingleton<IAgentCapabilityBroker>(provider =>
            provider.GetRequiredService<AgentCapabilityBroker>());
        services.AddSingleton<IAgentAuthorizationConsumer>(provider =>
            provider.GetRequiredService<AgentCapabilityBroker>());
        services.AddSingleton<IAgentMcpRunAuthorityVerifier>(provider =>
            provider.GetRequiredService<AgentCapabilityBroker>());
        services.AddSingleton<AgentTerminalActionComposer>();
        services.AddSingleton<AgentBrowserActionComposer>();
        services.AddSingleton<AgentFileActionComposer>();
        services.AddSingleton<AgentProcessListActionComposer>();
        services.AddSingleton<AgentStatisticsReadActionComposer>();
        services.AddSingleton<AgentDatabaseReadActionComposer>();
        services.AddSingleton<AgentDockerReadActionComposer>();
        services.AddSingleton<AgentGitActionComposer>();
        services.AddSingleton<AgentMcpToolCallActionComposer>();
        services.AddSingleton<AgentWebToolActionComposer>();
        services.AddSingleton<AgentPanelActionComposer>();
        services.AddSingleton<AgentWorkspaceLayoutActionComposer>();
        services.AddSingleton<AgentWorkspaceGraphActionComposer>();
        services.AddSingleton<TerminalStartupCommandDispatcher>();
        services.AddSingleton<ISecretAccessAuditSink, SqliteSecretAccessAuditSink>();
        services.AddSingleton(provider => PlatformSecretVaultFactory.Create(
            new SecretVaultFactoryOptions
            {
                DataDirectory = Path.GetDirectoryName(
                    provider.GetRequiredService<SqliteStorageOptions>().DatabasePath),
                AuditSink = provider.GetRequiredService<ISecretAccessAuditSink>(),
                ServiceName = profile.SecretServiceName,
            }));
        services.AddSingleton(provider =>
            provider.GetRequiredService<SecretVaultFactoryResult>().Diagnostic);
        services.AddSingleton<ISecretVault>(provider =>
            provider.GetRequiredService<SecretVaultFactoryResult>().Vault);
        services.AddSingleton(new ConnectionCredentialBrokerOptions
        {
            SelfReentry = SelfReentryLaunch.Detect(),
        });
        services.AddSingleton<IConnectionCredentialBroker, ConnectionCredentialBroker>();
        services.AddSingleton(_ => ConnectionRuntimeOptions.Detect());
        var executableLocator = new BundledConnectionEngineExecutableLocator(
            new PathConnectionExecutableLocator());
        services.AddSingleton<IConnectionExecutableLocator>(executableLocator);
        var hasWorkspaceIsolationProvider = false;
        if (new WorkspaceIsolationPlatformResolver().ResolveCurrent()
            is WorkspaceIsolationPlatformSupport.Available { Adapter: var isolationAdapter })
        {
            services.AddSingleton<IWorkspaceIsolationRuntimeInstaller>(_ =>
                new WorkspaceIsolationRuntimeInstaller(isolationAdapter.Installation));
            if (WorkspaceSdkIsolationProvider.FindBundledRuntime()
                is { } runtimeExecutable)
            {
                services.AddSingleton<IWorkspaceIsolationProvider>(_ =>
                    isolationAdapter.CreateProvider(runtimeExecutable, Path.Combine(profile.Data.DataDirectory, "sdk-workspaces")));
                hasWorkspaceIsolationProvider = true;
            }
        }

        services.AddSingleton<INetworkConnectionProvider>(provider =>
            new ProxyNetworkConnectionProvider(
                provider.GetRequiredService<ISecretVault>(),
                provider.GetService<IWorkspaceIsolationProvider>()));
        services.AddSingleton<INetworkPasswordPrompt, AvaloniaNetworkPasswordPrompt>();
        if (hasWorkspaceIsolationProvider)
        {
            services.AddSingleton<IWorkspaceIsolationEgressGuard,
                WorkspaceIsolationEgressGuard>();
        }

        AddVpnProvider(services, NetworkConnectionKind.WireGuard);
        AddVpnProvider(services, NetworkConnectionKind.OpenVpn);
        AddVpnProvider(services, NetworkConnectionKind.AnyConnect);
        AddVpnProvider(services, NetworkConnectionKind.Tailscale);

        services.AddSingleton<IWorkspacePacketGatewayRuntime>(provider =>
            WorkspacePacketGatewayRuntimeFactory.Create(
                provider.GetServices<INetworkConnectionProvider>(),
                provider.GetRequiredService<IConnectionExecutableLocator>(),
                provider.GetRequiredService<ISecretVault>()));
        services.AddSingleton<IWorkspaceNetworkRuntime>(provider =>
            new WorkspaceNetworkRuntime(
                provider.GetServices<INetworkConnectionProvider>(),
                provider.GetService<IWorkspaceIsolationEgressGuard>(),
                provider.GetRequiredService<INetworkPasswordPrompt>(),
                provider.GetRequiredService<IWorkspacePacketGatewayRuntime>(),
                provider.GetRequiredService<ISecretVault>()));
        services.AddSingleton<WorkspaceNetworkRouteRegistry>();
        services.AddSingleton<IWorkspaceNetworkRouteResolver>(provider =>
            provider.GetRequiredService<WorkspaceNetworkRouteRegistry>());

        services.AddSingleton<IConnectionCommandRunner, ProcessConnectionCommandRunner>();
        services.AddSingleton<IConnectionRuntimeAdapter, LocalConnectionRuntimeAdapter>();
        services.AddSingleton<IConnectionRuntimeAdapter, SshConnectionRuntimeAdapter>();
        services.AddSingleton<IConnectionRuntimeAdapter, DockerConnectionRuntimeAdapter>();
        services.AddSingleton<IConnectionRuntimeAdapter, WslConnectionRuntimeAdapter>();
        services.AddSingleton<IConnectionRuntime, ConnectionRuntime>();
        services.AddSingleton<IConnectionCommandExecutor, ConnectionCommandExecutor>();
        services.AddSingleton(provider => new WorkspaceConnectionBackendFactory(
            () => OperatingSystem.IsMacOSVersionAtLeast(26)
                && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                && WorkspaceSdkIsolationProvider.FindBundledRuntime() is { } executable
                ? WorkspaceSdkIsolationProvider.CreateServiceProvider(executable,
                    Path.Combine(profile.Data.DataDirectory, "connection-services"))
                : new ContainerRelayIsolationProvider(provider.GetRequiredService<IConnectionExecutableLocator>(),
                    async (architecture, token) => (await WorkspaceDatabaseBackend.EnsureDefaultArchiveAsync(architecture, token).ConfigureAwait(false)).Path),
            provider.GetRequiredService<IWorkspacePacketGatewayRuntime>(),
            provider.GetRequiredService<ISecretVault>(),
            provider.GetRequiredService<ISshHostKeyTrustStore>(),
            provider.GetRequiredService<Func<DatabaseValueContentStore>>(),
            provider.GetRequiredService<IDatabaseOperationExecutor>(),
            SelfReentryLaunch.Detect()));
        services.AddSingleton<IWorkspaceRuntimeServicesFactory,
            DesktopWorkspaceRuntimeServicesFactory>();
        services.AddSingleton<IDockerEngineClient, DockerEngineClient>();
        services.AddSingleton<IGitRepositoryClient, GitRepositoryClient>();
        services.AddSingleton<IGitRepositoryMutationCoordinator,
            GitRepositoryMutationCoordinator>();
        services.AddSingleton<SshKnownHostStore>();
        services.AddSingleton<ISshHostKeyTrustStore>(provider =>
            provider.GetRequiredService<SshKnownHostStore>());
        services.AddSingleton<IConnectionSecurityRuntime, ConnectionSecurityRuntime>();
        services.AddSingleton<SqliteFilePreviewPreferences>();
        services.AddSingleton<IFilePreviewPreferences>(provider =>
            provider.GetRequiredService<SqliteFilePreviewPreferences>());
        services.AddSingleton<IGitPanelPreferences, SqliteGitPanelPreferences>();
        services.AddSingleton<SqliteBrowserProfilePreferences>();
        services.AddSingleton<IBrowserProfilePreferences>(provider =>
            provider.GetRequiredService<SqliteBrowserProfilePreferences>());
        services.AddSingleton<IBrowserProfileAuthenticationResolver,
            BrowserProfileAuthenticationResolver>();
        services.AddSingleton(provider => new EncryptedBrowserProfileStateStore(
            provider.GetRequiredService<BrowserProfileStoragePaths>()
                .PersistentDirectory,
            provider.GetRequiredService<IApplicationEncryption>()));
        services.AddSingleton<IBrowserProfileStateStore>(provider =>
            provider.GetRequiredService<EncryptedBrowserProfileStateStore>());
        services.AddSingleton(provider => new CefBrowserProfileStore(
            provider.GetRequiredService<IBrowserProfileAuthenticationResolver>(),
            provider.GetRequiredService<IBrowserProfileStateStore>(),
            provider.GetRequiredService<BrowserProfileStoragePaths>()
                .RuntimeDirectory));
        services.AddSingleton<IBrowserProfileDataControl>(provider =>
            provider.GetRequiredService<CefBrowserProfileStore>());
        services.AddSingleton(provider => new PreviewContentCache(
            provider.GetRequiredService<IFilePreviewPreferences>(),
            Path.Combine(
                provider.GetRequiredService<LocalArtifactPaths>().CacheDirectory,
                "previews"),
            provider.GetRequiredService<IApplicationEncryption>()));
        services.AddSingleton<IPreviewCacheControl>(provider =>
            provider.GetRequiredService<PreviewContentCache>());
        services.AddSingleton<IInMemoryDatabaseRegistry, SqliteInMemoryDatabaseRegistry>();
        services.AddSingleton(provider => new CatalogFileProviderRuntime(
            provider.GetRequiredService<IDefinitionCatalog>(),
            provider.GetRequiredService<ISecretVault>(),
            provider.GetRequiredService<ISshHostKeyTrustStore>(),
            provider.GetRequiredService<IConnectionSecurityRuntime>(),
            provider.GetRequiredService<IConnectionRuntime>(),
            provider.GetRequiredService<PreviewContentCache>()));
        services.AddSingleton<IFilePanelClient>(provider =>
            provider.GetRequiredService<CatalogFileProviderRuntime>());
        services.AddSingleton<IFileTransferQueueClient>(provider =>
            provider.GetRequiredService<CatalogFileProviderRuntime>());
        services.AddSingleton<FilePanelSessionFactory>();
        services.AddSingleton<WorkspaceFilePanelSessionFactory>();
        services.AddSingleton<IFilePanelSessionFactory>(provider =>
            provider.GetRequiredService<WorkspaceFilePanelSessionFactory>());
        services.AddSingleton<BrowserPanelSessionFactory>();
        services.AddSingleton<IBrowserPanelSessionFactory>(provider =>
            provider.GetRequiredService<BrowserPanelSessionFactory>());
        services.AddSingleton<IAgentWebToolExecutor, AgentWebToolExecutor>();
        services.AddSingleton<SystemMonitorPanelSessionFactory>();
        services.AddSingleton<WorkspaceSystemMonitorPanelSessionFactory>();
        services.AddSingleton<ISystemMonitorPanelSessionFactory>(provider =>
            provider.GetRequiredService<WorkspaceSystemMonitorPanelSessionFactory>());
        services.AddSingleton<IDatabaseTunnelFactory, SshNetDatabaseTunnelFactory>();
        services.AddSingleton<SshNetBrowserTunnelFactory>();
        services.AddSingleton<IDatabaseDiagramWorkerFactory, DatabaseDiagramWorker>();
        services.AddSingleton<Func<DatabaseValueContentStore>>(provider =>
        {
            var directory = Path.Combine(
                provider.GetRequiredService<LocalArtifactPaths>().CacheDirectory,
                "database-results");
            return () => new DatabaseResultContentStore(directory);
        });
        services.AddSingleton<IDatabaseOperationExecutor, DatabaseOperationWorker>();
        services.AddSingleton<IDatabasePanelClient, DatabasePanelClient>();
        services.AddSingleton<IRedisPanelSessionFactory>(provider =>
            new RedisPanelSessionFactory(
                provider.GetRequiredService<IDatabaseTunnelFactory>()));
        services.AddSingleton<DatabasePanelSessionFactory>();
        services.AddSingleton<WorkspaceDatabasePanelSessionFactory>();
        services.AddSingleton<IDatabasePanelSessionFactory>(provider =>
            provider.GetRequiredService<WorkspaceDatabasePanelSessionFactory>());
        services.AddSingleton<DockerPanelSessionFactory>();
        services.AddSingleton<WorkspaceDockerPanelSessionFactory>();
        services.AddSingleton<IDockerPanelSessionFactory>(provider =>
            provider.GetRequiredService<WorkspaceDockerPanelSessionFactory>());
        services.AddSingleton<GitPanelSessionFactory>();
        services.AddSingleton<WorkspaceGitPanelSessionFactory>();
        services.AddSingleton<IGitPanelSessionFactory>(provider =>
            provider.GetRequiredService<WorkspaceGitPanelSessionFactory>());
        services.AddSingleton<IDatabaseConnectionCatalog, RedisConnectionCatalog>();
        services.AddSingleton<ISqlLanguageService, CalciteSqlLanguageService>();
        // Keep ImageMagick previews unavailable until native decoding runs in a
        // killable worker. Process-global limits reduce risk, but cancellation
        // cannot interrupt an in-process coder that stops making progress.
        services.AddSingleton<IArchiveTableOfContents, ArchiveTableOfContents>();
        // Keep PDF previews unavailable until PDFium runs in a killable worker.
        // Task cancellation cannot interrupt an in-process native parse or
        // render after it starts; the file panel already treats a missing
        // renderer as an unsupported preview rather than a failure.
        services.AddSingleton<IFileProviderProfileRuntime>(provider =>
            provider.GetRequiredService<CatalogFileProviderRuntime>());
        services.AddSingleton(provider => new CatalogAiProviderRuntime(
            provider.GetRequiredService<IDefinitionCatalog>(),
            provider.GetRequiredService<ISecretVault>(),
            oauthOptions: provider.GetRequiredService<AiProviderOAuthOptions>(),
            routedHandlerFactory: provider.GetRequiredService<WorkspaceNetworkRouteRegistry>().CreateHttpHandler));
        services.AddSingleton<IAiProviderProfileRuntime>(provider =>
            provider.GetRequiredService<CatalogAiProviderRuntime>());
        services.AddSingleton(_ => new AiProviderOAuthOptions(
            gitHubClientId: Environment.GetEnvironmentVariable(
                "GHOSTSHELL_GITHUB_OAUTH_CLIENT_ID")));
        services.AddSingleton<AiProviderAuthenticationRuntime>();
        services.AddSingleton<IAiProviderAuthenticationRuntime>(provider =>
            provider.GetRequiredService<AiProviderAuthenticationRuntime>());
        services.AddSingleton<IAgentApprovalPrincipal, DesktopAgentApprovalPrincipal>();
        services.AddSingleton<IAgentProviderResolver, CatalogAgentProviderResolver>();
        services.AddSingleton<IAgentSessionCheckpointStore, SqliteAgentSessionCheckpointStore>();
        services.AddSingleton<IAgentModelFavoriteStore, SqliteAgentModelFavoriteStore>();
        services.AddSingleton<IAgentPolicyPreferenceStore,
            SqliteAgentPolicyPreferenceStore>();
        services.AddSingleton<IMcpServerDiagnosticStore,
            SqliteMcpServerDiagnosticStore>();
        services.AddSingleton<AgentPolicyCoordinator>();
        services.AddSingleton<AgentMcpSessionHost>();
        services.AddSingleton<IAgentMcpSessionHost>(provider =>
            provider.GetRequiredService<AgentMcpSessionHost>());
        services.AddSingleton<IMcpServerDiagnostics>(provider =>
            provider.GetRequiredService<AgentMcpSessionHost>());
        services.AddSingleton<IMcpCredentialSessionInvalidator>(provider =>
            provider.GetRequiredService<AgentMcpSessionHost>());
        services.AddSingleton<IAgentWorkspaceRuntimeFactory,
            DesktopAgentWorkspaceRuntimeFactory>();
        services.AddSingleton(typeof(IDefinitionRepository<>), typeof(SqliteDefinitionRepository<>));
        services.AddSingleton<ILayoutGraphStore, SqliteLayoutGraphStore>();
        services.AddSingleton<IDefinitionCatalog, DefinitionCatalog>();
        services.AddSingleton<IDefinitionBundleStore, SqliteDefinitionBundleStore>();
        services.AddSingleton<IApplicationRunStore, SqliteApplicationRunStore>();
        services.AddSingleton<SqliteRuntimeRecoveryStore>();
        services.AddSingleton<IRuntimeRecoveryStore>(provider =>
            provider.GetRequiredService<SqliteRuntimeRecoveryStore>());
        services.AddSingleton<IRuntimeRecoveryDataControl>(provider =>
            provider.GetRequiredService<SqliteRuntimeRecoveryStore>());
        services.AddSingleton<ILocalArtifactControl, FileSystemLocalArtifactControl>();
        services.AddSingleton<IOnboardingProgressStore, SqliteOnboardingProgressStore>();
        services.AddSingleton<ISessionRestorePreferenceStore,
            SqliteSessionRestorePreferenceStore>();
        services.AddSingleton<SqliteTerminalMultiplexerStore>();
        services.AddSingleton<ITerminalMultiplexingPreferenceStore>(provider =>
            provider.GetRequiredService<SqliteTerminalMultiplexerStore>());
        services.AddSingleton<ITerminalMultiplexerLeaseStore>(provider =>
            provider.GetRequiredService<SqliteTerminalMultiplexerStore>());
        services.AddSingleton<TerminalMultiplexerCoordinator>();
        services.AddSingleton<SqliteRecentSessionStore>();
        services.AddSingleton<IRecentSessionStore>(provider =>
            provider.GetRequiredService<SqliteRecentSessionStore>());
        services.AddSingleton<IRecentSessionRetentionStore>(provider =>
            provider.GetRequiredService<SqliteRecentSessionStore>());
        services.AddSingleton<IRecentSessionHistoryExporter,
            DeterministicRecentSessionHistoryExporter>();
        services.AddSingleton<IDiagnosticsBundleExporter, DeterministicDiagnosticsBundleExporter>();
        services.AddSingleton<IDiagnosticsBundleRequestSource, SafeDiagnosticsBundleRequestSource>();
        services.AddSingleton<IDiagnosticsArtifactPresenter, DesktopDiagnosticsArtifactPresenter>();
        services.AddSingleton<RecentSessionHistory>();
        services.AddSingleton<RuntimeRecoveryWriter>();
        services.AddSingleton<DesktopRunFinalizer>();
        services.AddSingleton<SessionRestoreCoordinator>();
        services.AddSingleton<ApplicationStartupState>();
        services.AddSingleton<ITerminalSessionFactory>(_ =>
            TerminalSessionFactorySelector.CreateForCurrentPlatform());
        services.AddSingleton<ISessionLifecyclePolicy, DesktopLifecyclePolicy>();
        services.AddSingleton<InMemorySessionHostClient>();
        services.AddSingleton<ISessionHostClient>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentTerminalSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentBrowserSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentFileSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentProcessSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentStatisticsSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentDatabaseSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentDockerSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentGitSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentPanelSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentWorkspaceGraphSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentWorkspaceLayoutSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IAgentWebToolSessionHost>(provider =>
            provider.GetRequiredService<InMemorySessionHostClient>());
        services.AddSingleton<IGlobalHotkeyService>(_ =>
            GlobalHotkeyServiceSelector.CreateForCurrentPlatform());
        services.AddSingleton<IScreenColorSampler>(_ =>
            ScreenColorSamplerSelector.Create());
        services.AddSingleton(provider => new OnboardingViewModel(
            provider.GetRequiredService<IOnboardingProgressStore>(),
            provider.GetRequiredService<IDefinitionCatalog>(),
            provider.GetRequiredService<IConnectionRuntime>(),
            provider.GetRequiredService<ISecretVault>().Availability));
        services.AddSingleton<RecoveryDataControlViewModel>();
        services.AddSingleton<LocalArtifactControlViewModel>();
        services.AddSingleton<IProductComponentCatalog, DesktopProductComponentCatalog>();
        services.AddSingleton<IBrowserRendererViewFactory, DesktopBrowserRendererViewFactory>();
        services.AddSingleton<AppearancePreviewCoordinator>();
        services.AddSingleton<WorkspaceDefinitionOccupancy>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindowViewModelFactory>(provider =>
            () => ActivatorUtilities.CreateInstance<MainWindowViewModel>(
                provider,
                MainWindowRole.Additional));
        services.AddSingleton<GhostShell.App.QuickTerminalController>();
        services.AddSingleton<GhostShell.App.App>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static void AddVpnProvider(
        IServiceCollection services,
        NetworkConnectionKind kind)
    {
        services.AddSingleton<INetworkConnectionProvider>(provider =>
            new IsolatedVpnConnectionProvider(
                kind,
                provider.GetRequiredService<ISecretVault>(),
                provider.GetService<IWorkspaceIsolationProvider>(),
                provider.GetRequiredService<IConnectionExecutableLocator>(),
                Path.Combine(provider.GetRequiredService<GhostShellDataPaths>().DataDirectory, "vpn-state")));
    }

    private static HostOperatingSystem CurrentOperatingSystem() =>
        OperatingSystem.IsMacOS()
            ? HostOperatingSystem.MacOS
            : OperatingSystem.IsWindows()
                ? HostOperatingSystem.Windows
                : HostOperatingSystem.Linux;
}
