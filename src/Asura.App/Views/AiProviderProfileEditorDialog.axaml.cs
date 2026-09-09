using System.Diagnostics;
using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Asura.App.Views;

public sealed partial class AiProviderProfileEditorDialog : Window
{
    private readonly CancellationTokenSource _lifetime = new();

    public AiProviderProfileEditorDialog()
    {
        InitializeComponent();
    }

    public AiProviderProfileEditorDialog(AiProviderProfileEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private AiProviderProfileEditorViewModel ViewModel =>
        DataContext as AiProviderProfileEditorViewModel
        ?? throw new InvalidOperationException("The AI-provider editor is unavailable.");

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is AiProviderProfileEditorViewModel editor)
        {
            editor.ApiKeyValue = string.Empty;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        HideValidationError();
        await ViewModel.TestAsync(_lifetime.Token);
    }

    private async void OnAuthenticateClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            HideValidationError();
            var launch = await ViewModel.BeginAuthenticationAsync(_lifetime.Token);
            if (launch is null)
            {
                return;
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = launch.AuthorizationUri.AbsoluteUri,
                UseShellExecute = true,
            });
            process?.Dispose();
            await launch.Completion;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowValidationError("Authentication could not be started.");
        }
    }

    private async void OnStoreApiKeyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        HideValidationError();
        await ViewModel.StoreApiKeyAsync(_lifetime.Token);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            HideValidationError();
            if (await ViewModel.PrepareSaveAsync(_lifetime.Token) is { } request)
            {
                Close(request);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            ShowValidationError(exception.Message);
        }
    }

    private void HideValidationError()
    {
        var error = this.FindControl<TextBlock>("ValidationError");
        error?.IsVisible = false;
    }

    private void ShowValidationError(string message)
    {
        var error = this.FindControl<TextBlock>("ValidationError");
        if (error is not null)
        {
            error.Text = message;
            error.IsVisible = true;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
