using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Asura.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentIcons.Common;

namespace Asura.App.Views;

internal sealed record RuntimeTabDragPresentation(
    Action<DragGhostPayload, Point> ShowGhost,
    Action<Point> MoveGhost,
    Action HideGhost);

/// <summary>
/// Owns the complete pointer/capture/drop lifecycle for runtime-tab reordering.
/// The window remains the visual root and lends the controller its lifetime.
/// </summary>
internal sealed class RuntimeTabDragController(
    Window window,
    MainWindowViewModel viewModel,
    RuntimeTabDragPresentation presentation,
    CancellationToken lifetime)
{
    private const double DragThreshold = 6;
    private static readonly DataFormat<RuntimeTabDragPayload> DragFormat =
        DataFormat.CreateInProcessFormat<RuntimeTabDragPayload>(
            "sh.asura.runtime-tab");

    private RuntimeTabActiveDrag? _activeDrag;
    private RuntimeTabDragCandidate? _candidate;
    private Grid? _dropTarget;
    private bool _dragInProgress;

    public void PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dragInProgress
            || sender is not Control
            {
                DataContext: RuntimeTabViewModel tab,
            } source
            || viewModel.RuntimeWorkspace is not { } workspace
            || workspace.Tabs.Count < 2
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

        _candidate = new RuntimeTabDragCandidate(
            source,
            point.Position,
            e.Pointer,
            new RuntimeTabDragPayload(
                viewModel.WindowId,
                workspace.Id,
                tab.Id,
                tab.Title));
    }

    public void PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeDrag is { } active
            && ReferenceEquals(sender, active.Source)
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            var current = e.GetCurrentPoint(active.Source);
            if (!current.Properties.IsLeftButtonPressed
                && e.Pointer.Type != PointerType.Touch)
            {
                Cancel(active.Pointer);
                return;
            }

            Update(e, active);
            e.Handled = true;
            return;
        }

        if (_candidate is not { } candidate
            || !ReferenceEquals(sender, candidate.Source)
            || !ReferenceEquals(e.Pointer, candidate.Pointer))
        {
            return;
        }

        var point = e.GetCurrentPoint(candidate.Source);
        if (!point.Properties.IsLeftButtonPressed
            && e.Pointer.Type != PointerType.Touch)
        {
            _candidate = null;
            return;
        }

        var delta = point.Position - candidate.Origin;
        if (Math.Abs(delta.X) < DragThreshold
            && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        _candidate = null;
        _dragInProgress = true;
        e.Handled = true;
        var activeDrag = new RuntimeTabActiveDrag(
            candidate.Source,
            candidate.Pointer,
            candidate.Payload);
        candidate.Pointer.Capture(candidate.Source);
        _activeDrag = activeDrag;
        presentation.ShowGhost(
            new DragGhostPayload(
                Symbol.WindowConsole,
                candidate.Payload.Title,
                "Move tab"),
            e.GetPosition(window));
        Update(e, activeDrag);
    }

    public async Task PointerReleasedAsync(
        object? sender,
        PointerReleasedEventArgs e)
    {
        if (_activeDrag is { } active
            && ReferenceEquals(sender, active.Source)
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            await CompleteAsync(e, active);
            e.Handled = true;
            return;
        }

        _candidate = null;
    }

    public void PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _ = sender;
        if (_activeDrag is { } active
            && ReferenceEquals(e.Pointer, active.Pointer))
        {
            Cancel(active.Pointer, releaseCapture: false);
            return;
        }

        _candidate = null;
    }

    public void DragEnter(object? sender, DragEventArgs e) =>
        UpdateDropTarget(sender, e);

    public void DragOver(object? sender, DragEventArgs e) =>
        UpdateDropTarget(sender, e);

    public void DragLeave(object? sender, DragEventArgs e)
    {
        _ = e;
        if (ReferenceEquals(sender, _dropTarget))
        {
            ClearDropIndicator();
        }
    }

    public async Task DropAsync(object? sender, DragEventArgs e)
    {
        if (!TryResolveDrop(
                sender,
                e,
                out var payload,
                out var anchorTabId,
                out var placement))
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropIndicator();
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        ClearDropIndicator();
        await MoveTabAsync(payload.TabId, anchorTabId, placement);
    }

    private void Update(PointerEventArgs e, RuntimeTabActiveDrag active)
    {
        var position = e.GetPosition(window);
        presentation.MoveGhost(position);
        if (ResolveDrop(position, active.Payload) is { } target)
        {
            ShowDropIndicator(target.Target, target.Placement);
        }
        else
        {
            ClearDropIndicator();
        }
    }

    private async Task CompleteAsync(
        PointerReleasedEventArgs e,
        RuntimeTabActiveDrag active)
    {
        var target = ResolveDrop(e.GetPosition(window), active.Payload);
        ClearState();
        active.Pointer.Capture(null);
        presentation.HideGhost();
        if (target is not null)
        {
            await MoveTabAsync(
                active.Payload.TabId,
                target.AnchorTabId,
                target.Placement);
        }
    }

    private void Cancel(IPointer pointer, bool releaseCapture = true)
    {
        ClearState();
        if (releaseCapture)
        {
            pointer.Capture(null);
        }

        presentation.HideGhost();
    }

    private void ClearState()
    {
        _activeDrag = null;
        _candidate = null;
        _dragInProgress = false;
        ClearDropIndicator();
    }

    private async Task MoveTabAsync(
        TabInstanceId tabId,
        TabInstanceId anchorTabId,
        RuntimeTabPlacement placement)
    {
        try
        {
            if (await viewModel.MoveTabAsync(
                tabId,
                anchorTabId,
                placement,
                lifetime))
            {
                FocusTabButton(tabId);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException)
        {
            viewModel.SetError(exception.Message);
        }
    }

    private RuntimeTabDropTarget? ResolveDrop(
        Point position,
        RuntimeTabDragPayload payload)
    {
        if (window.InputHitTest(position) is not Visual hit)
        {
            return null;
        }

        var target = hit is Grid grid
            && grid.Classes.Contains("RuntimeTabDropTarget")
                ? grid
                : hit.GetVisualAncestors()
                    .OfType<Grid>()
                    .FirstOrDefault(control =>
                        control.Classes.Contains("RuntimeTabDropTarget"));
        if (target is null
            || !TryResolveDrop(
                target,
                payload,
                position - target.TranslatePoint(default, window).GetValueOrDefault(),
                out var anchorTabId,
                out var placement))
        {
            return null;
        }

        return new RuntimeTabDropTarget(target, anchorTabId, placement);
    }

    private void UpdateDropTarget(object? sender, DragEventArgs e)
    {
        if (!TryResolveDrop(sender, e, out _, out _, out var placement)
            || sender is not Grid target)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropIndicator();
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        ShowDropIndicator(target, placement);
    }

    private bool TryResolveDrop(
        object? sender,
        DragEventArgs e,
        out RuntimeTabDragPayload payload,
        out TabInstanceId anchorTabId,
        out RuntimeTabPlacement placement)
    {
        payload = null!;
        anchorTabId = default;
        placement = default;
        if (sender is not Grid
            {
                DataContext: RuntimeTabViewModel,
            } target
            || e.DataTransfer.TryGetValue(DragFormat) is not { } candidate
            || !TryResolveDrop(
                target,
                candidate,
                e.GetPosition(target),
                out anchorTabId,
                out placement))
        {
            return false;
        }

        payload = candidate;
        return true;
    }

    private bool TryResolveDrop(
        Grid target,
        RuntimeTabDragPayload candidate,
        Point targetPosition,
        out TabInstanceId anchorTabId,
        out RuntimeTabPlacement placement)
    {
        anchorTabId = default;
        placement = default;
        if (target.DataContext is not RuntimeTabViewModel targetTab
            || viewModel.RuntimeWorkspace is not { } workspace
            || candidate.WindowId != viewModel.WindowId
            || candidate.WorkspaceId != workspace.Id
            || candidate.TabId == targetTab.Id
            || workspace.Tabs.All(tab => tab.Id != candidate.TabId))
        {
            return false;
        }

        placement = targetPosition.X < target.Bounds.Width / 2
            ? RuntimeTabPlacement.Before
            : RuntimeTabPlacement.After;
        if (!WouldMove(workspace, candidate.TabId, targetTab.Id, placement))
        {
            return false;
        }

        anchorTabId = targetTab.Id;
        return true;
    }

    private static bool WouldMove(
        RuntimeWorkspaceViewModel workspace,
        TabInstanceId sourceTabId,
        TabInstanceId anchorTabId,
        RuntimeTabPlacement placement)
    {
        var source = workspace.Tabs.SingleOrDefault(tab => tab.Id == sourceTabId);
        var anchor = workspace.Tabs.SingleOrDefault(tab => tab.Id == anchorTabId);
        if (source is null || anchor is null)
        {
            return false;
        }

        var sourceIndex = workspace.Tabs.IndexOf(source);
        var anchorIndex = workspace.Tabs.IndexOf(anchor);
        var destinationIndex = placement == RuntimeTabPlacement.Before
            ? anchorIndex
            : anchorIndex + 1;
        if (sourceIndex < destinationIndex)
        {
            destinationIndex--;
        }

        return sourceIndex != destinationIndex;
    }

    private void ShowDropIndicator(Grid target, RuntimeTabPlacement placement)
    {
        ClearDropIndicator();
        _dropTarget = target;
        foreach (var indicator in target.Children
                     .OfType<Border>()
                     .Where(control =>
                         control.Classes.Contains("RuntimeTabDropIndicator")))
        {
            indicator.IsVisible = placement == RuntimeTabPlacement.Before
                ? indicator.Classes.Contains("Before")
                : indicator.Classes.Contains("After");
        }
    }

    private void ClearDropIndicator()
    {
        if (_dropTarget is { } target)
        {
            foreach (var indicator in target.Children
                         .OfType<Border>()
                         .Where(control =>
                             control.Classes.Contains("RuntimeTabDropIndicator")))
            {
                indicator.IsVisible = false;
            }
        }

        _dropTarget = null;
    }

    private void FocusTabButton(TabInstanceId tabId) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (lifetime.IsCancellationRequested)
            {
                return;
            }

            var button = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(control =>
                    control.Classes.Contains("RuntimeTabActivator")
                    && control.DataContext is RuntimeTabViewModel tab
                    && tab.Id == tabId);
            button?.BringIntoView();
            button?.Focus(NavigationMethod.Pointer);
        });

    private sealed record RuntimeTabDragPayload(
        WindowInstanceId WindowId,
        WorkspaceInstanceId WorkspaceId,
        TabInstanceId TabId,
        string Title);

    private sealed record RuntimeTabActiveDrag(
        Control Source,
        IPointer Pointer,
        RuntimeTabDragPayload Payload);

    private sealed record RuntimeTabDragCandidate(
        Control Source,
        Point Origin,
        IPointer Pointer,
        RuntimeTabDragPayload Payload);

    private sealed record RuntimeTabDropTarget(
        Grid Target,
        TabInstanceId AnchorTabId,
        RuntimeTabPlacement Placement);
}
