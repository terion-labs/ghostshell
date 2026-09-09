using Asura.App.Controls;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views;

/// <summary>What the remote editor was closed with; null means cancelled.</summary>
public sealed record GitRemoteEditorResult(string Name, string Url);

/// <summary>
/// One dialog for both remote gestures: adding a remote and editing one.
/// The caller supplies the initial values for edit and reads back a result;
/// the Git-side consequences (rename versus set-url) are the adapter's call.
/// </summary>
public sealed partial class GitRemoteEditorDialog : Window
{
    public GitRemoteEditorDialog()
    {
        InitializeComponent();
    }

    public GitRemoteEditorDialog(
        string title,
        string action,
        string? initialName = null,
        string? initialUrl = null)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Title = title;
        this.FindControl<DialogShell>("Shell")!.Title = title;
        this.FindControl<Button>("ConfirmButton")!.Content = action;
        this.FindControl<TextBox>("NameInput")!.Text = initialName ?? string.Empty;
        this.FindControl<TextBox>("UrlInput")!.Text = initialUrl ?? string.Empty;
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

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
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
        var name = this.FindControl<TextBox>("NameInput")?.Text?.Trim() ?? string.Empty;
        var url = this.FindControl<TextBox>("UrlInput")?.Text?.Trim() ?? string.Empty;
        if (name.Length == 0 || url.Length == 0)
        {
            var message = this.FindControl<TextBlock>("ValidationMessage")!;
            message.Text = name.Length == 0 ? "Enter a remote name." : "Enter a remote URL.";
            message.IsVisible = true;
            return;
        }

        Close(new GitRemoteEditorResult(name, url));
    }
}
