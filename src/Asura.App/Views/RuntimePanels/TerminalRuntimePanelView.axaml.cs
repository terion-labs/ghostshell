using Asura.App.Controls;
using Asura.App.ViewModels;
using Asura.App.Views.Components;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.RuntimePanels;

public sealed partial class TerminalRuntimePanelView : UserControl
{
    public TerminalRuntimePanelView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? CancelReconnectRequested;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<PanelConnectionSelectedEventArgs>? ConnectionSelected;

    public event EventHandler<RoutedEventArgs>? NewConnectionRequested;

    /// <summary>
    /// Splitting places an empty panel beside this one; what it becomes is chosen
    /// there rather than in a modal over the window.
    /// </summary>
    public event EventHandler<PanelSplitOrientation>? SplitRequested;

    public event EventHandler<RoutedEventArgs>? RetryConnectionRequested;

    public event EventHandler<TerminalSessionFailureEventArgs>? SessionInitializationFailed;

    public event EventHandler<TerminalSessionSnapshotEventArgs>? SessionSnapshotChanged;

    public event EventHandler<RoutedEventArgs>? TrustHostKeyRequested;

    private void OnCancelReconnectClick(object? sender, RoutedEventArgs e) =>
        CancelReconnectRequested?.Invoke(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnConnectionSelected(
        object? sender,
        PanelConnectionSelectedEventArgs e)
    {
        _ = sender;
        ConnectionSelected?.Invoke(this, e);
    }

    private void OnNewConnectionRequested(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        NewConnectionRequested?.Invoke(this, e);
    }

    private void OnSplitRequested(object? sender, PanelSplitOrientation orientation) =>
        SplitRequested?.Invoke(sender, orientation);

    private void OnRetryConnectionClick(object? sender, RoutedEventArgs e) =>
        RetryConnectionRequested?.Invoke(sender, e);

    private void OnSessionInitializationFailed(
        object? sender,
        TerminalSessionFailureEventArgs e) =>
        SessionInitializationFailed?.Invoke(sender, e);

    private void OnSessionSnapshotChanged(
        object? sender,
        TerminalSessionSnapshotEventArgs e) =>
        SessionSnapshotChanged?.Invoke(sender, e);

    private void OnTrustHostKeyClick(object? sender, RoutedEventArgs e) =>
        TrustHostKeyRequested?.Invoke(sender, e);
}
