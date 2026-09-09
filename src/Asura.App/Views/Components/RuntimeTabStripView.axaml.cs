using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Asura.App.Views.Components;

/// <summary>
/// The open runtime tabs. The shell hosts this at whichever edge the appearance
/// profile selects, so the strip is one control with one template rather than a
/// copy per edge. Every interaction is re-raised for the shell to handle, which
/// keeps tab activation, closing, and reordering owned in one place.
/// </summary>
public sealed partial class RuntimeTabStripView : UserControl
{
    private Control? _reorderPointerSource;
    private IPointer? _reorderPointer;

    public static readonly StyledProperty<IEnumerable?> TabsProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, IEnumerable?>(nameof(Tabs));

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, Orientation>(
            nameof(Orientation),
            Orientation.Horizontal);

    public static readonly StyledProperty<PlacementMode> IconPickerPlacementProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, PlacementMode>(
            nameof(IconPickerPlacement),
            PlacementMode.BottomEdgeAlignedLeft);

    /// <summary>
    /// Whether an overflowing strip draws a hairline after its add button.
    /// Only the title-bar strip wants it — that is the one place something
    /// else sits immediately beyond the strip's end.
    /// </summary>
    public static readonly StyledProperty<bool> ShowsOverflowSeparatorProperty =
        AvaloniaProperty.Register<RuntimeTabStripView, bool>(nameof(ShowsOverflowSeparator));

    public bool ShowsOverflowSeparator
    {
        get => GetValue(ShowsOverflowSeparatorProperty);
        set => SetValue(ShowsOverflowSeparatorProperty, value);
    }

    public RuntimeTabStripView()
    {
        InitializeComponent();
        SyncScrollBars();
        SyncAddButtonDock();
        AddHandler(
            PointerPressedEvent,
            OnTabPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerMovedEvent,
            OnTabPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerReleasedEvent,
            OnTabPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerCaptureLostEvent,
            OnTabPointerCaptureLost,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        SizeChanged += (_, _) => UpdateOverflowPresentation();
    }

    public IEnumerable? Tabs
    {
        get => GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    /// <summary>
    /// Horizontal for a strip along the top or bottom, vertical for one docked to
    /// a side. The scroll bars follow, so the strip only ever scrolls along the
    /// axis it actually grows on.
    /// </summary>
    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>
    /// The edge the icon picker opens toward. The host knows which viewport
    /// edge owns this strip; orientation alone cannot distinguish top from
    /// bottom or left from right.
    /// </summary>
    public PlacementMode IconPickerPlacement
    {
        get => GetValue(IconPickerPlacementProperty);
        set => SetValue(IconPickerPlacementProperty, value);
    }

    /// <summary>
    /// Hidden along the strip's axis (overflow shows as an edge fade, not a
    /// bar), Disabled across it. Assigned directly rather than bound: the
    /// consumer sets Orientation after this view's own bindings have read
    /// their value, and the refresh never reached them — which left a
    /// side-docked strip free to grow 13px wider than its viewport and clip
    /// its own close buttons.
    /// </summary>
    private void SyncScrollBars()
    {
        if (TabScrollViewer is null)
        {
            return;
        }

        var horizontal = Orientation == Orientation.Horizontal;
        TabScrollViewer.HorizontalScrollBarVisibility = horizontal
            ? ScrollBarVisibility.Hidden
            : ScrollBarVisibility.Disabled;
        TabScrollViewer.VerticalScrollBarVisibility = horizontal
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Hidden;
    }

    /// <summary>Raised when the strip's own add-a-tab control is pressed.</summary>
    public event EventHandler<RoutedEventArgs>? AddTabRequested;

    public event EventHandler<RoutedEventArgs>? ActivateRequested;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<DragEventArgs>? TabDragEnter;

    public event EventHandler<DragEventArgs>? TabDragLeave;

    public event EventHandler<DragEventArgs>? TabDragOver;

    public event EventHandler<DragEventArgs>? TabDrop;

    public event EventHandler<PointerCaptureLostEventArgs>? ReorderPointerCaptureLost;

    public event EventHandler<PointerEventArgs>? ReorderPointerMoved;

    public event EventHandler<PointerPressedEventArgs>? ReorderPointerPressed;

    public event EventHandler<PointerReleasedEventArgs>? ReorderPointerReleased;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OrientationProperty)
        {
            SyncScrollBars();
            SyncAddButtonDock();
            UpdateOverflowPresentation();
        }
        else if (change.Property == ShowsOverflowSeparatorProperty)
        {
            UpdateOverflowPresentation();
        }
        else if (change.Property == TabsProperty)
        {
            ObserveTabs(change.NewValue as IEnumerable);
        }
    }

    /// <summary>The add button sits past the strip's growing end, outside the scroll.</summary>
    private void SyncAddButtonDock() =>
        DockPanel.SetDock(
            AddTabButton,
            Orientation == Orientation.Horizontal
                ? Avalonia.Controls.Dock.Right
                : Avalonia.Controls.Dock.Bottom);

    private void RaisePropertyChanged(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    public new event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnAddTab(object? sender, RoutedEventArgs e) =>
        AddTabRequested?.Invoke(sender, e);

    private void OnActivate(object? sender, RoutedEventArgs e) =>
        ActivateRequested?.Invoke(sender, e);

    private void OnClose(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnDragEnter(object? sender, DragEventArgs e) =>
        TabDragEnter?.Invoke(sender, e);

    private void OnDragLeave(object? sender, DragEventArgs e) =>
        TabDragLeave?.Invoke(sender, e);

    private void OnDragOver(object? sender, DragEventArgs e) =>
        TabDragOver?.Invoke(sender, e);

    private void OnDrop(object? sender, DragEventArgs e) =>
        TabDrop?.Invoke(sender, e);

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        if (!e.Pointer.IsPrimary || FindTabActivator(e.Source) is not { } source)
        {
            return;
        }

        _reorderPointerSource = source;
        _reorderPointer = e.Pointer;
        ReorderPointerPressed?.Invoke(source, e);
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
        if (_reorderPointerSource is { } source
            && ReferenceEquals(e.Pointer, _reorderPointer))
        {
            ReorderPointerMoved?.Invoke(source, e);
        }
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        if (_reorderPointerSource is not { } source
            || !ReferenceEquals(e.Pointer, _reorderPointer))
        {
            return;
        }

        ReorderPointerReleased?.Invoke(source, e);
        _reorderPointerSource = null;
        _reorderPointer = null;
    }

    private void OnTabPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _ = sender;
        if (_reorderPointerSource is not { } source
            || !ReferenceEquals(e.Pointer, _reorderPointer))
        {
            return;
        }

        ReorderPointerCaptureLost?.Invoke(source, e);
        _reorderPointerSource = null;
        _reorderPointer = null;
    }

    private static Control? FindTabActivator(object? eventSource)
    {
        if (eventSource is Control control
            && control.Classes.Contains("RuntimeTabActivator"))
        {
            return control;
        }

        return (eventSource as Visual)?.GetVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(candidate => candidate.Classes.Contains("RuntimeTabActivator"));
    }
}
