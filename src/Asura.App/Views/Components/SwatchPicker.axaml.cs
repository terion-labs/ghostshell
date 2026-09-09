using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Asura.App.Views.Components;

/// <summary>
/// A row of colour presets, a picker for anything else, and — where the colour
/// is allowed to be absent — a way to give it back.
///
/// The items it presents need a <c>Name</c>, a <c>Hex</c>, and an
/// <c>IsSelected</c>. Selection lives on the item because Avalonia can bind a
/// style class from one value only, and marking the chosen swatch needs both it
/// and the current value.
/// </summary>
public sealed partial class SwatchPicker : UserControl
{
    public static readonly StyledProperty<System.Collections.IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<SwatchPicker, System.Collections.IEnumerable?>(nameof(ItemsSource));

    /// <summary>
    /// The chosen colour, as the hex a definition stores. Empty means the
    /// subject has no colour of its own — which only some callers allow, and
    /// those are the ones that set <see cref="ClearLabel"/>.
    /// </summary>
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<SwatchPicker, string?>(
            nameof(Value),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>
    /// What the picker fills in, in the words an action would use: "workspace
    /// colour", "accent". It names the controls for assistive technology, which
    /// otherwise announce three unlabelled buttons.
    /// </summary>
    public static readonly StyledProperty<string> PurposeProperty =
        AvaloniaProperty.Register<SwatchPicker, string>(nameof(Purpose), "colour");

    public static readonly StyledProperty<bool> CanSampleProperty =
        AvaloniaProperty.Register<SwatchPicker, bool>(nameof(CanSample));

    public static readonly StyledProperty<string?> ClearLabelProperty =
        AvaloniaProperty.Register<SwatchPicker, string?>(nameof(ClearLabel));

    public static readonly StyledProperty<bool> CanClearProperty =
        AvaloniaProperty.Register<SwatchPicker, bool>(nameof(CanClear), true);

    /// <summary>
    /// The colour the picker shows while <see cref="Value"/> is empty. A picker
    /// opening on black when the subject is plainly bronze is a lie about what
    /// clearing did.
    /// </summary>
    public static readonly StyledProperty<string?> FallbackValueProperty =
        AvaloniaProperty.Register<SwatchPicker, string?>(nameof(FallbackValue));

    private bool _syncing;

    static SwatchPicker()
    {
        ValueProperty.Changed.AddClassHandler<SwatchPicker>(
            (picker, _) => picker.SynchronizePicker());
        FallbackValueProperty.Changed.AddClassHandler<SwatchPicker>(
            (picker, _) => picker.SynchronizePicker());
        ClearLabelProperty.Changed.AddClassHandler<SwatchPicker>(
            (picker, _) => picker.RaisePropertyChanged(
                HasClearActionProperty,
                default,
                picker.HasClearAction));
    }

    public SwatchPicker()
    {
        InitializeComponent();
        SynchronizePicker();
    }

    /// <summary>Asks the host to sample a colour from the screen.</summary>
    public event EventHandler? SampleRequested;

    public System.Collections.IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Purpose
    {
        get => GetValue(PurposeProperty);
        set => SetValue(PurposeProperty, value);
    }

    public bool CanSample
    {
        get => GetValue(CanSampleProperty);
        set => SetValue(CanSampleProperty, value);
    }

    public string? ClearLabel
    {
        get => GetValue(ClearLabelProperty);
        set => SetValue(ClearLabelProperty, value);
    }

    public bool CanClear
    {
        get => GetValue(CanClearProperty);
        set => SetValue(CanClearProperty, value);
    }

    public string? FallbackValue
    {
        get => GetValue(FallbackValueProperty);
        set => SetValue(FallbackValueProperty, value);
    }

    public static readonly DirectProperty<SwatchPicker, bool> HasClearActionProperty =
        AvaloniaProperty.RegisterDirect<SwatchPicker, bool>(
            nameof(HasClearAction),
            picker => picker.HasClearAction);

    public bool HasClearAction => !string.IsNullOrWhiteSpace(ClearLabel);

    /// <summary>Applies a colour the host sampled from the screen.</summary>
    public void ApplySampled(Color color) => Value = ToHex(color);

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Button { DataContext: IColorChoice choice })
        {
            Value = choice.Hex;
        }
    }

    private void OnCustomColorChanged(object? sender, ColorChangedEventArgs e)
    {
        _ = sender;
        if (_syncing)
        {
            return;
        }

        Value = ToHex(e.NewColor);
    }

    private void OnSampleClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SampleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Value = string.Empty;
    }

    private void SynchronizePicker()
    {
        // A half-typed or absent value is not a colour: the picker keeps what it
        // had rather than jumping to black on the way through.
        var candidate = string.IsNullOrWhiteSpace(Value) ? FallbackValue : Value;
        if (string.IsNullOrWhiteSpace(candidate) || !Color.TryParse(candidate, out var color))
        {
            return;
        }

        _syncing = true;
        try
        {
            CustomPicker.Color = color;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
