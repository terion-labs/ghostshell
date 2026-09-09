using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Asura.Application;
using Asura.Core;
using Avalonia;
using Avalonia.Media;

namespace Asura.App.Controls;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct TerminalCellMetrics(
    double CellWidth,
    double CellHeight,
    Thickness Padding,
    int Columns,
    int Rows)
{
    private static readonly ConcurrentDictionary<FontMetricKey, (double Width, double Height)> FontMetrics = new();

    public static TerminalCellMetrics Measure(
        Size availableSize,
        TerminalRenderProfileSnapshot? profile)
    {
        var fontSize = profile?.FontSize ?? 13;
        var lineHeight = profile?.LineHeight ?? 1;
        var typeface = TerminalTypefaceResolver.Resolve(profile?.FontFamily);
        var padding = new Thickness(12, 10);
        var metricKey = new FontMetricKey(typeface, fontSize);
        var measured = FontMetrics.TryGetValue(metricKey, out var cached)
            ? cached
            : MeasureFont(metricKey);
        var cellWidth = Math.Max(1, measured.Width);
        var cellHeight = Math.Max(1, measured.Height * lineHeight);
        var contentWidth = Math.Max(0, availableSize.Width - padding.Left - padding.Right);
        var contentHeight = Math.Max(0, availableSize.Height - padding.Top - padding.Bottom);
        var columns = Math.Clamp((int)Math.Floor(contentWidth / cellWidth), 2, 1_000);
        var rows = Math.Clamp((int)Math.Floor(contentHeight / cellHeight), 1, 1_000);
        return new TerminalCellMetrics(cellWidth, cellHeight, padding, columns, rows);
    }

    public ViewportDescriptor ToViewport(Size availableSize, double renderScale) => new(
        Math.Max(0, availableSize.Width - Padding.Left - Padding.Right),
        Math.Max(0, availableSize.Height - Padding.Top - Padding.Bottom),
        Math.Max(0.1, renderScale),
        Columns,
        Rows);

    public Rect CellBounds(int row, int column, int width = 1) => new(
        Padding.Left + column * CellWidth,
        Padding.Top + row * CellHeight,
        Math.Max(1, width) * CellWidth,
        CellHeight);

    public (int Column, int Row) CellAt(Point point) => (
        Math.Clamp((int)Math.Floor((point.X - Padding.Left) / CellWidth), 0, Columns - 1),
        Math.Clamp((int)Math.Floor((point.Y - Padding.Top) / CellHeight), 0, Rows - 1));

    private readonly record struct FontMetricKey(Typeface Typeface, double FontSize);

    private static (double Width, double Height) MeasureFont(FontMetricKey key)
    {
        try
        {
            var glyph = new FormattedText(
                "M",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                key.Typeface,
                key.FontSize,
                Brushes.White);
            var measured = (glyph.Width, glyph.Height);
            FontMetrics.TryAdd(key, measured);
            return measured;
        }
        catch (InvalidOperationException)
        {
            // Design-time and plain unit-test processes do not install Avalonia's
            // platform font manager. The desktop process always takes the shaped path.
            return (key.FontSize * 0.62, key.FontSize * 1.2);
        }
    }
}

internal sealed record TerminalDrawCell(
    int Row,
    int Column,
    int Width,
    string Text,
    Color Foreground,
    Color Background,
    bool UsesDefaultBackground,
    TerminalRenderCellStyle Style,
    TerminalUnderlineKind Underline,
    Color? UnderlineColor,
    string? Hyperlink,
    bool IsSelected);

internal sealed record TerminalDrawCursor(
    int Row,
    int Column,
    int Width,
    TerminalCursorVisualStyle VisualStyle,
    bool IsInViewport,
    bool IsVisible,
    bool IsBlinking,
    bool IsPasswordInput,
    Color? Color);

internal sealed record TerminalDrawFrame(
    IReadOnlyList<TerminalDrawCell> Cells,
    TerminalDrawCursor Cursor,
    TerminalKittyGraphicsFrame KittyGraphics);

