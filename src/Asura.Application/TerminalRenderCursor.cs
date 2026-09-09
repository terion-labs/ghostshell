namespace Asura.Application;

/// <summary>
/// The terminal-controlled cursor shape. This is distinct from the profile's
/// fallback cursor shape because DECSCUSR may change it at runtime.
/// </summary>
public enum TerminalCursorVisualStyle
{
    Bar,
    Block,
    Underline,
    HollowBlock,
}

public sealed record TerminalRenderCursor
{
    public TerminalRenderCursor(
        TerminalCursorVisualStyle VisualStyle,
        bool IsVisible,
        bool IsBlinking,
        bool IsPasswordInput,
        int? ViewportRow = null,
        int? ViewportColumn = null,
        bool IsWideCharacterTail = false,
        TerminalCellColor? Color = null)
    {
        if (!Enum.IsDefined(VisualStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(VisualStyle));
        }

        if (ViewportRow.HasValue != ViewportColumn.HasValue)
        {
            throw new ArgumentException(
                "A cursor viewport position requires both a row and a column.",
                nameof(ViewportRow));
        }

        if (ViewportRow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ViewportRow));
        }

        if (ViewportColumn < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ViewportColumn));
        }

        if (IsWideCharacterTail && !ViewportRow.HasValue)
        {
            throw new ArgumentException(
                "A wide-character cursor tail requires a viewport position.",
                nameof(IsWideCharacterTail));
        }

        if (Color is { Mode: not TerminalColorMode.Rgb })
        {
            throw new ArgumentException(
                "An explicit terminal cursor color must be an RGB color.",
                nameof(Color));
        }

        this.VisualStyle = VisualStyle;
        this.IsVisible = IsVisible;
        this.IsBlinking = IsBlinking;
        this.IsPasswordInput = IsPasswordInput;
        this.ViewportRow = ViewportRow;
        this.ViewportColumn = ViewportColumn;
        this.IsWideCharacterTail = IsWideCharacterTail;
        this.Color = Color;
    }

    public TerminalCursorVisualStyle VisualStyle { get; }

    /// <summary>Whether terminal modes permit the cursor to be drawn.</summary>
    public bool IsVisible { get; }

    public bool IsBlinking { get; }

    public bool IsPasswordInput { get; }

    /// <summary>
    /// The cursor row in the current viewport, or <see langword="null"/> when
    /// the cursor is outside the viewport.
    /// </summary>
    public int? ViewportRow { get; }

    public int? ViewportColumn { get; }

    public bool IsWideCharacterTail { get; }

    /// <summary>An explicit terminal cursor color, or null for the profile color.</summary>
    public TerminalCellColor? Color { get; }

    public bool IsInViewport => ViewportRow.HasValue;
}
