using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using GhostShell.App;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.App.Views.Overlays;
using GhostShell.Application;
using GhostShell.Core;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;

namespace GhostShell.App.Views;

public sealed partial class MainWindow : Window
{
    public static readonly DirectProperty<MainWindow, double> TitleBarChromeHeightProperty =
        AvaloniaProperty.RegisterDirect<MainWindow, double>(
            nameof(TitleBarChromeHeight),
            window => window.TitleBarChromeHeight);

    public static readonly DirectProperty<MainWindow, Thickness>
        WindowTitleBarContentMarginProperty =
            AvaloniaProperty.RegisterDirect<MainWindow, Thickness>(
                nameof(WindowTitleBarContentMargin),
                window => window.WindowTitleBarContentMargin);

    private readonly CancellationTokenSource _lifetime = new();
    private ApplicationKeyController? _applicationKeyController;
    private ShellClipboard? _clipboardWriter;
    private ShellCloseCoordinator? _closeCoordinator;
    private ShellFocusNavigator? _focusNavigator;
    private LayoutDesignerInteractionController? _layoutDesignerInteractions;
    private CancellationTokenSource? _historyExportLifetime;
    private readonly IDefinitionBundleStore? _definitionBundleStore;
    private readonly IDefinitionCatalog? _definitionCatalog;
    private readonly WorkspaceDefinitionOccupancy? _workspaceDefinitionOccupancy;
    private readonly IDiagnosticsBundleExporter? _diagnosticsExporter;
    private readonly IDiagnosticsBundleRequestSource? _diagnosticsRequestSource;
    private readonly IDiagnosticsArtifactPresenter? _diagnosticsArtifactPresenter;
    private readonly IRecentSessionHistoryExporter? _recentSessionHistoryExporter;
    private readonly RecoveryDataControlViewModel? _recoveryDataControlViewModel;
    private readonly LocalArtifactControlViewModel? _localArtifactControlViewModel;
    private DefinitionBundleController? _definitionBundles;
    private RecentSessionHistoryExportController? _historyExport;
    private SettingsView? _settingsRoute;
    private CommandPaletteView? _commandPaletteOverlay;
    private LayoutDesignerView? _layoutDesignerOverlay;
    private NewPanelChooserView? _newPanelChooserOverlay;
    private WorkspaceEditorView? _workspaceDefinitionEditor;
    private bool _backingScaleReconciliationQueued;
    private IDisposable? _backingScaleSettledPass;
    private double _titleBarChromeHeight = 44;
    private Thickness _windowTitleBarContentMargin = new(10, 0);

    public double TitleBarChromeHeight
    {
        get => _titleBarChromeHeight;
        private set => SetAndRaise(
            TitleBarChromeHeightProperty,
            ref _titleBarChromeHeight,
            value);
    }

    public Thickness WindowTitleBarContentMargin
    {
        get => _windowTitleBarContentMargin;
        private set => SetAndRaise(
            WindowTitleBarContentMarginProperty,
            ref _windowTitleBarContentMargin,
            value);
    }

