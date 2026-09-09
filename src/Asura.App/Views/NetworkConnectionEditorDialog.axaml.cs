using System.ComponentModel;
using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views;

/// <summary>
/// Edits the network connection the settings page has open. The page's view
/// model owns the draft, the vault, and the test route; this window is its
/// frame, and it closes itself when the draft is gone — saved, discarded, or
/// deleted from under it.
/// </summary>
public sealed partial class NetworkConnectionEditorDialog : Window
{
    private bool _closing;

    public NetworkConnectionEditorDialog()
    {
        InitializeComponent();
    }

    public NetworkConnectionEditorDialog(NetworkSettingsViewModel settings)
        : this()
    {
        DataContext = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    private NetworkSettingsViewModel? ViewModel => DataContext as NetworkSettingsViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.PropertyChanged += OnSettingsPropertyChanged;
        if (!viewModel.HasProfileEditor)
        {
            Close();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        _closing = true;
        // The window is going regardless — the host closed it, or Escape did.
        // The draft must not outlive it, or the page would refuse to open the
        // next one until this invisible one was cancelled.
        if (ViewModel is { HasProfileEditor: true } viewModel)
        {
            _ = viewModel.CancelProfileEditAsync(CancellationToken.None).AsTask();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.PropertyChanged -= OnSettingsPropertyChanged;
        }

        base.OnClosed(e);
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (!_closing
            && string.Equals(
                e.PropertyName,
                nameof(NetworkSettingsViewModel.HasProfileEditor),
                StringComparison.Ordinal)
            && ViewModel?.HasProfileEditor == false)
        {
            _closing = true;
            Close();
        }
    }

    private async void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { HasProfileEditor: true } viewModel)
        {
            await viewModel.CancelProfileEditAsync(CancellationToken.None);
        }
        else if (!_closing)
        {
            Close();
        }
    }

    private async void OnSaveProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.SaveProfileAsync(CancellationToken.None);
        }
    }

    private async void OnTestProfileClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.TestProfileAsync(CancellationToken.None);
        }
    }

    private void OnAddCredentialRequested(object? sender, NetworkCredentialTarget target)
    {
        _ = sender;
        ViewModel?.ProfileEditor?.BeginCredentialDraft(target);
    }

    private async void OnStoreCredentialClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel is { } viewModel)
        {
            await viewModel.StoreCredentialAsync(CancellationToken.None);
        }
    }

    private void OnCancelCredentialDraftClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel?.ProfileEditor?.CloseCredentialDraft();
    }
}
