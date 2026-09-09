using Asura.Core;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Inpc.Controls;

namespace Asura.App.ViewModels;

/// <summary>
/// Projects a Dock tree onto the durable layout contract. Dock owns the exact
/// recursive proportions; screens, previews, and the validator speak in grid
/// slots. The projection snaps every split boundary onto one rational grid so
/// a saved layout keeps a valid, non-overlapping slot set whose spans still
/// reflect the designed proportions — while <c>DockLayoutJson</c> remains the
/// authority the runtime actually restores.
/// </summary>
internal static class DockLayoutProjection
{
    /// <summary>
    /// The rational grid the boundaries snap onto. Fine enough that a 70/30
    /// split stays visibly asymmetric in previews, coarse enough that the grid
    /// stays a grid rather than a pixel map.
    /// </summary>
    private const int Resolution = 24;

    private const double CutTolerance = 1e-4;

    internal sealed record LeafRegion(
        IDocument Document,
        double X,
        double Y,
        double Width,
        double Height);

    /// <summary>
    /// Every document leaf with its normalized bounds, in Dock traversal order —
    /// which is also the reading order the designer publishes as slot order.
    /// </summary>
    public static IReadOnlyList<LeafRegion> CollectRegions(IRootDock root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var regions = new List<LeafRegion>();
        foreach (var dockable in root.VisibleDockables ?? [])
        {
            AddRegions(dockable, 0, 0, 1, 1, regions);
        }

        return regions;
    }

    public static (LayoutGrid Grid, IReadOnlyList<LayoutSlotDefinition> Slots) ProjectSlots(
        IRootDock root,
        Func<string, LayoutMinimumSize> minimumSize)
    {
        ArgumentNullException.ThrowIfNull(minimumSize);
        var regions = CollectRegions(root);
        if (regions.Count == 0)
        {
            return (new LayoutGrid(1, 1), []);
        }

        var xCuts = CollectCuts(regions, region => region.X, region => region.X + region.Width);
        var yCuts = CollectCuts(regions, region => region.Y, region => region.Y + region.Height);
        var xPositions = SnapCuts(xCuts);
        var yPositions = SnapCuts(yCuts);

        var slots = regions
            .Select(region =>
            {
                var left = xPositions[IndexOfCut(xCuts, region.X)];
                var right = xPositions[IndexOfCut(xCuts, region.X + region.Width)];
                var top = yPositions[IndexOfCut(yCuts, region.Y)];
                var bottom = yPositions[IndexOfCut(yCuts, region.Y + region.Height)];
                return new LayoutSlotDefinition(
                    new LayoutSlotId(region.Document.Id),
                    new LayoutGridBounds(left, top, right - left, bottom - top),
                    minimumSize(region.Document.Id));
            })
            .ToArray();

        return (new LayoutGrid(xPositions[^1], yPositions[^1]), slots);
    }

    private static void AddRegions(
        IDockable dockable,
        double x,
        double y,
        double width,
        double height,
        ICollection<LeafRegion> regions)
    {
        if (dockable is IDocument document)
        {
            regions.Add(new LeafRegion(document, x, y, width, height));
            return;
        }

        if (dockable is not IDock { VisibleDockables: { } children })
        {
            return;
        }

        var panels = children
            .Where(child => child is not ProportionalDockSplitter)
            .ToArray();
        if (dockable is not ProportionalDock proportional || panels.Length < 2)
        {
            foreach (var child in panels)
            {
                AddRegions(child, x, y, width, height, regions);
            }

            return;
        }

        var weights = panels
            .Select(panel => double.IsFinite(panel.Proportion) && panel.Proportion > 0
                ? panel.Proportion
                : 1d)
            .ToArray();
        var total = weights.Sum();
        var offset = 0d;
        for (var index = 0; index < panels.Length; index++)
        {
            var share = weights[index] / total;
            if (proportional.Orientation == Orientation.Horizontal)
            {
                AddRegions(
                    panels[index],
                    x + (width * offset),
                    y,
                    width * share,
                    height,
                    regions);
            }
            else
            {
                AddRegions(
                    panels[index],
                    x,
                    y + (height * offset),
                    width,
                    height * share,
                    regions);
            }

            offset += share;
        }
    }

    private static List<double> CollectCuts(
        IReadOnlyList<LeafRegion> regions,
        Func<LeafRegion, double> start,
        Func<LeafRegion, double> end)
    {
        var cuts = new List<double> { 0, 1 };
        foreach (var region in regions)
        {
            cuts.Add(start(region));
            cuts.Add(end(region));
        }

        cuts.Sort();
        var unique = new List<double> { 0 };
        foreach (var cut in cuts)
        {
            if (cut - unique[^1] > CutTolerance && cut < 1 - CutTolerance)
            {
                unique.Add(cut);
            }
        }

        unique.Add(1);
        return unique;
    }

    /// <summary>
    /// Maps sorted normalized cuts onto strictly increasing integer positions
    /// and reduces the axis by its common divisor, so an even two-way split
    /// projects as a 2-track axis rather than as 24 identical tracks.
    /// </summary>
    private static int[] SnapCuts(List<double> cuts)
    {
        var segments = cuts.Count - 1;
        var positions = new int[cuts.Count];
        if (segments >= Resolution)
        {
            // Degenerate density: give every boundary its own unit track.
            for (var index = 0; index < cuts.Count; index++)
            {
                positions[index] = index;
            }

            return positions;
        }

        for (var index = 1; index < cuts.Count; index++)
        {
            var remaining = cuts.Count - 1 - index;
            positions[index] = Math.Clamp(
                (int)Math.Round(cuts[index] * Resolution),
                positions[index - 1] + 1,
                Resolution - remaining);
        }

        var divisor = 0;
        for (var index = 1; index < positions.Length; index++)
        {
            divisor = GreatestCommonDivisor(divisor, positions[index] - positions[index - 1]);
        }

        if (divisor > 1)
        {
            for (var index = 0; index < positions.Length; index++)
            {
                positions[index] /= divisor;
            }
        }

        return positions;
    }

    private static int IndexOfCut(List<double> cuts, double value)
    {
        var nearest = 0;
        var nearestDistance = double.MaxValue;
        for (var index = 0; index < cuts.Count; index++)
        {
            var distance = Math.Abs(cuts[index] - value);
            if (distance < nearestDistance)
            {
                nearest = index;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private static int GreatestCommonDivisor(int first, int second)
    {
        while (second != 0)
        {
            (first, second) = (second, first % second);
        }

        return first;
    }
}
