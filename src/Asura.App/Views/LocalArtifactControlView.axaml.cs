using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Asura.App.Views;

public sealed partial class LocalArtifactControlView : UserControl
{
    public LocalArtifactControlView() => InitializeComponent();

    private LocalArtifactControlViewModel? ViewModel =>
        DataContext as LocalArtifactControlViewModel;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.RefreshAsync();
        }
    }

    private async void OnClearClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: LocalArtifactItemViewModel item }
            || !item.HasFiles
            || ViewModel is not { CanClearItems: true } viewModel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var confirmed = await Confirmations.LocalArtifactClear(item)
            .ShowDialog<bool>(owner);
        if (confirmed)
        {
            await viewModel.ClearAsync(item);
            RestoreRefreshFocus();
        }
    }

    private void RestoreRefreshFocus() =>
        Dispatcher.UIThread.Post(() =>
            this.FindControl<Button>("RefreshLocalArtifactsButton")
                ?.Focus(NavigationMethod.Tab));

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
