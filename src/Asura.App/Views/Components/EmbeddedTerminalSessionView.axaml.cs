using Asura.App.Controls;
using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

public sealed partial class EmbeddedTerminalSessionView : UserControl
{
    public EmbeddedTerminalSessionView() => InitializeComponent();

    public event EventHandler<RoutedEventArgs>? TrustHostKeyRequested;

    private TerminalRuntimePanelViewModel? ViewModel =>
        DataContext as TerminalRuntimePanelViewModel;

    private void OnCancelReconnectClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel?.CancelConnection();
    }

    private async void OnRetryConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { CanRetry: true } panel)
        {
            await panel.RetryAsync();
        }
    }

    private void OnSessionInitializationFailed(
        object? sender,
        TerminalSessionFailureEventArgs e)
    {
        _ = sender;
        ViewModel?.ObserveSessionInitializationFailure(e.Failure);
    }

    private void OnSessionSnapshotChanged(
        object? sender,
        TerminalSessionSnapshotEventArgs e)
    {
        _ = sender;
        ViewModel?.ObserveSessionSnapshot(e.Snapshot);
    }

    private void OnTrustHostKeyClick(object? sender, RoutedEventArgs e) =>
        TrustHostKeyRequested?.Invoke(this, e);
}
