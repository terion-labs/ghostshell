using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluentIcons.Common;
using GhostShell.App;
using GhostShell.Application;
using GhostShell.Application.ApplicationUpdates;
using GhostShell.Application.Previews;
using GhostShell.Core;
using GhostShell.Docker;
using GhostShell.Git;

namespace GhostShell.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable, IAgentWorkspaceHost, IPanelConnectionOptionsHost
{
    private enum McpServerTestPresentationState
    {
        Testing,
        Succeeded,
        Failed,
    }

    private sealed record McpServerTestPresentation(
        long Revision,
        McpServerTestPresentationState State,
        string Status,
        string Detail);

    private readonly IDefinitionCatalog _catalog;
    private DefinitionCatalogSnapshot _presentedCatalogSnapshot =
        DefinitionCatalogSnapshot.Empty;
    private readonly WorkspaceDefinitionOccupancy _workspaceDefinitionOccupancy;
    private readonly IConnectionRuntime _connectionRuntime;
    private readonly ISecretVault _secretVault;
    private readonly IFilePanelClient _filePanelClient;
    private readonly IFileTransferQueueClient _fileTransferQueue;

    /// <summary>
    /// What has been copied or cut, for the whole window: copying in one panel
    /// and pasting into another is the whole point of it, so it cannot belong
    /// to either of them.
    /// </summary>
    public FileTransferClipboard FileTransferClipboard { get; } = new();
    private readonly IBrowserRendererViewFactory? _browserRendererViewFactory;
    private readonly IBrowserProfilePreferences _browserProfilePreferences;
    private readonly CatalogBrowserProfileRuntime _browserProfileRuntime;
    private readonly IDatabasePanelClient? _databasePanelClient;
    private readonly IDatabaseConnectionCatalog? _databaseConnectionCatalog;
    private readonly IRedisPanelSessionFactory? _redisPanelSessionFactory;
    private readonly IDockerEngineClient? _dockerEngineClient;
    private readonly IGitRepositoryClient? _gitRepositoryClient;
    private readonly IGitRepositoryMutationCoordinator? _gitMutationCoordinator;
    private readonly IGitPanelPreferences? _gitPanelPreferences;
    private readonly ISqlLanguageService? _sqlLanguageService;
    private readonly IImagePreviewDecoder? _imagePreviewDecoder;
    private readonly IPdfPreviewRenderer? _pdfPreviewRenderer;
    private readonly IArchiveTableOfContents? _archiveTableOfContents;
    private readonly IInMemoryDatabaseRegistry? _inMemoryDatabaseRegistry;
    private readonly IFilePreviewPreferences _filePreviewPreferences;
    private readonly TerminalStartupCommandDispatcher _startupCommandDispatcher;
    private readonly IFileProviderProfileRuntime? _fileProviderRuntime;
    private readonly IAiProviderProfileRuntime? _aiProviderRuntime;
    private readonly IAgentWorkspaceRuntimeFactory? _agentRuntimeFactory;
    private readonly IAgentRunAuditReader? _agentRunAuditReader;
    private readonly IAgentModelFavoriteStore? _agentModelFavoriteStore;
    private readonly Dictionary<WorkspaceInstanceId, WorkspaceAgentChat>
        _workspaceAgentChats = [];
    private readonly IAiProviderAuthenticationRuntime? _aiProviderAuthenticationRuntime;
    private readonly IMcpServerDiagnostics? _mcpServerDiagnostics;
    private McpServerDiagnosticsSnapshot _mcpDiagnosticsSnapshot =
        new([], cleanupUncertain: false, cleanupUncertainAtUtc: null);
    private readonly IMcpCredentialSessionInvalidator?
        _mcpCredentialSessionInvalidator;
    private readonly IConnectionSecurityRuntime? _connectionSecurityRuntime;
    private readonly SessionRestoreCoordinator? _sessionRestoreCoordinator;
    private readonly TerminalMultiplexerCoordinator? _terminalMultiplexerCoordinator;
    private readonly IUiThreadDispatcher _uiThreadDispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly AppearancePreviewCoordinator _appearancePreview;
    private AppearancePreviewLease? _appearancePreviewLease;
    private string _appearancePreviewStatus =
        "Preview changes, then apply or cancel each section independently.";
    private readonly CancellationTokenSource _runtimeGraphLifetime = new();
    private readonly SemaphoreSlim _runtimeGraphGate;
    private readonly object _mcpServerTestGate = new();
    private readonly object _shutdownGate = new();
    private readonly Dictionary<PanelInstanceId, SessionId> _recentSessionIds = [];
    private readonly Dictionary<WorkspaceInstanceId, TerminalMultiplexingMode>
        _workspaceTerminalMultiplexingModes = [];
    private readonly Dictionary<
        McpServerProfileId,
        McpServerTestPresentation> _mcpServerTests = [];
    private Task? _shutdownTask;
    private RuntimeHistorySource? _runtimeHistorySource;
    private readonly ShellNavigationViewModel _navigation = new();
    private RuntimeWorkspaceViewModel? _runtimeWorkspace;
    private AgentChatViewModel? _agentChat;
    private LauncherScreenViewModel? _selectedAgentSavedScreenTemplate;
    private AgentSavedScreenLiveTarget? _agentSavedScreenLiveTarget;
    private bool _isPreparingAgentSavedScreenTarget;
    private bool _isChangingAgentScopeForSavedScreen;
    private string _agentSavedScreenTargetStatus =
        "A saved screen template grants no agent authority until its live instance is created, reviewed, and authorized.";
    private string? _operationError;
    private string _tabReorderStatus = string.Empty;
    private string _workspaceTransferStatus = string.Empty;
    private bool _isAgentPanelVisible;
    private bool _isAgentPanelDocked;
    private string _definitionBundleStatus =
        "Exports include saved settings but not credentials or terminal content.";
    private string? _applicationKeySequenceHint;
    private bool _restoreSessionsOnStart = true;
    private bool _sessionRestorePreferenceLoaded;
    private bool _sessionRestorePreferenceSaving;
    private volatile bool _shutdownStarted;
    private bool _presentationTeardownCompleted;
    private bool _disposed;

    public MainWindowViewModel(
        ISessionHostClient sessionClient,
        IDefinitionCatalog catalog,
        IConnectionRuntime connectionRuntime,
        ISecretVault secretVault,
        IFilePanelClient filePanelClient,
        IFileTransferQueueClient fileTransferQueue,
        TerminalStartupCommandDispatcher startupCommandDispatcher,
        IFileProviderProfileRuntime? fileProviderRuntime = null,
        IConnectionSecurityRuntime? connectionSecurityRuntime = null,
        RuntimeRecoveryWriter? runtimeRecoveryWriter = null,
        RecentSessionHistory? recentSessionHistory = null,
        TimeProvider? timeProvider = null,
        IUiThreadDispatcher? uiThreadDispatcher = null,
        OnboardingViewModel? onboarding = null,
        IProductComponentCatalog? productComponentCatalog = null,
        IAiProviderProfileRuntime? aiProviderRuntime = null,
        IAiProviderAuthenticationRuntime? aiProviderAuthenticationRuntime = null,
        IGovernedAgentRuntime? agentChatRuntime = null,
        IAgentApprovalPrincipal? agentApprovalPrincipal = null,
        IBrowserRendererViewFactory? browserRendererViewFactory = null,
        IDatabasePanelClient? databasePanelClient = null,
        IDatabaseConnectionCatalog? databaseConnectionCatalog = null,
        IRedisPanelSessionFactory? redisPanelSessionFactory = null,
        IImagePreviewDecoder? imagePreviewDecoder = null,
        IPdfPreviewRenderer? pdfPreviewRenderer = null,
        IArchiveTableOfContents? archiveTableOfContents = null,
        IInMemoryDatabaseRegistry? inMemoryDatabaseRegistry = null,
        IFilePreviewPreferences? filePreviewPreferences = null,
        IPreviewCacheControl? previewCacheControl = null,
        IApplicationEncryption? applicationEncryption = null,
        IStartupProtection? startupProtection = null,
        IBiometricAuthenticator? biometricAuthenticator = null,
        IAgentRunAuditReader? agentRunAuditReader = null,
        IMcpServerDiagnostics? mcpServerDiagnostics = null,
        IMcpCredentialSessionInvalidator?
            mcpCredentialSessionInvalidator = null,
        SessionRestoreCoordinator? sessionRestoreCoordinator = null,
        ISqlLanguageService? sqlLanguageService = null,
        IDockerEngineClient? dockerEngineClient = null,
        IGitRepositoryClient? gitRepositoryClient = null,
        IGitPanelPreferences? gitPanelPreferences = null,
        IGitRepositoryMutationCoordinator? gitMutationCoordinator = null,
        TerminalMultiplexerCoordinator? terminalMultiplexerCoordinator = null,
        IAgentModelFavoriteStore? agentModelFavoriteStore = null,
        AgentPolicyCoordinator? agentPolicyCoordinator = null,
        IAgentWorkspaceRuntimeFactory? agentRuntimeFactory = null,
        IBrowserProfilePreferences? browserProfilePreferences = null,
        IBrowserProfileDataControl? browserProfileDataControl = null,
        INativeNotificationService? nativeNotificationService = null,
        AppearancePreviewCoordinator? appearancePreview = null,
        IApplicationUpdateService? applicationUpdateService = null,
        MainWindowRole role = MainWindowRole.Primary,
        IWorkspaceIsolationProvider? workspaceIsolationProvider = null,
        WorkspaceDefinitionOccupancy? workspaceDefinitionOccupancy = null,
        IWorkspaceIsolationRuntimeInstaller? workspaceIsolationRuntimeInstaller = null,
        IWorkspaceRuntimeServicesFactory? workspaceRuntimeServicesFactory = null,
        IWorkspaceNetworkRuntime? workspaceNetworkRuntime = null)
    {
        SessionClient = sessionClient ?? throw new ArgumentNullException(nameof(sessionClient));
        _workspaceDefinitionOccupancy = workspaceDefinitionOccupancy
            ?? new WorkspaceDefinitionOccupancy();
        _uiThreadDispatcher = uiThreadDispatcher ?? AvaloniaUiThreadDispatcher.Instance;
        _workspaceNetworkRuntime = workspaceNetworkRuntime;
        _inactiveWorkspaceNetwork = new WorkspaceNetworkControlViewModel(
            new WorkspaceNetworkPolicyUpdate(NetworkPolicy.Direct, []),
            session: null,
            _uiThreadDispatcher);
        ApplicationUpdates = new ApplicationUpdateViewModel(
            applicationUpdateService
                ?? new PassiveApplicationUpdateService(
                    DistributionIdentity.Development),
            _uiThreadDispatcher);
        OpenWorkspaces = new(_openWorkspaces);
        Notifications = new ShellNotificationCenter(
            () => RuntimeWorkspace,
            () => IsWindowFocused,
            RefreshWorkspaceRuntimeFlags,
            _uiThreadDispatcher,
            nativeNotificationService,
            ActivateNativeNotification,
            () => IsWorkspaceCanvasVisible);
        _navigation.PropertyChanged += OnShellNavigationPropertyChanged;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _browserProfileRuntime = new CatalogBrowserProfileRuntime(_catalog);
        DefinitionEdit = new DefinitionEditSessionViewModel(_catalog);
        DefinitionEdit.PropertyChanged += OnDefinitionEditPropertyChanged;
        DefinitionSettings = new DefinitionSettingsViewModel(_catalog);
        DefinitionSettings.PropertyChanged += OnDefinitionSettingsPropertyChanged;
        TerminalSettings = new TerminalSettingsViewModel(_catalog);
        TerminalSettings.PropertyChanged += OnTerminalSettingsPropertyChanged;
        _appearancePreview = appearancePreview ?? new AppearancePreviewCoordinator();
        _appearancePreview.Changed += OnAppearancePreviewChanged;
        AppearanceSettings = new AppearanceSettingsViewModel(_catalog);
        AppearanceSettings.PropertyChanged += OnAppearanceSettingsPropertyChanged;
        AppearanceSettings.BackgroundSaveStarting += OnAppearanceBackgroundSaveStarting;
        AppearanceSettings.BackgroundSaveCompleted += OnAppearanceBackgroundSaveCompleted;
        _workspaceIsolationProvider = workspaceIsolationProvider;
        _workspaceIsolationRuntimeInstaller = workspaceIsolationRuntimeInstaller;
        _workspaceRuntimeServicesFactory = workspaceRuntimeServicesFactory;
        WorkspaceSettings = new WorkspaceSettingsViewModel(
            _catalog,
            () => _aiProviderRuntime?.Profiles ?? [],
            workspaceDefinitionOccupancy: _workspaceDefinitionOccupancy,
            isIsolationAvailable: _workspaceIsolationProvider is not null,
            isolationRuntimeDisplayName:
                _workspaceIsolationRuntimeInstaller?.RuntimeDisplayName,
            defaultIsolationImageReference:
                _workspaceIsolationProvider?.DefaultImageReference
                ?? WorkspaceIsolationImages.Default,
            activeIsolationImageReference: ActiveIsolationImageReference);
        WorkspaceSettings.PropertyChanged += OnWorkspaceSettingsPropertyChanged;
        NetworkSettings = new NetworkSettingsViewModel(
            _catalog,
            secretVault,
            workspaceNetworkRuntime);
        SavedScreenSettings = new SavedScreenSettingsViewModel(
            _catalog,
            () => _aiProviderRuntime?.Profiles ?? []);
        _connectionRuntime = connectionRuntime ?? throw new ArgumentNullException(nameof(connectionRuntime));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _filePanelClient = filePanelClient ?? throw new ArgumentNullException(nameof(filePanelClient));
        _fileTransferQueue = fileTransferQueue
            ?? throw new ArgumentNullException(nameof(fileTransferQueue));
        _fileProviderRuntime = fileProviderRuntime
            ?? filePanelClient as IFileProviderProfileRuntime;
        _aiProviderRuntime = aiProviderRuntime;
        _agentRuntimeFactory = agentRuntimeFactory;
        _agentRunAuditReader = agentRunAuditReader;
        _agentModelFavoriteStore = agentModelFavoriteStore;
        _aiProviderAuthenticationRuntime = aiProviderAuthenticationRuntime;
        _mcpServerDiagnostics = mcpServerDiagnostics;
        if (_mcpServerDiagnostics is not null)
        {
            _mcpDiagnosticsSnapshot = _mcpServerDiagnostics.Snapshot;
            _mcpServerDiagnostics.Changed += OnMcpServerDiagnosticsChanged;
        }

        _mcpCredentialSessionInvalidator = mcpCredentialSessionInvalidator;
        SecretSettings = new SecretSettingsViewModel(
            _catalog,
            _secretVault,
            _fileProviderRuntime,
            _aiProviderRuntime,
            _mcpCredentialSessionInvalidator,
            InvalidateMcpServerTests,
            ClearError,
            SetError);
        SecretSettings.PropertyChanged += OnSecretSettingsPropertyChanged;
        SecretSettings.ProjectionChanged += OnSecretProjectionChanged;
        FileTransferState = new FileTransferViewModel(
            _fileTransferQueue,
            ResolveFileTransferQueue,
            RefreshPanelsAfterTransferAsync,
            SetError,
            _uiThreadDispatcher);
        FileTransferState.PropertyChanged += OnFileTransferStatePropertyChanged;
        _browserRendererViewFactory = browserRendererViewFactory;
        _browserProfilePreferences = browserProfilePreferences
            ?? new InMemoryBrowserProfilePreferences();
        _databasePanelClient = databasePanelClient;
        _databaseConnectionCatalog = databaseConnectionCatalog ?? databasePanelClient;
        DatabaseConnectionSettings = new DatabaseConnectionSettingsCoordinator(
            _catalog,
            _databaseConnectionCatalog,
            _secretVault,
            SetError,
            SecretSettings.ReportStatus);
        _redisPanelSessionFactory = redisPanelSessionFactory;
        _dockerEngineClient = dockerEngineClient;
        _gitRepositoryClient = gitRepositoryClient;
        _hostWorkspaceRuntimeServices = new WorkspaceRuntimeServices(
            new WorkspaceRuntimeBackends(
                _dockerEngineClient,
                _gitRepositoryClient,
                _filePanelClient,
                _fileTransferQueue,
                _databasePanelClient,
                _redisPanelSessionFactory,
                _browserRendererViewFactory),
            WorkspaceNetworkRoute.Direct);
        _workspaceRuntimeLeases = new WorkspaceRuntimeLeaseCoordinator(
            _connectionRuntime,
            _hostWorkspaceRuntimeServices,
            _workspaceIsolationProvider,
            _workspaceRuntimeServicesFactory);
        _gitMutationCoordinator = gitMutationCoordinator;
        _gitPanelPreferences = gitPanelPreferences;
        _sqlLanguageService = sqlLanguageService;
        _imagePreviewDecoder = imagePreviewDecoder;
        _pdfPreviewRenderer = pdfPreviewRenderer;
        _archiveTableOfContents = archiveTableOfContents;
        _inMemoryDatabaseRegistry = inMemoryDatabaseRegistry;
        _filePreviewPreferences = filePreviewPreferences ?? new InMemoryFilePreviewPreferences();
        FilePreviewSettingsEditor = new FilePreviewSettingsEditorViewModel(
            _filePreviewPreferences,
            previewCacheControl);
        BrowserProfileSettingsEditor = new BrowserProfileSettingsEditorViewModel(
            _browserProfilePreferences,
            browserProfileDataControl,
            _catalog,
            _secretVault);
        RefreshBrowserPanelProfileOptions(_catalog.Snapshot);
        ApplicationSecurityEditor = new ApplicationSecurityEditorViewModel(
            applicationEncryption,
            startupProtection,
            biometricAuthenticator);
        _startupCommandDispatcher = startupCommandDispatcher
            ?? throw new ArgumentNullException(nameof(startupCommandDispatcher));
        FileProviderSettings = new FileProviderSettingsViewModel(
            _catalog,
            _fileProviderRuntime,
            () => _filePanelClient.Profiles,
            () => [.. SecretSettings.Secrets],
            _uiThreadDispatcher);
        FileProviderSettings.PropertyChanged += OnFileProviderSettingsPropertyChanged;
        AiProviderSettings = new AiProviderSettingsViewModel(
            _catalog,
            _aiProviderRuntime,
            _aiProviderAuthenticationRuntime,
            () => [.. SecretSettings.Secrets],
            _uiThreadDispatcher);
        AiProviderSettings.PropertyChanged += OnAiProviderSettingsPropertyChanged;
        AiProviderSettings.RuntimeProfilesChanged += OnAiProviderRuntimeProfilesChanged;
        McpServerSettings = new McpServerSettingsViewModel(
            _catalog,
            () => [.. SecretSettings.Secrets]);
        _connectionSecurityRuntime = connectionSecurityRuntime;
        TerminalConnectionSettings = new TerminalConnectionSettingsViewModel(
            _catalog,
            _connectionRuntime,
            _connectionSecurityRuntime,
            _gitRepositoryClient);
        _sessionRestoreCoordinator = sessionRestoreCoordinator;
        _terminalMultiplexerCoordinator = terminalMultiplexerCoordinator;
        TerminalContinuity = new TerminalContinuitySettingsViewModel(
            _terminalMultiplexerCoordinator,
            FindConnection,
            SetError,
            _uiThreadDispatcher);
        TerminalContinuity.PropertyChanged +=
            OnTerminalContinuityPropertyChanged;
        _timeProvider = timeProvider ?? TimeProvider.System;
        History = new RecentSessionHistoryViewModel(
            recentSessionHistory,
            _timeProvider,
            ToRecentSessionItem);
        History.PropertyChanged += OnHistoryPropertyChanged;
        History.SnapshotChanged += OnHistorySnapshotChanged;
        Launcher = new LauncherViewModel(BuildLauncherSearchCandidates);
        Launcher.PropertyChanged += OnLauncherPropertyChanged;
        ClientId = agentApprovalPrincipal is null
            ? ClientId.New()
            : RequireDesktopClientId(agentApprovalPrincipal);
        WindowId = WindowInstanceId.New();
        Role = role;
        WorkspaceAutoSave = new WorkspaceAutoSaveCoordinator(
            _catalog,
            () => RuntimeWorkspace,
            () => _runtimeHistorySource,
            () => _shutdownStarted,
            _timeProvider);
        RuntimeRecovery = new RuntimeWorkspaceRecoveryCoordinator(
            runtimeRecoveryWriter,
            () => RuntimeWorkspace,
            () => _runtimeHistorySource,
            () => _shutdownStarted,
            WorkspaceAutoSave.Queue,
            SetError,
            _uiThreadDispatcher);
        RuntimeGraph = new RuntimeWorkspaceGraphCoordinator(
            SessionClient,
            ClientId,
            WindowId,
            _uiThreadDispatcher,
            _timeProvider,
            () => RuntimeWorkspace,
            runtime =>
            {
                CloseRuntimeWorkspace(runtime);
                CloseOverlay();
                if (RuntimeWorkspace is null)
                {
                    Route = ShellRoute.Workspace;
                }
            },
            SetError,
            () =>
            {
                MarkVisibleNotificationsSeen();
                QueueRuntimeRecoverySnapshot();
            },
            Notifications.Watch);
        _runtimeGraphGate = RuntimeGraph.SerializationGate;
        AgentChat = _agentRuntimeFactory is null
            && agentChatRuntime is not null
            && _aiProviderRuntime is not null
            ? new AgentChatViewModel(
                agentChatRuntime,
                _aiProviderRuntime,
                _uiThreadDispatcher,
                agentRunAuditReader,
                agentModelFavoriteStore)
            : null;
        AgentWorkspaceScope = new AgentWorkspaceScopeViewModel(
            WindowId,
            () => AgentChat is not { CanChangeProvider: false },
            () => AgentChat is { CanChangeProvider: true },
            TrackRecentSession);
        AgentWorkspaceScope.PropertyChanged += OnAgentWorkspaceScopePropertyChanged;
        DefaultAgentPolicySettings = new DefaultAgentPolicySettingsViewModel(
            agentPolicyCoordinator,
            _aiProviderRuntime?.Profiles,
            _uiThreadDispatcher,
            SetError,
            ClearError);
        DefaultAgentPolicySettings.PropertyChanged +=
            OnDefaultAgentPolicySettingsPropertyChanged;
        DefaultAgentPolicySettings.PolicyChanged +=
            OnDefaultAgentPolicyChanged;
        Onboarding = role == MainWindowRole.Primary ? onboarding : null;
        ProductComponents = productComponentCatalog?.Components ?? [];
        _catalog.Changed += OnCatalogChanged;
        RefreshCatalog(_catalog.Snapshot);
        if (role == MainWindowRole.Primary)
        {
            Onboarding?.Start();
            History.StartLoading();

            // The editor displays a complete default as soon as an enabled
            // provider exists. Persist that exact visible configuration so the
            // Agent surface and Settings cannot disagree about whether AI is set
            // up. Subsequent edits are persisted by OnDefaultAgentPolicyChanged.
            DefaultAgentPolicySettings.QueuePersistence(onlyWhenMissing: true);
        }
    }

    public ISessionHostClient SessionClient { get; }

    public RecentSessionHistoryViewModel History { get; }

    public LauncherViewModel Launcher { get; }

    public DefinitionEditSessionViewModel DefinitionEdit { get; }

    public DefinitionSettingsViewModel DefinitionSettings { get; }

    public TerminalSettingsViewModel TerminalSettings { get; }

    public AppearanceSettingsViewModel AppearanceSettings { get; }

    public WorkspaceSettingsViewModel WorkspaceSettings { get; }

    public NetworkSettingsViewModel NetworkSettings { get; }

    public SavedScreenSettingsViewModel SavedScreenSettings { get; }

    public TerminalConnectionSettingsViewModel TerminalConnectionSettings { get; }

    public McpServerSettingsViewModel McpServerSettings { get; }

    public DatabaseConnectionSettingsCoordinator DatabaseConnectionSettings { get; }

    public SecretSettingsViewModel SecretSettings { get; }

    public FileTransferViewModel FileTransferState { get; }

    public TerminalContinuitySettingsViewModel TerminalContinuity { get; }

    public MainWindowRole Role { get; }

    public RuntimeWorkspaceGraphCoordinator RuntimeGraph { get; }

    public RuntimeWorkspaceRecoveryCoordinator RuntimeRecovery { get; }

    public WorkspaceAutoSaveCoordinator WorkspaceAutoSave { get; }

    /// <summary>
    /// The preview settings the Files &amp; transfers page edits. Always
    /// present: without stored preferences it edits in-memory ones, so the
    /// page behaves the same everywhere it renders.
    /// </summary>
    public FilePreviewSettingsEditorViewModel FilePreviewSettingsEditor { get; }

    public BrowserProfileSettingsEditorViewModel BrowserProfileSettingsEditor { get; }

    /// <summary>
    /// The application-security controls on the Security &amp; secrets page.
    /// Always present; without an encryption service it reports itself
    /// unavailable rather than not rendering.
    /// </summary>
    public ApplicationSecurityEditorViewModel ApplicationSecurityEditor { get; }

    public ObservableCollection<ManagedRemoteSessionViewModel> ManagedRemoteSessions =>
        TerminalContinuity.ManagedSessions;

    public OnboardingViewModel? Onboarding { get; }

    public AgentChatViewModel? AgentChat
    {
        get => _agentChat;
        private set
        {
            if (SetProperty(ref _agentChat, value))
            {
                OnPropertyChanged(nameof(HasAgentSavedScreenTemplates));
                OnPropertyChanged(nameof(CanCreateAgentSavedScreenTarget));
                OnPropertyChanged(nameof(CanAuthorizeAgentSavedScreenTarget));
            }
        }
    }

    public DefaultAgentPolicySettingsViewModel DefaultAgentPolicySettings { get; }

    public SavedScreenAgentPolicyEditorViewModel DefaultAgentPolicy =>
        DefaultAgentPolicySettings.Editor;

    public bool CanSaveDefaultAgentPolicy =>
        DefaultAgentPolicySettings.CanSave;

    public async Task SaveDefaultAgentPolicyAsync(CancellationToken cancellationToken)
    {
        await DefaultAgentPolicySettings.SaveAsync(cancellationToken);
    }

    public SavedScreenDeleteUndoViewModel SavedScreenDeleteUndo =>
        SavedScreenSettings.DeleteUndo;

    public ClientId ClientId { get; }

    public WindowInstanceId WindowId { get; }

    public IReadOnlyList<AgentRunScopeOption> AgentRunScopeOptions =>
        AgentWorkspaceScope.ScopeOptions;

    public AgentWorkspaceScopeViewModel AgentWorkspaceScope { get; }

    public AgentRunScopeOption SelectedAgentRunScope
    {
        get => AgentWorkspaceScope.SelectedScope;
        set => AgentWorkspaceScope.SelectedScope = value;
    }

    public ObservableCollection<AgentTerminalSelectionItemViewModel> AgentTerminalSelectionOptions
        => AgentWorkspaceScope.TerminalOptions;

    public bool IsAgentSelectedPanelsScope =>
        AgentWorkspaceScope.IsSelectedPanelsScope;

    public bool HasAgentTerminalSelectionOptions =>
        AgentWorkspaceScope.HasTerminalOptions;

    public int AgentSelectedTerminalCount =>
        AgentWorkspaceScope.SelectedTerminalCount;

    public string AgentTerminalSelectionSummary =>
        AgentWorkspaceScope.SelectionSummary;

    public string AgentTerminalSelectionStatus => AgentWorkspaceScope.SelectionStatus;

    public bool HasAgentTerminalSelectionError => AgentWorkspaceScope.HasSelectionError;

    public ObservableCollection<LauncherWorkspaceViewModel> Workspaces => Launcher.Workspaces;

    public ObservableCollection<LauncherConnectionViewModel> Connections => Launcher.Connections;

    /// <summary>
    /// Saved file-transfer providers, presented as connection cards so the
    /// launcher manages every connection family in one place.
    /// </summary>
    public ObservableCollection<LauncherConnectionViewModel> FileConnections =>
        Launcher.FileConnections;

    /// <summary>Saved database connections, presented as connection cards.</summary>
    public ObservableCollection<LauncherConnectionViewModel> DatabaseConnections =>
        Launcher.DatabaseConnections;

    public IReadOnlyList<SavedConnectionShortcutViewModel> SavedConnectionShortcuts =>
        BuildSavedConnectionShortcuts();

    /// <summary>
    /// Counted here rather than as <c>SavedConnectionShortcuts.Count</c> in the
    /// view: the list is an array behind an interface, whose runtime type has
    /// no public Count for a binding to reflect over, so the pill would render
    /// empty and say nothing about it.
    /// </summary>
    public int SavedConnectionShortcutCount => SavedConnectionShortcuts.Count;

    public IEnumerable<PanelConnectionOptionViewModel> PanelConnectionOptions =>
        Connections.Select(connection => new PanelConnectionOptionViewModel(
            new PanelConnectionOptionViewModel.Target.Connection(connection.Id),
            PanelConnectionDisplayName(connection),
            connection.Kind,
            connection.Detail,
            connection.CanOpen));

    public IEnumerable<PanelConnectionOptionViewModel> BrowserConnectionOptions =>
        Connections
            .Where(connection => FindConnection(connection.Id)?.Endpoint is
                ConnectionEndpoint.Local or ConnectionEndpoint.Ssh)
            .Select(connection => new PanelConnectionOptionViewModel(
                new PanelConnectionOptionViewModel.Target.Connection(connection.Id),
                PanelConnectionDisplayName(connection),
                connection.Kind,
                connection.Detail,
                CanOpen: true));

    public IReadOnlyList<PanelConnectionOptionViewModel> FileConnectionOptions =>
        BuildFileConnectionOptions();

    /// <summary>
    /// What the database panel's connection pill offers: saved database
    /// connections — not tunnels; a profile carries its own route.
    /// </summary>
    public IEnumerable<PanelConnectionOptionViewModel> DatabasePanelConnectionOptions =>
        _catalog.Snapshot.DatabaseConnections.Select(item =>
        {
            var profile = item.Value;
            var driver = _databaseConnectionCatalog?.Drivers
                .FirstOrDefault(descriptor => string.Equals(descriptor.Id, profile.DriverId, StringComparison.Ordinal));
            return new PanelConnectionOptionViewModel(
                new PanelConnectionOptionViewModel.Target.Database(profile.Id),
                profile.Name,
                driver?.DisplayName ?? profile.DriverId,
                DatabaseConnectionDetailText(profile),
                CanOpen: _databaseConnectionCatalog is not null);
        });

    public ObservableCollection<LauncherScreenViewModel> Screens => Launcher.Screens;

    public IReadOnlyList<LauncherScreenViewModel> AgentSavedScreenTemplates => Screens;

    public bool HasAgentSavedScreenTemplates =>
        AgentChat is not null && Screens.Count > 0;

    public LauncherScreenViewModel? SelectedAgentSavedScreenTemplate
    {
        get => _selectedAgentSavedScreenTemplate;
        set
        {
            if (ReferenceEquals(_selectedAgentSavedScreenTemplate, value))
            {
                return;
            }

            if (value is not null && !Screens.Contains(value))
            {
                throw new ArgumentException(
                    "The selected saved-screen template is not in the current catalog.",
                    nameof(value));
            }

            if (AgentChat is { CanChangeProvider: false })
            {
                return;
            }

            _selectedAgentSavedScreenTemplate = value;
            _agentSavedScreenLiveTarget = null;
            AgentSavedScreenTargetStatus = value is null
                ? "A saved screen template grants no agent authority until its live instance is created, reviewed, and authorized."
                : $"Template selected: {value.Name} · revision {value.Revision}. No live target or agent authority exists yet.";
            NotifyAgentSavedScreenTargetChanged();
        }
    }

    public AgentSavedScreenLiveTarget? AgentSavedScreenLiveTarget =>
        _agentSavedScreenLiveTarget;

    public bool HasPendingAgentSavedScreenTarget =>
        _agentSavedScreenLiveTarget is { IsAuthorized: false };

    public bool HasAuthorizedAgentSavedScreenTarget =>
        _agentSavedScreenLiveTarget is { IsAuthorized: true };

    public bool IsPreparingAgentSavedScreenTarget
    {
        get => _isPreparingAgentSavedScreenTarget;
        private set
        {
            if (SetProperty(ref _isPreparingAgentSavedScreenTarget, value))
            {
                OnPropertyChanged(nameof(CanCreateAgentSavedScreenTarget));
                OnPropertyChanged(nameof(CanAuthorizeAgentSavedScreenTarget));
            }
        }
    }

    public bool CanCreateAgentSavedScreenTarget =>
        SelectedAgentSavedScreenTemplate is not null
        && AgentChat is { CanChangeProvider: true }
        && !IsPreparingAgentSavedScreenTarget;

    public bool CanAuthorizeAgentSavedScreenTarget =>
        HasPendingAgentSavedScreenTarget
        && AgentChat is { CanChangeProvider: true }
        && !IsPreparingAgentSavedScreenTarget;

    public string AgentSavedScreenTargetStatus
    {
        get => _agentSavedScreenTargetStatus;
        private set => SetProperty(ref _agentSavedScreenTargetStatus, value);
    }

    public ObservableCollection<LayoutCardViewModel> Layouts => DefinitionSettings.Layouts;

    public ObservableCollection<KeybindingRowViewModel> Keybindings => DefinitionSettings.Keybindings;

    public ObservableCollection<KeybindingProfileItemViewModel> KeybindingProfiles =>
        DefinitionSettings.KeybindingProfiles;

    public ObservableCollection<LauncherSearchResultViewModel> LauncherSearchResults =>
        Launcher.SearchResults;

    public ObservableCollection<SecretMetadataViewModel> Secrets =>
        SecretSettings.Secrets;

    public bool HasNoSecrets => SecretSettings.HasNoSecrets;

    /// <summary>
    /// The browser-view factory, for surfaces that host a page of their own — the
    /// file preview of an HTML document uses the same engine the browser panel
    /// does rather than a second one.
    /// </summary>
    public IBrowserRendererViewFactory? BrowserRendererViewFactory =>
        _browserRendererViewFactory;

    public ObservableCollection<FileTransferItemViewModel> FileTransfers =>
        FileTransferState.Transfers;

    public ObservableCollection<FileProviderProfileItemViewModel> FileProviderDefinitions =>
        FileProviderSettings.Definitions;

    public FileProviderSettingsViewModel FileProviderSettings { get; }

    public ObservableCollection<AiProviderProfileItemViewModel> AiProviderDefinitions =>
        AiProviderSettings.Definitions;

    public AiProviderSettingsViewModel AiProviderSettings { get; }

    public ObservableCollection<McpServerProfileItemViewModel> McpServerDefinitions { get; } = [];

    public ObservableCollection<McpServerSecretTargetViewModel>
        McpServerSecretTargets
    { get; } = [];

    public ObservableCollection<RecentSessionHistoryItemViewModel> RecentSessions =>
        History.RecentSessions;

    public ObservableCollection<RecentSessionHistoryItemViewModel> HistorySessions =>
        History.Sessions;

    public ObservableCollection<RecentSessionHistoryItemViewModel> FilteredHistorySessions =>
        History.FilteredSessions;

    public IReadOnlyList<HistoryExportScope> HistoryExportScopes => History.ExportScopes;

    public ObservableCollection<HistoryRetentionOption> HistoryRetentionOptions =>
        History.RetentionOptions;

    public LayoutDesignerViewModel? LayoutDesignerEditor
    {
        get => DefinitionSettings.LayoutDesignerEditor;
    }

    public WorkspaceEditorViewModel? WorkspaceEditor
    {
        get => WorkspaceSettings.Editor;
    }

    public bool HasWorkspaceEditor => WorkspaceSettings.HasEditor;

    public KeybindingProfileItemViewModel? SelectedKeybindingProfile
    {
        get => DefinitionSettings.SelectedKeybindingProfile;
    }

    public KeybindingEditorSessionViewModel? KeybindingEditorSession
    {
        get => DefinitionSettings.KeybindingEditorSession;
    }

    public bool HasKeybindingEditor => DefinitionSettings.HasKeybindingEditor;

    public bool CanCloneSelectedKeybindingProfile =>
        DefinitionSettings.CanCloneSelectedKeybindingProfile;

    public string ProductName => ProductIdentity.DisplayName;

    public string ApplicationIdentifier => ProductIdentity.BundleIdentifier;

    public string ExecutableName => ProductIdentity.ExecutableName;

    public string ApplicationVersion =>
        $"v{typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public string RuntimeDescription => RuntimeInformation.FrameworkDescription;

    public string PlatformDescription =>
        $"{PlatformName()} · {RuntimeInformation.ProcessArchitecture}";

    public string AgentRuntimeDescription => AgentChat is null
        ? "Unavailable · provider runtime not composed"
        : AgentChat.RendererModeDescription;

    public ApplicationUpdateViewModel ApplicationUpdates { get; }

    public IReadOnlyList<ProductComponentViewModel> ProductComponents { get; }

    /// <summary>
    /// A build composed without a component catalog would otherwise leave the
    /// About page promising an inventory and then showing nothing at all.
    /// </summary>
    public bool HasNoProductComponents => ProductComponents.Count == 0;

    public bool HasWorkspaces => Launcher.HasWorkspaces;

    public bool HasNoWorkspaces => Launcher.HasNoWorkspaces;

    /// <summary>
    /// Home is a summary, so it shows a bounded preview and sends the rest to the
    /// dedicated page. Without the cap a profile with a hundred connections would
    /// push every other section off the page.
    /// </summary>
    public ObservableCollection<LauncherConnectionViewModel> ConnectionsPreview =>
        Launcher.ConnectionsPreview;

    public ObservableCollection<LauncherScreenViewModel> ScreensPreview =>
        Launcher.ScreensPreview;

    public bool HasMoreConnectionsThanPreview => Launcher.HasMoreConnectionsThanPreview;

    public bool HasMoreScreensThanPreview => Launcher.HasMoreScreensThanPreview;

    public bool HasConnections => Launcher.HasConnections;

    public bool HasNoConnections => Launcher.HasNoConnections;

    public bool HasTerminalConnections => Launcher.HasTerminalConnections;

    public bool HasFileConnections => Launcher.HasFileConnections;

    public bool HasDatabaseConnections => Launcher.HasDatabaseConnections;

    public int TotalConnectionCount => Launcher.TotalConnectionCount;

    public bool HasScreens => Launcher.HasScreens;

    public bool HasNoScreens => Launcher.HasNoScreens;

    public bool HasRecentSessions => History.HasRecentSessions;

    public bool HasNoRecentSessions => History.HasNoRecentSessions;

    public bool HasHistorySessions => History.HasSessions;

    public bool HasNoHistorySessions => History.HasNoSessions;

    public bool HasFilteredHistorySessions => History.HasFilteredSessions;

    public bool HasNoFilteredHistorySessions => History.HasNoFilteredSessions;

    public bool HasRecentSessionFailure => History.HasFailure;

    public bool CanResetRecentSessionHistory => History.CanReset;

    public bool HasUnreadableRecentSessionHistory => History.HasUnreadableHistory;

    public bool IsHistoryLoading => History.IsLoading;

    public bool IsHistoryMutating => History.IsMutating;

    public bool IsHistoryExporting => History.IsExporting;

    public bool CanRetryRecentSessionHistory => History.CanRetry;

    public bool CanClearRecentSessionHistory => History.CanClear;

    public bool CanExportAllHistory => History.CanExportAll;

    public bool CanExportFilteredHistory => History.CanExportFiltered;

    public string HistoryResultCount => History.ResultCount;

    public string HistorySearchEmptyState => History.SearchEmptyState;

    public bool HasLauncherSearchResults => Launcher.HasSearchResults;

    public bool HasNoLauncherSearchResults => Launcher.HasNoSearchResults;

    public string LauncherSearchEmptyState => Launcher.SearchEmptyState;

    public string RecentSessionStatus => History.RecentSessionStatus;

    public string HistoryExportStatus => History.ExportStatus;

    public string HistoryRetentionStatus => History.RetentionStatus;

    public bool CanManageHistoryRetention => History.CanManageRetention;

    public HistoryRetentionOption? SelectedHistoryRetentionOption
    {
        get => History.SelectedRetentionOption;
        set => History.SelectedRetentionOption = value;
    }

    public bool HasPendingHistoryRetentionChange
    {
        get => History.HasPendingRetentionChange;
    }

    public bool CanApplyHistoryRetention =>
        History.CanApplyRetention;

    public bool RequiresHistoryRetentionConfirmation =>
        History.RequiresRetentionConfirmation;

    public string DefinitionBundleStatus
    {
        get => _definitionBundleStatus;
        private set => SetProperty(ref _definitionBundleStatus, value);
    }

    public IReadOnlyList<FileProviderProfileDescriptor> FileProviderProfiles =>
        FileProviderSettings.Profiles;

    public IReadOnlyList<AiProviderProfileDescriptor> AiProviderProfiles =>
        AiProviderSettings.Profiles;

    public bool HasAiProviders => AiProviderSettings.HasProviders;

    public bool HasNoAiProviders => AiProviderSettings.HasNoProviders;

    public bool HasMcpServers => McpServerDefinitions.Count > 0;

    public bool HasNoMcpServers => !HasMcpServers;

    public bool HasMcpDiagnosticHistory =>
        _mcpDiagnosticsSnapshot.Summaries.Count > 0;

    public bool IsMcpCleanupUncertain =>
        _mcpDiagnosticsSnapshot.CleanupUncertain;

    public bool HasMcpServerSecretTargets => McpServerSecretTargets.Count > 0;

    public bool HasFileTransfers => FileTransferState.HasTransfers;

    public bool HasNoFileTransfers => FileTransferState.HasNoTransfers;

    public int ActiveFileTransferCount =>
        FileTransferState.ActiveCount;

    public int FailedFileTransferCount =>
        FileTransferState.FailedCount;

    public string FileTransferStatusText => FileTransferState.StatusText;

    public IReadOnlyList<SecretKind> SecretKinds => SecretSettings.Kinds;

    public ShellRoute Route
    {
        get => _navigation.Route;
        private set
        {
            var previous = _navigation.Route;
            _navigation.ShowRoute(value);
            if (previous != value && value == ShellRoute.Workspace)
            {
                MarkVisibleNotificationsSeen();
            }
        }
    }

    /// <summary>
    /// The independently testable owner of shell route and overlay state.
    /// Existing root properties remain as migration forwarders until views
    /// bind directly to the composed surface.
    /// </summary>
    public ShellNavigationViewModel Navigation => _navigation;

    public SettingsPage SettingsPage
    {
        get => _navigation.SettingsPage;
        set => _navigation.SettingsPage = value;
    }

    public ShellOverlay Overlay
    {
        get => _navigation.Overlay;
        private set
        {
            var previous = _navigation.Overlay;
            _navigation.ShowOverlay(value);
            if (previous != value && value == ShellOverlay.None)
            {
                MarkVisibleNotificationsSeen();
            }
        }
    }

    /// <summary>
    /// Every workspace that is open, not only the one on screen.
    ///
    /// Switching between them used to dispose the one being left: its sessions
    /// were killed and its tabs rebuilt from the definition on the way back,
    /// which is not what changing view means. A workspace now lives until it is
    /// closed, and <see cref="RuntimeWorkspace"/> names which of them is in
    /// front.
    /// </summary>
    public ReadOnlyObservableCollection<RuntimeWorkspaceViewModel> OpenWorkspaces { get; }

    /// <summary>
    /// Whether the shell has the user's attention. The window tells it; a
    /// notification arriving while the app is in the background always leaves a
    /// mark, because nobody was looking at anything.
    /// </summary>
    public bool IsWindowFocused
    {
        get => _isWindowFocused;
        set
        {
            if (SetProperty(ref _isWindowFocused, value) && value)
            {
                MarkVisibleNotificationsSeen();
            }
        }
    }

    private bool _isWindowFocused = true;

    internal ShellNotificationCenter Notifications { get; }

    private readonly ObservableCollection<RuntimeWorkspaceViewModel> _openWorkspaces = [];

    /// <summary>
    /// What each open workspace was opened from. The active one's is
    /// <see cref="_runtimeHistorySource"/>; this keeps the rest so a workspace
    /// can be found again by its definition instead of being opened twice.
    /// </summary>
    private readonly Dictionary<WorkspaceInstanceId, RuntimeHistorySource> _runtimeSources = [];

    public RuntimeWorkspaceViewModel? RuntimeWorkspace
    {
        get => _runtimeWorkspace;
        private set
        {
            var previous = _runtimeWorkspace;
            if (SetProperty(ref _runtimeWorkspace, value))
            {
                // Marked first because the notification above is not free: the
                // dock control, three tab strips and the status bar all re-read
                // the workspace from it, and Dock rebuilds its layout while
                // this setter is still running. Without a mark here that cost
                // would be charged to whatever came next.
                ShowCanvasOf(value, insteadOf: previous);
                _activation?.Mark("bindings");
                StopRuntimeGraphWatch();
                _activation?.Mark("graph stop");
                // One announcement, from the one place that knows what is in
                // front. Scattered across the open paths it was missing from
                // every other way of arriving — restore among them.
                SetActiveWorkspaceAccent(ShellAccentOf(value));
                _activation?.Mark("accent");

                AgentWorkspaceScope.StopTracking(previous);
                StopTrackingRecovery(previous);

                // Only a workspace that has actually gone is torn down. One that
                // is merely no longer in front keeps its sessions, its panels,
                // and its place in the open set.
                if (previous is not null && !_openWorkspaces.Contains(previous))
                {
                    Notifications.Forget(previous);
                    QueueRemainingRecentSessionCompletions(RecentSessionOutcome.GracefullyClosed);
                    previous.DisposePanels();
                }

                _runtimeHistorySource = value is null
                    ? null
                    : _runtimeSources.GetValueOrDefault(value.Id);
                ActivateWorkspaceAgentChat(value?.Id);
                SyncAgentPanelPlacement(value);
                StartTrackingRecovery(value);
                AgentWorkspaceScope.AttachWorkspace(value);
                if (!IsPreparingAgentSavedScreenTarget
                    && _selectedAgentSavedScreenTemplate is not null)
                {
                    ClearAgentSavedScreenTarget();
                }
                _activation?.Mark("tracking");
                _activation?.Mark("agent terminals");
                OnPropertyChanged(nameof(HasRuntimeWorkspace));
                OnPropertyChanged(nameof(NewItemLauncherTitle));
                OnPropertyChanged(nameof(CanCreateBrowserPanel));
                OnPropertyChanged(nameof(PanelConnectionOptions));
                OnPropertyChanged(nameof(BrowserConnectionOptions));
                OnPropertyChanged(nameof(WorkspaceNetwork));
                OnPropertyChanged(nameof(IsWorkspaceNetworkControlVisible));
                _activation?.Mark("notifications");
                Launcher.RefreshSearchResults();
                _activation?.Mark("search results");
            }
        }
    }

    public bool HasRuntimeWorkspace => RuntimeWorkspace is not null;

    internal void MarkVisibleNotificationsSeen()
    {
        Notifications.MarkVisibleSeen();
        if (IsAgentPanelVisible && IsWorkspaceCanvasVisible)
        {
            Notifications.MarkWorkspaceSourceSeen(RuntimeWorkspace);
        }
    }

    private void OnShellNavigationPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        OnPropertyChanged(eventArgs.PropertyName);
        if (IsSettingsVisible && SettingsPage == SettingsPage.Appearance)
        {
            _ = BeginAppearanceEditing();
        }
        else
        {
            ReleaseAppearanceEditing();
        }
    }

    private void OnDefinitionEditPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        var propertyName = eventArgs.PropertyName switch
        {
            nameof(DefinitionEditSessionViewModel.EditorTitle) => nameof(EditorTitle),
            nameof(DefinitionEditSessionViewModel.EditorName) => nameof(EditorName),
            nameof(DefinitionEditSessionViewModel.EditorDescription) => nameof(EditorDescription),
            _ => null,
        };
        if (propertyName is not null)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnDefinitionSettingsPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        string[] propertyNames = eventArgs.PropertyName switch
        {
            nameof(DefinitionSettingsViewModel.LayoutDesignerEditor) =>
                [nameof(LayoutDesignerEditor)],
            nameof(DefinitionSettingsViewModel.SelectedKeybindingProfile) =>
                [nameof(SelectedKeybindingProfile)],
            nameof(DefinitionSettingsViewModel.KeybindingEditorSession) =>
                [nameof(KeybindingEditorSession)],
            nameof(DefinitionSettingsViewModel.HasKeybindingEditor) =>
                [nameof(HasKeybindingEditor)],
            nameof(DefinitionSettingsViewModel.CanCloneSelectedKeybindingProfile) =>
                [nameof(CanCloneSelectedKeybindingProfile)],
            nameof(DefinitionSettingsViewModel.KeybindingConflictCount) =>
                [nameof(KeybindingConflictCount)],
            nameof(DefinitionSettingsViewModel.ActiveApplicationKeymap) =>
                [nameof(ActiveApplicationKeymap)],
            nameof(DefinitionSettingsViewModel.ActiveApplicationKeymapRevision) =>
                [nameof(ActiveApplicationKeymapRevision)],
            nameof(DefinitionSettingsViewModel.ActiveApplicationKeymapName) =>
                [nameof(ActiveApplicationKeymapName)],
            _ => [],
        };
        foreach (var propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnTerminalSettingsPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        var propertyName = eventArgs.PropertyName switch
        {
            nameof(TerminalSettingsViewModel.TerminalEditor) =>
                nameof(TerminalSettingsEditor),
            nameof(TerminalSettingsViewModel.QuickTerminalEditor) =>
                nameof(QuickTerminalSettingsEditor),
            nameof(TerminalSettingsViewModel.ActiveTerminalProfile) =>
                nameof(ActiveTerminalProfile),
            _ => null,
        };
        if (propertyName is not null)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnAppearanceSettingsPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName is not null)
        {
            OnPropertyChanged(eventArgs.PropertyName);
        }
    }

    private void OnAppearanceBackgroundSaveStarting(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ClearError();
    }

    private void OnAppearanceBackgroundSaveCompleted(
        object? sender,
        AppearanceSaveCompletedEventArgs eventArgs)
    {
        _ = sender;
        ApplyError(eventArgs.Error);
    }

    private void OnWorkspaceSettingsPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        string[] propertyNames = eventArgs.PropertyName switch
        {
            nameof(WorkspaceSettingsViewModel.Editor) =>
                [nameof(WorkspaceEditor)],
            nameof(WorkspaceSettingsViewModel.HasEditor) =>
                [nameof(HasWorkspaceEditor)],
            _ => [],
        };
        foreach (var propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnFileProviderSettingsPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        string[] propertyNames = eventArgs.PropertyName switch
        {
            nameof(FileProviderSettingsViewModel.Definitions) =>
                [nameof(FileProviderDefinitions)],
            nameof(FileProviderSettingsViewModel.Profiles) =>
                [
                    nameof(FileProviderProfiles),
                    nameof(FileConnectionOptions),
                    nameof(SavedConnectionShortcuts),
                    nameof(SavedConnectionShortcutCount),
                ],
            _ => [],
        };
        foreach (var propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnSecretSettingsPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        var propertyName = eventArgs.PropertyName switch
        {
            nameof(SecretSettingsViewModel.Status) => nameof(SecretVaultStatus),
            nameof(SecretSettingsViewModel.HasNoSecrets) => nameof(HasNoSecrets),
            nameof(SecretSettingsViewModel.Secrets) => nameof(Secrets),
            _ => null,
        };
        if (propertyName is not null)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnSecretProjectionChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        AiProviderSettings.ApplyCatalog(_catalog.Snapshot);
        RefreshMcpServerDefinitions(_catalog.Snapshot);
    }

    private async void OnMcpServerDiagnosticsChanged(
        object? sender,
        McpServerDiagnosticsChangedEventArgs eventArgs)
    {
        _ = sender;
        try
        {
            await _uiThreadDispatcher.InvokeAsync(
                () =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _mcpDiagnosticsSnapshot = eventArgs.Snapshot;
                    RefreshMcpServerDefinitions(_catalog.Snapshot);
                    OnPropertyChanged(nameof(HasMcpDiagnosticHistory));
                    OnPropertyChanged(nameof(IsMcpCleanupUncertain));
                },
                _runtimeGraphLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // Window shutdown owns cancellation of presentation refreshes.
        }
    }

    private void OnTerminalContinuityPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        var propertyName = eventArgs.PropertyName switch
        {
            nameof(TerminalContinuitySettingsViewModel.UseForSshTerminals) =>
                nameof(UseTerminalMultiplexingForSshTerminals),
            nameof(TerminalContinuitySettingsViewModel.CanChange) =>
                nameof(CanChangeTerminalMultiplexing),
            nameof(TerminalContinuitySettingsViewModel.HasManagedSessions) =>
                nameof(HasManagedRemoteSessions),
            nameof(TerminalContinuitySettingsViewModel.ManagedSessions) =>
                nameof(ManagedRemoteSessions),
            _ => null,
        };
        if (propertyName is not null)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnAiProviderSettingsPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        string[] propertyNames = eventArgs.PropertyName switch
        {
            nameof(AiProviderSettingsViewModel.Definitions) => [nameof(AiProviderDefinitions)],
            nameof(AiProviderSettingsViewModel.Profiles) => [nameof(AiProviderProfiles)],
            nameof(AiProviderSettingsViewModel.HasProviders) => [nameof(HasAiProviders)],
            nameof(AiProviderSettingsViewModel.HasNoProviders) => [nameof(HasNoAiProviders)],
            _ => [],
        };
        foreach (var propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnAiProviderRuntimeProfilesChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        RefreshDefaultAgentPolicyOptions();
    }

    private void OnAgentWorkspaceScopePropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (!IsPreparingAgentSavedScreenTarget
            && !_isChangingAgentScopeForSavedScreen
            && _selectedAgentSavedScreenTemplate is not null
            && eventArgs.PropertyName is
                nameof(AgentWorkspaceScopeViewModel.SelectedScope)
                or nameof(AgentWorkspaceScopeViewModel.SelectedTerminalCount))
        {
            ClearAgentSavedScreenTarget();
        }

        string[] propertyNames = eventArgs.PropertyName switch
        {
            nameof(AgentWorkspaceScopeViewModel.SelectedScope) =>
                [nameof(SelectedAgentRunScope)],
            nameof(AgentWorkspaceScopeViewModel.IsSelectedPanelsScope) =>
                [nameof(IsAgentSelectedPanelsScope)],
            nameof(AgentWorkspaceScopeViewModel.HasTerminalOptions) =>
                [nameof(HasAgentTerminalSelectionOptions)],
            nameof(AgentWorkspaceScopeViewModel.SelectedTerminalCount) =>
                [nameof(AgentSelectedTerminalCount)],
            nameof(AgentWorkspaceScopeViewModel.SelectionSummary) =>
                [nameof(AgentTerminalSelectionSummary)],
            nameof(AgentWorkspaceScopeViewModel.SelectionStatus) =>
                [nameof(AgentTerminalSelectionStatus)],
            nameof(AgentWorkspaceScopeViewModel.HasSelectionError) =>
                [nameof(HasAgentTerminalSelectionError)],
            _ => [],
        };
        foreach (var propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnLauncherPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        string[] propertyNames = eventArgs.PropertyName switch
        {
            nameof(LauncherViewModel.SearchQuery) =>
                [nameof(LauncherSearchQuery)],
            nameof(LauncherViewModel.SelectedSearchResult) =>
                [nameof(SelectedLauncherSearchResult)],
            nameof(LauncherViewModel.HasSearchResults) =>
                [nameof(HasLauncherSearchResults)],
            nameof(LauncherViewModel.HasNoSearchResults) =>
                [nameof(HasNoLauncherSearchResults)],
            nameof(LauncherViewModel.SearchEmptyState) =>
                [nameof(LauncherSearchEmptyState)],
            _ => eventArgs.PropertyName is { } propertyName
                ? [propertyName]
                : [],
        };
        foreach (var propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void ActivateWorkspaceAgentChat(WorkspaceInstanceId? workspaceId)
    {
        if (_agentRuntimeFactory is null || _aiProviderRuntime is null)
        {
            return;
        }

        if (DefaultAgentPolicySettings.Policy is not { } configuredPolicy)
        {
            AgentChat = null;
            return;
        }

        if (workspaceId is not { } id)
        {
            AgentChat = null;
            return;
        }

        if (!_workspaceAgentChats.TryGetValue(id, out var owned))
        {
            var agentNetwork = AgentNetworkProxyFor(id);
            if (agentNetwork.Required && agentNetwork.Proxy is null)
            {
                AgentChat = null;
                return;
            }

            var runtime = _agentRuntimeFactory.Create(
                id,
                ConversationScopeOf(id),
                configuredPolicy,
                agentNetwork.Proxy);
            try
            {
                if (runtime is IAgentWorkspaceLayoutRuntime layoutRuntime)
                {
                    var layoutPort = new MainWindowAgentWorkspaceLayoutPort(this, id);
                    _agentWorkspaceLayoutPorts[id] = layoutPort;
                    layoutRuntime.AttachWorkspaceLayoutPort(layoutPort);
                }

                owned = new WorkspaceAgentChat(
                    runtime,
                    new AgentChatViewModel(
                        runtime,
                        _aiProviderRuntime,
                        _uiThreadDispatcher,
                        _agentRunAuditReader,
                        _agentModelFavoriteStore),
                    () => NotifyAgentRunFinished(id),
                    NotifyAgentRunningStateChanged,
                    activity => ApplyWorkspaceAgentActivity(id, activity));
                _workspaceAgentChats.Add(id, owned);
            }
            catch
            {
                runtime.Dispose();
                throw;
            }
        }

        AgentChat = owned.ViewModel;
    }

    private void NotifyAgentRunFinished(WorkspaceInstanceId workspaceId)
    {
        if (_openWorkspaces.FirstOrDefault(workspace => workspace.Id == workspaceId)
                is not { } workspace
            || !_workspaceAgentChats.TryGetValue(workspaceId, out var owned)
            || owned.ViewModel.State == GovernedAgentState.Cancelled)
        {
            return;
        }

        var failed = owned.ViewModel.State == GovernedAgentState.Failed;
        Notifications.NotifyWorkspaceSource(
            workspace,
            new PanelNotificationEvent(
                0,
                failed
                    ? PanelNotificationKind.AgentFailed
                    : PanelNotificationKind.AgentCompleted,
                failed ? "Agent run failed" : "Agent finished",
                workspace.Name,
                _timeProvider.GetUtcNow())
            {
                Effects = PanelNotificationEffects.Visual
                    | PanelNotificationEffects.System,
            },
            sourceIsVisible: IsAgentPanelVisible);
    }

    private void ActivateNativeNotification(
        NativeNotificationRoute route,
        PanelNotificationKind kind) =>
        _ = ActivateNativeNotificationAsync(route, kind);

    private async Task ActivateNativeNotificationAsync(
        NativeNotificationRoute route,
        PanelNotificationKind kind)
    {
        try
        {
            await ActivateNativeNotificationCoreAsync(route, kind);
        }
        catch (OperationCanceledException) when (
            _disposed || _runtimeGraphLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "notifications.activation.failed",
                exception);
        }
    }

    private async Task ActivateNativeNotificationCoreAsync(
        NativeNotificationRoute route,
        PanelNotificationKind kind)
    {
        if (_disposed
            || _openWorkspaces.FirstOrDefault(workspace => workspace.Id == route.WorkspaceId)
                is not { } workspace)
        {
            return;
        }

        if (!ReferenceEquals(RuntimeWorkspace, workspace))
        {
            await WorkspaceAutoSave.FlushAsync();
            if (_disposed || !_openWorkspaces.Contains(workspace))
            {
                return;
            }

            ReactivateRuntimeWorkspace(workspace);
        }

        Route = ShellRoute.Workspace;
        if (kind is PanelNotificationKind.AgentCompleted
            or PanelNotificationKind.AgentFailed)
        {
            IsAgentPanelVisible = true;
        }

        if (route.PanelId is { } panelId
            && workspace.Tabs.Any(tab => tab.Panels.Any(panel => panel.Id == panelId)))
        {
            await ActivatePanelAsync(panelId, _runtimeGraphLifetime.Token);
            return;
        }

        if (route.TabId is { } tabId
            && workspace.Tabs.Any(tab => tab.Id == tabId))
        {
            await ActivateTabAsync(tabId, _runtimeGraphLifetime.Token);
            return;
        }

        MarkVisibleNotificationsSeen();
    }

    private void NotifyAgentRunningStateChanged() =>
        OnPropertyChanged(nameof(HasRunningAgent));

    private void ApplyWorkspaceAgentActivity(
        WorkspaceInstanceId workspaceId,
        AgentToolActivityViewModel? activity)
    {
        var workspace = _openWorkspaces.FirstOrDefault(
            candidate => candidate.Id == workspaceId);
        if (workspace is null)
        {
            return;
        }

        foreach (var tab in workspace.Tabs)
        {
            foreach (var panel in tab.Panels)
            {
                panel.SetAgentActivity(panel.Id == activity?.PanelId
                    ? "AI agent working in this panel"
                    : null);
            }

            tab.SetAgentActivity(tab.Panels
                .FirstOrDefault(panel => panel.IsAgentActive)
                ?.AgentActivity);
        }

        workspace.HasAgentActivity = workspace.Tabs.Any(tab => tab.HasAgentActivity);
        RefreshWorkspaceRuntimeFlags();
    }

    private AgentConversationScopeId ConversationScopeOf(
        WorkspaceInstanceId workspaceId) =>
        _runtimeSources.TryGetValue(workspaceId, out var source)
            ? new AgentConversationScopeId(
                "definition:"
                + Convert.ToHexStringLower(SHA256.HashData(
                    Encoding.UTF8.GetBytes(source.SourceDefinition.ToString()))))
            : new AgentConversationScopeId($"runtime:{workspaceId.Value}");

    private void RemoveWorkspaceAgentChat(WorkspaceInstanceId workspaceId)
    {
        if (_workspaceAgentChats.Remove(workspaceId, out var owned))
        {
            owned.Dispose();
            NotifyAgentRunningStateChanged();
        }
    }

    /// <summary>
    /// The accent the open workspace asks the shell to wear, or null to go back
    /// to the application's own. Raised rather than applied here: retinting the
    /// shell means republishing application resources, which is the host's job.
    /// </summary>
    public event EventHandler<string?>? WorkspaceAccentChanged;

    private string? _activeWorkspaceAccent;

    /// <summary>
    /// The accent the open workspace is asking the shell to wear, for anything
    /// that starts listening after it was announced.
    /// </summary>
    public string? ActiveWorkspaceAccent => _activeWorkspaceAccent;

    /// <summary>
    /// Says which part of bringing a workspace forward cost the time, when any
    /// of it did. Under the budget it says nothing; a switch that is not felt
    /// is not worth a line.
    /// </summary>
    private static void ReportSwitchPhases(
        string _,
        long autoSaveMilliseconds,
        long activationMilliseconds,
        long snapshotMilliseconds)
    {
        const long budgetMilliseconds = 32;
        var total = autoSaveMilliseconds + activationMilliseconds + snapshotMilliseconds;
        if (total < budgetMilliseconds)
        {
            return;
        }

        SecretSafeDiagnosticProjection.WriteStandardError(
            "workspace.activation.performance-budget-exceeded",
            SecretSafeDiagnosticKind.Unexpected);
    }

    /// <summary>
    /// Brings one workspace's canvas forward without ever showing an empty one.
    ///
    /// A dock control builds only the layout it is showing — one that is not
    /// shown has no visual tree at all, whatever it is given and however long it
    /// waits — so the arriving canvas cannot be prepared out of sight. It is
    /// shown straight away and the departing one is left on top of it, covering
    /// it whole, until it has had frames enough to build. Then the cover comes
    /// off and what is underneath is finished.
    /// </summary>
    private static void ShowCanvasOf(
        RuntimeWorkspaceViewModel? workspace,
        RuntimeWorkspaceViewModel? insteadOf)
    {
        if (workspace is not null)
        {
            workspace.CanvasDepth = 0;
            workspace.IsCanvasShown = true;
        }

        if (insteadOf is null || ReferenceEquals(insteadOf, workspace))
        {
            return;
        }

        if (workspace is null)
        {
            insteadOf.IsCanvasShown = false;
            insteadOf.CanvasDepth = 0;
            return;
        }

        insteadOf.CanvasDepth = 1;
        RetireCanvas(insteadOf, CoveringCanvasFrames);
    }

    /// <summary>
    /// How long the departing canvas keeps covering the arriving one. Frames
    /// rather than milliseconds, because what is being waited for is a dock
    /// control building its tree, which happens across passes. Three, because
    /// the measurement that found this showed the rebuild landing after the
    /// frame following the swap.
    /// </summary>
    private const int CoveringCanvasFrames = 3;

    private static void RetireCanvas(
        RuntimeWorkspaceViewModel canvas,
        int framesRemaining)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                // It came back to the front while it was covering; it is not
                // retiring any more.
                if (canvas.CanvasDepth == 0)
                {
                    return;
                }

                if (framesRemaining > 0)
                {
                    RetireCanvas(canvas, framesRemaining - 1);
                    return;
                }

                canvas.IsCanvasShown = false;
                canvas.CanvasDepth = 0;
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void SetActiveWorkspaceAccent(string? accent)
    {
        var next = string.IsNullOrWhiteSpace(accent) ? null : accent;
        if (string.Equals(_activeWorkspaceAccent, next, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeWorkspaceAccent = next;
        WorkspaceAccentChanged?.Invoke(this, next);
    }

    public string NewItemLauncherTitle => HasRuntimeWorkspace ? "New Tab" : "New Session";

    public bool CanStartBrowserSession => _browserRendererViewFactory is not null;

    public bool CanCreateBrowserPanel =>
        CanStartBrowserSession
        && RuntimeWorkspace?.ActiveTab is not null;

    public RuntimePanelViewModel? ActivePanel => RuntimeWorkspace?.ActiveTab?.ActivePanel;

    public async Task CreateAgentSavedScreenTargetAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanCreateAgentSavedScreenTarget
            || SelectedAgentSavedScreenTemplate is not { } selected)
        {
            return;
        }

        var stored = _catalog.Snapshot.Screens.SingleOrDefault(
            item => item.Value.Id == selected.Id);
        if (stored is null || stored.Revision != selected.Revision)
        {
            AgentSavedScreenTargetStatus =
                "The selected template changed or was removed. Select its current revision and review it again.";
            _selectedAgentSavedScreenTemplate = null;
            _agentSavedScreenLiveTarget = null;
            NotifyAgentSavedScreenTargetChanged();
            return;
        }

        IsPreparingAgentSavedScreenTarget = true;
        AgentSavedScreenTargetStatus =
            $"Creating a pending live instance from {selected.Name} revision {selected.Revision}. The template still grants no agent authority.";
        try
        {
            if (!await LaunchScreenAsync(selected.Id, cancellationToken))
            {
                AgentSavedScreenTargetStatus = string.IsNullOrWhiteSpace(OperationError)
                    ? "The template could not create a live instance. No agent authority was granted."
                    : $"The template could not create a live instance: {OperationError}";
                return;
            }

            if (RuntimeWorkspace is not { ActiveTab: { } tab } workspace
                || tab.HistorySource?.SourceDefinition != stored.Value.Key
                || !tab.AgentPolicy.Sources.Any(source =>
                    source.Definition == stored.Value.Key
                    && source.Revision == stored.Revision))
            {
                AgentSavedScreenTargetStatus =
                    "The created tab did not preserve the selected template revision. No agent authority was granted.";
                return;
            }

            _agentSavedScreenLiveTarget = new AgentSavedScreenLiveTarget(
                selected.Id,
                selected.Revision,
                selected.Name,
                WindowId,
                workspace.Id,
                workspace.Name,
                tab.Id,
                tab.Title,
                tab.Panels.Count);
            _isChangingAgentScopeForSavedScreen = true;
            try
            {
                SelectedAgentRunScope = AgentRunScopeOptions.Single(
                    option => option.Kind == AgentRunScopeKind.CurrentTab);
            }
            finally
            {
                _isChangingAgentScopeForSavedScreen = false;
            }

            AgentSavedScreenTargetStatus =
                $"Pending live instance: {tab.Title} · {tab.Panels.Count} panels · template revision {selected.Revision}. Review the visible tab, then authorize this exact live target.";
            NotifyAgentSavedScreenTargetChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AgentSavedScreenTargetStatus =
                "Creating the pending live instance was cancelled. No agent authority was granted.";
            throw;
        }
        finally
        {
            IsPreparingAgentSavedScreenTarget = false;
        }
    }

    public bool TryAuthorizeAgentSavedScreenTarget(out string error)
    {
        if (_agentSavedScreenLiveTarget is not { IsAuthorized: false } pending)
        {
            error = "Create a pending live instance before authorizing a saved-screen target.";
            return false;
        }

        if (!TryResolveAgentSavedScreenLiveTarget(pending, requireVisibleReview: true, out _))
        {
            error =
                "The pending live instance is no longer the visible reviewed tab. Return to that exact tab or create a new instance.";
            AgentSavedScreenTargetStatus = error;
            return false;
        }

        _agentSavedScreenLiveTarget = pending.Authorize();
        error = string.Empty;
        AgentSavedScreenTargetStatus =
            $"Authorized live target: {pending.TabName} · {pending.PanelCount} panels · {pending.ExactIdentity}. Later template edits cannot retarget this run context.";
        NotifyAgentSavedScreenTargetChanged();
        return true;
    }

    public void ClearAgentSavedScreenTarget()
    {
        _selectedAgentSavedScreenTemplate = null;
        _agentSavedScreenLiveTarget = null;
        AgentSavedScreenTargetStatus =
            "A saved screen template grants no agent authority until its live instance is created, reviewed, and authorized.";
        NotifyAgentSavedScreenTargetChanged();
    }

    public Task SendAgentPromptAsync(CancellationToken cancellationToken = default)
    {
        if (AgentChat is not { } agentChat)
        {
            return Task.CompletedTask;
        }

        if (agentChat.CanOfferFollowUpQueue)
        {
            return agentChat.QueueFollowUpAsync(cancellationToken);
        }

        AgentTarget target;
        string targetError;
        if (_selectedAgentSavedScreenTemplate is not null)
        {
            if (_agentSavedScreenLiveTarget is not { IsAuthorized: true } accepted)
            {
                agentChat.ReportTargetUnavailable(
                    "The saved screen is still a template or pending live instance. Review and authorize its exact live target before sending.");
                return Task.CompletedTask;
            }

            if (!TryResolveAgentSavedScreenLiveTarget(
                    accepted,
                    requireVisibleReview: false,
                    out target))
            {
                AgentSavedScreenTargetStatus =
                    "The authorized live target is no longer available. No replacement was selected; create and review a new live instance.";
                agentChat.ReportTargetUnavailable(AgentSavedScreenTargetStatus);
                return Task.CompletedTask;
            }

            targetError = string.Empty;
        }
        else if (!AgentWorkspaceScope.TryCreateTarget(out target, out targetError))
        {
            agentChat.ReportTargetUnavailable(targetError);
            return Task.CompletedTask;
        }

        if (RuntimeWorkspace is not { } workspace)
        {
            agentChat.ReportTargetUnavailable(targetError);
            return Task.CompletedTask;
        }

        if (!TryResolveAgentPolicy(workspace, target, out var policy, out var policyError))
        {
            agentChat.ReportTargetUnavailable(policyError);
            return Task.CompletedTask;
        }

        return agentChat.SendAsync(
            target,
            policy ?? throw new InvalidOperationException(
                "Policy resolution succeeded without a complete agent policy."),
            cancellationToken);
    }

    private bool TryResolveAgentSavedScreenLiveTarget(
        AgentSavedScreenLiveTarget candidate,
        bool requireVisibleReview,
        out AgentTarget target)
    {
        target = null!;
        if (candidate.WindowId != WindowId
            || RuntimeWorkspace is not { } workspace
            || workspace.Id != candidate.WorkspaceId
            || workspace.Tabs.SingleOrDefault(tab => tab.Id == candidate.TabId)
                is not { } tab
            || (requireVisibleReview && !ReferenceEquals(workspace.ActiveTab, tab)))
        {
            return false;
        }

        target = candidate.Target;
        return true;
    }

    private void NotifyAgentSavedScreenTargetChanged()
    {
        OnPropertyChanged(nameof(SelectedAgentSavedScreenTemplate));
        OnPropertyChanged(nameof(AgentSavedScreenLiveTarget));
        OnPropertyChanged(nameof(HasPendingAgentSavedScreenTarget));
        OnPropertyChanged(nameof(HasAuthorizedAgentSavedScreenTarget));
        OnPropertyChanged(nameof(CanCreateAgentSavedScreenTarget));
        OnPropertyChanged(nameof(CanAuthorizeAgentSavedScreenTarget));
    }

    private RuntimeAgentPolicyProvenance CurrentAgentPolicyProvenance() =>
        new(DefaultAgentPolicySettings.Policy);

    private bool TryResolveAgentPolicy(
        RuntimeWorkspaceViewModel workspace,
        AgentTarget target,
        out AgentPolicy? policy,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(target);
        RuntimeTabViewModel[] tabs = target switch
        {
            AgentTarget.Panel panel =>
                [.. workspace.Tabs.Where(tab => tab.Id == panel.TabId)],
            AgentTarget.OpenTab openTab =>
                [.. workspace.Tabs.Where(tab => tab.Id == openTab.TabId)],
            AgentTarget.Workspace =>
                [.. workspace.Tabs],
            AgentTarget.SelectedPanels selected
                when selected.Panels.All(panel =>
                    workspace.Tabs.Any(tab =>
                        tab.Id == panel.TabId
                        && tab.Panels.Any(candidate => candidate.Id == panel.PanelId))) =>
                [.. selected.Panels
                    .Select(panel => workspace.Tabs.Single(tab => tab.Id == panel.TabId))
                    .Distinct()],
            _ => [],
        };
        if (tabs.Length == 0)
        {
            policy = null;
            error = "The selected agent scope no longer has trusted runtime policy provenance.";
            return false;
        }

        var overrideCount = tabs.Count(tab => tab.AgentPolicy.HasPolicyOverride);
        if (overrideCount == 0)
        {
            if (DefaultAgentPolicySettings.Policy is not { } configuredPolicy)
            {
                policy = null;
                error = "Configure the primary, compaction, and title models in AI settings.";
                return false;
            }

            policy = configuredPolicy;
            error = string.Empty;
            return true;
        }

        if (overrideCount != tabs.Length)
        {
            policy = null;
            error =
                "The selected scope combines saved policy overrides with inherited provider settings. "
                + "Choose a narrower scope.";
            return false;
        }

        var policies = tabs
            .Select(tab => tab.AgentPolicy.EffectivePolicy
                ?? throw new InvalidOperationException(
                    "A saved agent-policy override is missing its effective policy."))
            .ToArray();
        var first = policies[0];
        if (policies.Any(candidate =>
                !string.Equals(candidate.Provider, first.Provider, StringComparison.Ordinal)
                || !string.Equals(candidate.Model, first.Model, StringComparison.Ordinal)))
        {
            policy = null;
            error =
                "The selected scope combines different saved agent policy providers or models. "
                + "Choose a narrower scope.";
            return false;
        }

        try
        {
            policy = AgentPolicyResolver.ResolveLeastPrivilege(policies);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            policy = null;
            error =
                "The selected workspace does not have valid agent settings. "
                + "Choose a narrower scope.";
            return false;
        }
    }

    public CommandContext ActiveCommandContexts
    {
        get
        {
            var contexts = CommandContext.Global | CommandContext.Window;
            if (!IsWorkspaceVisible
                || RuntimeWorkspace is not { ActiveTab: { } activeTab })
            {
                return HasOverlay ? contexts | CommandContext.Modal : contexts;
            }

            contexts |= CommandContext.Workspace | CommandContext.Tab;
            if (activeTab.ActivePanel is { } activePanel)
            {
                contexts |= CommandContext.Panel;
                contexts |= activePanel.Kind switch
                {
                    PanelKind.Terminal => CommandContext.Terminal,
                    PanelKind.Browser => CommandContext.Browser,
                    _ => CommandContext.None,
                };
            }

            return HasOverlay ? contexts | CommandContext.Modal : contexts;
        }
    }

    public bool IsWorkspaceVisible => _navigation.IsWorkspaceVisible;

    public bool IsSettingsVisible => _navigation.IsSettingsVisible;

    public bool IsWorkspaceCanvasVisible =>
        _navigation.IsWorkspaceCanvasVisible && !IsWorkspaceIsolationStarting;

    public bool IsAppearanceSettingsVisible => _navigation.IsAppearanceSettingsVisible;

    public bool IsWorkspaceSettingsVisible => _navigation.IsWorkspaceSettingsVisible;

    public bool IsNetworkingSettingsVisible => _navigation.IsNetworkingSettingsVisible;

    public bool RestoreSessionsOnStart
    {
        get => _restoreSessionsOnStart;
        private set => SetProperty(ref _restoreSessionsOnStart, value);
    }

    public bool CanChangeRestoreSessionsOnStart =>
        _sessionRestoreCoordinator is null
        || (_sessionRestorePreferenceLoaded && !_sessionRestorePreferenceSaving);

    public bool UseTerminalMultiplexingForSshTerminals =>
        TerminalContinuity.UseForSshTerminals;

    public bool CanChangeTerminalMultiplexing =>
        TerminalContinuity.CanChange;

    public bool HasManagedRemoteSessions => TerminalContinuity.HasManagedSessions;

    public bool IsKeybindingSettingsVisible => _navigation.IsKeybindingSettingsVisible;

    public bool IsFilesSettingsVisible => _navigation.IsFilesSettingsVisible;

    public bool IsBrowserSettingsVisible => _navigation.IsBrowserSettingsVisible;

    public bool IsTerminalSettingsVisible => _navigation.IsTerminalSettingsVisible;

    public bool IsQuickTerminalSettingsVisible => _navigation.IsQuickTerminalSettingsVisible;

    public bool IsSecretsSettingsVisible => _navigation.IsSecretsSettingsVisible;

    public bool IsDiagnosticsSettingsVisible => _navigation.IsDiagnosticsSettingsVisible;

    public bool IsAgentSettingsVisible => _navigation.IsAgentSettingsVisible;

    public bool IsMcpSettingsVisible => _navigation.IsMcpSettingsVisible;

    public bool IsAboutSettingsVisible => _navigation.IsAboutSettingsVisible;

    public string SecretVaultStatus
    {
        get => SecretSettings.Status;
    }

    public bool HasOverlay => _navigation.HasOverlay;

    public bool IsCommandPaletteVisible => _navigation.IsCommandPaletteVisible;

    public bool IsNewPanelVisible => _navigation.IsNewPanelVisible;

    public bool IsLayoutDesignerVisible => _navigation.IsLayoutDesignerVisible;

    public bool IsDefinitionEditorVisible => _navigation.IsDefinitionEditorVisible;

    public string EditorTitle => DefinitionEdit.EditorTitle;

    public string EditorName
    {
        get => DefinitionEdit.EditorName;
        set => DefinitionEdit.EditorName = value;
    }

    public string EditorDescription
    {
        get => DefinitionEdit.EditorDescription;
        set => DefinitionEdit.EditorDescription = value;
    }

    public bool IsAgentPanelVisible
    {
        get => _isAgentPanelVisible;
        set
        {
            if (SetProperty(ref _isAgentPanelVisible, value))
            {
                OnPropertyChanged(nameof(IsAgentPanelDockedVisible));
                if (value)
                {
                    Notifications.MarkWorkspaceSourceSeen(RuntimeWorkspace);
                }
            }
        }
    }

    /// <summary>
    /// Whether the agent panel holds a slot in the layout rather than floating
    /// over the canvas. Per workspace: read from its definition when it comes
    /// to the front, written back when the pin is toggled.
    /// </summary>
    public bool IsAgentPanelDocked
    {
        get => _isAgentPanelDocked;
        private set
        {
            if (SetProperty(ref _isAgentPanelDocked, value))
            {
                OnPropertyChanged(nameof(IsAgentPanelDockedVisible));
                OnPropertyChanged(nameof(AgentPanelPinTip));
            }
        }
    }

    /// <summary>The layout reserves the agent panel's width only while a pinned panel is on screen.</summary>
    public bool IsAgentPanelDockedVisible => IsAgentPanelVisible && IsAgentPanelDocked;

    public string AgentPanelPinTip => IsAgentPanelDocked
        ? "Unpin — float over the workspace"
        : "Pin to the workspace layout";

    public string LauncherSearchQuery
    {
        get => Launcher.SearchQuery;
        set => Launcher.SearchQuery = value;
    }

    public LauncherSearchResultViewModel? SelectedLauncherSearchResult
    {
        get => Launcher.SelectedSearchResult;
        set => Launcher.SelectedSearchResult = value;
    }

    public string HistorySearchQuery
    {
        get => History.SearchQuery;
        set => History.SearchQuery = value;
    }

    public RecentSessionHistoryItemViewModel? SelectedHistorySession
    {
        get => History.SelectedSession;
        set => History.SelectedSession = value;
    }

    public bool HasSelectedHistorySession => History.HasSelectedSession;

    public bool HasNoSelectedHistorySession => !HasSelectedHistorySession;

    public HistoryExportScope SelectedHistoryExportScope
    {
        get => History.SelectedExportScope;
        set => History.SelectedExportScope = value;
    }

    public IReadOnlyList<RecentSessionRecord> CaptureHistoryExportSnapshot() =>
        History.CaptureExportSnapshot();

    public void SetHistoryExportStatus(string status)
    {
        History.SetExportStatus(status);
    }

    public bool TryBeginHistoryExport(HistoryExportScope scope)
    {
        return History.TryBeginExport(scope);
    }

    public void EndHistoryExport(string status)
    {
        History.EndExport(status);
    }

    public async Task<bool> RetryRecentSessionHistoryAsync(
        CancellationToken cancellationToken)
    {
        return await History.RetryAsync(cancellationToken);
    }

    public void SelectFirstAvailableLauncherSearchResult()
    {
        Launcher.SelectFirstAvailableSearchResult();
    }

    public void MoveLauncherSearchSelection(int direction)
    {
        Launcher.MoveSearchSelection(direction);
    }

    public LauncherSearchTarget? ConfirmLauncherSearchSelection() =>
        Launcher.ConfirmSearchSelection();

    public string? OperationError
    {
        get => _operationError;
        private set
        {
            if (SetProperty(ref _operationError, value))
            {
                OnPropertyChanged(nameof(HasOperationError));
            }
        }
    }

    public bool HasOperationError => !string.IsNullOrWhiteSpace(OperationError);

    public string TabReorderStatus
    {
        get => _tabReorderStatus;
        private set => SetProperty(ref _tabReorderStatus, value);
    }

    public string WorkspaceTransferStatus
    {
        get => _workspaceTransferStatus;
        private set => SetProperty(ref _workspaceTransferStatus, value);
    }

    public string? ApplicationKeySequenceHint
    {
        get => _applicationKeySequenceHint;
        private set
        {
            if (SetProperty(ref _applicationKeySequenceHint, value))
            {
                OnPropertyChanged(nameof(HasApplicationKeySequenceHint));
            }
        }
    }

    public bool HasApplicationKeySequenceHint =>
        !string.IsNullOrWhiteSpace(ApplicationKeySequenceHint);

    public string CommandPaletteShortcut =>
        QuickTerminalHotkeyText.FormatApplicationCommand("K");

    public string LauncherShortcutSummary =>
        $"{QuickTerminalHotkeyText.Format(new KeyStroke("1–9", KeyModifiers.Meta))} workspaces   " +
        $"{QuickTerminalHotkeyText.FormatApplicationCommand(",")} settings   " +
        $"{CommandPaletteShortcut} search";

    public string CommandPaletteAction => $"{CommandPaletteShortcut}  Search";

    public string CommandPaletteSettingsAction =>
        $"{CommandPaletteShortcut}  Search & commands";

    public ThemePreference ActiveTheme => AppearanceSettings.ActiveTheme;

    public TerminalProfile? ActiveTerminalProfile =>
        TerminalSettings.ActiveTerminalProfile;

    public KeymapProfile ActiveApplicationKeymap =>
        DefinitionSettings.ActiveApplicationKeymap;

    public long ActiveApplicationKeymapRevision =>
        DefinitionSettings.ActiveApplicationKeymapRevision;

    public string ActiveApplicationKeymapName =>
        DefinitionSettings.ActiveApplicationKeymapName;

    public TerminalProfileEditorViewModel? TerminalSettingsEditor
    {
        get => TerminalSettings.TerminalEditor;
    }

    public QuickTerminalSettingsEditorViewModel? QuickTerminalSettingsEditor
    {
        get => TerminalSettings.QuickTerminalEditor;
    }

    public string ThemeMode => AppearanceSettings.ThemeMode;

    public string ThemeProfile => AppearanceSettings.ThemeProfile;

    public string ThemeTextScale => AppearanceSettings.ThemeTextScale;

    /// <summary>Window-chrome settings the shell layout binds to directly.</summary>
    public bool ShowTabBar => AppearanceSettings.ShowTabBar;

    /// <summary>
    /// Whether the workspace rail is shown.
    ///
    /// Settable, so a switch can bind to it both ways like every other toggle
    /// in the shell. Bound one way and driven by a changed event instead, the
    /// binding pushed the stored value back over the user's flip and the switch
    /// sprang shut again.
    ///
    /// Writing it saves only this field: a surface showing one switch must not
    /// carry the rest of the chrome back to its defaults on the way past.
    /// </summary>
    public bool ShowWorkspacesPanel
    {
        get => AppearanceSettings.ShowWorkspacesPanel;
        set => AppearanceSettings.ShowWorkspacesPanel = value;
    }

    public bool IsWorkspacePanelOnLeft => AppearanceSettings.IsWorkspacePanelOnLeft;

    public bool IsWorkspacePanelOnRight => AppearanceSettings.IsWorkspacePanelOnRight;

    /// <summary>The rail's dock edge, so the setting moves the real panel.</summary>
    public Avalonia.Controls.Dock WorkspacePanelDock => AppearanceSettings.WorkspacePanelDock;

    public bool IsTabStripVisibleOnTop => AppearanceSettings.IsTabStripVisibleOnTop;

    public bool IsTabStripVisibleOnBottom => AppearanceSettings.IsTabStripVisibleOnBottom;

    /// <summary>A side strip is one control docked to whichever edge is chosen.</summary>
    public bool IsTabStripVisibleOnSide => AppearanceSettings.IsTabStripVisibleOnSide;

    public Avalonia.Controls.Dock TabStripDock => AppearanceSettings.TabStripDock;

    /// <summary>Which side the strip touches, as booleans, because the strip's
    /// floating margin belongs on the window side only — the canvas supplies
    /// the gap on the inner side, the same contract the other sidebars keep.</summary>
    public bool IsTabStripDockedLeft => AppearanceSettings.IsTabStripDockedLeft;

    public bool IsTabStripDockedRight => AppearanceSettings.IsTabStripDockedRight;

    public Avalonia.Controls.PlacementMode SideTabIconPickerPlacement =>
        AppearanceSettings.SideTabIconPickerPlacement;

    public string ThemeAccent => AppearanceSettings.ThemeAccent;

    public int KeybindingConflictCount =>
        DefinitionSettings.KeybindingConflictCount;

    public void ShowSettings(SettingsPage page = SettingsPage.Appearance)
    {
        if (!TryDismissOverlayForNavigation())
        {
            return;
        }

        _navigation.ShowSettings(page);
        if (page is SettingsPage.Secrets or SettingsPage.Mcp)
        {
            // Listing the native credential store may show an OS authorization
            // prompt. Cross that boundary only after explicit user navigation.
            _ = RefreshSecretsAsync(CancellationToken.None);
        }

        if (page == SettingsPage.Keybindings)
        {
            DefinitionSettings.EnsureKeybindingEditor();
        }

        if (page == SettingsPage.Files)
        {
            // The usage figure is read when the page is opened, not on a
            // timer: a settings page is looked at, not watched.
            FilePreviewSettingsEditor.RefreshCacheUsage();
        }

        if (page == SettingsPage.Browser)
        {
            BrowserProfileSettingsEditor.RefreshUsage();
        }
    }

    public void ShowWorkspace()
    {
        if (RuntimeWorkspace is not null && TryDismissOverlayForNavigation())
        {
            var previousRoute = Route;
            _navigation.ShowWorkspace();
            if (previousRoute != Route)
            {
                MarkVisibleNotificationsSeen();
            }
        }
    }

    public void ShowOverlay(ShellOverlay overlay)
    {
        if (Overlay == ShellOverlay.LayoutDesigner
            && overlay != ShellOverlay.LayoutDesigner
            && LayoutDesignerEditor?.RequestCancel()
                == LayoutDesignerCancelDisposition.ConfirmDiscard)
        {
            SetError("Save or discard the layout changes before opening another overlay.");
            return;
        }

        if (Overlay == ShellOverlay.DefinitionEditor
            && overlay != ShellOverlay.DefinitionEditor
            && WorkspaceEditor?.RequestCancel()
                == WorkspaceEditorCancelDisposition.ConfirmDiscard)
        {
            SetError("Save or discard the workspace changes before opening another overlay.");
            return;
        }

        Overlay = overlay;
        if (overlay == ShellOverlay.CommandPalette)
        {
            LauncherSearchQuery = string.Empty;
            Launcher.RefreshSearchResults(preserveSelection: false);
        }
    }

    public void CloseOverlay() => Overlay = ShellOverlay.None;

    public void DismissWorkspaceEditor()
    {
        WorkspaceSettings.Dismiss();
        DefinitionEdit.Clear();
        if (Overlay == ShellOverlay.DefinitionEditor)
        {
            Overlay = ShellOverlay.None;
        }
    }

    public void BeginCreateLayout()
    {
        if (!DefinitionSettings.TryBeginCreateLayout(out var error))
        {
            SetError(error!);
            return;
        }

        ClearError();
        Overlay = ShellOverlay.LayoutDesigner;
    }

    public void BeginEditLayout(LayoutId id)
    {
        if (!DefinitionSettings.TryBeginEditLayout(id, out var error))
        {
            SetError(error!);
            return;
        }

        ClearError();
        Overlay = ShellOverlay.LayoutDesigner;
    }

    public void DismissLayoutDesigner()
    {
        DefinitionSettings.DismissLayoutDesigner();
        if (Overlay == ShellOverlay.LayoutDesigner)
        {
            Overlay = ShellOverlay.None;
        }
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>>
        SaveLayoutDesignerAsync(CancellationToken cancellationToken)
    {
        ClearError();
        var result = await DefinitionSettings.SaveLayoutDesignerAsync(cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public void SelectKeybindingProfile(KeybindingProfileItemViewModel profile)
    {
        if (!DefinitionSettings.TrySelectKeybindingProfile(profile, out var error))
        {
            SetError(error!);
            return;
        }

        ClearError();
    }

    public void CloneSelectedKeybindingProfile()
    {
        if (!DefinitionSettings.TryCloneSelectedKeybindingProfile(out var error))
        {
            SetError(error!);
            return;
        }

        ClearError();
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<KeymapProfile>>>
        SaveKeybindingEditorAsync(CancellationToken cancellationToken)
    {
        ClearError();
        var result = await DefinitionSettings.SaveKeybindingEditorAsync(cancellationToken);
        ApplyError(result.Error);
        if (result.IsSuccess)
        {
            Launcher.RefreshSearchResults();
        }

        return result;
    }

    public void BeginEditWorkspace(WorkspaceId id)
    {
        if (!WorkspaceSettings.TryBeginEdit(id, out var identity, out var error))
        {
            SetError(error!);
            return;
        }

        DefinitionEdit.Begin(
            identity!.Key,
            identity.Revision,
            identity.Name,
            identity.Description);
        ClearError();
        Overlay = ShellOverlay.DefinitionEditor;
    }

    public bool TryGetWorkspaceDefinitionId(
        WorkspaceInstanceId runtimeId,
        out WorkspaceId workspaceId)
    {
        if (_runtimeSources.TryGetValue(runtimeId, out var source)
            && source.SourceDefinition.Kind == WorkspaceDefinition.Kind)
        {
            workspaceId = new WorkspaceId(source.SourceDefinition.Value);
            return true;
        }

        workspaceId = default;
        return false;
    }

    /// <summary>
    /// Opens the workspace editor over a fresh unsaved definition. Nothing is
    /// persisted until the editor saves, so cancelling leaves no orphan.
    /// </summary>
    public void BeginCreateWorkspace()
    {
        if (!WorkspaceSettings.TryBeginCreate(out var identity, out var error))
        {
            SetError(error!);
            return;
        }

        DefinitionEdit.Begin(
            identity!.Key,
            identity.Revision,
            identity.Name,
            identity.Description);
        ClearError();
        Overlay = ShellOverlay.DefinitionEditor;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
        SaveWorkspaceEditorAsync(CancellationToken cancellationToken)
    {
        ClearError();
        var result = await WorkspaceSettings.SaveAsync(cancellationToken);
        CompleteWorkspaceEditorSave(result);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
        SaveWorkspaceEditorAsync(
            WorkspaceEditorSaveRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        var result = await WorkspaceSettings.SaveAsync(request, cancellationToken);
        CompleteWorkspaceEditorSave(result);
        return result;
    }

    private void CompleteWorkspaceEditorSave(
        DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>> result)
    {
        ApplyError(result.Error);
        if (result is not { IsSuccess: true, Value: { } saved })
        {
            return;
        }

        ApplyTerminalMultiplexingOverrideToOpenWorkspaces(saved.Value);
        DismissWorkspaceEditor();
    }

    private void ApplyTerminalMultiplexingOverrideToOpenWorkspaces(
        WorkspaceDefinition definition)
    {
        foreach (var runtime in _openWorkspaces)
        {
            if (!_runtimeSources.TryGetValue(runtime.Id, out var source)
                || source.SourceDefinition != definition.Key)
            {
                continue;
            }

            var mode = definition.IsIsolated || runtime.IsolationBinding is not null
                ? TerminalMultiplexingMode.Disabled
                : definition.TerminalMultiplexingOverride;
            runtime.TerminalMultiplexingMode = mode;
            if (mode is { } workspaceMode)
            {
                _workspaceTerminalMultiplexingModes[runtime.Id] = workspaceMode;
            }
            else
            {
                _workspaceTerminalMultiplexingModes.Remove(runtime.Id);
            }
        }
    }

    public void BeginEditScreen(ScreenId id)
    {
        var stored = _catalog.Snapshot.Screens.SingleOrDefault(item => item.Value.Id == id);
        if (stored is null)
        {
            SetError("That saved screen no longer exists.");
            return;
        }

        BeginEdit(stored.Value.Key, stored.Revision, stored.Value.Name, stored.Value.Description);
    }

    public void ToggleAgentPanel() => IsAgentPanelVisible = !IsAgentPanelVisible;

    /// <summary>
    /// Reads the front workspace's saved pin. A pinned panel is part of the
    /// layout and comes up with it; an unpinned one is a flyout, summoned when
    /// asked for — so arriving anywhere starts it hidden.
    /// </summary>
    private void SyncAgentPanelPlacement(RuntimeWorkspaceViewModel? workspace)
    {
        var pinned = FrontWorkspaceDefinition(workspace)?.Value.AgentPanelPinned == true;
        IsAgentPanelDocked = pinned;
        IsAgentPanelVisible = pinned;
    }

    /// <summary>
    /// Flips between the docked slot and the floating flyout, and writes the
    /// choice onto the workspace it was made in. A workspace with no saved
    /// definition behind it — a local browser, an ad-hoc database — keeps the
    /// choice for as long as it is open, which is all it has.
    /// </summary>
    public async Task ToggleAgentPanelPinAsync(CancellationToken cancellationToken)
    {
        var docked = !IsAgentPanelDocked;
        IsAgentPanelDocked = docked;
        IsAgentPanelVisible = true;

        var saved = await WorkspaceSettings.SetAgentPanelPinnedAsync(
            _runtimeHistorySource?.SourceDefinition,
            docked,
            cancellationToken);
        ApplyError(saved?.Error);
    }

    private StoredDefinition<WorkspaceDefinition>? FrontWorkspaceDefinition(
        RuntimeWorkspaceViewModel? workspace)
    {
        if (workspace is null
            || !_runtimeSources.TryGetValue(workspace.Id, out var source)
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind)
        {
            return null;
        }

        return _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Key == source.SourceDefinition);
    }

    public async Task<bool> OpenWorkspaceAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (_shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested)
        {
            return false;
        }

        using var operationLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        var operationCancellation = operationLifetime.Token;
        var storedWorkspace = _catalog.Snapshot.Workspaces
            .SingleOrDefault(item => item.Value.Id == workspaceId);
        if (storedWorkspace is null)
        {
            SetError("That workspace no longer exists.");
            return false;
        }

        var workspace = storedWorkspace.Value;

        // Already open: bring it forward rather than building a second one. The
        // sessions in it are the point — rebuilding them from the definition is
        // what "switching killed my processes" was.
        if (FindOpenWorkspace(workspace.Key) is { } alreadyOpen)
        {
            if (!IsolationIntentMatches(alreadyOpen))
            {
                SetError(
                    "Workspace isolation changed while this workspace was open. "
                    + "Close and reopen it to apply the new isolation setting.");
                return false;
            }

            // Timed in pieces because the whole of it runs on the thread that
            // draws, and from outside a slow switch is just a frozen window.
            // The autosave flush writes the workspace being left; reactivating
            // announces the accent, which republishes every appearance token;
            // the snapshot serialises the workspace that is now in front.
            var clock = Stopwatch.StartNew();
            await WorkspaceAutoSave.FlushAsync();
            if (_shutdownStarted || operationCancellation.IsCancellationRequested)
            {
                return false;
            }

            var flushed = clock.ElapsedMilliseconds;
            ReactivateRuntimeWorkspace(alreadyOpen);
            var reactivated = clock.ElapsedMilliseconds;
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            clock.Stop();
            ReportSwitchPhases(
                workspace.Name,
                flushed,
                reactivated - flushed,
                clock.ElapsedMilliseconds - reactivated);
            return true;
        }

        var runtimeId = WorkspaceInstanceId.New();
        if (!_workspaceDefinitionOccupancy.TryRegisterRuntime(
            WindowId,
            runtimeId,
            workspace.Key))
        {
            SetError(
                "This workspace is already opening in this window, or its isolation settings "
                + "are being saved. Wait for that operation to finish, then try again.");
            return false;
        }

        if (!TryBeginWorkspaceActivation(
            out var activationId,
            out var activationCompletion))
        {
            _workspaceDefinitionOccupancy.Unregister(WindowId, runtimeId);
            return false;
        }

        var isolation = WorkspaceIsolationPreparation.Host;
        RuntimeWorkspaceViewModel? runtime = null;
        if (workspace.IsIsolated)
        {
            BeginWorkspaceIsolationStartup(workspace, activationId);
        }

        try
        {
            isolation = await PrepareWorkspaceIsolationAsync(
                workspace,
                operationCancellation);
            if (!isolation.Succeeded || _shutdownStarted)
            {
                return false;
            }

            operationCancellation.ThrowIfCancellationRequested();

            if (!WorkspaceIsolationConfigurationMatches(workspace))
            {
                SetError(
                    "Workspace isolation or host mounts changed while the workspace was opening. "
                    + "Open it again to use the saved configuration.");
                return false;
            }

            await WorkspaceAutoSave.FlushAsync();
            if (_shutdownStarted)
            {
                return false;
            }

            if (!WorkspaceIsolationConfigurationMatches(workspace))
            {
                SetError(
                    "Workspace isolation or host mounts changed while the workspace was opening. "
                    + "Open it again to use the saved configuration.");
                return false;
            }

            runtime = new RuntimeWorkspaceViewModel(
                runtimeId,
                workspace.Name,
                WorkspaceTints.Of(workspace),
                ResolveWorkspaceConnections(workspace),
                CurrentAgentPolicyProvenance().WithOverride(
                    workspace.AgentPolicyOverride,
                    workspace.Key,
                    storedWorkspace.Revision),
                workspace.IsIsolated
                    ? TerminalMultiplexingMode.Disabled
                    : workspace.TerminalMultiplexingOverride,
                isolation.Binding);
            _runtimeSources[runtime.Id] = new RuntimeHistorySource(
                workspace.Key,
                workspace.Name);
            RegisterWorkspaceConnectionRuntime(runtime);
            if (runtime.TerminalMultiplexingMode is { } workspaceMultiplexingOverride)
            {
                _workspaceTerminalMultiplexingModes[runtime.Id] = workspaceMultiplexingOverride;
            }

            foreach (var entry in workspace.Entries)
            {
                switch (entry)
                {
                    case WorkspaceEntry.ConnectionReference connectionReference:
                        var connection = FindConnection(connectionReference.ConnectionId);
                        if (connection is not null)
                        {
                            runtime.Tabs.Add(CreateConnectionTab(
                                runtime.Id,
                                connection,
                                connectionReference.Alias,
                                runtime.AgentPolicy));
                        }

                        break;
                    case WorkspaceEntry.ScreenReference screenReference:
                        var storedScreen = _catalog.Snapshot.Screens
                            .SingleOrDefault(
                                item => item.Value.Id == screenReference.ScreenId);
                        if (storedScreen is not null)
                        {
                            var screen = storedScreen.Value;
                            runtime.Tabs.Add(CreateRuntimeTab(
                                runtime.Id,
                                screenReference.Alias ?? screen.Name,
                                "Saved screen",
                                screen.LayoutId,
                                screen.Panels,
                                screen.Key,
                                screen.Name,
                                runtime.AgentPolicy.WithOverride(
                                    screen.AgentPolicyOverride,
                                    screen.Key,
                                    storedScreen.Revision)));
                        }

                        break;
                    case WorkspaceEntry.Tab tab:
                        runtime.Tabs.Add(CreateRuntimeTab(
                            runtime.Id,
                            tab.Name,
                            "Workspace tab",
                            tab.LayoutId,
                            tab.Panels,
                            workspace.Key,
                            workspace.Name,
                            runtime.AgentPolicy));
                        break;
                }
            }

            if (runtime.Tabs.Count == 0)
            {
                var firstConnection = ResolveWorkspaceConnectionDefinitions(workspace).FirstOrDefault()
                    ?? _catalog.Snapshot.Connections.FirstOrDefault()?.Value;
                if (firstConnection is not null)
                {
                    runtime.Tabs.Add(CreateConnectionTab(
                        runtime.Id,
                        firstConnection,
                        agentPolicy: runtime.AgentPolicy));
                }
            }

            if (runtime.Tabs.Count == 0)
            {
                runtime.Tabs.Add(CreateLauncherTab());
            }

            runtime.ActiveTab = runtime.Tabs[0];
            MarkWorkspaceGraphRegistrationAttempt(runtime.Id);
            if (!await RegisterRuntimeWorkspaceAsync(runtime, operationCancellation))
            {
                return false;
            }

            if (!WorkspaceIsolationConfigurationMatches(workspace))
            {
                SetError(
                    "Workspace isolation or host mounts changed while the workspace was opening. "
                    + "The stale runtime was discarded; open it again to use the saved configuration.");
                return false;
            }

            if (_shutdownStarted || operationCancellation.IsCancellationRequested)
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, workspace.Key, workspace.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        catch (OperationCanceledException) when (_runtimeGraphLifetime.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            try
            {
                var retained = runtime is not null
                    && _openWorkspaces.Contains(runtime);
                var graphRollbackConfirmed = true;
                if (retained)
                {
                    RetainWorkspaceGraph(runtime!.Id);
                }
                else if (runtime is not null
                    && RequiresWorkspaceGraphRollback(runtime.Id))
                {
                    graphRollbackConfirmed = await TryRollbackWorkspaceGraphAsync(runtime.Id);
                }

                if (!retained && graphRollbackConfirmed)
                {
                    _workspaceDefinitionOccupancy.Unregister(WindowId, runtimeId);
                }

                if (runtime is not null)
                {
                    DisposeRuntimeWorkspaceUnlessOwned(runtime);
                }

                if (!retained
                    && graphRollbackConfirmed
                    && isolation.Binding is { } binding)
                {
                    var releaseError = await ReleasePreparedWorkspaceIsolationAsync(
                        binding,
                        CancellationToken.None);
                    if (releaseError is not null)
                    {
                        SetError(releaseError.Message);
                    }
                }
            }
            finally
            {
                CompleteWorkspaceIsolationStartup(activationId);
                CompleteWorkspaceActivation(activationId, activationCompletion);
            }
        }
    }

    public Task<bool> SelectWorkspaceAtPositionAsync(
        int position,
        CancellationToken cancellationToken = default)
    {
        if (position < 0 || position >= Workspaces.Count)
        {
            SetError($"Workspace position {position + 1} is not available.");
            return Task.FromResult(false);
        }

        return OpenWorkspaceAsync(Workspaces[position].Id, cancellationToken);
    }

    public async Task<bool> OpenConnectionAsync(
        ConnectionId connectionId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        var connection = FindConnection(connectionId);
        if (connection is null)
        {
            SetError(ConnectionUnavailableMessage(connectionId));
            return false;
        }

        var launchItem = Connections.SingleOrDefault(item => item.Id == connectionId);
        if (launchItem is not { CanOpen: true })
        {
            SetError(launchItem?.Status ?? "That connection is unavailable on this platform.");
            return false;
        }

        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            connection.Name,
            ThemePreference.BronzeFallback.ToString(),
            [.. Connections.Where(item => item.Id == connection.Id)],
            CurrentAgentPolicyProvenance());
        try
        {
            RegisterWorkspaceConnectionRuntime(runtime);
            var defaultPanel = connection.PanelLaunchCapabilities.DefaultPanel;
            runtime.Tabs.Add(defaultPanel == PanelKind.Terminal
                ? CreateConnectionTab(runtime.Id, connection)
                : CreateConnectionPanelTab(
                    runtime.Id,
                    connection,
                    defaultPanel,
                    runtime.AgentPolicy));
            runtime.ActiveTab = runtime.Tabs[0];
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, connection.Key, connection.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public async Task<bool> OpenScreenAsync(
        ScreenId screenId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        var storedScreen = _catalog.Snapshot.Screens
            .SingleOrDefault(item => item.Value.Id == screenId);
        if (storedScreen is null)
        {
            SetError("That saved screen no longer exists.");
            return false;
        }

        var screen = storedScreen.Value;
        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            screen.Name,
            ThemePreference.BronzeFallback.ToString(),
            [.. Connections],
            CurrentAgentPolicyProvenance());
        try
        {
            RegisterWorkspaceConnectionRuntime(runtime);
            runtime.Tabs.Add(CreateRuntimeTab(
                runtime.Id,
                screen.Name,
                "Saved screen",
                screen.LayoutId,
                screen.Panels,
                screen.Key,
                screen.Name,
                runtime.AgentPolicy.WithOverride(
                    screen.AgentPolicyOverride,
                    screen.Key,
                    storedScreen.Revision)));
            runtime.ActiveTab = runtime.Tabs[0];
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, screen.Key, screen.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public Task<bool> LaunchConnectionAsync(
        ConnectionId connectionId,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeWorkspace is null)
        {
            return OpenConnectionAsync(connectionId, cancellationToken);
        }

        // A Git connection opens its Git panel by default; every other
        // connection still opens a terminal tab.
        var panel = FindConnection(connectionId)?.PanelLaunchCapabilities.DefaultPanel
            ?? PanelKind.Terminal;
        return AddConnectionPanelTabAsync(connectionId, panel, cancellationToken);
    }

    /// <summary>
    /// A saved screen brings its own tab. Asked for from a tab that is nothing
    /// but the launcher, it takes that tab over rather than opening beside it:
    /// the launcher tab was the question, and this is the answer.
    /// </summary>
    public Task<bool> LaunchScreenAsync(
        ScreenId screenId,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeWorkspace is null)
        {
            return OpenScreenAsync(screenId, cancellationToken);
        }

        return RuntimeWorkspace.ActiveTab is { } tab && IsLauncherTab(tab)
            ? ReplaceScreenTabAsync(tab, screenId, cancellationToken)
            : AddScreenTabAsync(screenId, cancellationToken);
    }

    /// <summary>Opens a saved file provider in a tab, like a terminal connection.</summary>
    public async Task<bool> LaunchFileProviderAsync(
        FileProviderProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        var stored = _catalog.Snapshot.FileProviderProfiles
            .SingleOrDefault(item => item.Value.Id == profileId);
        if (stored is null)
        {
            SetError("That file connection no longer exists.");
            return false;
        }

        var profile = stored.Value;
        if (RuntimeWorkspace is not null)
        {
            return await AddFileProviderTabAsync(
                profile.Id,
                PanelKind.FileViewer,
                cancellationToken);
        }

        var runtimeWorkspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            profile.Name,
            ThemePreference.BronzeFallback.ToString(),
            [],
            CurrentAgentPolicyProvenance());
        try
        {
            RegisterWorkspaceConnectionRuntime(runtimeWorkspace);
            var tab = CreateFileProviderTab(runtimeWorkspace.Id, profile);
            if (tab is null)
            {
                return false;
            }

            runtimeWorkspace.Tabs.Add(tab);
            runtimeWorkspace.ActiveTab = tab;
            if (!await RegisterRuntimeWorkspaceAsync(runtimeWorkspace, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtimeWorkspace, null, runtimeWorkspace.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtimeWorkspace);
        }
    }

    /// <summary>Opens a saved database connection in a tab.</summary>
    public async Task<bool> LaunchSavedDatabaseAsync(
        DatabaseConnectionProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (_databaseConnectionCatalog is null)
        {
            SetError("The database drivers are unavailable in this build.");
            return false;
        }

        var profile = FindDatabaseConnection(profileId);
        if (profile is null)
        {
            SetError("That database connection no longer exists.");
            return false;
        }

        if (RuntimeWorkspace is { } workspace)
        {
            return await AppendRuntimeTabAsync(
                workspace,
                runtime => CreateSavedDatabaseTab(profile, runtime.Id),
                "database connection tab creation",
                cancellationToken);
        }

        var runtimeWorkspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            profile.Name,
            ThemePreference.BronzeFallback.ToString(),
            [],
            CurrentAgentPolicyProvenance());
        try
        {
            RegisterWorkspaceConnectionRuntime(runtimeWorkspace);
            var tab = CreateSavedDatabaseTab(profile, runtimeWorkspace.Id);
            runtimeWorkspace.Tabs.Add(tab);
            runtimeWorkspace.ActiveTab = tab;
            if (!await RegisterRuntimeWorkspaceAsync(runtimeWorkspace, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtimeWorkspace, null, runtimeWorkspace.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtimeWorkspace);
        }
    }

    private RuntimeTabViewModel CreateSavedDatabaseTab(
        DatabaseConnectionProfile profile,
        WorkspaceInstanceId? workspaceId = null)
    {
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            profile.Name,
            "Database");
        var panel = CreateDatabasePanelFromTarget(
            PanelInstanceId.New(),
            profile.Name,
            SavedDatabaseTargetPrefix + profile.Id.Value,
            recoveredTunnel: null,
            workspaceId: workspaceId);
        AddPanelOrDispose(tab, panel);
        return tab;
    }

    public async Task<bool> OpenLocalMonitorWorkspaceAsync(
        PanelKind kind,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (kind is not (PanelKind.Statistics or PanelKind.ProcessMonitor))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Local system",
            ThemePreference.BronzeFallback.ToString(),
            [],
            CurrentAgentPolicyProvenance());
        try
        {
            RegisterWorkspaceConnectionRuntime(runtime);
            var tab = new RuntimeTabViewModel(
                TabInstanceId.New(),
                kind == PanelKind.Statistics ? "Statistics" : "Processes",
                "Local host");
            var panel = CreateMonitorPanel(
                runtime.Id,
                tab.Id,
                PanelInstanceId.New(),
                kind == PanelKind.Statistics ? "Statistics" : "Process Monitor",
                kind);
            tab.AddPanel(panel);
            runtime.Tabs.Add(tab);
            runtime.ActiveTab = tab;
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, null, runtime.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public async Task<bool> OpenLocalDatabaseWorkspaceAsync(
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (_databaseConnectionCatalog is null)
        {
            SetError("The database drivers are unavailable in this build.");
            return false;
        }

        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Database",
            ThemePreference.BronzeFallback.ToString(),
            [],
            CurrentAgentPolicyProvenance());
        try
        {
            RegisterWorkspaceConnectionRuntime(runtime);
            var tab = new RuntimeTabViewModel(
                TabInstanceId.New(),
                "Database",
                "Local");
            var panel = CreateDatabasePanel(
                PanelInstanceId.New(),
                "Database",
                workspaceId: runtime.Id);
            tab.AddPanel(panel);
            runtime.Tabs.Add(tab);
            runtime.ActiveTab = tab;
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, null, runtime.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public Task<bool> OpenLocalBrowserWorkspaceAsync(
        CancellationToken cancellationToken = default) =>
        OpenLocalBrowserWorkspaceAsync(profileId: null, cancellationToken);

    public async Task<bool> OpenLocalBrowserWorkspaceAsync(
        BrowserProfileId? profileId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (_browserRendererViewFactory is null)
        {
            SetError("The embedded browser is unavailable in this build.");
            return false;
        }

        var runtime = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Browser",
            ThemePreference.BronzeFallback.ToString(),
            [],
            CurrentAgentPolicyProvenance());
        try
        {
            RegisterWorkspaceConnectionRuntime(runtime);
            var tab = new RuntimeTabViewModel(
                TabInstanceId.New(),
                "Browser",
                "Local");
            var panel = CreateBrowserPanel(
                runtime.Id,
                tab.Id,
                PanelInstanceId.New(),
                "Browser",
                BrowserAddress.Blank,
                requestedProfileId: profileId);
            if (panel is not BrowserRuntimePanelViewModel)
            {
                SetError(BrowserCreationFailure(panel));
                panel.Dispose();
                return false;
            }

            tab.AddPanel(panel);
            runtime.Tabs.Add(tab);
            runtime.ActiveTab = tab;
            if (!await RegisterRuntimeWorkspaceAsync(runtime, cancellationToken))
            {
                return false;
            }

            ActivateRuntimeWorkspace(runtime, null, runtime.Name);
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        finally
        {
            DisposeRuntimeWorkspaceUnlessOwned(runtime);
        }
    }

    public async Task<bool> ActivateTabAsync(
        TabInstanceId tabId,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        if (workspace is null || workspace.Tabs.All(tab => tab.Id != tabId))
        {
            return false;
        }

        return await RuntimeGraph.ActivateTabAsync(
            workspace,
            tabId,
            cancellationToken);
    }

    public async Task<bool> ActivatePanelAsync(
        PanelInstanceId panelId,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(item =>
            item.Panels.Any(panel => panel.Id == panelId));
        if (workspace is null || tab is null)
        {
            return false;
        }

        return await RuntimeGraph.ActivatePanelAsync(
            workspace,
            panelId,
            cancellationToken);
    }

    public async Task<bool> LoadSessionRestorePreferenceAsync(
        CancellationToken cancellationToken = default)
    {
        if (_sessionRestoreCoordinator is null)
        {
            return true;
        }

        var result = await _sessionRestoreCoordinator.ReadPreferenceAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            SetError("The session restore preference could not be loaded.");
            return false;
        }

        RestoreSessionsOnStart = result.Value;
        _sessionRestorePreferenceLoaded = true;
        OnPropertyChanged(nameof(CanChangeRestoreSessionsOnStart));
        return true;
    }

    public async Task<bool> SetRestoreSessionsOnStartAsync(
        bool restoreSessionsOnStart,
        CancellationToken cancellationToken = default)
    {
        if (_sessionRestoreCoordinator is null)
        {
            RestoreSessionsOnStart = restoreSessionsOnStart;
            return true;
        }

        if (!_sessionRestorePreferenceLoaded || _sessionRestorePreferenceSaving)
        {
            return false;
        }

        _sessionRestorePreferenceSaving = true;
        OnPropertyChanged(nameof(CanChangeRestoreSessionsOnStart));
        try
        {
            var result = await _sessionRestoreCoordinator.WritePreferenceAsync(
                restoreSessionsOnStart,
                cancellationToken);
            if (!result.IsSuccess)
            {
                SetError("The session restore preference could not be saved.");
                OnPropertyChanged(nameof(RestoreSessionsOnStart));
                return false;
            }

            RestoreSessionsOnStart = restoreSessionsOnStart;
            return true;
        }
        finally
        {
            _sessionRestorePreferenceSaving = false;
            OnPropertyChanged(nameof(CanChangeRestoreSessionsOnStart));
        }
    }

    public async Task<bool> LoadTerminalMultiplexingAsync(
        CancellationToken cancellationToken = default)
    {
        return await TerminalContinuity.LoadAsync(cancellationToken);
    }

    public async Task<bool> SetUseTerminalMultiplexingForSshTerminalsAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return await TerminalContinuity.SetUseForSshTerminalsAsync(
            enabled,
            cancellationToken);
    }

    public async Task TerminateManagedRemoteSessionAsync(
        ManagedRemoteSessionViewModel item,
        CancellationToken cancellationToken = default)
    {
        await TerminalContinuity.TerminateAsync(item, cancellationToken);
    }

    public async Task ForgetManagedRemoteSessionAsync(
        ManagedRemoteSessionViewModel item,
        CancellationToken cancellationToken = default)
    {
        await TerminalContinuity.ForgetAsync(item, cancellationToken);
    }

    /// <summary>
    /// Opens Main on its launcher when the window has come up with nothing in it.
    ///
    /// Main always exists, so there is always somewhere for the launcher to
    /// live. It defers to everything that has a better claim on the window: a
    /// restored session or an overlay. First-run onboarding is part of the
    /// launcher, so it needs this workspace rather than blocking it.
    /// </summary>
    public async Task<bool> OpenDefaultLauncherIfIdleAsync(
        CancellationToken cancellationToken = default)
    {
        if (RuntimeWorkspace is not null
            || HasOverlay)
        {
            return false;
        }

        var main = Workspaces.FirstOrDefault(item => string.Equals(
            item.Id.Value,
            WorkspaceDefinition.DefaultWorkspaceId,
            StringComparison.Ordinal));
        if (main is null
            || !await OpenWorkspaceAsync(main.Id, cancellationToken))
        {
            return false;
        }

        if (RuntimeWorkspace?.ActiveTab is { } activeTab
            && IsLauncherTab(activeTab))
        {
            return true;
        }

        return await AddLauncherTabAsync(cancellationToken);
    }

    public async Task<bool> RestoreSessionOnStartupAsync(
        CancellationToken cancellationToken = default)
    {
        _ = await LoadTerminalMultiplexingAsync(cancellationToken);
        if (_sessionRestoreCoordinator is null)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "recovery.startup.unavailable",
                SecretSafeDiagnosticKind.Unexpected);
            return false;
        }

        if (!await LoadSessionRestorePreferenceAsync(cancellationToken))
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "recovery.startup-preference.load-failed",
                SecretSafeDiagnosticKind.Unexpected);
            return false;
        }

        if (!RestoreSessionsOnStart
            || RuntimeWorkspace is not null
            || HasOverlay)
        {
            return false;
        }

        var result = await _sessionRestoreCoordinator.LoadLatestSessionAsync(
            cancellationToken);
        if (!result.IsSuccess)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "recovery.previous-session.lookup-failed",
                SecretSafeDiagnosticKind.Unexpected);
            SetError("The previous session could not be loaded.");
            return false;
        }

        if (RuntimeWorkspace is not null
            || HasOverlay)
        {
            return false;
        }

        return await RestoreRuntimeSnapshotsAsync(result.Value!, cancellationToken);
    }

    /// <summary>
    /// Opens the window on stored runtime state. The one way in: how the run
    /// that wrote these snapshots ended is not a question anyone asks here,
    /// because the answer never changed what gets opened.
    /// </summary>
    public async Task<bool> RestoreRuntimeSnapshotsAsync(
        IReadOnlyList<RuntimeRecoverySnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (_shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested)
        {
            return false;
        }

        using var operationLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        var operationCancellation = operationLifetime.Token;
        var snapshot = snapshots
            .Where(item => string.Equals(item.Key, RuntimeWorkspaceRecoveryCodec.SnapshotKey, StringComparison.Ordinal))
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        if (snapshot is null)
        {
            return false;
        }

        if (!RuntimeWorkspaceRecoveryCodec.TryDeserialize(
                snapshot,
                out var payload,
                out var error))
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "recovery.previous-session.payload-rejected",
                SecretSafeDiagnosticKind.Unexpected);
            SetError(error ?? "Runtime recovery state could not be read.");
            return false;
        }

        if (payload!.Workspace is null)
        {
            if (_shutdownStarted || operationCancellation.IsCancellationRequested)
            {
                return false;
            }

            RuntimeWorkspace = null;
            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }

        var runtimeId = WorkspaceInstanceId.New();
        var recoveredSource = payload.Workspace.HistorySource?.ToHistorySource();
        var recoveredWorkspaceDefinition = recoveredSource is
        { SourceDefinition.Kind: var sourceKind }
            && sourceKind == WorkspaceDefinition.Kind
                ? recoveredSource.SourceDefinition
                : (DefinitionKey?)null;
        if (recoveredWorkspaceDefinition is { } definition
            && !_workspaceDefinitionOccupancy.TryRegisterRuntime(
                WindowId,
                runtimeId,
                definition))
        {
            SetError(
                "This workspace is already opening in this window, or its isolation settings "
                + "are being saved. Wait for that operation to finish, then restore again.");
            return false;
        }

        if (!TryBeginWorkspaceActivation(
            out var activationId,
            out var activationCompletion))
        {
            if (recoveredWorkspaceDefinition is not null)
            {
                _workspaceDefinitionOccupancy.Unregister(WindowId, runtimeId);
            }

            return false;
        }

        RuntimeWorkspaceViewModel? runtime = null;
        WorkspaceIsolationBinding? preparedIsolationBinding = null;
        try
        {
            var isolation = await PrepareRecoveredWorkspaceIsolationAsync(
                payload.Workspace,
                operationCancellation);
            if (!isolation.Succeeded || _shutdownStarted)
            {
                return false;
            }

            preparedIsolationBinding = isolation.Binding;
            operationCancellation.ThrowIfCancellationRequested();
            if (!RecoveredWorkspaceIsolationConfigurationMatches(payload.Workspace))
            {
                SetError(
                    "Workspace isolation or host mounts changed while recovery was preparing. "
                    + "Discard the recovery snapshot or restore it again with the saved configuration.");
                return false;
            }

            runtime = RestoreWorkspace(
                payload.Workspace,
                runtimeId,
                isolation.Binding);
            MarkWorkspaceGraphRegistrationAttempt(runtime.Id);
            if (!await RegisterRuntimeWorkspaceAsync(runtime, operationCancellation))
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "recovery.workspace.registration-rejected",
                    SecretSafeDiagnosticKind.Unexpected);
                return false;
            }

            if (!RecoveredWorkspaceIsolationConfigurationMatches(payload.Workspace))
            {
                SetError(
                    "Workspace isolation or host mounts changed while recovery was registering. "
                    + "The stale runtime was discarded; restore it again with the saved configuration.");
                return false;
            }

            if (_shutdownStarted || operationCancellation.IsCancellationRequested)
            {
                return false;
            }

            // Through ActivateRuntimeWorkspace, not around it. Restoring by
            // hand left the workspace out of the open set and with no record of
            // which definition it came from, so clicking its own rail tile
            // built a second copy and the terminals it was restored with were
            // replaced by fresh shells.
            if (payload.Workspace.HistorySource?.ToHistorySource() is { } restoredSource)
            {
                ActivateRuntimeWorkspace(
                    runtime,
                    restoredSource.SourceDefinition,
                    restoredSource.DurableTitle);
            }
            else
            {
                // Restored from a snapshot written before workspaces recorded
                // where they came from. It cannot be found again by definition,
                // but it must still be in the open set.
                ActivateRuntimeWorkspace(runtime, null, runtime.Name);
            }

            Route = ShellRoute.Workspace;
            QueueRuntimeRecoverySnapshot();
            return true;
        }
        catch (ArgumentException exception)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "recovery.target.invalid",
                exception);
            SetError("Runtime recovery state contains an invalid target.");
            return false;
        }
        catch (InvalidOperationException exception)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "recovery.apply.failed",
                exception);
            SetError("Runtime recovery state could not be applied.");
            return false;
        }
        catch (OperationCanceledException) when (_runtimeGraphLifetime.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            try
            {
                var retained = runtime is not null
                    && _openWorkspaces.Contains(runtime);
                var graphRollbackConfirmed = true;
                if (retained)
                {
                    RetainWorkspaceGraph(runtime!.Id);
                }
                else if (runtime is not null
                    && RequiresWorkspaceGraphRollback(runtime.Id))
                {
                    graphRollbackConfirmed = await TryRollbackWorkspaceGraphAsync(runtime.Id);
                }

                if (!retained
                    && graphRollbackConfirmed
                    && recoveredWorkspaceDefinition is not null)
                {
                    _workspaceDefinitionOccupancy.Unregister(WindowId, runtimeId);
                }

                if (runtime is not null)
                {
                    DisposeRuntimeWorkspaceUnlessOwned(runtime);
                }

                if (!retained
                    && graphRollbackConfirmed
                    && preparedIsolationBinding is { } binding)
                {
                    var releaseError = await ReleasePreparedWorkspaceIsolationAsync(
                        binding,
                        CancellationToken.None);
                    if (releaseError is not null)
                    {
                        SetError(releaseError.Message);
                    }
                }
            }
            finally
            {
                CompleteWorkspaceActivation(activationId, activationCompletion);
            }
        }
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>> CreateWorkspaceAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await WorkspaceSettings.CreateAsync(name, cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
        SetWorkspaceIsolationAsync(
            LauncherWorkspaceViewModel workspace,
            bool isIsolated,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ClearError();
        var openInstance = OpenWorkspaceInstance(workspace.Id);
        if (openInstance is { } instanceId
            && !await StopWorkspaceForConfigurationRestartAsync(
                instanceId,
                cancellationToken))
        {
            return WorkspaceConfigurationRestartFailure();
        }

        var result = await WorkspaceSettings.SetIsolationAsync(
            workspace.Id,
            isIsolated,
            cancellationToken);
        ApplyError(result.Error);
        if (openInstance is not null)
        {
            // A failed save leaves the durable definition unchanged, so this
            // also restores the original execution boundary.
            _ = await OpenWorkspaceAsync(workspace.Id, cancellationToken);
        }

        return result;
    }

    public bool WorkspaceEditorConfigurationChangeRequiresRestart(
        WorkspaceEditorSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Id == request.Definition.Id);
        return current is not null
            && FindOpenWorkspace(current.Value.Key) is not null
            && (current.Value.IsIsolated != request.Definition.IsIsolated
                || !current.Value.IsolationMounts.SequenceEqual(
                    request.Definition.IsolationMounts)
                || !string.Equals(
                    current.Value.IsolationImageReference,
                    request.Definition.IsolationImageReference,
                    StringComparison.Ordinal));
    }

    public bool WorkspaceEditorImageChangeRebuildsIsolate(
        WorkspaceEditorSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Id == request.Definition.Id);
        return current is not null
            && current.Value.IsIsolated
            && request.Definition.IsIsolated
            && !string.Equals(
                current.Value.IsolationImageReference,
                request.Definition.IsolationImageReference,
                StringComparison.Ordinal);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
        SaveWorkspaceEditorRestartingAsync(
            WorkspaceEditorSaveRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var openInstance = OpenWorkspaceInstance(request.Definition.Id);
        if (openInstance is { } instanceId
            && !await StopWorkspaceForConfigurationRestartAsync(
                instanceId,
                cancellationToken))
        {
            return WorkspaceConfigurationRestartFailure();
        }

        var result = await SaveWorkspaceEditorAsync(request, cancellationToken);
        if (openInstance is not null)
        {
            _ = await OpenWorkspaceAsync(request.Definition.Id, cancellationToken);
        }

        return result;
    }

    public async Task<bool> RecreateWorkspaceIsolationAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        var stored = _catalog.Snapshot.Workspaces
            .FirstOrDefault(item => item.Value.Id == workspaceId);
        if (stored is null || !stored.Value.IsIsolated)
        {
            SetError("Choose a saved isolated workspace to recreate its environment.");
            return false;
        }

        var provider = _workspaceIsolationProvider;
        if (provider is null)
        {
            SetError("Workspace isolation is unavailable on this host.");
            return false;
        }

        var imageReference = stored.Value.IsolationImageReference
            ?? ActiveIsolationImageReference(workspaceId);

        var openInstance = OpenWorkspaceInstance(workspaceId);
        try
        {
            if (openInstance is { } instanceId
                && !await StopWorkspaceForConfigurationRestartAsync(
                    instanceId,
                    cancellationToken))
            {
                return false;
            }

            var operationId = Guid.NewGuid();
            BeginWorkspaceIsolationStartup(stored.Value, operationId);
            try
            {
                var result = await provider.RecreateAsync(
                    new WorkspaceIsolationPrepareRequest(
                        workspaceId,
                        IsolationMountsOf(stored.Value),
                        imageReference),
                    new Progress<WorkspaceIsolationProgress>(ReportWorkspaceIsolationProgress),
                    cancellationToken);
                if (result is WorkspaceIsolationResult<Unit>.Failure failure)
                {
                    await RestoreWorkspaceAfterFailedRecreateAsync(
                        workspaceId,
                        openInstance is not null);
                    SetError(failure.Error.Message);
                    return false;
                }
            }
            finally
            {
                CompleteWorkspaceIsolationStartup(operationId);
            }

            // Opening performs the pull/create/start sequence and shows each stage
            // in the workspace canvas. It also leaves the new environment running.
            return await OpenWorkspaceAsync(workspaceId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RestoreWorkspaceAfterFailedRecreateAsync(
                workspaceId,
                openInstance is not null);
            SetError("Workspace environment recreation was cancelled.");
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-isolation.recreate.failed",
                exception);
            await RestoreWorkspaceAfterFailedRecreateAsync(
                workspaceId,
                openInstance is not null);
            SetError("The workspace environment could not be recreated.");
            return false;
        }
    }

    private async Task RestoreWorkspaceAfterFailedRecreateAsync(
        WorkspaceId workspaceId,
        bool wasOpen)
    {
        if (!wasOpen
            || _shutdownStarted
            || OpenWorkspaceInstance(workspaceId) is not null)
        {
            return;
        }

        try
        {
            _ = await OpenWorkspaceAsync(workspaceId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-isolation.recreate-restore.failed",
                exception);
        }
    }

    private async Task<bool> StopWorkspaceForConfigurationRestartAsync(
        WorkspaceInstanceId instanceId,
        CancellationToken cancellationToken)
    {
        var close = await CloseWorkspaceAsync(
            instanceId,
            CloseDecision.Confirm,
            cancellationToken);
        if (close is HostResult<CloseScopeResult>.Failure failure)
        {
            SetError(failure.Error.Message);
            return false;
        }

        if (((HostResult<CloseScopeResult>.Success)close).Value
            is not CloseScopeResult.Completed completed
            || completed.Scope != CloseScopeKind.Workspace
            || !string.Equals(completed.TargetId, instanceId.Value, StringComparison.Ordinal)
            || completed.Sessions.Any(session => session.Outcome is not (
                SessionCloseOutcome.GracefullyClosed
                or SessionCloseOutcome.ForceTerminated
                or SessionCloseOutcome.AlreadyClosed)))
        {
            SetError("The workspace could not be restarted to change isolation configuration.");
            return false;
        }

        RemoveRuntimeWorkspace(instanceId);
        return true;
    }

    private DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>
        WorkspaceConfigurationRestartFailure() =>
        DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Failure(
            new DefinitionStoreError(
                DefinitionStoreErrorCode.InvalidDefinition,
                OperationError
                ?? "The workspace could not be restarted to change isolation configuration."));

    public ConnectionEditorViewModel CreateConnectionEditor(ConnectionId? connectionId = null)
        => TerminalConnectionSettings.CreateEditor(connectionId);

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ConnectionProfile>>> SaveConnectionAsync(
        ConnectionEditorSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        var result = await TerminalConnectionSettings.SaveAsync(request, cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public FileProviderProfileEditorViewModel CreateFileProviderEditor(
        FileProviderProfileId? profileId = null) =>
        FileProviderSettings.CreateEditor(profileId);

    /// <summary>
    /// Builds the single editor that covers every connection family. A locked
    /// family (used when editing an existing definition) restricts the type
    /// selector to that family; otherwise every available family contributes.
    /// </summary>
    public UnifiedConnectionEditorViewModel CreateUnifiedConnectionEditor(
        SavedConnectionFamily? lockedFamily = null,
        ConnectionId? terminalConnectionId = null,
        FileProviderProfileId? fileProfileId = null,
        DatabaseConnectionProfileId? databaseProfileId = null,
        SavedConnectionFamily initialFamily = SavedConnectionFamily.Terminal)
    {
        var terminal = CreateConnectionEditor(terminalConnectionId);
        FileProviderProfileEditorViewModel? files = null;
        if (_fileProviderRuntime is not null
            && lockedFamily is null or SavedConnectionFamily.Files)
        {
            files = CreateFileProviderEditor(fileProfileId);
        }

        DatabaseConnectionEditorViewModel? database = null;
        if (_databaseConnectionCatalog is not null
            && lockedFamily is null or SavedConnectionFamily.Database)
        {
            DatabaseConnectionProfile? existing = null;
            if (databaseProfileId is { } databaseId)
            {
                existing = FindDatabaseConnection(databaseId)
                    ?? throw new InvalidOperationException(
                        "That database connection no longer exists.");
            }

            database = new DatabaseConnectionEditorViewModel(
                _databaseConnectionCatalog,
                [.. _catalog.Snapshot.Connections.Select(item => item.Value)],
                existing,
                existing?.PasswordSecret is { } storedSecret
                    ? token => ResolveDatabasePasswordAsync(storedSecret, token)
                    : null);
        }

        return new UnifiedConnectionEditorViewModel(
            terminal,
            files,
            database,
            lockedFamily,
            initialFamily);
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<FileProviderProfile>>>
        SaveFileProviderProfileAsync(
            FileProviderProfileSaveRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        var result = await FileProviderSettings.SaveAsync(request, cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public AiProviderProfileEditorViewModel CreateAiProviderEditor(
        AiProviderProfileId? profileId = null) =>
        AiProviderSettings.CreateEditor(profileId);

    public async ValueTask<DefinitionStoreResult<StoredDefinition<AiProviderProfile>>>
        SaveAiProviderProfileAsync(
            AiProviderProfileSaveRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        var result = await AiProviderSettings.SaveAsync(request, cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public McpServerProfileEditorViewModel CreateMcpServerEditor(
        McpServerProfileId? profileId = null) =>
        McpServerSettings.CreateEditor(profileId);

    public async ValueTask<DefinitionStoreResult<StoredDefinition<McpServerProfile>>>
        SaveMcpServerProfileAsync(
            McpServerProfileSaveRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        var result = await McpServerSettings.SaveAsync(request, cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask TestMcpServerAsync(
        McpServerProfileItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_mcpServerDiagnostics is null || !item.CanTest)
        {
            return;
        }

        // The row is updated in place to preserve keyboard focus, so freeze the
        // tested identity before awaiting a catalog or diagnostics change.
        var profileId = item.Id;
        var revision = item.Revision;
        var current = _catalog.Snapshot.McpServerProfiles
            .SingleOrDefault(stored =>
                stored.Value.Id == profileId);
        if (current is null || current.Revision != revision)
        {
            SetMcpServerTest(
                profileId,
                new McpServerTestPresentation(
                    revision,
                    McpServerTestPresentationState.Failed,
                    "Test unavailable",
                    "The MCP server changed before the test could start."));
            RefreshMcpServerDefinitions(_catalog.Snapshot);
            return;
        }

        if (!TryBeginMcpServerTest(profileId, revision))
        {
            return;
        }

        RefreshMcpServerDefinitions(_catalog.Snapshot);
        try
        {
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            var result = await _mcpServerDiagnostics.TestAsync(
                    new McpServerTestRequest(
                        profileId,
                        revision),
                    OperationContext.ForHuman(
                        ClientId,
                        expectedRevision: revision,
                        deadlineUtc: now + TimeSpan.FromSeconds(30)),
                    cancellationToken)
                .ConfigureAwait(true);
            switch (result)
            {
                case McpServerTestResult.Success success:
                    CompleteMcpServerTest(
                        profileId,
                        revision,
                        CreateMcpServerTestSuccess(
                            current,
                            success.Report));
                    break;
                case McpServerTestResult.Failure failure:
                    CompleteMcpServerTest(
                        profileId,
                        revision,
                        new McpServerTestPresentation(
                            revision,
                            McpServerTestPresentationState.Failed,
                            "Test failed",
                            failure.Error.Message));
                    break;
                default:
                    throw new InvalidOperationException(
                        "The MCP server test result is invalid.");
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CompleteMcpServerTest(
                profileId,
                revision,
                new McpServerTestPresentation(
                    revision,
                    McpServerTestPresentationState.Failed,
                    "Test cancelled",
                    "The MCP server test was cancelled."));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            CompleteMcpServerTest(
                profileId,
                revision,
                new McpServerTestPresentation(
                    revision,
                    McpServerTestPresentationState.Failed,
                    "Test failed",
                    "The MCP diagnostics boundary could not complete the test."));
        }

        if (!_disposed)
        {
            RefreshMcpServerDefinitions(_catalog.Snapshot);
        }
    }

    public async ValueTask RefreshMcpServerDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        if (_mcpServerDiagnostics is null)
        {
            return;
        }

        await _mcpServerDiagnostics.RefreshAsync(cancellationToken)
            .ConfigureAwait(true);
        _mcpDiagnosticsSnapshot = _mcpServerDiagnostics.Snapshot;
        RefreshMcpServerDefinitions(_catalog.Snapshot);
        OnPropertyChanged(nameof(HasMcpDiagnosticHistory));
        OnPropertyChanged(nameof(IsMcpCleanupUncertain));
    }

    public async ValueTask ClearMcpServerDiagnosticHistoryAsync(
        CancellationToken cancellationToken)
    {
        if (_mcpServerDiagnostics is null)
        {
            return;
        }

        _ = await _mcpServerDiagnostics.ClearHistoryAsync(
                OperationContext.ForHuman(ClientId),
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async ValueTask<bool> CreateConnectionSecretAsync(
        ConnectionId connectionId,
        string label,
        SecretKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        return await SecretSettings.CreateConnectionAsync(
            connectionId,
            label,
            kind,
            value,
            cancellationToken);
    }

    public async ValueTask<bool> CreateFileProviderSecretAsync(
        FileProviderProfileId profileId,
        string label,
        SecretKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        return await SecretSettings.CreateFileProviderAsync(
            profileId,
            label,
            kind,
            value,
            cancellationToken);
    }

    public async ValueTask<bool> CreateAiProviderSecretAsync(
        AiProviderProfileId profileId,
        string label,
        string value,
        CancellationToken cancellationToken)
    {
        return await SecretSettings.CreateAiProviderAsync(
            profileId,
            label,
            value,
            cancellationToken);
    }

    public async ValueTask<bool> CreateMcpServerSecretAsync(
        McpServerSecretTargetViewModel target,
        string label,
        SecretKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        return await SecretSettings.CreateMcpServerAsync(
            target,
            label,
            kind,
            value,
            cancellationToken);
    }

    public async ValueTask<bool> DeleteSecretAsync(
        SecretMetadataViewModel secret,
        CancellationToken cancellationToken)
    {
        return await SecretSettings.DeleteAsync(secret, cancellationToken);
    }

    public async ValueTask<bool> RelabelSecretAsync(
        SecretMetadataViewModel secret,
        string label,
        CancellationToken cancellationToken)
    {
        return await SecretSettings.RelabelAsync(secret, label, cancellationToken);
    }

    public async ValueTask<bool> ReplaceSecretAsync(
        SecretMetadataViewModel secret,
        SecretMaterial replacement,
        CancellationToken cancellationToken)
    {
        return await SecretSettings.ReplaceAsync(
            secret,
            replacement,
            cancellationToken);
    }

    public async ValueTask<bool> CancelFileTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        ClearError();
        return await FileTransferState.CancelAsync(id, cancellationToken);
    }

    public async ValueTask<bool> QueueFileTransferAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken)
    {
        ClearError();
        return await FileTransferState.EnqueueAsync(request, cancellationToken);
    }

    public async ValueTask<bool> RetryFileTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        ClearError();
        return await FileTransferState.RetryAsync(id, cancellationToken);
    }

    public Task RefreshSecretsAsync(CancellationToken cancellationToken) =>
        SecretSettings.RefreshAsync(cancellationToken);

    public SavedScreenEditorViewModel CreateSavedScreenEditor(ScreenId screenId) =>
        SavedScreenSettings.CreateEditor(screenId);

    public SavedScreenEditorViewModel CreateNewSavedScreenEditor(string name) =>
        SavedScreenSettings.CreateNewEditor(name);

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>> SaveSavedScreenAsync(
        SavedScreenEditorSaveRequest request,
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await SavedScreenSettings.SaveAsync(request, cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>> SaveTerminalProfileAsync(
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await TerminalSettings.SaveTerminalProfileAsync(cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public string AppearancePreviewStatus
    {
        get => _appearancePreviewStatus;
        private set => SetProperty(ref _appearancePreviewStatus, value);
    }

    public bool HasThemeAppearanceDraft =>
        OwnsAppearancePreview && _appearancePreview.Current.HasThemeDraft;

    public bool HasTerminalAppearanceDraft =>
        OwnsAppearancePreview && _appearancePreview.Current.HasTerminalDraft;

    public bool HasUnresolvedAppearanceDrafts =>
        HasThemeAppearanceDraft || HasTerminalAppearanceDraft;

    private bool OwnsAppearancePreview =>
        string.Equals(
            _appearancePreview.Current.OwnerId,
            WindowId.Value,
            StringComparison.Ordinal);

    public bool BeginAppearanceEditing()
    {
        if (_disposed)
        {
            return false;
        }

        if (_appearancePreviewLease is { IsActive: true })
        {
            return true;
        }

        var snapshot = _catalog.Snapshot;
        var acquisition = _appearancePreview.TryAcquire(
            WindowId.Value,
            snapshot.Themes
                .FirstOrDefault(item => item.Value.Id == ThemePreference.Default.Id)
                ?.Revision,
            snapshot.TerminalProfiles
                .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?.Revision);
        _appearancePreviewLease = acquisition.Lease;
        AppearancePreviewStatus = acquisition.Conflict
            ?? "Preview changes, then apply or cancel each section independently.";
        PublishAppearanceDraftState();
        return acquisition.IsSuccess;
    }

    public bool PreviewAppearanceTheme(ThemePreference theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (!BeginAppearanceEditing()
            || _appearancePreviewLease?.PreviewTheme(theme) != true)
        {
            return false;
        }

        AppearancePreviewStatus = "Application appearance preview active.";
        PublishAppearanceDraftState();
        return true;
    }

    public bool PreviewTerminalAppearance(TerminalProfile terminalProfile)
    {
        ArgumentNullException.ThrowIfNull(terminalProfile);
        if (!BeginAppearanceEditing()
            || _appearancePreviewLease?.PreviewTerminal(
                TerminalRenderProfileSnapshot.FromProfile(terminalProfile)) != true)
        {
            return false;
        }

        AppearancePreviewStatus = "Terminal appearance preview active.";
        PublishAppearanceDraftState();
        return true;
    }

    public void CancelAppearanceThemeDraft()
    {
        _ = _appearancePreviewLease?.ClearTheme();
        ReleaseInactiveAppearanceLease();
        AppearancePreviewStatus = "Saved application appearance restored.";
        PublishAppearanceDraftState();
    }

    public void CancelTerminalAppearanceDraft()
    {
        _ = _appearancePreviewLease?.ClearTerminal();
        TerminalSettings.DiscardTerminalDraft();
        OnPropertyChanged(nameof(TerminalSettingsEditor));
        ReleaseInactiveAppearanceLease();
        AppearancePreviewStatus = "Saved terminal appearance restored.";
        PublishAppearanceDraftState();
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>>
        ApplyAppearanceThemeAsync(
            ThemePreference theme,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (!BeginAppearanceEditing() || _appearancePreviewLease is null)
        {
            return DefinitionStoreResult<StoredDefinition<ThemePreference>>.Failure(new(
                DefinitionStoreErrorCode.RevisionConflict,
                AppearancePreviewStatus));
        }

        ClearError();
        var result = await AppearanceSettings.SaveThemeAsync(
            theme,
            _appearancePreviewLease.BaselineThemeRevision,
            cancellationToken);
        ApplyError(result.Error);
        if (result.IsSuccess)
        {
            _ = _appearancePreviewLease.AdvanceThemeBaseline(result.Value!.Revision);
            _ = _appearancePreviewLease.ClearTheme();
            ReleaseInactiveAppearanceLease();
            AppearancePreviewStatus = "Application appearance saved.";
            PublishAppearanceDraftState();
        }

        return result;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<TerminalProfile>>>
        ApplyTerminalAppearanceAsync(CancellationToken cancellationToken)
    {
        if (!BeginAppearanceEditing() || _appearancePreviewLease is null)
        {
            return DefinitionStoreResult<StoredDefinition<TerminalProfile>>.Failure(new(
                DefinitionStoreErrorCode.RevisionConflict,
                AppearancePreviewStatus));
        }

        ClearError();
        var result = await TerminalSettings.SaveTerminalProfileAsync(
            cancellationToken,
            _appearancePreviewLease.BaselineTerminalRevision);
        ApplyError(result.Error);
        if (result.IsSuccess)
        {
            _ = _appearancePreviewLease.AdvanceTerminalBaseline(result.Value!.Revision);
            _ = _appearancePreviewLease.ClearTerminal();
            TerminalSettings.DiscardTerminalDraft();
            OnPropertyChanged(nameof(TerminalSettingsEditor));
            ReleaseInactiveAppearanceLease();
            AppearancePreviewStatus = "Terminal appearance saved.";
            PublishAppearanceDraftState();
        }

        return result;
    }

    public void ReleaseAppearanceEditing()
    {
        if (_appearancePreviewLease is null)
        {
            return;
        }

        // Window shutdown may arrive after this view model has completed its
        // own disposal. The process-wide lease still must be released, but its
        // editor has already crossed its lifetime boundary and must not be
        // queried or rebuilt.
        var mayRefreshEditor = !_disposed;
        var hadTerminalDraft = mayRefreshEditor && HasTerminalAppearanceDraft;
        _appearancePreviewLease.Dispose();
        _appearancePreviewLease = null;
        if (!mayRefreshEditor)
        {
            return;
        }

        if (hadTerminalDraft)
        {
            TerminalSettings.DiscardTerminalDraft();
            OnPropertyChanged(nameof(TerminalSettingsEditor));
        }

        AppearancePreviewStatus =
            "Preview changes, then apply or cancel each section independently.";
        PublishAppearanceDraftState();
    }

    private void ReleaseInactiveAppearanceLease()
    {
        if (_appearancePreviewLease is { IsActive: false })
        {
            _appearancePreviewLease = null;
        }
    }

    private void PublishAppearanceDraftState()
    {
        OnPropertyChanged(nameof(HasThemeAppearanceDraft));
        OnPropertyChanged(nameof(HasTerminalAppearanceDraft));
        OnPropertyChanged(nameof(HasUnresolvedAppearanceDrafts));
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<QuickTerminalSettings>>> SaveQuickTerminalSettingsAsync(
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await TerminalSettings.SaveQuickTerminalSettingsAsync(cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public void ApplyQuickTerminalRegistration(
        KeyStroke configuredGesture,
        KeyStroke? activeGesture,
        GlobalHotkeyRegistrationResult result) =>
        TerminalSettings.ApplyQuickTerminalRegistration(
            configuredGesture,
            activeGesture,
            result);

    public async ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>> CreateLayoutAsync(
        string name,
        int rows,
        int columns,
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await DefinitionSettings.CreateLayoutAsync(
            name,
            rows,
            columns,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<Unit>> DeleteLayoutAsync(
        LayoutId id,
        long revision,
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await DefinitionSettings.DeleteLayoutAsync(
            id,
            revision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<Unit>> DeleteKeymapAsync(
        KeymapProfileId id,
        long revision,
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await DefinitionSettings.DeleteKeymapAsync(
            id,
            revision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ThemePreference>>> SaveThemeAsync(
        AppearanceMode appearance,
        PlatformProfile platformProfile,
        AccentPreference accent,
        double? textScaleOverride,
        CancellationToken cancellationToken,
        ThemeChromePreference? chrome = null)
    {
        ClearError();
        var result = await AppearanceSettings.SaveThemeAsync(
            appearance,
            platformProfile,
            accent,
            textScaleOverride,
            cancellationToken,
            chrome);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<Unit>> SaveDefinitionEditAsync(
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = await DefinitionEdit.SaveAsync(cancellationToken);
        ApplyError(result.Error);
        if (result.IsSuccess)
        {
            CloseOverlay();
        }

        return result;
    }

    public async ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long revision,
        CancellationToken cancellationToken)
    {
        ClearError();
        var result = key.Kind switch
        {
            var kind when kind == LayoutDefinition.Kind =>
                await DefinitionSettings.DeleteLayoutAsync(
                    new LayoutId(key.Value),
                    revision,
                    cancellationToken),
            var kind when kind == KeymapProfile.Kind =>
                await DefinitionSettings.DeleteKeymapAsync(
                    new KeymapProfileId(key.Value),
                    revision,
                    cancellationToken),
            var kind when kind == ScreenDefinition.Kind =>
                await SavedScreenSettings.DeleteAsync(
                    new ScreenId(key.Value),
                    revision,
                    cancellationToken),
            _ => await DefinitionSettings.DeleteAsync(key, revision, cancellationToken),
        };
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<Unit>> DeleteSavedScreenAsync(
        DefinitionKey key,
        long revision,
        CancellationToken cancellationToken)
    {
        ClearError();
        if (key.Kind != ScreenDefinition.Kind)
        {
            var unsupported = DefinitionStoreResult<Unit>.Failure(new DefinitionStoreError(
                DefinitionStoreErrorCode.UnsupportedKind,
                "Only a saved screen can be deleted with saved-screen undo."));
            ApplyError(unsupported.Error);
            return unsupported;
        }

        var result = await SavedScreenSettings.DeleteAsync(
            new ScreenId(key.Value),
            revision,
            cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<ScreenDefinition>>>
        UndoSavedScreenDeleteAsync(CancellationToken cancellationToken)
    {
        ClearError();
        var result = await SavedScreenSettings.UndoDeleteAsync(cancellationToken);
        ApplyError(result.Error);
        return result;
    }

    public void DismissSavedScreenDeleteUndo() =>
        SavedScreenSettings.DismissDeleteUndo();

    public bool IsDefinitionOpen(DefinitionKey key) =>
        RuntimeWorkspace is not null && _runtimeHistorySource?.SourceDefinition == key;

    public async ValueTask<HostResult<CloseScopeResult>> ClosePanelAsync(
        PanelInstanceId panelId,
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        var multiplexed = OpenTerminalPanels().Where(panel => panel.Id == panelId).ToArray();
        CancelPendingConnections(multiplexed);
        var result = await RuntimeGraph.CloseAsync(
            CloseScopeRequest.Panel(panelId, decision),
            cancellationToken);
        RecordRecentSessionCompletions(result);
        if (result is HostResult<CloseScopeResult>.Success
            { Value: CloseScopeResult.Completed })
        {
            QueueMultiplexerTermination(multiplexed);
        }
        return result;
    }

    public async ValueTask<HostResult<CloseScopeResult>> CloseTabAsync(
        TabInstanceId tabId,
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        var multiplexed = _openWorkspaces
            .SelectMany(workspace => workspace.Tabs)
            .Where(tab => tab.Id == tabId)
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>()
            .ToArray();
        CancelPendingConnections(multiplexed);
        var result = await RuntimeGraph.CloseAsync(
            CloseScopeRequest.Tab(tabId, decision),
            cancellationToken);
        RecordRecentSessionCompletions(result);
        if (result is HostResult<CloseScopeResult>.Success
            { Value: CloseScopeResult.Completed })
        {
            QueueMultiplexerTermination(multiplexed);
        }
        return result;
    }

    /// <summary>
    /// Ends every session belonging to one workspace, and nothing else.
    ///
    /// Scoped to the workspace rather than the window because the window holds
    /// several of them: closing the one you are pointing at must leave the rest
    /// running.
    /// </summary>
    public async ValueTask<HostResult<CloseScopeResult>> CloseWorkspaceAsync(
        WorkspaceInstanceId workspaceId,
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        var runtimeWorkspace = _openWorkspaces.FirstOrDefault(
            workspace => workspace.Id == workspaceId);
        var multiplexed = _openWorkspaces
            .Where(workspace => workspace.Id == workspaceId)
            .SelectMany(workspace => workspace.Tabs)
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>()
            .ToArray();
        CancelPendingConnections(multiplexed);
        var result = await RuntimeGraph.CloseAsync(
            CloseScopeRequest.Workspace(workspaceId, decision),
            cancellationToken);
        RecordRecentSessionCompletions(result);
        if (result is HostResult<CloseScopeResult>.Success
            {
                Value: CloseScopeResult.Completed completed,
            })
        {
            QueueMultiplexerTermination(multiplexed);
            if (decision != CloseDecision.Cancel
                && completed.Scope == CloseScopeKind.Workspace
                && string.Equals(
                    completed.TargetId,
                    workspaceId.Value,
                    StringComparison.Ordinal)
                && completed.Sessions.All(session => session.Outcome is
                    SessionCloseOutcome.GracefullyClosed
                    or SessionCloseOutcome.ForceTerminated
                    or SessionCloseOutcome.AlreadyClosed)
                && runtimeWorkspace is not null
                && await ScheduleWorkspaceIsolationCleanup(runtimeWorkspace)
                    is { } releaseError)
            {
                SetError(releaseError.Message);
            }
        }
        return result;
    }

    public Task CloseDetachedMultiplexedTerminalAsync(
        TerminalRuntimePanelViewModel panel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(panel);
        cancellationToken.ThrowIfCancellationRequested();
        panel.CancelPendingConnection();
        QueueMultiplexerTermination([panel]);
        return Task.CompletedTask;
    }

    private IEnumerable<TerminalRuntimePanelViewModel> OpenTerminalPanels() =>
        _openWorkspaces
            .SelectMany(workspace => workspace.Tabs)
            .SelectMany(tab => tab.Panels)
            .OfType<TerminalRuntimePanelViewModel>();

    private static void CancelPendingConnections(
        IEnumerable<TerminalRuntimePanelViewModel> panels)
    {
        foreach (var panel in panels)
        {
            panel.CancelPendingConnection();
        }
    }

    private void QueueMultiplexerTermination(
        IEnumerable<TerminalRuntimePanelViewModel> panels)
    {
        if (_terminalMultiplexerCoordinator is null)
        {
            return;
        }

        var pending = panels
            .Where(panel => panel.MultiplexerSession is { IsEstablished: true })
            .ToArray();
        if (pending.Length > 0)
        {
            _ = TerminateMultiplexersInBackgroundAsync(pending);
        }
    }

    private async Task TerminateMultiplexersInBackgroundAsync(
        IReadOnlyList<TerminalRuntimePanelViewModel> panels)
    {
        try
        {
            await TerminateMultiplexersAsync(
                panels,
                _runtimeGraphLifetime.Token);
        }
        catch (OperationCanceledException) when (
            _runtimeGraphLifetime.IsCancellationRequested || _shutdownStarted)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "connections.multiplexer-cleanup.failed",
                exception);
        }
    }

    private async Task TerminateMultiplexersAsync(
        IEnumerable<TerminalRuntimePanelViewModel> panels,
        CancellationToken cancellationToken)
    {
        if (_terminalMultiplexerCoordinator is null)
        {
            return;
        }

        foreach (var panel in panels)
        {
            if (panel.MultiplexerSession is not { IsEstablished: true } session)
            {
                continue;
            }

            var result = await _terminalMultiplexerCoordinator.TerminateAsync(
                panel.Connection,
                session,
                cancellationToken);
            if (!result.Terminated)
            {
                SetError(result.Detail);
            }
        }

        await TerminalContinuity.RefreshSessionsAsync(CancellationToken.None);
    }

    public async ValueTask<HostResult<CloseScopeResult>> CloseWindowAsync(
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        var keepClosePending = false;
        try
        {
            if (decision != CloseDecision.Cancel)
            {
                var activations = BeginWindowCloseActivationDrain();
                if (activations.Length > 0)
                {
                    await Task.WhenAll(activations).WaitAsync(cancellationToken);
                }
            }

            var result = await RuntimeGraph.CloseAsync(
                CloseScopeRequest.Window(WindowId, decision),
                cancellationToken);
            RecordRecentSessionCompletions(result);
            keepClosePending = decision != CloseDecision.Cancel
                && result is HostResult<CloseScopeResult>.Success
                {
                    Value: CloseScopeResult.Completed,
                };
            return result;
        }
        finally
        {
            if (!keepClosePending)
            {
                ResumeAfterWindowCloseAttempt();
            }
        }
    }

    public async ValueTask<HostResult<CloseScopeResult>> CloseFilePanelAsync(
        FileRuntimePanelViewModel panel,
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (panel.HostedClient is null)
        {
            return HostResult<CloseScopeResult>.Fail(
                HostError.Create(
                    HostErrorCode.NotFound,
                    "This File Viewer has no hosted session to close."),
                currentRevision: 0);
        }

        var result = await panel.HostedClient.CloseAsync(decision, cancellationToken);
        RecordRecentSessionCompletions(result);
        return result;
    }

    public async Task<bool> RemovePanelAsync(
        PanelInstanceId panelId,
        CancellationToken cancellationToken = default,
        bool retryAfterGraphChange = true)
    {
        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            return false;
        }

        CancelPendingConnections(
            workspace.Tabs
                .SelectMany(tab => tab.Panels)
                .OfType<TerminalRuntimePanelViewModel>()
                .Where(panel => panel.Id == panelId));

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace))
            {
                return false;
            }

            var tab = workspace.Tabs.FirstOrDefault(item =>
                item.Panels.Any(panel => panel.Id == panelId));
            var panel = tab?.Panels.SingleOrDefault(item => item.Id == panelId);
            if (tab is null || panel is null)
            {
                return false;
            }

            if (tab.Panels.Count == 1)
            {
                return await RemoveTabUnderGateAsync(
                    workspace,
                    tab,
                    linkedCancellation.Token,
                    retryAfterGraphChange);
            }

            var proposal = BuildPanelRemovalProposal(
                workspace,
                tab.Id,
                panelId)
                ?? throw new InvalidOperationException(
                    "The runtime panel changed before removal could start.");
            return await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
                workspace,
                proposal,
                "panel removal",
                () =>
                {
                    StopTrackingRecovery(panel);
                    QueueRecentSessionCompletion(
                        panel.Id,
                        RecentSessionOutcome.GracefullyClosed);
                    if (!tab.RemovePanel(panelId))
                    {
                        throw new InvalidOperationException(
                            "The runtime panel changed before the host-approved removal was applied.");
                    }
                },
                retryAfterGraphChange
                    ? RuntimeGraphStaleProposalHandling.RefreshAndRetry
                    : RuntimeGraphStaleProposalHandling.Reject,
                linkedCancellation.Token,
                currentWorkspace => BuildPanelRemovalProposal(
                    currentWorkspace,
                    tab.Id,
                    panelId));
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    public async Task<bool> RemoveTabAsync(
        TabInstanceId tabId,
        CancellationToken cancellationToken = default,
        bool retryAfterGraphChange = true)
    {
        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            return false;
        }

        CancelPendingConnections(
            workspace.Tabs
                .Where(tab => tab.Id == tabId)
                .SelectMany(tab => tab.Panels)
                .OfType<TerminalRuntimePanelViewModel>());

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace)
                || workspace.Tabs.SingleOrDefault(item => item.Id == tabId)
                    is not { } tab)
            {
                return false;
            }

            return await RemoveTabUnderGateAsync(
                workspace,
                tab,
                linkedCancellation.Token,
                retryAfterGraphChange);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    private async Task<bool> RemoveTabUnderGateAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab,
        CancellationToken cancellationToken,
        bool retryAfterGraphChange = true)
    {
        if (!ReferenceEquals(RuntimeWorkspace, workspace)
            || !workspace.Tabs.Contains(tab))
        {
            return false;
        }

        // Closing the last tab leaves the question "what do I open" in its
        // place. A workspace with nothing in it is a blank window with a button
        // on it, which is what closing tabs one by one used to arrive at.
        //
        // Unless that is already all it holds: closing the launcher itself is
        // how a workspace is finished from the tab strip, so the button on it
        // always does something.
        if (workspace.Tabs.Count == 1 && !IsLauncherTab(tab))
        {
            return await ReplaceRuntimeTabUnderGateAsync(
                workspace,
                tab,
                _ => CreateLauncherTab(),
                "last tab removal",
                cancellationToken,
                retryAfterGraphChange
                    ? RuntimeGraphStaleProposalHandling.RefreshAndRetry
                    : RuntimeGraphStaleProposalHandling.Reject);
        }

        // The last launcher tab stays. Closing it would leave the window with
        // nothing in it and nothing to open anything from; ending the workspace
        // is the rail's job.
        if (workspace.Tabs.Count == 1)
        {
            return false;
        }

        var proposal = BuildTabRemovalProposal(workspace, tab.Id)
            ?? throw new InvalidOperationException(
                "The runtime tab changed before removal could start.");
        return await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
            workspace,
            proposal,
            "tab removal",
            () =>
            {
                foreach (var panel in tab.Panels)
                {
                    StopTrackingRecovery(panel);
                    QueueRecentSessionCompletion(
                        panel.Id,
                        RecentSessionOutcome.GracefullyClosed);
                }

                // The tab before this one, or the one that took its place at the
                // front. Jumping to the first tab sent the user across the strip
                // every time they closed something near the end.
                var at = workspace.Tabs.IndexOf(tab);
                tab.DisposePanels();
                workspace.Tabs.Remove(tab);
                if (ReferenceEquals(workspace.ActiveTab, tab))
                {
                    workspace.ActiveTab = workspace.Tabs[Math.Max(0, at - 1)];
                }
            },
            retryAfterGraphChange
                ? RuntimeGraphStaleProposalHandling.RefreshAndRetry
                : RuntimeGraphStaleProposalHandling.Reject,
            cancellationToken,
            currentWorkspace => BuildTabRemovalProposal(
                currentWorkspace,
                tab.Id));
    }

    public async Task<bool> MoveActiveTabAsync(
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (offset is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                "A tab can move one position left or right.");
        }

        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace)
                || workspace.ActiveTab is not { } activeTab)
            {
                return false;
            }

            var sourceIndex = workspace.Tabs.IndexOf(activeTab);
            var anchorIndex = sourceIndex + offset;
            if (anchorIndex < 0 || anchorIndex >= workspace.Tabs.Count)
            {
                return false;
            }

            return await MoveTabUnderGateAsync(
                workspace,
                activeTab,
                workspace.Tabs[anchorIndex],
                offset < 0 ? RuntimeTabPlacement.Before : RuntimeTabPlacement.After,
                linkedCancellation.Token);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    public async Task<bool> MoveTabAsync(
        TabInstanceId sourceTabId,
        TabInstanceId anchorTabId,
        RuntimeTabPlacement placement,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(placement))
        {
            throw new ArgumentOutOfRangeException(nameof(placement), placement, null);
        }

        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            return false;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace))
            {
                return false;
            }

            var sourceTab = workspace.Tabs.SingleOrDefault(tab => tab.Id == sourceTabId);
            var anchorTab = workspace.Tabs.SingleOrDefault(tab => tab.Id == anchorTabId);
            if (sourceTab is null || anchorTab is null)
            {
                return false;
            }

            return await MoveTabUnderGateAsync(
                workspace,
                sourceTab,
                anchorTab,
                placement,
                linkedCancellation.Token);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    // The caller owns _runtimeGraphGate and resolves source/anchor from the
    // post-wait collection. This keeps queued keyboard moves adjacent even
    // when an earlier drag changes the tab order while they wait.
    private async Task<bool> MoveTabUnderGateAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel sourceTab,
        RuntimeTabViewModel anchorTab,
        RuntimeTabPlacement placement,
        CancellationToken cancellationToken)
    {
        var sourceIndex = workspace.Tabs.IndexOf(sourceTab);
        var anchorIndex = workspace.Tabs.IndexOf(anchorTab);
        if (sourceIndex < 0 || anchorIndex < 0)
        {
            return false;
        }

        var insertionIndex = placement == RuntimeTabPlacement.Before
            ? anchorIndex
            : anchorIndex + 1;
        if (sourceIndex < insertionIndex)
        {
            insertionIndex--;
        }

        if (sourceIndex == insertionIndex)
        {
            return false;
        }

        var current = CaptureRuntimeWorkspaceGraph(workspace);
        var reorderedTabs = current.Tabs.ToList();
        var movedTab = reorderedTabs[sourceIndex];
        reorderedTabs.RemoveAt(sourceIndex);
        reorderedTabs.Insert(insertionIndex, movedTab);
        var proposal = new WorkspaceInstance(
            current.Id,
            current.Title,
            reorderedTabs,
            current.ActiveTabId);

        // Clear the live-region value only for a real attempt so a rejected
        // or cancelled proposal can never leave a fresh success announcement.
        TabReorderStatus = string.Empty;
        return await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
            workspace,
            proposal,
            "tab reorder",
            () =>
            {
                workspace.Tabs.Move(sourceIndex, insertionIndex);
                TabReorderStatus =
                    $"Moved tab “{sourceTab.Title}” to position " +
                    $"{insertionIndex + 1} of {workspace.Tabs.Count}.";
                Launcher.RefreshSearchResults();
            },
            RuntimeGraphStaleProposalHandling.Reject,
            cancellationToken);
    }

    public Task<bool> AddLocalTerminalPanelAsync(
        CancellationToken cancellationToken = default) =>
        AddLocalTerminalPanelCoreAsync(null, cancellationToken);

    public Task<bool> AddLocalTerminalPanelAsync(
        PanelSplitOrientation orientation,
        CancellationToken cancellationToken = default) =>
        AddLocalTerminalPanelCoreAsync(orientation, cancellationToken);

    private async Task<bool> AddLocalTerminalPanelCoreAsync(
        PanelSplitOrientation? orientation,
        CancellationToken cancellationToken)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        var connection = tab?.ActivePanel is TerminalRuntimePanelViewModel terminal
            ? FindConnection(terminal.ConnectionId)
            : null;
        connection ??= _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .FirstOrDefault(item => item.Endpoint is ConnectionEndpoint.Local);
        if (workspace is null || tab is null || connection is null)
        {
            SetError(orientation is null
                ? "Open a workspace with a local connection before adding a terminal panel."
                : "Open a workspace with a local connection before splitting a terminal panel.");
            return false;
        }

        var panel = CreateTerminalPanel(
            workspace.Id,
            tab.Id,
            connection,
            "Terminal",
            PanelStartupBehavior.None);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            orientation is null ? "panel creation" : "panel split",
            () =>
            {
                if (orientation is { } splitOrientation)
                {
                    _ = tab.SplitActivePanel(panel, splitOrientation);
                }
                else
                {
                    tab.AddPanel(panel);
                    _ = tab.ActivatePanel(panel.Id);
                }

                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
            },
            cancellationToken);
    }

    /// <summary>
    /// Opens a saved connection as a new panel in the active tab.
    ///
    /// A blank adapter is only one of the things a new panel can become: the
    /// launcher offers saved connections too, and choosing one has to open
    /// that connection rather than a default local shell.
    /// </summary>
    public async Task<bool> AddConnectionPanelAsync(
        ConnectionId id,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        var connection = FindConnection(id);
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace before adding a panel.");
            return false;
        }

        if (connection is null)
        {
            SetError(ConnectionUnavailableMessage(id));
            return false;
        }

        var panel = CreateTerminalPanel(
            workspace.Id,
            tab.Id,
            connection,
            connection.Name,
            PanelStartupBehavior.None);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "panel creation",
            () =>
            {
                tab.AddPanel(panel);
                _ = tab.ActivatePanel(panel.Id);
                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
            },
            cancellationToken);
    }

    /// <summary>
    /// Places an empty cell against one edge of the active tab.
    ///
    /// A placed cell is part of the workspace graph, so placing one is a graph
    /// mutation like any other rather than a local edit — the two sides holding
    /// different cells is what every placeholder bug has been made of.
    /// </summary>
    public async Task<bool> AddPlaceholderPanelAsync(
        PanelSide side,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a panel.");
            return false;
        }

        var placeholder = RuntimeTabViewModel.NewPlaceholder();
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            placeholder,
            "panel placement",
            () => tab.AddPlaceholder(side, placeholder),
            cancellationToken);
    }

    /// <summary>Divides a panel's own cell, leaving the new half empty.</summary>
    public async Task<bool> SplitPanelWithPlaceholderAsync(
        PanelInstanceId panelId,
        PanelSplitOrientation orientation,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before splitting a panel.");
            return false;
        }

        if (tab.Panels.All(panel => panel.Id != panelId))
        {
            return false;
        }

        var placeholder = RuntimeTabViewModel.NewPlaceholder();
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            placeholder,
            "panel split",
            () => tab.SplitWithPlaceholder(panelId, orientation, placeholder),
            cancellationToken);
    }

    /// <summary>
    /// Opens a tab that is nothing but the launcher.
    ///
    /// Asking what to open used to be a modal over the whole window. A tab whose
    /// one cell is unanswered says the same thing in the place the answer will
    /// land, and it can be left open, switched away from, and closed like any
    /// other tab.
    /// </summary>
    public Task<bool> AddLauncherTabAsync(CancellationToken cancellationToken = default)
    {
        ClearError();
        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            SetError("Open a workspace before creating a tab.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            _ => CreateLauncherTab(),
            "launcher tab creation",
            cancellationToken);
    }

    private static RuntimeTabViewModel CreateLauncherTab()
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), "New tab", "Launcher");
        tab.AddPlaceholder(PanelSide.Right);
        return tab;
    }

    /// <summary>
    /// Whether this tab is only the launcher: one cell, still unanswered. What
    /// opens as a tab of its own opens here instead, because the user asked for
    /// something to open and this tab is the asking.
    /// </summary>
    private static bool IsLauncherTab(RuntimeTabViewModel tab) =>
        tab.Panels is [PanelPlaceholderViewModel];

    /// <summary>
    /// Opens a saved target as a panel in the active tab, with the adapter the
    /// launcher asked for.
    ///
    /// The tab-level counterpart is <see cref="AddSavedConnectionTabAsync"/>.
    /// Both exist because the same row means two things depending on where it
    /// was clicked: from a placed cell it fills that cell, and from the tab
    /// launcher it opens a tab.
    /// </summary>
    public Task<bool> AddSavedConnectionPanelAsync(
        SavedConnectionLaunchViewModel launch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return launch.Target switch
        {
            PanelConnectionOptionViewModel.Target.Connection connection =>
                AddConnectionPanelAsync(connection.Id, launch.Panel, cancellationToken),
            PanelConnectionOptionViewModel.Target.FileProvider fileProvider =>
                AddFileProviderPanelAsync(fileProvider.Id, launch.Panel, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(launch),
                launch.Target.GetType(),
                "The saved connection target is unsupported."),
        };
    }

    private async Task<bool> AddConnectionPanelAsync(
        ConnectionId id,
        PanelKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == PanelKind.Terminal)
        {
            return await AddConnectionPanelAsync(id, cancellationToken);
        }

        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        var connection = FindConnection(id);
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace before adding a panel.");
            return false;
        }

        if (connection is null)
        {
            SetError(ConnectionUnavailableMessage(id));
            return false;
        }

        if (!connection.PanelLaunchCapabilities.Supports(kind))
        {
            SetError($"{connection.Name} cannot open {PanelTitle(kind)}.");
            return false;
        }

        var title = PanelTitle(kind);
        var panel = kind switch
        {
            PanelKind.FileViewer => CreateFilePanel(
                workspace.Id,
                tab.Id,
                PanelInstanceId.New(),
                title,
                connection.Endpoint is ConnectionEndpoint.Ssh
                    ? ConnectionFileProviderProfiles.Id(connection.Id)
                    : BuiltInFileProviders.HomeId,
                deferInitialization: true,
                connection: connection),
            PanelKind.Statistics or PanelKind.ProcessMonitor => CreateMonitorPanel(
                workspace.Id,
                tab.Id,
                PanelInstanceId.New(),
                title,
                kind,
                connection),
            PanelKind.Docker => CreateDockerPanel(
                PanelInstanceId.New(),
                title,
                connection,
                workspace.Id),
            PanelKind.Git => CreateGitPanel(
                PanelInstanceId.New(),
                title,
                connection,
                workspaceId: workspace.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            $"{title} panel creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    private async Task<bool> AddFileProviderPanelAsync(
        FileProviderProfileId profileId,
        PanelKind kind,
        CancellationToken cancellationToken)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace before adding a panel.");
            return false;
        }

        var storedProfile = _catalog.Snapshot.FileProviderProfiles
            .SingleOrDefault(item => item.Value.Id == profileId);
        if (storedProfile is null)
        {
            SetError("That file connection no longer exists.");
            return false;
        }

        if (!storedProfile.Value.Configuration.PanelLaunchCapabilities.Supports(kind))
        {
            SetError($"{storedProfile.Value.Name} cannot open {PanelTitle(kind)}.");
            return false;
        }

        if (_filePanelClient.Profiles.All(profile => !string.Equals(profile.Id, profileId.Value, StringComparison.Ordinal)))
        {
            SetError("That file connection is not ready yet.");
            return false;
        }

        var panel = CreateFilePanel(
            workspace.Id,
            tab.Id,
            PanelInstanceId.New(),
            PanelTitle(PanelKind.FileViewer),
            profileId,
            deferInitialization: true);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "File Viewer creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    /// <summary>
    /// Changes the connection behind an existing terminal while preserving the
    /// panel ID, tab, layout cell, and panel chrome. The caller closes the old
    /// hosted session first, including any required active-work confirmation.
    /// </summary>
    public bool ReplaceTerminalConnection(
        TerminalRuntimePanelViewModel currentPanel,
        ConnectionProfile connection)
    {
        ArgumentNullException.ThrowIfNull(currentPanel);
        ArgumentNullException.ThrowIfNull(connection);
        ClearError();

        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(candidate =>
            candidate.Panels.Any(panel => panel.Id == currentPanel.Id));
        if (workspace is null || tab is null)
        {
            SetError("That terminal panel is no longer open.");
            return false;
        }

        var livePanel = tab.Panels
            .OfType<TerminalRuntimePanelViewModel>()
            .SingleOrDefault(candidate => candidate.Id == currentPanel.Id);
        if (livePanel is null)
        {
            SetError("The terminal changed before its connection could be switched.");
            return false;
        }

        var replacement = CreateTerminalPanel(
            workspace.Id,
            tab.Id,
            connection,
            livePanel.Title,
            PanelStartupBehavior.None,
            livePanel.Id);
        if (!tab.ReplacePanel(livePanel, replacement))
        {
            replacement.Dispose();
            SetError("The terminal changed before its connection could be switched.");
            return false;
        }

        Notifications.Watch(workspace);
        workspace.AddConnections(Connections.Where(item => item.Id == connection.Id));
        StartTrackingRecovery(replacement);
        TrackRecentSession(replacement);
        AgentWorkspaceScope.ResetTerminalOptions();
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    public bool ReplaceTerminalConnection(
        TerminalRuntimePanelViewModel currentPanel,
        ConnectionId connectionId)
    {
        var connection = FindConnection(connectionId);
        if (connection is null)
        {
            SetError(ConnectionUnavailableMessage(connectionId));
            return false;
        }

        return ReplaceTerminalConnection(currentPanel, connection);
    }

    public bool ReplacePanelConnection(
        RuntimePanelViewModel currentPanel,
        ConnectionProfile connection)
    {
        ArgumentNullException.ThrowIfNull(currentPanel);
        ArgumentNullException.ThrowIfNull(connection);
        ClearError();

        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(candidate =>
            candidate.Panels.Any(panel => panel.Id == currentPanel.Id));
        if (workspace is null || tab is null)
        {
            SetError("That panel is no longer open.");
            return false;
        }

        var livePanel = tab.Panels.SingleOrDefault(candidate => candidate.Id == currentPanel.Id);
        if (livePanel is null || livePanel.Kind != currentPanel.Kind)
        {
            SetError("The panel changed before its connection could be switched.");
            return false;
        }

        if (livePanel is DatabaseRuntimePanelViewModel or RedisRuntimePanelViewModel)
        {
            // The database panel binds to saved database connections through
            // its own selector; a terminal connection is never its target.
            SetError("Choose a database connection from the panel selector.");
            return false;
        }

        RuntimePanelViewModel replacement;
        if (livePanel is FileRuntimePanelViewModel)
        {
            if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
            {
                SetError(
                    "That execution connection cannot back File Viewer. "
                    + "Choose a file connection from the panel selector.");
                return false;
            }

            var profileId = connection.Endpoint is ConnectionEndpoint.Ssh
                ? ConnectionFileProviderProfiles.Id(connection.Id)
                : BuiltInFileProviders.HomeId;
            replacement = CreateFilePanel(
                workspace.Id,
                tab.Id,
                livePanel.Id,
                livePanel.Title,
                profileId,
                connection: connection,
                deferInitialization: true);
        }
        else if (livePanel.Kind is PanelKind.Statistics or PanelKind.ProcessMonitor)
        {
            replacement = CreateMonitorPanel(
                workspace.Id,
                tab.Id,
                livePanel.Id,
                livePanel.Title,
                livePanel.Kind,
                connection);
        }
        else if (livePanel is BrowserRuntimePanelViewModel browser)
        {
            if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
            {
                SetError("A browser can use only a local or SSH connection.");
                return false;
            }

            replacement = CreateBrowserPanel(
                workspace.Id,
                tab.Id,
                livePanel.Id,
                livePanel.Title,
                browser.CurrentAddress,
                connection,
                browser.ProfileBinding);
        }
        else if (livePanel is DockerRuntimePanelViewModel)
        {
            if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
            {
                SetError("A Docker panel can use only a local or SSH connection.");
                return false;
            }

            replacement = CreateDockerPanel(
                livePanel.Id,
                livePanel.Title,
                connection,
                workspace.Id);
        }
        else if (livePanel is GitRuntimePanelViewModel)
        {
            if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
            {
                SetError("A Git panel can use only a local or SSH connection.");
                return false;
            }

            replacement = CreateGitPanel(
                livePanel.Id,
                livePanel.Title,
                connection,
                workspaceId: workspace.Id);
        }
        else
        {
            SetError("This panel type does not support connection switching.");
            return false;
        }

        if (!tab.ReplacePanel(livePanel, replacement))
        {
            replacement.Dispose();
            SetError("The panel changed before its connection could be switched.");
            return false;
        }

        Notifications.Watch(workspace);
        workspace.AddConnections(Connections.Where(item => item.Id == connection.Id));
        StartTrackingRecovery(replacement);
        StartAcceptedRuntimePanel(replacement);
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    public bool ReplacePanelConnection(
        RuntimePanelViewModel currentPanel,
        ConnectionId connectionId)
    {
        var connection = FindConnection(connectionId);
        if (connection is null)
        {
            SetError(ConnectionUnavailableMessage(connectionId));
            return false;
        }

        return ReplacePanelConnection(currentPanel, connection);
    }

    public bool ReplaceFilePanelProfile(
        FileRuntimePanelViewModel currentPanel,
        FileProviderProfileId profileId)
    {
        ArgumentNullException.ThrowIfNull(currentPanel);
        ClearError();

        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(candidate =>
            candidate.Panels.Any(panel => panel.Id == currentPanel.Id));
        if (workspace is null || tab is null)
        {
            SetError("That File Viewer panel is no longer open.");
            return false;
        }

        var livePanel = tab.Panels
            .OfType<FileRuntimePanelViewModel>()
            .SingleOrDefault(candidate => candidate.Id == currentPanel.Id);
        if (livePanel is null)
        {
            SetError("The File Viewer changed before its connection could be switched.");
            return false;
        }

        var replacement = CreateFilePanel(
            workspace.Id,
            tab.Id,
            livePanel.Id,
            livePanel.Title,
            profileId,
            deferInitialization: true);
        if (!tab.ReplacePanel(livePanel, replacement))
        {
            replacement.Dispose();
            SetError("The File Viewer changed before its connection could be switched.");
            return false;
        }

        Notifications.Watch(workspace);
        StartTrackingRecovery(replacement);
        StartAcceptedRuntimePanel(replacement);
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    public async Task<bool> AddFilePanelAsync(
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a File Viewer panel.");
            return false;
        }

        var panel = CreateFilePanel(
            workspace.Id,
            tab.Id,
            PanelInstanceId.New(),
            "File Viewer",
            deferInitialization: true);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "File Viewer creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    public Task<bool> AddBrowserPanelAsync(
        CancellationToken cancellationToken = default) =>
        AddBrowserPanelAsync(profileId: null, cancellationToken);

    public async Task<bool> AddBrowserPanelAsync(
        BrowserProfileId? profileId,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a browser panel.");
            return false;
        }

        if (_browserRendererViewFactory is null)
        {
            SetError("The embedded browser is unavailable in this build.");
            return false;
        }

        var panel = CreateBrowserPanel(
            workspace.Id,
            tab.Id,
            PanelInstanceId.New(),
            "Browser",
            BrowserAddress.Blank,
            requestedProfileId: profileId);
        if (panel is not BrowserRuntimePanelViewModel)
        {
            SetError(BrowserCreationFailure(panel));
            panel.Dispose();
            return false;
        }

        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "browser creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                TrackRecentSession(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    public Task<bool> AddStatisticsPanelAsync(
        CancellationToken cancellationToken = default) =>
        AddMonitorPanelAsync(PanelKind.Statistics, cancellationToken);

    public Task<bool> AddProcessMonitorPanelAsync(
        CancellationToken cancellationToken = default) =>
        AddMonitorPanelAsync(PanelKind.ProcessMonitor, cancellationToken);

    public async Task<bool> AddDatabasePanelAsync(
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a database panel.");
            return false;
        }

        var panel = CreateDatabasePanel(
            PanelInstanceId.New(),
            "Database",
            workspaceId: workspace.Id);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "Database panel creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    public async Task<bool> AddDockerPanelAsync(
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a Docker panel.");
            return false;
        }

        var panel = CreateDockerPanel(
            PanelInstanceId.New(),
            "Docker",
            workspaceId: workspace.Id);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "Docker panel creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    public async Task<bool> AddGitPanelAsync(
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace tab before adding a Git panel.");
            return false;
        }

        var panel = CreateGitPanel(
            PanelInstanceId.New(),
            "Git",
            workspaceId: workspace.Id);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "Git panel creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    private async Task<bool> AddMonitorPanelAsync(
        PanelKind kind,
        CancellationToken cancellationToken)
    {
        if (kind is not (PanelKind.Statistics or PanelKind.ProcessMonitor))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        var workspace = RuntimeWorkspace;
        var tab = workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError($"Open a workspace tab before adding a {PanelTitle(kind)} panel.");
            return false;
        }

        var panel = CreateMonitorPanel(
            workspace.Id,
            tab.Id,
            PanelInstanceId.New(),
            PanelTitle(kind),
            kind);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            $"{PanelTitle(kind)} creation",
            () =>
            {
                tab.AddPanel(panel);
                StartTrackingRecovery(panel);
                _ = tab.ActivatePanel(panel.Id);
            },
            cancellationToken);
    }

    private async Task<bool> AddRuntimePanelUnderReceiptAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab,
        RuntimePanelViewModel panel,
        string operation,
        Action commit,
        CancellationToken cancellationToken,
        RuntimeGraphStaleProposalHandling staleProposalHandling =
            RuntimeGraphStaleProposalHandling.RefreshAndRetry)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(commit);

        var navigation = CaptureRuntimeMutationNavigation();
        var replacesLauncher = IsLauncherTab(tab);
        var firstPanelTitle = replacesLauncher
            ? tab.TitleForFirstPanel(panel.Title)
            : tab.Title;
        var attached = false;
        try
        {
            if (tab.ReplaceTarget is null
                && !HasRuntimeWorkspacePanelCapacity(
                    workspace,
                    removedPanelCount: 0,
                    addedPanelCount: 1))
            {
                return false;
            }

            var changed = await ReplaceRuntimeWorkspaceGraphAsync(
                workspace,
                operation,
                // Answering a placed cell swaps that cell for the panel; anything
                // else grows the tab. The two must not be confused: proposing an
                // append while the commit replaces would leave the host holding a
                // panel the client had already dropped.
                currentWorkspace =>
                {
                    var replacement = tab.ReplaceTarget is { } replacedPanelId
                        ? ReplaceRuntimePanel(
                            CaptureRuntimeWorkspaceGraph(currentWorkspace),
                            tab.Id,
                            replacedPanelId,
                            new PanelInstance(panel.Id, panel.Kind, panel.Title))
                        : AppendRuntimePanel(
                            CaptureRuntimeWorkspaceGraph(currentWorkspace),
                            tab.Id,
                            new PanelInstance(panel.Id, panel.Kind, panel.Title));
                    return !string.Equals(firstPanelTitle, tab.Title, StringComparison.Ordinal)
                        ? RenameRuntimeTab(replacement, tab.Id, firstPanelTitle)
                        : replacement;
                },
                () =>
                {
                    try
                    {
                        commit();
                    }
                    finally
                    {
                        attached = tab.Panels.Contains(panel);
                    }

                    if (attached)
                    {
                        if (replacesLauncher)
                        {
                            tab.AdoptFirstPanelTitle(panel.Title);
                        }

                        CompleteRuntimeMutationNavigation(navigation);
                    }
                },
                cancellationToken,
                staleProposalHandling);
            if (changed && attached)
            {
                // A hosted panel can link its session immediately, advancing
                // the graph again. Start it only after the accepted topology
                // receipt has been applied, so that newer link event cannot
                // overtake and invalidate the older layout receipt.
                StartAcceptedRuntimePanel(panel);
            }

            return changed;
        }
        finally
        {
            if (!attached)
            {
                panel.Dispose();
            }
        }
    }

    public Task<bool> AddConnectionTabAsync(
        ConnectionId connectionId,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        if (!CanAppendSavedDefinitionTab())
        {
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        var connection = FindConnection(connectionId);
        var launchItem = Connections.SingleOrDefault(item => item.Id == connectionId);
        if (workspace is null)
        {
            SetError("Open a workspace before adding a saved connection as a tab.");
            return Task.FromResult(false);
        }

        if (connection is null)
        {
            SetError(ConnectionUnavailableMessage(connectionId));
            return Task.FromResult(false);
        }

        if (launchItem is not { CanOpen: true })
        {
            SetError(launchItem?.Status ?? "That connection is unavailable on this platform.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            runtime =>
            {
                var currentStored = _catalog.Snapshot.Connections.SingleOrDefault(
                    item => item.Value.Id == connectionId);
                var currentConnection = currentStored?.Value
                    .ResolveHostConnection(FindStoredConnection);
                if (currentConnection is null)
                {
                    SetError(ConnectionUnavailableMessage(connectionId));
                    return null;
                }

                var currentLaunchItem = ToConnectionItem(
                    currentConnection,
                    currentStored!.Revision);
                if (currentLaunchItem is not { CanOpen: true })
                {
                    SetError(
                        currentLaunchItem?.Status
                        ?? "That connection is unavailable on this platform.");
                    return null;
                }

                return CreateConnectionTab(
                    runtime.Id,
                    currentConnection,
                    agentPolicy: runtime.AgentPolicy);
            },
            "connection tab creation",
            cancellationToken);
    }

    public Task<bool> AddSavedConnectionTabAsync(
        SavedConnectionLaunchViewModel launch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return launch.Target switch
        {
            PanelConnectionOptionViewModel.Target.Connection connection =>
                AddConnectionPanelTabAsync(
                    connection.Id,
                    launch.Panel,
                    cancellationToken),
            PanelConnectionOptionViewModel.Target.FileProvider fileProvider =>
                AddFileProviderTabAsync(
                    fileProvider.Id,
                    launch.Panel,
                    cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(launch),
                launch.Target.GetType(),
                "The saved connection target is unsupported."),
        };
    }

    private Task<bool> AddConnectionPanelTabAsync(
        ConnectionId connectionId,
        PanelKind panel,
        CancellationToken cancellationToken)
    {
        if (panel == PanelKind.Terminal)
        {
            return AddConnectionTabAsync(connectionId, cancellationToken);
        }

        ClearError();
        if (!CanAppendSavedDefinitionTab())
        {
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        var connection = FindConnection(connectionId);
        var launchItem = Connections.SingleOrDefault(item => item.Id == connectionId);
        if (workspace is null)
        {
            SetError("Open a workspace before adding a saved connection as a tab.");
            return Task.FromResult(false);
        }

        if (connection is null)
        {
            SetError(ConnectionUnavailableMessage(connectionId));
            return Task.FromResult(false);
        }

        if (launchItem is not { CanOpen: true })
        {
            SetError(launchItem?.Status ?? "That connection is unavailable on this platform.");
            return Task.FromResult(false);
        }

        if (!connection.PanelLaunchCapabilities.Supports(panel))
        {
            SetError($"{connection.Name} cannot open {PanelTitle(panel)}.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            runtime =>
            {
                var currentConnection = FindConnection(connectionId);
                if (currentConnection is null)
                {
                    SetError(ConnectionUnavailableMessage(connectionId));
                    return null;
                }

                if (!currentConnection.PanelLaunchCapabilities.Supports(panel))
                {
                    SetError($"{currentConnection.Name} cannot open {PanelTitle(panel)}.");
                    return null;
                }

                return CreateConnectionPanelTab(
                    runtime.Id,
                    currentConnection,
                    panel,
                    runtime.AgentPolicy);
            },
            $"{PanelTitle(panel)} connection tab creation",
            cancellationToken);
    }

    private Task<bool> AddFileProviderTabAsync(
        FileProviderProfileId profileId,
        PanelKind panel,
        CancellationToken cancellationToken)
    {
        ClearError();
        if (!CanAppendSavedDefinitionTab())
        {
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        var storedProfile = _catalog.Snapshot.FileProviderProfiles
            .SingleOrDefault(item => item.Value.Id == profileId);
        if (workspace is null)
        {
            SetError("Open a workspace before adding a saved connection as a tab.");
            return Task.FromResult(false);
        }

        if (storedProfile is null)
        {
            SetError("That file connection no longer exists.");
            return Task.FromResult(false);
        }

        if (!storedProfile.Value.Configuration.PanelLaunchCapabilities.Supports(panel))
        {
            SetError($"{storedProfile.Value.Name} cannot open {PanelTitle(panel)}.");
            return Task.FromResult(false);
        }

        if (_filePanelClient.Profiles.All(profile => !string.Equals(profile.Id, profileId.Value, StringComparison.Ordinal)))
        {
            SetError("That file connection is not ready yet.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            runtime =>
            {
                var currentProfile = _catalog.Snapshot.FileProviderProfiles
                    .SingleOrDefault(item => item.Value.Id == profileId);
                if (currentProfile is null)
                {
                    SetError("That file connection no longer exists.");
                    return null;
                }

                return CreateFileProviderTab(runtime.Id, currentProfile.Value);
            },
            "file connection tab creation",
            cancellationToken);
    }

    public Task<bool> AddScreenTabAsync(
        ScreenId screenId,
        CancellationToken cancellationToken = default) =>
        OpenScreenTabAsync(screenId, replacedTab: null, cancellationToken);

    private Task<bool> ReplaceScreenTabAsync(
        RuntimeTabViewModel replacedTab,
        ScreenId screenId,
        CancellationToken cancellationToken) =>
        OpenScreenTabAsync(screenId, replacedTab, cancellationToken);

    private Task<bool> OpenScreenTabAsync(
        ScreenId screenId,
        RuntimeTabViewModel? replacedTab,
        CancellationToken cancellationToken)
    {
        ClearError();
        if (!CanAppendSavedDefinitionTab())
        {
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        var storedScreen = _catalog.Snapshot.Screens
            .SingleOrDefault(item => item.Value.Id == screenId);
        if (workspace is null)
        {
            SetError("Open a workspace before adding a saved screen as a tab.");
            return Task.FromResult(false);
        }

        if (storedScreen is null)
        {
            SetError("That saved screen no longer exists.");
            return Task.FromResult(false);
        }

        return replacedTab is null
            ? AppendRuntimeTabAsync(workspace, CreateScreenTab, "saved-screen tab creation", cancellationToken)
            : ReplaceRuntimeTabAsync(
                workspace,
                replacedTab,
                CreateScreenTab,
                "saved-screen tab creation",
                cancellationToken);

        RuntimeTabViewModel? CreateScreenTab(RuntimeWorkspaceViewModel runtime)
        {
            var currentStoredScreen = _catalog.Snapshot.Screens
                .SingleOrDefault(item => item.Value.Id == screenId);
            if (currentStoredScreen is null)
            {
                SetError("That saved screen no longer exists.");
                return null;
            }

            var currentScreen = currentStoredScreen.Value;
            return CreateRuntimeTab(
                runtime.Id,
                currentScreen.Name,
                "Saved screen",
                currentScreen.LayoutId,
                currentScreen.Panels,
                currentScreen.Key,
                currentScreen.Name,
                runtime.AgentPolicy.WithOverride(
                    currentScreen.AgentPolicyOverride,
                    currentScreen.Key,
                    currentStoredScreen.Revision));
        }
    }

    public Task<bool> AddLocalTerminalTabAsync(
        CancellationToken cancellationToken = default)
    {
        if (HasOverlay)
        {
            SetError("Close the current overlay before creating a terminal tab.");
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        var connection = ActivePanel is TerminalRuntimePanelViewModel terminal
            ? FindConnection(terminal.ConnectionId)
            : null;
        connection ??= _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .FirstOrDefault(item => item.Endpoint is ConnectionEndpoint.Local);
        if (workspace is null || connection is null)
        {
            SetError("Open a workspace with a local connection before creating a tab.");
            return Task.FromResult(false);
        }

        var connectionId = connection.Id;
        return AppendRuntimeTabAsync(
            workspace,
            runtime =>
            {
                var currentConnection = FindConnection(connectionId);
                if (currentConnection is null)
                {
                    SetError("The local connection no longer exists.");
                    return null;
                }

                return CreateConnectionTab(
                    runtime.Id,
                    currentConnection,
                    agentPolicy: runtime.AgentPolicy);
            },
            "tab creation",
            cancellationToken);
    }

    public Task<bool> AddBrowserTabAsync(
        CancellationToken cancellationToken = default) =>
        AddBrowserTabAsync(profileId: null, cancellationToken);

    public Task<bool> AddBrowserTabAsync(
        BrowserProfileId? profileId,
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.Browser, profileId, cancellationToken);

    public Task<bool> AddFileViewerTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.FileViewer, profileId: null, cancellationToken);

    public Task<bool> AddStatisticsTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.Statistics, profileId: null, cancellationToken);

    public Task<bool> AddDatabaseTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.DatabaseViewer, profileId: null, cancellationToken);

    public Task<bool> AddDockerTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.Docker, profileId: null, cancellationToken);

    public Task<bool> AddGitTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.Git, profileId: null, cancellationToken);

    public Task<bool> AddProcessMonitorTabAsync(
        CancellationToken cancellationToken = default) =>
        AddSinglePanelTabAsync(PanelKind.ProcessMonitor, profileId: null, cancellationToken);

    /// <summary>
    /// Appends a one-panel tab for a local adapter. The New Tab catalog uses this
    /// path; the visually identical catalog inside a placed placeholder continues
    /// to use the panel-creation methods instead.
    /// </summary>
    private Task<bool> AddSinglePanelTabAsync(
        PanelKind kind,
        BrowserProfileId? profileId,
        CancellationToken cancellationToken)
    {
        if (kind is not (PanelKind.Browser
            or PanelKind.FileViewer
            or PanelKind.Statistics
            or PanelKind.ProcessMonitor
            or PanelKind.DatabaseViewer
            or PanelKind.Docker
            or PanelKind.Git))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        ClearError();
        if (HasOverlay)
        {
            SetError("Close the current overlay before creating a tab.");
            return Task.FromResult(false);
        }

        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            SetError("Open a workspace before creating a tab.");
            return Task.FromResult(false);
        }

        if (kind == PanelKind.Browser && _browserRendererViewFactory is null)
        {
            SetError("The embedded browser is unavailable in this build.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            runtime => CreateSinglePanelTab(runtime.Id, kind, profileId),
            $"{SinglePanelTabTitle(kind)} tab creation",
            cancellationToken);
    }

    private RuntimeTabViewModel? CreateSinglePanelTab(
        WorkspaceInstanceId workspaceId,
        PanelKind kind,
        BrowserProfileId? profileId)
    {
        var title = SinglePanelTabTitle(kind);
        var source = kind is PanelKind.Statistics or PanelKind.ProcessMonitor
            ? "Local host"
            : "Local";
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), title, source);
        try
        {
            var panel = kind switch
            {
                PanelKind.Browser => CreateBrowserPanel(
                    workspaceId,
                    tab.Id,
                    PanelInstanceId.New(),
                    title,
                    BrowserAddress.Blank,
                    requestedProfileId: profileId),
                PanelKind.FileViewer => CreateFilePanel(
                    workspaceId,
                    tab.Id,
                    PanelInstanceId.New(),
                    title,
                    deferInitialization: true),
                PanelKind.Statistics or PanelKind.ProcessMonitor =>
                    CreateMonitorPanel(
                        workspaceId,
                        tab.Id,
                        PanelInstanceId.New(),
                        title,
                        kind),
                PanelKind.DatabaseViewer => CreateDatabasePanel(
                    PanelInstanceId.New(),
                    title,
                    workspaceId: workspaceId),
                PanelKind.Docker => CreateDockerPanel(
                    PanelInstanceId.New(),
                    title,
                    workspaceId: workspaceId),
                PanelKind.Git => CreateGitPanel(
                    PanelInstanceId.New(),
                    title,
                    workspaceId: workspaceId),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
            if (kind == PanelKind.Browser
                && panel is not BrowserRuntimePanelViewModel)
            {
                SetError(BrowserCreationFailure(panel));
                panel.Dispose();
                return null;
            }

            AddPanelOrDispose(tab, panel);
            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private static string SinglePanelTabTitle(PanelKind kind) => kind switch
    {
        PanelKind.Browser => "Browser",
        PanelKind.FileViewer => "File Viewer",
        PanelKind.Statistics => "Statistics",
        PanelKind.ProcessMonitor => "Process Monitor",
        PanelKind.DatabaseViewer => "Database",
        PanelKind.Docker => "Docker",
        PanelKind.Git => "Git",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string BrowserCreationFailure(RuntimePanelViewModel panel) =>
        panel is UnavailableRuntimePanelViewModel unavailable
            ? unavailable.CapabilityMessage
            : "The embedded browser could not be initialized.";

    private bool CanAppendSavedDefinitionTab()
    {
        if (Overlay is ShellOverlay.None or ShellOverlay.CommandPalette)
        {
            return true;
        }

        SetError("Close the current editor before adding a saved tab.");
        return false;
    }

    private async Task<bool> AppendRuntimeTabAsync(
        RuntimeWorkspaceViewModel workspace,
        Func<RuntimeWorkspaceViewModel, RuntimeTabViewModel?> createTab,
        string operation,
        CancellationToken cancellationToken,
        RuntimeGraphStaleProposalHandling staleProposalHandling =
            RuntimeGraphStaleProposalHandling.RefreshAndRetry)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(createTab);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var navigation = CaptureRuntimeMutationNavigation();
        RuntimeTabViewModel? tab = null;
        var committed = false;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace))
            {
                return false;
            }

            tab = createTab(workspace);
            if (tab is null)
            {
                return false;
            }

            if (!HasRuntimeWorkspacePanelCapacity(
                    workspace,
                    removedPanelCount: 0,
                    addedPanelCount: tab.Panels.Count))
            {
                return false;
            }

            var current = CaptureRuntimeWorkspaceGraph(workspace);
            var proposal = new WorkspaceInstance(
                current.Id,
                current.Title,
                current.Tabs.Append(CaptureRuntimeTab(tab)),
                tab.Id);
            var changed = await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
                workspace,
                proposal,
                operation,
                () =>
                {
                    // The host accepted this tab. Transfer ownership before
                    // publishing it so a later projection-side exception cannot
                    // dispose panels that are already part of the live graph.
                    committed = true;
                    CommitRuntimeTabAppend(workspace, tab);
                    CompleteRuntimeMutationNavigation(navigation);
                },
                staleProposalHandling,
                linkedCancellation.Token,
                currentWorkspace => BuildTabAppendProposal(
                    currentWorkspace,
                    tab));
            if (changed)
            {
                // Session startup belongs after graph projection, not inside
                // its commit callback. Fast hosted sessions publish a newer
                // graph revision as soon as they link.
                foreach (var panel in tab.Panels)
                {
                    StartAcceptedRuntimePanel(panel);
                }
            }

            return changed;
        }
        finally
        {
            _runtimeGraphGate.Release();
            if (!committed)
            {
                tab?.DisposePanels();
            }
        }
    }

    /// <summary>
    /// Puts a tab where a launcher tab was standing.
    ///
    /// A launcher tab is the question "what do I open"; a saved screen is an
    /// answer to it. Appending beside it instead left the question sitting there
    /// next to its own answer, and the user closing it by hand every time.
    ///
    /// Only a launcher tab is ever replaced this way. It holds one unanswered
    /// cell and therefore no session, so nothing is lost when it goes.
    /// </summary>
    private async Task<bool> ReplaceRuntimeTabAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel replacedTab,
        Func<RuntimeWorkspaceViewModel, RuntimeTabViewModel?> createTab,
        string operation,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeGraphLifetime.Token);
        await _runtimeGraphGate.WaitAsync(linkedCancellation.Token);
        try
        {
            return await ReplaceRuntimeTabUnderGateAsync(
                workspace,
                replacedTab,
                createTab,
                operation,
                linkedCancellation.Token);
        }
        finally
        {
            _runtimeGraphGate.Release();
        }
    }

    /// <summary>
    /// The gate is already held. Closing the last tab reaches this from inside
    /// tab removal, and the gate is not reentrant — taking it a second time
    /// waits for a release that cannot come until the wait returns.
    /// </summary>
    private async Task<bool> ReplaceRuntimeTabUnderGateAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel replacedTab,
        Func<RuntimeWorkspaceViewModel, RuntimeTabViewModel?> createTab,
        string operation,
        CancellationToken cancellationToken,
        RuntimeGraphStaleProposalHandling staleProposalHandling =
            RuntimeGraphStaleProposalHandling.RefreshAndRetry)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(replacedTab);
        ArgumentNullException.ThrowIfNull(createTab);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var navigation = CaptureRuntimeMutationNavigation();
        RuntimeTabViewModel? tab = null;
        var committed = false;
        try
        {
            if (!ReferenceEquals(RuntimeWorkspace, workspace)
                || !workspace.Tabs.Contains(replacedTab))
            {
                return false;
            }

            tab = createTab(workspace);
            if (tab is null)
            {
                return false;
            }

            if (!HasRuntimeWorkspacePanelCapacity(
                    workspace,
                    replacedTab.Panels.Count,
                    tab.Panels.Count))
            {
                return false;
            }

            var changed = await ReplaceRuntimeWorkspaceGraphUnderGateAsync(
                workspace,
                BuildTabReplacementProposal(workspace, replacedTab, tab)!,
                operation,
                () =>
                {
                    committed = true;
                    var at = workspace.Tabs.IndexOf(replacedTab);
                    workspace.Tabs[at] = tab;
                    foreach (var panel in replacedTab.Panels)
                    {
                        StopTrackingRecovery(panel);
                        QueueRecentSessionCompletion(
                            panel.Id,
                            RecentSessionOutcome.GracefullyClosed);
                    }

                    replacedTab.DisposePanels();
                    foreach (var panel in tab.Panels)
                    {
                        StartTrackingRecovery(panel);
                    }

                    TrackRecentSessions(tab.Panels);
                    workspace.ActiveTab = tab;
                    CompleteRuntimeMutationNavigation(navigation);
                },
                staleProposalHandling,
                cancellationToken,
                currentWorkspace => BuildTabReplacementProposal(
                    currentWorkspace,
                    replacedTab,
                    tab));
            if (changed)
            {
                // See AppendRuntimeTabAsync: topology acceptance precedes
                // every panel/session startup that can advance the graph.
                foreach (var panel in tab.Panels)
                {
                    StartAcceptedRuntimePanel(panel);
                }
            }

            return changed;
        }
        finally
        {
            if (!committed)
            {
                tab?.DisposePanels();
            }
        }
    }

    private static WorkspaceInstance? BuildTabReplacementProposal(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel replacedTab,
        RuntimeTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(replacedTab);
        ArgumentNullException.ThrowIfNull(tab);
        var current = CaptureRuntimeWorkspaceGraph(workspace);
        if (current.Tabs.All(item => item.Id != replacedTab.Id))
        {
            return null;
        }

        var captured = CaptureRuntimeTab(tab);
        return new WorkspaceInstance(
            current.Id,
            current.Title,
            current.Tabs.Select(item => item.Id == replacedTab.Id ? captured : item),
            tab.Id);
    }

    private void CommitRuntimeTabAppend(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab)
    {
        workspace.Tabs.Add(tab);
        foreach (var panel in tab.Panels)
        {
            StartTrackingRecovery(panel);
        }

        var connectionIds = tab.Panels
            .OfType<TerminalRuntimePanelViewModel>()
            .Select(panel => panel.ConnectionId)
            .ToHashSet();
        workspace.AddConnections(Connections.Where(connection =>
            connectionIds.Contains(connection.Id)));
        TrackRecentSessions(tab.Panels);
        workspace.ActiveTab = tab;
    }

    private ShellNavigationSnapshot CaptureRuntimeMutationNavigation() =>
        _navigation.CaptureRuntimeMutation();

    private void CompleteRuntimeMutationNavigation(
        ShellNavigationSnapshot initiatingState)
    {
        var previousRoute = Route;
        var previousOverlay = Overlay;
        _navigation.CompleteRuntimeMutation(initiatingState);
        if (previousRoute != Route && Route == ShellRoute.Workspace)
        {
            MarkVisibleNotificationsSeen();
        }

        if (previousOverlay != Overlay && Overlay == ShellOverlay.None)
        {
            MarkVisibleNotificationsSeen();
        }
    }

    public async Task<bool> SelectRelativeTabAsync(
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (HasOverlay)
        {
            SetError("Close the current overlay before changing tabs.");
            return false;
        }

        var workspace = RuntimeWorkspace;
        if (workspace?.ActiveTab is null || workspace.Tabs.Count < 2)
        {
            return false;
        }

        var current = workspace.Tabs.IndexOf(workspace.ActiveTab);
        var destination = (current + offset + workspace.Tabs.Count) % workspace.Tabs.Count;
        return await ActivateTabAsync(workspace.Tabs[destination].Id, cancellationToken);
    }

    public Task<bool> SelectLastActiveTabAsync(CancellationToken cancellationToken = default)
    {
        return RuntimeWorkspace?.LastActiveTab is { } lastActiveTab
            ? ActivateTabAsync(lastActiveTab.Id, cancellationToken)
            : Task.FromResult(false);
    }

    public Task<bool> SelectTabAtPositionAsync(
        int position,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        if (workspace is null || position < 0 || position >= workspace.Tabs.Count)
        {
            return Task.FromResult(false);
        }

        return ActivateTabAsync(workspace.Tabs[position].Id, cancellationToken);
    }

    public Task<bool> FocusPanelAsync(
        PanelFocusDirection direction,
        CancellationToken cancellationToken = default)
    {
        var panelId = RuntimeWorkspace?.ActiveTab?.FindPanel(direction);
        if (panelId is null)
        {
            return Task.FromResult(false);
        }

        return ActivatePanelAsync(panelId.Value, cancellationToken);
    }

    public bool ToggleActivePanelZoom()
    {
        if (RuntimeWorkspace?.ActiveTab?.ToggleActivePanelZoom() != true)
        {
            return false;
        }

        QueueRuntimeRecoverySnapshot();
        return true;
    }

    public async Task<bool> RenameActiveTabAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        var tab = RuntimeWorkspace?.ActiveTab;
        if (tab is null)
        {
            return false;
        }

        return await UpdateRuntimeTabIdentityAsync(
            tab.Id,
            title,
            tab.Icon,
            cancellationToken);
    }

    public async Task<bool> UpdateRuntimeTabIdentityAsync(
        TabInstanceId tabId,
        string title,
        string icon,
        CancellationToken cancellationToken = default)
    {
        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(candidate => candidate.Id == tabId);
        if (workspace is null
            || tab is null
            || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalizedTitle = title.Trim();
        var normalizedIcon = WorkspaceIcons.OptionFor(icon).Id;
        var titleChanged = !string.Equals(tab.Title, normalizedTitle, StringComparison.Ordinal);
        var iconChanged = !string.Equals(tab.Icon, normalizedIcon, StringComparison.Ordinal);
        if (!titleChanged && !iconChanged)
        {
            return true;
        }

        if (!titleChanged)
        {
            _ = tab.ChooseIcon(normalizedIcon);
            QueueRuntimeRecoverySnapshot();
            return true;
        }

        return await ReplaceRuntimeWorkspaceGraphAsync(
            workspace,
            "tab identity update",
            currentWorkspace =>
            {
                if (!currentWorkspace.Tabs.Contains(tab))
                {
                    return null;
                }

                return RenameRuntimeTab(
                    CaptureRuntimeWorkspaceGraph(currentWorkspace),
                    tab.Id,
                    normalizedTitle);
            },
            () =>
            {
                if (!tab.Rename(normalizedTitle))
                {
                    throw new InvalidOperationException(
                        "The runtime tab changed before the host-approved identity was applied.");
                }

                if (iconChanged)
                {
                    _ = tab.ChooseIcon(normalizedIcon);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Records an explicit icon choice even when the user picked the icon that
    /// was already displayed. Equality says whether pixels change, not whether
    /// the first panel still owns this field.
    /// </summary>
    public bool ChooseRuntimeTabIcon(TabInstanceId tabId, string icon)
    {
        var tab = RuntimeWorkspace?.Tabs.SingleOrDefault(candidate => candidate.Id == tabId);
        if (tab is null)
        {
            return false;
        }

        _ = tab.ChooseIcon(icon);
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    public bool EnterTerminalCopyMode() =>
        (ActivePanel as TerminalRuntimePanelViewModel)?.EnterCopyMode() == true;

    public bool ExitTerminalCopyMode() =>
        (ActivePanel as TerminalRuntimePanelViewModel)?.ExitCopyMode() == true;

    public void ClearError() => OperationError = null;

    public void ShowApplicationKeySequenceHint(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ApplicationKeySequenceHint = message.Trim();
    }

    public void ClearApplicationKeySequenceHint() => ApplicationKeySequenceHint = null;

    /// <summary>
    /// Raises an operation error.
    ///
    /// It is also written to standard error. The banner is transient, sits against
    /// the window edge, and has been seen showing with nothing legible in it — an
    /// error the user cannot read is the same as no error at all, and the text is
    /// the only thing that says what went wrong.
    /// </summary>
    public void SetError(string message)
    {
        OperationError = message;
    }

    public void SetDefinitionBundleStatus(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        DefinitionBundleStatus = message.Trim();
    }

    private void OnCatalogChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            RefreshCatalog(_catalog.Snapshot);
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshCatalog(_catalog.Snapshot));
        }
    }

    private void OnFileTransferStatePropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        var propertyName = eventArgs.PropertyName switch
        {
            nameof(FileTransferViewModel.HasTransfers) => nameof(HasFileTransfers),
            nameof(FileTransferViewModel.HasNoTransfers) => nameof(HasNoFileTransfers),
            nameof(FileTransferViewModel.ActiveCount) => nameof(ActiveFileTransferCount),
            nameof(FileTransferViewModel.FailedCount) => nameof(FailedFileTransferCount),
            nameof(FileTransferViewModel.StatusText) => nameof(FileTransferStatusText),
            _ => null,
        };
        if (propertyName is not null)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void RefreshDefaultAgentPolicyOptions() =>
        DefaultAgentPolicySettings.RefreshProviders(_aiProviderRuntime?.Profiles);

    private void OnDefaultAgentPolicySettingsPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        var propertyName = eventArgs.PropertyName switch
        {
            nameof(DefaultAgentPolicySettingsViewModel.Editor) =>
                nameof(DefaultAgentPolicy),
            nameof(DefaultAgentPolicySettingsViewModel.CanSave) =>
                nameof(CanSaveDefaultAgentPolicy),
            _ => null,
        };
        if (propertyName is not null)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnDefaultAgentPolicyChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ActivateWorkspaceAgentChat(RuntimeWorkspace?.Id);
    }

    private void StartTrackingRecovery(RuntimeWorkspaceViewModel? workspace)
        => RuntimeRecovery.Track(workspace);

    private void StartTrackingRecovery(RuntimePanelViewModel panel)
        => RuntimeRecovery.Track(panel);

    private void StopTrackingRecovery(RuntimeWorkspaceViewModel? workspace)
        => RuntimeRecovery.Untrack(workspace);

    private void StopTrackingRecovery(RuntimePanelViewModel panel)
        => RuntimeRecovery.Untrack(panel);

    private void QueueRuntimeRecoverySnapshot()
        => RuntimeRecovery.QueueSnapshot();

    private async Task RefreshPanelsAfterTransferAsync(
        FilePanelTransferSnapshot transfer)
    {
        if (RuntimeWorkspace is null || _shutdownStarted)
        {
            return;
        }

        var profileIds = new HashSet<string>(StringComparer.Ordinal)
        {
            transfer.Request.Source.ProviderProfileId,
            transfer.EffectiveDestination.ProviderProfileId,
        };
        var panels = RuntimeWorkspace.Tabs
            .SelectMany(tab => tab.Panels)
            .OfType<FileRuntimePanelViewModel>()
            .Where(panel => panel.SelectedProfile is not null
                && profileIds.Contains(panel.SelectedProfile.Id))
            .ToArray();

        foreach (var panel in panels)
        {
            try
            {
                await panel
                    .RefreshAsync(_runtimeGraphLifetime.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
                when (_runtimeGraphLifetime.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private IFileTransferQueueClient ResolveFileTransferQueue(FilePanelTransferId id) =>
        RuntimeWorkspace?.Tabs
            .SelectMany(tab => tab.Panels)
            .OfType<FileRuntimePanelViewModel>()
            .Select(panel => panel.HostedClient)
            .OfType<IFileTransferQueueClient>()
            .FirstOrDefault(queue => queue.Transfers.Any(transfer => transfer.Id == id))
        ?? _fileTransferQueue;

    public async Task<bool> OpenRecentSessionAsync(
        RecentSessionHistoryItemViewModel recentSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recentSession);
        if (!CanOpenDefinition(recentSession.SourceDefinition))
        {
            SetError(
                "The current saved definition for that recent session is missing or unavailable on this platform.");
            return false;
        }

        // Launch, not open: reopening something joins the workspace you are in
        // rather than standing up one of its own beside it. Opening has never
        // been allowed to replace what is already running, and arriving
        // detached from the workspace the request came from is the same
        // surprise wearing a different coat.
        var source = recentSession.SourceDefinition;
        if (source.Kind == ConnectionProfile.Kind)
        {
            return await LaunchConnectionAsync(
                new ConnectionId(source.Value),
                cancellationToken);
        }

        if (source.Kind == ScreenDefinition.Kind)
        {
            return await LaunchScreenAsync(new ScreenId(source.Value), cancellationToken);
        }

        if (source.Kind == WorkspaceDefinition.Kind)
        {
            return await OpenWorkspaceAsync(new WorkspaceId(source.Value), cancellationToken);
        }

        SetError("That recent-session definition type cannot be reopened here.");
        return false;
    }

    public async Task<bool> ClearRecentSessionsAsync(CancellationToken cancellationToken)
    {
        var cutoff = History.CaptureClearCutoff();
        return await ClearRecentSessionsAsync(cutoff, cancellationToken);
    }

    public RecentSessionClearCutoff CaptureRecentSessionClearCutoff() =>
        History.CaptureClearCutoff();

    public async Task<bool> ClearRecentSessionsAsync(
        RecentSessionClearCutoff cutoff,
        CancellationToken cancellationToken)
    {
        return await History.ClearAsync(cutoff, cancellationToken);
    }

    public async Task<bool> ResetUnreadableRecentSessionsAsync(
        CancellationToken cancellationToken)
    {
        return await History.ResetUnreadableAsync(cancellationToken);
    }

    public async Task<RecentSessionStoreResult<RecentSessionRetentionUpdateResult>>
        SaveHistoryRetentionAsync(CancellationToken cancellationToken)
    {
        return await History.SaveRetentionAsync(cancellationToken);
    }

    public async Task<ApplicationRunResult<Unit>> FlushRecentSessionHistoryAsync(
        CancellationToken cancellationToken)
    {
        return await History.DrainAsync(cancellationToken);
    }

    /// <summary>
    /// Re-derives what the rails show about running workspaces: which are open,
    /// which one is in front, and which are asking to be noticed.
    ///
    /// Derived rather than pushed, because the rail lists saved definitions
    /// while "open" is a fact about runtime instances. The join is the
    /// definition key, the same one <see cref="FindOpenWorkspace"/> uses.
    /// </summary>
    private void RefreshWorkspaceRuntimeFlags() =>
        RefreshWorkspaceRuntimeFlags(_presentedCatalogSnapshot);

    private void RefreshWorkspaceRuntimeFlags(DefinitionCatalogSnapshot snapshot)
    {
        var durableIsolationByWorkspace = snapshot.Workspaces.ToDictionary(
            stored => stored.Value.Id,
            stored => stored.Value.IsIsolated);
        foreach (var item in Workspaces)
        {
            var runtime = FindOpenWorkspace(
                new DefinitionKey(WorkspaceDefinition.Kind, item.Id.Value));
            item.IsOpen = runtime is not null;
            item.IsInFront = _workspaceIsolationStartingWorkspaceId is { } startingId
                ? item.Id == startingId
                : runtime is not null && ReferenceEquals(runtime, RuntimeWorkspace);
            item.HasAttention = runtime?.HasAttention == true;
            item.HasAgentActivity = runtime?.HasAgentActivity == true;
            // An open workspace reports the execution boundary it actually
            // acquired. Once closed, the tile returns to current durable intent.
            item.IsIsolated = runtime is not null
                ? runtime.IsolationBinding is not null
                : durableIsolationByWorkspace.GetValueOrDefault(item.Id);
        }

        OnPropertyChanged(nameof(HasWorkspaceAttention));
        OnPropertyChanged(nameof(HasWorkspaceAgentActivity));
    }

    /// <summary>
    /// Whether anything in any workspace wants attention.
    ///
    /// The rail marks the workspace it happened in, but the rail can be turned
    /// off — and then nothing said so at all. The menu that lists workspaces
    /// carries the mark instead, so a workspace calling from behind a hidden
    /// rail is still visible from the chrome.
    /// </summary>
    public bool HasWorkspaceAttention =>
        !ShowWorkspacesPanel && Workspaces.Any(item => item.HasAttention);

    public bool HasWorkspaceAgentActivity =>
        Workspaces.Any(item => item.HasAgentActivity);

    /// <summary>
    /// Whether any open workspace has a live agent turn. Unlike panel activity,
    /// this begins before the first tool call and remains true for provider
    /// reasoning, approval, and tool phases, including background workspaces.
    /// </summary>
    public bool HasRunningAgent =>
        _workspaceAgentChats.Values.Any(owned => owned.ViewModel.IsBusy);

    /// <summary>
    /// The running instance of a saved workspace, if it is running. The rail
    /// lists definitions and the host speaks in instances, so closing from the
    /// rail has to cross that boundary somewhere; here is the only place that
    /// already knows how.
    /// </summary>
    public WorkspaceInstanceId? OpenWorkspaceInstance(WorkspaceId workspaceId) =>
        FindOpenWorkspace(new DefinitionKey(WorkspaceDefinition.Kind, workspaceId.Value))?.Id;

    /// <summary>
    /// Takes a workspace out of the shell once the host has ended its sessions,
    /// falling back to the one you were in before it. Closing the last one
    /// leaves nothing to fall back to, so the launcher takes over.
    /// </summary>
    public void RemoveRuntimeWorkspace(WorkspaceInstanceId workspaceId)
    {
        if (_openWorkspaces.FirstOrDefault(runtime => runtime.Id == workspaceId)
            is not { } runtime)
        {
            return;
        }

        CloseRuntimeWorkspace(runtime);
        if (RuntimeWorkspace is null)
        {
            Route = ShellRoute.Workspace;
        }
    }

    private RuntimeWorkspaceViewModel? FindOpenWorkspace(DefinitionKey definition) =>
        _openWorkspaces.FirstOrDefault(runtime =>
            _runtimeSources.TryGetValue(runtime.Id, out var source)
            && source.SourceDefinition == definition);

    /// <summary>
    /// The one way a runtime workspace goes on screen.
    ///
    /// <paramref name="sourceDefinition"/> is null for the workspaces that have
    /// no saved definition behind them — a local browser, a monitor, an ad-hoc
    /// database. Those still belong in the open set: membership is what stops
    /// the next switch from disposing their panels, and it was the absence of
    /// it that made "open a browser, then click a workspace tile" throw the
    /// browser away.
    /// </summary>
    private void ActivateRuntimeWorkspace(
        RuntimeWorkspaceViewModel runtime,
        DefinitionKey? sourceDefinition,
        string durableTitle)
    {
        if (sourceDefinition is { } definition)
        {
            _runtimeSources[runtime.Id] = new RuntimeHistorySource(definition, durableTitle);
        }

        BringToFrontOfOpenSet(runtime);
        RuntimeWorkspace = runtime;
        Notifications.Watch(runtime);
        StartAcceptedRuntimePanels(runtime);
        TrackRecentSessions(runtime.Tabs.SelectMany(tab => tab.Panels));
        StartRuntimeGraphWatch(runtime);
        RefreshWorkspaceRuntimeFlags();
        MarkVisibleNotificationsSeen();
    }

    /// <summary>
    /// Brings an already-open workspace back to the front. Nothing is started
    /// or restored: its sessions never stopped.
    /// </summary>
    private void ReactivateRuntimeWorkspace(RuntimeWorkspaceViewModel runtime)
    {
        var activation = new ActivationTrace();
        _activation = activation;
        try
        {
            BringToFrontOfOpenSet(runtime);
            RuntimeWorkspace = runtime;
            Notifications.Watch(runtime);
            StartRuntimeGraphWatch(runtime);
            activation.Mark("graph watch");
            RefreshWorkspaceRuntimeFlags();
            activation.Mark("rail flags");
            MarkVisibleNotificationsSeen();
            activation.Mark("seen marks");
        }
        finally
        {
            _activation = null;
        }

        if (activation.TryDescribe(ActivationBudgetMilliseconds, out _))
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "activation.performance-budget-exceeded",
                SecretSafeDiagnosticKind.Unexpected);
        }
    }

    /// <summary>
    /// Roughly two frames. Under it, bringing a workspace forward is not felt.
    /// </summary>
    private const long ActivationBudgetMilliseconds = 32;

    private ActivationTrace? _activation;

    /// <summary>
    /// Times the steps of bringing a workspace forward, so a slow one says
    /// which step it was.
    ///
    /// All of this runs on the thread that draws, and the phase timing around
    /// it could only say "the view model" — which is most of a third of a
    /// second and a dozen different things. The steps are marked where they
    /// happen, including inside the property setter that does most of them,
    /// because that is the only place their boundaries exist.
    /// </summary>
    private sealed class ActivationTrace
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(string Step, long Milliseconds)> _steps = [];
        private long _previous;

        public void Mark(string step)
        {
            var elapsed = _clock.ElapsedMilliseconds;
            _steps.Add((step, elapsed - _previous));
            _previous = elapsed;
        }

        public bool TryDescribe(long budgetMilliseconds, out string description)
        {
            _clock.Stop();
            var total = _clock.ElapsedMilliseconds;
            if (total < budgetMilliseconds)
            {
                description = string.Empty;
                return false;
            }

            description = $"{total} ms: "
                + string.Join(
                    ", ",
                    _steps.Select(step => $"{step.Step} {step.Milliseconds} ms"));
            return true;
        }
    }

    /// <summary>
    /// Keeps the open set in the order the workspaces were last looked at,
    /// least recent first. That ordering is what makes "the one before this
    /// one" answerable when the workspace in front is closed — appending on
    /// first open only ever gave the oldest, which is rarely where you were.
    /// </summary>
    private void BringToFrontOfOpenSet(RuntimeWorkspaceViewModel runtime)
    {
        if (_runtimeSources.TryGetValue(runtime.Id, out var source))
        {
            if (!_workspaceDefinitionOccupancy.TryRegisterRuntime(
                WindowId,
                runtime.Id,
                source.SourceDefinition))
            {
                throw new InvalidOperationException(
                    "The workspace definition is being changed while its runtime activates.");
            }
        }

        var index = _openWorkspaces.IndexOf(runtime);
        if (index < 0)
        {
            _openWorkspaces.Add(runtime);
            return;
        }

        if (index != _openWorkspaces.Count - 1)
        {
            _openWorkspaces.Move(index, _openWorkspaces.Count - 1);
        }
    }

    /// <summary>
    /// Closes the workspace in front, and shows the launcher only when it was
    /// the last one. Another open workspace is somewhere to go back to.
    /// </summary>
    private void CloseActiveRuntimeWorkspace()
    {
        if (RuntimeWorkspace is { } runtime)
        {
            CloseRuntimeWorkspace(runtime);
        }

        if (RuntimeWorkspace is null)
        {
            Route = ShellRoute.Workspace;
        }
    }

    /// <summary>
    /// Closes a workspace for good: it leaves the open set, its panels are
    /// disposed, and if it was the one in front another takes its place.
    /// </summary>
    private void CloseRuntimeWorkspace(RuntimeWorkspaceViewModel runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        try
        {
            var wasActive = ReferenceEquals(RuntimeWorkspace, runtime);
            _openWorkspaces.Remove(runtime);
            _workspaceDefinitionOccupancy.Unregister(WindowId, runtime.Id);
            _runtimeSources.Remove(runtime.Id);
            _workspaceTerminalMultiplexingModes.Remove(runtime.Id);
            Notifications.Forget(runtime);
            if (wasActive)
            {
                // The setter disposes what is no longer in the open set, so the
                // removal above is what makes this a close rather than a switch.
                RuntimeWorkspace = _openWorkspaces.LastOrDefault();
                if (RuntimeWorkspace is { } next)
                {
                    ReactivateRuntimeWorkspace(next);
                }
                else
                {
                    // Nothing left to fall back to, and the rail still has the tile
                    // it was closed from marked as running.
                    RefreshWorkspaceRuntimeFlags();
                }

                RemoveWorkspaceAgentChat(runtime.Id);
                return;
            }

            StopTrackingRecovery(runtime);
            AgentWorkspaceScope.StopTracking(runtime);
            runtime.DisposePanels();
            RemoveWorkspaceAgentChat(runtime.Id);
            RefreshWorkspaceRuntimeFlags();
        }
        finally
        {
            ScheduleWorkspaceNetworkCleanup(runtime.Id);
            _ = ScheduleWorkspaceIsolationCleanup(runtime);
        }
    }

    private void DisposeRuntimeWorkspaceUnlessOwned(
        RuntimeWorkspaceViewModel runtime)
    {
        if (!_openWorkspaces.Contains(runtime))
        {
            _runtimeSources.Remove(runtime.Id);
            _workspaceTerminalMultiplexingModes.Remove(runtime.Id);
            runtime.DisposePanels();
            ScheduleWorkspaceNetworkCleanup(runtime.Id);
            if (!RequiresWorkspaceGraphRollback(runtime.Id))
            {
                _ = ScheduleWorkspaceIsolationCleanup(runtime);
            }
        }
    }

    private void StartRuntimeGraphWatch(RuntimeWorkspaceViewModel runtime)
        => RuntimeGraph.StartWatching(runtime);

    private void StopRuntimeGraphWatch()
        => RuntimeGraph.StopWatching();

    private async Task<bool> RegisterRuntimeWorkspaceAsync(
        RuntimeWorkspaceViewModel runtime,
        CancellationToken cancellationToken)
    {
        await PrepareWorkspaceNetworkAsync(runtime, cancellationToken);
        var registered = await RuntimeGraph.RegisterAsync(runtime, cancellationToken);
        if (!registered)
        {
            ScheduleWorkspaceNetworkCleanup(runtime.Id);
        }

        return registered;
    }

    private bool HasRuntimeWorkspacePanelCapacity(
        RuntimeWorkspaceViewModel workspace,
        int removedPanelCount,
        int addedPanelCount)
    {
        var proposedPanelCount = workspace.Tabs.Sum(tab => (long)tab.Panels.Count)
            - removedPanelCount
            + addedPanelCount;
        if (proposedPanelCount <= WorkspaceInstance.MaximumPanelCount)
        {
            return true;
        }

        SetError(
            $"A workspace can contain at most " +
            $"{WorkspaceInstance.MaximumPanelCount} panels.");
        return false;
    }

    private Task<bool> ReplaceRuntimeWorkspaceGraphAsync(
        RuntimeWorkspaceViewModel runtime,
        string operation,
        Func<RuntimeWorkspaceViewModel, WorkspaceInstance?> buildProposal,
        Action commit,
        CancellationToken cancellationToken,
        RuntimeGraphStaleProposalHandling staleProposalHandling =
            RuntimeGraphStaleProposalHandling.RefreshAndRetry) =>
        RuntimeGraph.ReplaceAsync(
            runtime,
            operation,
            buildProposal,
            commit,
            cancellationToken,
            staleProposalHandling);

    // The caller owns _runtimeGraphGate. Keeping proposal submission and the
    // observable commit in one critical section prevents optimistic UI order.
    private Task<bool> ReplaceRuntimeWorkspaceGraphUnderGateAsync(
        RuntimeWorkspaceViewModel runtime,
        WorkspaceInstance proposal,
        string operation,
        Action commit,
        RuntimeGraphStaleProposalHandling staleProposalHandling,
        CancellationToken cancellationToken,
        Func<RuntimeWorkspaceViewModel, WorkspaceInstance?>? rebuildProposal = null) =>
        RuntimeGraph.ReplaceUnderGateAsync(
            runtime,
            proposal,
            operation,
            commit,
            staleProposalHandling,
            cancellationToken,
            rebuildProposal);

    // The caller owns _runtimeGraphGate and has already revalidated the live
    // workspace and the operation-specific intent.
    private Task<bool> UnregisterRuntimeWorkspaceUnderGateAsync(
        RuntimeWorkspaceViewModel runtime,
        string operation,
        Action commit,
        CancellationToken cancellationToken) =>
        RuntimeGraph.UnregisterUnderGateAsync(
            runtime,
            operation,
            commit,
            cancellationToken);

    private static bool WorkspaceTopologyMatches(
        WorkspaceInstance expected,
        WorkspaceInstance actual) =>
        RuntimeWorkspaceGraphProjection.TopologyMatches(expected, actual);

    private static WorkspaceInstance CaptureRuntimeWorkspaceGraph(
        RuntimeWorkspaceViewModel workspace) =>
        RuntimeWorkspaceGraphProjection.Capture(workspace);

    /// <summary>
    /// The tab as the session host knows it.
    ///
    /// A placed but unanswered cell is a panel like any other. It carries no
    /// session — a panel's session has always been optional — but it occupies the
    /// layout, it can be selected, and it can be the only thing in a tab, which is
    /// what a tab opened straight onto the launcher is.
    ///
    /// It was once left out, so the host would not be asked to activate a cell it
    /// had never heard of. That bought three rules about when the two sides were
    /// allowed to disagree, and the disagreement leaked anyway.
    /// </summary>
    private static TabInstance CaptureRuntimeTab(RuntimeTabViewModel tab)
        => RuntimeWorkspaceGraphProjection.CaptureTab(tab);

    private static WorkspaceInstance? BuildTabAppendProposal(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tab);
        var current = CaptureRuntimeWorkspaceGraph(workspace);
        return current.Tabs.Any(item => item.Id == tab.Id)
            ? null
            : new WorkspaceInstance(
                current.Id,
                current.Title,
                current.Tabs.Append(CaptureRuntimeTab(tab)),
                tab.Id);
    }

    private static WorkspaceInstance? BuildPanelRemovalProposal(
        RuntimeWorkspaceViewModel workspace,
        TabInstanceId tabId,
        PanelInstanceId panelId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var current = CaptureRuntimeWorkspaceGraph(workspace);
        var currentTab = current.Tabs.SingleOrDefault(item => item.Id == tabId);
        if (currentTab is null
            || currentTab.Panels.Count <= 1
            || currentTab.Panels.All(item => item.Id != panelId))
        {
            return null;
        }

        var remainingPanels = currentTab.Panels
            .Where(item => item.Id != panelId)
            .ToArray();
        var removedIndex = currentTab.Panels
            .Select((item, index) => (item, index))
            .Single(item => item.item.Id == panelId)
            .index;
        var activePanelId = currentTab.ActivePanelId == panelId
            ? remainingPanels[Math.Min(removedIndex, remainingPanels.Length - 1)].Id
            : currentTab.ActivePanelId;
        return ReplaceRuntimeTab(
            current,
            new TabInstance(
                currentTab.Id,
                currentTab.Title,
                remainingPanels,
                activePanelId),
            current.ActiveTabId);
    }

    private static WorkspaceInstance? BuildTabRemovalProposal(
        RuntimeWorkspaceViewModel workspace,
        TabInstanceId tabId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var current = CaptureRuntimeWorkspaceGraph(workspace);
        if (current.Tabs.Count <= 1
            || current.Tabs.All(item => item.Id != tabId))
        {
            return null;
        }

        var remainingTabs = current.Tabs.Where(item => item.Id != tabId).ToArray();
        // The tab before the one going, or the one that takes its place at the
        // front. This has to be the same choice the commit makes, or the client
        // activates one tab while the host was told another.
        var removedAt = 0;
        for (var index = 0; index < current.Tabs.Count; index++)
        {
            if (current.Tabs[index].Id == tabId)
            {
                removedAt = index;
                break;
            }
        }

        var activeTabId = current.ActiveTabId == tabId
            ? remainingTabs[Math.Max(0, removedAt - 1)].Id
            : current.ActiveTabId;
        return new WorkspaceInstance(
            current.Id,
            current.Title,
            remainingTabs,
            activeTabId);
    }

    private static WorkspaceInstance AppendRuntimePanel(
        WorkspaceInstance workspace,
        TabInstanceId tabId,
        PanelInstance panel)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(panel);
        var tab = workspace.Tabs.Single(item => item.Id == tabId);
        var replacement = new TabInstance(
            tab.Id,
            tab.Title,
            tab.Panels.Append(panel),
            panel.Id);
        return ReplaceRuntimeTab(workspace, replacement, tabId);
    }

    /// <summary>
    /// Swaps one panel for another in place, which is what answering a placed cell
    /// does: the cell was already part of the graph, so the tab does not grow.
    /// </summary>
    private static WorkspaceInstance ReplaceRuntimePanel(
        WorkspaceInstance workspace,
        TabInstanceId tabId,
        PanelInstanceId replacedPanelId,
        PanelInstance panel)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(panel);
        var tab = workspace.Tabs.Single(item => item.Id == tabId);
        var replacement = new TabInstance(
            tab.Id,
            tab.Title,
            tab.Panels.Select(item => item.Id == replacedPanelId ? panel : item),
            panel.Id);
        return ReplaceRuntimeTab(workspace, replacement, tabId);
    }

    private static WorkspaceInstance RenameRuntimeTab(
        WorkspaceInstance workspace,
        TabInstanceId tabId,
        string title)
    {
        var tab = workspace.Tabs.Single(item => item.Id == tabId);
        return ReplaceRuntimeTab(
            workspace,
            new TabInstance(tab.Id, title, tab.Panels, tab.ActivePanelId),
            workspace.ActiveTabId);
    }

    private static WorkspaceInstance ReplaceRuntimeTab(
        WorkspaceInstance workspace,
        TabInstance replacement,
        TabInstanceId activeTabId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(replacement);
        if (workspace.Tabs.All(tab => tab.Id != replacement.Id))
        {
            throw new ArgumentOutOfRangeException(
                nameof(replacement),
                "The replacement tab must belong to the runtime workspace.");
        }

        return new WorkspaceInstance(
            workspace.Id,
            workspace.Title,
            workspace.Tabs.Select(tab => tab.Id == replacement.Id ? replacement : tab),
            activeTabId);
    }

    private void TrackRecentSession(RuntimePanelViewModel panel) =>
        TrackRecentSessions([panel]);

    private void TrackRecentSessions(IEnumerable<RuntimePanelViewModel> panels)
    {
        foreach (var panel in panels)
        {
            var source = ResolveRuntimeHistorySource(panel);
            var identity = panel switch
            {
                TerminalRuntimePanelViewModel { SessionRequest: { } request } =>
                    new RuntimeSessionIdentity(request.SessionId, PanelKind.Terminal),
                BrowserRuntimePanelViewModel { SessionRequest: { } request } =>
                    new RuntimeSessionIdentity(request.SessionId, PanelKind.Browser),
                FileRuntimePanelViewModel { HostedClient: { } hosted } =>
                    new RuntimeSessionIdentity(hosted.SessionId, PanelKind.FileViewer),
                StatisticsRuntimePanelViewModel { HasHostedSession: true } statistics =>
                    new RuntimeSessionIdentity(statistics.SessionId, PanelKind.Statistics),
                ProcessMonitorRuntimePanelViewModel { HasHostedSession: true } processes =>
                    new RuntimeSessionIdentity(processes.SessionId, PanelKind.ProcessMonitor),
                _ => null,
            };
            if (source is null
                || identity is null
                || !_recentSessionIds.TryAdd(panel.Id, identity.SessionId))
            {
                continue;
            }

            _ = History.RecordStartedAsync(
                identity.SessionId,
                source.SourceDefinition,
                identity.Kind,
                source.DurableTitle);
        }
    }

    private RuntimeHistorySource? ResolveRuntimeHistorySource(RuntimePanelViewModel panel)
    {
        var sourceTab = RuntimeWorkspace?.Tabs.FirstOrDefault(
            tab => tab.Panels.Contains(panel));
        return sourceTab?.HistorySource ?? _runtimeHistorySource;
    }

    private void RecordRecentSessionCompletions(HostResult<CloseScopeResult> result)
    {
        if (result is not HostResult<CloseScopeResult>.Success
            {
                Value: CloseScopeResult.Completed completed,
            })
        {
            return;
        }

        var completions = completed.Sessions
            .Select(item => (item.SessionId, Outcome: MapRecentSessionOutcome(item.Outcome)))
            .Where(item => item.Outcome is not null)
            .Select(item => (item.SessionId, Outcome: item.Outcome!.Value))
            .ToArray();
        QueueRecentSessionCompletions(completions);
    }

    private void QueueRecentSessionCompletion(
        PanelInstanceId panelId,
        RecentSessionOutcome outcome)
    {
        if (!_recentSessionIds.Remove(panelId, out var sessionId))
        {
            return;
        }

        QueueRecentSessionCompletions([(sessionId, outcome)]);
    }

    private void QueueRemainingRecentSessionCompletions(RecentSessionOutcome outcome)
    {
        if (_recentSessionIds.Count == 0)
        {
            return;
        }

        var completions = _recentSessionIds.Values
            .Select(sessionId => (sessionId, outcome))
            .ToArray();
        _recentSessionIds.Clear();
        QueueRecentSessionCompletions(completions);
    }

    private void QueueRecentSessionCompletions(
        IReadOnlyList<(SessionId SessionId, RecentSessionOutcome Outcome)> completions)
    {
        if (completions.Count == 0)
        {
            return;
        }

        var trackedSessionIds = completions
            .Select(item => item.SessionId)
            .ToHashSet();
        foreach (var panelId in _recentSessionIds
            .Where(item => trackedSessionIds.Contains(item.Value))
            .Select(item => item.Key)
            .ToArray())
        {
            _recentSessionIds.Remove(panelId);
        }

        _ = History.RecordCompletionsAsync(
            completions,
            refreshAfterWrite: !_shutdownStarted);
    }

    /// <summary>
    /// Pushes the saved terminal profile to every open panel.
    ///
    /// A panel captured its render profile when it launched, so changing the
    /// terminal font changed the stored definition and nothing visible: open
    /// panels kept the size they started with until they were closed and
    /// reopened. Typography is applied in place — restarting the session to change
    /// a font would throw away the scrollback with it.
    /// </summary>
    private void RefreshOpenTerminalRenderProfiles()
    {
        var snapshot = _appearancePreview.Current.TerminalRenderProfile
            ?? (ActiveTerminalProfile is { } profile
                ? TerminalRenderProfileSnapshot.FromProfile(profile)
                : null);
        if (snapshot is null)
        {
            return;
        }

        var workspaces = _openWorkspaces
            .Concat(RuntimeWorkspace is null ? [] : [RuntimeWorkspace])
            .DistinctBy(workspace => workspace.Id);
        foreach (var panel in workspaces
                     .SelectMany(workspace => workspace.Tabs)
                     .SelectMany(tab => tab.Panels)
                     .OfType<TerminalRuntimePanelViewModel>())
        {
            panel.RenderProfile = snapshot;
        }
    }

    private void OnAppearancePreviewChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        RefreshOpenTerminalRenderProfiles();
    }

    private RecentSessionHistoryItemViewModel ToRecentSessionItem(
        RecentSessionRecord record,
        DateTimeOffset observedAt)
    {
        // A row is read as "what would I reconnect to", so it carries the saved
        // definition's transport and endpoint rather than only the session's own
        // metadata. Resolving it here — instead of storing it in history — keeps
        // the row truthful after the connection is edited, and leaves it null for
        // a definition that no longer exists.
        var connection = record.SourceDefinition.Kind == ConnectionProfile.Kind
            ? Connections.FirstOrDefault(item => string.Equals(item.Id.Value, record.SourceDefinition.Value, StringComparison.Ordinal))
            : null;

        return new RecentSessionHistoryItemViewModel(
            record,
            CanOpenDefinition(record.SourceDefinition),
            observedAt,
            connection?.Kind,
            connection?.Detail);
    }

    private void OnHistorySnapshotChanged(object? sender, EventArgs eventArgs)
    {
        // These are joins with root-owned runtime/catalog state. The history
        // component intentionally knows nothing about either side.
        RefreshWorkspaceRuntimeFlags();
        Launcher.RefreshSearchResults();
    }

    private void OnHistoryPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        var propertyNames = eventArgs.PropertyName switch
        {
            nameof(RecentSessionHistoryViewModel.HasRecentSessions) =>
                new[] { nameof(HasRecentSessions) },
            nameof(RecentSessionHistoryViewModel.HasNoRecentSessions) =>
                [nameof(HasNoRecentSessions)],
            nameof(RecentSessionHistoryViewModel.HasSessions) =>
                [nameof(HasHistorySessions)],
            nameof(RecentSessionHistoryViewModel.HasNoSessions) =>
                [nameof(HasNoHistorySessions)],
            nameof(RecentSessionHistoryViewModel.HasFilteredSessions) =>
                [nameof(HasFilteredHistorySessions)],
            nameof(RecentSessionHistoryViewModel.HasNoFilteredSessions) =>
                [nameof(HasNoFilteredHistorySessions)],
            nameof(RecentSessionHistoryViewModel.HasFailure) =>
                [nameof(HasRecentSessionFailure)],
            nameof(RecentSessionHistoryViewModel.HasUnreadableHistory) =>
                [nameof(HasUnreadableRecentSessionHistory)],
            nameof(RecentSessionHistoryViewModel.IsLoading) =>
                [nameof(IsHistoryLoading)],
            nameof(RecentSessionHistoryViewModel.IsMutating) =>
                [nameof(IsHistoryMutating)],
            nameof(RecentSessionHistoryViewModel.IsExporting) =>
                [nameof(IsHistoryExporting)],
            nameof(RecentSessionHistoryViewModel.CanRetry) =>
                [nameof(CanRetryRecentSessionHistory)],
            nameof(RecentSessionHistoryViewModel.CanClear) =>
                [nameof(CanClearRecentSessionHistory)],
            nameof(RecentSessionHistoryViewModel.CanReset) =>
                [nameof(CanResetRecentSessionHistory)],
            nameof(RecentSessionHistoryViewModel.CanExportAll) =>
                [nameof(CanExportAllHistory)],
            nameof(RecentSessionHistoryViewModel.CanExportFiltered) =>
                [nameof(CanExportFilteredHistory)],
            nameof(RecentSessionHistoryViewModel.ResultCount) =>
                [nameof(HistoryResultCount)],
            nameof(RecentSessionHistoryViewModel.SearchEmptyState) =>
                [nameof(HistorySearchEmptyState)],
            nameof(RecentSessionHistoryViewModel.RecentSessionStatus) =>
                [nameof(RecentSessionStatus)],
            nameof(RecentSessionHistoryViewModel.ExportStatus) =>
                [nameof(HistoryExportStatus)],
            nameof(RecentSessionHistoryViewModel.RetentionStatus) =>
                [nameof(HistoryRetentionStatus)],
            nameof(RecentSessionHistoryViewModel.CanManageRetention) =>
                [nameof(CanManageHistoryRetention)],
            nameof(RecentSessionHistoryViewModel.SelectedRetentionOption) =>
                [nameof(SelectedHistoryRetentionOption)],
            nameof(RecentSessionHistoryViewModel.HasPendingRetentionChange) =>
                [nameof(HasPendingHistoryRetentionChange)],
            nameof(RecentSessionHistoryViewModel.CanApplyRetention) =>
                [nameof(CanApplyHistoryRetention)],
            nameof(RecentSessionHistoryViewModel.RequiresRetentionConfirmation) =>
                [nameof(RequiresHistoryRetentionConfirmation)],
            nameof(RecentSessionHistoryViewModel.SearchQuery) =>
                [nameof(HistorySearchQuery)],
            nameof(RecentSessionHistoryViewModel.SelectedSession) =>
                [nameof(SelectedHistorySession)],
            nameof(RecentSessionHistoryViewModel.HasSelectedSession) =>
                [nameof(HasSelectedHistorySession)],
            nameof(RecentSessionHistoryViewModel.HasNoSelectedSession) =>
                [nameof(HasNoSelectedHistorySession)],
            nameof(RecentSessionHistoryViewModel.SelectedExportScope) =>
                [nameof(SelectedHistoryExportScope)],
            _ => [],
        };

        foreach (var propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private bool CanOpenDefinition(DefinitionKey key) => key.Kind switch
    {
        var kind when kind == ConnectionProfile.Kind => Connections
            .Any(item => string.Equals(item.Id.Value, key.Value, StringComparison.Ordinal) && item.CanOpen),
        var kind when kind == ScreenDefinition.Kind => _catalog.Snapshot.Screens
            .Any(item => item.Value.Key == key),
        var kind when kind == WorkspaceDefinition.Kind => _catalog.Snapshot.Workspaces
            .Any(item => item.Value.Key == key),
        _ => false,
    };

    private static RecentSessionOutcome? MapRecentSessionOutcome(
        SessionCloseOutcome outcome) => outcome switch
        {
            SessionCloseOutcome.GracefullyClosed or SessionCloseOutcome.AlreadyClosed =>
                RecentSessionOutcome.GracefullyClosed,
            SessionCloseOutcome.ForceTerminated => RecentSessionOutcome.ForceTerminated,
            SessionCloseOutcome.EngineFailed => RecentSessionOutcome.Failed,
            SessionCloseOutcome.Cancelled or SessionCloseOutcome.ConfirmationRequired => null,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

    private void BeginEdit(
        DefinitionKey key,
        long revision,
        string name,
        string? description)
    {
        DefinitionEdit.Begin(key, revision, name, description);
        ShowOverlay(ShellOverlay.DefinitionEditor);
    }

    private bool TryDismissOverlayForNavigation()
    {
        if (LayoutDesignerEditor?.RequestCancel()
            == LayoutDesignerCancelDisposition.ConfirmDiscard)
        {
            SetError("Save or discard the layout changes before leaving this view.");
            return false;
        }

        if (WorkspaceEditor?.RequestCancel()
            == WorkspaceEditorCancelDisposition.ConfirmDiscard)
        {
            SetError("Save or discard the workspace changes before leaving this view.");
            return false;
        }

        DefinitionSettings.DismissLayoutDesigner();
        WorkspaceSettings.Dismiss();
        DefinitionEdit.Clear();
        Overlay = ShellOverlay.None;
        return true;
    }

    /// <summary>
    /// Rebuilds every launcher list from a catalog snapshot.
    ///
    /// Internal rather than private so a test can drive it directly: the catalog's
    /// own notification hops through the dispatcher, and in a unit test the UI
    /// thread is whichever thread happened to touch Avalonia first, so the hop may
    /// never be pumped.
    /// </summary>
    internal void RefreshCatalog(DefinitionCatalogSnapshot snapshot)
    {
        _presentedCatalogSnapshot = snapshot;
        var workspaces = snapshot.Workspaces
            .OrderBy(item => item.Value.SortOrder)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value.Id.Value, StringComparer.Ordinal)
            .Select(item => new LauncherWorkspaceViewModel(
                item.Value.Id,
                item.Revision,
                item.Value.Name,
                item.Value.Description ?? "No description",
                // The tile carries the workspace's identity colour, which is
                // its own field now; an accent-only workspace keeps looking
                // the way it did before colours existed.
                WorkspaceTints.Of(item.Value),
                Initials(item.Value.Name),
                WorkspaceIconSymbol(item.Value.Icon),
                item.Value.Entries.Count,
                IsolationBadgeFor(item.Value),
                _workspaceIsolationProvider is not null))
            .ToArray();
        var connections = snapshot.Connections
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => ToConnectionItem(
                ResolveForDisplay(snapshot, item.Value),
                item.Revision))
            .ToArray();
        var fileConnections = snapshot.FileProviderProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => ToFileConnectionItem(item.Value, item.Revision))
            .ToArray();
        var databaseConnections = snapshot.DatabaseConnections
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => ToDatabaseConnectionItem(item.Value, item.Revision))
            .ToArray();
        var layoutsById = snapshot.Layouts.ToDictionary(item => item.Value.Id, item => item.Value);
        var screens = snapshot.Screens
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                layoutsById.TryGetValue(item.Value.LayoutId, out var layout);
                return new LauncherScreenViewModel(
                    item.Value.Id,
                    item.Revision,
                    item.Value.Name,
                    item.Value.Description ?? "Reusable screen",
                    layout?.Name ?? "Missing layout",
                    item.Value.Panels.Count,
                    CreateScreenPreview(item.Value, layout),
                    ScreenSummary(item.Value, snapshot));
            })
            .ToArray();
        Launcher.ApplyCatalog(
            workspaces,
            connections,
            fileConnections,
            databaseConnections,
            screens);
        ReconcileAgentSavedScreenTemplate();
        // A rail item that was replaced arrives with its runtime flags cleared,
        // and the flags are derived rather than stored, so nothing else would
        // put them back. Saving the workspace you are working in bumps its
        // revision — which is exactly what autosave does every time a tab is
        // added — and the rail forgot which workspace you were in.
        RefreshWorkspaceRuntimeFlags(snapshot);
        // The active runtime does not change when its backing definition is
        // edited. Re-resolve its shell accent from this newly published
        // snapshot so an accent save retints the open workspace immediately.
        SetActiveWorkspaceAccent(ShellAccentOf(RuntimeWorkspace, snapshot));
        FileProviderSettings.ApplyCatalog(snapshot);
        AiProviderSettings.ApplyCatalog(snapshot);
        RefreshMcpServerDefinitions(snapshot);
        DefinitionSettings.ApplyCatalog(snapshot);
        TerminalSettings.ApplyCatalog(
            snapshot,
            preserveTerminalDraft: HasTerminalAppearanceDraft);
        AppearanceSettings.ApplyCatalog(snapshot);
        BrowserProfileSettingsEditor.ApplyCatalog(snapshot);
        NetworkSettings.ApplyCatalog(snapshot);
        WorkspaceSettings.ApplyCatalog(snapshot);
        RefreshOpenWorkspaceNetworkPolicies(snapshot);
        RefreshBrowserPanelProfileOptions(snapshot);
        OnPropertyChanged(nameof(PanelConnectionOptions));
        OnPropertyChanged(nameof(BrowserConnectionOptions));
        OnPropertyChanged(nameof(DatabasePanelConnectionOptions));
        OnPropertyChanged(nameof(FileConnectionOptions));
        RefreshOpenTerminalRenderProfiles();
        OnPropertyChanged(nameof(ActiveTerminalProfile));
        // The mark on the menu stands in for the rail, so turning the rail on
        // or off decides whether it is needed.
        OnPropertyChanged(nameof(HasWorkspaceAttention));
        OnPropertyChanged(nameof(KeybindingConflictCount));
        RefreshRecentSessionAvailability();
        Launcher.RefreshSearchResults();
    }

    private void ReconcileAgentSavedScreenTemplate()
    {
        OnPropertyChanged(nameof(HasAgentSavedScreenTemplates));
        if (_selectedAgentSavedScreenTemplate is not { } selected
            || _agentSavedScreenLiveTarget is not null)
        {
            return;
        }

        var current = Screens.SingleOrDefault(screen => screen.Id == selected.Id);
        if (current is not null && current.Revision == selected.Revision)
        {
            if (!ReferenceEquals(current, selected))
            {
                _selectedAgentSavedScreenTemplate = current;
                NotifyAgentSavedScreenTargetChanged();
            }

            return;
        }

        _selectedAgentSavedScreenTemplate = null;
        AgentSavedScreenTargetStatus =
            "The selected template changed or was removed. Select its current revision before creating a live target.";
        NotifyAgentSavedScreenTargetChanged();
    }

    private void RefreshRecentSessionAvailability()
    {
        History.RefreshAvailability(ToRecentSessionItem);
    }

    private static IReadOnlyList<LauncherScreenPanelPreviewViewModel> CreateScreenPreview(
        ScreenDefinition screen,
        LayoutDefinition? layout)
    {
        if (layout is null)
        {
            return [];
        }

        var slots = layout.Slots.ToDictionary(slot => slot.Id);
        return [.. screen.Panels
            .Select((panel, index) => (panel, index))
            .Where(item => slots.ContainsKey(item.panel.SlotId))
            .Select(item =>
            {
                var bounds = slots[item.panel.SlotId].Bounds;
                return new LauncherScreenPanelPreviewViewModel(
                    layout.Grid.Columns,
                    layout.Grid.Rows,
                    bounds.Column,
                    bounds.Row,
                    bounds.ColumnSpan,
                    bounds.RowSpan,
                    item.index == 0);
            })];
    }

    private IReadOnlyList<SavedConnectionShortcutViewModel> BuildSavedConnectionShortcuts()
    {
        var connectionItems = Connections.ToDictionary(item => item.Id);
        var shortcuts = _catalog.Snapshot.Connections
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                connectionItems.TryGetValue(item.Value.Id, out var launchItem);
                return CreateSavedConnectionShortcut(
                    new PanelConnectionOptionViewModel.Target.Connection(item.Value.Id),
                    item.Value.Name,
                    KindBadges.Connection(item.Value),
                    launchItem?.Detail ?? string.Empty,
                    launchItem is { CanOpen: true },
                    item.Value.PanelLaunchCapabilities);
            })
            .ToList();

        var liveFileProfiles = _filePanelClient.Profiles
            .Select(profile => profile.Id)
            .ToHashSet(StringComparer.Ordinal);
        shortcuts.AddRange(_catalog.Snapshot.FileProviderProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => CreateSavedConnectionShortcut(
                new PanelConnectionOptionViewModel.Target.FileProvider(item.Value.Id),
                item.Value.Name,
                FileProviderKindLabel(item.Value.ProviderKind),
                FileProviderEndpoint(item.Value.Configuration),
                liveFileProfiles.Contains(item.Value.Id.Value),
                item.Value.Configuration.PanelLaunchCapabilities)));
        return [.. shortcuts
            .OrderBy(shortcut => shortcut.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(shortcut => shortcut.Kind, StringComparer.OrdinalIgnoreCase)];
    }

    private static SavedConnectionShortcutViewModel CreateSavedConnectionShortcut(
        PanelConnectionOptionViewModel.Target target,
        string name,
        string kind,
        string detail,
        bool canOpen,
        PanelLaunchCapabilities capabilities)
    {
        var launches = capabilities.SupportedPanels
            .Select(panel => new SavedConnectionLaunchViewModel(
                target,
                panel,
                PanelLaunchLabel(panel),
                PanelLaunchIcon(panel)))
            .ToArray();
        var defaultLaunch = launches.Single(launch =>
            launch.Panel == capabilities.DefaultPanel);
        return new SavedConnectionShortcutViewModel(
            target,
            name,
            kind,
            detail,
            canOpen,
            defaultLaunch,
            [.. launches.Where(launch => launch != defaultLaunch)]);
    }

    private static string PanelLaunchLabel(PanelKind panel) => panel switch
    {
        PanelKind.Terminal => "Open terminal",
        PanelKind.FileViewer => "Open files",
        PanelKind.Statistics => "Open statistics",
        PanelKind.ProcessMonitor => "Open processes",
        PanelKind.Docker => "Open Docker",
        PanelKind.Git => "Open Git",
        _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, null),
    };

    private static Symbol PanelLaunchIcon(PanelKind panel) => panel switch
    {
        PanelKind.Terminal => Symbol.WindowConsole,
        PanelKind.FileViewer => Symbol.Folder,
        PanelKind.Statistics => Symbol.PulseSquare,
        PanelKind.ProcessMonitor => Symbol.Gauge,
        PanelKind.Docker => Symbol.Box,
        PanelKind.Git => Symbol.BranchFork,
        _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, null),
    };

    private IReadOnlyList<PanelConnectionOptionViewModel> BuildFileConnectionOptions()
    {
        var liveProfiles = _filePanelClient.Profiles.ToDictionary(
            profile => profile.Id,
            StringComparer.Ordinal);
        var options = _catalog.Snapshot.FileProviderProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new PanelConnectionOptionViewModel(
                new PanelConnectionOptionViewModel.Target.FileProvider(item.Value.Id),
                item.Value.Name,
                FileProviderKindLabel(item.Value.ProviderKind),
                FileProviderEndpoint(item.Value.Configuration),
                liveProfiles.ContainsKey(item.Value.Id.Value)))
            .ToList();
        var durableIds = _catalog.Snapshot.FileProviderProfiles
            .Select(item => item.Value.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
        options.AddRange(liveProfiles.Values
            .Where(profile => !durableIds.Contains(profile.Id))
            .Select(profile => new PanelConnectionOptionViewModel(
                new PanelConnectionOptionViewModel.Target.FileProvider(
                    new FileProviderProfileId(profile.Id)),
                FilePanelConnectionDisplayName(profile),
                FileProviderFamilyLabel(profile.Family),
                FileProviderDetail(profile),
                true)));
        return [.. options
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Kind, StringComparer.OrdinalIgnoreCase)];
    }

    private void RefreshMcpServerDefinitions(DefinitionCatalogSnapshot snapshot)
    {
        PruneMcpServerTests(snapshot);
        var projectedProfiles = snapshot.McpServerProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => ProjectMcpServerProfile(
                item,
                Secrets,
                GetMcpServerTest(item),
                GetMcpServerDiagnostic(item),
                _mcpDiagnosticsSnapshot.CleanupUncertain,
                _mcpServerDiagnostics is not null))
            .ToArray();
        ReconcileMcpServerDefinitions(projectedProfiles);
        Replace(McpServerSecretTargets, snapshot.McpServerProfiles
            .OrderBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(item => SecretDefinitionReferences
                .EnumerateMcpServerCredentialBindings(item.Value)
                .Where(binding => Secrets.All(secret =>
                    secret.Reference != binding.Reference
                    || secret.SecretScope.Kind != SecretScopeKind.McpServer
                    || !string.Equals(
                        secret.SecretScope.OwnerId,
                        item.Value.Id.Value,
                        StringComparison.Ordinal)))
                .Select(binding => new McpServerSecretTargetViewModel(
                    item.Value.Id,
                    item.Value.Name,
                    binding.Kind,
                    binding.Name,
                    binding.Reference))));
        OnPropertyChanged(nameof(HasMcpServers));
        OnPropertyChanged(nameof(HasNoMcpServers));
        OnPropertyChanged(nameof(HasMcpServerSecretTargets));
    }

    private void ReconcileMcpServerDefinitions(
        IReadOnlyList<McpServerProfileItemViewModel> projectedProfiles)
    {
        for (var targetIndex = 0;
             targetIndex < projectedProfiles.Count;
             targetIndex++)
        {
            var projected = projectedProfiles[targetIndex];
            var existingIndex = FindMcpServerDefinitionIndex(
                projected.Id,
                targetIndex);
            if (existingIndex < 0)
            {
                McpServerDefinitions.Insert(targetIndex, projected);
                continue;
            }

            var existing = McpServerDefinitions[existingIndex];
            existing.UpdateFrom(projected);
            if (existingIndex != targetIndex)
            {
                McpServerDefinitions.Move(existingIndex, targetIndex);
            }
        }

        while (McpServerDefinitions.Count > projectedProfiles.Count)
        {
            McpServerDefinitions.RemoveAt(McpServerDefinitions.Count - 1);
        }
    }

    private int FindMcpServerDefinitionIndex(
        McpServerProfileId profileId,
        int startIndex)
    {
        for (var index = startIndex;
             index < McpServerDefinitions.Count;
             index++)
        {
            if (McpServerDefinitions[index].Id == profileId)
            {
                return index;
            }
        }

        return -1;
    }

    internal static McpServerProfileItemViewModel ProjectMcpServerProfile(
        StoredDefinition<McpServerProfile> stored,
        IReadOnlyCollection<SecretMetadataViewModel> secrets) =>
        ProjectMcpServerProfile(
            stored,
            secrets,
            test: null,
            diagnostic: null,
            cleanupUncertain: false,
            diagnosticsAvailable: false);

    private static McpServerProfileItemViewModel ProjectMcpServerProfile(
        StoredDefinition<McpServerProfile> stored,
        IReadOnlyCollection<SecretMetadataViewModel> secrets,
        McpServerTestPresentation? test,
        McpServerDiagnosticSummary? diagnostic,
        bool cleanupUncertain,
        bool diagnosticsAvailable)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(secrets);
        var profile = stored.Value;
        var credentialBindings = SecretDefinitionReferences
            .EnumerateMcpServerCredentialBindings(profile)
            .ToArray();
        var missingSecretCount = credentialBindings.Count(binding =>
            secrets.All(secret =>
                secret.Reference != binding.Reference
                || secret.SecretScope.Kind != SecretScopeKind.McpServer
                || !string.Equals(
                    secret.SecretScope.OwnerId,
                    profile.Id.Value,
                    StringComparison.Ordinal)));
        var hasNoEnabledTools = profile.EnabledTools.Count == 0;
        var baselineStatus = !profile.IsTrusted
            ? "Untrusted"
            : !profile.IsEnabled
            ? "Trusted · disabled for runs"
            : hasNoEnabledTools
                ? "No tools enabled"
                : missingSecretCount > 0
                    ? "Credential missing"
                    : "Enabled for new runs";
        var baselineDetail = !profile.IsTrusted
            ? "Review this server definition before testing or enabling it."
            : !profile.IsEnabled
            ? "Testing is available, but agent runs cannot select this server."
            : hasNoEnabledTools
                ? "Choose at least one tool before enabling this server."
                : missingSecretCount > 0
                    ? missingSecretCount == 1
                        ? "Add the missing credential."
                        : $"Add {missingSecretCount} missing credentials."
                    : "Ready.";
        var currentTest = missingSecretCount == 0
            && test?.Revision == stored.Revision
                ? test
                : null;
        var currentDiagnostic = diagnostic?.Revision == stored.Revision
            ? diagnostic
            : null;
        var isTesting = currentTest?.State
                == McpServerTestPresentationState.Testing
            || currentDiagnostic?.State == McpServerLifecycleState.Testing;
        var testFailed = currentTest?.State
            == McpServerTestPresentationState.Failed;
        var diagnosticPresentation = currentTest is null
            ? ProjectMcpServerDiagnostic(currentDiagnostic)
            : null;
        var cleanupPresentation = cleanupUncertain && profile.IsTrusted
            ? (
                Status: "Cleanup uncertain",
                Detail: "A previous MCP process may still be alive. Restart GhostSHELL before retrying.")
            : ((string Status, string Detail)?)null;
        return new McpServerProfileItemViewModel(
            profile.Id,
            stored.Revision,
            profile.Name,
            profile.Transport.Kind,
            profile.Transport switch
            {
                McpServerTransport.Stdio stdioTarget => stdioTarget.Executable,
                McpServerTransport.StreamableHttp http =>
                    http.Endpoint.AbsoluteUri,
                _ => string.Empty,
            },
            profile.Transport is McpServerTransport.Stdio stdioArguments
                ? stdioArguments.Arguments.Count
                : 0,
            credentialBindings.Length,
            profile.EnabledTools.Count,
            cleanupPresentation?.Status
                ?? currentTest?.Status
                ?? diagnosticPresentation?.Status
                ?? baselineStatus,
            cleanupPresentation?.Detail
                ?? currentTest?.Detail
                ?? diagnosticPresentation?.Detail
                ?? baselineDetail,
            profile.IsEnabled,
            !profile.IsTrusted
            || cleanupUncertain
            || (profile.IsEnabled
                && (hasNoEnabledTools || missingSecretCount > 0))
            || testFailed,
            isTesting,
            diagnosticsAvailable
                && profile.IsTrusted
                && missingSecretCount == 0
                && !cleanupUncertain
                && !isTesting,
            profile.IsTrusted);
    }

    private McpServerDiagnosticSummary? GetMcpServerDiagnostic(
        StoredDefinition<McpServerProfile> stored) =>
        _mcpDiagnosticsSnapshot.Summaries.SingleOrDefault(summary =>
            summary.ProfileId == stored.Value.Id
            && summary.Revision == stored.Revision);

    private static (string Status, string Detail)? ProjectMcpServerDiagnostic(
        McpServerDiagnosticSummary? summary)
    {
        if (summary is null)
        {
            return null;
        }

        var session = summary.SessionId[..Math.Min(8, summary.SessionId.Length)];
        var kind = summary.SessionKind == McpServerSessionKind.Test
            ? "Test"
            : "Live session";
        var detail = $"{kind} {session} · {summary.Events[^1].Message}";
        return summary.State switch
        {
            McpServerLifecycleState.Testing => ("Testing", detail),
            McpServerLifecycleState.Starting => ("Starting", detail),
            McpServerLifecycleState.Healthy => ("Healthy", detail),
            McpServerLifecycleState.Degraded => ("Degraded", detail),
            McpServerLifecycleState.Failed => ("Failed", detail),
            McpServerLifecycleState.CleanupUncertain =>
                ("Cleanup uncertain", detail),
            McpServerLifecycleState.Stopped => ("Stopped", detail),
            McpServerLifecycleState.Untrusted => ("Untrusted", detail),
            McpServerLifecycleState.Disabled => ("Disabled", detail),
            _ => throw new ArgumentOutOfRangeException(
                nameof(summary),
                summary.State,
                null),
        };
    }

    private McpServerTestPresentation? GetMcpServerTest(
        StoredDefinition<McpServerProfile> stored)
    {
        lock (_mcpServerTestGate)
        {
            return _mcpServerTests.TryGetValue(
                    stored.Value.Id,
                    out var test)
                && test.Revision == stored.Revision
                    ? test
                    : null;
        }
    }

    private void SetMcpServerTest(
        McpServerProfileId profileId,
        McpServerTestPresentation presentation)
    {
        lock (_mcpServerTestGate)
        {
            _mcpServerTests[profileId] = presentation;
        }
    }

    private bool TryBeginMcpServerTest(
        McpServerProfileId profileId,
        long revision)
    {
        lock (_mcpServerTestGate)
        {
            if (_mcpServerTests.TryGetValue(
                    profileId,
                    out var current)
                && current.Revision == revision
                && current.State
                    == McpServerTestPresentationState.Testing)
            {
                return false;
            }

            _mcpServerTests[profileId] =
                new McpServerTestPresentation(
                    revision,
                    McpServerTestPresentationState.Testing,
                    "Testing",
                    "Testing the connection and loading its tools…");
            return true;
        }
    }

    private void CompleteMcpServerTest(
        McpServerProfileId profileId,
        long revision,
        McpServerTestPresentation presentation)
    {
        lock (_mcpServerTestGate)
        {
            if (_mcpServerTests.TryGetValue(
                    profileId,
                    out var current)
                && current.Revision == revision
                && current.State
                    == McpServerTestPresentationState.Testing)
            {
                _mcpServerTests[profileId] = presentation;
            }
        }
    }

    private void PruneMcpServerTests(
        DefinitionCatalogSnapshot snapshot)
    {
        var revisions = snapshot.McpServerProfiles.ToDictionary(
            stored => stored.Value.Id,
            stored => stored.Revision);
        lock (_mcpServerTestGate)
        {
            foreach (var profileId in _mcpServerTests.Keys.ToArray())
            {
                if (!revisions.TryGetValue(
                        profileId,
                        out var revision)
                    || _mcpServerTests[profileId].Revision != revision)
                {
                    _mcpServerTests.Remove(profileId);
                }
            }
        }
    }

    private void InvalidateMcpServerTests(SecretRef reference)
    {
        var snapshot = _catalog.Snapshot;
        var affectedProfileIds = snapshot.McpServerProfiles
            .Where(stored => SecretDefinitionReferences.Uses(
                stored.Value,
                reference))
            .Select(stored => stored.Value.Id)
            .ToHashSet();
        if (affectedProfileIds.Count == 0)
        {
            return;
        }

        lock (_mcpServerTestGate)
        {
            foreach (var profileId in affectedProfileIds)
            {
                _mcpServerTests.Remove(profileId);
            }
        }

        RefreshMcpServerDefinitions(snapshot);
    }

    private static McpServerTestPresentation
        CreateMcpServerTestSuccess(
            StoredDefinition<McpServerProfile> stored,
            McpServerTestReport report)
    {
        var profile = stored.Value;
        if (report.ProfileId != profile.Id
            || report.Revision != stored.Revision
            || report.EnabledToolCount != profile.EnabledTools.Count)
        {
            return new McpServerTestPresentation(
                stored.Revision,
                McpServerTestPresentationState.Failed,
                "Test failed",
                "The test result did not match the saved server settings.");
        }

        var discovered = report.DiscoveredToolCount == 1
            ? "1 tool"
            : $"{report.DiscoveredToolCount} tools";
        var enabled = report.EnabledToolCount == 1
            ? "1 enabled tool matched."
            : $"{report.EnabledToolCount} enabled tools matched.";
        var eligibility = report.EnabledToolCount == 0
                ? " No tools are enabled for agent runs."
                : string.Empty;
        var completed = report.CompletedAtUtc.ToString(
            "yyyy-MM-dd HH:mm:ss 'UTC'",
            System.Globalization.CultureInfo.InvariantCulture);
        return new McpServerTestPresentation(
            stored.Revision,
            McpServerTestPresentationState.Succeeded,
            "Last test passed",
            $"Tested {completed}. Found {discovered}; {enabled}"
                + eligibility);
    }

    private IReadOnlyList<LauncherSearchResultViewModel> BuildLauncherSearchCandidates()
    {
        var activeBindings = ActiveApplicationKeymap.Bindings
            .ToLookup(binding => binding.CommandId);
        var candidates = new List<LauncherSearchResultViewModel>();
        var savedDefinitionAction = HasRuntimeWorkspace ? "Add tab" : "Open";

        var canStartFileViewer = RuntimeWorkspace?.ActiveTab is not null
            || Workspaces.Count > 0
            || Connections.Any(connection => connection.CanOpen);
        var canStartBrowser = CanStartBrowserSession;
        var canStartDatabase = _databaseConnectionCatalog is not null;
        candidates.AddRange(
        [
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.Terminal),
                Symbol.WindowConsole,
                "Create · terminal",
                "New terminal",
                "Start a local PTY in a new tab.",
                "Open",
                IsAvailable: true,
                UnavailableReason: null,
                ["create", "new", "terminal", "local", "pty", "panel", "tab"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.Browser),
                Symbol.Globe,
                "Create · browser",
                "New browser",
                "Open an embedded Chromium browser panel.",
                canStartBrowser ? "Open" : "Unavailable",
                canStartBrowser,
                canStartBrowser
                    ? null
                    : "The embedded browser is unavailable in this build.",
                ["create", "new", "browser", "web", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.FileViewer),
                Symbol.Folder,
                "Create · files",
                "New File Viewer",
                "Browse local or configured file providers.",
                canStartFileViewer ? "Open" : "Unavailable",
                canStartFileViewer,
                canStartFileViewer
                    ? null
                    : "Open or save a workspace or connection first.",
                ["create", "new", "file", "files", "viewer", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.Statistics),
                Symbol.PulseSquare,
                "Create · statistics",
                "New statistics panel",
                "Watch live system metrics on this host.",
                "Open",
                IsAvailable: true,
                UnavailableReason: null,
                ["create", "new", "statistics", "stats", "local", "host", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.ProcessMonitor),
                Symbol.Gauge,
                "Create · process monitor",
                "New process monitor",
                "Watch running processes on this host.",
                "Open",
                IsAvailable: true,
                UnavailableReason: null,
                ["create", "new", "process", "monitor", "local", "host", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.DatabaseViewer),
                Symbol.Database,
                "Create · database",
                "New database viewer",
                "Query SQLite, PostgreSQL, or MySQL.",
                canStartDatabase ? "Open" : "Unavailable",
                canStartDatabase,
                canStartDatabase
                    ? null
                    : "The database drivers are unavailable in this build.",
                ["create", "new", "database", "sql", "sqlite", "postgres", "mysql", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.Docker),
                Symbol.Box,
                "Create · Docker",
                "New Docker panel",
                "Manage containers, images, volumes, and networks locally or over SSH.",
                _dockerEngineClient is null ? "Unavailable" : "Open",
                _dockerEngineClient is not null,
                _dockerEngineClient is null
                    ? "Docker support is unavailable in this build."
                    : null,
                ["create", "new", "docker", "container", "image", "volume", "network", "panel"]),
            new LauncherSearchResultViewModel(
                new LauncherSearchTarget.CreatePanel(PanelKind.Git),
                Symbol.BranchFork,
                "Create · Git",
                "New Git panel",
                "Stage, commit, and browse history locally or over SSH.",
                _gitRepositoryClient is null ? "Unavailable" : "Open",
                _gitRepositoryClient is not null,
                _gitRepositoryClient is null
                    ? "Git support is unavailable in this build."
                    : null,
                ["create", "new", "git", "repository", "commit", "branch", "diff", "panel"]),
        ]);

        foreach (var command in BuiltInCommands.Registry.Commands)
        {
            var bindings = activeBindings[command.Id].ToArray();
            if (bindings.Length == 0)
            {
                bindings = [.. command.DefaultBindings];
            }

            var invocations = bindings
                .Select(binding => new
                {
                    Binding = binding,
                    Target = new LauncherSearchTarget.Command(
                        command.Id,
                        binding.Arguments),
                })
                .DistinctBy(invocation => invocation.Target.InvocationKey, StringComparer.Ordinal)
                .ToArray();
            if (invocations.Length == 0)
            {
                candidates.Add(CreateCommandLauncherResult(
                    command,
                    ImmutableDictionary<string, string>.Empty,
                    "Unbound"));
                continue;
            }

            candidates.AddRange(invocations.Select(invocation =>
                CreateCommandLauncherResult(
                    command,
                    invocation.Target.Arguments,
                    invocation.Binding.Sequence.ToString())));
        }

        candidates.AddRange(Connections.Select(connection => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.Connection(connection.Id),
            Symbol.Server,
            $"CONNECTION · {connection.Kind}",
            connection.Name,
            connection.Detail,
            connection.CanOpen ? savedDefinitionAction : "Unavailable",
            connection.CanOpen,
            connection.CanOpen ? null : connection.Status,
            [
                "connection",
                connection.Id.Value,
                connection.Kind,
                connection.Detail,
                connection.Status,
            ])));
        candidates.AddRange(FileConnections.Select(connection => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.FileConnection(
                new FileProviderProfileId(connection.TargetId)),
            Symbol.Folder,
            $"CONNECTION · {connection.Kind}",
            connection.Name,
            connection.Detail,
            connection.CanOpen ? savedDefinitionAction : "Unavailable",
            connection.CanOpen,
            connection.CanOpen ? null : connection.Status,
            [
                "connection",
                "files",
                connection.TargetId,
                connection.Kind,
                connection.Detail,
            ])));
        candidates.AddRange(DatabaseConnections.Select(connection => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.DatabaseConnection(
                new DatabaseConnectionProfileId(connection.TargetId)),
            Symbol.Database,
            $"CONNECTION · {connection.Kind}",
            connection.Name,
            connection.Detail,
            connection.CanOpen ? savedDefinitionAction : "Unavailable",
            connection.CanOpen,
            connection.CanOpen ? null : connection.Status,
            [
                "connection",
                "database",
                connection.TargetId,
                connection.Kind,
                connection.Detail,
            ])));
        candidates.AddRange(Screens.Select(screen => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.Screen(screen.Id),
            Symbol.Grid,
            $"SAVED SCREEN · {screen.Layout}",
            screen.Name,
            $"{screen.Description} · {CountLabel(screen.PanelCount, "panel")}",
            savedDefinitionAction,
            IsAvailable: true,
            UnavailableReason: null,
            ["screen", "saved screen", screen.Id.Value, screen.Description, screen.Layout])));
        candidates.AddRange(Workspaces.Select(workspace => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.Workspace(workspace.Id),
            workspace.IconSymbol,
            "Workspace",
            workspace.Name,
            workspace.Description,
            CountLabel(workspace.ItemCount, "item"),
            IsAvailable: true,
            UnavailableReason: null,
            ["workspace", workspace.Id.Value, workspace.Description])));
        candidates.AddRange(HistorySessions.Select(recent => new LauncherSearchResultViewModel(
            new LauncherSearchTarget.RecentSession(recent.SessionId),
            Symbol.History,
            $"RECENT SESSION · {recent.SourceKind}",
            recent.Title,
            recent.Detail,
            recent.LastUsed,
            recent.CanOpen,
            recent.CanOpen
                ? null
                : "The current saved definition no longer exists or is unavailable on this platform.",
            [
                "recent",
                "recent session",
                recent.SessionId.Value,
                recent.SourceKind,
                recent.Detail,
            ])));

        return candidates;
    }

    private LauncherSearchResultViewModel CreateCommandLauncherResult(
        CommandDefinition command,
        IReadOnlyDictionary<string, string> arguments,
        string shortcut)
    {
        var argumentSummary = string.Join(
            " · ",
            arguments
                .OrderBy(argument => argument.Key, StringComparer.Ordinal)
                .Select(argument => argument.Value));
        var argumentDetail = string.Join(
            ", ",
            arguments
                .OrderBy(argument => argument.Key, StringComparer.Ordinal)
                .Select(argument => $"{argument.Key}={argument.Value}"));
        var isAvailable = IsCommandAvailable(command.Id, arguments);
        var searchTerms = new List<string>
        {
            "command",
            command.Id.Value,
            command.Category,
        };
        foreach (var argument in arguments)
        {
            searchTerms.Add(argument.Key);
            searchTerms.Add(argument.Value);
            searchTerms.Add($"{argument.Key}={argument.Value}");
        }

        return new LauncherSearchResultViewModel(
            new LauncherSearchTarget.Command(command.Id, arguments),
            Symbol.Code,
            $"COMMAND · {command.Category}",
            argumentSummary.Length == 0
                ? command.Title
                : $"{command.Title} · {argumentSummary}",
            argumentDetail.Length == 0
                ? command.Id.Value
                : $"{command.Id.Value} · {argumentDetail}",
            shortcut,
            isAvailable,
            isAvailable ? null : "Unavailable in the current route.",
            searchTerms);
    }

    private static string CountLabel(int count, string singular) =>
        $"{count} {(count == 1 ? singular : $"{singular}s")}";

    private bool IsCommandAvailable(
        CommandId id,
        IReadOnlyDictionary<string, string> arguments)
    {
        if (id == BuiltInCommands.NewTab)
        {
            return true;
        }

        if (!BuiltInCommands.Registry.TryGet(id, out var definition)
            || definition is null)
        {
            return false;
        }

        if (!definition.IsAvailable(new CommandInvocation(
                ActiveCommandContexts,
                arguments)))
        {
            return false;
        }

        if (id == BuiltInCommands.MoveTabLeft
            || id == BuiltInCommands.MoveTabRight)
        {
            var workspace = RuntimeWorkspace;
            var activeTab = workspace?.ActiveTab;
            if (workspace is null || activeTab is null || workspace.Tabs.Count < 2)
            {
                return false;
            }

            var activeIndex = workspace.Tabs.IndexOf(activeTab);
            return id == BuiltInCommands.MoveTabLeft
                ? activeIndex > 0
                : activeIndex < workspace.Tabs.Count - 1;
        }

        if (id == BuiltInCommands.MoveTabToWorkspace
            || id == BuiltInCommands.MovePanelToWorkspace)
        {
            if (!arguments.TryGetValue("position", out var value)
                || !int.TryParse(
                    value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var position)
                || position < 0
                || position >= _openWorkspaces.Count
                || ReferenceEquals(RuntimeWorkspace, _openWorkspaces[position]))
            {
                return false;
            }

            return id == BuiltInCommands.MoveTabToWorkspace
                ? RuntimeWorkspace?.Tabs.Count > 1
                : RuntimeWorkspace?.ActiveTab?.Panels.Count > 1;
        }

        if (id == BuiltInCommands.SelectWorkspace)
        {
            return arguments.TryGetValue("position", out var value)
                && int.TryParse(
                    value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var position)
                && position >= 0
                && position < Workspaces.Count;
        }

        var hasTab = RuntimeWorkspace?.ActiveTab is not null;
        var hasPanel = ActivePanel is not null;
        var hasTerminal = ActivePanel is TerminalRuntimePanelViewModel;
        return (id == BuiltInCommands.SplitPanel && hasPanel)
            || (id == BuiltInCommands.FocusPanel && hasPanel)
            || (id == BuiltInCommands.TogglePanelZoom && hasPanel)
            || (id == BuiltInCommands.ClosePanel && hasPanel)
            || (id == BuiltInCommands.RenameTab && hasTab)
            || id == BuiltInCommands.CloseTab
            || id == BuiltInCommands.NextTab
            || id == BuiltInCommands.PreviousTab
            || id == BuiltInCommands.LastTab
            || id == BuiltInCommands.SelectTab
            || (id == BuiltInCommands.EnterTerminalCopyMode && hasTerminal)
            || (id == BuiltInCommands.SendPrefix && hasTerminal);
    }

    private RuntimeTabViewModel CreateConnectionTab(
        WorkspaceInstanceId workspaceId,
        ConnectionProfile connection,
        string? title = null,
        RuntimeAgentPolicyProvenance? agentPolicy = null)
    {
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            string.IsNullOrWhiteSpace(title) ? connection.Name : title.Trim(),
            "Connection",
            historySource: new RuntimeHistorySource(connection.Key, connection.Name),
            agentPolicy: agentPolicy);
        try
        {
            AddPanelOrDispose(
                tab,
                CreatePanel(
                    workspaceId,
                    tab.Id,
                    new ScreenPanelDefinition(
                        ScreenPanelId.New(),
                        LayoutSlotId.New(),
                        ScreenPanelKind.Terminal,
                        connection.Name,
                        connection.Id,
                        PanelStartupBehavior.None)));
            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private RuntimeTabViewModel CreateConnectionPanelTab(
        WorkspaceInstanceId workspaceId,
        ConnectionProfile connection,
        PanelKind panel,
        RuntimeAgentPolicyProvenance agentPolicy)
    {
        if (panel == PanelKind.Terminal)
        {
            return CreateConnectionTab(
                workspaceId,
                connection,
                agentPolicy: agentPolicy);
        }

        var title = PanelTitle(panel);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            title,
            connection.Name,
            historySource: new RuntimeHistorySource(connection.Key, connection.Name),
            agentPolicy: agentPolicy);
        try
        {
            var runtimePanel = panel switch
            {
                PanelKind.FileViewer => CreateFilePanel(
                    workspaceId,
                    tab.Id,
                    PanelInstanceId.New(),
                    title,
                    connection.Endpoint is ConnectionEndpoint.Ssh
                        ? ConnectionFileProviderProfiles.Id(connection.Id)
                        : BuiltInFileProviders.HomeId,
                    deferInitialization: true,
                    connection: connection),
                PanelKind.Statistics or PanelKind.ProcessMonitor =>
                    CreateMonitorPanel(
                        workspaceId,
                        tab.Id,
                        PanelInstanceId.New(),
                        title,
                        panel,
                        connection),
                PanelKind.Docker => CreateDockerPanel(
                    PanelInstanceId.New(),
                    title,
                    connection,
                    workspaceId),
                PanelKind.Git => CreateGitPanel(
                    PanelInstanceId.New(),
                    title,
                    connection,
                    workspaceId: workspaceId),
                _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, null),
            };
            AddPanelOrDispose(tab, runtimePanel);
            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private RuntimeTabViewModel CreateFileProviderTab(
        WorkspaceInstanceId workspaceId,
        FileProviderProfile profile)
    {
        var title = PanelTitle(PanelKind.FileViewer);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            profile.Name,
            FileProviderKindLabel(profile.ProviderKind),
            historySource: new RuntimeHistorySource(profile.Key, profile.Name));
        try
        {
            AddPanelOrDispose(
                tab,
                CreateFilePanel(
                    workspaceId,
                    tab.Id,
                    PanelInstanceId.New(),
                    title,
                    profile.Id,
                    deferInitialization: true));
            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    /// <summary>
    /// The shell accent a restored workspace should come up wearing.
    ///
    /// The snapshot does not carry it — it records the colour the workspace is
    /// recognised by, which is a different field — so it is read back from the
    /// definition the restored workspace came from. A workspace with no
    /// definition behind it, or one whose definition is gone, leaves the shell
    /// wearing its own accent, which is what it would have done anyway.
    /// </summary>
    private string? ShellAccentOf(RuntimeWorkspaceViewModel? runtime) =>
        ShellAccentOf(runtime, _catalog.Snapshot);

    private string? ShellAccentOf(
        RuntimeWorkspaceViewModel? runtime,
        DefinitionCatalogSnapshot snapshot)
    {
        if (runtime is null
            || !_runtimeSources.TryGetValue(runtime.Id, out var source)
            || source.SourceDefinition.Kind != WorkspaceDefinition.Kind)
        {
            return null;
        }

        return snapshot.Workspaces
            .FirstOrDefault(item => string.Equals(item.Value.Id.Value, source.SourceDefinition.Value, StringComparison.Ordinal))
            ?.Value.Accent;
    }

    private RuntimeWorkspaceViewModel RestoreWorkspace(
        RuntimeWorkspaceRecoveryPayload recovered,
        WorkspaceInstanceId runtimeId,
        WorkspaceIsolationBinding? isolationBinding)
    {
        var connectionIds = recovered.ConnectionIds
            .Concat(recovered.Tabs
                .SelectMany(tab => tab.Panels)
                .Select(panel => panel.ConnectionId)
                .OfType<string>())
            .Select(id => new ConnectionId(id))
            .ToHashSet();
        var runtime = new RuntimeWorkspaceViewModel(
            runtimeId,
            recovered.Name,
            recovered.Accent,
            [.. Connections.Where(item => connectionIds.Contains(item.Id))],
            recovered.AgentPolicy?.ToProvenance()
                ?? RuntimeAgentPolicyProvenance.Unconfigured,
            isolationBinding is null
                ? ResolveRecoveredTerminalMultiplexingOverride(recovered)
                : TerminalMultiplexingMode.Disabled,
            isolationBinding);
        if (recovered.HistorySource?.ToHistorySource() is { } recoveredSource)
        {
            _runtimeSources[runtime.Id] = recoveredSource;
        }
        RegisterWorkspaceConnectionRuntime(runtime);
        if (runtime.TerminalMultiplexingMode is { } recoveredMultiplexingOverride)
        {
            _workspaceTerminalMultiplexingModes[runtime.Id] = recoveredMultiplexingOverride;
        }
        try
        {
            var restoredTabs = new Dictionary<string, RuntimeTabViewModel>(StringComparer.Ordinal);
            foreach (var recoveredTab in recovered.Tabs)
            {
                var tab = RestoreTab(runtime.Id, recoveredTab);
                runtime.Tabs.Add(tab);
                restoredTabs.Add(recoveredTab.Key, tab);
            }

            runtime.ActiveTab = recovered.ActiveTabKey is { } activeTabKey
                ? restoredTabs[activeTabKey]
                : runtime.Tabs[0];
            return runtime;
        }
        catch
        {
            _runtimeSources.Remove(runtime.Id);
            _workspaceTerminalMultiplexingModes.Remove(runtime.Id);
            runtime.DisposePanels();
            throw;
        }
    }

    private TerminalMultiplexingMode? ResolveRecoveredTerminalMultiplexingOverride(
        RuntimeWorkspaceRecoveryPayload recovered)
    {
        if (recovered.HistorySource is not
            { SourceKind: var kind, SourceValue: var value }
            || !string.Equals(kind, WorkspaceDefinition.Kind.Value, StringComparison.Ordinal))
        {
            return recovered.TerminalMultiplexingMode;
        }

        return _catalog.Snapshot.Workspaces
            .Select(item => item.Value)
            .FirstOrDefault(workspace => string.Equals(workspace.Id.Value, value, StringComparison.Ordinal))
            ?.TerminalMultiplexingOverride;
    }

    private RuntimeTabViewModel RestoreTab(
        WorkspaceInstanceId workspaceId,
        RuntimeTabRecoveryPayload recovered)
    {
        var slots = recovered.Panels.Select(panel => new LayoutSlotDefinition(
            LayoutSlotId.New(),
            new LayoutGridBounds(
                panel.Column,
                panel.Row,
                panel.ColumnSpan,
                panel.RowSpan),
            new LayoutMinimumSize(panel.MinimumWidth, panel.MinimumHeight))).ToArray();
        var layout = new LayoutDefinition(
            LayoutId.New(),
            LayoutDefinition.CurrentSchemaVersion,
            "Recovered runtime layout",
            new LayoutGrid(recovered.Columns, recovered.Rows),
            slots,
            recovered.DockLayoutJson);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            recovered.Title,
            recovered.Source,
            // A recovery snapshot contains the authoritative Dock tree even
            // when the tab began in automatic-layout mode. Reconstructing a
            // fresh automatic layout here discards user docking, splitter and
            // floating-window changes captured later in the session.
            layout,
            recovered.HistorySource?.ToHistorySource(),
            recovered.AgentPolicy?.ToProvenance()
                ?? RuntimeAgentPolicyProvenance.Unconfigured,
            usesAutomaticLayout: recovered.UsesAutomaticLayout,
            icon: recovered.Icon,
            // A recovered launcher title/icon keeps the ownership recorded by
            // the current recovery schema.
            hasChosenTitle: recovered.HasChosenTitle
                ?? !string.Equals(recovered.Title, "New tab", StringComparison.Ordinal),
            hasChosenIcon: recovered.HasChosenIcon ?? recovered.Icon is not null);
        try
        {
            var restoredPanels = new Dictionary<string, RuntimePanelViewModel>(StringComparer.Ordinal);
            for (var index = 0; index < recovered.Panels.Length; index++)
            {
                var recoveredPanel = recovered.Panels[index];
                var panel = RestorePanel(workspaceId, tab.Id, recoveredPanel);
                try
                {
                    tab.AddPanel(
                        panel,
                        recovered.UsesAutomaticLayout ? null : slots[index].Id,
                        recoveredPanel.Key);
                }
                catch
                {
                    panel.Dispose();
                    throw;
                }
                restoredPanels.Add(recoveredPanel.Key, panel);
            }

            if (recovered.ActivePanelKey is { } activePanelKey)
            {
                _ = tab.ActivatePanel(restoredPanels[activePanelKey].Id);
            }
            if (recovered.ZoomedPanelKey is not null)
            {
                _ = tab.ToggleActivePanelZoom();
            }

            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private RuntimePanelViewModel RestorePanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        RuntimePanelRecoveryPayload recovered)
    {
        if (recovered.Kind == RuntimePanelRecoveryKind.Terminal)
        {
            var connection = recovered.ConnectionId is { } statisticsConnectionId
                ? FindConnection(new ConnectionId(statisticsConnectionId))
                : LocalConnection();
            return connection is null
                ? new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Terminal,
                    recovered.Title,
                    "Terminal",
                    "The recovered terminal connection is no longer available. Repair or recreate the connection, then reopen the panel.")
                : CreateTerminalPanel(
                    workspaceId,
                    tabId,
                    connection,
                    recovered.Title,
                    new PanelStartupBehavior(recovered.StartupLocation),
                    multiplexerSession: recovered.Multiplexer?.ToSession());
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.FileViewer)
        {
            var location = recovered.FileLocation?.ToLocation();
            var profileId = recovered.FileProviderProfileId ?? location?.ProviderProfileId;
            var panel = CreateFilePanel(
                workspaceId,
                tabId,
                PanelInstanceId.New(),
                recovered.Title,
                profileId is null ? null : new FileProviderProfileId(profileId),
                location,
                deferInitialization: true,
                connection: recovered.ConnectionId is { } fileConnectionId
                    ? FindConnection(new ConnectionId(fileConnectionId))
                    : null);
            if (panel is FileRuntimePanelViewModel filePanel)
            {
                filePanel.Filter = recovered.Filter ?? string.Empty;
                filePanel.ShowHidden = recovered.ShowHidden;
            }

            return panel;
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.Browser)
        {
            var connection = recovered.ConnectionId is { } browserConnectionId
                ? FindConnection(new ConnectionId(browserConnectionId))
                : LocalConnection();
            if (connection is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Browser,
                    recovered.Title,
                    "Browser",
                    "The recovered browser connection is no longer available.");
            }

            if (!BrowserAddress.TryParse(recovered.StartupLocation, out var address))
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Browser,
                    recovered.Title,
                    "Browser",
                    "The recovered browser address is invalid.");
            }

            var profileId = new BrowserProfileId(recovered.BrowserProfileId!);
            var partition = new BrowserProfileKey(
                recovered.BrowserProfileKind!.Value,
                recovered.BrowserProfileIdentity!);
            var pinned = _browserProfileRuntime.Pin(profileId, partition);
            return pinned.IsSuccess
                ? CreateBrowserPanel(
                    workspaceId,
                    tabId,
                    PanelInstanceId.New(),
                    recovered.Title,
                    address,
                    connection,
                    pinned.Binding)
                : new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Browser,
                    recovered.Title,
                    "Browser",
                    $"Recovered browser profile unavailable. {pinned.Message}");
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.DatabaseViewer)
        {
            return CreateDatabasePanelFromTarget(
                PanelInstanceId.New(),
                recovered.Title,
                recovered.StartupLocation,
                recovered.ConnectionId is { } tunnelId
                    ? FindConnection(new ConnectionId(tunnelId))
                    : null,
                deferStoredCredentialAccess: true,
                workspaceId: workspaceId);
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.Docker)
        {
            var connection = recovered.ConnectionId is { } dockerConnectionId
                ? FindConnection(new ConnectionId(dockerConnectionId))
                : LocalConnection();
            return connection is null
                ? new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Docker,
                    recovered.Title,
                    "Docker",
                    "The recovered Docker connection is no longer available.")
                : CreateDockerPanel(
                    PanelInstanceId.New(),
                    recovered.Title,
                    connection,
                    workspaceId);
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.Git)
        {
            var connection = recovered.ConnectionId is { } gitConnectionId
                ? FindConnection(new ConnectionId(gitConnectionId))
                : LocalConnection();
            return connection is null
                ? new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Git,
                    recovered.Title,
                    "Git",
                    "The recovered Git connection is no longer available.")
                : CreateGitPanel(
                    PanelInstanceId.New(),
                    recovered.Title,
                    connection,
                    recovered.StartupLocation,
                    workspaceId);
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.Statistics)
        {
            var connection = recovered.ConnectionId is { } processConnectionId
                ? FindConnection(new ConnectionId(processConnectionId))
                : LocalConnection();
            if (connection is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Statistics,
                    recovered.Title,
                    "Statistics",
                    "The recovered monitoring connection is no longer available.");
            }

            return CreateMonitorPanel(
                workspaceId,
                tabId,
                PanelInstanceId.New(),
                recovered.Title,
                PanelKind.Statistics,
                connection);
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.ProcessMonitor)
        {
            var connection = FindConnection(new ConnectionId(recovered.ConnectionId!));
            if (connection is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.ProcessMonitor,
                    recovered.Title,
                    "Process monitor",
                    "The recovered monitoring connection is no longer available.");
            }

            return CreateMonitorPanel(
                workspaceId,
                tabId,
                PanelInstanceId.New(),
                recovered.Title,
                PanelKind.ProcessMonitor,
                connection);
        }

        if (recovered.Kind == RuntimePanelRecoveryKind.Placeholder
            || recovered is
            {
                Kind: RuntimePanelRecoveryKind.Unavailable,
                KindLabel: "Choose",
            })
        {
            return new PanelPlaceholderViewModel(PanelInstanceId.New());
        }

        return new UnavailableRuntimePanelViewModel(
            PanelInstanceId.New(),
            PanelKindFromRecovery(recovered.KindLabel),
            recovered.Title,
            recovered.KindLabel!,
            "This recovered panel type is not available in the current build.");
    }

    private RuntimeTabViewModel CreateRuntimeTab(
        WorkspaceInstanceId workspaceId,
        string title,
        string source,
        LayoutId layoutId,
        IReadOnlyList<ScreenPanelDefinition> panels,
        DefinitionKey? sourceDefinition = null,
        string? durableSourceTitle = null,
        RuntimeAgentPolicyProvenance? agentPolicy = null)
    {
        var layout = _catalog.Snapshot.Layouts
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == layoutId);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            title,
            source,
            layout,
            sourceDefinition is { } key
                && !string.IsNullOrWhiteSpace(durableSourceTitle)
                    ? new RuntimeHistorySource(key, durableSourceTitle)
                    : null,
            agentPolicy);
        try
        {
            foreach (var panel in panels)
            {
                AddPanelOrDispose(
                    tab,
                    CreatePanel(
                        workspaceId,
                        tab.Id,
                        panel,
                        deferFileInitialization: true),
                    panel.SlotId);
            }

            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private static void AddPanelOrDispose(
        RuntimeTabViewModel tab,
        RuntimePanelViewModel panel,
        LayoutSlotId? slotId = null)
    {
        try
        {
            tab.AddPanel(panel, slotId);
        }
        catch
        {
            panel.Dispose();
            throw;
        }
    }

    private RuntimePanelViewModel CreatePanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        ScreenPanelDefinition panel,
        bool deferFileInitialization = false)
    {
        var title = string.IsNullOrWhiteSpace(panel.Title) ? PanelTitle(panel.Kind) : panel.Title;
        if (panel.Kind == ScreenPanelKind.FileViewer)
        {
            FilePanelLocation? initialLocation = null;
            if (!string.IsNullOrWhiteSpace(panel.Startup.Location))
            {
                var profile = ResolveFileProfile(panel.FileProviderProfileId);
                if (profile is not null)
                {
                    try
                    {
                        initialLocation = FileLocationPresentation.Parse(
                            profile,
                            panel.Startup.Location);
                    }
                    catch (ArgumentException)
                    {
                        return new UnavailableRuntimePanelViewModel(
                            PanelInstanceId.New(),
                            PanelKind.FileViewer,
                            title,
                            "File viewer",
                            "This panel has an invalid startup location. Repair the saved screen with a location supported by its file provider.");
                    }
                }
            }

            return CreateFilePanel(
                    workspaceId,
                    tabId,
                    PanelInstanceId.New(),
                    title,
                    panel.FileProviderProfileId,
                    initialLocation,
                    initialLocation is null ? panel.Startup.Location : null,
                    deferInitialization: deferFileInitialization);
        }

        if (panel.Kind == ScreenPanelKind.Browser)
        {
            if (panel.Startup.Location is { } location
                && !BrowserAddress.TryParse(location, out _))
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Browser,
                    title,
                    "Browser",
                    "This panel has an invalid startup URL. Repair the saved screen with a complete HTTP or HTTPS address.");
            }

            var browserRoute = panel.ConnectionId is { } browserConnectionId
                ? FindConnection(browserConnectionId)
                : LocalConnection();
            if (browserRoute is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Browser,
                    title,
                    "Browser",
                    "The browser connection is no longer in the catalog. Repair the saved screen with a local or SSH connection.");
            }

            return CreateBrowserPanel(
                workspaceId,
                tabId,
                PanelInstanceId.New(),
                title,
                panel.Startup.Location is null
                    ? BrowserAddress.Blank
                    : BrowserAddress.TryParse(panel.Startup.Location, out var address)
                        ? address
                        : BrowserAddress.Blank,
                browserRoute);
        }

        if (panel.Kind is ScreenPanelKind.Statistics or ScreenPanelKind.ProcessMonitor)
        {
            // A monitor panel runs its sampler over whichever connection it
            // names, local or not — the same treatment the recovery path has
            // always given it. This branch refused every saved screen that
            // named one, from back when there was nothing behind a remote
            // sampler to run.
            var monitored = panel.ConnectionId is { } monitorConnectionId
                ? FindConnection(monitorConnectionId)
                : null;
            if (panel.ConnectionId is not null && monitored is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKindFromDefinition(panel.Kind),
                    title,
                    panel.Kind == ScreenPanelKind.Statistics
                        ? "Statistics"
                        : "Process monitor",
                    "The connection this panel monitors is no longer in the catalog. "
                    + "Repair the saved screen with a connection that still exists, or remove "
                    + "it from the panel to monitor the local host.");
            }

            return CreateMonitorPanel(
                workspaceId,
                tabId,
                PanelInstanceId.New(),
                title,
                PanelKindFromDefinition(panel.Kind),
                monitored);
        }

        if (panel.Kind == ScreenPanelKind.DatabaseViewer)
        {
            return CreateDatabasePanelFromTarget(
                PanelInstanceId.New(),
                title,
                panel.Startup.Location,
                panel.ConnectionId is { } tunnelId ? FindConnection(tunnelId) : null,
                workspaceId: workspaceId);
        }

        if (panel.Kind == ScreenPanelKind.Docker)
        {
            var dockerConnection = panel.ConnectionId is { } dockerConnectionId
                ? FindConnection(dockerConnectionId)
                : LocalConnection();
            if (dockerConnection is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Docker,
                    title,
                    "Docker",
                    "The Docker panel connection is no longer available.");
            }

            return CreateDockerPanel(
                PanelInstanceId.New(),
                title,
                dockerConnection,
                workspaceId);
        }

        if (panel.Kind == ScreenPanelKind.Git)
        {
            var gitConnection = panel.ConnectionId is { } gitConnectionId
                ? FindConnection(gitConnectionId)
                : LocalConnection();
            if (gitConnection is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    PanelInstanceId.New(),
                    PanelKind.Git,
                    title,
                    "Git",
                    "The Git panel connection is no longer available.");
            }

            return CreateGitPanel(
                PanelInstanceId.New(),
                title,
                gitConnection,
                panel.Startup.Location,
                workspaceId);
        }

        if (panel.Kind != ScreenPanelKind.Terminal)
        {
            return new UnavailableRuntimePanelViewModel(
                PanelInstanceId.New(),
                PanelKindFromDefinition(panel.Kind),
                title,
                KindBadges.Panel(panel.Kind),
                $"{panel.Kind} panels are defined, but their native adapter arrives in a later milestone.");
        }

        var connection = panel.ConnectionId is { } connectionId
            ? FindConnection(connectionId)
            : _catalog.Snapshot.Connections
                .Select(item => item.Value)
                .FirstOrDefault(item => item.Endpoint is ConnectionEndpoint.Local);
        if (connection is null)
        {
            return new UnavailableRuntimePanelViewModel(
                PanelInstanceId.New(),
                PanelKind.Terminal,
                title,
                "Terminal",
                "This panel references a connection that is not available. Repair the saved screen in Settings.");
        }

        return CreateTerminalPanel(workspaceId, tabId, connection, title, panel.Startup);
    }

    private TerminalRuntimePanelViewModel CreateTerminalPanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        ConnectionProfile connection,
        string title,
        PanelStartupBehavior startup,
        PanelInstanceId? panelId = null,
        PanelInstanceId? ownerPanelId = null,
        PanelSessionRole sessionRole = PanelSessionRole.Primary,
        TerminalMultiplexerSession? multiplexerSession = null)
    {
        var resolvedPanelId = panelId ?? PanelInstanceId.New();
        var terminalProfile = ActiveTerminalProfile;
        var terminalKeymap = terminalProfile is null
            ? null
            : ResolveTerminalKeymap(_catalog.Snapshot, terminalProfile.KeymapId);
        var mode = _workspaceTerminalMultiplexingModes.TryGetValue(workspaceId, out var workspaceMode)
            ? workspaceMode
            : TerminalContinuity.Mode;
        var usesContinuity = mode == TerminalMultiplexingMode.Automatic
            && connection.ConnectionKind == ConnectionKind.Ssh;
        if (!usesContinuity)
        {
            // Recovery metadata describes the previous launch, while the
            // current workspace policy decides the next one. Keeping the old
            // identity here silently re-enabled continuity in disabled workspaces.
            multiplexerSession = null;
        }
        else
        {
            multiplexerSession ??= TerminalMultiplexerSession.CreateAutomatic();
        }

        return new TerminalRuntimePanelViewModel(
            resolvedPanelId,
            title,
            ConnectionRuntimeFor(workspaceId),
            connection,
            new SessionOwner(
                HostMode.Desktop,
                WindowId,
                workspaceId,
                tabId,
                ownerPanelId ?? resolvedPanelId),
            startup,
            terminalProfile is not null
                ? TerminalRenderProfileSnapshot.FromProfile(terminalProfile)
                : null,
            SessionClient,
            ClientId,
            _startupCommandDispatcher,
            WorkspaceRuntimeServicesFor(workspaceId)?.Backends.ConnectionSecurityRuntime
                ?? _connectionSecurityRuntime,
            keymap: terminalKeymap is null
                ? null
                : TerminalKeymapSnapshot.FromProfile(terminalKeymap),
            sessionRole: sessionRole,
            multiplexerCoordinator: _terminalMultiplexerCoordinator,
            multiplexerSession: multiplexerSession,
            connectionDisplayName: WorkspaceConnectionDisplayName(workspaceId, connection));
    }

    private static KeymapProfile? ResolveTerminalKeymap(
        DefinitionCatalogSnapshot snapshot,
        KeymapProfileId keymapId)
    {
        var stored = snapshot.Keymaps
            .Select(item => item.Value)
            .FirstOrDefault(item => item.Id == keymapId);
        if (stored is not null)
        {
            return stored.Layer == KeymapLayer.Terminal ? stored : null;
        }

        var builtIn = BuiltInKeymaps.All.FirstOrDefault(item => item.Id == keymapId);
        return builtIn?.Layer == KeymapLayer.Terminal ? builtIn : null;
    }

    private RuntimePanelViewModel CreateFilePanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        string title,
        FileProviderProfileId? initialProfileId = null,
        FilePanelLocation? initialLocation = null,
        string? initialLocationText = null,
        bool deferInitialization = false,
        ConnectionProfile? connection = null)
    {
        if (WorkspaceUsesIsolation(workspaceId))
        {
            var isolated = WorkspaceRuntimeServicesFor(workspaceId)?.Backends;
            if (isolated is null)
            {
                return IsolationBackendUnavailable(
                    panelId,
                    PanelKind.FileViewer,
                    title,
                    "File Viewer");
            }

            var workspaceProfile = isolated.FilePanelClient.Profiles.First();
            var selectedProfile = initialProfileId is { } requestedProfileId
                ? isolated.FilePanelClient.Profiles.FirstOrDefault(profile =>
                    string.Equals(
                        profile.Id,
                        requestedProfileId.Value,
                        StringComparison.Ordinal))
                : null;
            selectedProfile ??= workspaceProfile;
            var selectedProfileId = new FileProviderProfileId(selectedProfile.Id);
            var selectedLocation = initialLocation is not null
                && string.Equals(
                    initialLocation.ProviderProfileId,
                    selectedProfile.Id,
                    StringComparison.Ordinal)
                    ? initialLocation
                    : selectedProfile.StartLocation;
            connection ??= ConnectionForFileProfile(selectedProfileId)
                ?? BuiltInConnections.Local;
            return new FileRuntimePanelViewModel(
                panelId,
                title,
                isolated.FilePanelClient,
                isolated.FileTransferQueueClient,
                selectedProfileId,
                selectedLocation,
                initialLocationText,
                deferInitialization,
                connection,
                isolated.DatabasePanelClient,
                _imagePreviewDecoder,
                _pdfPreviewRenderer,
                _archiveTableOfContents,
                previewers: null,
                databaseRegistry: null,
                _filePreviewPreferences,
                FileTransferClipboard,
                new FileProviderProfileId(workspaceProfile.Id));
        }

        connection ??= ConnectionForFileProfile(initialProfileId) ?? LocalConnection();
        var hostBackends = WorkspaceRuntimeServicesFor(workspaceId)?.Backends
            ?? _hostWorkspaceRuntimeServices.Backends;
        var profile = ResolveFileProfile(initialProfileId);
        var hostInitialLocation = initialLocation ?? profile?.Root;
        var owner = new SessionOwner(
            HostMode.Desktop,
            WindowId,
            workspaceId,
            tabId,
            panelId);
        var options = hostInitialLocation is null
            ? HostedFilePanelClientOptions.Deferred(
                SessionId.New(),
                owner,
                ClientId,
                title,
                initialProfileId)
            : new HostedFilePanelClientOptions(
                SessionId.New(),
                owner,
                ClientId,
                title,
                hostInitialLocation);
        var hostedClient = new SessionHostedFilePanelClient(
            SessionClient,
            hostBackends.FilePanelClient,
            options,
            hostBackends.FileTransferQueueClient
                ?? throw new InvalidOperationException(
                    "A direct workspace requires file transfer services."));
        return new FileRuntimePanelViewModel(
            panelId,
            title,
            hostedClient,
            hostedClient,
            initialProfileId,
            initialLocation,
            initialLocationText,
            deferInitialization,
            connection,
            hostBackends.DatabasePanelClient,
            _imagePreviewDecoder,
            _pdfPreviewRenderer,
            _archiveTableOfContents,
            previewers: null,
            _inMemoryDatabaseRegistry,
            _filePreviewPreferences,
            FileTransferClipboard);
    }

    private FileProviderProfileDescriptor? ResolveFileProfile(
        FileProviderProfileId? profileId) =>
        profileId is { } requestedProfile
            ? _filePanelClient.Profiles.FirstOrDefault(
                item => string.Equals(item.Id, requestedProfile.Value, StringComparison.Ordinal))
            : _filePanelClient.Profiles.FirstOrDefault(
                    item => string.Equals(item.Id, BuiltInFileProviders.HomeId.Value, StringComparison.Ordinal))
                ?? _filePanelClient.Profiles.FirstOrDefault();

    private RuntimePanelViewModel CreateBrowserPanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        string title,
        BrowserAddress initialAddress,
        ConnectionProfile? connection = null,
        BrowserProfileBinding? profile = null,
        BrowserProfileId? requestedProfileId = null)
    {
        var runtimeBackends = WorkspaceRuntimeServicesFor(workspaceId)?.Backends;
        var browserRenderer = runtimeBackends?.BrowserRendererViewFactory;
        if (WorkspaceUsesIsolation(workspaceId) && browserRenderer is null)
        {
            return IsolationBackendUnavailable(
                panelId,
                PanelKind.Browser,
                title,
                "Browser");
        }

        if (browserRenderer is null)
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Browser,
                title,
                "Browser",
                "The embedded browser is unavailable in this build.");
        }

        connection ??= LocalConnection() ?? BuiltInConnections.Local;
        if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Browser,
                title,
                "Browser",
                "This browser route is not a local or SSH connection.");
        }

        var pinned = profile is null
            ? ResolveBrowserProfile(workspaceId, panelId, requestedProfileId)
            : BrowserProfilePinResult.Success(profile);
        if (!pinned.IsSuccess)
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Browser,
                title,
                "Browser",
                pinned.Message!);
        }

        var browser = new BrowserRuntimePanelViewModel(
            panelId,
            title,
            new SessionOwner(
                HostMode.Desktop,
                WindowId,
                workspaceId,
                tabId,
                panelId),
            initialAddress,
            SessionClient,
            ClientId,
            connection,
            pinned.Binding!,
            browserRenderer,
            WorkspaceConnectionDisplayName(workspaceId, connection));
        browser.NewTabRequested += OnBrowserNewTabRequested;
        return browser;
    }

    private BrowserProfilePinResult ResolveBrowserProfile(
        WorkspaceInstanceId workspaceId,
        PanelInstanceId panelId,
        BrowserProfileId? requestedProfileId = null)
    {
        WorkspaceDefinition? definition = null;
        if (_runtimeSources.TryGetValue(workspaceId, out var source)
            && source.SourceDefinition.Kind == WorkspaceDefinition.Kind)
        {
            definition = _catalog.Snapshot.Workspaces
                .Select(item => item.Value)
                .FirstOrDefault(item => string.Equals(item.Id.Value, source.SourceDefinition.Value, StringComparison.Ordinal));
        }

        var isolated = definition?.BrowserProfileOverride switch
        {
            WorkspaceBrowserProfileMode.Shared => false,
            WorkspaceBrowserProfileMode.Isolated => true,
            null => _browserProfilePreferences.Current.Sharing
                == BrowserProfileSharing.PerWorkspace,
            _ => throw new ArgumentOutOfRangeException(),
        };
        var legacyPartition = isolated
            ? BrowserProfileKey.ForWorkspace(
                definition?.Id.Value ?? workspaceId.Value)
            : BrowserProfileKey.Global;
        var profileId = requestedProfileId
            ?? _browserProfilePreferences.Current.DefaultProfileId
            ?? BuiltInBrowserProfiles.Default.Id;
        return _browserProfileRuntime.PinNewPanel(
            profileId,
            legacyPartition,
            panelId);
    }

    private async void OnBrowserNewTabRequested(
        object? sender,
        BrowserNewTabRequestedEventArgs args)
    {
        if (_shutdownStarted
            || !args.UserGesture
            || sender is not BrowserRuntimePanelViewModel source)
        {
            return;
        }

        try
        {
            await OpenBrowserPopupInNewTabAsync(
                source,
                args.Address,
                _runtimeGraphLifetime.Token);
        }
        catch (OperationCanceledException) when (
            _shutdownStarted || _runtimeGraphLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError($"The browser could not open a new tab: {exception.Message}");
        }
    }

    private Task<bool> OpenBrowserPopupInNewTabAsync(
        BrowserRuntimePanelViewModel source,
        BrowserAddress address,
        CancellationToken cancellationToken)
    {
        var workspace = _openWorkspaces
            .Append(RuntimeWorkspace)
            .OfType<RuntimeWorkspaceViewModel>()
            .Distinct()
            .FirstOrDefault(item => item.Tabs.Any(tab =>
                tab.Panels.Contains(source)));
        if (workspace is null)
        {
            return Task.FromResult(false);
        }

        var connection = FindConnection(source.ConnectionId)
            ?? (source.ConnectionId == BuiltInConnections.Local.Id
                ? BuiltInConnections.Local
                : null);
        if (connection is null)
        {
            SetError("The browser route used by this page is no longer available.");
            return Task.FromResult(false);
        }

        return AppendRuntimeTabAsync(
            workspace,
            runtime => CreateBrowserTab(
                runtime.Id,
                address,
                connection,
                source.ProfileBinding),
            "browser new-tab creation",
            cancellationToken);
    }

    private RuntimeTabViewModel CreateBrowserTab(
        WorkspaceInstanceId workspaceId,
        BrowserAddress address,
        ConnectionProfile connection,
        BrowserProfileBinding profile)
    {
        var title = "Browser";
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            title,
            connection.Endpoint is ConnectionEndpoint.Local
                ? "Local"
                : connection.Name);
        try
        {
            AddPanelOrDispose(
                tab,
                CreateBrowserPanel(
                    workspaceId,
                    tab.Id,
                    PanelInstanceId.New(),
                    title,
                    address,
                    connection,
                    profile));
            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private RuntimePanelViewModel CreateMonitorPanel(
        WorkspaceInstanceId workspaceId,
        TabInstanceId tabId,
        PanelInstanceId panelId,
        string title,
        PanelKind kind,
        ConnectionProfile? connection = null)
    {
        connection ??= LocalConnection() ?? BuiltInConnections.Local;
        if (WorkspaceUsesIsolation(workspaceId)
            && WorkspaceRuntimeServicesFor(workspaceId) is null)
        {
            return IsolationBackendUnavailable(
                panelId,
                kind,
                title,
                kind == PanelKind.Statistics ? "Statistics" : "Process monitor");
        }

        var owner = new SessionOwner(
            HostMode.Desktop,
            WindowId,
            workspaceId,
            tabId,
            panelId);
        return kind switch
        {
            PanelKind.Statistics => new StatisticsRuntimePanelViewModel(
                panelId,
                title,
                SessionClient,
                ClientId,
                owner,
                connection,
                _uiThreadDispatcher,
                connectionDisplayName: WorkspaceConnectionDisplayName(workspaceId, connection)),
            PanelKind.ProcessMonitor => new ProcessMonitorRuntimePanelViewModel(
                panelId,
                title,
                SessionClient,
                ClientId,
                owner,
                connection,
                _uiThreadDispatcher,
                connectionDisplayName: WorkspaceConnectionDisplayName(workspaceId, connection)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private bool WorkspaceUsesIsolation(WorkspaceInstanceId workspaceId) =>
        ConnectionRuntimeFor(workspaceId) is IWorkspaceIsolationTerminalRuntime;

    private string PanelConnectionDisplayName(LauncherConnectionViewModel connection)
    {
        var profile = FindConnection(connection.Id);
        return RuntimeWorkspace is { } workspace
            && profile?.Endpoint is ConnectionEndpoint.Local
            && WorkspaceUsesIsolation(workspace.Id)
                ? "Workspace environment"
                : connection.Name;
    }

    private string? WorkspaceConnectionDisplayName(
        WorkspaceInstanceId workspaceId,
        ConnectionProfile connection) =>
        connection.Endpoint is ConnectionEndpoint.Local && WorkspaceUsesIsolation(workspaceId)
            ? "Workspace"
            : null;

    private string FilePanelConnectionDisplayName(FileProviderProfileDescriptor profile) =>
        RuntimeWorkspace is { } workspace
        && WorkspaceUsesIsolation(workspace.Id)
        && profile.Family == FileProviderFamily.Posix
            ? "Workspace environment"
            : profile.Name;

    private WorkspaceRuntimeServices? WorkspaceRuntimeServicesFor(
        WorkspaceInstanceId workspaceId)
        => _workspaceRuntimeLeases.RuntimeServicesFor(workspaceId);

    private static RuntimePanelViewModel IsolationBackendUnavailable(
        PanelInstanceId panelId,
        PanelKind kind,
        string title,
        string kindLabel) =>
        new UnavailableRuntimePanelViewModel(
            panelId,
            kind,
            title,
            kindLabel,
            "Workspace isolation services are unavailable in this app build. "
                + "No process was started on the host.");

    /// <summary>
    /// The Database shell hosts two deliberately different runtimes: pooled,
    /// per-call ADO.NET work and a long-lived Redis session.
    /// </summary>
    private RuntimePanelViewModel CreateDatabasePanel(
        PanelInstanceId panelId,
        string title,
        string? driverId = null,
        string? connectionString = null,
        ConnectionProfile? tunnelConnection = null,
        DatabaseConnectionProfile? savedConnection = null,
        DatabaseObjectId? initialObject = null,
        bool deferStoredCredentialAccess = false,
        WorkspaceInstanceId? workspaceId = null)
    {
        var runtimeServices = workspaceId is { } runtimeWorkspaceId
            ? WorkspaceRuntimeServicesFor(runtimeWorkspaceId)
            : _hostWorkspaceRuntimeServices;
        var runtimeBackends = runtimeServices?.Backends;
        if (workspaceId is { } requiredIsolatedWorkspaceId
            && WorkspaceUsesIsolation(requiredIsolatedWorkspaceId)
            && runtimeBackends is null)
        {
            return IsolationBackendUnavailable(
                panelId,
                PanelKind.DatabaseViewer,
                title,
                "Database");
        }

        var databaseClient = runtimeBackends?.DatabasePanelClient;
        var redisFactory = runtimeBackends?.RedisPanelSessionFactory;
        if (runtimeServices?.NetworkRoute.ProxyUri is not null)
        {
            // A non-null route tells both relational and Redis clients to open
            // their endpoint through the isolate-owned TCP forwarder.
            tunnelConnection ??= BuiltInConnections.Local;
        }

        var effectiveDriver = savedConnection?.DriverId ?? driverId;
        if (string.Equals(effectiveDriver, RedisDatabase.DriverId, StringComparison.Ordinal))
        {
            return redisFactory is null || _databaseConnectionCatalog is null
                ? new UnavailableRuntimePanelViewModel(
                    panelId,
                    PanelKind.DatabaseViewer,
                    title,
                    "Database",
                    "Redis support is unavailable in this build.")
                : new RedisRuntimePanelViewModel(
                    panelId,
                    title,
                    redisFactory,
                    _databaseConnectionCatalog,
                    connectionString,
                    tunnelConnection,
                    savedConnection,
                    ResolveDatabasePasswordAsync,
                    _secretVault.Availability.CanPersist
                        ? StoreDatabasePasswordAsync
                        : null,
                    DatabasePasswordStoreLabel(_secretVault.Availability.Adapter),
                    deferStoredCredentialAccess: deferStoredCredentialAccess);
        }

        return databaseClient is null
            ? new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.DatabaseViewer,
                title,
                "Database",
                "The database drivers are unavailable in this build.")
            : new DatabaseRuntimePanelViewModel(
                panelId,
                title,
                databaseClient,
                driverId,
                connectionString,
                tunnelConnection,
                savedConnection,
                ResolveDatabasePasswordAsync,
                initialObject: initialObject,
                sqlLanguageService: _sqlLanguageService,
                passwordPersister: _secretVault.Availability.CanPersist
                    ? StoreDatabasePasswordAsync
                    : null,
                passwordStoreLabel: DatabasePasswordStoreLabel(
                    _secretVault.Availability.Adapter),
                deferStoredCredentialAccess: deferStoredCredentialAccess);
    }

    private static string DatabasePasswordStoreLabel(string adapter) => adapter switch
    {
        "macos-keychain" => "Save in macOS Keychain",
        "windows-dpapi" => "Save securely for this Windows user",
        "linux-secret-service" => "Save in Secret Service",
        _ => "Save in system credential store",
    };

    private RuntimePanelViewModel CreateDockerPanel(
        PanelInstanceId panelId,
        string title,
        ConnectionProfile? connection = null,
        WorkspaceInstanceId? workspaceId = null)
    {
        var dockerClient = workspaceId is { } runtimeWorkspaceId
            ? WorkspaceRuntimeServicesFor(runtimeWorkspaceId)?.Backends.DockerEngineClient
            : _hostWorkspaceRuntimeServices.Backends.DockerEngineClient;
        if (dockerClient is null)
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Docker,
                title,
                "Docker",
                "Docker support is unavailable in this build.");
        }

        connection ??= LocalConnection() ?? BuiltInConnections.Local;
        if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Docker,
                title,
                "Docker",
                "Docker panels can use only a local or SSH connection.");
        }

        return new DockerRuntimePanelViewModel(
            panelId,
            title,
            dockerClient,
            connection,
            workspaceId is { } dockerWorkspaceId
                ? WorkspaceConnectionDisplayName(dockerWorkspaceId, connection)
                : null);
    }

    private RuntimePanelViewModel CreateGitPanel(
        PanelInstanceId panelId,
        string title,
        ConnectionProfile? connection = null,
        string? repositoryPath = null,
        WorkspaceInstanceId? workspaceId = null)
    {
        var gitClient = workspaceId is { } runtimeWorkspaceId
            ? WorkspaceRuntimeServicesFor(runtimeWorkspaceId)?.Backends.GitRepositoryClient
            : _hostWorkspaceRuntimeServices.Backends.GitRepositoryClient;
        if (gitClient is null)
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Git,
                title,
                "Git",
                "Git support is unavailable in this build.");
        }

        connection ??= LocalConnection() ?? BuiltInConnections.Local;
        if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
        {
            return new UnavailableRuntimePanelViewModel(
                panelId,
                PanelKind.Git,
                title,
                "Git",
                "Git panels can use only a local or SSH connection.");
        }

        // A Git connection's startup directory is its repository path, so
        // opening the saved connection lands directly in that repository.
        repositoryPath ??= connection.PreferredPanel == PanelKind.Git
            ? connection.Startup.Directory
            : null;
        return new GitRuntimePanelViewModel(
            panelId,
            title,
            gitClient,
            connection,
            repositoryPath,
            _gitPanelPreferences,
            _gitMutationCoordinator,
            workspaceId is { } gitWorkspaceId
                ? WorkspaceConnectionDisplayName(gitWorkspaceId, connection)
                : null);
    }

    /// <summary>
    /// Opens an interactive shell in a new tab. The terminal uses
    /// the same local or SSH connection as the panel, then sends one quoted
    /// docker-exec command through the normal audited startup-command path.
    /// </summary>
    public async Task<bool> OpenDockerContainerShellAsync(
        DockerRuntimePanelViewModel source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ClearError();
        if (source.SelectedResource?.Container is not { IsRunning: true } container)
        {
            SetError("Select a running container before opening a shell.");
            return false;
        }

        var workspace = RuntimeWorkspace;
        if (workspace is null
            || workspace.Tabs.All(candidate => !candidate.Panels.Contains(source)))
        {
            SetError("That Docker panel is no longer open.");
            return false;
        }

        var shellPath = await ResolveDockerContainerShellAsync(
            source,
            container.Id,
            cancellationToken);
        if (shellPath is null
            || !ReferenceEquals(RuntimeWorkspace, workspace)
            || workspace.Tabs.All(candidate => !candidate.Panels.Contains(source)))
        {
            return false;
        }

        return await AppendRuntimeTabAsync(
            workspace,
            runtime =>
            {
                var tab = new RuntimeTabViewModel(
                    TabInstanceId.New(),
                    $"{container.Name} shell",
                    source.ConnectionDisplayName,
                    historySource: new RuntimeHistorySource(
                        source.Connection.Key,
                        source.Connection.Name));
                AddPanelOrDispose(
                    tab,
                    CreateTerminalPanel(
                        runtime.Id,
                        tab.Id,
                        source.Connection,
                        $"{container.Name} shell",
                        new PanelStartupBehavior(
                            commands:
                            [
                                DockerContainerShellCommand.Build(
                                    container.Id,
                                    shellPath),
                            ])));
                return tab;
            },
            "container shell tab creation",
            cancellationToken);
    }

    /// <summary>
    /// Hosts a container shell inside its Docker inspector. The terminal view model has its own
    /// presentation identity, while the hosted session is owned by the Docker panel so panel/tab/
    /// window close scopes still discover and close it.
    /// </summary>
    public async Task<bool> OpenDockerContainerInlineShellAsync(
        DockerRuntimePanelViewModel source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        ClearError();
        if (source.HasInlineShell)
        {
            return true;
        }

        if (source.SelectedResource?.Container is not { IsRunning: true } container)
        {
            SetError("Select a running container before opening a shell.");
            return false;
        }

        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.FirstOrDefault(candidate => candidate.Panels.Contains(source));
        if (workspace is null || tab is null)
        {
            SetError("That Docker panel is no longer open.");
            return false;
        }

        var shellPath = await ResolveDockerContainerShellAsync(
            source,
            container.Id,
            cancellationToken);
        if (shellPath is null
            || !ReferenceEquals(RuntimeWorkspace, workspace)
            || !tab.Panels.Contains(source)
            || !string.Equals(source.SelectedResource?.Container?.Id, container.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (source.HasInlineShell)
        {
            return true;
        }

        var shell = CreateTerminalPanel(
            workspace.Id,
            tab.Id,
            source.Connection,
            $"{container.Name} shell",
            new PanelStartupBehavior(
                commands:
                [
                    DockerContainerShellCommand.Build(container.Id, shellPath),
                ]),
            ownerPanelId: source.Id,
            sessionRole: PanelSessionRole.Embedded);
        source.AttachInlineShell(container.Id, shell);
        return true;
    }

    private async Task<string?> ResolveDockerContainerShellAsync(
        DockerRuntimePanelViewModel source,
        string containerId,
        CancellationToken cancellationToken)
    {
        source.BeginShellResolution(containerId);
        if (_dockerEngineClient is null)
        {
            source.PresentShellResolutionFailure(
                containerId,
                new DockerError(
                    DockerErrorCode.RuntimeUnavailable,
                    "Docker support is unavailable in this build.",
                    false));
            return null;
        }

        var result = await _dockerEngineClient.ResolveContainerShellAsync(
            source.Connection,
            containerId,
            cancellationToken);
        if (result is DockerResult<string>.Success success)
        {
            source.CompleteShellResolution(containerId);
            return success.Value;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            source.PresentShellResolutionFailure(
                containerId,
                ((DockerResult<string>.Failure)result).Error);
        }

        return null;
    }

    /// <summary>
    /// A twin of a live database panel: same connection, tunnel and all,
    /// opening straight onto one object — or, with no object, onto the whole
    /// database. The twin binds the same saved profile when there is one, so
    /// passwords resolve the same way.
    /// </summary>
    private RuntimePanelViewModel CreateDatabaseTwinPanel(
        DatabaseRuntimePanelViewModel source,
        DatabaseTableDescriptor? databaseObject)
    {
        var workspaceId = RuntimeWorkspace?.Tabs.Any(tab => tab.Panels.Contains(source)) == true
            ? RuntimeWorkspace.Id
            : (WorkspaceInstanceId?)null;
        var title = databaseObject?.DisplayName ?? source.Title;
        if (source.SavedConnectionId is { } profileId
            && FindDatabaseConnection(profileId) is { } profile)
        {
            return CreateDatabasePanel(
                PanelInstanceId.New(),
                title,
                tunnelConnection: ResolveDatabaseTunnel(profile),
                savedConnection: profile,
                initialObject: databaseObject?.Id,
                workspaceId: workspaceId);
        }

        return CreateDatabasePanel(
            PanelInstanceId.New(),
            title,
            source.SelectedDriver.Id,
            source.ConnectionString,
            source.TunnelConnection,
            initialObject: databaseObject?.Id,
            workspaceId: workspaceId);
    }

    /// <summary>
    /// Opens the same database as a full viewer tab — the embedded preview's
    /// way out into the real thing, without the preview's read-only leash.
    /// </summary>
    public async Task<bool> OpenDatabaseInTabAsync(
        DatabaseRuntimePanelViewModel source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ClearError();
        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            SetError("Open a workspace before opening a database tab.");
            return false;
        }

        return await AppendRuntimeTabAsync(
            workspace,
            _ =>
            {
                var tab = new RuntimeTabViewModel(
                    TabInstanceId.New(),
                    source.Title,
                    "Database");
                AddPanelOrDispose(tab, CreateDatabaseTwinPanel(source, databaseObject: null));
                return tab;
            },
            "database viewer tab creation",
            cancellationToken);
    }

    /// <summary>Opens an object from a live panel as its own tab, same connection.</summary>
    public async Task<bool> OpenDatabaseObjectInTabAsync(
        DatabaseRuntimePanelViewModel source,
        DatabaseTableDescriptor databaseObject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(databaseObject);
        ClearError();
        var workspace = RuntimeWorkspace;
        if (workspace is null)
        {
            SetError("Open a workspace before opening an object in a tab.");
            return false;
        }

        return await AppendRuntimeTabAsync(
            workspace,
            _ =>
            {
                var tab = new RuntimeTabViewModel(
                    TabInstanceId.New(),
                    databaseObject.DisplayName,
                    "Database");
                AddPanelOrDispose(tab, CreateDatabaseTwinPanel(source, databaseObject));
                return tab;
            },
            "database object tab creation",
            cancellationToken);
    }

    /// <summary>Opens an object beside its panel — the split with a purpose.</summary>
    public async Task<bool> OpenDatabaseObjectInPanelAsync(
        DatabaseRuntimePanelViewModel source,
        DatabaseTableDescriptor databaseObject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(databaseObject);
        ClearError();
        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.FirstOrDefault(candidate =>
                candidate.Panels.Contains(source))
            ?? workspace?.ActiveTab;
        if (workspace is null || tab is null)
        {
            SetError("Open a workspace before opening an object in a panel.");
            return false;
        }

        var panel = CreateDatabaseTwinPanel(source, databaseObject);
        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            "panel split",
            () =>
            {
                _ = tab.SplitActivePanel(panel, PanelSplitOrientation.LeftRight);
                StartTrackingRecovery(panel);
            },
            cancellationToken);
    }

    private const string SavedDatabaseTargetPrefix = "saved:";

    /// <summary>
    /// Builds a database panel from a durable target: "saved:{profile id}"
    /// binds a saved connection (its own tunnel wins over the recovered one),
    /// anything else is a raw "driver:connection string" address.
    /// </summary>
    private RuntimePanelViewModel CreateDatabasePanelFromTarget(
        PanelInstanceId panelId,
        string title,
        string? target,
        ConnectionProfile? recoveredTunnel,
        bool deferStoredCredentialAccess = false,
        WorkspaceInstanceId? workspaceId = null)
    {
        if (target?.StartsWith(SavedDatabaseTargetPrefix, StringComparison.Ordinal) == true)
        {
            var profileId = target[SavedDatabaseTargetPrefix.Length..];
            var stored = _catalog.Snapshot.DatabaseConnections
                .SingleOrDefault(item => string.Equals(item.Value.Id.Value, profileId, StringComparison.Ordinal));
            if (stored is null)
            {
                return new UnavailableRuntimePanelViewModel(
                    panelId,
                    PanelKind.DatabaseViewer,
                    title,
                    "Database",
                    "The saved database connection no longer exists.");
            }

            var profile = stored.Value;
            var tunnel = ResolveDatabaseTunnel(profile) ?? recoveredTunnel;
            return CreateDatabasePanel(
                panelId,
                title,
                tunnelConnection: tunnel,
                savedConnection: profile,
                deferStoredCredentialAccess: deferStoredCredentialAccess,
                workspaceId: workspaceId);
        }

        var parsed = DatabasePanelTarget.TryParse(target);
        return CreateDatabasePanel(
            panelId,
            title,
            parsed?.DriverId,
            parsed?.ConnectionString,
            recoveredTunnel,
            deferStoredCredentialAccess: deferStoredCredentialAccess,
            workspaceId: workspaceId);
    }

    /// <summary>
    /// The tunnel a database profile connects through: a saved SSH connection
    /// when referenced, else the profile's own inline tunnel.
    /// </summary>
    public ConnectionProfile? ResolveDatabaseTunnel(DatabaseConnectionProfile profile) =>
        profile.TunnelConnectionId is { } tunnelId
            ? FindConnection(tunnelId)
            : profile.InlineTunnel;

    /// <summary>
    /// Rebinds a live database panel to a saved connection, tunnel and all.
    /// The panel is stateless between operations, so nothing is torn down —
    /// it simply connects to the new target.
    /// </summary>
    public bool ReplaceDatabasePanelConnection(
        RuntimePanelViewModel panel,
        DatabaseConnectionProfileId profileId)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ClearError();
        var profile = FindDatabaseConnection(profileId);
        if (profile is null)
        {
            SetError("That database connection no longer exists.");
            return false;
        }

        return ApplyDatabasePanelConnection(
            panel,
            profile,
            tunnel: ResolveDatabaseTunnel(profile));
    }

    /// <summary>
    /// Binds a database profile, replacing the runtime when the selected
    /// connection crosses the relational/Redis boundary.
    /// </summary>
    public bool ApplyDatabasePanelConnection(
        RuntimePanelViewModel panel,
        DatabaseConnectionProfile profile,
        string? sessionPassword = null,
        ConnectionProfile? tunnel = null,
        bool persisted = true)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(profile);
        var wantsRedis = string.Equals(
            profile.DriverId,
            RedisDatabase.DriverId,
            StringComparison.Ordinal);
        if (panel is RedisRuntimePanelViewModel redis && wantsRedis)
        {
            redis.ApplySavedConnection(profile, sessionPassword, tunnel, persisted);
            QueueRuntimeRecoverySnapshot();
            return true;
        }

        if (panel is DatabaseRuntimePanelViewModel relational && !wantsRedis)
        {
            relational.ApplySavedConnection(profile, sessionPassword, tunnel, persisted);
            QueueRuntimeRecoverySnapshot();
            return true;
        }

        var workspace = RuntimeWorkspace;
        var tab = workspace?.Tabs.SingleOrDefault(candidate =>
            candidate.Panels.Any(candidatePanel => candidatePanel.Id == panel.Id));
        if (workspace is null || tab is null)
        {
            SetError("That database panel is no longer open.");
            return false;
        }

        var replacement = CreateDatabasePanel(
            panel.Id,
            panel.Title,
            driverId: profile.DriverId,
            workspaceId: workspace.Id);
        if (replacement is RedisRuntimePanelViewModel replacementRedis)
        {
            replacementRedis.ApplySavedConnection(
                profile,
                sessionPassword,
                tunnel,
                persisted);
        }
        else if (replacement is DatabaseRuntimePanelViewModel replacementRelational)
        {
            replacementRelational.ApplySavedConnection(
                profile,
                sessionPassword,
                tunnel,
                persisted);
        }

        if (!tab.ReplacePanel(panel, replacement))
        {
            replacement.Dispose();
            SetError("The database panel changed before its connection could be switched.");
            return false;
        }

        Notifications.Watch(workspace);
        StartTrackingRecovery(replacement);
        StartAcceptedRuntimePanel(replacement);
        QueueRuntimeRecoverySnapshot();
        return true;
    }

    /// <summary>
    /// The editor's "connect without saving": the request becomes an
    /// in-memory profile the panel binds to. The typed password becomes the
    /// session password; nothing reaches the catalog or the vault — which is
    /// also why an inline tunnel needing a stored password must be saved.
    /// </summary>
    public bool BindUnsavedDatabaseConnection(
        RuntimePanelViewModel panel,
        DatabaseConnectionSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(request);
        ClearError();
        if (_databaseConnectionCatalog is null)
        {
            SetError("The database drivers are unavailable in this build.");
            return false;
        }

        ConnectionProfile? tunnel = null;
        if (request.TunnelConnectionId is { } tunnelId)
        {
            tunnel = FindConnection(tunnelId);
            if (tunnel is null)
            {
                SetError("That SSH tunnel connection no longer exists.");
                return false;
            }
        }
        else if (request.InlineTunnel is { } inline)
        {
            if (!inline.UseAgent)
            {
                SetError(
                    "Tunnel passwords live in the OS keychain. "
                    + "Save the connection to use a password tunnel.");
                return false;
            }

            tunnel = DatabaseConnectionEditorViewModel.BuildInlineTunnelProfile(
                DatabaseConnectionProfile.InlineTunnelId(DatabaseConnectionProfileId.New()),
                $"{request.Name} tunnel",
                inline,
                new ConnectionAuthentication.SshAgent());
        }

        var profile = new DatabaseConnectionProfile(
            DatabaseConnectionProfileId.New(),
            DatabaseConnectionProfile.CurrentSchemaVersion,
            request.Name,
            request.DriverId,
            _databaseConnectionCatalog.BuildConnectionString(
                request.DriverId,
                request.Details with { Password = null }));
        return ApplyDatabasePanelConnection(
            panel,
            profile,
            request.Details.Password,
            tunnel,
            persisted: false);
    }

    /// <summary>Every saved database connection, for panel pickers.</summary>
    public IReadOnlyList<DatabaseConnectionProfile> DatabaseConnectionOptions =>
        [.. _catalog.Snapshot.DatabaseConnections.Select(item => item.Value)];

    public DatabaseConnectionProfile? FindDatabaseConnection(DatabaseConnectionProfileId id) =>
        _catalog.Snapshot.DatabaseConnections
            .SingleOrDefault(item => item.Value.Id == id)?.Value;

    public async Task<DatabaseConnectionProfile?> SaveDatabaseConnectionAsync(
        DatabaseConnectionProfileId? existingId,
        string name,
        string driverId,
        DatabaseConnectionDetails details,
        bool storePassword,
        ConnectionId? tunnelConnectionId,
        DatabaseInlineTunnelRequest? inlineTunnel = null,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        var saved = await DatabaseConnectionSettings.SaveDatabaseConnectionAsync(
            existingId,
            name,
            driverId,
            details,
            storePassword,
            tunnelConnectionId,
            inlineTunnel,
            cancellationToken);
        NotifyDatabaseConnectionOptionsChanged(saved);
        return saved;
    }

    internal async Task<DatabaseConnectionProfile?> StoreDatabasePasswordAsync(
        DatabaseConnectionProfileId profileId,
        string password,
        CancellationToken cancellationToken = default)
    {
        ClearError();
        var saved = await DatabaseConnectionSettings.StoreDatabasePasswordAsync(
            profileId,
            password,
            cancellationToken);
        NotifyDatabaseConnectionOptionsChanged(saved);
        return saved;
    }

    private Task<string?> ResolveDatabasePasswordAsync(
        SecretRef secret,
        CancellationToken cancellationToken) =>
        DatabaseConnectionSettings.ResolveDatabasePasswordAsync(secret, cancellationToken);

    private void NotifyDatabaseConnectionOptionsChanged(DatabaseConnectionProfile? saved)
    {
        if (saved is null)
        {
            return;
        }

        OnPropertyChanged(nameof(DatabaseConnectionOptions));
        OnPropertyChanged(nameof(DatabasePanelConnectionOptions));
    }

    private void StartAcceptedRuntimePanels(RuntimeWorkspaceViewModel runtime)
    {
        foreach (var tab in runtime.Tabs)
        {
            foreach (var panel in tab.Panels)
            {
                StartAcceptedRuntimePanel(
                    panel,
                    new SessionOwner(
                        HostMode.Desktop,
                        WindowId,
                        runtime.Id,
                        tab.Id,
                        panel.Id));
            }
        }
    }

    private void StartAcceptedRuntimePanel(RuntimePanelViewModel panel)
    {
        StartAcceptedRuntimePanel(panel, FindAcceptedPanelOwner(panel));
    }

    private void StartAcceptedRuntimePanel(
        RuntimePanelViewModel panel,
        SessionOwner? owner)
    {
        if (panel is FileRuntimePanelViewModel files)
        {
            _ = files.StartInitialization();
        }

        if (panel is BrowserRuntimePanelViewModel browser)
        {
            var initialization = browser.StartInitialization();
            _ = TrackBrowserAfterInitializationAsync(
                browser,
                initialization);
            if (owner is not null)
            {
                _ = TrackHostedPanelInitializationAsync(
                    StartAcceptedBrowserSessionAsync(
                        browser,
                        owner,
                        initialization));
            }
        }

        if (owner is not null)
        {
            var hostedInitialization = panel switch
            {
                TerminalRuntimePanelViewModel terminal =>
                    StartAcceptedTerminalSessionAsync(terminal, owner),
                DatabaseRuntimePanelViewModel database => database.StartHostingAsync(
                    SessionClient,
                    ClientId,
                    owner),
                RedisRuntimePanelViewModel redis => redis.StartHostingAsync(
                    SessionClient,
                    ClientId,
                    owner),
                DockerRuntimePanelViewModel docker => docker.StartHostingAsync(
                    SessionClient,
                    ClientId,
                    owner),
                GitRuntimePanelViewModel git => git.StartHostingAsync(
                    SessionClient,
                    ClientId,
                    owner),
                _ => null,
            };
            if (hostedInitialization is not null)
            {
                _ = TrackHostedPanelInitializationAsync(hostedInitialization);
            }
        }

        StartMonitorPanel(panel);
    }

    /// <summary>
    /// Browser session identity and its renderer attachment belong to the
    /// accepted workspace panel, not to whichever visual happens to be
    /// mounted. A presentation host adopts that attachment when shown.
    /// </summary>
    private async Task StartAcceptedBrowserSessionAsync(
        BrowserRuntimePanelViewModel browser,
        SessionOwner owner,
        Task rendererInitialization)
    {
        await rendererInitialization.ConfigureAwait(true);
        if (_shutdownStarted
            || _runtimeGraphLifetime.IsCancellationRequested
            || browser.SessionRequest.Owner != owner)
        {
            return;
        }

        var snapshot = await RuntimeGraph.EnsureBrowserSessionAsync(
            browser.SessionRequest,
            owner,
            _runtimeGraphLifetime.Token);
        if (snapshot is null)
        {
            return;
        }

        try
        {
            await browser.EnsureHostedRendererAsync(
                _runtimeGraphLifetime.Token);
        }
        catch (OperationCanceledException) when (
            _runtimeGraphLifetime.IsCancellationRequested || _shutdownStarted)
        {
        }
        catch (Exception)
        {
            // A layout readiness check or mounted presentation host can retry
            // the same panel-owned attachment. Until one succeeds, the panel
            // remains unavailable; no provider-authored text is surfaced.
        }
    }

    /// <summary>
    /// Starts the terminal process for an accepted panel independently of its
    /// renderer. A tab switch removes the inactive tab's visual tree, but that
    /// presentation detail must not decide whether the terminal exists or
    /// whether a workspace-scoped agent can reach it.
    /// </summary>
    private async Task StartAcceptedTerminalSessionAsync(
        TerminalRuntimePanelViewModel terminal,
        SessionOwner owner)
    {
        try
        {
            while (!_shutdownStarted
                && !_runtimeGraphLifetime.IsCancellationRequested)
            {
                await terminal.Initialization.ConfigureAwait(true);
                if (terminal.SessionRequest is not { } request
                    || request.Owner != owner)
                {
                    return;
                }

                var attemptToken = terminal.ConnectionAttemptToken;
                using var startupCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        _runtimeGraphLifetime.Token,
                        attemptToken);
                SessionSnapshot? snapshot;
                try
                {
                    snapshot = await RuntimeGraph.EnsureTerminalSessionAsync(
                        request,
                        owner,
                        startupCancellation.Token);
                }
                catch (OperationCanceledException) when (attemptToken.IsCancellationRequested)
                {
                    if (!terminal.IsCurrentConnectionAttempt(attemptToken))
                    {
                        continue;
                    }

                    return;
                }
                if (snapshot is null)
                {
                    if (attemptToken.IsCancellationRequested
                        && !terminal.IsCurrentConnectionAttempt(attemptToken))
                    {
                        continue;
                    }

                    return;
                }

                terminal.ObserveSessionSnapshot(snapshot);
                await WatchAcceptedTerminalSessionAsync(
                    terminal,
                    request,
                    _runtimeGraphLifetime.Token);
                if (ReferenceEquals(terminal.SessionRequest, request))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (
            _runtimeGraphLifetime.IsCancellationRequested || _shutdownStarted)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The mounted terminal view will report its own renderer failure.
            // Inactive panels have no surface on which to show a host transport
            // error, and provider text must not leak through the shell status.
        }
    }

    private async Task WatchAcceptedTerminalSessionAsync(
        TerminalRuntimePanelViewModel terminal,
        EnsureTerminalSessionRequest request,
        CancellationToken cancellationToken)
    {
        await foreach (var snapshot in RuntimeGraph.WatchSessionAsync(
            request.SessionId,
            cancellationToken))
        {
            terminal.ObserveSessionSnapshot(snapshot);
            if (!ReferenceEquals(terminal.SessionRequest, request))
            {
                return;
            }
        }
    }

    private SessionOwner? FindAcceptedPanelOwner(RuntimePanelViewModel panel)
    {
        foreach (var workspace in _openWorkspaces)
        {
            var tab = workspace.Tabs.FirstOrDefault(candidate =>
                candidate.Panels.Contains(panel));
            if (tab is not null)
            {
                return new SessionOwner(
                    HostMode.Desktop,
                    WindowId,
                    workspace.Id,
                    tab.Id,
                    panel.Id);
            }
        }

        if (RuntimeWorkspace is { } active
            && !_openWorkspaces.Contains(active)
            && active.Tabs.FirstOrDefault(candidate =>
                candidate.Panels.Contains(panel)) is { } activeTab)
        {
            return new SessionOwner(
                HostMode.Desktop,
                WindowId,
                active.Id,
                activeTab.Id,
                panel.Id);
        }

        return null;
    }

    private async Task TrackHostedPanelInitializationAsync(Task initialization)
    {
        try
        {
            await initialization;
        }
        catch (OperationCanceledException) when (
            _runtimeGraphLifetime.IsCancellationRequested || _shutdownStarted)
        {
        }
    }

    private async Task TrackBrowserAfterInitializationAsync(
        BrowserRuntimePanelViewModel panel,
        Task initialization)
    {
        try
        {
            await initialization;
        }
        catch (OperationCanceledException) when (_shutdownStarted)
        {
        }
        catch (Exception exception)
        {
            if (!panel.HasRouteError)
            {
                SetError(exception.Message);
                return;
            }

            SetError(panel.RouteErrorMessage!);
        }
    }

    private void StartMonitorPanel(RuntimePanelViewModel panel)
    {
        var initialization = panel switch
        {
            StatisticsRuntimePanelViewModel statistics => statistics.Start(),
            ProcessMonitorRuntimePanelViewModel processes => processes.Start(),
            _ => null,
        };
        if (initialization is not null)
        {
            _ = TrackMonitorAfterInitializationAsync(panel, initialization);
        }
    }

    private async Task TrackMonitorAfterInitializationAsync(
        RuntimePanelViewModel panel,
        Task initialization)
    {
        try
        {
            await initialization;
            var hosted = panel switch
            {
                StatisticsRuntimePanelViewModel statistics => statistics.HasHostedSession,
                ProcessMonitorRuntimePanelViewModel processes => processes.HasHostedSession,
                _ => false,
            };
            if (hosted && !_runtimeGraphLifetime.IsCancellationRequested)
            {
                await _uiThreadDispatcher.InvokeAsync(
                    () => TrackRecentSession(panel),
                    _runtimeGraphLifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_runtimeGraphLifetime.IsCancellationRequested)
        {
        }
    }

    private IReadOnlyList<LauncherConnectionViewModel> ResolveWorkspaceConnections(
        WorkspaceDefinition workspace)
    {
        var ids = ResolveWorkspaceConnectionDefinitions(workspace)
            .Select(item => item.Id)
            .ToHashSet();
        return [.. Connections.Where(item => ids.Contains(item.Id))];
    }

    private IEnumerable<ConnectionProfile> ResolveWorkspaceConnectionDefinitions(
        WorkspaceDefinition workspace)
    {
        var directIds = workspace.Entries
            .OfType<WorkspaceEntry.ConnectionReference>()
            .Select(item => item.ConnectionId);
        var screenIds = workspace.Entries
            .OfType<WorkspaceEntry.ScreenReference>()
            .Select(item => item.ScreenId)
            .ToHashSet();
        var panelConnectionIds = _catalog.Snapshot.Screens
            .Where(item => screenIds.Contains(item.Value.Id))
            .SelectMany(item => item.Value.Panels)
            .Select(item => item.ConnectionId)
            .OfType<ConnectionId>();
        var workspaceTabConnectionIds = workspace.Entries
            .OfType<WorkspaceEntry.Tab>()
            .SelectMany(item => item.Panels)
            .Select(item => item.ConnectionId)
            .OfType<ConnectionId>();
        var ids = directIds
            .Concat(panelConnectionIds)
            .Concat(workspaceTabConnectionIds)
            .ToHashSet();
        return _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .Where(item => ids.Contains(item.Id));
    }

    /// <summary>
    /// The one place a saved connection becomes usable. Contract: a profile
    /// with a host-connection reference is resolved against the CURRENT
    /// catalog snapshot at this moment — the referenced connection's endpoint,
    /// authentication, keep-alive, and host-key policy with this profile's
    /// name, preferred panel, and startup (repository path) — so later edits
    /// to the referenced connection apply on the next open. Returns null when
    /// the connection is missing OR its reference cannot be resolved; use
    /// <see cref="ConnectionUnavailableMessage"/> to tell those apart.
    /// </summary>
    private ConnectionProfile? FindConnection(ConnectionId id) =>
        FindStoredConnection(id)?.ResolveHostConnection(FindStoredConnection);

    /// <summary>
    /// The stored profile exactly as saved — a reference profile still carries
    /// its stand-in endpoint. Only for editing, display fallbacks, and
    /// resolving references; every launch path goes through
    /// <see cref="FindConnection"/>.
    /// </summary>
    private ConnectionProfile? FindStoredConnection(ConnectionId id) => _catalog.Snapshot.Connections
        .Select(item => item.Value)
        .SingleOrDefault(item => item.Id == id);

    /// <summary>
    /// The failure text for a <see cref="FindConnection"/> null: a stored
    /// profile whose reference broke fails differently from a deleted one.
    /// </summary>
    private string ConnectionUnavailableMessage(ConnectionId id) =>
        FindStoredConnection(id) is { HostConnectionId: not null }
            ? "The saved connection this Git connection references no longer exists. Edit the Git connection and choose its SSH connection again."
            : "That connection no longer exists.";

    private ConnectionProfile? LocalConnection() => _catalog.Snapshot.Connections
        .Select(item => item.Value)
        .FirstOrDefault(item => item.Endpoint is ConnectionEndpoint.Local);

    private ConnectionProfile? ConnectionForFileProfile(FileProviderProfileId? profileId)
    {
        if (profileId is null || profileId == BuiltInFileProviders.HomeId)
        {
            return LocalConnection();
        }

        return _catalog.Snapshot.Connections
            .Select(item => item.Value)
            .Where(item => item.Endpoint is ConnectionEndpoint.Ssh)
            .FirstOrDefault(item =>
                ConnectionFileProviderProfiles.Id(item.Id) == profileId.Value);
    }

    private const int DefaultSshPort = 22;

    private const int ScreenSummaryConnectionLimit = 2;

    /// <summary>
    /// The saved-screen card reads "3 panels · prod-api, staging-web": the panel
    /// count plus the distinct connections it opens, so the card says what the
    /// screen touches without repeating the layout's internal name.
    /// </summary>
    private static string ScreenSummary(
        ScreenDefinition screen,
        DefinitionCatalogSnapshot snapshot)
    {
        var panels = $"{screen.Panels.Count} {(screen.Panels.Count == 1 ? "panel" : "panels")}";
        var namesById = snapshot.Connections.ToDictionary(
            item => item.Value.Id,
            item => item.Value.Name);
        var connections = screen.Panels
            .Select(panel => panel.ConnectionId)
            .OfType<ConnectionId>()
            .Distinct()
            .Select(id => namesById.TryGetValue(id, out var name) ? name : id.Value)
            .ToArray();

        if (connections.Length == 0)
        {
            return panels;
        }

        var shown = string.Join(", ", connections.Take(ScreenSummaryConnectionLimit));
        var remaining = connections.Length - ScreenSummaryConnectionLimit;
        return remaining > 0
            ? $"{panels} · {shown} +{remaining}"
            : $"{panels} · {shown}";
    }

    /// <summary>
    /// The launcher card shows what a reference profile resolves to right now;
    /// a broken reference falls back to the stored stand-in, and opening it
    /// reports the missing referenced connection.
    /// </summary>
    private static ConnectionProfile ResolveForDisplay(
        DefinitionCatalogSnapshot snapshot,
        ConnectionProfile profile) =>
        profile.ResolveHostConnection(id => snapshot.Connections
            .Select(item => item.Value)
            .SingleOrDefault(item => item.Id == id))
        ?? profile;

    private static LauncherConnectionViewModel ToConnectionItem(
        ConnectionProfile connection,
        long revision)
    {
        var (detail, canOpen) = connection.Endpoint switch
        {
            ConnectionEndpoint.Local local =>
                (local.ShellPath ?? "Default local shell", true),
            ConnectionEndpoint.Ssh ssh => (
                ssh.Port == DefaultSshPort
                    ? $"{ssh.Username ?? "user"}@{ssh.Host}"
                    : $"{ssh.Username ?? "user"}@{ssh.Host}:{ssh.Port}",
                true),
            ConnectionEndpoint.Docker docker => ($"Container {docker.Container}", true),
            ConnectionEndpoint.Wsl wsl => ($"Distribution {wsl.Distribution}", OperatingSystem.IsWindows()),
            _ => ("Unsupported endpoint", false),
        };
        return new(
            connection.Id,
            revision,
            connection.Name,
            KindBadges.Connection(connection),
            detail,
            canOpen ? "Validated on open" : "Unavailable on this platform",
            canOpen,
            connection.Tags);
    }

    private LauncherConnectionViewModel ToFileConnectionItem(
        FileProviderProfile profile,
        long revision) =>
        new(
            new ConnectionId(profile.Id.Value),
            revision,
            profile.Name,
            FileProviderKindLabel(profile.ProviderKind),
            FileProviderEndpoint(profile.Configuration),
            "Validated on open",
            _fileProviderRuntime is not null,
            [],
            SavedConnectionFamily.Files,
            profile.Id.Value);

    private LauncherConnectionViewModel ToDatabaseConnectionItem(
        DatabaseConnectionProfile profile,
        long revision)
    {
        var driver = _databaseConnectionCatalog?.Drivers
            .FirstOrDefault(item => string.Equals(item.Id, profile.DriverId, StringComparison.Ordinal));
        return new(
            new ConnectionId(profile.Id.Value),
            revision,
            profile.Name,
            driver?.DisplayName ?? profile.DriverId,
            DatabaseConnectionDetailText(profile),
            _databaseConnectionCatalog is null
                ? "Database drivers are unavailable in this build"
                : "Validated on connect",
            _databaseConnectionCatalog is not null,
            [],
            SavedConnectionFamily.Database,
            profile.Id.Value);
    }

    /// <summary>
    /// A compact endpoint summary for the card. The stored connection string
    /// never contains the password, so falling back to it verbatim is safe.
    /// </summary>
    private string DatabaseConnectionDetailText(DatabaseConnectionProfile profile)
    {
        if (_databaseConnectionCatalog is null)
        {
            return profile.DriverId;
        }

        try
        {
            var details = _databaseConnectionCatalog.ParseConnectionDetails(
                profile.DriverId,
                profile.ConnectionString);
            if (details.FilePath is { } filePath)
            {
                return filePath;
            }

            var host = details.Host ?? "localhost";
            var endpoint = details.Port is { } port
                ? $"{host}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : host;
            return details.Database is { } database
                ? $"{endpoint}/{database}"
                : endpoint;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return profile.ConnectionString;
        }
    }

    private static string FileProviderEndpoint(FileProviderConfiguration configuration) =>
        configuration switch
        {
            FileProviderConfiguration.Local value => value.RootPath,
            FileProviderConfiguration.S3 value => value.ServiceUri is null
                ? $"s3://{value.BucketName} · {value.Region ?? "us-east-1"}"
                : $"{value.ServiceUri.Host} · {value.BucketName}",
            FileProviderConfiguration.Sftp value =>
                $"SSH connection {value.ConnectionId.Value} · {value.RemoteRoot}",
            FileProviderConfiguration.Ftp value =>
                $"{value.Security} · {value.Host}:{value.Port}{value.RemoteRoot}",
            FileProviderConfiguration.Smb value =>
                $"smb://{value.Server}/{value.Share}{value.RemoteRoot}",
            FileProviderConfiguration.WebDav value => value.BaseUri.AbsoluteUri,
            _ => "Unsupported provider",
        };

    private static string FileProviderKindLabel(FileProviderKind kind) => kind switch
    {
        FileProviderKind.Local => "Local",
        FileProviderKind.S3 => "S3",
        FileProviderKind.Sftp => "SFTP",
        FileProviderKind.Ftp => "FTP/FTPS",
        FileProviderKind.Smb => "SMB",
        FileProviderKind.WebDav => "WebDAV",
        _ => kind.ToString().ToUpperInvariant(),
    };

    private static string FileProviderFamilyLabel(FileProviderFamily family) => family switch
    {
        FileProviderFamily.Posix or FileProviderFamily.Windows => "Local",
        FileProviderFamily.S3 => "S3",
        FileProviderFamily.Sftp => "SFTP",
        FileProviderFamily.Ftp => "FTP/FTPS",
        FileProviderFamily.Smb => "SMB",
        FileProviderFamily.WebDav => "WebDAV",
        _ => family.ToString().ToUpperInvariant(),
    };

    private static string FileProviderDetail(FileProviderProfileDescriptor profile)
    {
        var root = FileLocationPresentation.Display(profile.Root);
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.IsNullOrWhiteSpace(profile.Root.Authority)
                ? "Configured root"
                : profile.Root.Authority;
        }

        return string.IsNullOrWhiteSpace(profile.Root.Authority)
            ? root
            : $"{profile.Root.Authority} · {root}";
    }

    public Task QuiesceForShutdownAsync(CancellationToken cancellationToken)
    {
        Task shutdown;
        lock (_shutdownGate)
        {
            _shutdownStarted = true;
            try
            {
                _runtimeGraphLifetime.Cancel();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                SecretSafeDiagnostics.WriteTraceAndStandardError(
                    "runtime-graph.shutdown-cancellation.failed",
                    exception);
            }
            _shutdownTask ??= QuiesceForShutdownCoreAsync();
            shutdown = _shutdownTask;
        }

        return shutdown.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Releases presentation-owned runtimes while Avalonia still owns its UI
    /// thread. The desktop lifetime calls this before its asynchronous shutdown
    /// finalizer; repeated calls are safe so the main thread can provide a
    /// fallback after the lifetime returns.
    /// </summary>
    public void TeardownPresentationForShutdown()
    {
        _uiThreadDispatcher.VerifyAccess();
        if (_presentationTeardownCompleted)
        {
            return;
        }

        _shutdownStarted = true;
        AgentWorkspaceScope.StopTracking(_runtimeWorkspace);
        StopTrackingRecovery(_runtimeWorkspace);
        QueueRemainingRecentSessionCompletions(RecentSessionOutcome.GracefullyClosed);

        var openWorkspaces = _openWorkspaces.ToArray();
        Notifications.ForgetAll();
        foreach (var workspace in openWorkspaces)
        {
            workspace.DisposePanels();
        }

        // The active workspace normally belongs to OpenWorkspaces. Retain a
        // defensive path for a partially completed open/restore operation.
        if (_runtimeWorkspace is { } activeWorkspace
            && !openWorkspaces.Contains(activeWorkspace))
        {
            activeWorkspace.DisposePanels();
        }

        _presentationTeardownCompleted = true;
    }

    private async Task QuiesceForShutdownCoreAsync()
    {
        try
        {
            await DefaultAgentPolicySettings.QuiesceAsync().ConfigureAwait(false);

            if (_agentRuntimeFactory is null)
            {
                if (AgentChat is not null)
                {
                    await AgentChat.QuiesceAsync(CancellationToken.None).ConfigureAwait(false);
                    AgentChat.Dispose();
                }
            }
            else
            {
                await Task.WhenAll(
                        _workspaceAgentChats.Values.Select(owned => owned.QuiesceAsync()))
                    .ConfigureAwait(false);
            }

            _catalog.Changed -= OnCatalogChanged;
            _mcpServerDiagnostics?.Changed -= OnMcpServerDiagnosticsChanged;

            FileTransferState.Dispose();
            TerminalContinuity.Dispose();
            WorkspaceAutoSave.Seal();
            RuntimeRecovery.Seal();

            History.SealOperations();

            await RuntimeGraph.QuiesceAsync().ConfigureAwait(false);
        }
        finally
        {
            await FinalizeWorkspaceIsolationShutdownAsync().ConfigureAwait(false);
        }
    }

    private async Task FinalizeWorkspaceIsolationShutdownAsync()
    {
        try
        {
            await AwaitWorkspaceIsolationPreparationsAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-isolation.shutdown-prepare-drain.failed",
                exception);
        }

        try
        {
            await AwaitWorkspaceActivationsAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "runtime-graph.shutdown-activation-drain.failed",
                exception);
        }

        await FinalizeWorkspaceNetworkShutdownAsync().ConfigureAwait(false);
        await DisposeInactiveWorkspaceNetworkAsync().ConfigureAwait(false);

        try
        {
            await AwaitWorkspaceIsolationCleanupAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-isolation.shutdown-cleanup-drain.failed",
                exception);
        }

        try
        {
            await RetryWorkspaceGraphRollbacksAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "runtime-graph.shutdown-registration-rollback.failed",
                exception);
        }

        try
        {
            var isolationReleaseErrors = await ReleaseAllWorkspaceIsolationAsync()
                .ConfigureAwait(false);
            if (isolationReleaseErrors.Count > 0)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "workspace-isolation.shutdown-stop.failed",
                    SecretSafeDiagnosticKind.Unexpected);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnostics.WriteTraceAndStandardError(
                "workspace-isolation.shutdown-release.failed",
                exception);
        }
        finally
        {
            _workspaceDefinitionOccupancy.UnregisterWindow(WindowId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdownStarted = true;
        _workspaceDefinitionOccupancy.UnregisterWindow(WindowId);
        ReleaseAppearanceEditing();
        History.SealOperations();

        _catalog.Changed -= OnCatalogChanged;
        _mcpServerDiagnostics?.Changed -= OnMcpServerDiagnosticsChanged;

        FileTransferState.PropertyChanged -= OnFileTransferStatePropertyChanged;
        FileTransferState.Dispose();
        TerminalContinuity.PropertyChanged -=
            OnTerminalContinuityPropertyChanged;
        TerminalContinuity.Dispose();
        SecretSettings.PropertyChanged -= OnSecretSettingsPropertyChanged;
        SecretSettings.ProjectionChanged -= OnSecretProjectionChanged;
        SecretSettings.Dispose();
        DefaultAgentPolicySettings.PropertyChanged -=
            OnDefaultAgentPolicySettingsPropertyChanged;
        DefaultAgentPolicySettings.PolicyChanged -=
            OnDefaultAgentPolicyChanged;
        DefaultAgentPolicySettings.Dispose();
        WorkspaceAutoSave.Seal();
        RuntimeRecovery.Seal();
        _navigation.PropertyChanged -= OnShellNavigationPropertyChanged;
        DefinitionEdit.PropertyChanged -= OnDefinitionEditPropertyChanged;
        DefinitionSettings.PropertyChanged -= OnDefinitionSettingsPropertyChanged;
        TerminalSettings.PropertyChanged -= OnTerminalSettingsPropertyChanged;
        AppearanceSettings.PropertyChanged -= OnAppearanceSettingsPropertyChanged;
        AppearanceSettings.BackgroundSaveStarting -= OnAppearanceBackgroundSaveStarting;
        AppearanceSettings.BackgroundSaveCompleted -= OnAppearanceBackgroundSaveCompleted;
        _appearancePreview.Changed -= OnAppearancePreviewChanged;
        WorkspaceSettings.PropertyChanged -= OnWorkspaceSettingsPropertyChanged;
        NetworkSettings.Dispose();
        FileProviderSettings.PropertyChanged -= OnFileProviderSettingsPropertyChanged;
        AiProviderSettings.PropertyChanged -= OnAiProviderSettingsPropertyChanged;
        AiProviderSettings.RuntimeProfilesChanged -= OnAiProviderRuntimeProfilesChanged;
        AgentWorkspaceScope.PropertyChanged -= OnAgentWorkspaceScopePropertyChanged;
        Launcher.PropertyChanged -= OnLauncherPropertyChanged;
        AgentWorkspaceScope.StopTracking(_runtimeWorkspace);
        StopTrackingRecovery(_runtimeWorkspace);
        // Every open workspace, not only the one in front: the others are just
        // as alive, and leaving them behind leaks their sessions.
        Notifications.ForgetAll();
        foreach (var workspace in _openWorkspaces.ToArray())
        {
            workspace.DisposePanels();
        }

        _openWorkspaces.Clear();
        _runtimeSources.Clear();
        foreach (var workspaceId in _workspaceNetworkControls.Keys.ToArray())
        {
            ScheduleWorkspaceNetworkCleanup(workspaceId);
        }
        _runtimeWorkspace?.DisposePanels();
        _runtimeWorkspace = null;
        WorkspaceSettings.Dispose();
        SavedScreenSettings.Dispose();
        TerminalConnectionSettings.Dispose();
        McpServerSettings.Dispose();
        FileProviderSettings.Dispose();
        AiProviderSettings.Dispose();
        AgentWorkspaceScope.Dispose();
        WorkspaceAutoSave.Dispose();
        RuntimeRecovery.Dispose();
        DefinitionSettings.Dispose();
        TerminalSettings.Dispose();
        AppearanceSettings.Dispose();
        Onboarding?.Dispose();
        AgentChat?.Cancel();
        if (_agentRuntimeFactory is null)
        {
            AgentChat?.Dispose();
        }
        else
        {
            foreach (var owned in _workspaceAgentChats.Values)
            {
                owned.Dispose();
            }

            _workspaceAgentChats.Clear();
            AgentChat = null;
        }
        History.PropertyChanged -= OnHistoryPropertyChanged;
        History.SnapshotChanged -= OnHistorySnapshotChanged;
        History.Dispose();
        ApplicationUpdates.Dispose();
        Launcher.Dispose();
        _runtimeGraphLifetime.Cancel();
        RuntimeGraph.Dispose();
        _runtimeGraphLifetime.Dispose();
    }

    private sealed class WorkspaceAgentChat : IDisposable
    {
        private readonly IGovernedAgentRuntime _runtime;
        private readonly Action _runFinished;
        private readonly Action _runningStateChanged;
        private readonly Action<AgentToolActivityViewModel?> _activityChanged;

        public WorkspaceAgentChat(
            IGovernedAgentRuntime runtime,
            AgentChatViewModel viewModel,
            Action runFinished,
            Action runningStateChanged,
            Action<AgentToolActivityViewModel?> activityChanged)
        {
            _runtime = runtime;
            ViewModel = viewModel;
            _runFinished = runFinished;
            _runningStateChanged = runningStateChanged;
            _activityChanged = activityChanged;
            ViewModel.RunFinished += OnRunFinished;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        public AgentChatViewModel ViewModel { get; }

        public Task QuiesceAsync() =>
            ViewModel.QuiesceAsync(CancellationToken.None);

        public void Dispose()
        {
            ViewModel.RunFinished -= OnRunFinished;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _activityChanged(null);
            ViewModel.Cancel();
            ViewModel.Dispose();
            _runtime.Dispose();
        }

        private void OnRunFinished(object? sender, EventArgs e)
        {
            _ = sender;
            _ = e;
            _runFinished();
        }

        private void OnViewModelPropertyChanged(
            object? sender,
            PropertyChangedEventArgs eventArgs)
        {
            _ = sender;
            if (string.Equals(eventArgs.PropertyName, nameof(AgentChatViewModel.PanelActivity), StringComparison.Ordinal))
            {
                _activityChanged(ViewModel.PanelActivity);
            }

            if (string.Equals(eventArgs.PropertyName, nameof(AgentChatViewModel.IsBusy), StringComparison.Ordinal))
            {
                _runningStateChanged();
            }
        }
    }

    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
    }

    private static string PlatformName()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        return "Unknown platform";
    }

    private static Symbol WorkspaceIconSymbol(string icon) => WorkspaceIcons.SymbolFor(icon);

    private static string PanelTitle(ScreenPanelKind kind) => kind switch
    {
        ScreenPanelKind.Terminal => "Terminal",
        ScreenPanelKind.Browser => "Browser",
        ScreenPanelKind.FileViewer => "Files",
        ScreenPanelKind.Statistics => "Statistics",
        ScreenPanelKind.ProcessMonitor => "Processes",
        ScreenPanelKind.DatabaseViewer => "Database",
        ScreenPanelKind.Docker => "Docker",
        ScreenPanelKind.Git => "Git",
        _ => "Panel",
    };

    private static string PanelTitle(PanelKind kind) => kind switch
    {
        PanelKind.Terminal => "Terminal",
        PanelKind.Browser => "Browser",
        PanelKind.FileViewer => "File Viewer",
        PanelKind.Statistics => "Statistics",
        PanelKind.ProcessMonitor => "Process Monitor",
        PanelKind.DatabaseViewer => "Database",
        PanelKind.Docker => "Docker",
        PanelKind.Git => "Git",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static PanelKind PanelKindFromDefinition(ScreenPanelKind kind) => kind switch
    {
        ScreenPanelKind.Terminal => PanelKind.Terminal,
        ScreenPanelKind.Browser => PanelKind.Browser,
        ScreenPanelKind.FileViewer => PanelKind.FileViewer,
        ScreenPanelKind.Statistics => PanelKind.Statistics,
        ScreenPanelKind.ProcessMonitor => PanelKind.ProcessMonitor,
        ScreenPanelKind.DatabaseViewer => PanelKind.DatabaseViewer,
        ScreenPanelKind.Docker => PanelKind.Docker,
        ScreenPanelKind.Git => PanelKind.Git,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static PanelKind PanelKindFromRecovery(string? kindLabel) =>
        kindLabel?.Replace(" ", string.Empty).ToUpperInvariant() switch
        {
            "TERMINAL" => PanelKind.Terminal,
            "BROWSER" => PanelKind.Browser,
            "FILEVIEWER" => PanelKind.FileViewer,
            "STATISTICS" => PanelKind.Statistics,
            "PROCESSMONITOR" => PanelKind.ProcessMonitor,
            "DATABASE" or "DATABASEVIEWER" => PanelKind.DatabaseViewer,
            "DOCKER" => PanelKind.Docker,
            "GIT" => PanelKind.Git,
            _ => throw new InvalidOperationException(
                "The recovered panel kind is not supported by this build."),
        };

    private static string RequireName(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static ClientId RequireDesktopClientId(
        IAgentApprovalPrincipal approvalPrincipal)
    {
        ArgumentNullException.ThrowIfNull(approvalPrincipal);
        var actor = approvalPrincipal.Actor
            ?? throw new ArgumentException(
                "The agent approval principal must expose a local human actor.",
                nameof(approvalPrincipal));
        if (actor.Kind != ActorKind.Human || actor.ClientId is not { } clientId)
        {
            throw new ArgumentException(
                "The agent approval principal must expose a local human client identity.",
                nameof(approvalPrincipal));
        }

        return clientId;
    }

    /// <summary>
    /// Replaces a collection only when the result would look different.
    ///
    /// <see cref="Replace"/> clears and refills, which destroys every realized row
    /// and drops whatever the pointer was over. Most refreshes are provoked by
    /// something unrelated and produce exactly the rows already on screen, so those
    /// have to leave the collection alone.
    /// </summary>
    private static void ReplaceIfChanged<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> values,
        Func<T, T, bool> presentsSame)
    {
        if (target.Count == values.Count)
        {
            var unchanged = true;
            for (var index = 0; index < values.Count; index++)
            {
                if (!presentsSame(target[index], values[index]))
                {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged)
            {
                return;
            }
        }

        Replace(target, values);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private DefinitionStoreResult<T> Fail<T>(string message)
    {
        SetError(message);
        return DefinitionStoreResult<T>.Failure(new(
            DefinitionStoreErrorCode.InvalidDefinition,
            message));
    }

    private void ApplyError(DefinitionStoreError? error) => OperationError = error?.Message;

    private sealed record RuntimeSessionIdentity(
        SessionId SessionId,
        PanelKind Kind);

}
