using Asura.Application;

namespace Asura.Application.Tests;

public sealed class TerminalRenderContractsTests
{
    private static readonly TerminalCellColor RgbWhite =
        new(TerminalColorMode.Rgb, 0xFFFFFF);
    private static readonly TerminalCellColor RgbOrange =
        new(TerminalColorMode.Rgb, 0xFF7A00);

    [Fact]
    public void Render_frame_captures_rich_cells_damage_and_terminal_cursor_state()
    {
        var sourceCells = new List<TerminalRenderCell>
        {
            new(
                "x",
                TerminalRenderCellWidth.Narrow,
                RgbWhite,
                TerminalCellColor.Default,
                TerminalRenderCellStyle.Bold,
                TerminalUnderlineKind.Curly,
                RgbOrange,
                TerminalCellSemanticRole.Input),
            new(
                string.Empty,
                TerminalRenderCellWidth.SpacerTail,
                RgbWhite,
                TerminalCellColor.Default),
        };
        var sourceRows = new List<TerminalRenderRow>
        {
            new(
                0,
                sourceCells,
                IsWrapped: true,
                IsWrapContinuation: true,
                SemanticRole: TerminalRowSemanticRole.PromptContinuation,
                ContainsKittyVirtualPlaceholder: true),
        };
        var dirtyRows = new List<int> { 0 };
        var frame = new TerminalRenderFrame(
            12,
            1,
            2,
            sourceRows,
            new TerminalRenderCursor(
                TerminalCursorVisualStyle.HollowBlock,
                IsVisible: true,
                IsBlinking: true,
                IsPasswordInput: true,
                ViewportRow: 0,
                ViewportColumn: 1,
                IsWideCharacterTail: true,
                Color: RgbOrange),
            new TerminalRenderDelta(TerminalRenderDamageKind.Partial, dirtyRows));

        sourceCells.Clear();
        sourceRows.Clear();
        dirtyRows.Clear();

        Assert.Equal(12, frame.Revision);
        Assert.Single(frame.ViewportRows);
        Assert.Equal(2, frame.ViewportRows[0].Cells.Count);
        Assert.True(frame.ViewportRows[0].ContainsKittyVirtualPlaceholder);
        Assert.Equal(TerminalCellSemanticRole.Input, frame.ViewportRows[0].Cells[0].SemanticRole);
        Assert.Equal(TerminalUnderlineKind.Curly, frame.ViewportRows[0].Cells[0].Underline);
        Assert.Equal(RgbOrange, frame.ViewportRows[0].Cells[0].UnderlineColor);
        Assert.Equal([0], frame.Delta.DirtyRows);
        Assert.Equal(TerminalCursorVisualStyle.HollowBlock, frame.Cursor.VisualStyle);
        Assert.True(frame.Cursor.IsBlinking);
        Assert.True(frame.Cursor.IsPasswordInput);
        Assert.True(frame.Cursor.IsWideCharacterTail);
        Assert.Equal(RgbOrange, frame.Cursor.Color);
    }

