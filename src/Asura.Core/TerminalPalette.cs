using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// Terminal colors are deliberately owned by the terminal profile rather than the
/// application theme, so following a light host appearance never rewrites a pinned palette.
/// </summary>
public sealed record TerminalPalette
{
    [JsonConstructor]
    public TerminalPalette(
        string name,
        RgbColor foreground,
        RgbColor background,
        RgbColor cursor,
        RgbColor selectionBackground,
        IReadOnlyList<RgbColor> ansiColors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(ansiColors);

        var colors = ansiColors.ToArray();
        if (colors.Length != 16)
        {
            throw new ArgumentException(
                "A terminal palette must define the standard and bright variants of all eight ANSI colors.",
                nameof(ansiColors));
        }

        Name = name;
        Foreground = foreground;
        Background = background;
        Cursor = cursor;
        SelectionBackground = selectionBackground;
        AnsiColors = Array.AsReadOnly(colors);
    }

    public string Name { get; }

    public RgbColor Foreground { get; }

    public RgbColor Background { get; }

    public RgbColor Cursor { get; }

    public RgbColor SelectionBackground { get; }

    public IReadOnlyList<RgbColor> AnsiColors { get; }

    public static TerminalPalette AsuraDark { get; } = new(
        "Asura Dark",
        RgbColor.Parse("#E8E4DE"),
        RgbColor.Parse("#111111"),
        RgbColor.Parse("#D9944D"),
        RgbColor.Parse("#4A3828"),
        [
            RgbColor.Parse("#1F1C19"),
            RgbColor.Parse("#D26060"),
            RgbColor.Parse("#72B57B"),
            RgbColor.Parse("#D1A85A"),
            RgbColor.Parse("#6B9BD2"),
            RgbColor.Parse("#B17AC5"),
            RgbColor.Parse("#66B8B2"),
            RgbColor.Parse("#D8D2C8"),
            RgbColor.Parse("#69625B"),
            RgbColor.Parse("#EE7B72"),
            RgbColor.Parse("#91D39A"),
            RgbColor.Parse("#EBC574"),
            RgbColor.Parse("#86B6EA"),
            RgbColor.Parse("#CD98DF"),
            RgbColor.Parse("#83D5CF"),
            RgbColor.Parse("#FFF9F0"),
        ]);

    public static TerminalPalette Midnight { get; } = new(
        "Midnight",
        RgbColor.Parse("#C8D3E0"),
        RgbColor.Parse("#0B1020"),
        RgbColor.Parse("#6B9BD2"),
        RgbColor.Parse("#23304C"),
        [
            RgbColor.Parse("#141A2C"),
            RgbColor.Parse("#E06C75"),
            RgbColor.Parse("#8FBF7F"),
            RgbColor.Parse("#D6B26B"),
            RgbColor.Parse("#5C9CE6"),
            RgbColor.Parse("#A57BD1"),
            RgbColor.Parse("#5FB3B3"),
            RgbColor.Parse("#C8D3E0"),
            RgbColor.Parse("#3B4663"),
            RgbColor.Parse("#F08B94"),
            RgbColor.Parse("#A9D69B"),
            RgbColor.Parse("#EBCB8B"),
            RgbColor.Parse("#83B7F0"),
            RgbColor.Parse("#C39BE4"),
            RgbColor.Parse("#7FD1D1"),
            RgbColor.Parse("#EEF3FA"),
        ]);

    public static TerminalPalette Solarized { get; } = new(
        "Solarized",
        RgbColor.Parse("#93A1A1"),
        RgbColor.Parse("#002B36"),
        RgbColor.Parse("#93A1A1"),
        RgbColor.Parse("#073642"),
        [
            RgbColor.Parse("#073642"),
            RgbColor.Parse("#DC322F"),
            RgbColor.Parse("#859900"),
            RgbColor.Parse("#B58900"),
            RgbColor.Parse("#268BD2"),
            RgbColor.Parse("#D33682"),
            RgbColor.Parse("#2AA198"),
            RgbColor.Parse("#EEE8D5"),
            RgbColor.Parse("#002B36"),
            RgbColor.Parse("#CB4B16"),
            RgbColor.Parse("#586E75"),
            RgbColor.Parse("#657B83"),
            RgbColor.Parse("#839496"),
            RgbColor.Parse("#6C71C4"),
            RgbColor.Parse("#93A1A1"),
            RgbColor.Parse("#FDF6E3"),
        ]);

