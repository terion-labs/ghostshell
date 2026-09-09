using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Asura.App.Views;

public sealed partial class RecoveryDataControlView : UserControl
{
    public RecoveryDataControlView() => InitializeComponent();

    private RecoveryDataControlViewModel? ViewModel =>
        DataContext as RecoveryDataControlViewModel;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.RefreshAsync();
        }
    }

    private async void OnClearRunClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: RecoveryRunItemViewModel item }
            || ViewModel is not { CanClearRuns: true } viewModel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var confirmed = await Confirmations.RecoveryDataClear(
                "Clear this saved recovery?",
                $"{item.SnapshotLabel} from one previous run will be permanently removed.",
                "Clear recovery")
            .ShowDialog<bool>(owner);
        if (confirmed)
        {
            await viewModel.DiscardRunAsync(item);
            RestoreRefreshFocus();
        }
    }

    private async void OnClearAllClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is not { CanClearAll: true } viewModel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var confirmed = await Confirmations.RecoveryDataClear().ShowDialog<bool>(owner);
        if (confirmed)
        {
            await viewModel.DiscardAllAsync();
            RestoreRefreshFocus();
        }
    }

    private void RestoreRefreshFocus() =>
        Dispatcher.UIThread.Post(() =>
            this.FindControl<Button>("RefreshRecoveryDataButton")
                ?.Focus(NavigationMethod.Tab));

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
