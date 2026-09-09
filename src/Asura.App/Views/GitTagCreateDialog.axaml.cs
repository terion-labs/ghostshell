using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views;

/// <summary>What the tag dialog was closed with; null means cancelled.</summary>
public sealed record GitTagCreateResult(string Name, string? Message);

/// <summary>
/// Creates a tag at HEAD. The message field decides the tag's nature: empty
/// makes a lightweight tag, anything else an annotated one.
/// </summary>
public sealed partial class GitTagCreateDialog : Window
{
    public GitTagCreateDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("NameInput")!.Focus();
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

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;

        // The message box owns Enter for new lines; only Escape leaves.
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void Confirm()
    {
        var name = this.FindControl<TextBox>("NameInput")?.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            var message = this.FindControl<TextBlock>("ValidationMessage")!;
            message.Text = "Enter a tag name.";
            message.IsVisible = true;
            return;
        }

        var annotation = this.FindControl<TextBox>("MessageInput")?.Text?.Trim();
        Close(new GitTagCreateResult(
            name,
            string.IsNullOrEmpty(annotation) ? null : annotation));
    }
}