internal static class TerminalRenderLayout
{
    public static TerminalDrawFrame Create(
        TerminalScreenSnapshot snapshot,
        TerminalRenderProfileSnapshot? profile,
        TerminalCellMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var palette = profile?.Palette ?? TerminalPalette.AsuraDark;
        var defaultBackground = TerminalCellColors.Resolve(
            TerminalCellColor.Default,
            palette,
            foreground: false);
        var cells = new List<TerminalDrawCell>(
            Math.Min(metrics.Rows * metrics.Columns, 32_768));

        foreach (var row in snapshot.StructuredRows)
        {
            if (row.Index >= metrics.Rows)
            {
                break;
            }

            var column = 0;
            foreach (var cell in row.Cells)
            {
                if (cell.Width == 0)
                {
                    continue;
                }

                if (column >= metrics.Columns)
                {
                    break;
                }

                var width = Math.Min(cell.Width, metrics.Columns - column);
                var colors = TerminalCellColors.Resolve(cell, palette);
                cells.Add(new TerminalDrawCell(
                    row.Index,
                    column,
                    width,
                    cell.Text,
                    colors.Foreground,
                    colors.Background,
                    UsesDefaultBackground(cell) || colors.Background == defaultBackground,
                    ConvertStyle(cell.Style),
                    cell.Style.HasFlag(TerminalCellStyle.Underline)
                        ? TerminalUnderlineKind.Single
                        : TerminalUnderlineKind.None,
                    null,
                    cell.Hyperlink,
                    cell.IsSelected));
                column += cell.Width;
            }
        }

        return new TerminalDrawFrame(
            cells,
            new TerminalDrawCursor(
                Math.Clamp(snapshot.CursorRow, 0, metrics.Rows - 1),
                Math.Clamp(snapshot.CursorColumn, 0, metrics.Columns - 1),
                1,
                profile?.CursorStyle switch
                {
                    TerminalCursorStyle.Bar => TerminalCursorVisualStyle.Bar,
                    TerminalCursorStyle.Underline => TerminalCursorVisualStyle.Underline,
                    _ => TerminalCursorVisualStyle.Block,
                },
                IsInViewport: snapshot.Rows > 0 && snapshot.Columns > 0,
                snapshot.IsCursorVisible,
                profile?.CursorBlink == true,
                IsPasswordInput: false,
                Color: null),
            TerminalKittyGraphicsFrame.Empty);
    }

