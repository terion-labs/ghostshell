using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace Asura.App.Controls;

/// <summary>
/// A named on/off setting: the label, the state written out beside the switch,
/// and the switch. Four editor dialogs carried this as the same three-element
/// grid with the same converter binding, each wired to its neighbour by
/// element name — the shape is one control's worth of decisions, so it is one
/// control.
/// </summary>
internal sealed class ToggleField : TemplatedControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<ToggleField, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<ToggleField, bool>(
            nameof(IsChecked),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>What the two states are called; "On"/"Off" unless the setting
    /// has better words for them.</summary>
    public static readonly StyledProperty<string> OnLabelProperty =
        AvaloniaProperty.Register<ToggleField, string>(nameof(OnLabel), "On");

    public static readonly StyledProperty<string> OffLabelProperty =
        AvaloniaProperty.Register<ToggleField, string>(nameof(OffLabel), "Off");

    /// <summary>The state, written out. Computed here so the template needs no
    /// converter resolved across style files.</summary>
    public static readonly DirectProperty<ToggleField, string> StateTextProperty =
        AvaloniaProperty.RegisterDirect<ToggleField, string>(
            nameof(StateText),
            field => field.StateText);

    private string _stateText = "Off";

    static ToggleField()
    {
        IsCheckedProperty.Changed.AddClassHandler<ToggleField>(
            (field, _) => field.UpdateStateText());
        OnLabelProperty.Changed.AddClassHandler<ToggleField>(
            (field, _) => field.UpdateStateText());
        OffLabelProperty.Changed.AddClassHandler<ToggleField>(
            (field, _) => field.UpdateStateText());
    }

    public ToggleField() => UpdateStateText();

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public string OnLabel
    {
        get => GetValue(OnLabelProperty);
        set => SetValue(OnLabelProperty, value);
    }

    public string OffLabel
    {
        get => GetValue(OffLabelProperty);
        set => SetValue(OffLabelProperty, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => SetAndRaise(StateTextProperty, ref _stateText, value);
    }

    private void UpdateStateText() => StateText = IsChecked ? OnLabel : OffLabel;
}
