using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views.Overlays;

public sealed partial class LayoutDesignerView : UserControl
{
    public LayoutDesignerView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? AddSlotRequested;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<RoutedEventArgs>? EditLayoutRequested;

    public event EventHandler<RoutedEventArgs>? RemoveSlotRequested;

    public event EventHandler<RoutedEventArgs>? ResetRequested;

    public event EventHandler<RoutedEventArgs>? SaveRequested;

    public event EventHandler<RoutedEventArgs>? SlotSelectedRequested;

    public event EventHandler<RoutedEventArgs>? SplitSlotDownRequested;

    public event EventHandler<RoutedEventArgs>? SplitSlotRightRequested;

    internal void FocusNameEditor() =>
        NewLayoutName.Focus(NavigationMethod.Tab);

    private void OnLayoutAddSlotClick(object? sender, RoutedEventArgs e) =>
        AddSlotRequested?.Invoke(sender, e);

    private void OnCloseOverlayClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnEditLayoutClick(object? sender, RoutedEventArgs e) =>
        EditLayoutRequested?.Invoke(sender, e);

    private void OnLayoutSlotClick(object? sender, RoutedEventArgs e) =>
        SlotSelectedRequested?.Invoke(sender, e);

    private void OnResetLayoutClick(object? sender, RoutedEventArgs e) =>
        ResetRequested?.Invoke(sender, e);

    private void OnSaveLayoutDesignerClick(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(sender, e);

    private void OnLayoutSlotPointerPressed(object? sender, PointerPressedEventArgs e) =>
        SlotSelectedRequested?.Invoke(sender, e);

    private void OnLayoutRemoveSlotClick(object? sender, RoutedEventArgs e) =>
        RemoveSlotRequested?.Invoke(sender, e);

    private void OnLayoutSplitSlotDownClick(object? sender, RoutedEventArgs e) =>
        SplitSlotDownRequested?.Invoke(sender, e);

    private void OnLayoutSplitSlotRightClick(object? sender, RoutedEventArgs e) =>
        SplitSlotRightRequested?.Invoke(sender, e);
}
