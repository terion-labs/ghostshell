using Asura.App.Controls;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views;

/// <summary>The confirmed deletion's scope; null still means cancelled.</summary>
public sealed record GitTagDeleteResult(bool AlsoOnRemotes);

/// <summary>
/// Confirms deleting a tag, with the one choice a plain confirmation cannot
/// carry: whether the deletion also travels to the repository's remotes.
/// </summary>
public sealed partial class GitTagDeleteDialog : Window
{
    public GitTagDeleteDialog()
    {
        InitializeComponent();
    }

    public GitTagDeleteDialog(string tagName, IReadOnlyList<string> remoteNames)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ArgumentNullException.ThrowIfNull(remoteNames);
        this.FindControl<DialogShell>("Shell")!.Title = $"Delete tag “{tagName}”?";
        var checkbox = this.FindControl<CheckBox>("RemotesCheckBox")!;
        if (remoteNames.Count == 0)
        {
            checkbox.IsVisible = false;
        }
        else
        {
            checkbox.Content = $"Also delete on remotes ({string.Join(", ", remoteNames)})";
        }

        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
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
        var checkbox = this.FindControl<CheckBox>("RemotesCheckBox")!;
        Close(new GitTagDeleteResult(checkbox.IsVisible && checkbox.IsChecked == true));
    }
}
