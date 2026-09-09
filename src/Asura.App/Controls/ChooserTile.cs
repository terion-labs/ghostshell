using Avalonia;
using Avalonia.Controls;
using FluentIcons.Common;

namespace Asura.App.Controls;

/// <summary>
/// One tile in a chooser grid: an icon in its tile, a name, a sentence.
///
/// The launcher and the panel chooser wrote this body out fourteen times
/// between them, and the copies had drifted — tile 38 against 36, icon 19
/// against 18, description text at two sizes. A tile states what it offers;
/// the anatomy is decided here.
/// </summary>
internal sealed class ChooserTile : Button
{
    public static readonly StyledProperty<Symbol> IconProperty =
        AvaloniaProperty.Register<ChooserTile, Symbol>(nameof(Icon), Symbol.Apps);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ChooserTile, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<ChooserTile, string?>(nameof(Description));

    public Symbol Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ChooserTile);
}
