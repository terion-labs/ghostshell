using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentIcons.Common;

namespace Asura.App.Views.Components;

public sealed partial class ShellNavigationItem : UserControl
{
    public static readonly StyledProperty<string> AutomationNameProperty =
        AvaloniaProperty.Register<ShellNavigationItem, string>(
            nameof(AutomationName),
            string.Empty);

    public static readonly StyledProperty<Symbol> IconProperty =
        AvaloniaProperty.Register<ShellNavigationItem, Symbol>(nameof(Icon));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<ShellNavigationItem, double>(
            nameof(IconSize),
            15);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ShellNavigationItem, bool>(nameof(IsActive));

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ShellNavigationItem, string>(
            nameof(Label),
            string.Empty);

    public ShellNavigationItem()
    {
        InitializeComponent();
    }

    public event EventHandler<RoutedEventArgs>? Click;

    public string AutomationName
    {
        get => GetValue(AutomationNameProperty);
        set => SetValue(AutomationNameProperty, value);
    }

    public Symbol Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    internal void FocusItem() =>
        NavigationButton.Focus(NavigationMethod.Tab);

    private void OnClick(object? sender, RoutedEventArgs e) =>
        Click?.Invoke(sender, e);
}