    public MainWindow()
    {
        InitializeComponent();
        _ = new WorkspaceRailDragController(this);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        // Tunneled so any input anywhere counts as activity for the idle
        // lock, before a control can mark it handled.
        AddHandler(KeyDownEvent, OnAnyActivity, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnAnyActivity, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnAnyActivity, RoutingStrategies.Tunnel);
        Activated += OnWindowActivated;
        ScalingChanged += OnWindowScalingChanged;
        // Asked for before the window is created, not after it is on screen.
        // The platform decides whether a window can be seen through when it
        // makes one; a hint arriving later is a request to change something
        // already built, which macOS simply declines.
        //
        // The platform's own material, on every platform.
        //
        // macOS was asked only to be see-through, with the blur applied
        // underneath by radius, because two blurs of the same backdrop
        // compound and the shell came out far blurrier than the Quick
        // Terminal. That worked for the blur and was wrong about the window:
        // a translucent fill over a bare window gives AppKit nothing to shape
        // the frame from, so it took the content to be a plain square, and
        // the shadow and edge it built from that square stood proud of the
        // rounded corners. That is the dark outline, and the reason it was
        // heaviest at the corners and barely there along the straight runs.
        //
        // A visual-effect view is what the platform expects to be handed. It
        // masks it to the window's shape and works the shadow out from it,
        // which is why every other window gets this right without asking.
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.Transparent,
        ];
    }

    private void OnAnyActivity(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        (DataContext as MainWindowViewModel)?.ApplicationSecurityEditor.NoteActivity();
    }

    public MainWindow(
        IDefinitionBundleStore definitionBundleStore,
        IDefinitionCatalog definitionCatalog,
        WorkspaceDefinitionOccupancy workspaceDefinitionOccupancy,
        IDiagnosticsBundleExporter diagnosticsExporter,
        IDiagnosticsBundleRequestSource diagnosticsRequestSource,
        IDiagnosticsArtifactPresenter diagnosticsArtifactPresenter,
        IRecentSessionHistoryExporter recentSessionHistoryExporter,
        RecoveryDataControlViewModel recoveryDataControlViewModel,
        LocalArtifactControlViewModel localArtifactControlViewModel,
        IScreenColorSampler screenColorSampler)
        : this()
    {
        _definitionBundleStore = definitionBundleStore;
        _definitionCatalog = definitionCatalog;
        _workspaceDefinitionOccupancy = workspaceDefinitionOccupancy;
        _diagnosticsExporter = diagnosticsExporter;
        _diagnosticsRequestSource = diagnosticsRequestSource;
        _diagnosticsArtifactPresenter = diagnosticsArtifactPresenter;
        _recentSessionHistoryExporter = recentSessionHistoryExporter;
        _recoveryDataControlViewModel = recoveryDataControlViewModel;
        _localArtifactControlViewModel = localArtifactControlViewModel;
        _screenColorSampler = screenColorSampler;
    }

    private readonly IScreenColorSampler? _screenColorSampler;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is not MainWindowViewModel)
        {
            e.Cancel = true;
            base.OnClosing(e);
            return;
        }

        if (!CloseCoordinator.IsWindowCloseApproved)
        {
            e.Cancel = true;
            if (!CloseCoordinator.IsWindowCloseInProgress)
            {
                _ = CloseCoordinator.RequestWindowCloseAsync();
            }
        }

        base.OnClosing(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        // Subscribed before the base raises Opened, not after. Startup restore
        // runs from that event, and when the work it waits on has already
        // finished it runs straight through — so the restored workspace
        // announced its accent while nothing was listening yet, and the shell
        // kept its own colour until the first switch.
        ViewModel.WorkspaceAccentChanged += OnWorkspaceAccentChanged;
        // Whether a notification leaves a mark depends on whether anyone was
        // looking, and the window is the only thing that can say.
        Deactivated += OnWindowDeactivated;
        base.OnOpened(e);
        // And whatever it announced before any of this, in case something else
        // ever gets in front of the subscription again. The accent that is
        // already being worn costs nothing to apply.
        OnWorkspaceAccentChanged(this, ViewModel.ActiveWorkspaceAccent);
        RefreshWindowChromeMetrics();
        Avalonia.Threading.Dispatcher.UIThread.Post(
            RefreshWindowChromeMetrics,
            Avalonia.Threading.DispatcherPriority.Loaded);
        ApplyWindowBackdrop();
        Screens.Changed += OnScreensChanged;
        QueueBackingScaleReconciliation();
    }

    /// <summary>
    /// Puts the platform's material behind the window so the translucent base
    /// surface has something to be translucent against.
    ///
    /// A host asking for reduced transparency gets none of it: the base
    /// surface is published opaque, and a material behind an opaque surface
    /// is only cost.
    /// </summary>
    /// <summary>
    /// Re-reads the stored backdrop and applies it. Called when the appearance
    /// is republished, so turning the blur down — or off — takes effect where
    /// you set it rather than at the next start.
    /// </summary>
    internal void RefreshWindowBackdrop() => ApplyWindowBackdrop();

    private void ApplyWindowBackdrop()
    {
        if (Avalonia.Application.Current is not App app)
        {
            return;
        }

        if (!app.WindowIsTranslucent)
        {
            // Declining is a decision, and it said nothing. A silent early
            // return is indistinguishable from a backdrop that was asked for
            // and refused, which is two different things to go and fix.
            // Off is a setting as well as an accessibility preference, and an
            // opaque shell is the answer to both.
            Background = null;
            return;
        }

        RequestBackdrop();
        // And again once the window is really on screen. The native call needs a
        // window number the platform only issues then, and asking too early
        // fails quietly — which is what left the shell opaque with no sign of
        // why.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            RequestBackdrop,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void RequestBackdrop()
    {
        // The base surface has to reach the top edge; the material behind it
        // does the rest.
        var density = (Avalonia.Application.Current as App)?.WindowDensity
            ?? InterfaceDensity.Cozy;
        var titleBar = MacOsWindowTitleBar.TryLetTheBaseSurfaceRunToTheTop(
            this,
            // A kind per setting, because this desktop decides both the band's
            // height and the window's corner by it, and a density that changes
            // neither is a density that stops at the frame.
            //
            // The tightest setting asked for the compact toolbar for a while:
            // the title-bar kind's 16pt is the sharpest corner here, a surface
            // inside keeps that less the gap it stands off by, and the gap took
            // most of it. The gap is six now rather than sixteen, so what is
            // left is ten — a corner, not a square — and the setting can have
            // its own frame back.
            density switch
            {
                InterfaceDensity.Compact => MacOsWindowKind.TitleBarOnly,
                InterfaceDensity.Comfortable => MacOsWindowKind.Toolbar,
                _ => MacOsWindowKind.CompactToolbar,
            });
        // The platform's own material for a window's base. Avalonia pins the
        // view it creates to a fixed light one and lets its state follow the
        // window's, so the glass reads wrong for a dark shell and dulls
        // whenever focus moves away. Both are answered there.
        MacOsWindowMaterial.TrySit(this, MacOsMaterial.UnderWindowBackground);
        // Asking to be a different kind of window moves the standard buttons,
        // and the band is measured from where they are. Nothing else notices:
        // the decoration margin does not move when the toolbar style does, so
        // without this the band keeps whatever it measured first and the
        // density setting appears to change everything but the chrome.
        //
        // Deferred, because the platform has not applied the new kind yet at
        // the point it is asked for.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            RefreshWindowChromeMetrics,
            Avalonia.Threading.DispatcherPriority.Background);

        var negotiated = ActualTransparencyLevel;
        if (negotiated != WindowTransparencyLevel.None)
        {
            LetTheBackdropThrough();
        }

        // Only when something did not take. Every appearance republish comes
        // back through here, so a line each time is eight lines a start.
        var titleBarQuiet = !OperatingSystem.IsMacOS()
            || titleBar is MacOsTitleBarOutcome.Hidden;
        if (titleBarQuiet && negotiated != WindowTransparencyLevel.None)
        {
            return;
        }

        SecretSafeDiagnosticProjection.WriteStandardError(
            "appearance.backdrop.incomplete",
            SecretSafeDiagnosticKind.Unexpected);
    }

    private void LetTheBackdropThrough() => Background = Brushes.Transparent;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowDecorationMarginProperty
            || change.Property == WindowStateProperty)
        {
            RefreshWindowChromeMetrics();
        }

        if (change.Property == WindowStateProperty)
        {
            // Leaving full screen rebuilds the decorations, and Avalonia shows
            // the title-bar material again on the way out. Hiding it once at
            // startup is not enough.
            RequestBackdrop();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.IsWindowFocused = false;
    }

    private string? _pendingWorkspaceAccent;
    private bool _workspaceAccentRepaintQueued;

    /// <summary>
    /// Retints the shell for the workspace now in front — after that workspace
    /// is on screen, not before it.
    ///
    /// Republishing the appearance restyles every control bound to a token,
    /// which is the whole window. Doing it inline made it part of switching:
    /// the click was answered by a third of a second of restyling and only
    /// then by the workspace appearing. The colour is not what you asked for
    /// when you clicked a workspace, so it waits a frame and follows.
    ///
    /// Queued once and coalesced. Switching twice quickly should repaint the
    /// colour you landed on, not each colour you passed through.
    /// </summary>
    private void OnWorkspaceAccentChanged(object? sender, string? accent)
    {
        _ = sender;
        _pendingWorkspaceAccent = accent;
        if (_workspaceAccentRepaintQueued)
        {
            return;
        }

        _workspaceAccentRepaintQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                _workspaceAccentRepaintQueued = false;
                if (Avalonia.Application.Current is not App app)
                {
                    return;
                }

                // A stored accent that no longer parses is a definition problem,
                // not a reason to leave the shell wearing the last workspace's
                // colour.
                app.SetWorkspaceAccent(
                    _pendingWorkspaceAccent is { } pending
                    && RgbColor.TryParse(pending, out var color)
                        ? color
                        : null);
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.WorkspaceAccentChanged -= OnWorkspaceAccentChanged;
            viewModel.ReleaseAppearanceEditing();
        }

        Deactivated -= OnWindowDeactivated;
        ScalingChanged -= OnWindowScalingChanged;
        Screens.Changed -= OnScreensChanged;
        _backingScaleSettledPass?.Dispose();
        _backingScaleSettledPass = null;

        _applicationKeyController?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private MainWindowViewModel ViewModel => DataContext as MainWindowViewModel
        ?? throw new InvalidOperationException("The main window view model is unavailable.");

    private ApplicationKeyController ApplicationKeys =>
        _applicationKeyController ??= new ApplicationKeyController(
            new ApplicationKeyPresentation(
                ExecuteCommandAsync,
                ViewModel.ShowApplicationKeySequenceHint,
                ViewModel.ClearApplicationKeySequenceHint,
                ViewModel.SetError),
            _lifetime.Token);

    private ShellFocusNavigator FocusNavigator => _focusNavigator ??= new(
        this,
        ViewModel,
        () => SettingsRoute,
        () => LayoutDesignerOverlay,
        () => _workspaceDefinitionEditor,
        _lifetime.Token);

    private ShellCloseCoordinator CloseCoordinator => _closeCoordinator ??= new(
        ViewModel,
        ShellClosePresentation.ForWindow(this, FocusNavigator),
        _lifetime.Token);

    private ShellClipboard ClipboardWriter => _clipboardWriter ??= new(
        new ShellClipboardPresentation(text =>
            Clipboard?.SetTextAsync(text) ?? Task.CompletedTask),
        _lifetime.Token);

    private LayoutDesignerInteractionController LayoutDesignerInteractions =>
        _layoutDesignerInteractions ??= new(ViewModel);

    private CommandPaletteView CommandPaletteOverlay => _commandPaletteOverlay
        ?? throw new InvalidOperationException(
            "The command palette overlay has not been opened.");

    private LayoutDesignerView LayoutDesignerOverlay => _layoutDesignerOverlay
        ?? throw new InvalidOperationException(
            "The layout designer overlay has not been opened.");

    private NewPanelChooserView NewPanelChooserOverlay => _newPanelChooserOverlay
        ?? throw new InvalidOperationException(
            "The new panel chooser overlay has not been opened.");

    private T MaterializeRoute<T>(string templateKey, string hostName)
        where T : Control
    {
        var host = this.FindControl<ContentControl>(hostName)
            ?? throw new InvalidOperationException(
                $"The lazy route host '{hostName}' is unavailable.");
        if (host.Content is T existing)
        {
            return existing;
        }

        if (!Resources.TryGetResource(templateKey, ActualThemeVariant, out var resource)
            || resource is not IDataTemplate template
            || template.Build(ViewModel) is not T route)
        {
            throw new InvalidOperationException(
                $"The lazy route template '{templateKey}' is unavailable.");
        }

        host.Content = route;
        return route;
    }

    private void EnsureCommandPaletteOverlay() =>
        _commandPaletteOverlay ??= MaterializeRoute<CommandPaletteView>(
            "CommandPaletteOverlayTemplate",
            "CommandPaletteOverlayHost");

    private void EnsureNewPanelChooserOverlay() =>
        _newPanelChooserOverlay ??= MaterializeRoute<NewPanelChooserView>(
            "NewPanelOverlayTemplate",
            "NewPanelOverlayHost");

    private void EnsureLayoutDesignerOverlay() =>
        _layoutDesignerOverlay ??= MaterializeRoute<LayoutDesignerView>(
            "LayoutDesignerOverlayTemplate",
            "LayoutDesignerOverlayHost");

    private void EnsureDefinitionEditorOverlay()
    {
        if (_workspaceDefinitionEditor is not null)
        {
            return;
        }

        var card = MaterializeRoute<SurfaceCard>(
            "DefinitionEditorOverlayTemplate",
            "DefinitionEditorOverlayHost");
        _workspaceDefinitionEditor = card
            .GetLogicalDescendants()
            .OfType<WorkspaceEditorView>()
            .SingleOrDefault()
            ?? throw new InvalidOperationException(
                "The workspace definition editor is unavailable.");
    }

    public void ShowCommandPalette()
    {
        EnsureCommandPaletteOverlay();
        ViewModel.ShowOverlay(ShellOverlay.CommandPalette);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!ViewModel.IsCommandPaletteVisible)
            {
                return;
            }

            CommandPaletteOverlay.FocusSearch();
            ViewModel.SelectFirstAvailableLauncherSearchResult();
        });
    }

    /// <summary>
    /// Opens a tab that asks what to open.
    ///
    /// This was a modal over the whole window. A tab is the same question asked
    /// where the answer will land: it can be left open while something else is
    /// looked at, and it closes like any other tab rather than needing to be
    /// dismissed before the shell can be used again.
    /// </summary>
    public async Task ShowNewItemLauncherAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (!ViewModel.HasRuntimeWorkspace
            && ViewModel.Workspaces.FirstOrDefault() is { } workspace)
        {
            await OpenRuntimeWorkspaceAsync(token =>
                ViewModel.OpenWorkspaceAsync(workspace.Id, token));
        }

        ViewModel.ShowWorkspace();
        if (await ViewModel.AddLauncherTabAsync(_lifetime.Token))
        {
            FocusActivePanel();
        }
    }

    /// <summary>
    /// Opens a tab that asks what to open. There was a screen for this, reached
    /// by leaving whatever was on it; it is a tab now, so nothing has to be left.
    /// </summary>
    public async Task NavigateToLauncherAsync() => await ShowNewItemLauncherAsync();


    public async Task ShowNewPanelChooserAsync()
    {
        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        if (ViewModel.RuntimeWorkspace?.ActiveTab is null)
        {
            ViewModel.SetError("Open a workspace tab before adding a panel.");
            return;
        }

        ViewModel.ShowWorkspace();
        if (!ViewModel.IsWorkspaceVisible)
        {
            return;
        }

        EnsureNewPanelChooserOverlay();
        ViewModel.ShowOverlay(ShellOverlay.NewPanel);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel.IsNewPanelVisible)
            {
                NewPanelChooserOverlay.FocusInitialAction();
            }
        });
    }

    public async Task ShowLayoutDesignerAsync()
    {
        if (ViewModel.IsLayoutDesignerVisible)
        {
            FocusNavigator.FocusLayoutDesignerName();
            return;
        }

        if (ViewModel.HasOverlay && !await TryCloseOverlayAsync())
        {
            return;
        }

        EnsureLayoutDesignerOverlay();
        ViewModel.BeginCreateLayout();
        FocusNavigator.FocusLayoutDesignerName();
    }


    internal static bool IsExactGlobalGesture(
        Key actualKey,
        AvaloniaKeyModifiers actualModifiers,
        Key expectedKey,
        AvaloniaKeyModifiers commandModifier) =>
        actualKey == expectedKey && actualModifiers == commandModifier;

    private async void OnShowLauncherClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            await NavigateToLauncherAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnHistorySearchKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Enter
            && ViewModel.SelectedHistorySession is { CanOpen: true } selected)
        {
            e.Handled = true;
            await OpenRuntimeWorkspaceAsync(token =>
                ViewModel.OpenRecentSessionAsync(selected, token));
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (!string.IsNullOrEmpty(ViewModel.HistorySearchQuery))
        {
            ViewModel.HistorySearchQuery = string.Empty;
            return;
        }

        ViewModel.HistorySearchQuery = string.Empty;
    }

    private void OnShowSettingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateToSettings();
    }

    private void OnShowAgentSettingsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.RuntimeWorkspace is { } runtime
            && ViewModel.TryGetWorkspaceDefinitionId(runtime.Id, out var workspaceId))
        {
            EnsureDefinitionEditorOverlay();
            ViewModel.BeginEditWorkspace(workspaceId);
            FocusNavigator.FocusDefinitionEditor();
            return;
        }

        NavigateToSettings(SettingsPage.Agent);
    }

    private async void OnExportDefinitionsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_definitionBundles is null)
        {
            ViewModel.SetError("Definition portability is unavailable in this host.");
            return;
        }

        ViewModel.ClearError();
        var result = await _definitionBundles.ExportAsync(_lifetime.Token);
        if (result.IsSuccess)
        {
            var receipt = result.Value!;
            ViewModel.SetDefinitionBundleStatus(
                $"Exported {receipt.DefinitionCount} definitions to {Path.GetFileName(receipt.Path)}."
                + (receipt.Warning is { } warning ? " " + warning : string.Empty));
        }
        else if (result.Error!.Code != DefinitionStoreErrorCode.Cancelled)
        {
            ViewModel.SetError(result.Error.Message);
        }
    }

    private async void OnImportDefinitionsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_definitionBundles is null)
        {
            ViewModel.SetError("Definition portability is unavailable in this host.");
            return;
        }

        var mode = await new DefinitionImportModeDialog()
            .ShowDialog<DefinitionImportMode?>(this);
        if (mode is null)
        {
            return;
        }

        ViewModel.ClearError();
        var preflight = await _definitionBundles.PreflightImportAsync(
            mode.Value,
            _lifetime.Token);
        if (!preflight.IsSuccess)
        {
            if (preflight.Error!.Code != DefinitionStoreErrorCode.Cancelled)
            {
                ViewModel.SetError(preflight.Error.Message);
            }

            return;
        }

        var plan = preflight.Value!;
        var confirmed = await new DefinitionImportPreflightDialog(plan)
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        var applied = await _definitionBundles.ConfirmAndApplyImportAsync(
            plan,
            _lifetime.Token,
            plan.RequiresExecutionReview ? plan.AcknowledgeExecutionReview() : null);
        if (!applied.IsSuccess)
        {
            if (applied.Error!.Code != DefinitionStoreErrorCode.Cancelled)
            {
                ViewModel.SetError(applied.Error.Message);
            }

            return;
        }

        var receipt = applied.Value!;
        ViewModel.SetDefinitionBundleStatus(
            receipt.CatalogReloaded
                ? $"Imported {receipt.Inserted} new and replaced {receipt.Replaced} definitions."
                : receipt.WorkspacesRemainUnavailable
                    ? $"Imported {receipt.Inserted} new and replaced {receipt.Replaced} definitions, "
                        + "but the catalog refresh failed. Imported workspaces remain unavailable "
                        + "until a catalog refresh succeeds or GhostShell restarts."
                : $"Imported {receipt.Inserted} new and replaced {receipt.Replaced} definitions, but the catalog refresh failed.");
        if (!receipt.CatalogReloaded)
        {
            ViewModel.SetError(receipt.ReloadError!.Message);
        }

        if (ViewModel.Onboarding is { } onboarding)
        {
            await onboarding.RefreshAsync(_lifetime.Token);
        }
    }

    private async void OnRetryOnboardingClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.Onboarding is { } onboarding)
        {
            await onboarding.RefreshAsync(_lifetime.Token);
        }
    }

    private async void OnFinishOnboardingClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.Onboarding is { } onboarding)
        {
            await onboarding.CompleteAsync(_lifetime.Token);
        }
    }

    private void OnReviewOnboardingClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.Onboarding?.ShowReview();
        _ = NavigateToLauncherAsync();
    }

    private void OnReviewHistoryPrivacyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NavigateToSettings();
        SetSettingsPage(SettingsPage.Secrets);
    }


    private void OnShowCommandPaletteClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowCommandPalette();
    }

    private async void OnShowNewItemClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            await ShowNewItemLauncherAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnShowNewPanelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowNewPanelChooserAsync();
    }

    private async void OnShowLayoutDesignerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowLayoutDesignerAsync();
    }

    private async void OnCloseOverlayClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await TryCloseOverlayAsync();
    }

    private Task<bool> TryCloseOverlayAsync() =>
        CloseCoordinator.TryCloseOverlayAsync();

    private async void OnOpenWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherWorkspaceViewModel workspace })
        {
            await OpenRuntimeWorkspaceAsync(token =>
                ViewModel.OpenWorkspaceAsync(workspace.Id, token));
        }
    }

    private async void OnSaveWorkspaceLayoutClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherWorkspaceViewModel workspace })
        {
            await ViewModel.SaveWorkspaceLayoutAsync(workspace.Id, _lifetime.Token);
        }
    }

    /// <summary>
    /// Ends one workspace from the rail, and nothing else.
    ///
    /// Scoped to the workspace rather than the window: the window holds the
    /// others, and they keep running. The same confirmation a tab close uses
    /// stands in front of it, because the sessions being ended are just as
    /// live — the difference is only how many of them there are.
    /// </summary>
    private async void OnCloseWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: LauncherWorkspaceViewModel workspace }
            || !workspace.CanClose
            || ViewModel.OpenWorkspaceInstance(workspace.Id) is not { } instanceId)
        {
            return;
        }

        var runtime = ViewModel.OpenWorkspaces.FirstOrDefault(candidate =>
            candidate.Id == instanceId);
        if (runtime is not null
            && !await ConfirmDiscardDatabaseChangesAsync(runtime.Tabs.SelectMany(tab =>
                tab.Panels)))
        {
            return;
        }

        if (await RunCloseFlowAsync((decision, cancellationToken) =>
                ViewModel.CloseWorkspaceAsync(instanceId, decision, cancellationToken)))
        {
            ViewModel.RemoveRuntimeWorkspace(instanceId);
            FocusCurrentRoute();
        }
    }

    private async void OnOpenConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherConnectionViewModel connection })
        {
            await LaunchSavedConnectionAsync(connection);
        }
    }

    private Task LaunchSavedConnectionAsync(LauncherConnectionViewModel connection) =>
        connection.Family switch
        {
            SavedConnectionFamily.Files => LaunchTargetAsync(token =>
                ViewModel.LaunchFileProviderAsync(
                    new FileProviderProfileId(connection.TargetId),
                    token)),
            SavedConnectionFamily.Database => LaunchTargetAsync(token =>
                ViewModel.LaunchSavedDatabaseAsync(
                    new DatabaseConnectionProfileId(connection.TargetId),
                    token)),
            _ => LaunchConnectionTargetAsync(connection.Id),
        };

    private Task LaunchConnectionTargetAsync(ConnectionId connectionId) =>
        LaunchTargetAsync(token => ViewModel.LaunchConnectionAsync(connectionId, token));

    private async Task LaunchTargetAsync(Func<CancellationToken, Task<bool>> launch)
    {
        try
        {
            if (await launch(_lifetime.Token)
                && ViewModel.Overlay == ShellOverlay.None
                && ViewModel.Route == ShellRoute.Workspace)
            {
                FocusActivePanel();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnNewLocalTerminalClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewTerminalAsync();
    }

    private async void OnNewFileViewerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewAdapterTabAsync(PanelKind.FileViewer);
    }

    private async void OnNewBrowserClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewAdapterTabAsync(PanelKind.Browser);
    }

    private async void OnNewStatisticsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewAdapterTabAsync(PanelKind.Statistics);
    }

    private async void OnNewProcessMonitorClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewAdapterTabAsync(PanelKind.ProcessMonitor);
    }

    private async void OnNewDatabaseClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewAdapterTabAsync(PanelKind.DatabaseViewer);
    }

    private async void OnNewDockerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewAdapterTabAsync(PanelKind.Docker);
    }

    private async void OnNewGitClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RequestNewAdapterTabAsync(PanelKind.Git);
    }

    private async void OnAddConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ShowConnectionEditorAsync(null);
    }

    private async void OnEditConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherConnectionViewModel connection })
        {
            await ShowConnectionEditorAsync(connection);
        }
    }

    /// <summary>
    /// The launcher row names a target rather than carrying the card the editor
    /// wants, so the card is looked up here. A row whose target has since been
    /// deleted simply has nothing to edit.
    /// </summary>
    private LauncherConnectionViewModel? FindSavedConnection(
        SavedConnectionShortcutViewModel shortcut) =>
        shortcut.Target is PanelConnectionOptionViewModel.Target.Connection target
            ? ViewModel.Connections.FirstOrDefault(item => item.Id == target.Id)
            : null;

    private async void OnEditSavedConnectionRequested(
        object? sender,
        SavedConnectionShortcutViewModel shortcut)
    {
        _ = sender;
        if (FindSavedConnection(shortcut) is { } connection)
        {
            await ShowConnectionEditorAsync(connection);
        }
    }

    private async void OnDeleteSavedConnectionRequested(
        object? sender,
        SavedConnectionShortcutViewModel shortcut)
    {
        if (FindSavedConnection(shortcut) is { } connection)
        {
            await DeleteSavedConnectionAsync(connection);
        }
    }

    private async void OnDeleteConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherConnectionViewModel connection })
        {
            await DeleteSavedConnectionAsync(connection);
        }
    }

    private async Task DeleteSavedConnectionAsync(LauncherConnectionViewModel connection)
    {
        var noun = connection.Family switch
        {
            SavedConnectionFamily.Files => "file connection",
            SavedConnectionFamily.Database => "database connection",
            _ => "connection",
        };
        var confirmed = await Confirmations.DefinitionDelete(noun, connection.Name)
            .ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        var kind = connection.Family switch
        {
            SavedConnectionFamily.Files => FileProviderProfile.Kind,
            SavedConnectionFamily.Database => DatabaseConnectionProfile.Kind,
            _ => ConnectionProfile.Kind,
        };
        _ = await ViewModel.DeleteAsync(
            new DefinitionKey(kind, connection.TargetId),
            connection.Revision,
            _lifetime.Token);
    }

    private async Task ShowConnectionEditorAsync(LauncherConnectionViewModel? existing)
    {
        try
        {
            ViewModel.CloseOverlay();
            // The files form offers only secrets already in the vault, so the
            // vault listing must be current before the editor is built.
            await ViewModel.RefreshSecretsAsync(_lifetime.Token);
            var editor = existing?.Family switch
            {
                SavedConnectionFamily.Terminal => ViewModel.CreateUnifiedConnectionEditor(
                    SavedConnectionFamily.Terminal,
                    terminalConnectionId: existing.Id),
                SavedConnectionFamily.Files => ViewModel.CreateUnifiedConnectionEditor(
                    SavedConnectionFamily.Files,
                    fileProfileId: new FileProviderProfileId(existing.TargetId),
                    initialFamily: SavedConnectionFamily.Files),
                SavedConnectionFamily.Database => ViewModel.CreateUnifiedConnectionEditor(
                    SavedConnectionFamily.Database,
                    databaseProfileId: new DatabaseConnectionProfileId(existing.TargetId),
                    initialFamily: SavedConnectionFamily.Database),
                _ => ViewModel.CreateUnifiedConnectionEditor(),
            };
            var result = await new ConnectionEditorDialog(editor)
                .ShowDialog<UnifiedConnectionEditorResult?>(this);
            if (result is not null)
            {
                await ApplyConnectionEditorResultAsync(result);
            }
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    public async Task<ConnectionId?> ShowNewTerminalConnectionEditorAsync()
    {
        try
        {
            ViewModel.CloseOverlay();
            await ViewModel.RefreshSecretsAsync(_lifetime.Token);
            var editor = ViewModel.CreateUnifiedConnectionEditor(
                SavedConnectionFamily.Terminal);
            var result = await new ConnectionEditorDialog(editor)
                .ShowDialog<UnifiedConnectionEditorResult?>(this);
            if (result is not UnifiedConnectionEditorResult.Terminal terminal)
            {
                return null;
            }

            var saved = await ViewModel.SaveConnectionAsync(
                terminal.Request,
                _lifetime.Token);
            return saved.IsSuccess ? terminal.Request.Profile.Id : null;
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
            return null;
        }
    }

    private async Task ApplyConnectionEditorResultAsync(UnifiedConnectionEditorResult result)
    {
        switch (result)
        {
            case UnifiedConnectionEditorResult.Terminal terminal:
                _ = await ViewModel.SaveConnectionAsync(terminal.Request, _lifetime.Token);
                break;
            case UnifiedConnectionEditorResult.Files files:
                _ = await ViewModel.SaveFileProviderProfileAsync(files.Request, _lifetime.Token);
                break;
            case UnifiedConnectionEditorResult.Database database:
                _ = await ViewModel.SaveDatabaseConnectionAsync(
                    database.Request.ExistingId,
                    database.Request.Name,
                    database.Request.DriverId,
                    database.Request.Details,
                    database.Request.StorePassword,
                    database.Request.TunnelConnectionId,
                    database.Request.InlineTunnel,
                    _lifetime.Token,
                    database.Request.DraftId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result), result, null);
        }
    }

    private async void OnOpenScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherScreenViewModel screen })
        {
            await LaunchScreenTargetAsync(screen.Id);
        }
    }

    private async Task LaunchScreenTargetAsync(ScreenId screenId)
    {
        try
        {
            if (await ViewModel.LaunchScreenAsync(screenId, _lifetime.Token)
                && ViewModel.Overlay == ShellOverlay.None
                && ViewModel.Route == ShellRoute.Workspace)
            {
                FocusActivePanel();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async void OnOpenRecentSessionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: RecentSessionHistoryItemViewModel recentSession })
        {
            await OpenRuntimeWorkspaceAsync(token =>
                ViewModel.OpenRecentSessionAsync(recentSession, token));
        }
    }

    private async void OnClearRecentSessionsClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!await Confirmations.HistoryClear().ShowDialog<bool>(this))
        {
            return;
        }

        // Captured at confirmation time, as the old dialog did on its confirm
        // click: rows added while the dialog was open are included.
        var cutoff = ViewModel.CaptureRecentSessionClearCutoff();
        _ = await ViewModel.ClearRecentSessionsAsync(cutoff, _lifetime.Token);
    }

    private async void OnOpenSelectedHistorySessionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.SelectedHistorySession is { CanOpen: true } recentSession)
        {
            await OpenRuntimeWorkspaceAsync(token =>
                ViewModel.OpenRecentSessionAsync(recentSession, token));
        }
    }

    private async void OnResetRecentSessionHistoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!ViewModel.CanResetRecentSessionHistory
            || !await Confirmations.HistoryReset().ShowDialog<bool>(this))
        {
            return;
        }

        _ = await ViewModel.ResetUnreadableRecentSessionsAsync(_lifetime.Token);
    }

    private async void OnRetryRecentSessionHistoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _ = await ViewModel.RetryRecentSessionHistoryAsync(_lifetime.Token);
    }

    private async void OnSaveHistoryRetentionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.SelectedHistoryRetentionOption is not { } selected)
        {
            return;
        }

        if (ViewModel.RequiresHistoryRetentionConfirmation
            && !await Confirmations.HistoryRetentionChange(selected).ShowDialog<bool>(this))
        {
            return;
        }

        _ = await ViewModel.SaveHistoryRetentionAsync(_lifetime.Token);
    }

    private async void OnExportAllHistoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ExportHistoryAsync(HistoryExportScope.AllRetained);
    }

    private async void OnExportFilteredHistoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await ExportHistoryAsync(HistoryExportScope.CurrentResults);
    }

    private async Task ExportHistoryAsync(HistoryExportScope scope)
    {
        if (_historyExport is null)
        {
            ViewModel.SetHistoryExportStatus("Session-history export is unavailable.");
            return;
        }

        if (!ViewModel.TryBeginHistoryExport(scope))
        {
            return;
        }

        using var exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        _historyExportLifetime = exportCancellation;
        var finalStatus = "Session-history export failed. Please choose a different destination and retry.";
        try
        {
            var snapshot = ViewModel.CaptureHistoryExportSnapshot();
            if (snapshot.Count == 0)
            {
                finalStatus = "There are no matching metadata records to export.";
                return;
            }

            var result = await _historyExport.ExportAsync(
                snapshot,
                exportCancellation.Token);
            if (!result.IsSuccess)
            {
                finalStatus = result.Error!.Code == RecentSessionHistoryExportErrorCode.Cancelled
                    ? "Session-history export cancelled."
                    : $"{result.Error.Message} Choose a different destination and retry.";
                return;
            }

            finalStatus =
                $"Exported {result.Value!.Export.RecordCount:N0} metadata-only records to {Path.GetFileName(result.Value.Path)}.";
        }
        catch (OperationCanceledException) when (exportCancellation.IsCancellationRequested)
        {
            finalStatus = "Session-history export cancelled.";
        }
        catch (Exception)
        {
            finalStatus =
                "Session-history export failed unexpectedly. Choose a different destination and retry.";
        }
        finally
        {
            _historyExportLifetime = null;
            ViewModel.EndHistoryExport(finalStatus);
        }
    }

    private void OnCancelHistoryExportClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _historyExportLifetime?.Cancel();
    }

    private async void OnCreateScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            // Named in the editor itself; there is no longer a name box to carry
            // one in from.
            var editor = ViewModel.CreateNewSavedScreenEditor(string.Empty);
            var saved = await new SavedScreenEditorDialog(
                    editor,
                    ViewModel.SaveSavedScreenAsync)
                .ShowDialog<bool>(this);
            if (!saved)
            {
                return;
            }

            ViewModel.CloseOverlay();
            FocusCurrentRoute();
        }
        catch (InvalidOperationException exception)
        {
            ViewModel.SetError(exception.Message);
        }
    }

    private async void OnSaveLayoutDesignerClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var result = await ViewModel.SaveLayoutDesignerAsync(_lifetime.Token);
        if (result.IsSuccess)
        {
            ViewModel.DismissLayoutDesigner();
            FocusCurrentRoute();
        }
    }

    private async void OnEditLayoutClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: LayoutCardViewModel layout })
        {
            return;
        }

        if (ViewModel.LayoutDesignerEditor?.RequestCancel()
                == LayoutDesignerCancelDisposition.ConfirmDiscard
            && !await Confirmations.DiscardChanges().ShowDialog<bool>(this))
        {
            return;
        }

        ViewModel.DismissLayoutDesigner();
        EnsureLayoutDesignerOverlay();
        ViewModel.BeginEditLayout(layout.Id);
        FocusNavigator.FocusLayoutDesignerName();
    }

    private void OnLayoutSlotClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        LayoutDesignerInteractions.SelectSlot(sender);
    }

    private void OnLayoutSplitSlotRightClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        LayoutDesignerInteractions.SplitSlotRight(sender);
    }

    private void OnLayoutSplitSlotDownClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        LayoutDesignerInteractions.SplitSlotDown(sender);
    }

    private void OnLayoutAddSlotClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        LayoutDesignerInteractions.AddSlot();
    }

    private void OnLayoutRemoveSlotClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        LayoutDesignerInteractions.RemoveSlot(sender);
    }

    private void OnResetLayoutClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        LayoutDesignerInteractions.Reset();
    }

    private void OnCreateWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        EnsureDefinitionEditorOverlay();
        ViewModel.BeginCreateWorkspace();
        FocusNavigator.FocusDefinitionEditor();
    }

    private void OnEditWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherWorkspaceViewModel workspace })
        {
            EnsureDefinitionEditorOverlay();
            ViewModel.BeginEditWorkspace(workspace.Id);
            FocusNavigator.FocusDefinitionEditor();
        }
    }

    private async void OnEditScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherScreenViewModel screen })
        {
            try
            {
                var editor = ViewModel.CreateSavedScreenEditor(screen.Id);
                _ = await new SavedScreenEditorDialog(
                        editor,
                        ViewModel.SaveSavedScreenAsync)
                    .ShowDialog<bool>(this);
            }
            catch (InvalidOperationException exception)
            {
                ViewModel.SetError(exception.Message);
            }
        }
    }

    private async void OnSaveDefinitionEditClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.WorkspaceEditor is { } workspaceEditor)
        {
            try
            {
                _ = await SaveWorkspaceEditorFromWindowAsync(
                    workspaceEditor.CreateSaveRequest());
            }
            catch (InvalidOperationException exception)
            {
                ViewModel.SetError(exception.Message);
            }

            return;
        }

        _ = await ViewModel.SaveDefinitionEditAsync(_lifetime.Token);
    }

    private async void OnWorkspaceEditorSaveRequested(
        object? sender,
        WorkspaceEditorSaveRequestedEventArgs e)
    {
        _ = sender;
        var result = await SaveWorkspaceEditorFromWindowAsync(e.Request);
        if (result.IsSuccess)
        {
            FocusCurrentRoute();
        }
    }

    private async void OnRecreateWorkspaceIsolationRequested(
        object? sender,
        WorkspaceId workspaceId)
    {
        _ = sender;
        if (ViewModel.WorkspaceEditor is not { } editor)
        {
            return;
        }

        if (editor.IsDirty)
        {
            ViewModel.SetError("Save the workspace changes before recreating its environment.");
            return;
        }

        if (!await Confirmations.RecreateWorkspaceIsolation(editor.Name)
                .ShowDialog<bool>(this))
        {
            return;
        }

        ViewModel.DismissWorkspaceEditor();
        _ = await ViewModel.RecreateWorkspaceIsolationAsync(
            workspaceId,
            _lifetime.Token);
    }

    private async ValueTask<DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>>
        SaveWorkspaceEditorFromWindowAsync(WorkspaceEditorSaveRequest request)
    {
        if (!ViewModel.WorkspaceEditorConfigurationChangeRequiresRestart(request))
        {
            return await ViewModel.SaveWorkspaceEditorAsync(request, _lifetime.Token);
        }

        if (!await Confirmations.WorkspaceIsolationRestart(
                    request.Definition.Name,
                    ViewModel.WorkspaceEditorImageChangeRebuildsIsolate(request))
                .ShowDialog<bool>(this))
        {
            return DefinitionStoreResult<StoredDefinition<WorkspaceDefinition>>.Failure(
                new DefinitionStoreError(
                    DefinitionStoreErrorCode.Cancelled,
                    "Workspace isolation change was cancelled."));
        }

        return await ViewModel.SaveWorkspaceEditorRestartingAsync(
            request,
            _lifetime.Token);
    }

    /// <summary>
    /// The rail asked for another workspace. Switching carries the edits in
    /// progress with it when they are complete — refusing to move because
    /// something is unsaved would make the rail a list you cannot use — and only
    /// stops when the workspace could not be saved as it stands.
    /// </summary>
    private async void OnWorkspaceSelectionRequested(object? sender, WorkspaceId id)
    {
        _ = sender;
        if (!await CommitWorkspaceEditsBeforeSwitchAsync())
        {
            return;
        }

        ViewModel.BeginEditWorkspace(id);
        FocusNavigator.FocusDefinitionEditor();
    }

    private async void OnCreateWorkspaceRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (!await CommitWorkspaceEditsBeforeSwitchAsync())
        {
            return;
        }

        ViewModel.BeginCreateWorkspace();
        FocusNavigator.FocusDefinitionEditor();
    }

    private async Task<bool> CommitWorkspaceEditsBeforeSwitchAsync()
    {
        if (ViewModel.WorkspaceEditor is not { IsDirty: true } editor)
        {
            return true;
        }

        if (!editor.IsValid)
        {
            if (!await Confirmations.DiscardChanges(
                    "Discard workspace changes?",
                    "These changes cannot be saved yet. Discard them to switch workspaces, or keep editing.\n\n"
                    + editor.ValidationSummary).ShowDialog<bool>(this))
            {
                return false;
            }
            editor.Reset();
            return true;
        }

        return (await SaveWorkspaceEditorFromWindowAsync(
            editor.CreateSaveRequest())).IsSuccess;
    }

    private async void OnWorkspaceEditorCancelRequested(
        object? sender,
        WorkspaceEditorCancelRequestedEventArgs e)
    {
        _ = sender;
        if (e.Disposition == WorkspaceEditorCancelDisposition.ConfirmDiscard
            && !await Confirmations.DiscardChanges(
                    "Discard workspace changes?",
                    "The unsaved workspace order, tabs, panels, and startup settings will be lost.")
                .ShowDialog<bool>(this))
        {
            return;
        }

        ViewModel.DismissWorkspaceEditor();
        FocusCurrentRoute();
    }

    private async void OnDeleteWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherWorkspaceViewModel workspace })
        {
            var key = new DefinitionKey(WorkspaceDefinition.Kind, workspace.Id.Value);
            var isOpen = ViewModel.IsDefinitionOpen(key);
            var dialog = workspace.IsIsolated
                ? Confirmations.DefinitionDelete(
                    "Delete isolated workspace?",
                    isOpen
                        ? $"“{workspace.Name}” will be removed, while its running tabs and sessions remain alive until they close."
                        : $"“{workspace.Name}” will be permanently removed from this profile.",
                    "This does not delete the persistent isolate or its installed packages. Remove the isolate with the platform runtime until GhostSHELL adds reset and delete controls.",
                    "Delete workspace")
                : isOpen
                ? Confirmations.DefinitionDelete(
                    "Delete the open workspace definition?",
                    $"“{workspace.Name}” is currently open. Its running tabs and sessions will remain alive, but this saved workspace can no longer be reopened after they close.",
                    "Close this dialog if you want to keep the definition or save a replacement before deleting it.",
                    "Delete and keep running")
                : Confirmations.DefinitionDelete("workspace", workspace.Name);
            var confirmed = await dialog
                .ShowDialog<bool>(this);
            if (!confirmed)
            {
                return;
            }

            _ = await ViewModel.DeleteAsync(
                key,
                workspace.Revision,
                _lifetime.Token);
        }
    }

    private async void OnDeleteScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: LauncherScreenViewModel screen })
        {
            var confirmed = await Confirmations.DefinitionDelete("saved screen", screen.Name)
                .ShowDialog<bool>(this);
            if (!confirmed)
            {
                return;
            }

            var result = await ViewModel.DeleteSavedScreenAsync(
                new DefinitionKey(ScreenDefinition.Kind, screen.Id.Value),
                screen.Revision,
                _lifetime.Token);
            if (result.IsSuccess)
            {
                FocusNavigator.FocusSavedScreenUndo();
            }
        }
    }

    private async void OnUndoDeletedSavedScreenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var result = await ViewModel.UndoSavedScreenDeleteAsync(_lifetime.Token);
        if (result.IsSuccess)
        {
            FocusNavigator.FocusCurrentRoute();
        }
    }

    private void OnDismissSavedScreenDeleteUndoClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.DismissSavedScreenDeleteUndo();
        FocusNavigator.FocusCurrentRoute();
    }

    private void OnClearErrorClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.ClearError();
    }

    private async void OnLauncherSearchResultClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: LauncherSearchResultViewModel item }
            || !item.IsAvailable)
        {
            return;
        }

        await ExecuteLauncherSearchTargetAsync(item.Target);
    }

    private async Task ExecuteLauncherSearchTargetAsync(LauncherSearchTarget target)
    {
        switch (target)
        {
            case LauncherSearchTarget.CreatePanel createPanel:
                await ExecuteCreatePanelTargetAsync(createPanel.Kind);
                break;
            case LauncherSearchTarget.Command command:
                await ExecuteCommandPaletteCommandAsync(command);
                break;
            case LauncherSearchTarget.Connection connection:
                await LaunchConnectionTargetAsync(connection.Id);
                break;
            case LauncherSearchTarget.FileConnection fileConnection:
                await LaunchTargetAsync(token =>
                    ViewModel.LaunchFileProviderAsync(fileConnection.Id, token));
                break;
            case LauncherSearchTarget.DatabaseConnection databaseConnection:
                await LaunchTargetAsync(token =>
                    ViewModel.LaunchSavedDatabaseAsync(databaseConnection.Id, token));
                break;
            case LauncherSearchTarget.Screen screen:
                await LaunchScreenTargetAsync(screen.Id);
                break;
            case LauncherSearchTarget.Workspace workspace:
                await OpenRuntimeWorkspaceAsync(token =>
                    ViewModel.OpenWorkspaceAsync(workspace.Id, token));
                break;
            case LauncherSearchTarget.RecentSession recent:
                var recentSession = ViewModel.HistorySessions.FirstOrDefault(item =>
                    item.SessionId == recent.Id);
                if (recentSession is null)
                {
                    ViewModel.SetError("That recent session is no longer available.");
                    return;
                }

                await OpenRuntimeWorkspaceAsync(token =>
                    ViewModel.OpenRecentSessionAsync(recentSession, token));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private async Task ExecuteCreatePanelTargetAsync(PanelKind kind)
    {
        switch (kind)
        {
            case PanelKind.Terminal:
                await RequestNewTerminalAsync();
                break;
            case PanelKind.FileViewer:
                await RequestNewFileViewerAsync();
                break;
            case PanelKind.Browser:
                await RequestNewBrowserAsync();
                break;
            case PanelKind.Statistics:
                await RequestNewStatisticsAsync();
                break;
            case PanelKind.ProcessMonitor:
                await RequestNewProcessMonitorAsync();
                break;
            case PanelKind.DatabaseViewer:
                await RequestNewDatabaseAsync();
                break;
            case PanelKind.Docker:
                await RequestNewDockerAsync();
                break;
            case PanelKind.Git:
                await RequestNewGitAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private async Task ExecuteCommandPaletteCommandAsync(
        LauncherSearchTarget.Command command)
    {
        if (command.Id == BuiltInCommands.NewTab)
        {
            await ShowNewItemLauncherAsync();
            return;
        }

        ViewModel.CloseOverlay();
        await ExecuteCommandAsync(command.Id, command.Arguments);
    }

    public Task ExecuteCommandAsync(CommandId commandId) =>
        ShellCommands.ExecuteAsync(commandId, EmptyCommandArguments.Instance);

    private Task ExecuteCommandAsync(CommandBinding binding) =>
        ShellCommands.ExecuteAsync(binding.CommandId, binding.Arguments);

    private Task ExecuteCommandAsync(
        CommandId commandId,
        IReadOnlyDictionary<string, string> arguments) =>
        ShellCommands.ExecuteAsync(commandId, arguments);

    private async Task SendLiteralPrefixAsync()
    {
        var terminal = FindActiveTerminalHost();
        if (terminal is null)
        {
            ViewModel.SetError("The active panel cannot receive terminal input.");
            return;
        }

        await terminal.SendTextAsync("\u0002", _lifetime.Token);
    }

    private async void OnCommandSearchKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Escape)
        {
            _ = await TryCloseOverlayAsync();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            ViewModel.MoveLauncherSearchSelection(e.Key == Key.Down ? 1 : -1);
            CommandPaletteOverlay.ScrollSelectedResultIntoView();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (ViewModel.ConfirmLauncherSearchSelection() is { } target)
            {
                await ExecuteLauncherSearchTargetAsync(target);
            }

            e.Handled = true;
        }
    }

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (ViewModel.ApplicationSecurityEditor.IsLocked)
        {
            // Locked means locked: no shortcut may act behind the veil. The
            // keystrokes still reach the PIN box, which is text input, not a
            // shortcut.
            return;
        }

        if (!ViewModel.HasOverlay)
        {
            var replayTarget = FindActiveTerminalHost();
            ApplicationKeyReplay? replay = replayTarget is null
                ? null
                : replayTarget.ReplayApplicationKeyStrokesAsync;
            var profile = new ApplicationKeyProfileSnapshot(
                ViewModel.ActiveApplicationKeymap,
                ViewModel.ActiveApplicationKeymapRevision,
                ViewModel.ActiveApplicationKeymapName,
                ViewModel.ActiveCommandContexts);
            var handling = ApplicationKeys.Resolve(
                ApplicationKeyStrokeMapper.Map(e.Key, e.KeyModifiers, e.KeySymbol),
                profile);
            if (handling.Kind != ApplicationKeyResolutionKind.NotHandled)
            {
                // Routed input continues synchronously when an async handler
                // yields. Claim the shortcut before command execution or replay.
                e.Handled = handling.ShouldHandle;
                await ApplicationKeys.ApplyAsync(handling, profile, replay);
                return;
            }
        }
        else
        {
            ApplicationKeys.Reset();
        }

        if (e.Key == Key.Escape && ViewModel.HasOverlay)
        {
            e.Handled = true;
            _ = await TryCloseOverlayAsync();
            return;
        }

        if (e.Key == Key.Escape && ViewModel.ExitTerminalCopyMode())
        {
            FocusNavigator.FocusActivePanel();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsLayoutDesignerVisible)
        {
            return;
        }

        var commandModifier = OperatingSystem.IsMacOS()
            ? AvaloniaKeyModifiers.Meta
            : AvaloniaKeyModifiers.Control;
        if (IsExactGlobalGesture(e.Key, e.KeyModifiers, Key.K, commandModifier))
        {
            ShowCommandPalette();
            e.Handled = true;
        }
        else if (IsExactGlobalGesture(
            e.Key,
            e.KeyModifiers,
            Key.OemComma,
            commandModifier))
        {
            NavigateToSettings();
            e.Handled = true;
        }
        else if (IsExactGlobalGesture(e.Key, e.KeyModifiers, Key.T, commandModifier))
        {
            e.Handled = true;
            await RequestNewTerminalAsync();
        }
        else if (ViewModel.ActivePanel is TerminalRuntimePanelViewModel { IsCopyMode: true }
            && !IsTerminalCopyGesture(e))
        {
            // Local copy mode leaves mouse selection and scrolling available but
            // prevents ordinary key presses from mutating the live remote shell.
            e.Handled = true;
        }
    }

    private static bool IsTerminalCopyGesture(KeyEventArgs e) => OperatingSystem.IsMacOS()
        ? e.Key == Key.C && (e.KeyModifiers & AvaloniaKeyModifiers.Meta) != AvaloniaKeyModifiers.None : e.Key == Key.C
            && (e.KeyModifiers & AvaloniaKeyModifiers.Control) != AvaloniaKeyModifiers.None && (e.KeyModifiers & AvaloniaKeyModifiers.Shift) != AvaloniaKeyModifiers.None;

    private void FocusCurrentRoute() => FocusNavigator.FocusCurrentRoute();

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.IsWindowFocused = true;
        }

        RefreshWindowChromeMetrics();
        QueueBackingScaleReconciliation();
        _focusNavigator?.NotifyWindowActivated();
    }

    private void OnWindowScalingChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        QueueBackingScaleReconciliation();
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        QueueBackingScaleReconciliation();
        _backingScaleSettledPass?.Dispose();
        _backingScaleSettledPass = Avalonia.Threading.DispatcherTimer.RunOnce(
            () =>
            {
                _backingScaleSettledPass = null;
                QueueBackingScaleReconciliation();
            },
            TimeSpan.FromMilliseconds(750),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void QueueBackingScaleReconciliation()
    {
        if (_backingScaleReconciliationQueued)
        {
            return;
        }

        _backingScaleReconciliationQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    _ = MacOsWindowBackingScale.TryReconcile(this);
                }
                finally
                {
                    // The native callback raises ScalingChanged even when the
                    // value is unchanged. Keep the guard set until it returns
                    // so that reconciliation cannot recursively queue itself.
                    _backingScaleReconciliationQueued = false;
                }
            },
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// How much room the chrome leaves for the window's own controls.
    ///
    /// The platform still draws them, so the platform is still asked. Drawing
    /// them ourselves to a hand-computed inset lost the rounded corners, the
    /// resize edges and the correct button size along with the title bar, so
    /// the measurement comes back from the system.
    /// </summary>
    /// <summary>
    /// What the band takes off the height the platform's own number implies.
    ///
    /// The buttons do not sit where that number says: twice the reported centre
    /// comes out taller than the buttons are centred in, by the same amount at
    /// every window kind. Measured on screen, the space under them exceeded the
    /// space above by this much at compact, normal and comfortable alike.
    /// </summary>
    private const double ChromeBandTrim = 2;

    private void RefreshWindowChromeMetrics()
    {
        const double horizontalSpacing = 14;
        // Twice the standard buttons' own centre, so anything centred in this
        // band sits on the same axis they do. They move with the window's
        // corner — a rounder window puts them lower — and the shell has to
        // follow rather than pick a height and hope.
        var reportedHeight = MacOsWindowChromeMetrics.TryGetButtonCentreFromTop(this)
            is { } centre
            ? centre * 2
            : WindowDecorationMargin.Top;
        var bandHeight = double.IsFinite(reportedHeight) && reportedHeight > 0
            ? reportedHeight
            : 44;
        // Less what the reported centre overstates, which it does whatever kind
        // of window this is. Each kind puts the buttons somewhere else and the
        // band follows; the correction is the same one every time, because it
        // belongs to the measurement rather than to the height.
        TitleBarChromeHeight = Math.Max(1, bandHeight - ChromeBandTrim);

        if (OperatingSystem.IsMacOS())
        {
            var trafficLightRightEdge = WindowState == WindowState.FullScreen
                ? 0
                : MacOsWindowChromeMetrics.TryGetTrafficLightRightEdge(this)
                    ?? Math.Max(92, TitleBarChromeHeight * 2.25);
            // The trailing edge owes what the leading one does. The buttons sit
            // clear of the window's corner, and the corner is a setting now, so
            // whatever they are inset by is what the shell's own controls need
            // in the opposite corner — otherwise the band leans.
            var trailingInset =
                MacOsWindowChromeMetrics.TryGetButtonLeadingInset(this)
                ?? horizontalSpacing;
            WindowTitleBarContentMargin = new Thickness(
                trafficLightRightEdge + horizontalSpacing,
                0,
                trailingInset,
                0);
            return;
        }

        WindowTitleBarContentMargin = OperatingSystem.IsWindows()
            ? new Thickness(10, 0, 148, 0)
            : new Thickness(10, 0);
    }

    private async Task<bool> RunCloseFlowAsync(
        Func<CloseDecision, CancellationToken, ValueTask<HostResult<CloseScopeResult>>> close)
        => await CloseCoordinator.RunHostCloseAsync(close);

    private static class EmptyCommandArguments
    {
        public static IReadOnlyDictionary<string, string> Instance { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

internal enum NewTerminalTarget
{
    ExistingRuntimeWorkspace,
    DefaultConnectionWorkspace,
}
