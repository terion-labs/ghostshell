using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Asura.App.Views.SettingsPages;

public sealed partial class LocalMcpServerSettingsView : UserControl
{
    public LocalMcpServerSettingsView() => InitializeComponent();

    private async void OnEnabledChanged(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is ToggleSwitch toggle
            && DataContext is LocalMcpServerSettingsViewModel { CanEdit: true } model
            && (toggle.IsChecked == true) != model.Enabled)
        {
            await model.SetEnabledAsync(toggle.IsChecked == true);
        }
    }

    private async void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is LocalMcpServerSettingsViewModel model)
        {
            await model.ApplyPortAsync();
        }
    }

    private async void OnCopyUrlClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is LocalMcpServerSettingsViewModel model)
        {
            await model.CopyEndpointAsync(CopyAsync);
        }
    }

    private async void OnCopyTokenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is LocalMcpServerSettingsViewModel model)
        {
            await model.CopyTokenAsync(CopyAsync);
        }
    }

    private async void OnRotateTokenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is LocalMcpServerSettingsViewModel model)
        {
            await model.RotateTokenAsync();
        }
    }

    private Task CopyAsync(string text) => TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
        ? clipboard.SetTextAsync(text)
        : throw new InvalidOperationException("The clipboard is unavailable.");
}