    public static TerminalDrawFrame Create(
        Asura.Application.TerminalRenderFrame frame,
        TerminalRenderProfileSnapshot? profile,
        TerminalCellMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var palette = profile?.Palette ?? TerminalPalette.AsuraDark;
        var defaultBackground = TerminalCellColors.Resolve(
            TerminalCellColor.Default,
            palette,
            foreground: false);
        var cells = new List<TerminalDrawCell>(
            Math.Min(frame.Rows * frame.Columns, 32_768));
        var visibleRows = Math.Min(frame.Rows, metrics.Rows);
        var visibleColumns = Math.Min(frame.Columns, metrics.Columns);

        for (var rowIndex = 0; rowIndex < visibleRows; rowIndex++)
        {
            var row = frame.ViewportRows[rowIndex];
            for (var column = 0; column < visibleColumns; column++)
            {
                var cell = row.Cells[column];
                var width = cell.Width == TerminalRenderCellWidth.Wide ? 2 : 1;
                width = Math.Min(width, visibleColumns - column);
                var colors = TerminalCellColors.Resolve(cell, palette);
                Color? underlineColor = cell.UnderlineColor is null
                    ? null
                    : TerminalCellColors.Resolve(cell.UnderlineColor, palette, foreground: true);
                cells.Add(new TerminalDrawCell(
                    rowIndex,
                    column,
                    width,
                    cell.HasText ? cell.Text : string.Empty,
                    colors.Foreground,
                    colors.Background,
                    UsesDefaultBackground(cell) || colors.Background == defaultBackground,
                    cell.Style,
                    cell.Underline,
                    underlineColor,
                    cell.Hyperlink,
                    cell.IsSelected));
            }
        }

        var cursorInDrawViewport = frame.Cursor is
        {
            ViewportRow: int viewportRow,
            ViewportColumn: int viewportColumn,
        }
            && viewportRow < visibleRows
            && viewportColumn < visibleColumns;
        var cursorRow = cursorInDrawViewport
            ? frame.Cursor.ViewportRow!.Value
            : 0;
        var cursorColumn = cursorInDrawViewport
            ? frame.Cursor.ViewportColumn!.Value
            : 0;
        var cursorWidth = 1;
        if (cursorInDrawViewport && frame.Cursor.IsWideCharacterTail && cursorColumn > 0)
        {
            cursorColumn--;
            cursorWidth = Math.Min(2, metrics.Columns - cursorColumn);
        }
        else if (cursorInDrawViewport
            && frame.ViewportRows[cursorRow]
                .Cells[cursorColumn]
                .Width == TerminalRenderCellWidth.Wide)
        {
            cursorWidth = Math.Min(2, metrics.Columns - cursorColumn);
        }

        return new TerminalDrawFrame(
            cells,
            new TerminalDrawCursor(
                cursorRow,
                cursorColumn,
                cursorWidth,
                frame.Cursor.VisualStyle,
                cursorInDrawViewport,
                frame.Cursor.IsVisible,
                frame.Cursor.IsBlinking,
                frame.Cursor.IsPasswordInput,
                frame.Cursor.Color is null
                    ? null
                    : TerminalCellColors.Resolve(
                        frame.Cursor.Color,
                        palette,
                        foreground: true)),
            frame.KittyGraphics);
    }

    // A cell whose resolved background matches the palette's default background
    // is treated as default even when the application painted it explicitly.
    // Multiplexed sessions (the SSH continuity path attaches through tmux or
    // Screen) and full-screen programs clear regions with an explicit
    // background-color-erase in the theme's own background, and the emulator
    // reports those cells as concrete RGB. Without the color comparison every
    // such cell is painted at full alpha and the panel silently stops following
    // the translucency setting, which is why remote terminals read opaque while
    // local ones follow the glass. Desktop terminals with background opacity
    // treat default-colored cells the same way.
    private static bool UsesDefaultBackground(TerminalScreenCell cell) =>
        cell.Background.Mode == TerminalColorMode.Default
        && !cell.IsSelected
        && !cell.Style.HasFlag(TerminalCellStyle.Inverse);

    private static bool UsesDefaultBackground(
        Asura.Application.TerminalRenderCell cell) =>
        cell.Background.Mode == TerminalColorMode.Default
        && !cell.IsSelected
        && !cell.Style.HasFlag(TerminalRenderCellStyle.Inverse);