    [Fact]
    public void Render_delta_requires_canonical_dirty_rows()
    {
        Assert.Throws<ArgumentException>(() =>
            new TerminalRenderDelta(TerminalRenderDamageKind.None, [0]));
        Assert.Throws<ArgumentException>(() =>
            new TerminalRenderDelta(TerminalRenderDamageKind.Partial));
        Assert.Throws<ArgumentException>(() =>
            new TerminalRenderDelta(TerminalRenderDamageKind.Partial, [1, 1]));
        Assert.Throws<ArgumentException>(() =>
            new TerminalRenderDelta(TerminalRenderDamageKind.Partial, [2, 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TerminalRenderDelta(TerminalRenderDamageKind.Partial, [-1]));

        var full = new TerminalRenderDelta(TerminalRenderDamageKind.Full, [0]);

        Assert.Equal([0], full.DirtyRows);
    }

    [Fact]
    public void Render_frame_rejects_rows_damage_and_cursor_outside_viewport()
    {
        var row = Row(0, 1);
        var clean = new TerminalRenderDelta(TerminalRenderDamageKind.None);

        Assert.Throws<ArgumentException>(() => new TerminalRenderFrame(
            0,
            1,
            0,
            [],
            Cursor(),
            clean));
        Assert.Throws<ArgumentException>(() => new TerminalRenderFrame(
            0,
            1,
            1,
            [],
            Cursor(),
            clean));
        Assert.Throws<ArgumentException>(() => new TerminalRenderFrame(
            0,
            1,
            2,
            [row],
            Cursor(),
            clean));
        Assert.Throws<ArgumentException>(() => new TerminalRenderFrame(
            0,
            1,
            1,
            [row],
            Cursor(),
            new TerminalRenderDelta(TerminalRenderDamageKind.Partial, [1])));
        Assert.Throws<ArgumentException>(() => new TerminalRenderFrame(
            0,
            1,
            1,
            [row],
            new TerminalRenderCursor(
                TerminalCursorVisualStyle.Block,
                true,
                false,
                false,
                0,
                1),
            clean));
    }

    [Fact]
    public void Cursor_and_underline_contracts_reject_ambiguous_state()
    {
        Assert.Throws<ArgumentException>(() => new TerminalRenderCursor(
            TerminalCursorVisualStyle.Block,
            true,
            false,
            false,
            ViewportRow: 0));
        Assert.Throws<ArgumentException>(() => new TerminalRenderCursor(
            TerminalCursorVisualStyle.Block,
            true,
            false,
            false,
            IsWideCharacterTail: true));
        Assert.Throws<ArgumentException>(() => new TerminalRenderCursor(
            TerminalCursorVisualStyle.Block,
            true,
            false,
            false,
            Color: new TerminalCellColor(TerminalColorMode.Indexed, 1)));
        Assert.Throws<ArgumentException>(() => new TerminalRenderCell(
            "x",
            TerminalRenderCellWidth.Narrow,
            RgbWhite,
            TerminalCellColor.Default,
            UnderlineColor: RgbOrange));
        Assert.Throws<ArgumentException>(() => new TerminalRenderCell(
            "x",
            TerminalRenderCellWidth.Narrow,
            RgbWhite,
            TerminalCellColor.Default,
            Underline: TerminalUnderlineKind.Single,
            UnderlineColor: TerminalCellColor.Default));
    }

    [Fact]
    public void Kitty_frame_keys_content_by_image_generation_and_copies_pixels()
    {
        var key = new TerminalKittyImageKey(7, 42);
        var sourcePixels = new byte[] { 1, 2, 3, 4 };
        var image = new TerminalKittyImageContent(
            key,
            ImageNumber: 9,
            PixelWidth: 1,
            PixelHeight: 1,
            TerminalKittyImagePixelFormat.Rgba,
            sourcePixels);
        var placement = new TerminalKittyPlacement(
            key,
            PlacementId: 3,
            IsVirtual: false,
            ZIndex: (int.MinValue / 2) - 1,
            PixelOffsetX: 2.25,
            PixelOffsetY: 4.5,
            new TerminalKittySourceRectangle(0, 0, 1, 1),
            new TerminalKittyPlacementGeometry(-1, -2, 2, 3, 20, 30));
        var sourceImages = new List<TerminalKittyImageContent> { image };
        var sourcePlacements = new List<TerminalKittyPlacement> { placement };
        var graphics = new TerminalKittyGraphicsFrame(44, sourcePlacements, sourceImages);

        sourcePixels[0] = 255;
        sourceImages.Clear();
        sourcePlacements.Clear();

        Assert.Single(graphics.Placements);
        Assert.Single(graphics.Images);
        Assert.Equal(TerminalKittyPlacementLayer.BelowBackground, graphics.Placements[0].Layer);
        Assert.Equal(2.25, graphics.Placements[0].PixelOffsetX);
        Assert.Equal(4.5, graphics.Placements[0].PixelOffsetY);
        Assert.Equal(1, graphics.Images[key].Pixels.Span[0]);
        Assert.Equal(-2, graphics.Placements[0].Geometry?.ViewportRow);
    }

    [Fact]
    public void Kitty_frame_rejects_missing_content_and_multiple_generations()
    {
        var firstKey = new TerminalKittyImageKey(7, 1);
        var replacementKey = new TerminalKittyImageKey(7, 2);
        var first = Image(firstKey);
        var replacement = Image(replacementKey);
        var placement = new TerminalKittyPlacement(
            firstKey,
            0,
            false,
            0,
            0,
            0,
            new TerminalKittySourceRectangle(0, 0, 1, 1),
            null);

        Assert.Throws<ArgumentException>(() => new TerminalKittyGraphicsFrame(
            2,
            [placement],
            [replacement]));
        Assert.Throws<ArgumentException>(() => new TerminalKittyGraphicsFrame(
            2,
            Images: [first, replacement]));
        Assert.Throws<ArgumentException>(() => new TerminalKittyImageContent(
            default,
            0,
            1,
            1,
            TerminalKittyImagePixelFormat.Rgba,
            new byte[4]));
        Assert.Throws<ArgumentException>(() => new TerminalKittyImageContent(
            firstKey,
            0,
            2,
            1,
            TerminalKittyImagePixelFormat.Rgba,
            new byte[4]));
    }

    private static TerminalRenderCursor Cursor() => new(
        TerminalCursorVisualStyle.Block,
        IsVisible: false,
        IsBlinking: false,
        IsPasswordInput: false);

    private static TerminalRenderRow Row(int index, int columns) => new(
        index,
        [.. Enumerable.Range(0, columns)
            .Select(_ => new TerminalRenderCell(
                string.Empty,
                TerminalRenderCellWidth.Narrow,
                TerminalCellColor.Default,
                TerminalCellColor.Default))]);

    private static TerminalKittyImageContent Image(TerminalKittyImageKey key) => new(
        key,
        0,
        1,
        1,
        TerminalKittyImagePixelFormat.Rgba,
        new byte[4]);
}
