using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Asura.App.Controls;

/// <summary>
/// The terminal's application-owned font collection.
/// </summary>
/// <remarks>
/// Keeping a distinct collection key prevents an installed font with the same family name
/// from changing terminal metrics or substituting a different face.
/// </remarks>
public sealed class AsuraTerminalFontCollection : EmbeddedFontCollection
{
    public const string FamilyName = "JetBrains Mono";

    public const string FamilyReference = "fonts:AsuraTerminal#JetBrains Mono";

    public static readonly Uri CollectionKey =
        new("fonts:AsuraTerminal", UriKind.Absolute);

    public static readonly FontFamily Family =
        new(FamilyReference);

    public AsuraTerminalFontCollection()
        : base(
            CollectionKey,
            new Uri(
                "avares://Asura.App/Assets/Fonts/JetBrainsMono",
                UriKind.Absolute))
    {
    }
}
