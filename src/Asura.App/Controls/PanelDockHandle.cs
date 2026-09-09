using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Settings;

namespace Asura.App.Controls;

/// <summary>
/// Uses a panel's existing title as Dock's drag surface, avoiding a second tab
/// strip above content that already owns complete panel chrome.
/// </summary>
public sealed class PanelDockHandle : ContentControl
{
    public PanelDockHandle()
    {
        // The title glyphs are usually narrower than the header column that owns
        // them. Keep the complete column draggable instead of requiring a grab
        // directly on the text.
        Background = Brushes.Transparent;
        SetValue(DockProperties.IsDragAreaProperty, true);
        SetValue(DockProperties.IsDragEnabledProperty, true);
        AutomationProperties.SetName(this, "Rearrange panel");
        ToolTip.SetTip(this, "Drag to rearrange");
        Loaded += OnLoaded;
    }

    /// <summary>
    /// How far the pointer travels before a press counts as a drag. Below it a
    /// click on a title is just a click, and the surfaces should not blink.
    /// </summary>
    private const double DragThreshold = 4;

    private IDisposable? _dockableBinding;
    private IDisposable? _surfaceSuspension;
    private TopLevel? _releaseWatch;
    private Point? _pressOrigin;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        MarkRenderedSurface();
    }

    /// <summary>
    /// Points this handle at the dockable it belongs to.
    ///
    /// Dock reads the drag source's own data context to decide what is being
    /// dragged, so a drag surface whose context is something else is not a drag
    /// surface at all — the pointer goes down on the title and nothing happens.
    /// Each panel view used to set this by hand. It cannot: the handle is
    /// declared inside a control template now, and a template does not know where
    /// it will be used. So the handle finds the dockable and follows it.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _dockableBinding?.Dispose();
        _dockableBinding = this.FindAncestorOfType<DockableControl>() is { } host
            ? Bind(DataContextProperty, host.GetObservable(DataContextProperty))
            : null;
        MarkRenderedSurface();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _dockableBinding?.Dispose();
        _dockableBinding = null;
        EndDrag();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// While a panel is being dragged, the shell's native surfaces stand down.
    ///
    /// Dock's placement targets are drawn by the shell, and a native view is
    /// composited above everything the shell draws — so dragging a panel over a
    /// browser showed no targets at all, and there was no way to drop anything
    /// there. There is no z-order to fix that with; the surfaces simply leave for
    /// the length of the drag and come back untouched, which they can now do
    /// because hiding one no longer costs it its page.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // A floating panel has no place in the layout to be dragged to, so the
        // same grab moves it instead. One gesture, two meanings, decided by where
        // the panel is rather than by which control was built.
        if (FloatingPanelLayer.For(this) is { } floating)
        {
            floating.BeginMove(this, e);
            e.Handled = true;
            return;
        }

        _pressOrigin = e.GetPosition(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_surfaceSuspension is not null || _pressOrigin is not { } origin)
        {
            return;
        }

        var moved = e.GetPosition(this) - origin;
        if (Math.Abs(moved.X) <= DragThreshold && Math.Abs(moved.Y) <= DragThreshold)
        {
            return;
        }

        _surfaceSuspension = NativeSurfaceLayer.Suspend();
        // Dock captures the pointer for the length of a drag, so the release may
        // never reach this handle. The window sees it either way, and a
        // suspension nobody ends is a browser nobody gets back.
        _releaseWatch = TopLevel.GetTopLevel(this);
        _releaseWatch?.AddHandler(
            PointerReleasedEvent,
            OnAnyPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void OnAnyPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        _ = e;
        EndDrag();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        _pressOrigin = null;
        _releaseWatch?.RemoveHandler(PointerReleasedEvent, OnAnyPointerReleased);
        _releaseWatch = null;
        _surfaceSuspension?.Dispose();
        _surfaceSuspension = null;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        // A ContentControl's presenter and content may be attached after its
        // template has been applied (notably while restoring Dock windows).
        // Mark the completed visual tree as well as the template-time tree.
        MarkRenderedSurface();
    }

    private void MarkRenderedSurface()
    {
        // Dock resolves a drag source from the exact visuals returned by its hit
        // test rather than walking to an ancestor. Every rendered layer of the
        // title therefore needs to opt into the same drag surface.
        SetDragArea(this);
        foreach (var control in this.GetVisualDescendants().OfType<Control>())
        {
            SetDragArea(control);
        }
    }

    private static void SetDragArea(Control control)
    {
        control.SetValue(DockProperties.IsDragAreaProperty, true);
        control.SetValue(DockProperties.IsDragEnabledProperty, true);
    }

}
