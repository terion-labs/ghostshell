using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Dock.Controls.ProportionalStackPanel;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.App.Views;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

public sealed partial class App : Avalonia.Application
{
    private readonly MainWindowViewModel? _mainWindowViewModel;
    private readonly MainWindowViewModelFactory? _mainWindowViewModelFactory;
    private readonly ApplicationStartupState? _startupState;
    private readonly IDefinitionCatalog? _definitionCatalog;
    private readonly IDefinitionBundleStore? _definitionBundleStore;
    private readonly WorkspaceDefinitionOccupancy? _workspaceDefinitionOccupancy;
    private readonly IDiagnosticsBundleExporter? _diagnosticsExporter;
    private readonly IDiagnosticsBundleRequestSource? _diagnosticsRequestSource;
    private readonly IDiagnosticsArtifactPresenter? _diagnosticsArtifactPresenter;
    private readonly IRecentSessionHistoryExporter? _recentSessionHistoryExporter;
    private readonly RecoveryDataControlViewModel? _recoveryDataControlViewModel;
    private readonly LocalArtifactControlViewModel? _localArtifactControlViewModel;
    private readonly QuickTerminalController? _quickTerminalController;
    private readonly IHostAccessibilityPreferencesSource? _hostAccessibilityPreferences;
    private readonly IScreenColorSampler? _screenColorSampler;
    private readonly AppearancePreviewCoordinator? _appearancePreview;
    private IPlatformSettings? _platformSettings;
    private AvaloniaHostAppearanceAdapter? _hostAppearance;
    private INotifyCollectionChanged? _windowCollection;
    private DispatcherTimer? _applicationIconRefreshTimer;
    private readonly HashSet<MainWindowViewModel> _additionalMainWindowViewModels = [];
    private bool _desktopExitStarted;

    private static readonly string[] AppearanceClasses =
    [
        "profile-macos-classic",
        "profile-macos-liquid-glass",
        "profile-windows11",
        "profile-gnome",
        "profile-kde",
        "profile-ghostshell",
        "profile-custom",
        "appearance-light",
        "appearance-dark",
        "high-contrast",
        "motion-disabled",
        "materials-enabled",
    ];

    private static readonly int[] ScalableFontSizes =
    [
        8, 9, 10, 11, 12, 13, 14,
        15, 16, 17, 18, 19, 20, 21,
        22, 23, 24, 25, 26, 27, 28,
    ];

    static App()
    {
        // A dock splitter is laid out by its Width or Height, while its panel
        // reserves the current Thickness. Dock copies Thickness to the active
        // axis when the splitter joins a panel, but those values can drift in
        // either direction: density changes Thickness after the first layout,
        // and a rebuilt layout can restore Dock's one-pixel axis after the
        // styled Thickness is already in place. Either mismatch clips a panel
        // border or collapses the resize target.
        //
        // Whichever axis the splitter was given, it keeps; setting the other
        // would size it across the row instead of along it.
        ProportionalStackPanelSplitter.ThicknessProperty.Changed
            .AddClassHandler<ProportionalStackPanelSplitter>(SynchronizeDockSplitterAxis);
        ProportionalStackPanelSplitter.WidthProperty.Changed
            .AddClassHandler<ProportionalStackPanelSplitter>(SynchronizeDockSplitterAxis);
        ProportionalStackPanelSplitter.HeightProperty.Changed
            .AddClassHandler<ProportionalStackPanelSplitter>(SynchronizeDockSplitterAxis);
    }

    public App()
    {
    }

    private static void SynchronizeDockSplitterAxis(
        ProportionalStackPanelSplitter splitter,
        AvaloniaPropertyChangedEventArgs _)
    {
        if (!double.IsNaN(splitter.Width) && splitter.Width != splitter.Thickness)
        {
            splitter.Width = splitter.Thickness;
        }

        if (!double.IsNaN(splitter.Height) && splitter.Height != splitter.Thickness)
        {
            splitter.Height = splitter.Thickness;
        }
    }

