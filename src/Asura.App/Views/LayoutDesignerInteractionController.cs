using Asura.App.ViewModels;
using Avalonia.Controls;

namespace Asura.App.Views;

/// <summary>
/// Maps layout-designer pointer and keyboard activations to the same typed
/// editor mutations. The overlay and window only forward interaction events.
/// </summary>
internal sealed class LayoutDesignerInteractionController(
    MainWindowViewModel viewModel)
{
    public void SelectSlot(object? source)
    {
        if (source is Control { DataContext: LayoutDesignerSlotViewModel slot })
        {
            _ = viewModel.LayoutDesignerEditor?.SelectSlot(slot.Id);
        }
    }

    public void SplitSlotRight(object? source) =>
        SplitSlot(source, LayoutDesignerSplitDirection.Right);

    public void SplitSlotDown(object? source) =>
        SplitSlot(source, LayoutDesignerSplitDirection.Down);

    public void AddSlot() =>
        _ = viewModel.LayoutDesignerEditor?.AddSlot();

    public void RemoveSlot(object? source)
    {
        // Pointer activation carries the slot. Keyboard invocation may come
        // from the command surface, in which case the current selection owns it.
        _ = source is Control { DataContext: LayoutDesignerSlotViewModel slot }
            ? viewModel.LayoutDesignerEditor?.RemoveSlot(slot.Id)
            : viewModel.LayoutDesignerEditor?.RemoveSelectedSlot();
    }

    public void Reset() =>
        viewModel.LayoutDesignerEditor?.Reset();

    private void SplitSlot(
        object? source,
        LayoutDesignerSplitDirection direction)
    {
        if (source is Control { DataContext: LayoutDesignerSlotViewModel slot })
        {
            _ = viewModel.LayoutDesignerEditor?.SplitSlot(slot.Id, direction);
        }
    }
}
