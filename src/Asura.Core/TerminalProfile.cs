using System.Text.Json.Serialization;

namespace Asura.Core;

public enum TerminalCursorStyle
{
    Block,
    Bar,
    Underline,
}

public sealed record TerminalProfile : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Whether this profile would store the same thing as <paramref name="other"/>.
    ///
    /// Record equality cannot answer it: the palette holds its ANSI colours in a
    /// list, which records compare by reference, so two profiles built from the
    /// same values are never equal. Saving on that basis rewrote the profile every
    /// time it was asked to, and the notification that followed rebuilt the editor,
    /// whose rebinding asked again.
    /// </summary>
    public bool RepresentsSameAs(TerminalProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Id == other.Id
            && FontSize == other.FontSize
            && LineHeight == other.LineHeight
            && CursorStyle == other.CursorStyle
            && CursorBlink == other.CursorBlink
            && ScrollbackLines == other.ScrollbackLines
            && KeymapId == other.KeymapId
            && LinkPolicy == other.LinkPolicy
            && ImeEnabled == other.ImeEnabled
            && ShellIntegration == other.ShellIntegration
            && BellMode == other.BellMode
            && Compatibility == other.Compatibility
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(FontFamily, other.FontFamily, StringComparison.Ordinal)
            && string.Equals(Palette.Name, other.Palette.Name, StringComparison.Ordinal)
            && Palette.Matches(other.Palette)
            && Equals(ClipboardPolicy, other.ClipboardPolicy);
    }

    public TerminalProfile(
        TerminalProfileId id,
        string name,
        string fontFamily,
        double fontSize,
        double lineHeight,
        TerminalCursorStyle cursorStyle,
        bool cursorBlink,
        int scrollbackLines,
        TerminalPalette palette,
        KeymapProfileId keymapId,
        TerminalClipboardPolicy? clipboardPolicy = null,
        TerminalLinkPolicy linkPolicy = TerminalLinkPolicy.ConfirmBeforeOpen,
        bool imeEnabled = true,
        TerminalShellIntegrationMode shellIntegration = TerminalShellIntegrationMode.Detect,
        TerminalBellMode bellMode = TerminalBellMode.Visual,
        TerminalCompatibilityProfile compatibility = TerminalCompatibilityProfile.Ghostty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        ArgumentNullException.ThrowIfNull(palette);

        if (!double.IsFinite(fontSize) || fontSize is < 6 or > 96)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize), fontSize, "Font size must be between 6 and 96 points.");
        }

        if (!double.IsFinite(lineHeight) || lineHeight is < 0.8 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(lineHeight), lineHeight, "Line height must be between 0.8 and 3.");
        }

        if (scrollbackLines is < 0 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scrollbackLines),
                scrollbackLines,
                "Scrollback must be between 0 and 10,000,000 lines.");
        }

        if (fontFamily.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("A terminal font family must fit on one configuration line.", nameof(fontFamily));
        }

        if (!Enum.IsDefined(cursorStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(cursorStyle), cursorStyle, "Unknown terminal cursor style.");
        }

        if (!Enum.IsDefined(linkPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(linkPolicy), linkPolicy, "Unknown terminal link policy.");
        }

        if (!Enum.IsDefined(shellIntegration))
        {
            throw new ArgumentOutOfRangeException(
                nameof(shellIntegration),
                shellIntegration,
                "Unknown shell-integration mode.");
        }

        if (!Enum.IsDefined(bellMode))
        {
            throw new ArgumentOutOfRangeException(nameof(bellMode), bellMode, "Unknown terminal bell mode.");
        }

        if (!Enum.IsDefined(compatibility))
        {
            throw new ArgumentOutOfRangeException(
                nameof(compatibility),
                compatibility,
                "Unknown terminal compatibility profile.");
        }

        Id = id;
        Name = name;
        FontFamily = fontFamily;
        FontSize = fontSize;
        LineHeight = lineHeight;
        CursorStyle = cursorStyle;
        CursorBlink = cursorBlink;
        ScrollbackLines = scrollbackLines;
        Palette = palette;
        KeymapId = keymapId;
        ClipboardPolicy = clipboardPolicy ?? TerminalClipboardPolicy.Default;
        LinkPolicy = linkPolicy;
        ImeEnabled = imeEnabled;
        ShellIntegration = shellIntegration;
        BellMode = bellMode;
        Compatibility = compatibility;
    }

    public static DefinitionKind Kind => DefinitionKind.TerminalProfile;

    public TerminalProfileId Id { get; }

    public int SchemaVersion => CurrentSchemaVersion;

    public string Name { get; }

    public string FontFamily { get; }

    public double FontSize { get; }

    public double LineHeight { get; }

    public TerminalCursorStyle CursorStyle { get; }

    public bool CursorBlink { get; }

    public int ScrollbackLines { get; }

    public TerminalPalette Palette { get; }

    public KeymapProfileId KeymapId { get; }

    public TerminalClipboardPolicy ClipboardPolicy { get; }

    public TerminalLinkPolicy LinkPolicy { get; }

    public bool ImeEnabled { get; }

    public TerminalShellIntegrationMode ShellIntegration { get; }

    public TerminalBellMode BellMode { get; }

    public TerminalCompatibilityProfile Compatibility { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);
}
