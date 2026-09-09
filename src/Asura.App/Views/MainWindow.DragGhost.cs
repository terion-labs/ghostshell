using Asura.App.Views.Components;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Asura.App.Views;

public sealed partial class MainWindow
{
    private const double DragGhostPointerOffset = 14;
    private const double DragGhostWindowInset = 8;
    private const double DragGhostOpacity = 0.96;
    private TranslateTransform? _dragGhostTransform;
    private Canvas? _resolvedDragGhostLayer;
    private Control? _resolvedDragGhostPresenter;

    internal void ShowDragGhost(DragGhostPayload payload, Point position)
    {
        if (ResolveDragGhostLayer() is not { } layer
            || ResolveDragGhostPresenter() is not { } presenter)
        {
            return;
        }

        presenter.DataContext = payload;
        presenter.Measure(Size.Infinity);
        _dragGhostTransform ??= new TranslateTransform();
        presenter.RenderTransform = _dragGhostTransform;
        presenter.Opacity = DragGhostOpacity;
        MoveDragGhost(position);
        presenter.InvalidateVisual();
    }

    internal void MoveDragGhost(Point position)
    {
        if (ResolveDragGhostLayer() is not { } layer
            || ResolveDragGhostPresenter() is not { Opacity: > 0 } presenter
            || _dragGhostTransform is not { } transform)
        {
            return;
        }

        var ghostSize = presenter.DesiredSize;
        var left = Math.Clamp(
            position.X + DragGhostPointerOffset,
            DragGhostWindowInset,
            Math.Max(
                DragGhostWindowInset,
                layer.Bounds.Width - ghostSize.Width - DragGhostWindowInset));
        var top = Math.Clamp(
            position.Y + DragGhostPointerOffset,
            DragGhostWindowInset,
            Math.Max(
                DragGhostWindowInset,
                layer.Bounds.Height - ghostSize.Height - DragGhostWindowInset));

        transform.X = left;
        transform.Y = top;
    }

    internal void HideDragGhost()
    {
        // An awaited or captured-pointer drag can finish after Avalonia has
        // already cleared generated XAML fields during window teardown.
        if (ResolveDragGhostPresenter() is not { } presenter)
        {
            return;
        }

        presenter.Opacity = 0;
        presenter.DataContext = null;
    }

    private Canvas? ResolveDragGhostLayer() =>
        _resolvedDragGhostLayer ??=
            DragGhostLayer ?? this.FindControl<Canvas>("DragGhostLayer");

    private Control? ResolveDragGhostPresenter() =>
        _resolvedDragGhostPresenter ??=
            DragGhostPresenter ?? this.FindControl<Control>("DragGhostPresenter");
}