    public static TerminalPalette Dracula { get; } = new(
        "Dracula",
        RgbColor.Parse("#F8F8F2"),
        RgbColor.Parse("#282A36"),
        RgbColor.Parse("#F8F8F2"),
        RgbColor.Parse("#44475A"),
        [
            RgbColor.Parse("#21222C"),
            RgbColor.Parse("#FF5555"),
            RgbColor.Parse("#50FA7B"),
            RgbColor.Parse("#F1FA8C"),
            RgbColor.Parse("#BD93F9"),
            RgbColor.Parse("#FF79C6"),
            RgbColor.Parse("#8BE9FD"),
            RgbColor.Parse("#F8F8F2"),
            RgbColor.Parse("#6272A4"),
            RgbColor.Parse("#FF6E6E"),
            RgbColor.Parse("#69FF94"),
            RgbColor.Parse("#FFFFA5"),
            RgbColor.Parse("#D6ACFF"),
            RgbColor.Parse("#FF92DF"),
            RgbColor.Parse("#A4FFFF"),
            RgbColor.Parse("#FFFFFF"),
        ]);

    public static TerminalPalette Nord { get; } = new(
        "Nord",
        RgbColor.Parse("#D8DEE9"),
        RgbColor.Parse("#2E3440"),
        RgbColor.Parse("#D8DEE9"),
        RgbColor.Parse("#434C5E"),
        [
            RgbColor.Parse("#3B4252"),
            RgbColor.Parse("#BF616A"),
            RgbColor.Parse("#A3BE8C"),
            RgbColor.Parse("#EBCB8B"),
            RgbColor.Parse("#81A1C1"),
            RgbColor.Parse("#B48EAD"),
            RgbColor.Parse("#88C0D0"),
            RgbColor.Parse("#E5E9F0"),
            RgbColor.Parse("#4C566A"),
            RgbColor.Parse("#BF616A"),
            RgbColor.Parse("#A3BE8C"),
            RgbColor.Parse("#EBCB8B"),
            RgbColor.Parse("#81A1C1"),
            RgbColor.Parse("#B48EAD"),
            RgbColor.Parse("#8FBCBB"),
            RgbColor.Parse("#ECEFF4"),
        ]);

    public static TerminalPalette Light { get; } = new(
        "Light",
        RgbColor.Parse("#2B2B2B"),
        RgbColor.Parse("#FAFAF7"),
        RgbColor.Parse("#B8793A"),
        RgbColor.Parse("#DCD9CF"),
        [
            RgbColor.Parse("#3B3B3B"),
            RgbColor.Parse("#C0392B"),
            RgbColor.Parse("#2E7D32"),
            RgbColor.Parse("#A17A16"),
            RgbColor.Parse("#2A6FB0"),
            RgbColor.Parse("#8E44AD"),
            RgbColor.Parse("#17807A"),
            RgbColor.Parse("#E8E4DE"),
            RgbColor.Parse("#6B6B6B"),
            RgbColor.Parse("#D94A3A"),
            RgbColor.Parse("#3E9E45"),
            RgbColor.Parse("#C49A22"),
            RgbColor.Parse("#3B8AD1"),
            RgbColor.Parse("#A85FC4"),
            RgbColor.Parse("#1FA39B"),
            RgbColor.Parse("#FFFFFF"),
        ]);

    /// <summary>
    /// The palettes offered as presets, in the order the settings page shows
    /// them. A profile may still hold a palette that is not in this list; that is
    /// what <see cref="Matches"/> is for.
    /// </summary>
    public static IReadOnlyList<TerminalPalette> Presets { get; } =
    [
        AsuraDark,
        Midnight,
        Solarized,
        Dracula,
        Nord,
        Light,
    ];

    /// <summary>
    /// Compares the colours rather than the name, so a profile whose palette was
    /// hand-edited back to a preset's exact values is recognised as that preset,
    /// and one that merely reuses a preset's name is not.
    /// </summary>
    public bool Matches(TerminalPalette other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Foreground == other.Foreground
            && Background == other.Background
            && Cursor == other.Cursor
            && SelectionBackground == other.SelectionBackground
            && AnsiColors.SequenceEqual(other.AnsiColors);
    }
}
