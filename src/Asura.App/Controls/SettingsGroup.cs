using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Asura.App.Controls;

/// <summary>
/// A card of related settings.
///
/// It owns the two things the rows inside it must agree on and cannot decide for
/// themselves: how wide the control column is, and where the separators go. Both
/// were hand-maintained before — every page picked its own column width, and each
/// separator was a one-pixel <c>Border</c> written between rows, which meant a
/// row added in the wrong place left a hairline dangling under the last one.
/// </summary>
[TemplatePart("PART_Rows", typeof(ItemsPresenter))]
internal sealed class SettingsGroup : ItemsControl
{
    public static readonly StyledProperty<string?> HeadingProperty =
        AvaloniaProperty.Register<SettingsGroup, string?>(nameof(Heading));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsGroup, string?>(nameof(Description));

    /// <summary>
    /// The width every row's control column takes. Auto by default, so a group of
    /// short controls does not reserve a column it has no use for.
    /// </summary>
    /// <summary>
    /// What closes the card: a live registration status, or a sentence qualifying
    /// the settings above it. These were rows in the stack with a hand-written
    /// separator above them, which is why a footnote could end up looking like a
    /// setting that had lost its control.
    /// </summary>
    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<SettingsGroup, object?>(nameof(Footer));

    public static readonly StyledProperty<GridLength> ControlWidthProperty =
        AvaloniaProperty.Register<SettingsGroup, GridLength>(
            nameof(ControlWidth),
            new GridLength(1, GridUnitType.Auto));

    static SettingsGroup()
    {
        ControlWidthProperty.Changed.AddClassHandler<SettingsGroup>(
            (group, _) => group.ApplyToRows());
        HeadingProperty.Changed.AddClassHandler<SettingsGroup>(
            (group, _) => group.UpdateStateClasses());
        DescriptionProperty.Changed.AddClassHandler<SettingsGroup>(
            (group, _) => group.UpdateStateClasses());
        FooterProperty.Changed.AddClassHandler<SettingsGroup>(
            (group, _) => group.UpdateStateClasses());
    }

    public SettingsGroup() => UpdateStateClasses();

    public string? Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public GridLength ControlWidth
    {
        get => GetValue(ControlWidthProperty);
        set => SetValue(ControlWidthProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SettingsGroup);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        ApplyToRows();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ApplyToRows();
    }

    /// <summary>
    /// Pushes the group's column width to every row, and tells the last row it is
    /// last so it draws no separator. Doing it here is what keeps a row from ever
    /// having to know its own position.
    /// </summary>
    private void ApplyToRows()
    {
        var rows = Items.OfType<SettingRow>().ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            rows[index].ControlWidth = ControlWidth;
            rows[index].IsLast = index == rows.Length - 1;
        }
    }

    private void UpdateStateClasses()
    {
        PseudoClasses.Set(":headed", !string.IsNullOrWhiteSpace(Heading));
        PseudoClasses.Set(":described", !string.IsNullOrWhiteSpace(Description));
        PseudoClasses.Set(":footed", Footer is not null);
    }
}
