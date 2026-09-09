using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Asura.App.Views;

public sealed partial class McpServerProfileEditorDialog : Window
{
    public McpServerProfileEditorDialog()
    {
        InitializeComponent();
    }

    public McpServerProfileEditorDialog(McpServerProfileEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private McpServerProfileEditorViewModel ViewModel =>
        DataContext as McpServerProfileEditorViewModel
        ?? throw new InvalidOperationException("The MCP-server editor is unavailable.");

    private void OnOpened(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.FindControl<TextBox>("McpServerNameInput")?.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private void OnAddArgumentClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RunEditorMutation(ViewModel.AddArgument);
    }

    private void OnMoveArgumentUpClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: McpArgumentEditorItemViewModel argument })
        {
            ViewModel.MoveArgumentUp(argument);
        }
    }

    private void OnMoveArgumentDownClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: McpArgumentEditorItemViewModel argument })
        {
            ViewModel.MoveArgumentDown(argument);
        }
    }

    private void OnRemoveArgumentClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: McpArgumentEditorItemViewModel argument })
        {
            ViewModel.RemoveArgument(argument);
        }
    }

    private void OnAddEnvironmentBindingClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RunEditorMutation(ViewModel.AddEnvironmentBinding);
    }

    private void OnRemoveEnvironmentBindingClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control
            {
                DataContext: McpEnvironmentBindingEditorItemViewModel binding,
            })
        {
            ViewModel.RemoveEnvironmentBinding(binding);
        }
    }

    private void OnAddHttpHeaderBindingClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RunEditorMutation(ViewModel.AddHttpHeaderBinding);
    }

    private void OnRemoveHttpHeaderBindingClick(
        object? sender,
        RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control
            {
                DataContext: McpHttpHeaderBindingEditorItemViewModel binding,
            })
        {
            ViewModel.RemoveHttpHeaderBinding(binding);
        }
    }

    private void OnAddEnabledToolClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        RunEditorMutation(ViewModel.AddEnabledTool);
    }

    private void OnRemoveEnabledToolClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: McpEnabledToolEditorItemViewModel tool })
        {
            ViewModel.RemoveEnabledTool(tool);
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            HideValidationError();
            var request = ViewModel.CreateSaveRequest();
            if (request.RequiresTrustConfirmation)
            {
                var confirmed = await new McpServerTrustConfirmationDialog(request.TrustReview)
                    .ShowDialog<bool>(this);
                if (!confirmed)
                {
                    return;
                }

                request = request.ConfirmTrust();
            }

            Close(request);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            ShowValidationError(exception.Message);
        }
    }

    private void RunEditorMutation(Action mutation)
    {
        try
        {
            HideValidationError();
            mutation();
        }
        catch (InvalidOperationException exception)
        {
            ShowValidationError(exception.Message);
        }
    }

    private void ShowValidationError(string message)
    {
        var error = this.FindControl<TextBlock>("ValidationError");
        if (error is not null)
        {
            error.Text = message;
            error.IsVisible = true;
            error.BringIntoView();
            error.Focus();
        }
    }

    private void HideValidationError()
    {
        var error = this.FindControl<TextBlock>("ValidationError");
        error?.IsVisible = false;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
