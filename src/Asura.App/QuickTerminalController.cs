using Asura.App.ViewModels;
using Asura.App.Views;
using Asura.Application;
using Asura.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Asura.App;

public sealed class QuickTerminalController : IDisposable
{
    private static readonly KeyStroke DefaultGesture = QuickTerminalSettings.Default.Hotkey;
    private readonly IGlobalHotkeyService _globalHotkey;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly IDefinitionCatalog _catalog;
    private readonly IConnectionRuntime _connectionRuntime;
    private readonly IAgentWorkspaceRuntimeFactory _agentRuntimeFactory;
    private readonly IAiProviderProfileRuntime _aiProviderRuntime;
    private readonly IAgentRunAuditReader _agentRunAuditReader;
    private readonly IAgentModelFavoriteStore? _agentModelFavoriteStore;
    private readonly AgentPolicyCoordinator? _agentPolicyCoordinator;
    private readonly IHostAccessibilityPreferencesSource _hostAccessibilityPreferences;
    private readonly IActiveWindowBoundsSource _activeWindowBounds;
    private readonly RuntimeRecoveryWriter _runtimeRecoveryWriter;
    private readonly SessionRestoreCoordinator _sessionRestoreCoordinator;
    private readonly ApplicationStartupState _startupState;
    private readonly AppearancePreviewCoordinator _appearancePreview;
    private readonly QuickTerminalDefinitionTracker _definitionTracker;
    private QuickTerminalViewModel _viewModel;
    private QuickTerminalSettings _settings = QuickTerminalSettings.Default;
    private HostAccessibilityPreferences _hostPreferences =
        HostAccessibilityPreferences.Default;
    private MainWindow? _mainWindow;
    private QuickTerminalWindow? _quickWindow;
    private IDisposable? _animationCompletion;
    private readonly QuickTerminalTransitionTimeline _transition = new();
    private KeyStroke? _configuredGesture;
    private KeyStroke? _activeGesture;
    private GlobalHotkeyRegistrationResult? _registrationResult;
    private long _settingsRevision = -1;
    private double _availableHeight;
    private double? _pendingHeightFraction;
    private bool _heightSaveRunning;
    private bool _restorePreviousApplicationAfterHide;
    private bool _quickWindowIsActive;
    private bool _recoveryReady;
    private bool _initialized;
    private bool _isShuttingDown;
    private bool _disposed;

    public QuickTerminalController(
        IGlobalHotkeyService globalHotkey,
        MainWindowViewModel mainWindowViewModel,
        IDefinitionCatalog catalog,
        IConnectionRuntime connectionRuntime,
        IAgentWorkspaceRuntimeFactory agentRuntimeFactory,
        IAiProviderProfileRuntime aiProviderRuntime,
        IAgentRunAuditReader agentRunAuditReader,
        IHostAccessibilityPreferencesSource hostAccessibilityPreferences,
        IActiveWindowBoundsSource activeWindowBounds,
        RuntimeRecoveryWriter runtimeRecoveryWriter,
        SessionRestoreCoordinator sessionRestoreCoordinator,
        ApplicationStartupState startupState,
        AppearancePreviewCoordinator? appearancePreview = null,
        IAgentModelFavoriteStore? agentModelFavoriteStore = null,
        AgentPolicyCoordinator? agentPolicyCoordinator = null)
    {
        _globalHotkey = globalHotkey ?? throw new ArgumentNullException(nameof(globalHotkey));
        _mainWindowViewModel = mainWindowViewModel
            ?? throw new ArgumentNullException(nameof(mainWindowViewModel));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _connectionRuntime = connectionRuntime
            ?? throw new ArgumentNullException(nameof(connectionRuntime));
        _agentRuntimeFactory = agentRuntimeFactory
            ?? throw new ArgumentNullException(nameof(agentRuntimeFactory));
        _aiProviderRuntime = aiProviderRuntime
            ?? throw new ArgumentNullException(nameof(aiProviderRuntime));
        _agentRunAuditReader = agentRunAuditReader
            ?? throw new ArgumentNullException(nameof(agentRunAuditReader));
        _agentModelFavoriteStore = agentModelFavoriteStore;
        _agentPolicyCoordinator = agentPolicyCoordinator;
        _hostAccessibilityPreferences = hostAccessibilityPreferences
            ?? throw new ArgumentNullException(nameof(hostAccessibilityPreferences));
        _activeWindowBounds = activeWindowBounds
            ?? throw new ArgumentNullException(nameof(activeWindowBounds));
        _runtimeRecoveryWriter = runtimeRecoveryWriter
            ?? throw new ArgumentNullException(nameof(runtimeRecoveryWriter));
        _sessionRestoreCoordinator = sessionRestoreCoordinator
            ?? throw new ArgumentNullException(nameof(sessionRestoreCoordinator));
        _startupState = startupState ?? throw new ArgumentNullException(nameof(startupState));
        _appearancePreview = appearancePreview ?? new AppearancePreviewCoordinator();
        _definitionTracker = new QuickTerminalDefinitionTracker(_catalog.Snapshot);
        _viewModel = CreateViewModel();
    }

