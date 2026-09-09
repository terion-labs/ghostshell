using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views;

/// <summary>
/// The connection-aware repository chooser. It browses the panel's target —
/// local or SSH — through the Git client's directory probe and closes with
/// the chosen path, or null when cancelled.
/// </summary>
public sealed partial class GitRepositoryPickerDialog : Window
{
    public GitRepositoryPickerDialog()
    {
        InitializeComponent();
    }

    public GitRepositoryPickerDialog(GitRepositoryPickerViewModel picker)
        : this()
    {
        ArgumentNullException.ThrowIfNull(picker);
        DataContext = picker;
        Opened += async (_, _) => await picker.LoadInitialAsync();
    }

    private GitRepositoryPickerViewModel? Picker => DataContext as GitRepositoryPickerViewModel;

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Picker?.ResolveSelection() is { } path)
        {
            Close(path);
        }
    }

    private async void OnNavigateUpClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Picker is { } picker)
        {
            await picker.NavigateUpAsync();
        }
    }

    private async void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Enter && Picker is { } picker)
        {
            e.Handled = true;
            await picker.NavigateToInputAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
        }
    }

    // Double-tapping a repository chooses it; double-tapping a plain folder
    // steps into it, because the repository may live deeper.
    private async void OnDirectoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Picker is not { SelectedDirectory: { } entry } picker)
        {
            return;
        }

        if (entry.IsRepository)
        {
            Close(entry.FullPath);
        }
        else
        {
            await picker.EnterAsync(entry);
        }
    }
}
