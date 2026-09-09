using Asura.Core;

namespace Asura.Application;

/// <summary>
/// The terminal-profile values supported by the native rendering boundary.
/// </summary>
public sealed record TerminalRenderProfileSnapshot
{
    public TerminalRenderProfileSnapshot(
        double fontSize,
        TerminalCursorStyle cursorStyle,
        bool cursorBlink,
        int scrollbackLines,
        TerminalPalette palette,
        string fontFamily = "monospace",
        double lineHeight = 1,
        TerminalClipboardPolicy? clipboardPolicy = null,
        TerminalLinkPolicy linkPolicy = TerminalLinkPolicy.ConfirmBeforeOpen,
        bool imeEnabled = true,
        TerminalShellIntegrationMode shellIntegration = TerminalShellIntegrationMode.Detect,
        TerminalBellMode bellMode = TerminalBellMode.Visual,
        TerminalCompatibilityProfile compatibility = TerminalCompatibilityProfile.Ghostty)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        if (!double.IsFinite(fontSize) || fontSize is < 6 or > 96)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fontSize),
                fontSize,
                "Font size must be between 6 and 96 points.");
        }

        if (!double.IsFinite(lineHeight) || lineHeight is < 0.8 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lineHeight),
                lineHeight,
                "Line height must be between 0.8 and 3.");
        }

        if (fontFamily.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("A terminal font family must fit on one configuration line.", nameof(fontFamily));
        }

        if (!Enum.IsDefined(cursorStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(cursorStyle), cursorStyle, "Unknown terminal cursor style.");
        }

        if (scrollbackLines is < 0 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scrollbackLines),
                scrollbackLines,
                "Scrollback must be between 0 and 10,000,000 lines.");
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

        FontFamily = fontFamily;
        FontSize = fontSize;
        LineHeight = lineHeight;
        CursorStyle = cursorStyle;
        CursorBlink = cursorBlink;
        ScrollbackLines = scrollbackLines;
        Palette = new TerminalPalette(
            palette.Name,
            palette.Foreground,
            palette.Background,
            palette.Cursor,
            palette.SelectionBackground,
            palette.AnsiColors);
        ClipboardPolicy = clipboardPolicy ?? TerminalClipboardPolicy.Default;
        LinkPolicy = linkPolicy;
        ImeEnabled = imeEnabled;
        ShellIntegration = shellIntegration;
        BellMode = bellMode;
        Compatibility = compatibility;
    }

    public string FontFamily { get; }

    public double FontSize { get; }

    public double LineHeight { get; }

    public TerminalCursorStyle CursorStyle { get; }

    public bool CursorBlink { get; }

    public int ScrollbackLines { get; }

    public TerminalPalette Palette { get; }

    /// <summary>
    /// Whether two snapshots would draw the same terminal.
    ///
    /// Record equality cannot answer this: the palette holds its ANSI colours in
    /// an array, so two palettes built from identical colours are never equal.
    /// Without this, re-publishing the saved profile would look like a change on
    /// every catalog refresh and relayout the terminal each time.
    /// </summary>
    public bool RendersSameAs(TerminalRenderProfileSnapshot? other) =>
        other is not null
        && string.Equals(FontFamily, other.FontFamily, StringComparison.Ordinal)
        && FontSize.Equals(other.FontSize)
        && LineHeight.Equals(other.LineHeight)
        && CursorStyle == other.CursorStyle
        && CursorBlink == other.CursorBlink
        && Palette.Matches(other.Palette);

    public TerminalClipboardPolicy ClipboardPolicy { get; }

    public TerminalLinkPolicy LinkPolicy { get; }

    public bool ImeEnabled { get; }

    public TerminalShellIntegrationMode ShellIntegration { get; }

    public TerminalBellMode BellMode { get; }

    public TerminalCompatibilityProfile Compatibility { get; }

    public static TerminalRenderProfileSnapshot FromProfile(TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new TerminalRenderProfileSnapshot(
            profile.FontSize,
            profile.CursorStyle,
            profile.CursorBlink,
            profile.ScrollbackLines,
            profile.Palette,
            profile.FontFamily,
            profile.LineHeight,
            profile.ClipboardPolicy,
            profile.LinkPolicy,
            profile.ImeEnabled,
            profile.ShellIntegration,
            profile.BellMode,
            profile.Compatibility);
    }
}