    public QuickTerminalViewModel ViewModel => _viewModel;

    public bool IsVisible => _transition.State != QuickTerminalVisibilityState.Hidden
        && _quickWindow?.IsVisible == true;

    public void Initialize(MainWindow mainWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mainWindow);
        if (_initialized)
        {
            throw new InvalidOperationException("The Quick Terminal controller is already initialized.");
        }

        _initialized = true;
        SetMainWindow(mainWindow);
        _globalHotkey.Pressed += OnGlobalHotkeyPressed;
        _globalHotkey.EscapePressed += OnEscapePressed;
        _catalog.Changed += OnCatalogChanged;
        _appearancePreview.Changed += OnAppearancePreviewChanged;
        _hostAccessibilityPreferences.Changed += OnHostAccessibilityPreferencesChanged;
        ApplyHostAccessibilityPreferences();
        ApplySettingsFromCatalog();
        _ = RestoreOnStartupAsync();
    }

    public void SetMainWindow(MainWindow mainWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
    }

    public void Toggle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isShuttingDown)
        {
            return;
        }

        if (!_initialized)
        {
            throw new InvalidOperationException("The Quick Terminal controller has not been initialized.");
        }

        if (_transition.State is QuickTerminalVisibilityState.Visible
            or QuickTerminalVisibilityState.Showing)
        {
            Hide();
            return;
        }

        MacOsQuickTerminalFocus.CaptureFrontmostApplication();
        _restorePreviousApplicationAfterHide = false;
        var window = GetOrCreateWindow();
        var progress = PauseTransition(window);
        if (_transition.State == QuickTerminalVisibilityState.Hidden)
        {
            progress = 0;
        }

        PositionAtTopOfWorkingArea(window);
        _ = _globalHotkey.BeginEscapeCapture();
        try
        {
            var wasVisible = window.IsVisible;
            if (!wasVisible)
            {
                window.PrepareReveal(progress);
                window.Show();
            }
            else
            {
                window.SetRevealProgress(progress);
            }

            window.ApplyBackdrop();
            if (!wasVisible)
            {
                window.CompletePreparedReveal();
            }

            window.Activate();
            window.FocusTerminal();
            StartTransition(window, progress, 1);
        }
        catch
        {
            _globalHotkey.EndEscapeCapture();
            PauseTransition(window);
            _transition.Reset();
            if (window.IsVisible)
            {
                window.Hide();
            }

            throw;
        }
    }

    public void Hide() => Hide(restorePreviousApplication: true);

    /// <summary>
    /// Routes the application-level New Tab command to Quick Terminal when it
    /// owns keyboard focus. On macOS, native menu key equivalents are invoked
    /// before Avalonia can raise KeyDown on the focused window.
    /// </summary>
    public async Task<bool> TryAddTabToActiveQuickTerminalAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_quickWindowIsActive || _quickWindow?.IsVisible != true)
        {
            return false;
        }

        await _viewModel.AddTabAsync();
        _quickWindow.FocusTerminal();
        return true;
    }

    public async Task<bool> TryCloseTabInActiveQuickTerminalAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_quickWindowIsActive || _quickWindow?.IsVisible != true)
        {
            return false;
        }

        if (_viewModel.ActiveTab is { } activeTab)
        {
            await _viewModel.CloseTabAsync(activeTab);
            _quickWindow.FocusTerminal();
        }

        // The command belongs to Quick Terminal even when its only tab cannot
        // be closed. Never let it fall through and mutate the main window.
        return true;
    }

    private void Hide(bool restorePreviousApplication)
    {
        _globalHotkey.EndEscapeCapture();
        if (_quickWindow?.IsVisible != true
            || _transition.State is QuickTerminalVisibilityState.Hidden
                or QuickTerminalVisibilityState.Hiding)
        {
            return;
        }

        _restorePreviousApplicationAfterHide = restorePreviousApplication;

        var window = _quickWindow;
        var progress = PauseTransition(window);
        StartTransition(window, progress, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _isShuttingDown = true;
        _disposed = true;
        CancelTransition();
        _catalog.Changed -= OnCatalogChanged;
        _appearancePreview.Changed -= OnAppearancePreviewChanged;
        _hostAccessibilityPreferences.Changed -= OnHostAccessibilityPreferencesChanged;
        _globalHotkey.Pressed -= OnGlobalHotkeyPressed;
        _globalHotkey.EscapePressed -= OnEscapePressed;
        _globalHotkey.EndEscapeCapture();
        _globalHotkey.Unregister();
        _viewModel.RecoveryStateChanged -= OnRecoveryStateChanged;
        _viewModel.Dispose();

        if (_quickWindow is not null)
        {
            _quickWindow.DismissRequested -= OnDismissRequested;
            _quickWindow.AgentSettingsRequested -= OnAgentSettingsRequested;
            _quickWindow.NewConnectionRequested -= OnNewConnectionRequested;
            _quickWindow.HeightResizeCompleted -= OnHeightResizeCompleted;
            _quickWindow.Activated -= OnQuickWindowActivated;
            _quickWindow.Deactivated -= OnQuickWindowDeactivated;
            _quickWindow.Closed -= OnQuickWindowClosed;
            _quickWindow.ClosePermanently();
            _quickWindow = null;
        }

        _mainWindow = null;
    }

    private bool MotionEnabled => QuickTerminalPresentationPolicy.ShouldAnimate(
        _settings,
        _hostPreferences);

    private QuickTerminalViewModel CreateViewModel()
    {
        var viewModel = new QuickTerminalViewModel(
            _mainWindowViewModel,
            _catalog,
            _connectionRuntime,
            null,
            _aiProviderRuntime,
            _agentRunAuditReader,
            AvaloniaUiThreadDispatcher.Instance,
            _agentModelFavoriteStore,
            _agentRuntimeFactory,
            _agentPolicyCoordinator);
        viewModel.PreviewRenderProfile(
            _appearancePreview.Current.TerminalRenderProfile);
        viewModel.RecoveryStateChanged += OnRecoveryStateChanged;
        return viewModel;
    }

    private QuickTerminalWindow GetOrCreateWindow()
    {
        if (_quickWindow is not null)
        {
            return _quickWindow;
        }

        _quickWindow = new QuickTerminalWindow
        {
            DataContext = _viewModel,
        };
        _quickWindow.ApplySettings(_settings, _hostPreferences);
        _quickWindow.DismissRequested += OnDismissRequested;
        _quickWindow.AgentSettingsRequested += OnAgentSettingsRequested;
        _quickWindow.NewConnectionRequested += OnNewConnectionRequested;
        _quickWindow.HeightResizeCompleted += OnHeightResizeCompleted;
        _quickWindow.Activated += OnQuickWindowActivated;
        _quickWindow.Deactivated += OnQuickWindowDeactivated;
        _quickWindow.Closed += OnQuickWindowClosed;
        return _quickWindow;
    }

    private void PositionAtTopOfWorkingArea(QuickTerminalWindow window)
    {
        var mainWindow = _mainWindow
            ?? throw new InvalidOperationException("The Asura main window is unavailable.");
        var mainWindowScreen = mainWindow.Screens.ScreenFromWindow(mainWindow);
        var activeWindowBounds = _settings.MonitorPolicy ==
            QuickTerminalMonitorPolicy.ActiveWindow
                ? _activeWindowBounds.TryGetBounds()
                : null;
        var activeWindowScreen = activeWindowBounds is { } bounds
            ? mainWindow.Screens.ScreenFromBounds(bounds)
            : null;
        var screen = QuickTerminalScreenResolver.Resolve(
            mainWindowScreen,
            mainWindow.Screens.Primary,
            activeWindowScreen,
            _settings.MonitorPolicy)
          ?? throw new InvalidOperationException("No desktop screen is available for Quick Terminal.");
        var workingArea = screen.WorkingArea;
        var scale = screen.Scaling;
        var availableHeight = workingArea.Height / scale;
        _availableHeight = availableHeight;
        var maximumHeight = availableHeight * QuickTerminalSettings.MaximumHeightFraction;
        var minimumHeight = Math.Min(
            maximumHeight,
            Math.Max(
                QuickTerminalWindow.MinimumRevealHeight,
                availableHeight * QuickTerminalSettings.MinimumHeightFraction));
        window.Width = workingArea.Width / scale;
        window.MinHeight = minimumHeight;
        window.MaxHeight = maximumHeight;
        window.Height = Math.Clamp(
            Math.Round(availableHeight * _settings.HeightFraction),
            minimumHeight,
            maximumHeight);
        window.PlaceAt(workingArea.Position, scale);
    }

    private void ApplySettingsFromCatalog()
    {
        if (_disposed)
        {
            return;
        }

        var previousRestorePolicy = _settings.RestoreLastSession;
        var terminalDefinitionsChanged = _definitionTracker.Update(_catalog.Snapshot);
        if (terminalDefinitionsChanged
            && !_definitionTracker.LastChangeRequiresSessionReset)
        {
            _viewModel.ApplySavedRenderProfile(_definitionTracker.CurrentRenderProfile);
        }
        var storedSettings = _catalog.Snapshot.QuickTerminalSettings
            .OrderByDescending(item => item.Value.Id == QuickTerminalSettings.DefaultId)
            .ThenBy(item => item.Value.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var nextSettings = storedSettings?.Value ?? QuickTerminalSettings.Default;
        var nextRevision = storedSettings?.Revision ?? 0;
        var definitionChanged = _settings.Id != nextSettings.Id
            || _settingsRevision != nextRevision;
        _settings = nextSettings;
        _settingsRevision = nextRevision;

        if (definitionChanged
            || _configuredGesture != _settings.Hotkey
            || _registrationResult is null)
        {
            RegisterConfiguredHotkey();
        }
        else
        {
            PublishRegistration();
        }

        var shouldResetSession = QuickTerminalRuntimeRules
            .ShouldResetForDefinitionOrPolicyChange(
                terminalDefinitionsChanged
                    && _definitionTracker.LastChangeRequiresSessionReset,
                previousRestorePolicy,
                _settings.RestoreLastSession,
                IsVisible);
        if (_quickWindow is { } window)
        {
            window.ApplySettings(_settings, _hostPreferences);
            if (window.IsVisible)
            {
                var target = _transition.State == QuickTerminalVisibilityState.Hiding
                    ? 0
                    : 1;
                var wasTransitioning = _transition.State is
                    QuickTerminalVisibilityState.Showing
                    or QuickTerminalVisibilityState.Hiding;
                var progress = PauseTransition(window);
                PositionAtTopOfWorkingArea(window);
                window.ApplyBackdrop();
                window.SetRevealProgress(progress);
                if (wasTransitioning)
                {
                    StartTransition(window, progress, target);
                }
            }
        }

        if (shouldResetSession)
        {
            var reopenAfterReset = terminalDefinitionsChanged && IsVisible;
            ResetSession();
            if (reopenAfterReset)
            {
                Toggle();
            }
        }
    }

    private void RegisterConfiguredHotkey()
    {
        _configuredGesture = _settings.Hotkey;
        _registrationResult = _globalHotkey.Register(_settings.Hotkey);
        _activeGesture = _registrationResult is GlobalHotkeyRegistrationResult.Success
            ? _settings.Hotkey
            : null;

        if (_registrationResult is GlobalHotkeyRegistrationResult.Failure
            && _settings.Hotkey != DefaultGesture)
        {
            var fallback = _globalHotkey.Register(DefaultGesture);
            if (fallback is GlobalHotkeyRegistrationResult.Success)
            {
                _activeGesture = DefaultGesture;
            }
        }

        PublishRegistration();
    }

    private void PublishRegistration()
    {
        if (_registrationResult is null)
        {
            return;
        }

        _mainWindowViewModel.ApplyQuickTerminalRegistration(
            _settings.Hotkey,
            _activeGesture,
            _registrationResult);
    }

    private void CompleteHide(QuickTerminalWindow window)
    {
        _quickWindowIsActive = false;
        if (window.IsVisible)
        {
            window.Hide();
        }

        if (_restorePreviousApplicationAfterHide)
        {
            _restorePreviousApplicationAfterHide = false;
            _ = MacOsQuickTerminalFocus.TryRestoreFrontmostApplication();
        }

        window.PrepareReveal(0);
        if (QuickTerminalRuntimeRules.ShouldResetAfterHide(_settings.RestoreLastSession))
        {
            ResetSession();
        }
    }

    private void ResetSession()
    {
        ResetTransition();
        var previousViewModel = _viewModel;
        var previousRequests = previousViewModel.TerminalRequests;
        if (_quickWindow is { } window)
        {
            window.DismissRequested -= OnDismissRequested;
            window.AgentSettingsRequested -= OnAgentSettingsRequested;
            window.NewConnectionRequested -= OnNewConnectionRequested;
            window.HeightResizeCompleted -= OnHeightResizeCompleted;
            window.Activated -= OnQuickWindowActivated;
            window.Deactivated -= OnQuickWindowDeactivated;
            window.Closed -= OnQuickWindowClosed;
            window.ClosePermanently();
            _quickWindow = null;
        }

        previousViewModel.RecoveryStateChanged -= OnRecoveryStateChanged;
        previousViewModel.Dispose();
        _viewModel = CreateViewModel();
        PublishRegistration();
        QueueRecoverySnapshot();
        foreach (var previousRequest in previousRequests)
        {
            _ = CloseDiscardedSessionAsync(previousRequest.SessionId, previousViewModel.ClientId);
        }
    }

    private async Task CloseDiscardedSessionAsync(SessionId sessionId, ClientId clientId)
    {
        try
        {
            var result = await _mainWindowViewModel.SessionClient.CloseAsync(
                CloseScopeRequest.Session(sessionId, CloseDecision.Confirm),
                OperationContext.ForHuman(clientId),
                CancellationToken.None);
            if (result is HostResult<CloseScopeResult>.Failure)
            {
                SecretSafeDiagnosticProjection.WriteTrace(
                    "quick-terminal.discard-close.failed",
                    SecretSafeDiagnosticKind.Unexpected);
            }
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace(
                "quick-terminal.discarded-session.close-failed",
                exception);
        }
    }

    private void StartTransition(
        QuickTerminalWindow window,
        double from,
        double to)
    {
        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(to, 0, 1);
        CancelCompletionTimer();
        var generation = _transition.Begin(
            from,
            to,
            MotionEnabled ? _settings.AnimationDurationMilliseconds : 0,
            Environment.TickCount64);
        if (_transition.DurationMilliseconds <= 0)
        {
            CompleteTransition(generation);
            return;
        }

        var duration = TimeSpan.FromMilliseconds(_transition.DurationMilliseconds);
        window.AnimateReveal(from, to, duration);
        _animationCompletion = DispatcherTimer.RunOnce(
            () => CompleteTransition(generation),
            duration,
            DispatcherPriority.Render);
    }

    private double PauseTransition(QuickTerminalWindow? window)
    {
        var progress = _transition.Pause(Environment.TickCount64);
        CancelCompletionTimer();
        window?.SetRevealProgress(progress);
        return progress;
    }

    private void CancelTransition()
    {
        CancelCompletionTimer();
        _transition.Cancel();
    }

    private void ResetTransition()
    {
        CancelCompletionTimer();
        _transition.Reset();
    }

    private void CancelCompletionTimer()
    {
        _animationCompletion?.Dispose();
        _animationCompletion = null;
    }

    private void CompleteTransition(long generation)
    {
        if (!_transition.TryComplete(generation))
        {
            return;
        }

        CancelCompletionTimer();
        var window = _quickWindow;
        var destination = _transition.Progress;
        window?.SetRevealProgress(destination);
        if (window is null)
        {
            _transition.Reset();
            return;
        }

        if (_transition.State == QuickTerminalVisibilityState.Visible)
        {
            window.FocusTerminal();
            return;
        }

        CompleteHide(window);
    }

    private void CompleteTransitionImmediately()
    {
        if (_animationCompletion is null)
        {
            return;
        }

        CompleteTransition(_transition.Generation);
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(ApplySettingsFromCatalog);
    }

    private void OnAppearancePreviewChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(() =>
            _viewModel.PreviewRenderProfile(
                _appearancePreview.Current.TerminalRenderProfile));
    }

    private async Task RestoreOnStartupAsync()
    {
        await _startupState.Initialized;
        if (_disposed)
        {
            return;
        }

        // The catalog and its durable Quick Terminal settings are guaranteed
        // ready by the startup signal. Re-read them before deciding whether
        // the previous run may be restored.
        ApplySettingsFromCatalog();
        try
        {
            if (_settings.RestoreOnStart)
            {
                var result = await _sessionRestoreCoordinator.LoadLatestSessionAsync(
                    CancellationToken.None);
                var snapshot = result.IsSuccess
                    ? result.Value!
                        .Where(item => string.Equals(item.Key, QuickTerminalRecoveryCodec.SnapshotKey, StringComparison.Ordinal))
                        .OrderByDescending(item => item.UpdatedAt)
                        .FirstOrDefault()
                    : null;
                if (snapshot is not null
                    && QuickTerminalRecoveryCodec.TryDeserialize(snapshot, out var recovered))
                {
                    await _viewModel.RestoreAsync(recovered!);
                }
                else if (!result.IsSuccess)
                {
                    SecretSafeDiagnosticProjection.WriteTrace(
                        "quick-terminal.recovery-load.failed",
                        SecretSafeDiagnosticKind.Unexpected);
                }
            }
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace(
                "quick-terminal.startup-restore.failed",
                exception);
        }
        finally
        {
            _recoveryReady = true;
            QueueRecoverySnapshot();
        }
    }

    private void OnRecoveryStateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        QueueRecoverySnapshot();
    }

    private void QueueRecoverySnapshot()
    {
        if (!_recoveryReady || _disposed)
        {
            return;
        }

        var queued = _runtimeRecoveryWriter.Enqueue(
            QuickTerminalRecoveryCodec.SnapshotKey,
            QuickTerminalRecoveryCodec.SchemaVersion,
            QuickTerminalRecoveryCodec.Serialize(_viewModel));
        if (!queued.IsSuccess)
        {
            SecretSafeDiagnosticProjection.WriteTrace(
                "quick-terminal.recovery-save.failed",
                SecretSafeDiagnosticKind.Unexpected);
        }
    }

    private void OnHostAccessibilityPreferencesChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(ApplyHostAccessibilityPreferences);
    }

    private void ApplyHostAccessibilityPreferences()
    {
        if (_disposed)
        {
            return;
        }

        var next = _hostAccessibilityPreferences.Current;
        if (_hostPreferences == next)
        {
            return;
        }

        var mustFinishAnimation = !_hostPreferences.ReducedMotion && next.ReducedMotion;
        _hostPreferences = next;
        if (mustFinishAnimation)
        {
            CompleteTransitionImmediately();
        }

        if (_quickWindow is { } window)
        {
            window.ApplySettings(_settings, _hostPreferences);
            if (window.IsVisible)
            {
                window.ApplyBackdrop();
            }
        }
    }

    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && !_isShuttingDown)
            {
                Toggle();
            }
        });
    }

    private void OnDismissRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Hide();
    }

    private void OnAgentSettingsRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Hide(restorePreviousApplication: false);
        if (_mainWindow is not { } mainWindow)
        {
            return;
        }

        (mainWindow.DataContext as MainWindowViewModel)?.ShowSettings(SettingsPage.Agent);
        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }

        mainWindow.Activate();
    }

    private async void OnNewConnectionRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Hide(restorePreviousApplication: false);
        if (_mainWindow is not { } mainWindow)
        {
            return;
        }

        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }

        mainWindow.Activate();
        var connectionId = await mainWindow.ShowNewTerminalConnectionEditorAsync();
        if (connectionId is { } id && !_disposed)
        {
            await _viewModel.SelectConnectionAsync(id);
        }
    }

    private void OnHeightResizeCompleted(
        object? sender,
        QuickTerminalHeightChangedEventArgs e)
    {
        _ = sender;
        if (_availableHeight <= 0)
        {
            return;
        }

        var fraction = QuickTerminalPresentationPolicy.HeightFraction(
            e.Height,
            _availableHeight);
        fraction = Math.Round(fraction * 100, MidpointRounding.AwayFromZero) / 100;
        if (Math.Abs(fraction - _settings.HeightFraction) < 0.0005)
        {
            return;
        }

        _pendingHeightFraction = fraction;
        if (!_heightSaveRunning)
        {
            _ = SavePendingHeightAsync();
        }
    }

    private async Task SavePendingHeightAsync()
    {
        _heightSaveRunning = true;
        try
        {
            while (_pendingHeightFraction is { } heightFraction && !_disposed)
            {
                _pendingHeightFraction = null;
                var settings = CopySettingsWithHeight(_settings, heightFraction);
                var result = await _catalog.SaveQuickTerminalSettingsAsync(
                    settings,
                    _settingsRevision,
                    CancellationToken.None);
                if (!result.IsSuccess || result.Value is null)
                {
                    SecretSafeDiagnosticProjection.WriteTrace(
                        "quick-terminal.height-save.failed",
                        SecretSafeDiagnosticKind.Unexpected);
                    return;
                }

                _settings = result.Value.Value;
                _settingsRevision = result.Value.Revision;
            }
        }
        catch (Exception exception)
        {
            SecretSafeDiagnostics.WriteTrace(
                "quick-terminal.height-save.failed",
                exception);
        }
        finally
        {
            _heightSaveRunning = false;
        }
    }

    private static QuickTerminalSettings CopySettingsWithHeight(
        QuickTerminalSettings settings,
        double heightFraction) => new(
            settings.Id,
            settings.Name,
            settings.Hotkey,
            settings.MonitorPolicy,
            heightFraction,
            settings.Opacity,
            settings.AnimateSlide,
            settings.AnimationDurationMilliseconds,
            settings.ReduceMotion,
            settings.RestoreLastSession,
            settings.HideOnFocusLoss,
            settings.IsTranslucent,
            settings.RestoreOnStart);

    private void OnQuickWindowActivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _quickWindowIsActive = true;
        if (!_disposed && IsVisible)
        {
            _ = _globalHotkey.BeginEscapeCapture();
        }
    }

    private void OnQuickWindowDeactivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _quickWindowIsActive = false;
        _globalHotkey.EndEscapeCapture();
    }

    private void OnQuickWindowClosed(object? sender, EventArgs e)
    {
        _ = e;
        if (!ReferenceEquals(sender, _quickWindow))
        {
            return;
        }

        if (sender is not QuickTerminalWindow window)
        {
            return;
        }

        window.DismissRequested -= OnDismissRequested;
        window.AgentSettingsRequested -= OnAgentSettingsRequested;
        window.NewConnectionRequested -= OnNewConnectionRequested;
        window.HeightResizeCompleted -= OnHeightResizeCompleted;
        window.Activated -= OnQuickWindowActivated;
        window.Deactivated -= OnQuickWindowDeactivated;
        window.Closed -= OnQuickWindowClosed;
        ResetTransition();
        _quickWindow = null;
        _globalHotkey.EndEscapeCapture();
    }

    private void OnEscapePressed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && IsVisible)
            {
                var action = QuickTerminalRuntimeRules.ResolveEscape(
                    IsVisible,
                    _quickWindow?.TryCancelPendingInteraction() == true);
                if (action != QuickTerminalEscapeAction.Hide)
                {
                    return;
                }

                Hide();
            }
        });
    }

}
