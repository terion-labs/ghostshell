using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

/// <summary>
/// The icon grid: a shortlist, a search over the whole catalog, and the tile
/// that gives up on the shortlist. It reports the identifier chosen and leaves
/// storing it to the caller.
/// </summary>
public sealed partial class IconPicker : UserControl
{
    public static readonly StyledProperty<System.Collections.IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<IconPicker, System.Collections.IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string?> SearchTextProperty =
        AvaloniaProperty.Register<IconPicker, string?>(
            nameof(SearchText),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowAllProperty =
        AvaloniaProperty.Register<IconPicker, bool>(
            nameof(ShowAll),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> TotalCountProperty =
        AvaloniaProperty.Register<IconPicker, int>(nameof(TotalCount));

    /// <summary>
    /// The line under the grid. It says something different when a search
    /// matched nothing than when it matched, so the caller supplies it.
    /// </summary>
    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<IconPicker, string?>(nameof(Hint));

    static IconPicker()
    {
        HintProperty.Changed.AddClassHandler<IconPicker>(
            (picker, _) => picker.RaisePropertyChanged(HasHintProperty, default, picker.HasHint));
        SearchTextProperty.Changed.AddClassHandler<IconPicker>(
            (picker, _) => picker.RaisePropertyChanged(
                IsShowAllOfferedProperty,
                default,
                picker.IsShowAllOffered));
    }

    public IconPicker()
    {
        InitializeComponent();
    }

    /// <summary>Raised with the chosen icon's durable identifier.</summary>
    public event EventHandler<string>? IconChosen;

    public System.Collections.IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public bool ShowAll
    {
        get => GetValue(ShowAllProperty);
        set => SetValue(ShowAllProperty, value);
    }

    public int TotalCount
    {
        get => GetValue(TotalCountProperty);
        set => SetValue(TotalCountProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public static readonly DirectProperty<IconPicker, bool> HasHintProperty =
        AvaloniaProperty.RegisterDirect<IconPicker, bool>(nameof(HasHint), picker => picker.HasHint);

    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);

    /// <summary>
    /// The "All" tile only means something while a shortlist is showing. During
    /// a search the grid is already the whole catalog, filtered.
    /// </summary>
    public static readonly DirectProperty<IconPicker, bool> IsShowAllOfferedProperty =
        AvaloniaProperty.RegisterDirect<IconPicker, bool>(
            nameof(IsShowAllOffered),
            picker => picker.IsShowAllOffered);

    public bool IsShowAllOffered => string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Moves keyboard input straight into search when a picker flyout opens.</summary>
    public void FocusSearch()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    private void OnIconClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Button { DataContext: WorkspaceIconChoiceViewModel choice })
        {
            IconChosen?.Invoke(this, choice.Id);
        }
    }
}
