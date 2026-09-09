using Avalonia;
using Avalonia.Controls;

namespace Asura.App.Views.Components;

/// <summary>The heading and one-line explanation over a group of settings.</summary>
public sealed partial class SectionHeader : UserControl
{
    public static readonly StyledProperty<string?> HeadingProperty =
        AvaloniaProperty.Register<SectionHeader, string?>(nameof(Heading));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SectionHeader, string?>(nameof(Description));

    static SectionHeader()
    {
        DescriptionProperty.Changed.AddClassHandler<SectionHeader>(
            (header, _) => header.RaisePropertyChanged(
                HasDescriptionProperty,
                default,
                header.HasDescription));
        CountProperty.Changed.AddClassHandler<SectionHeader>(
            (header, _) => header.RaisePropertyChanged(
                HasCountProperty,
                default,
                header.HasCount));
    }

    public SectionHeader()
    {
        InitializeComponent();
    }

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

    /// <summary>
    /// Whether there is a second line at all. A section with no explanation must
    /// not reserve the line: an empty muted row under a heading reads as a
    /// description that failed to load.
    /// </summary>
    public static readonly DirectProperty<SectionHeader, bool> HasDescriptionProperty =
        AvaloniaProperty.RegisterDirect<SectionHeader, bool>(
            nameof(HasDescription),
            header => header.HasDescription);

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>
    /// How many things the section is about, when saying so is useful. It sits
    /// against the heading rather than across the page from it, because a count
    /// pushed to the far margin stops reading as part of the heading at all.
    /// </summary>
    public static readonly StyledProperty<int?> CountProperty =
        AvaloniaProperty.Register<SectionHeader, int?>(nameof(Count));

    public int? Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public static readonly DirectProperty<SectionHeader, bool> HasCountProperty =
        AvaloniaProperty.RegisterDirect<SectionHeader, bool>(
            nameof(HasCount),
            header => header.HasCount);

    public bool HasCount => Count is not null;
}
