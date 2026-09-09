using System.Collections.ObjectModel;

namespace Asura.Application;

/// <summary>
/// The width role of one physical grid cell. Wide-character spacer cells stay
/// explicit so cursor tails and damage line up with libghostty-vt's grid.
/// </summary>
public enum TerminalRenderCellWidth
{
    Narrow,
    Wide,
    SpacerTail,
    SpacerHead,
}

[Flags]
public enum TerminalRenderCellStyle
{
    None = 0,
    Bold = 1 << 0,
    Faint = 1 << 1,
    Italic = 1 << 2,
    Blink = 1 << 3,
    Inverse = 1 << 4,
    Invisible = 1 << 5,
    Strikethrough = 1 << 6,
    Overline = 1 << 7,
    Protected = 1 << 8,
}

/// <summary>SGR underline styles represented by Ghostty terminal state.</summary>
public enum TerminalUnderlineKind
{
    None,
    Single,
    Double,
    Curly,
    Dotted,
    Dashed,
}

/// <summary>OSC 133 semantic content attached to a terminal cell.</summary>
public enum TerminalCellSemanticRole
{
    Output,
    Input,
    Prompt,
}

/// <summary>OSC 133 prompt classification attached to a terminal row.</summary>
public enum TerminalRowSemanticRole
{
    None,
    Prompt,
    PromptContinuation,
}

public sealed record TerminalRenderCell
{
    public const int MaximumTextLength = 4_096;
    public const int MaximumHyperlinkLength = 32_768;

    public TerminalRenderCell(
        string Text,
        TerminalRenderCellWidth Width,
        TerminalCellColor Foreground,
        TerminalCellColor Background,
        TerminalRenderCellStyle Style = TerminalRenderCellStyle.None,
        TerminalUnderlineKind Underline = TerminalUnderlineKind.None,
        TerminalCellColor? UnderlineColor = null,
        TerminalCellSemanticRole SemanticRole = TerminalCellSemanticRole.Output,
        string? Hyperlink = null,
        bool IsSelected = false)
    {
        ArgumentNullException.ThrowIfNull(Text);
        ArgumentNullException.ThrowIfNull(Foreground);
        ArgumentNullException.ThrowIfNull(Background);
        if (Text.Length > MaximumTextLength)
        {
            throw new ArgumentException(
                $"A render cell cannot contain more than {MaximumTextLength:N0} UTF-16 code units.",
                nameof(Text));
        }

        if (!Enum.IsDefined(Width))
        {
            throw new ArgumentOutOfRangeException(nameof(Width));
        }

        if (Width is TerminalRenderCellWidth.SpacerHead or TerminalRenderCellWidth.SpacerTail
            && Text.Length != 0)
        {
            throw new ArgumentException(
                "Wide-character spacer cells cannot contain text.",
                nameof(Text));
        }

        if ((Style & ~AllStyles) != TerminalRenderCellStyle.None)
        {
            throw new ArgumentOutOfRangeException(nameof(Style));
        }

        if (!Enum.IsDefined(Underline))
        {
            throw new ArgumentOutOfRangeException(nameof(Underline));
        }

        if (!Enum.IsDefined(SemanticRole))
        {
            throw new ArgumentOutOfRangeException(nameof(SemanticRole));
        }

        if (Underline == TerminalUnderlineKind.None && UnderlineColor is not null)
        {
            throw new ArgumentException(
                "An underline color requires an active underline style.",
                nameof(UnderlineColor));
        }

        if (UnderlineColor is { Mode: TerminalColorMode.Default })
        {
            throw new ArgumentException(
                "Use null when an underline inherits the cell foreground color.",
                nameof(UnderlineColor));
        }

        if (Hyperlink?.Length > MaximumHyperlinkLength)
        {
            throw new ArgumentException(
                $"A render-cell hyperlink cannot exceed {MaximumHyperlinkLength:N0} characters.",
                nameof(Hyperlink));
        }

        this.Text = Text;
        this.Width = Width;
        this.Foreground = Foreground;
        this.Background = Background;
        this.Style = Style;
        this.Underline = Underline;
        this.UnderlineColor = UnderlineColor;
        this.SemanticRole = SemanticRole;
        this.Hyperlink = Hyperlink;
        this.IsSelected = IsSelected;
    }

    public string Text { get; }

    public TerminalRenderCellWidth Width { get; }

    public TerminalCellColor Foreground { get; }

    public TerminalCellColor Background { get; }

    public TerminalRenderCellStyle Style { get; }

    public TerminalUnderlineKind Underline { get; }

    /// <summary>Null means the underline inherits the effective foreground.</summary>
    public TerminalCellColor? UnderlineColor { get; }

    public TerminalCellSemanticRole SemanticRole { get; }

    public string? Hyperlink { get; }

    public bool IsSelected { get; }

    public bool HasText => Width is not (
        TerminalRenderCellWidth.SpacerHead or TerminalRenderCellWidth.SpacerTail)
        && Text.Length != 0;

    private const TerminalRenderCellStyle AllStyles =
        TerminalRenderCellStyle.Bold
        | TerminalRenderCellStyle.Faint
        | TerminalRenderCellStyle.Italic
        | TerminalRenderCellStyle.Blink
        | TerminalRenderCellStyle.Inverse
        | TerminalRenderCellStyle.Invisible
        | TerminalRenderCellStyle.Strikethrough
        | TerminalRenderCellStyle.Overline
        | TerminalRenderCellStyle.Protected;
}

public sealed record TerminalRenderRow
{
    public TerminalRenderRow(
        int Index,
        IReadOnlyList<TerminalRenderCell> Cells,
        bool IsWrapped = false,
        bool IsWrapContinuation = false,
        TerminalRowSemanticRole SemanticRole = TerminalRowSemanticRole.None,
        bool ContainsKittyVirtualPlaceholder = false)
    {
        if (Index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Index));
        }

        ArgumentNullException.ThrowIfNull(Cells);
        if (!Enum.IsDefined(SemanticRole))
        {
            throw new ArgumentOutOfRangeException(nameof(SemanticRole));
        }

        var snapshot = new TerminalRenderCell[Cells.Count];
        for (var index = 0; index < Cells.Count; index++)
        {
            snapshot[index] = Cells[index]
                ?? throw new ArgumentException("Render rows cannot contain null cells.", nameof(Cells));
        }

        this.Index = Index;
        this.Cells = new ReadOnlyCollection<TerminalRenderCell>(snapshot);
        this.IsWrapped = IsWrapped;
        this.IsWrapContinuation = IsWrapContinuation;
        this.SemanticRole = SemanticRole;
        this.ContainsKittyVirtualPlaceholder = ContainsKittyVirtualPlaceholder;
    }

    public int Index { get; }

    /// <summary>One entry per physical viewport column, including wide spacers.</summary>
    public IReadOnlyList<TerminalRenderCell> Cells { get; }

    public bool IsWrapped { get; }

    public bool IsWrapContinuation { get; }

    public TerminalRowSemanticRole SemanticRole { get; }

    public bool ContainsKittyVirtualPlaceholder { get; }
}
