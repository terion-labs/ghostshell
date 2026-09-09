using Asura.App;
using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Asura.Core;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Views;

public sealed partial class QuickTerminalWindow : Window
{
    private const double ChromeHeight = 36;
    private static readonly CubicEaseOut SlideEasing = new();
    // Not None: asking for no transparency at all makes the native window
    // opaque, and the reveal animation slides a window that has already been
    // composited. Transparent without a material is the "no blur" state.
    private static readonly IReadOnlyList<WindowTransparencyLevel> TransparentHint =
        [WindowTransparencyLevel.Transparent];
    private static readonly IReadOnlyList<WindowTransparencyLevel> BlurHint =
    [
        WindowTransparencyLevel.AcrylicBlur,
        WindowTransparencyLevel.Blur,
        WindowTransparencyLevel.Transparent,
    ];
    private bool _allowClose;
    private QuickTerminalSettings _settings = QuickTerminalSettings.Default;
    private HostAccessibilityPreferences _hostPreferences =
        HostAccessibilityPreferences.Default;
    private double _preparedRevealProgress = 1;
    private int _revealToken;
    private PixelPoint _shownPosition;
    private PixelPoint _hiddenPosition;
    private bool _hasPlacement;
    private double _placementScale = 1;
    private bool _isResizing;
    private double _resizeStartScreenY;
    private double _resizeStartHeight;
    private QuickTerminalTabReorder? _tabReorder;
    private Grid? _tabDropTarget;
    private readonly CancellationTokenSource _lifetime = new();

