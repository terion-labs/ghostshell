using Asura.App.Controls;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views;

public sealed partial class FileNameDialog : Window
{
    public FileNameDialog()
    {
        InitializeComponent();
    }

    public FileNameDialog(
        string title,
        string action,
        string? initialValue = null,
        string? description = null)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Title = title;
        var shell = this.FindControl<DialogShell>("Shell")!;
        shell.Title = title;
        if (!string.IsNullOrWhiteSpace(description))
        {
            shell.Subtitle = description;
        }

        this.FindControl<Button>("ConfirmButton")!.Content = action;
        this.FindControl<TextBox>("NameInput")!.Text = initialValue ?? string.Empty;
        Opened += (_, _) =>
        {
            var input = this.FindControl<TextBox>("NameInput")!;
            input.Focus();
            input.SelectAll();
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Confirm();
    }

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void Confirm()
    {
        var value = this.FindControl<TextBox>("NameInput")?.Text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            var message = this.FindControl<TextBlock>("ValidationMessage")!;
            message.Text = "Enter a name.";
            message.IsVisible = true;
            return;
        }

        Close(value);
    }
}
