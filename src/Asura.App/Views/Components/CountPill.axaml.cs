using Avalonia;
using Avalonia.Controls;

namespace Asura.App.Views.Components;

public sealed partial class CountPill : UserControl
{
    public static readonly StyledProperty<object?> ValueProperty =
        AvaloniaProperty.Register<CountPill, object?>(nameof(Value));

    /// <summary>
    /// The same count with the words taken out, shown instead of
    /// <see cref="Value"/> where there is no room for the full reading. A pill
    /// with nothing short to say keeps saying the long thing.
    /// </summary>
    public static readonly StyledProperty<object?> CompactValueProperty =
        AvaloniaProperty.Register<CountPill, object?>(nameof(CompactValue));

    /// <summary>
    /// Set by whoever knows how much room there is — a container query, in the
    /// panels that have one. The pill itself has no opinion about its width.
    /// </summary>
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<CountPill, bool>(nameof(IsCompact));

    public static readonly DirectProperty<CountPill, object?> DisplayValueProperty =
        AvaloniaProperty.RegisterDirect<CountPill, object?>(
            nameof(DisplayValue),
            pill => pill.DisplayValue);

    private object? _displayValue;

    public CountPill()
    {
        InitializeComponent();
    }

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public object? CompactValue
    {
        get => GetValue(CompactValueProperty);
        set => SetValue(CompactValueProperty, value);
    }

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    /// <summary>Whichever of the two readings is being shown.</summary>
    public object? DisplayValue
    {
        get => _displayValue;
        private set => SetAndRaise(DisplayValueProperty, ref _displayValue, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty
            || change.Property == CompactValueProperty
            || change.Property == IsCompactProperty)
        {
            DisplayValue = IsCompact && CompactValue is not null ? CompactValue : Value;
        }
    }
}
