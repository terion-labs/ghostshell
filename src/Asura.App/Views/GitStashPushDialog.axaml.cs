using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views;

/// <summary>What the stash dialog was closed with; null means cancelled.</summary>
public sealed record GitStashPushResult(string? Message);

/// <summary>
/// Stashes the working changes. The message is optional: empty stashes
/// without one, exactly as Git's own default gesture does.
/// </summary>
public sealed partial class GitStashPushDialog : Window
{
    public GitStashPushDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("MessageInput")!.Focus();
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

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
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
        var message = this.FindControl<TextBox>("MessageInput")?.Text?.Trim();
        Close(new GitStashPushResult(string.IsNullOrEmpty(message) ? null : message));
    }
}
