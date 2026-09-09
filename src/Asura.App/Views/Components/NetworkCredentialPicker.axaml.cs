using System.Collections;
using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace Asura.App.Views.Components;

/// <summary>
/// A credential slot: the stored credentials that can fill it, the one that
/// does, and a way to add another. It knows which slot it is so the editor
/// does not have to ask.
/// </summary>
public sealed partial class NetworkCredentialPicker : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<NetworkCredentialPicker, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<NetworkCredentialPicker, string?>(nameof(Hint));

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<NetworkCredentialPicker, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<NetworkCredentialPicker, object?>(
            nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<NetworkCredentialTarget> TargetProperty =
        AvaloniaProperty.Register<NetworkCredentialPicker, NetworkCredentialTarget>(nameof(Target));

    public NetworkCredentialPicker()
    {
        InitializeComponent();
    }

    /// <summary>Raised with the slot a new credential should be added to.</summary>
    public event EventHandler<NetworkCredentialTarget>? AddRequested;

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public NetworkCredentialTarget Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        AddRequested?.Invoke(this, Target);
    }
}
