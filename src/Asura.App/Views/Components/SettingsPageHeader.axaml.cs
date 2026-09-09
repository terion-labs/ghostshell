using Avalonia;
using Avalonia.Controls;

namespace Asura.App.Views.Components;

public sealed partial class SettingsPageHeader : UserControl
{
    public static readonly StyledProperty<string> HeadingProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string>(
            nameof(Heading),
            string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string>(
            nameof(Description),
            string.Empty);

    public SettingsPageHeader()
    {
        InitializeComponent();
    }

    public string Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
