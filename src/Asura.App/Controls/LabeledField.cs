using Avalonia;
using Avalonia.Controls;

namespace Asura.App.Controls;

/// <summary>
/// One control with a name above it and, where it needs one, a sentence below.
///
/// This is the settings shape that does not fit a row: a colour, a font, a
/// platform profile — things that sit side by side in a grid rather than stacked
/// down a page. It was hand-built seventeen times, each restating the label style
/// and the hint's font size, which is how two fields in the same grid ended up
/// with different gaps between label and control.
///
/// It is deliberately separate from <see cref="SettingRow"/>. A row puts the name
/// beside the control and belongs in a list of settings; a field puts the name
/// above it and belongs in a grid of them. Merging the two behind a flag would
/// make every use site declare which of the two it meant.
/// </summary>
internal sealed class LabeledField : ContentControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<LabeledField, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<LabeledField, string?>(nameof(Hint));

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

    protected override Type StyleKeyOverride => typeof(LabeledField);
}
