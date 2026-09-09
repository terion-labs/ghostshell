using Asura.App.Controls;
using Avalonia.Media;

namespace Asura.App.Tests;

/// <summary>
/// The rail's colours are computed, not configured, so they are worth pinning:
/// a resting tile that came out identical to its neighbour is exactly the
/// "everything looks the same" the rail was rebuilt to fix.
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class WorkspaceRailTileTests
{
    [Fact]
    public void A_tile_wears_its_workspace_colour_and_a_muted_version_of_it()
    {
        var tile = new WorkspaceRailTile { Accent = "#B8793A" };

        Assert.Equal(Color.Parse("#B8793A"), Fill(tile.AccentBrush));
        var resting = Fill(tile.RestingBrush);
        Assert.NotEqual(Color.Parse("#B8793A"), resting);
        // Muted, not merely darkened: the gap between the channels closes.
        Assert.True(
            Spread(resting) < Spread(Color.Parse("#B8793A")),
            $"The resting colour {resting} is no less saturated than the accent.");
    }

    /// <summary>
    /// Two workspaces of different colours must still be told apart at rest,
    /// or the rail is a column of identical grey squares again.
    /// </summary>
    [Fact]
    public void Two_workspaces_stay_distinguishable_when_neither_is_in_front()
    {
        var red = new WorkspaceRailTile { Accent = "#C4322B" };
        var blue = new WorkspaceRailTile { Accent = "#2B62C4" };

        Assert.NotEqual(Fill(red.RestingBrush), Fill(blue.RestingBrush));
    }

    [Fact]
    public void A_tile_marks_itself_in_whichever_of_ink_or_paper_reads_on_its_colour()
    {
        var dark = new WorkspaceRailTile { Accent = "#1F3A5F" };
        var light = new WorkspaceRailTile { Accent = "#F2E7C9" };

        Assert.Equal(Colors.White, Fill(dark.MarkBrush));
        Assert.NotEqual(Colors.White, Fill(light.MarkBrush));
    }

    [Fact]
    public void A_colour_the_tile_cannot_read_leaves_it_unpainted_rather_than_guessing()
    {
        var tile = new WorkspaceRailTile { Accent = "not a colour" };

        Assert.Null(tile.AccentBrush);
        Assert.Null(tile.RestingBrush);
        Assert.Null(tile.MarkBrush);
    }

    private static Color Fill(IBrush? brush) =>
        Assert.IsType<SolidColorBrush>(brush).Color;

    private static int Spread(Color color) =>
        Math.Max(color.R, Math.Max(color.G, color.B))
        - Math.Min(color.R, Math.Min(color.G, color.B));
}