    public QuickTerminalWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
            Dispatcher.UIThread.Post(UpdateNativeAgentMaterial);
        SizeChanged += (_, _) => UpdateNativeAgentMaterial();
        QuickTerminalAgentSurface.SizeChanged += (_, _) =>
            UpdateNativeAgentMaterial();
        Deactivated += OnWindowDeactivated;
        Closing += OnWindowClosing;
    }

    public event EventHandler? DismissRequested;

    public event EventHandler? AgentSettingsRequested;

    public event EventHandler? NewConnectionRequested;

    public event EventHandler<QuickTerminalHeightChangedEventArgs>? HeightResizeCompleted;

    public bool HideOnFocusLoss { get; set; } = true;

    public void ApplySettings(QuickTerminalSettings settings) =>
        ApplySettings(settings, HostAccessibilityPreferences.Default);

    public void ApplySettings(
        QuickTerminalSettings settings,
        HostAccessibilityPreferences hostPreferences)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostPreferences);
        _settings = settings;
        _hostPreferences = hostPreferences;
        var backgroundOpacity = QuickTerminalPresentationPolicy.EffectiveOpacity(
            settings,
            hostPreferences);
        QuickTerminalHost.BackgroundOpacity = backgroundOpacity;
        QuickTerminalPlaceholderBackground.Opacity = backgroundOpacity;
        HideOnFocusLoss = settings.HideOnFocusLoss;
        ApplyBackdrop();
    }

    /// <summary>
    /// Applies the backdrop after the native window exists.
    ///
    /// The platform's own material, as the shell does. macOS was blurred by an
    /// explicit radius here instead, which left AppKit with no material to
    /// shape the window from — and a window it takes to be a plain square gets
    /// a shadow and an edge built from that square, standing proud of its own
    /// rounded corners.
    /// </summary>
    public void ApplyBackdrop()
    {
        var hostAllowsMaterials = Avalonia.Application.Current is not App app
            || app.HostAllowsAdvancedMaterials;
        if (!hostAllowsMaterials
            || _hostPreferences.ReducedTransparency
            || !QuickTerminalPresentationPolicy.ShouldUseBlur(
                _settings,
                _hostPreferences))
        {
            ApplyTransparencyHint(TransparentHint);
            // Keep the native backing clear when changing an already-created
            // window from material to transparent. Otherwise AppKit may leave
            // its previous windowBackgroundColor visible for this frame.
            _ = MacOsQuickTerminalReveal.TryClearWindowBacking(this);
            QuickTerminalStatusBackground.Opacity = 1;
            _ = MacOsQuickTerminalReveal.TrySetChromeMaterial(
                this,
                ChromeHeight,
                MacOsMaterial.Sidebar,
                isVisible: false);
            UpdateNativeAgentMaterial();
            return;
        }

        ApplyTransparencyHint(BlurHint);
        // Avalonia pins its visual-effect view to the deprecated Light
        // material. HUDWindow proved clearer on the live desktop than Popover
        // while retaining strong blur, so Quick Terminal uses it for both the
        // viewport and its separately framed native controls strip.
        _ = MacOsWindowMaterial.TrySit(
            this,
            MacOsMaterial.HudWindow);
        // Avalonia's macOS Blur mode also paints NSWindow itself with opaque
        // windowBackgroundColor. The reveal moves the material and Skia
        // siblings, so that backing would otherwise appear immediately in the
        // final rectangle underneath them.
        _ = MacOsQuickTerminalReveal.TryClearWindowBacking(this);
        var hasNativeChrome = MacOsQuickTerminalReveal.TrySetChromeMaterial(
            this,
            ChromeHeight,
            MacOsMaterial.Sidebar,
            isVisible: true);
        QuickTerminalStatusBackground.Opacity = hasNativeChrome ? 0 : 1;
        UpdateNativeAgentMaterial();
        _ = MacOsQuickTerminalReveal.TryKeepBackdropActive(this);
    }

    private void UpdateNativeAgentMaterial()
    {
        var viewModel = DataContext as QuickTerminalViewModel;
        var shouldShow = viewModel is
        {
            IsAgentPanelVisible: true,
            IsAgentPanelDocked: true,
        }
            && (Avalonia.Application.Current is not App app
                || app.HostAllowsAdvancedMaterials)
            && !_hostPreferences.ReducedTransparency
            && QuickTerminalPresentationPolicy.ShouldUseBlur(
                _settings,
                _hostPreferences);
        var arrangedWidth = QuickTerminalAgentSurface.Bounds.Width;
        var configuredWidth = QuickTerminalAgentSurface.Width;
        var width = double.IsFinite(arrangedWidth) && arrangedWidth > 0
            ? arrangedWidth
            : double.IsFinite(configuredWidth) && configuredWidth > 0
                ? configuredWidth
                : 352;
        var hasNativeAgent = MacOsQuickTerminalReveal.TrySetAgentMaterial(
            this,
            width,
            ChromeHeight,
            MacOsMaterial.Sidebar,
            viewModel?.IsAgentPanelOnLeft == true,
            shouldShow);
        QuickTerminalAgentSurface.Classes.Set(
            "nativeMaterial",
            shouldShow && hasNativeAgent);
    }

    private void ApplyTransparencyHint(IReadOnlyList<WindowTransparencyLevel> hint)
    {
        // Avalonia.Native 12.0.1 resets a repeated, already-satisfied hint to
        // opaque. Keep this setter idempotent so show/hide cycles cannot toggle
        // the native window between transparent and opaque modes.
        if (TransparencyLevelHint.SequenceEqual(hint))
        {
            return;
        }

        TransparencyLevelHint = hint;
    }

    /// <summary>
    /// The smallest useful terminal surface. The controller combines this with
    /// the configured monitor-relative minimum before enabling the resize grip.
    /// </summary>
    public const double MinimumRevealHeight = 320;

    /// <summary>
    /// Places the fully revealed panel and records its off-screen position.
    /// The native window retains its full size at both positions, so moving it
    /// never asks the terminal or the backdrop to reflow.
    /// </summary>
    public void PlaceAt(PixelPoint topLeft, double scaling)
    {
        if (!double.IsFinite(scaling) || scaling <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scaling));
        }

        var hiddenOffset = checked((int)Math.Ceiling(Math.Max(1, Height) * scaling));
        _placementScale = scaling;
        _shownPosition = topLeft;
        _hiddenPosition = new PixelPoint(topLeft.X, topLeft.Y - hiddenOffset);
        _hasPlacement = true;
        Position = topLeft;
        if (IsVisible)
        {
            ApplyRevealPosition(_preparedRevealProgress);
        }
    }

    public void PrepareReveal(double progress)
    {
        _revealToken++;
        _preparedRevealProgress = Math.Clamp(progress, 0, 1);
        if (IsVisible)
        {
            ApplyRevealPosition(_preparedRevealProgress);
            return;
        }

        // Showing invisibly at the final anchor lets the platform choose the
        // correct display and create its material before the first visible
        // frame. CompletePreparedReveal moves it off-screen synchronously.
        Opacity = 0;
        if (_hasPlacement)
        {
            Position = _shownPosition;
        }
    }

    public void CompletePreparedReveal()
    {
        if (!IsVisible)
        {
            return;
        }

        ApplyRevealPosition(_preparedRevealProgress);
        Opacity = 1;
    }

    public void SetRevealProgress(double progress)
    {
        _revealToken++;
        _preparedRevealProgress = Math.Clamp(progress, 0, 1);
        ApplyRevealPosition(_preparedRevealProgress);
    }

    public void AnimateReveal(double from, double to, TimeSpan duration)
    {
        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(to, 0, 1);
        var token = ++_revealToken;
        _preparedRevealProgress = to;
        if (duration <= TimeSpan.Zero || !_hasPlacement)
        {
            ApplyRevealPosition(to);
            return;
        }

        if (MacOsQuickTerminalReveal.TryAnimate(this, from, to, duration))
        {
            return;
        }

        var start = TimeSpan.MinValue;
        void Frame(TimeSpan timestamp)
        {
            if (token != _revealToken)
            {
                return;
            }

            if (start == TimeSpan.MinValue)
            {
                start = timestamp;
            }

            var t = Math.Clamp(
                (timestamp - start).TotalMilliseconds / duration.TotalMilliseconds,
                0,
                1);
            PositionForProgress(from + ((to - from) * SlideEasing.Ease(t)));
            if (t < 1)
            {
                RequestAnimationFrame(Frame);
            }
        }

        PositionForProgress(from);
        RequestAnimationFrame(Frame);
    }

    private void ApplyRevealPosition(double progress)
    {
        if (!_hasPlacement)
        {
            return;
        }

        if (MacOsQuickTerminalReveal.TrySetProgress(this, progress))
        {
            return;
        }

        PositionForProgress(progress);
    }

    private void PositionForProgress(double progress)
    {
        var y = checked((int)Math.Round(
            _hiddenPosition.Y
            + ((_shownPosition.Y - _hiddenPosition.Y) * Math.Clamp(progress, 0, 1))));
        Position = new PixelPoint(_shownPosition.X, y);
    }

    public void FocusTerminal() =>
        Dispatcher.UIThread.Post(
            () => QuickTerminalHost.RequestInputFocus(),
            DispatcherPriority.Loaded);

    public bool TryCancelPendingInteraction() =>
        QuickTerminalHost.TryCancelPendingInteraction();

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        var action = QuickTerminalRuntimeRules.ResolveEscape(
            IsVisible,
            TryCancelPendingInteraction());
        if (action != QuickTerminalEscapeAction.Hide)
        {
            return;
        }

        RequestDismiss();
    }

    private void OnHideClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RequestDismiss();
    }

    private async void OnAddTabRequested(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is QuickTerminalViewModel viewModel)
        {
            await viewModel.AddTabAsync();
            FocusTerminal();
        }
    }

    private void OnActivateTabRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (DataContext is QuickTerminalViewModel viewModel
            && sender is Control { DataContext: QuickTerminalTabViewModel tab })
        {
            viewModel.ActivateTab(tab);
            FocusTerminal();
        }
    }

    private async void OnCloseTabRequested(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (DataContext is QuickTerminalViewModel viewModel
            && sender is Control { DataContext: QuickTerminalTabViewModel tab })
        {
            await viewModel.CloseTabAsync(tab);
            FocusTerminal();
        }
    }

    private async void OnConnectionSelected(
        object? sender,
        PanelConnectionSelectedEventArgs e)
    {
        _ = sender;
        if (DataContext is QuickTerminalViewModel viewModel
            && e.Selection is PanelConnectionOptionViewModel.Target.Connection connection)
        {
            await viewModel.SelectConnectionAsync(connection.Id);
            FocusTerminal();
        }
    }

    private void OnTabTitleEditRequested(
        object? sender,
        RuntimeTabTitleEditRequestedEventArgs e)
    {
        _ = sender;
        if (DataContext is not QuickTerminalViewModel viewModel
            || e.Tab is not QuickTerminalTabViewModel tab)
        {
            return;
        }

        viewModel.UpdateTabIdentity(tab, e.Title, tab.Icon);
    }

    private void OnTabIconEditRequested(
        object? sender,
        RuntimeTabIconEditRequestedEventArgs e)
    {
        _ = sender;
        if (DataContext is not QuickTerminalViewModel viewModel
            || e.Tab is not QuickTerminalTabViewModel tab)
        {
            return;
        }

        viewModel.UpdateTabIdentity(tab, tab.Title, e.Icon);
    }

    private void OnNewConnectionRequested(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NewConnectionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTabReorderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_tabReorder is not null
            || sender is not Control
            {
                DataContext: QuickTerminalTabViewModel tab,
            } source
            || DataContext is not QuickTerminalViewModel { Tabs.Count: > 1 }
            || !e.Pointer.IsPrimary)
        {
            return;
        }

        var point = e.GetCurrentPoint(source);
        if (!point.Properties.IsLeftButtonPressed
            && e.Pointer.Type != PointerType.Touch)
        {
            return;
        }

        _tabReorder = new QuickTerminalTabReorder(
            source,
            point.Position,
            e.Pointer,
            tab,
            IsDragging: false);
    }

    private void OnTabReorderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabReorder is not { } reorder
            || !ReferenceEquals(sender, reorder.Source)
            || !ReferenceEquals(e.Pointer, reorder.Pointer))
        {
            return;
        }

        var point = e.GetCurrentPoint(reorder.Source);
        if (!point.Properties.IsLeftButtonPressed
            && e.Pointer.Type != PointerType.Touch)
        {
            CancelTabReorder();
            return;
        }

        if (!reorder.IsDragging)
        {
            var delta = point.Position - reorder.Origin;
            if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
            {
                return;
            }

            reorder = reorder with { IsDragging = true };
            _tabReorder = reorder;
            reorder.Pointer.Capture(reorder.Source);
        }

        ShowTabDropTarget(ResolveTabDrop(e.GetPosition(this), reorder.Tab));
        e.Handled = true;
    }

    private void OnTabReorderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_tabReorder is not { } reorder
            || !ReferenceEquals(sender, reorder.Source)
            || !ReferenceEquals(e.Pointer, reorder.Pointer))
        {
            return;
        }

        if (!reorder.IsDragging)
        {
            _tabReorder = null;
            return;
        }

        var drop = ResolveTabDrop(e.GetPosition(this), reorder.Tab);
        _tabReorder = null;
        ClearTabDropTarget();
        reorder.Pointer.Capture(null);
        if (drop is not null && DataContext is QuickTerminalViewModel viewModel)
        {
            viewModel.MoveTab(reorder.Tab, drop.Value.Tab, drop.Value.PlaceAfter);
        }

        e.Handled = true;
    }

    private void OnTabReorderPointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs e)
    {
        _ = sender;
        if (_tabReorder is { } reorder && ReferenceEquals(e.Pointer, reorder.Pointer))
        {
            CancelTabReorder(releaseCapture: false);
        }
    }

    private QuickTerminalTabDrop? ResolveTabDrop(
        Point position,
        QuickTerminalTabViewModel sourceTab)
    {
        if (this.InputHitTest(position) is not Visual hit)
        {
            return null;
        }

        var target = hit is Grid grid
            && grid.Classes.Contains("RuntimeTabDropTarget")
                ? grid
                : hit.GetVisualAncestors()
                    .OfType<Grid>()
                    .FirstOrDefault(candidate =>
                        candidate.Classes.Contains("RuntimeTabDropTarget"));
        if (target?.DataContext is not QuickTerminalTabViewModel targetTab
            || ReferenceEquals(sourceTab, targetTab))
        {
            return null;
        }

        var targetPosition = position
            - target.TranslatePoint(default, this).GetValueOrDefault();
        return new QuickTerminalTabDrop(
            target,
            targetTab,
            targetPosition.X >= target.Bounds.Width / 2);
    }

    private void ShowTabDropTarget(QuickTerminalTabDrop? drop)
    {
        ClearTabDropTarget();
        if (drop is null)
        {
            return;
        }

        _tabDropTarget = drop.Value.Target;
        var placementClass = drop.Value.PlaceAfter ? "After" : "Before";
        foreach (var indicator in _tabDropTarget
                     .GetVisualDescendants()
                     .OfType<Border>()
                     .Where(border =>
                         border.Classes.Contains("RuntimeTabDropIndicator")))
        {
            indicator.IsVisible = indicator.Classes.Contains(placementClass);
        }
    }

    private void ClearTabDropTarget()
    {
        if (_tabDropTarget is null)
        {
            return;
        }

        foreach (var indicator in _tabDropTarget
                     .GetVisualDescendants()
                     .OfType<Border>()
                     .Where(border =>
                         border.Classes.Contains("RuntimeTabDropIndicator")))
        {
            indicator.IsVisible = false;
        }

        _tabDropTarget = null;
    }

    private void CancelTabReorder(bool releaseCapture = true)
    {
        var reorder = _tabReorder;
        _tabReorder = null;
        ClearTabDropTarget();
        if (releaseCapture)
        {
            reorder?.Pointer.Capture(null);
        }
    }

    private void OnResizeGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control grip
            || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isResizing = true;
        _resizeStartScreenY = this.PointToScreen(e.GetPosition(this)).Y;
        _resizeStartHeight = Height;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnResizeGripPointerMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
        if (!_isResizing)
        {
            return;
        }

        var screenY = this.PointToScreen(e.GetPosition(this)).Y;
        var nextHeight = _resizeStartHeight
            + ((screenY - _resizeStartScreenY) / _placementScale);
        Height = Math.Clamp(nextHeight, MinHeight, MaxHeight);
        if (_hasPlacement)
        {
            PlaceAt(_shownPosition, _placementScale);
        }

        e.Handled = true;
    }

    private void OnResizeGripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        if (!_isResizing)
        {
            return;
        }

        e.Pointer.Capture(null);
        CompleteResize();
        e.Handled = true;
    }

    private void OnResizeGripPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _ = sender;
        _ = e;
        CompleteResize();
    }

    private void CompleteResize()
    {
        if (!_isResizing)
        {
            return;
        }

        _isResizing = false;
        HeightResizeCompleted?.Invoke(
            this,
            new QuickTerminalHeightChangedEventArgs(Height));
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (QuickTerminalRuntimeRules.ShouldDismissForFocusLoss(
                IsVisible,
                HideOnFocusLoss))
        {
            RequestDismiss();
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _ = sender;
        if (_allowClose)
        {
            // Avalonia can ask every top-level to close again after the main
            // window's Closed handler has already closed Quick Terminal. Cancel
            // is idempotent; disposing here would make that forced pass throw.
            // Async event handlers also keep using this token while unwinding.
            _lifetime.Cancel();
            return;
        }

        e.Cancel = true;
        RequestDismiss();
    }

    private void RequestDismiss() => DismissRequested?.Invoke(this, EventArgs.Empty);
}

public sealed class QuickTerminalHeightChangedEventArgs(double height) : EventArgs
{
    public double Height { get; } = height;
}

internal readonly record struct QuickTerminalTabDrop(
    Grid Target,
    QuickTerminalTabViewModel Tab,
    bool PlaceAfter);

internal sealed record QuickTerminalTabReorder(
    Control Source,
    Point Origin,
    IPointer Pointer,
    QuickTerminalTabViewModel Tab,
    bool IsDragging);