    public App(
        MainWindowViewModel mainWindowViewModel,
        MainWindowViewModelFactory mainWindowViewModelFactory,
        ApplicationStartupState startupState,
        IDefinitionCatalog definitionCatalog,
        IDefinitionBundleStore definitionBundleStore,
        WorkspaceDefinitionOccupancy workspaceDefinitionOccupancy,
        IDiagnosticsBundleExporter diagnosticsExporter,
        IDiagnosticsBundleRequestSource diagnosticsRequestSource,
        IDiagnosticsArtifactPresenter diagnosticsArtifactPresenter,
        IRecentSessionHistoryExporter recentSessionHistoryExporter,
        RecoveryDataControlViewModel recoveryDataControlViewModel,
        LocalArtifactControlViewModel localArtifactControlViewModel,
        QuickTerminalController quickTerminalController,
        IHostAccessibilityPreferencesSource hostAccessibilityPreferences,
        IScreenColorSampler screenColorSampler,
        AppearancePreviewCoordinator? appearancePreview = null)
    {
        ArgumentNullException.ThrowIfNull(mainWindowViewModel);
        ArgumentNullException.ThrowIfNull(mainWindowViewModelFactory);
        ArgumentNullException.ThrowIfNull(startupState);
        ArgumentNullException.ThrowIfNull(definitionCatalog);
        ArgumentNullException.ThrowIfNull(definitionBundleStore);
        ArgumentNullException.ThrowIfNull(workspaceDefinitionOccupancy);
        ArgumentNullException.ThrowIfNull(diagnosticsExporter);
        ArgumentNullException.ThrowIfNull(diagnosticsRequestSource);
        ArgumentNullException.ThrowIfNull(diagnosticsArtifactPresenter);
        ArgumentNullException.ThrowIfNull(recentSessionHistoryExporter);
        ArgumentNullException.ThrowIfNull(recoveryDataControlViewModel);
        ArgumentNullException.ThrowIfNull(localArtifactControlViewModel);
        ArgumentNullException.ThrowIfNull(quickTerminalController);
        ArgumentNullException.ThrowIfNull(hostAccessibilityPreferences);
        ArgumentNullException.ThrowIfNull(screenColorSampler);
        _mainWindowViewModel = mainWindowViewModel;
        _mainWindowViewModelFactory = mainWindowViewModelFactory;
        _startupState = startupState;
        _definitionCatalog = definitionCatalog;
        _definitionBundleStore = definitionBundleStore;
        _workspaceDefinitionOccupancy = workspaceDefinitionOccupancy;
        _diagnosticsExporter = diagnosticsExporter;
        _diagnosticsRequestSource = diagnosticsRequestSource;
        _diagnosticsArtifactPresenter = diagnosticsArtifactPresenter;
        _recentSessionHistoryExporter = recentSessionHistoryExporter;
        _recoveryDataControlViewModel = recoveryDataControlViewModel;
        _localArtifactControlViewModel = localArtifactControlViewModel;
        _quickTerminalController = quickTerminalController;
        _hostAccessibilityPreferences = hostAccessibilityPreferences;
        _screenColorSampler = screenColorSampler;
        _appearancePreview = appearancePreview ?? new AppearancePreviewCoordinator();
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var mainWindowViewModel = _mainWindowViewModel
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide the main window view model.");
            var mainWindow = CreateMainWindow(mainWindowViewModel);
            desktop.MainWindow = mainWindow;
            foreach (var hostWindow in desktop.Windows.OfType<RuntimePanelHostWindow>())
            {
                hostWindow.RefreshRuntimePanelTemplates();
            }

            QuickTerminalController.Initialize(mainWindow);
            RegisterMainWindow(mainWindow);
            desktop.Exit += OnDesktopExit;
            AttachAppearance();
            mainWindow.Opened += OnStartupWindowOpened;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow MainWindow =>
        ActiveMainWindow()
        ?? throw new InvalidOperationException("A GhostSHELL window is unavailable.");

    private MainWindowViewModel MainWindowViewModel => _mainWindowViewModel
        ?? throw new InvalidOperationException("The main window view model is unavailable.");

    private QuickTerminalController QuickTerminalController => _quickTerminalController
        ?? throw new InvalidOperationException("The Quick Terminal controller is unavailable.");

    private MainWindow CreateMainWindow(MainWindowViewModel viewModel) =>
        new(
            _definitionBundleStore
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide the definition bundle store."),
            _definitionCatalog
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide the definition catalog."),
            _workspaceDefinitionOccupancy
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide workspace occupancy."),
            _diagnosticsExporter
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide the diagnostics exporter."),
            _diagnosticsRequestSource
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide the diagnostics request source."),
            _diagnosticsArtifactPresenter
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide the diagnostics artifact presenter."),
            _recentSessionHistoryExporter
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide the session-history exporter."),
            _recoveryDataControlViewModel
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide recovery data controls."),
            _localArtifactControlViewModel
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide app-managed storage controls."),
            _screenColorSampler
                ?? throw new InvalidOperationException(
                    "The desktop composition root did not provide a screen colour sampler."))
        {
            DataContext = viewModel,
        };

    private MainWindow? ActiveMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        return desktop.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsActive)
            ?? desktop.Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsVisible)
            ?? desktop.MainWindow as MainWindow;
    }

    private void RegisterMainWindow(MainWindow window)
    {
        window.Activated += OnMainWindowActivated;
        window.Closed += OnMainWindowClosed;
    }

    internal void OpenNewWindow()
    {
        if (_desktopExitStarted)
        {
            return;
        }

        var factory = _mainWindowViewModelFactory
            ?? throw new InvalidOperationException(
                "The desktop composition root did not provide the main window factory.");
        var viewModel = factory();
        try
        {
            var window = CreateMainWindow(viewModel);
            _additionalMainWindowViewModels.Add(viewModel);
            RegisterMainWindow(window);
            window.Opened += OnAdditionalWindowOpened;
            window.Show();
            window.Activate();
        }
        catch
        {
            viewModel.Dispose();
            throw;
        }
    }

    internal void ToggleQuickTerminal() => QuickTerminalController.Toggle();

    internal async Task OpenNewTabAsync(MainWindow owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (await QuickTerminalController.TryAddTabToActiveQuickTerminalAsync())
        {
            return;
        }

        await owner.ShowNewItemLauncherAsync();
    }

    internal async Task CloseTabAsync(MainWindow owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (await QuickTerminalController.TryCloseTabInActiveQuickTerminalAsync())
        {
            return;
        }

        await owner.RequestCloseTabAsync();
    }

    private void OnSettingsMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        MainWindow.NavigateToSettings();
    }

    private void OnAboutMenuClick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        MainWindow.NavigateToSettings(SettingsPage.About);
    }

    private void AttachAppearance()
    {
        _platformSettings = PlatformSettings
            ?? throw new InvalidOperationException("Platform appearance settings are unavailable.");
        var hostAccessibilityPreferences = _hostAccessibilityPreferences
            ?? throw new InvalidOperationException(
                "The desktop composition root did not provide host accessibility preferences.");
        _hostAppearance = new AvaloniaHostAppearanceAdapter(
            _platformSettings,
            hostAccessibilityPreferences);
        _platformSettings.ColorValuesChanged += OnPlatformColorValuesChanged;
        hostAccessibilityPreferences.Changed += OnHostAccessibilityPreferencesChanged;
        hostAccessibilityPreferences.Start();
        (_appearancePreview ?? throw new InvalidOperationException(
            "The desktop composition root did not provide appearance preview state."))
            .Changed += OnAppearancePreviewChanged;
        ApplyAppearance();
        if (OperatingSystem.IsMacOSVersionAtLeast(26))
        {
            _applicationIconRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            _applicationIconRefreshTimer.Tick += OnApplicationIconRefresh;
            _applicationIconRefreshTimer.Start();
        }

        _definitionCatalog?.Changed += OnDefinitionCatalogChanged;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.Windows is INotifyCollectionChanged windowCollection)
        {
            _windowCollection = windowCollection;
            _windowCollection.CollectionChanged += OnWindowCollectionChanged;
        }

    }

    private void OnApplicationIconRefresh(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RefreshApplicationIcon();
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues values)
    {
        _ = sender;
        _ = values;
        ApplyAppearance();
    }

    private void OnDefinitionCatalogChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(() =>
        {
            ApplyAppearance();
        });
    }

    private void OnHostAccessibilityPreferencesChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(ApplyAppearance);
    }

    private void OnAppearancePreviewChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.UIThread.Post(ApplyAppearance);
    }

    private void OnWindowCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        if (e.NewItems is null)
        {
            return;
        }

        var resources = ResolveAppearanceResources();
        foreach (var window in e.NewItems.OfType<Window>())
        {
            ApplyToWindow(window, resources);
        }
    }

    private void ApplyAppearance()
    {
        var resources = ResolveAppearanceResources();
        RefreshApplicationIcon();

        RequestedThemeVariant = resources.ThemeVariant;
        ApplyApplicationResources(resources);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                ApplyToWindow(window, resources);
            }

            (desktop.MainWindow as MainWindow)?.RefreshAppearanceControlsFromStoredProfile();
        }
    }

    private void RefreshApplicationIcon()
    {
        var hostAccent = _hostAppearance?.GetCurrent().Accent;
        if (hostAccent is { } accent)
        {
            _ = MacOsApplicationIcon.TryApply(accent);
        }
    }

    /// <summary>
    /// The accent of the workspace that is open, when it has one of its own. A
    /// workspace accent is a temporary override of the application's: it lasts
    /// while that workspace is open and leaves nothing behind, which is why it
    /// is held here rather than written to the stored theme.
    /// </summary>
    private RgbColor? _workspaceAccent;

    public void SetWorkspaceAccent(RgbColor? accent)
    {
        if (_workspaceAccent == accent)
        {
            return;
        }

        _workspaceAccent = accent;
        ApplyAppearance();
    }

    private EffectiveAppearanceResources ResolveAppearanceResources()
    {
        var preference = _appearancePreview?.Current.Theme
            ?? _definitionCatalog?.Snapshot.Themes
            .FirstOrDefault(item => item.Value.Id == ThemePreference.Default.Id)?.Value
            ?? ThemePreference.Default;
        var hostAppearance = _hostAppearance?.GetCurrent()
            ?? throw new InvalidOperationException("Platform appearance settings are unavailable.");
        var theme = preference.Resolve(hostAppearance);
        if (_workspaceAccent is { } workspaceAccent)
        {
            theme = theme with
            {
                Accent = workspaceAccent,
                AccentSource = AccentSource.Custom,
            };
        }

        return EffectiveAppearanceResourceMapper.Map(theme);
    }

    /// <summary>
    /// Publishes one appearance token, and only when it has actually changed.
    ///
    /// Every write to the application's resources invalidates each control
    /// bound to that key, anywhere in the window. There are seventy of these
    /// and almost all of them are the same before and after — a workspace
    /// accent moves the accent family and nothing else — so rewriting the set
    /// wholesale restyled the entire tree to change fifteen colours. That is
    /// most of a third of a second on every workspace switch.
    ///
    /// Brushes are compared by colour rather than by reference, because each
    /// pass builds new ones; without that every write still looks like a
    /// change and nothing is saved.
    /// </summary>
    /// <summary>
    /// A surface colour, carrying the shell's surface opacity — or solid, when
    /// the backdrop is off and there is nothing behind it to show.
    /// </summary>
    private SolidColorBrush Translucent(Color color) =>
        new(Color.FromArgb(
            WindowIsTranslucent ? ShellSurfaceAlpha : byte.MaxValue,
            color.R,
            color.G,
            color.B));

    /// <summary>
    /// The same, as a number, for the surfaces that are drawn rather than
    /// filled — a terminal paints its own background from its palette.
    /// </summary>
    private double SurfaceOpacity =>
        WindowIsTranslucent ? ShellSurfaceAlpha / (double)byte.MaxValue : 1;

    private void Publish(string key, object? value)
    {
        if (Resources.TryGetValue(key, out var existing)
            && AppearanceValuesMatch(existing, value))
        {
            return;
        }

        Resources[key] = value;
    }

    private static bool AppearanceValuesMatch(object? existing, object? value) =>
        (existing, value) switch
        {
            (ISolidColorBrush left, ISolidColorBrush right) =>
                left.Color == right.Color
                && left.Opacity.Equals(right.Opacity),
            (FontFamily left, FontFamily right) =>
                string.Equals(left.Name, right.Name, StringComparison.Ordinal),
            _ => Equals(existing, value),
        };

    /// <summary>
    /// How much of the base surface survives.
    ///
    /// The surface stays the dark it always was — this is a hint of what is
    /// behind the window, not a window you can see through. Seventy per cent
    /// let so much light in that the shell read as pale bands around dark
    /// panels; the earlier eighty-five looked like nothing only because the
    /// window was still opaque at the time and none of it was reaching the
    /// screen. This is the number to move in either direction.
    /// </summary>
    /// <summary>
    /// How solid the base surface is painted.
    ///
    /// Nothing at all when the material's own translucency is left to stand:
    /// the platform's glass is then what shows, and painting over it is the
    /// whole of what there is to switch off.
    /// </summary>
    private byte ShellBackdropAlpha => WindowOverridesBackdropOpacity
        ? StoredOpacityAlpha
        : (byte)0;

    /// <summary>
    /// The stored opacity as an alpha, whether or not the base is painted with
    /// it — the panels use it as their glass even when the base does not.
    /// </summary>
    private byte StoredOpacityAlpha => (byte)Math.Clamp(
        (int)Math.Round(WindowBackdropOpacityPercent * byte.MaxValue / 100.0),
        0,
        byte.MaxValue);

    /// <summary>
    /// How solid the surfaces standing on the base are.
    ///
    /// They were fully opaque, which made the shell a pale frame around dark
    /// slabs — and the frame is what read as a bar along the top, because the
    /// chrome is the largest run of base surface not covered by a panel. The
    /// system title bar was quietened and the band stayed, which is what ruled
    /// that out and left this. Nearly solid, because text is read on these.
    /// </summary>
    /// <summary>
    /// How solid the surfaces standing on the base are.
    ///
    /// As glass they carry the base's own opacity, so the shell reads as one
    /// sheet of material rather than panels standing on it. Otherwise they sit
    /// most of the way to solid — halfway between the base and opaque — which
    /// is what the shell looked like before the choice existed, and text is
    /// read on them.
    /// </summary>
    private byte ShellSurfaceAlpha => WindowHasGlassPanels
        ? StoredOpacityAlpha
        : (byte)Math.Clamp(
            StoredOpacityAlpha + ((byte.MaxValue - StoredOpacityAlpha) / 2),
            0,
            byte.MaxValue);


    /// <summary>
    /// Whether the host has asked for less transparency. The shell answers it
    /// the same way the Quick Terminal does: no translucency and no blur.
    /// </summary>
    public bool PrefersReducedTransparency =>
        _hostAppearance?.GetCurrent().ReducedTransparency == true;

    /// <summary>
    /// How far to blur behind the shell, as stored — zero when the person has
    /// turned it off, and zero when the host asks for reduced transparency,
    /// which is the same answer arrived at two ways.
    /// </summary>
    public bool WindowIsTranslucent =>
        ResolveStoredTheme().MaterialDisposition == MaterialDisposition.Enabled;

    /// <summary>
    /// Whether the host permits native material at all. Quick Terminal owns a
    /// separate translucency preference, but it still obeys this host boundary.
    /// </summary>
    public bool HostAllowsAdvancedMaterials =>
        _hostAppearance?.GetCurrent() is { } host
        && AllowsAdvancedMaterials(host);

    internal static bool AllowsAdvancedMaterials(HostAppearance host) =>
        host.SupportsAdvancedMaterials
        && !host.HighContrast
        && !host.ReducedTransparency;

    /// <summary>
    /// How solid the base surface is, as stored — fully solid when the host
    /// asks for reduced transparency, which is the same answer the blur gives
    /// to the same question.
    /// </summary>
    public int WindowBackdropOpacityPercent =>
        PrefersReducedTransparency ? 100 : StoredTheme.BackdropOpacityPercent;

    /// <summary>
    /// Whether the panels are glass. Reduced transparency says no, the same
    /// way it says no to the base surface.
    /// </summary>
    public bool WindowHasGlassPanels =>
        WindowIsTranslucent && StoredTheme.HasGlassPanels;

    /// <summary>
    /// Whether the shell paints its own opacity over the material. A host
    /// asking for reduced transparency gets it painted regardless: there is no
    /// material to leave standing.
    /// </summary>
    public bool WindowOverridesBackdropOpacity =>
        !WindowIsTranslucent || StoredTheme.OverridesBackdropOpacity;

    /// <summary>
    /// What the platform draws the window's own corners at, in points. Not a
    /// number of ours: this desktop has three, one per kind of window, and the
    /// corner style decides which kind the shell asks to be.
    /// </summary>
    public double WindowCornerRadius =>
        DensityCornerScale.WindowRadius(StoredTheme.Density);

    public InterfaceDensity WindowDensity => StoredTheme.Density;

    private ThemePreference StoredTheme =>
        _appearancePreview?.Current.Theme
        ?? _definitionCatalog?.Snapshot.Themes
            .FirstOrDefault(item => item.Value.Id == ThemePreference.Default.Id)
            ?.Value ?? ThemePreference.Default;

    private EffectiveTheme ResolveStoredTheme() => StoredTheme.Resolve(
        _hostAppearance?.GetCurrent()
        ?? new HostAppearance(
            OperatingSystem.IsMacOS()
                ? HostOperatingSystem.MacOS
                : OperatingSystem.IsWindows()
                    ? HostOperatingSystem.Windows
                    : HostOperatingSystem.Linux,
            HostColorScheme.Dark,
            null,
            highContrast: false,
            reducedMotion: false,
            reducedTransparency: false,
            textScale: 1,
            supportsAdvancedMaterials: false,
            supportsLiquidGlass: false));

    private void ApplyApplicationResources(EffectiveAppearanceResources resources)
    {
        Publish("ShellBackgroundBrush", Brush(resources.Background));
        // The base surface the whole shell sits on. Slightly translucent, so
        // the blurred desktop reads through the gaps between panels — but only
        // where the host is willing: reduced transparency is an accessibility
        // setting, and an operating system that says so gets a solid surface.
        Publish(
            "ShellWindowBackdropBrush",
            new SolidColorBrush(Color.FromArgb(
                WindowIsTranslucent
                    ? ShellBackdropAlpha
                    : byte.MaxValue,
                resources.Background.R,
                resources.Background.G,
                resources.Background.B)));
        Publish("ShellSidebarBrush", Translucent(resources.SidebarSurface));
        // The same swatch as glass, for the one sidebar-coloured surface that
        // floats over the app's own content. It stands on a blurred snapshot
        // of what it covers — live backdrop blur is not available inside the
        // window — so it can afford to be genuinely translucent: the blur
        // beneath is what keeps the conversation legible.
        Publish(
            "ShellSidebarOverlayBrush",
            new SolidColorBrush(Color.FromArgb(
                0xCC,
                resources.SidebarSurface.R,
                resources.SidebarSurface.G,
                resources.SidebarSurface.B)));
        Publish("ShellSidebarBorderBrush", Brush(resources.SidebarBorder));
        Publish("ShellSidebarSelectionBrush", Brush(resources.SidebarSelectionSurface));
        Publish("ShellSurfaceBrush", Translucent(resources.Surface));
        Publish("ShellSurfaceRaisedBrush", Translucent(resources.RaisedSurface));
        // The same surface, solid, for the ones that open in a window of their
        // own. A flyout is its own top level with no material behind it, so the
        // shell's translucency there is not glass — it is a hole, showing
        // whatever the desktop has underneath, unblurred and unreadable. What
        // makes the surfaces inside the window read as glass is the platform
        // backdrop they stand on, and a popup stands on nothing.
        Publish("ShellPopupSurfaceBrush", Brush(resources.RaisedSurface));
        // An overlay floats over the shell rather than standing on it, so it is
        // glass whenever there is any to be — not only when the panels are.
        // Something that hangs in front of the window is where the material
        // reads best, and it is not one of the surfaces that switch governs.
        Publish(
            "ShellSurfaceOverlayBrush",
            new SolidColorBrush(Color.FromArgb(
                WindowIsTranslucent ? StoredOpacityAlpha : byte.MaxValue,
                resources.RaisedSurface.R,
                resources.RaisedSurface.G,
                resources.RaisedSurface.B)));
        // A veil is glass over the app's own content: a genuinely translucent
        // wash of the background tone, weighted toward black so it reads as a
        // distinct darker band even over its own colour — and anything that
        // slides beneath it stays visible, dimmed rather than hidden.
        Publish(
            "ShellVeilBrush",
            new SolidColorBrush(Color.FromArgb(
                0xA8,
                (byte)(resources.Background.R / 2),
                (byte)(resources.Background.G / 2),
                (byte)(resources.Background.B / 2))));
        Publish("ShellSurfaceHoverBrush", Brush(resources.HoverSurface));
        Publish("ShellBorderBrush", Brush(resources.Border));
        Publish("ShellControlSurfaceBrush", Brush(resources.ControlSurface));
        Publish("ShellControlBorderBrush", Brush(resources.ControlBorder));
        Publish("ShellControlHoverBrush", Brush(resources.ControlHoverSurface));
        Publish("ShellTextBrush", Brush(resources.Text));
        Publish("ShellMutedBrush", Brush(resources.MutedText));
        Publish("ShellAccentBrush", Brush(resources.Accent));
        // The flyout's shadow carries the accent in its inner glow, so it is
        // composed here where the live accent is known — a workspace retint
        // re-runs this and the glow follows.
        Publish(
            "ShellFlyoutShadow",
            BoxShadows.Parse(
                "0 8 48 0 #8C000000, 0 2 14 0 #59000000, inset 0 0 40 0 "
                + $"#38{resources.Accent.R:X2}{resources.Accent.G:X2}{resources.Accent.B:X2}"));
        Publish("ShellAccentForegroundBrush", Brush(resources.AccentForeground));
        Publish("ShellAccentSoftBrush", Brush(resources.AccentSoft));
        Publish("ShellDangerBrush", Brush(resources.Danger));
        Publish("ShellDangerForegroundBrush", Brush(resources.DangerForeground));
        Publish("ShellDangerSoftBrush", Brush(resources.DangerSoft));
        Publish("ShellDangerBorderBrush", Brush(resources.DangerBorder));
        Publish("ShellSuccessBrush", Brush(resources.Success));
        Publish("ShellSuccessSoftBrush", Brush(resources.SuccessSoft));
        Publish("ShellSuccessBorderBrush", Brush(resources.SuccessBorder));
        Publish("ShellWarningBrush", Brush(resources.Warning));
        Publish("ShellWarningSoftBrush", Brush(resources.WarningSoft));
        Publish("ShellWarningBorderBrush", Brush(resources.WarningBorder));
        Publish("ShellNoticeBorderBrush", Brush(resources.NoticeBorder));
        // Controls the design system has not retemplated — check boxes, radio
        // buttons, switches, sliders, calendar and text selection — take their
        // accent from Fluent's SystemAccentColor family, which otherwise tracks
        // the operating system rather than the shell's accent setting. Publishing
        // the resolved shell accent (and the shade ramp Fluent derives from it)
        // is what makes every control answer the same appearance setting.
        Publish("SystemAccentColor", resources.Accent);
        Publish("SystemAccentColorDark1", Shade(resources.Accent, Colors.Black, 0.15));
        Publish("SystemAccentColorDark2", Shade(resources.Accent, Colors.Black, 0.30));
        Publish("SystemAccentColorDark3", Shade(resources.Accent, Colors.Black, 0.45));
        Publish("SystemAccentColorLight1", Shade(resources.Accent, Colors.White, 0.15));
        Publish("SystemAccentColorLight2", Shade(resources.Accent, Colors.White, 0.30));
        Publish("SystemAccentColorLight3", Shade(resources.Accent, Colors.White, 0.45));
        // Text selection reads as a translucent accent wash, as the host does it,
        // rather than Fluent's opaque block.
        Publish("TextControlSelectionHighlightColor", Color.FromArgb(
            0x66,
            resources.Accent.R,
            resources.Accent.G,
            resources.Accent.B));
        // The focused field wears the host's accent halo — macOS's focus ring —
        // rather than only a recolored border.
        Publish("ShellFocusRingShadow", new BoxShadows(new BoxShadow
        {
            Blur = 0,
            Spread = 3,
            Color = Color.FromArgb(
                0x59,
                resources.Accent.R,
                resources.Accent.G,
                resources.Accent.B),
        }));
        Publish(
            "ShellAgentPanelGlowBrush",
            new SolidColorBrush(Color.FromArgb(
                0x10,
                resources.Accent.R,
                resources.Accent.G,
                resources.Accent.B)));
        Publish(
            "ShellAgentPanelGlowShadow",
            BoxShadows.Parse(
                "inset 0 0 224 0 "
                + $"#46{resources.Accent.R:X2}{resources.Accent.G:X2}{resources.Accent.B:X2}, "
                + "inset 0 0 112 0 "
                + $"#5C{resources.Accent.R:X2}{resources.Accent.G:X2}{resources.Accent.B:X2}, "
                + "inset 0 0 51.2 0 "
                + $"#7A{resources.Accent.R:X2}{resources.Accent.G:X2}{resources.Accent.B:X2}, "
                + "inset 0 0 17.6 0 "
                + $"#C0{resources.Accent.R:X2}{resources.Accent.G:X2}{resources.Accent.B:X2}"));
        Publish("ShellUiFontFamily", resources.FontFamily);
        // Data surfaces (result grids, SQL editors) read in a monospace stack;
        // interface chrome stays on the UI family.
        Publish("ShellDataFontFamily", new FontFamily(
            "SF Mono,Menlo,Monaco,Cascadia Mono,Consolas,monospace"));
        Publish("ShellBaseFontSize", resources.BaseFontSize);
        Publish("ShellPillFontSize", resources.PillFontSize);
        foreach (var baseFontSize in ScalableFontSizes)
        {
            Publish($"ShellFontSize{baseFontSize}", resources.ScaleFontSize(baseFontSize));
            Publish(
                $"ShellLineHeight{baseFontSize}",
                Math.Round(
                    resources.ScaleFontSize(baseFontSize) * 1.35 * 2,
                    MidpointRounding.AwayFromZero) / 2);
        }

        Publish("ShellControlMinHeight", resources.ControlMinHeight);
        Publish("ShellSurfaceOpacity", SurfaceOpacity);

        // The three sizes a mark is drawn at — in a row, beside a name, and as
        // the subject of the page. Derived from the control height rather than
        // fixed, so a compact density shrinks the tiles with everything else
        // instead of leaving them looming over the rows they belong to.
        Publish("ShellTileSizeSm", Math.Round(resources.ControlMinHeight * 0.85));
        Publish("ShellTileSizeMd", Math.Round(resources.ControlMinHeight * 1.05));
        Publish("ShellTileSizeLg", Math.Round(resources.ControlMinHeight * 1.75));

        // How wide the workspace rail is: a tile, the inset its column keeps on
        // either side, and the rail's own hairline border. Written down as a
        // number it was right at one density and held the rail open at the
        // others, because the tiles moved with the setting and it did not.
        Publish(
            "ShellRailWidth",
            Math.Round(resources.ControlMinHeight * 1.05)
            + (resources.Spacing.Small * 2)
            + 2);

        // The rail tile's outline, and how wide it opens when it offers to close
        // the workspace. Twice its own width, so the action it reveals is the
        // same size as the tile it belongs to rather than a sliver beside it.
        Publish("ShellWorkspaceRailRing", new Thickness(
            Math.Max(1, Math.Round(resources.ControlMinHeight * 0.05))));
        Publish(
            "ShellWorkspaceRailTileExpandedWidth",
            Math.Round(resources.ControlMinHeight * 1.05) * 2);
        Publish(
            "ShellWorkspaceRailTileSaveWidth",
            Math.Round(resources.ControlMinHeight * 1.05) * 3);

        // The attention dot, and the ring that keeps it legible on top of a
        // saturated tile. Derived like the tiles so a compact density does not
        // leave a mark sized for a roomier one.
        // A panel's header: one control's height plus the clearance around it, so
        // a compact density gives back the same proportion of the panel to its
        // content rather than leaving a bar sized for a roomier setting.
        Publish(
            "ShellPanelHeaderHeight",
            Math.Round(resources.ControlMinHeight * 1.3));
        Publish("ShellSignalDotSize", Math.Round(resources.ControlMinHeight * 0.42));
        Publish("ShellSignalDotRing", new Thickness(
            Math.Max(1, Math.Round(resources.ControlMinHeight * 0.06))));
        // The window's own corners, so the outermost surface inside it can be
        // concentric with the frame rather than guessing at it.
        Publish("ShellWindowCornerRadius", WindowCornerRadius);
        Publish("ShellControlCornerRadius", resources.ControlCornerRadius);
        Publish("ShellControlPadding", resources.ControlPadding);
        Publish("ShellButtonPadding", resources.ButtonPadding);
        Publish("ShellCardCornerRadius", resources.CardCornerRadius);
        Publish("ShellSidebarCornerRadius", resources.SidebarCornerRadius);
        Publish("ShellCardRadius", resources.CardCornerRadius.TopLeft);
        Publish("ShellPillCornerRadius", resources.PillCornerRadius);
        Publish("ShellInnerCornerRadius", resources.InnerCornerRadius);
        // The clearance between a rounded miniature frame and the tiles inside
        // it. It follows the radius, not a fixed step: at a tight radius a
        // couple of pixels reads fine, while a round setting needs the tiles
        // pulled in or they touch the frame's curve at every corner.
        Publish("ShellPreviewTileInset", new Thickness(
            Math.Max(3, resources.InnerCornerRadius.TopLeft * 0.6)));

        // The spacing scale, in both forms the framework needs: a number for the
        // Spacing/ColumnSpacing/RowSpacing properties, and a Thickness for Margin
        // and Padding. Publishing only one of the two is why the markup fell back
        // to literals — half the properties could not consume the token.
        var spacing = resources.Spacing;
        foreach (var (name, value) in new (string, double)[]
                 {
                     // A published zero, so an inset can name every edge the same
                     // way whether or not that edge has a value.
                     ("None", 0),
                     ("Xs", spacing.ExtraSmall),
                     ("Sm", spacing.Small),
                     ("Md", spacing.Medium),
                     ("Lg", spacing.Large),
                     ("Xl", spacing.ExtraLarge),
                     ("Xxl", spacing.Huge),
                 })
        {
            Publish($"ShellSpace{name}", value);
            Publish($"ShellInset{name}", new Thickness(value));
        }

        // Named insets, for the three shapes that recur often enough that spelling
        // them out at each use site is how they drifted apart in the first place.
        Publish("ShellCardPadding", new Thickness(spacing.Large));
        Publish("ShellCardPaddingCompact", new Thickness(spacing.Medium));
        Publish("ShellPagePadding", new Thickness(spacing.Large));
        Publish("ShellRowPadding", new Thickness(spacing.Large, spacing.Medium));
        Publish("ShellListRowPadding", new Thickness(spacing.Medium, spacing.Small));
        Publish("ShellPillPadding", new Thickness(spacing.Small, spacing.ExtraSmall));

        // For a native child view that has to round its own layer because Avalonia
        // cannot clip it. Only the bottom corners are at the panel's edge — a
        // header covers the top two — so rounding all four would carve notches
        // into the middle of the panel rather than shaping its outline.
        Publish("ShellPanelBottomCornerRadius", new CornerRadius(
            0,
            0,
            resources.CardCornerRadius.BottomRight,
            resources.CardCornerRadius.BottomLeft));
        Publish("ShellAppearanceStatus", resources.AppearanceStatus);
        Publish("ShellAccentStatus", resources.AccentStatus);
        Publish("ShellMaterialStatus", resources.MaterialStatus);
        Publish("ShellPlatformProfileClass", resources.ProfileClass);
        Publish("ShellHighContrast", resources.HighContrast);
        Publish("ShellMotionEnabled", resources.MotionEnabled);
        Publish("ShellAdvancedMaterialsEnabled", resources.AdvancedMaterialsEnabled);
    }

    private void ApplyToWindow(
        Window window,
        EffectiveAppearanceResources resources)
    {
        window.RequestedThemeVariant = resources.ThemeVariant;
        foreach (var appearanceClass in AppearanceClasses)
        {
            window.Classes.Remove(appearanceClass);
        }

        window.Classes.Add(resources.ProfileClass);
        window.Classes.Add(resources.AppearanceClass);
        // The backdrop is a stored setting, so a change to it has to reach the
        // window that is wearing it rather than only the next one opened.
        RefreshWindowBackdrop(window);
        if (resources.HighContrast)
        {
            window.Classes.Add("high-contrast");
        }
        if (!resources.MotionEnabled)
        {
            window.Classes.Add("motion-disabled");
        }
        if (resources.AdvancedMaterialsEnabled)
        {
            window.Classes.Add("materials-enabled");
        }

    }

    internal static void RefreshWindowBackdrop(Window window)
    {
        switch (window)
        {
            case MainWindow mainWindow:
                mainWindow.RefreshWindowBackdrop();
                break;
            case QuickTerminalWindow quickTerminalWindow:
                quickTerminalWindow.ApplyBackdrop();
                break;
        }
    }

    private static SolidColorBrush Brush(Color color) => new(color);

    /// <summary>
    /// One step of the accent shade ramp: the accent blended toward black or
    /// white, mirroring how Fluent derives its Dark1–3/Light1–3 variants from
    /// the system accent it is being asked to forget.
    /// </summary>
    private static Color Shade(Color accent, Color target, double amount) =>
        Color.FromRgb(
            BlendChannel(accent.R, target.R, amount),
            BlendChannel(accent.G, target.G, amount),
            BlendChannel(accent.B, target.B, amount));

    private static byte BlendChannel(byte from, byte to, double amount) =>
        (byte)Math.Round(from + ((to - from) * Math.Clamp(amount, 0, 1)));

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _ = e;
        _desktopExitStarted = true;
        if (sender is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit -= OnDesktopExit;
        }

        _platformSettings?.ColorValuesChanged -= OnPlatformColorValuesChanged;
        _hostAccessibilityPreferences?.Changed -= OnHostAccessibilityPreferencesChanged;
        _definitionCatalog?.Changed -= OnDefinitionCatalogChanged;
        _appearancePreview?.Changed -= OnAppearancePreviewChanged;
        _windowCollection?.CollectionChanged -= OnWindowCollectionChanged;
        _windowCollection = null;
        if (_applicationIconRefreshTimer is { } iconRefreshTimer)
        {
            iconRefreshTimer.Stop();
            iconRefreshTimer.Tick -= OnApplicationIconRefresh;
            _applicationIconRefreshTimer = null;
        }

        foreach (var viewModel in _additionalMainWindowViewModels.ToArray())
        {
            viewModel.TeardownPresentationForShutdown();
        }

        _quickTerminalController?.Dispose();
    }

    /// <summary>
    /// Drains every main-window runtime after Avalonia has finished pumping its
    /// dispatcher. Additional windows are application-owned rather than DI-owned,
    /// so they are disposed here only after their asynchronous shutdown work has
    /// released sessions and workspace-isolation leases.
    /// </summary>
    public async Task QuiesceForShutdownAsync(CancellationToken cancellationToken)
    {
        _desktopExitStarted = true;
        var additionalViewModels = _additionalMainWindowViewModels.ToArray();
        MainWindowViewModel[] allViewModels = _mainWindowViewModel is { } mainViewModel
            ? [mainViewModel, .. additionalViewModels]
            : additionalViewModels;
        var quiescenceTasks = allViewModels
            .Select(BeginMainWindowQuiescence)
            .ToArray();
        var quiescence = Task.WhenAll(quiescenceTasks);
        try
        {
            await quiescence.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // WaitAsync cancellation only cancels the caller's wait. The owned
                // shutdown cores must finish before Dispose tears down their state.
                await quiescence.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    foreach (var viewModel in additionalViewModels)
                    {
                        viewModel.Dispose();
                    }
                }
                finally
                {
                    _additionalMainWindowViewModels.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Performs the update-restart close while Avalonia still pumps its dispatcher.
    /// Host scopes close before presentation and isolation are torn down, so a
    /// terminal cannot retain a graph that points into an already-stopped isolate.
    /// </summary>
    public async Task PrepareForUpdateRestartAsync(CancellationToken cancellationToken)
    {
        _desktopExitStarted = true;
        var additionalViewModels = _additionalMainWindowViewModels.ToArray();
        MainWindowViewModel[] allViewModels = _mainWindowViewModel is { } mainViewModel
            ? [mainViewModel, .. additionalViewModels]
            : additionalViewModels;
        await Task.WhenAll(allViewModels.Select(viewModel =>
            CloseWindowForUpdateRestartAsync(viewModel, cancellationToken)));

        _mainWindowViewModel?.TeardownPresentationForShutdown();
        foreach (var viewModel in additionalViewModels)
        {
            viewModel.TeardownPresentationForShutdown();
        }

        _quickTerminalController?.Dispose();
        await QuiesceForShutdownAsync(cancellationToken);
    }

    private static async Task CloseWindowForUpdateRestartAsync(
        MainWindowViewModel viewModel,
        CancellationToken cancellationToken)
    {
        var result = await viewModel.CloseWindowAsync(
            CloseDecision.Confirm,
            cancellationToken);
        if (result is not HostResult<CloseScopeResult>.Success
            {
                Value: CloseScopeResult.Completed completed,
            }
            || completed.Scope != CloseScopeKind.Window
            || !string.Equals(
                completed.TargetId,
                viewModel.WindowId.Value,
                StringComparison.Ordinal)
            || completed.Sessions.Any(session => session.Outcome is not (
                SessionCloseOutcome.GracefullyClosed
                or SessionCloseOutcome.ForceTerminated
                or SessionCloseOutcome.AlreadyClosed)))
        {
            throw new InvalidOperationException(
                "The session host could not close a window for the update restart.");
        }
    }

    private static Task BeginMainWindowQuiescence(MainWindowViewModel viewModel)
    {
        try
        {
            return viewModel.QuiesceForShutdownAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Preserve a synchronous startup failure as a task so every other
            // window still begins quiescence and Task.WhenAll reports the fault.
            return Task.FromException(exception);
        }
    }

    private void OnMainWindowActivated(object? sender, EventArgs e)
    {
        _ = e;
        if (sender is not MainWindow window
            || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        desktop.MainWindow = window;
        QuickTerminalController.SetMainWindow(window);
        var accent = (window.DataContext as MainWindowViewModel)?.ActiveWorkspaceAccent;
        SetWorkspaceAccent(RgbColor.TryParse(accent, out var color) ? color : null);
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _ = e;
        if (sender is not MainWindow mainWindow
            || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        mainWindow.Activated -= OnMainWindowActivated;
        mainWindow.Closed -= OnMainWindowClosed;
        mainWindow.Opened -= OnAdditionalWindowOpened;
        if (!_desktopExitStarted
            && mainWindow.DataContext is MainWindowViewModel viewModel
            && _additionalMainWindowViewModels.Remove(viewModel))
        {
            viewModel.TeardownPresentationForShutdown();
            viewModel.Dispose();
        }

        var replacement = desktop.Windows
            .OfType<MainWindow>()
            .FirstOrDefault(window => !ReferenceEquals(window, mainWindow));
        if (replacement is null)
        {
            desktop.Shutdown();
            return;
        }

        desktop.MainWindow = replacement;
        QuickTerminalController.SetMainWindow(replacement);
    }

    private async void OnAdditionalWindowOpened(object? sender, EventArgs e)
    {
        _ = e;
        if (sender is not MainWindow owner
            || owner.DataContext is not MainWindowViewModel viewModel
            || _startupState is null
            || _desktopExitStarted)
        {
            return;
        }

        owner.Opened -= OnAdditionalWindowOpened;
        await _startupState.Initialized;
        if (_desktopExitStarted
            || !_additionalMainWindowViewModels.Contains(viewModel))
        {
            return;
        }

        _ = await viewModel.OpenDefaultLauncherIfIdleAsync(CancellationToken.None);
    }

    /// <summary>
    /// One way back in, however the last process ended.
    ///
    /// The runtime snapshot is written as the workspace changes, so by the time
    /// a process dies its state is already stored; asking which way it died and
    /// putting a modal choice in front of the window taught nothing the restore
    /// did not already know, and made a crash a decision the person who did not
    /// crash anything had to make before they could work.
    /// </summary>
    private async void OnStartupWindowOpened(object? sender, EventArgs e)
    {
        if (sender is not MainWindow owner
            || _startupState is null
            || _desktopExitStarted)
        {
            return;
        }

        owner.Opened -= OnStartupWindowOpened;
        // With keys sealed under the startup PIN, the run begins behind the
        // lock screen; the restore preference and the stored session both live
        // in the database that only exists to us after that.
        await _startupState.Initialized;
        if (_desktopExitStarted)
        {
            return;
        }

        // The history load the window construction queued hit that same
        // closed database; asked again now, it answers.
        _ = MainWindowViewModel.RetryRecentSessionHistoryAsync(CancellationToken.None);
        _ = await MainWindowViewModel.RestoreSessionOnStartupAsync(CancellationToken.None);
        // Whatever the restore did or did not find, the window does not come up
        // empty: Main's launcher is always there to come up in.
        _ = await MainWindowViewModel.OpenDefaultLauncherIfIdleAsync(CancellationToken.None);
    }
}
