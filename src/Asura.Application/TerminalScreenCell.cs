using System.Collections.ObjectModel;

namespace Asura.Application;

public enum TerminalColorMode
{
    Default,
    Indexed,
    Rgb,
}

[Flags]
public enum TerminalCellStyle
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Invisible = 1 << 6,
    Strikethrough = 1 << 7,
    Overline = 1 << 8,
}

public sealed record TerminalCellColor
{
    public TerminalCellColor(TerminalColorMode Mode, int Value)
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        var maximum = Mode switch
        {
            TerminalColorMode.Default => 0,
            TerminalColorMode.Indexed => 255,
            TerminalColorMode.Rgb => 0xFFFFFF,
            _ => 0,
        };
        if (Value < 0 || Value > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(Value));
        }

        this.Mode = Mode;
        this.Value = Value;
    }

    public TerminalColorMode Mode { get; }

    public int Value { get; }

    public static TerminalCellColor Default { get; } = new(TerminalColorMode.Default, 0);
}

public sealed record TerminalScreenCell
{
    public TerminalScreenCell(
        string Text,
        int Width,
        TerminalCellColor Foreground,
        TerminalCellColor Background,
        TerminalCellStyle Style = TerminalCellStyle.None,
        string? Hyperlink = null,
        bool IsSelected = false)
    {
        ArgumentNullException.ThrowIfNull(Text);
        ArgumentNullException.ThrowIfNull(Foreground);
        ArgumentNullException.ThrowIfNull(Background);
        if (Width is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(Width));
        }

        if (!Enum.IsDefined(Style) && (Style & ~AllStyles) != TerminalCellStyle.None)
        {
            throw new ArgumentOutOfRangeException(nameof(Style));
        }

        this.Text = Text;
        this.Width = Width;
        this.Foreground = Foreground;
        this.Background = Background;
        this.Style = Style;
        this.Hyperlink = Hyperlink;
        this.IsSelected = IsSelected;
    }

    public string Text { get; }

    public int Width { get; }

    public TerminalCellColor Foreground { get; }

    public TerminalCellColor Background { get; }

    public TerminalCellStyle Style { get; }

    public string? Hyperlink { get; }

    public bool IsSelected { get; }

    private const TerminalCellStyle AllStyles =
        TerminalCellStyle.Bold
        | TerminalCellStyle.Dim
        | TerminalCellStyle.Italic
        | TerminalCellStyle.Underline
        | TerminalCellStyle.Blink
        | TerminalCellStyle.Inverse
        | TerminalCellStyle.Invisible
        | TerminalCellStyle.Strikethrough
        | TerminalCellStyle.Overline;
}

public sealed record TerminalScreenRow
{
    public TerminalScreenRow(int Index, IReadOnlyList<TerminalScreenCell> Cells, bool IsWrapped = false)
    {
        if (Index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Index));
        }

        ArgumentNullException.ThrowIfNull(Cells);
        var snapshot = new TerminalScreenCell[Cells.Count];
        for (var index = 0; index < Cells.Count; index++)
        {
            snapshot[index] = Cells[index]
                ?? throw new ArgumentException("Terminal rows cannot contain null cells.", nameof(Cells));
        }

        this.Index = Index;
        this.Cells = new ReadOnlyCollection<TerminalScreenCell>(snapshot);
        this.IsWrapped = IsWrapped;
    }

    public int Index { get; }

    public IReadOnlyList<TerminalScreenCell> Cells { get; }

    public bool IsWrapped { get; }
}
