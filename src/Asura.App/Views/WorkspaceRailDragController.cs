using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.Application;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asura.App.Views;

/// <summary>Window-owned pointer and keyboard ordering for the workspace rail.</summary>
internal sealed class WorkspaceRailDragController
{
    private readonly Control _root;

    public WorkspaceRailDragController(Control root)
    {
        _root = root;
        root.AddHandler(InputElement.PointerPressedEvent, OnWorkspaceSortPressed, RoutingStrategies.Tunnel);
        root.AddHandler(InputElement.PointerMovedEvent, OnWorkspaceSortMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerReleasedEvent, OnWorkspaceSortReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(InputElement.PointerCaptureLostEvent, OnWorkspaceSortCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(InputElement.KeyDownEvent, OnWorkspaceSortKeyDown, RoutingStrategies.Tunnel);
        root.DetachedFromVisualTree += (_, _) => CancelWorkspaceSort();
    }

    private WorkspaceRailTile? _sortSource;
    private WorkspaceRailTile? _sortTarget;
    private IPointer? _sortPointer;
    private Point _sortOrigin;
    private bool _sorting;
    private bool _sortAfter;

    private void OnWorkspaceSortPressed(object? sender, PointerPressedEventArgs e)
    {
        _ = sender;
        if (e.Source is not Visual visual
            || visual.FindAncestorOfType<WorkspaceRailTile>() is not { } tile
            || visual.FindAncestorOfType<Button>(includeSelf: true)?.Name is "PART_Close" or "PART_Save"
            || !e.Pointer.IsPrimary
            || !e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed
            || _root.DataContext is not MainWindowViewModel { Workspaces.Count: > 1 })
        {
            return;
        }
        CancelWorkspaceSort();
        _sortSource = tile;
        _sortPointer = e.Pointer;
        _sortOrigin = e.GetPosition(_root);
        // Leave the initial press alone so an ordinary click still opens the workspace.
    }

    private void OnWorkspaceSortMoved(object? sender, PointerEventArgs e)
    {
        _ = sender;
        if (_sortSource is not { } source || !ReferenceEquals(e.Pointer, _sortPointer))
        {
            return;
        }
        if (!e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed)
        {
            CancelWorkspaceSort();
            return;
        }
        var delta = e.GetPosition(_root) - _sortOrigin;
        if (!_sorting && Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }
        if (!_sorting)
        {
            e.Pointer.Capture(source);
            _sorting = true;
            source.Opacity = 0.55;
        }
        UpdateWorkspaceSortTarget(e);
        e.Handled = true;
    }

    private void UpdateWorkspaceSortTarget(PointerEventArgs e)
    {
        ClearWorkspaceSortTarget();
        if (_sortSource?.FindAncestorOfType<ItemsControl>() is not { } list)
        {
            return;
        }
        foreach (var tile in list.GetVisualDescendants().OfType<WorkspaceRailTile>())
        {
            var position = e.GetPosition(tile);
            if (ReferenceEquals(tile, _sortSource)
                || !tile.IsEffectivelyVisible
                || position.X < 0 || position.X > tile.Bounds.Width
                || position.Y < -5 || position.Y > tile.Bounds.Height + 5)
            {
                continue;
            }
            _sortTarget = tile;
            _sortAfter = position.Y >= tile.Bounds.Height / 2;
            tile.Classes.Set(_sortAfter ? "sortAfter" : "sortBefore", true);
            break;
        }
    }

    private async void OnWorkspaceSortReleased(object? sender, PointerReleasedEventArgs e)
    {
        _ = sender;
        if (!ReferenceEquals(e.Pointer, _sortPointer))
        {
            return;
        }
        var source = _sortSource?.DataContext as LauncherWorkspaceViewModel;
        if (_sorting)
        {
            UpdateWorkspaceSortTarget(e);
            e.Handled = true;
        }
        var target = _sortTarget?.DataContext as LauncherWorkspaceViewModel;
        var after = _sortAfter;
        CancelWorkspaceSort();
        if (source is not null && target is not null)
        {
            await SaveWorkspaceSortAsync(source, target, after);
        }
    }

    private void OnWorkspaceSortCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _ = sender;
        // The inner button loses capture when dragging takes it. Only loss
        // from the tile itself cancels an established drag.
        if (_sorting && ReferenceEquals(e.Source, _sortSource))
        {
            CancelWorkspaceSort();
        }
    }

    private async void OnWorkspaceSortKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Escape && _sortSource is not null)
        {
            CancelWorkspaceSort();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down)
            || e.Source is not Visual visual
            || visual.FindAncestorOfType<WorkspaceRailTile>()?.DataContext is not LauncherWorkspaceViewModel source
            || _root.DataContext is not MainWindowViewModel model)
        {
            return;
        }
        e.Handled = true;
        var destination = model.Workspaces.IndexOf(source) + (e.Key == Key.Up ? -1 : 1);
        if (destination >= 0 && destination < model.Workspaces.Count)
        {
            await SaveWorkspaceSortAsync(source, model.Workspaces[destination], e.Key == Key.Down);
            // Catalog replacement recreates the tiles. Restore keyboard focus
            // to the same workspace so repeated shortcuts keep moving it.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _root.UpdateLayout();
                _root.GetVisualDescendants().OfType<WorkspaceRailTile>()
                    .FirstOrDefault(tile => tile.DataContext is LauncherWorkspaceViewModel item && item.Id == source.Id)?
                    .GetVisualDescendants().OfType<Button>().FirstOrDefault(button => string.Equals(button.Name, "PART_Open", StringComparison.Ordinal))?.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    private async Task SaveWorkspaceSortAsync(
        LauncherWorkspaceViewModel source, LauncherWorkspaceViewModel target, bool after)
    {
        if (_root.DataContext is not MainWindowViewModel model)
        {
            return;
        }
        try
        {
            await model.MoveWorkspaceAsync(source.Id, target.Id, after, CancellationToken.None);
        }
        catch (Exception error)
        {
            SecretSafeDiagnosticProjection.WriteStandardError("workspace.reorder.failed", error);
        }
    }

    private void CancelWorkspaceSort()
    {
        var pointer = _sortPointer;
        var captured = _sorting;
        if (_sortSource is { } source)
        {
            source.Opacity = 1;
        }
        _sortSource = null;
        _sortPointer = null;
        _sorting = false;
        ClearWorkspaceSortTarget();
        if (captured)
        {
            pointer?.Capture(null);
        }
    }

    private void ClearWorkspaceSortTarget()
    {
        _sortTarget?.Classes.Set("sortBefore", false);
        _sortTarget?.Classes.Set("sortAfter", false);
        _sortTarget = null;
    }
}
