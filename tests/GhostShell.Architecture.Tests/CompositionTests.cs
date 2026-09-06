using System.Reflection;
using GhostShell.Agent.Providers;
using GhostShell.App;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Browser;
using GhostShell.Core;
using GhostShell.Desktop;
using GhostShell.Infrastructure;
using GhostShell.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.Architecture.Tests;

public sealed class CompositionTests
{
    /// <summary>
    /// The file preview opens databases by path, and the path comes from the
    /// composed file client rather than the one the Files tests construct
    /// directly. A wrapper that forgets to forward materialization compiles
    /// perfectly and fails only at the moment a user selects a database, so the
    /// composed chain is asserted here.
    /// </summary>
    [Fact]
    public async Task ComposedFileClientCanServeWholeFileContent()
    {
        await using var services = DesktopComposition.CreateServiceProvider();

        var filePanel = services.GetRequiredService<IFilePanelClient>();

        Assert.IsAssignableFrom<IFileContentSource>(filePanel);
    }

    [Fact]
    public async Task Native_preview_decoders_without_killable_workers_are_not_composed()
    {
        await using var services = DesktopComposition.CreateServiceProvider();

        Assert.Null(services.GetService<IImagePreviewDecoder>());
        Assert.Null(services.GetService<IPdfPreviewRenderer>());
    }

    [Fact]
    public async Task Workspace_isolation_provider_requires_a_supported_host_and_installed_runtime()
    {
        await using var services = DesktopComposition.CreateServiceProvider();

        var support = new WorkspaceIsolationPlatformResolver().ResolveCurrent();
        var containerExecutable = services
            .GetRequiredService<IConnectionExecutableLocator>()
            .Find("container");
        var provider = services.GetService<IWorkspaceIsolationProvider>();
        var installer = services.GetService<IWorkspaceIsolationRuntimeInstaller>();

        if (support is WorkspaceIsolationPlatformSupport.Available { Adapter: var adapter })
        {
            Assert.IsType<WorkspaceIsolationRuntimeInstaller>(installer);
            if (containerExecutable is not null)
            {
                var isolationProvider = Assert.IsAssignableFrom<IWorkspaceIsolationProvider>(provider);
                Assert.Equal(adapter.Descriptor, isolationProvider.Descriptor);
            }
            else
            {
                Assert.Null(provider);
            }
        }
        else
        {
            Assert.Null(provider);
            Assert.Null(installer);
        }
    }

    [Fact]
    public async Task DesktopVpnProvidersUseTheBundledConnectionEngineLocator()
    {
        await using var services = DesktopComposition.CreateServiceProvider();

        Assert.IsType<BundledConnectionEngineExecutableLocator>(
            services.GetRequiredService<IConnectionExecutableLocator>());
    }

