using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using FluentIcons.Common;

namespace Asura.App.Controls;

/// <summary>How large a mark is drawn.</summary>
internal enum IdentityTileSize
{
    /// <summary>Inside a row: a list, a tab strip.</summary>
    Small,

    /// <summary>Beside a name: a rail entry, a card.</summary>
    Medium,

    /// <summary>The subject of the page.</summary>
    Large,
}

/// <summary>
/// The mark a workspace — or anything else with a colour and an icon — is
/// recognised by.
///
/// It was written by hand at every size it appears in: 32 in the settings list,
/// 34 in the editor's rail, 56 in its header, each with its own corner radius
/// and its own guess at what colour an icon should be on a saturated fill. They
/// are one control, because "this thing looks like this" is not a decision a
/// view gets to re-take.
/// </summary>
internal sealed class IdentityTile : TemplatedControl
{
    public static readonly StyledProperty<IBrush?> TintProperty =
        AvaloniaProperty.Register<IdentityTile, IBrush?>(nameof(Tint));

    public static readonly StyledProperty<Symbol> SymbolProperty =
        AvaloniaProperty.Register<IdentityTile, Symbol>(nameof(Symbol), Symbol.Window);

    public static readonly StyledProperty<IdentityTileSize> TileSizeProperty =
        AvaloniaProperty.Register<IdentityTile, IdentityTileSize>(
            nameof(TileSize),
            IdentityTileSize.Medium);

    static IdentityTile() =>
        TileSizeProperty.Changed.AddClassHandler<IdentityTile>(
            (tile, _) => tile.UpdateStateClasses());

    public IdentityTile() => UpdateStateClasses();

    /// <summary>
    /// The fill. A string hex binds here as it does to any brush property, so a
    /// view model may publish the colour it stores rather than a brush it would
    /// have to build.
    /// </summary>
    public IBrush? Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public Symbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public IdentityTileSize TileSize
    {
        get => GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    private void UpdateStateClasses()
    {
        foreach (var size in Enum.GetValues<IdentityTileSize>())
        {
            PseudoClasses.Set($":{size.ToString().ToLowerInvariant()}", size == TileSize);
        }
    }
}
