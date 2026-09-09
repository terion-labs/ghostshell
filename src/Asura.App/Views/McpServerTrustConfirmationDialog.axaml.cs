using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Asura.App.Views;

public sealed partial class McpServerTrustConfirmationDialog : Window
{
    public McpServerTrustConfirmationDialog()
        : this(new McpServerTrustReview(
            "MCP server",
            "Not configured",
            "Executable directory",
            ["Add a new local MCP server process"],
            [],
            [],
            []))
    {
    }

    public McpServerTrustConfirmationDialog(McpServerTrustReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        InitializeComponent();
        DataContext = review;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.FindControl<CheckBox>("Acknowledgement")?.Focus();
    }

    private void OnAcknowledgementChanged(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var acknowledged = this.FindControl<CheckBox>("Acknowledgement")?.IsChecked == true;
        if (this.FindControl<Button>("ConfirmButton") is { } confirm)
        {
            confirm.IsEnabled = acknowledged;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(false);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (this.FindControl<CheckBox>("Acknowledgement")?.IsChecked == true)
        {
            Close(true);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