    [Fact]
    public async Task DesktopGraphResolvesOneSessionHostClientAndPresentationRoot()
    {
        await using var services = DesktopComposition.CreateServiceProvider();

        var firstClient = services.GetRequiredService<ISessionHostClient>();
        var secondClient = services.GetRequiredService<ISessionHostClient>();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var application = services.GetRequiredService<GhostShell.App.App>();
        var definitions = services.GetRequiredService<IDefinitionBundleStore>();
        var runStore = services.GetRequiredService<IApplicationRunStore>();
        var recentSessions = services.GetRequiredService<IRecentSessionStore>();
        var recentSessionRetention = services.GetRequiredService<IRecentSessionRetentionStore>();
        var historyExporter = services.GetRequiredService<IRecentSessionHistoryExporter>();
        var vault = services.GetRequiredService<ISecretVault>();
        var auditSink = services.GetRequiredService<ISecretAccessAuditSink>();
        var vaultDiagnostic = services.GetRequiredService<SecretVaultFactoryDiagnostic>();
        var filePanel = services.GetRequiredService<IFilePanelClient>();
        var transferQueue = services.GetRequiredService<IFileTransferQueueClient>();
        var fileProfiles = services.GetRequiredService<IFileProviderProfileRuntime>();
        var browserSessionFactory =
            services.GetRequiredService<IBrowserPanelSessionFactory>();
        var browserViewFactory =
            services.GetRequiredService<IBrowserRendererViewFactory>();
        var aiProfiles = services.GetRequiredService<IAiProviderProfileRuntime>();
        var concreteAiProfiles = services.GetRequiredService<CatalogAiProviderRuntime>();
        var aiAuthentication =
            services.GetRequiredService<IAiProviderAuthenticationRuntime>();
        var concreteAiAuthentication =
            services.GetRequiredService<AiProviderAuthenticationRuntime>();
        var workspaceAgentFactory =
            services.GetRequiredService<IAgentWorkspaceRuntimeFactory>();
        var approvalPrincipal =
            services.GetRequiredService<IAgentApprovalPrincipal>();
        var capabilityBroker = services.GetRequiredService<IAgentCapabilityBroker>();
        var authorizationConsumer =
            services.GetRequiredService<IAgentAuthorizationConsumer>();
        var mcpRunAuthorityVerifier =
            services.GetRequiredService<IAgentMcpRunAuthorityVerifier>();
        var mcpSessionHost =
            services.GetRequiredService<IAgentMcpSessionHost>();
        var mcpCredentialSessionInvalidator =
            services.GetRequiredService<IMcpCredentialSessionInvalidator>();
        var agentTerminalHost =
            services.GetRequiredService<IAgentTerminalSessionHost>();
        var agentBrowserHost =
            services.GetRequiredService<IAgentBrowserSessionHost>();
        var agentProcessHost =
            services.GetRequiredService<IAgentProcessSessionHost>();
        var processActionComposer =
            services.GetRequiredService<AgentProcessListActionComposer>();
        var agentStatisticsHost =
            services.GetRequiredService<IAgentStatisticsSessionHost>();
        var statisticsActionComposer =
            services.GetRequiredService<AgentStatisticsReadActionComposer>();
        var agentCheckpointStore =
            services.GetRequiredService<IAgentSessionCheckpointStore>();
        var agentModelFavoriteStore =
            services.GetRequiredService<IAgentModelFavoriteStore>();
        var agentPolicyPreferenceStore =
            services.GetRequiredService<IAgentPolicyPreferenceStore>();
        var agentPolicyCoordinator =
            services.GetRequiredService<AgentPolicyCoordinator>();
        var diagnosticsExporter = services.GetRequiredService<IDiagnosticsBundleExporter>();
        var diagnosticsSource = services.GetRequiredService<IDiagnosticsBundleRequestSource>();
        var diagnosticsPresenter = services.GetRequiredService<IDiagnosticsArtifactPresenter>();
        var recoveryStore = services.GetRequiredService<IRuntimeRecoveryStore>();
        var recoveryDataControl = services.GetRequiredService<IRuntimeRecoveryDataControl>();
        var recoveryDataViewModel = services.GetRequiredService<RecoveryDataControlViewModel>();
        var localArtifactControl = services.GetRequiredService<ILocalArtifactControl>();
        var localArtifactViewModel = services.GetRequiredService<LocalArtifactControlViewModel>();

        Assert.Same(firstClient, secondClient);
        Assert.Same(concreteAiAuthentication, aiAuthentication);
        Assert.Same(firstClient, viewModel.SessionClient);
        Assert.True(viewModel.IsWorkspaceVisible);
        Assert.NotNull(application);
        Assert.NotNull(definitions);
        Assert.NotNull(runStore);
        Assert.IsType<SqliteRecentSessionStore>(recentSessions);
        Assert.Same(recentSessions, recentSessionRetention);
        Assert.IsType<DeterministicRecentSessionHistoryExporter>(historyExporter);
        Assert.IsType<SqliteSecretAccessAuditSink>(auditSink);
        Assert.IsType<AuditedSecretVault>(vault);
        Assert.Same(vault.Availability, vaultDiagnostic.Availability);
        Assert.Same(filePanel, transferQueue);
        Assert.Same(filePanel, fileProfiles);
        Assert.Contains(filePanel.Profiles, profile => string.Equals(profile.Id, "builtin.files.home", StringComparison.Ordinal));
        Assert.Equal(
            "GhostShell.Browser.BrowserPanelSessionFactory",
            browserSessionFactory.GetType().FullName);
        Assert.Equal(
            BrowserCapabilityProfile.Production.Capabilities.Values,
            browserSessionFactory.Capabilities.Values);
        Assert.Same(browserViewFactory, viewModel.BrowserRendererViewFactory);
        Assert.Equal(
            "GhostShell.Desktop.DesktopBrowserRendererViewFactory",
            browserViewFactory.GetType().FullName);

        var hostHello = Assert.IsType<HostResult<HostHello>.Success>(
            await firstClient.NegotiateAsync(
                new ClientHello(
                    [ProtocolVersions.Current],
                    BrowserCapabilityProfile.FullAutomationCandidate.Capabilities),
                OperationContext.ForHuman(
                    new ClientId("desktop-capability-check")),
                CancellationToken.None));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserReadState));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserNavigate));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserSnapshot));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserClick));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserFill));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserCheck));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserOriginGuard));
        Assert.True(hostHello.Value.Capabilities.Contains(
            SessionCapabilities.BrowserAgentInputBarrier));
        Assert.Same(concreteAiProfiles, aiProfiles);
        Assert.Null(services.GetService<IAgentChatRuntime>());
        Assert.Null(services.GetService<IGovernedAgentRuntime>());
        using (var firstWorkspaceAgent = workspaceAgentFactory.Create(
                   new WorkspaceInstanceId("composition-workspace-one"),
                   new AgentConversationScopeId("composition-scope-one"),
                   AgentPolicy.Default))
        using (var secondWorkspaceAgent = workspaceAgentFactory.Create(
                   new WorkspaceInstanceId("composition-workspace-two"),
                   new AgentConversationScopeId("composition-scope-two"),
                   AgentPolicy.Default))
        {
            Assert.NotSame(firstWorkspaceAgent, secondWorkspaceAgent);
        }
        Assert.Equal(
            approvalPrincipal.Actor.ClientId,
            viewModel.ClientId);
        Assert.IsType<AgentCapabilityBroker>(capabilityBroker);
        Assert.Same(capabilityBroker, authorizationConsumer);
        Assert.Same(capabilityBroker, mcpRunAuthorityVerifier);
        Assert.Same(
            mcpSessionHost,
            mcpCredentialSessionInvalidator);
        Assert.Same(firstClient, agentTerminalHost);
        Assert.Same(firstClient, agentBrowserHost);
        Assert.Same(firstClient, agentProcessHost);
        Assert.NotNull(processActionComposer);
        Assert.Same(firstClient, agentStatisticsHost);
        Assert.NotNull(statisticsActionComposer);
        Assert.IsType<SqliteAgentSessionCheckpointStore>(agentCheckpointStore);
        Assert.IsType<SqliteAgentModelFavoriteStore>(agentModelFavoriteStore);
        Assert.IsType<SqliteAgentPolicyPreferenceStore>(agentPolicyPreferenceStore);
        Assert.NotNull(agentPolicyCoordinator);
        Assert.IsType<DeterministicDiagnosticsBundleExporter>(diagnosticsExporter);
        Assert.IsType<SafeDiagnosticsBundleRequestSource>(diagnosticsSource);
        Assert.IsType<DesktopDiagnosticsArtifactPresenter>(diagnosticsPresenter);
        Assert.Same(recoveryStore, recoveryDataControl);
        Assert.NotNull(recoveryDataViewModel);
        Assert.IsType<FileSystemLocalArtifactControl>(localArtifactControl);
        Assert.NotNull(localArtifactViewModel);
    }

    [Fact]
    public async Task Main_window_factory_creates_independent_presentation_roots()
    {
        await using var services = DesktopComposition.CreateServiceProvider();
        var occupancy = services.GetRequiredService<WorkspaceDefinitionOccupancy>();
        var primary = services.GetRequiredService<MainWindowViewModel>();
        var factory = services.GetRequiredService<MainWindowViewModelFactory>();

        using var first = factory();
        using var second = factory();
        var occupancyField = typeof(MainWindowViewModel).GetField(
            "_workspaceDefinitionOccupancy",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotSame(primary, first);
        Assert.NotSame(first, second);
        Assert.NotEqual(primary.WindowId, first.WindowId);
        Assert.NotEqual(first.WindowId, second.WindowId);
        Assert.Same(
            occupancy,
            services.GetRequiredService<WorkspaceDefinitionOccupancy>());
        Assert.Same(occupancy, occupancyField?.GetValue(primary));
        Assert.Same(occupancy, occupancyField?.GetValue(first));
        Assert.Same(occupancy, occupancyField?.GetValue(second));
        Assert.Equal(MainWindowRole.Primary, primary.Role);
        Assert.Equal(MainWindowRole.Additional, first.Role);
        Assert.Equal(MainWindowRole.Additional, second.Role);
    }
}
