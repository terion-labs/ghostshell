using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Asura.App.Controls;

/// <summary>
/// Where a floated panel is drawn: over the workspace, inside the same window.
///
/// Not a window of its own, which is what Dock would have made. A panel can hold
/// an operating-system view, and such a view does not survive changing window —
/// the framework destroys it and builds an empty one — so a floated browser
/// arrived as a blank rectangle with a live session behind it. Here the panel
/// changes nothing but where it is drawn, and its surface follows the same way
/// it follows any other move.
/// </summary>
internal sealed class FloatingPanelLayer : ItemsControl
{
    /// <summary>
    /// The panel being dragged, and where in it the pointer took hold. Kept here
    /// rather than on the item, because the pointer leaves the item's bounds the
    /// moment the drag outruns the panel.
    /// </summary>
    private FloatingRuntimePanelViewModel? _dragging;
    private Point _grip;
    private bool _resizing;

    public FloatingPanelLayer()
    {
        // The layer is only its panels: everywhere else the workspace underneath
        // takes the pointer.
        Background = null;
        ClipToBounds = false;
    }

    /// <summary>
    /// Begins moving a floating panel, from its own header.
    ///
    /// The header is the panel's, not this layer's — a floating panel wears the
    /// same chrome as a docked one — so the handle in that header asks for this
    /// rather than the layer watching for presses it cannot attribute.
    /// </summary>
    internal void BeginMove(Visual source, PointerEventArgs e)
    {
        Begin(source, e, resizing: false);
    }

    internal void BeginResize(Visual source, PointerEventArgs e)
    {
        Begin(source, e, resizing: true);
    }

    private void Begin(Visual source, PointerEventArgs e, bool resizing)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(e);
        // The nearest presenter is one of the panel's own — its chrome is built
        // from several — and its data context is the panel, not the float. The
        // one being looked for is the one that knows where the panel is.
        if (source.GetSelfAndVisualAncestors()
                .OfType<StyledElement>()
                .Select(element => element.DataContext)
                .OfType<FloatingRuntimePanelViewModel>()
                .FirstOrDefault()
            is not { } panel)
        {
            return;
        }

        _dragging = panel;
        _resizing = resizing;
        var here = e.GetPosition(this);
        _grip = resizing
            ? new Point(here.X - panel.Width, here.Y - panel.Height)
            : new Point(here.X - panel.X, here.Y - panel.Y);
        e.Pointer.Capture(this);
    }

    /// <summary>
    /// The grip is declared in the item template, which has no code behind it to
    /// hand an event to. It is recognised on the way past instead.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Source is Control { Name: "PART_ResizeGrip" } grip
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResize(grip, e);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging is not { } panel)
        {
            return;
        }

        var here = e.GetPosition(this);
        if (_resizing)
        {
            panel.ResizeTo(here.X - _grip.X, here.Y - _grip.Y);
            return;
        }

        panel.MoveTo(here.X - _grip.X, here.Y - _grip.Y, Bounds.Size);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        End(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        End(pointer: null);
    }

    private void End(IPointer? pointer)
    {
        if (_dragging is null)
        {
            return;
        }

        _dragging = null;
        _resizing = false;
        pointer?.Capture(null);
    }

    /// <summary>
    /// The layer a control is floating in, or null where it is not floating.
    /// </summary>
    internal static FloatingPanelLayer? For(Visual visual) =>
        visual.FindAncestorOfType<FloatingPanelLayer>();
}