    private static TerminalRenderCellStyle ConvertStyle(TerminalCellStyle style)
    {
        var result = TerminalRenderCellStyle.None;
        result |= style.HasFlag(TerminalCellStyle.Bold)
            ? TerminalRenderCellStyle.Bold
            : TerminalRenderCellStyle.None;
        result |= style.HasFlag(TerminalCellStyle.Dim)
            ? TerminalRenderCellStyle.Faint
            : TerminalRenderCellStyle.None;
        result |= style.HasFlag(TerminalCellStyle.Italic)
            ? TerminalRenderCellStyle.Italic
            : TerminalRenderCellStyle.None;
        result |= style.HasFlag(TerminalCellStyle.Blink)
            ? TerminalRenderCellStyle.Blink
            : TerminalRenderCellStyle.None;
        result |= style.HasFlag(TerminalCellStyle.Inverse)
            ? TerminalRenderCellStyle.Inverse
            : TerminalRenderCellStyle.None;
        result |= style.HasFlag(TerminalCellStyle.Invisible)
            ? TerminalRenderCellStyle.Invisible
            : TerminalRenderCellStyle.None;
        result |= style.HasFlag(TerminalCellStyle.Strikethrough)
            ? TerminalRenderCellStyle.Strikethrough
            : TerminalRenderCellStyle.None;
        result |= style.HasFlag(TerminalCellStyle.Overline)
            ? TerminalRenderCellStyle.Overline
            : TerminalRenderCellStyle.None;
        return result;
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct TerminalResolvedColors(Color Foreground, Color Background);

internal static class TerminalCellColors
{
    public static TerminalResolvedColors Resolve(
        TerminalScreenCell cell,
        TerminalPalette palette)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(palette);
        var foreground = Resolve(cell.Foreground, palette, foreground: true);
        var background = Resolve(cell.Background, palette, foreground: false);
        if (cell.Style.HasFlag(TerminalCellStyle.Inverse))
        {
            (foreground, background) = (background, foreground);
        }

        if (cell.Style.HasFlag(TerminalCellStyle.Dim))
        {
            foreground = Color.FromArgb(166, foreground.R, foreground.G, foreground.B);
        }

        if (cell.Style.HasFlag(TerminalCellStyle.Invisible))
        {
            foreground = background;
        }

        if (cell.IsSelected)
        {
            foreground = FromRgb(palette.Foreground);
            background = FromRgb(palette.SelectionBackground);
        }

        return new TerminalResolvedColors(foreground, background);
    }

    public static TerminalResolvedColors Resolve(
        Asura.Application.TerminalRenderCell cell,
        TerminalPalette palette)
    {
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(palette);
        var foreground = Resolve(cell.Foreground, palette, foreground: true);
        var background = Resolve(cell.Background, palette, foreground: false);
        if (cell.Style.HasFlag(TerminalRenderCellStyle.Inverse))
        {
            (foreground, background) = (background, foreground);
        }

        if (cell.Style.HasFlag(TerminalRenderCellStyle.Faint))
        {
            foreground = Color.FromArgb(166, foreground.R, foreground.G, foreground.B);
        }

        if (cell.Style.HasFlag(TerminalRenderCellStyle.Invisible))
        {
            foreground = background;
        }

        if (cell.IsSelected)
        {
            foreground = FromRgb(palette.Foreground);
            background = FromRgb(palette.SelectionBackground);
        }

        return new TerminalResolvedColors(foreground, background);
    }

    public static Color Resolve(
        TerminalCellColor color,
        TerminalPalette palette,
        bool foreground)
    {
        ArgumentNullException.ThrowIfNull(color);
        ArgumentNullException.ThrowIfNull(palette);
        return color.Mode switch
        {
            TerminalColorMode.Default => FromRgb(
                foreground ? palette.Foreground : palette.Background),
            TerminalColorMode.Rgb => Color.FromRgb(
                (byte)((color.Value >> 16) & 0xFF),
                (byte)((color.Value >> 8) & 0xFF),
                (byte)(color.Value & 0xFF)),
            TerminalColorMode.Indexed => Indexed(color.Value, palette),
            _ => throw new ArgumentOutOfRangeException(
                nameof(color),
                color.Mode,
                "Unknown terminal color mode."),
        };
    }

    private static Color Indexed(int index, TerminalPalette palette)
    {
        if (index < 16)
        {
            return FromRgb(palette.AnsiColors[index]);
        }

        if (index < 232)
        {
            var cube = index - 16;
            var red = cube / 36;
            var green = cube / 6 % 6;
            var blue = cube % 6;
            return Color.FromRgb(Cube(red), Cube(green), Cube(blue));
        }

        var gray = (byte)(8 + (index - 232) * 10);
        return Color.FromRgb(gray, gray, gray);
    }

    private static byte Cube(int component) => component == 0
        ? (byte)0
        : (byte)(55 + component * 40);

    private static Color FromRgb(RgbColor color) =>
        Color.FromRgb(color.Red, color.Green, color.Blue);
}
