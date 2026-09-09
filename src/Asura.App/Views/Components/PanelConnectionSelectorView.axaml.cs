using System.Collections;
using System.Collections.ObjectModel;
using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

/// <summary>
/// A compact, filterable picker for the connection used by a runtime panel.
///
/// The selector only reports intent. The shell owns session shutdown, connection
/// creation, and panel replacement so every adapter can eventually share this
/// component without moving lifecycle policy into the view.
/// </summary>
public sealed partial class PanelConnectionSelectorView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> OptionsProperty =
        AvaloniaProperty.Register<PanelConnectionSelectorView, IEnumerable?>(
            nameof(Options));

    public static readonly StyledProperty<string> SelectedLabelProperty =
        AvaloniaProperty.Register<PanelConnectionSelectorView, string>(
            nameof(SelectedLabel),
            "Local");

    public static readonly StyledProperty<bool> HasNoMatchesProperty =
        AvaloniaProperty.Register<PanelConnectionSelectorView, bool>(
            nameof(HasNoMatches),
            true);

    public PanelConnectionSelectorView()
    {
        InitializeComponent();
    }

    public IEnumerable? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public string SelectedLabel
    {
        get => GetValue(SelectedLabelProperty);
        set => SetValue(SelectedLabelProperty, value);
    }

    public ObservableCollection<PanelConnectionOptionViewModel> FilteredOptions { get; } = [];

    public bool HasNoMatches
    {
        get => GetValue(HasNoMatchesProperty);
        private set => SetValue(HasNoMatchesProperty, value);
    }

    public event EventHandler<PanelConnectionSelectedEventArgs>? ConnectionSelected;

    public event EventHandler<RoutedEventArgs>? NewConnectionRequested;

    private void OnFlyoutOpened(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (this.FindControl<TextBox>("FilterBox") is { } filter)
        {
            filter.Text = string.Empty;
            filter.Focus();
        }

        RefreshFilter(string.Empty);
    }

    private void OnFilterTextChanged(object? sender, TextChangedEventArgs e)
    {
        _ = e;
        RefreshFilter((sender as TextBox)?.Text);
    }

    private void OnConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control
            {
                DataContext: PanelConnectionOptionViewModel connection,
            })
        {
            return;
        }

        HideFlyout();
        ConnectionSelected?.Invoke(
            this,
            new PanelConnectionSelectedEventArgs(connection.Selection));
    }

    private void OnNewConnectionClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        HideFlyout();
        NewConnectionRequested?.Invoke(this, e);
    }

    private void RefreshFilter(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        var matches = (Options?.OfType<PanelConnectionOptionViewModel>()
                ?? [])
            .Where(connection =>
                normalized.Length == 0
                || connection.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || connection.Kind.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || connection.Detail.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        FilteredOptions.Clear();
        foreach (var connection in matches)
        {
            FilteredOptions.Add(connection);
        }

        HasNoMatches = FilteredOptions.Count == 0;
    }

    private void HideFlyout() =>
        this.FindControl<Button>("SelectorButton")?.Flyout?.Hide();
}

public sealed class PanelConnectionSelectedEventArgs(
    PanelConnectionOptionViewModel.Target selection) : EventArgs
{
    public PanelConnectionOptionViewModel.Target Selection { get; } =
        selection ?? throw new ArgumentNullException(nameof(selection));
}
