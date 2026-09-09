using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asura.App.Views.Overlays;

public sealed partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? ActivateSearchResultRequested;

    public event EventHandler<RoutedEventArgs>? CloseRequested;

    public event EventHandler<KeyEventArgs>? SearchKeyDownRequested;

    internal void FocusSearch() =>
        CommandSearchBox.Focus();

    internal void ScrollSelectedResultIntoView()
    {
        if (LauncherSearchResultList.SelectedItem is { } selected)
        {
            LauncherSearchResultList.ScrollIntoView(selected);
        }
    }

    private void OnActivateSearchResultClick(object? sender, RoutedEventArgs e) =>
        ActivateSearchResultRequested?.Invoke(sender, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(sender, e);

    private void OnSearchKeyDown(object? sender, KeyEventArgs e) =>
        SearchKeyDownRequested?.Invoke(sender, e);
}
