using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

/// <summary>
/// The one editor for every saved connection family: terminal endpoints, file
/// providers, and database connections. Which family a definition belongs to
/// is part of the grouped connection-type selection.
/// </summary>
public sealed partial class ConnectionEditorDialog : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConnectionEditorDialogPurpose _purpose;

    public ConnectionEditorDialog()
    {
        InitializeComponent();
    }

    public ConnectionEditorDialog(UnifiedConnectionEditorViewModel viewModel)
        : this(viewModel, ConnectionEditorDialogPurpose.Save)
    {
    }

    public ConnectionEditorDialog(
        UnifiedConnectionEditorViewModel viewModel,
        ConnectionEditorDialogPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _purpose = purpose;
        InitializeComponent();
        DataContext = viewModel;
        if (purpose == ConnectionEditorDialogPurpose.Connect)
        {
            if (this.FindControl<Border>("ConnectPurposeRow") is { } connectRow)
            {
                connectRow.IsVisible = true;
            }

            if (this.FindControl<Button>("SubmitButton") is { } submit)
            {
                submit.Content = "Connect";
            }
        }
    }

    private UnifiedConnectionEditorViewModel ViewModel =>
        DataContext as UnifiedConnectionEditorViewModel
        ?? throw new InvalidOperationException("The connection editor view model is unavailable.");

    protected override void OnClosed(EventArgs e)
    {
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
        try
        {
            await ViewModel.TestAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the dialog owns cancellation of its in-flight test.
        }
    }

    private async void OnBrowseRepositoryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        HideValidationError();
        GitRepositoryPickerViewModel picker;
        try
        {
            // The picker connects with the endpoint typed so far, so an SSH
            // repository browses on its host. Incomplete endpoint fields
            // surface as the same validation callout a save would show.
            picker = ViewModel.Terminal.CreateRepositoryPicker();
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException)
        {
            ShowValidationError(exception.Message);
            return;
        }

        var path = await new GitRepositoryPickerDialog(picker).ShowDialog<string?>(this);
        if (path is not null)
        {
            ViewModel.Terminal.RepositoryPath = path;
        }
    }

    private async void OnTrustHostKeyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ViewModel.Terminal.HostKeyReview is not { } review)
        {
            return;
        }

        var confirmed = await new SshHostKeyReviewDialog(review).ShowDialog<bool>(this);
        if (confirmed)
        {
            await ViewModel.Terminal.TrustHostKeyAsync(_lifetime.Token);
        }
    }

    private async void OnTrustDatabaseHostKeyClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.Database is not { HostKeyReview: { } review } database)
        {
            return;
        }
        var confirmed = await new SshHostKeyReviewDialog(review).ShowDialog<bool>(this);
        if (confirmed)
        {
            await database.TrustHostKeyAsync(review.Id, _lifetime.Token);
        }
    }

    private async void OnTrustFileHostKeyClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        HideValidationError();
        if (ViewModel.Files is not { HostKeyReview: { } review } files)
        {
            return;
        }

        var confirmed = await new SshHostKeyReviewDialog(review).ShowDialog<bool>(this);
        if (confirmed)
        {
            await files.TrustHostKeyAsync(review.Id, _lifetime.Token);
        }
    }

    private void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            HideValidationError();
            var saveConnection = _purpose != ConnectionEditorDialogPurpose.Connect
                || !ViewModel.SupportsUnsavedConnect
                || this.FindControl<CheckBox>("SaveConnectionCheckBox")?.IsChecked == true;
            Close(ViewModel.CreateSaveResult(saveConnection));
        }
        catch (Exception exception) when (exception
            is ArgumentException or OverflowException or UriFormatException or FormatException)
        {
            ShowValidationError(exception.Message);
        }
    }

    private void ShowValidationError(string message)
    {
        var error = this.FindControl<Callout>("ValidationError");
        if (error is not null)
        {
            error.Text = message;
            error.IsVisible = true;
        }
    }

    private void HideValidationError()
    {
        var error = this.FindControl<Callout>("ValidationError");
        error?.IsVisible = false;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

public enum ConnectionEditorDialogPurpose
{
    Save,
    Connect,
}
