using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.SettingsPages;

public sealed partial class NetworkingSettingsPageView : UserControl
{
    public NetworkingSettingsPageView()
    {
        InitializeComponent();
    }

    private NetworkSettingsViewModel? ViewModel => DataContext as NetworkSettingsViewModel;

    private async void OnAddProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            viewModel.BeginCreateProfile();
            await ShowEditorAsync(viewModel);
        }
    }

    private async void OnEditProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (ViewModel is { } viewModel
            && sender is Control { DataContext: NetworkConnectionProfileItemViewModel item })
        {
            // The draft exists before the vault is asked for its credential
            // list, so the dialog opens at once and fills in as the list arrives.
            var editing = viewModel.BeginEditProfileAsync(item, CancellationToken.None);
            await ShowEditorAsync(viewModel);
            await editing;
        }
    }

    private async void OnDeleteProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (ViewModel is not { } viewModel
            || sender is not Control { DataContext: NetworkConnectionProfileItemViewModel item }
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        // The trash icon sits beside Edit, and a connection's credentials go
        // with it: every other definition asks first, so this one does too.
        if (!await Confirmations.DefinitionDelete("network connection", item.Name)
                .ShowDialog<bool>(owner))
        {
            return;
        }

        await viewModel.DeleteProfileAsync(item, CancellationToken.None);
    }

    private async void OnSavePolicyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.SavePolicyAsync(CancellationToken.None);
        }
    }

    private async Task ShowEditorAsync(NetworkSettingsViewModel viewModel)
    {
        if (!viewModel.HasProfileEditor || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        await new NetworkConnectionEditorDialog(viewModel).ShowDialog(owner);
    }
}
