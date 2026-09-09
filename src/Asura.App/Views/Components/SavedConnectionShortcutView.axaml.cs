using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

public sealed partial class SavedConnectionShortcutView : UserControl
{
    public SavedConnectionShortcutView()
    {
        InitializeComponent();
    }

    public event EventHandler<SavedConnectionLaunchViewModel>? LaunchRequested;

    /// <summary>Raised with the row's own view model as the sender's context.</summary>
    public event EventHandler<SavedConnectionShortcutViewModel>? EditRequested;

    public event EventHandler<SavedConnectionShortcutViewModel>? DeleteRequested;

    private void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is SavedConnectionShortcutViewModel shortcut)
        {
            LaunchRequested?.Invoke(this, shortcut.DefaultLaunch);
        }
    }

    private void OnEditClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is SavedConnectionShortcutViewModel shortcut)
        {
            EditRequested?.Invoke(this, shortcut);
        }
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is SavedConnectionShortcutViewModel shortcut)
        {
            DeleteRequested?.Invoke(this, shortcut);
        }
    }

    private void OnAlternativeClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Control { DataContext: SavedConnectionLaunchViewModel launch })
        {
            ShortcutMenuButton.Flyout?.IsOpen = false;

            LaunchRequested?.Invoke(this, launch);
        }
    }
}
