using Asura.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Asura.App.Views.Components;

/// <summary>
/// Draws one history row's slice of the commit graph. Edges meet the row
/// borders at lane centers, so stacked rows form continuous rails; the lane
/// palette comes from theme resources, never from literals here.
/// </summary>
public sealed class GitCommitGraphRowView : Control
{
    private const double LaneWidth = 10;
    private const double DotRadius = 3;
    private const double EdgeThickness = 1.5;

    private static readonly string[] LaneBrushKeys =
    [
        "ShellAccentBrush",
        "ShellSlotBadgeBlueBrush",
        "ShellSlotBadgeGreenBrush",
        "ShellSlotBadgePinkBrush",
        "ShellSlotBadgeOrangeBrush",
    ];

    public static readonly StyledProperty<GitGraphRow?> RowProperty =
        AvaloniaProperty.Register<GitCommitGraphRowView, GitGraphRow?>(nameof(Row));

    static GitCommitGraphRowView()
    {
        AffectsRender<GitCommitGraphRowView>(RowProperty);
        AffectsMeasure<GitCommitGraphRowView>(RowProperty);
    }

    public GitGraphRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _ = availableSize;
        var lanes = Row?.LaneCount ?? 0;
        return new Size(Math.Max(lanes, 1) * LaneWidth, 0);
    }

    public override void Render(DrawingContext context)
    {
        if (Row is not { } row)
        {
            return;
        }

        var height = Bounds.Height;
        var middle = height / 2;

        foreach (var edge in row.TopEdges)
        {
            context.DrawLine(
                Pen(edge.ColorIndex),
                new Point(LaneCenter(edge.FromLane), 0),
                new Point(LaneCenter(edge.ToLane), middle));
        }

        foreach (var edge in row.BottomEdges)
        {
            context.DrawLine(
                Pen(edge.ColorIndex),
                new Point(LaneCenter(edge.FromLane), middle),
                new Point(LaneCenter(edge.ToLane), height));
        }

        var dotCenter = new Point(LaneCenter(row.DotLane), middle);
        context.DrawEllipse(
            LaneBrush(row.DotColorIndex),
            row.IsMerge ? Pen(row.DotColorIndex) : null,
            dotCenter,
            row.IsMerge ? DotRadius - 1 : DotRadius,
            row.IsMerge ? DotRadius - 1 : DotRadius);
    }

    private static double LaneCenter(int lane) => (lane * LaneWidth) + (LaneWidth / 2);

    private IBrush LaneBrush(int colorIndex)
    {
        var key = LaneBrushKeys[colorIndex % LaneBrushKeys.Length];
        return this.TryFindResource(key, ActualThemeVariant, out var value)
            && value is IBrush brush
            ? brush
            : Brushes.Gray;
    }

    private Pen Pen(int colorIndex) => new(LaneBrush(colorIndex), EdgeThickness);
}
