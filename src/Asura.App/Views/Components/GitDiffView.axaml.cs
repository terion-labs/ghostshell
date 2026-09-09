using Asura.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

/// <summary>
/// The structured diff presenter, unified or side-by-side. It binds to the
/// Git panel view model it inherits as DataContext; the panel decides which
/// diff is shown and holds the comparison options.
/// </summary>
public sealed partial class GitDiffView : UserControl
{
    public GitDiffView()
    {
        InitializeComponent();
    }

    private GitRuntimePanelViewModel? Panel => DataContext as GitRuntimePanelViewModel;

    private void OnToggleWhitespaceClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { } panel)
        {
            panel.DiffIgnoresWhitespace = !panel.DiffIgnoresWhitespace;
        }
    }

    private void OnToggleSplitClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (Panel is { } panel)
        {
            panel.DiffIsSplit = !panel.DiffIsSplit;
        }
    }
}
