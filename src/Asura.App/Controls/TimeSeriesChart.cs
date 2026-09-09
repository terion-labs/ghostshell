using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Asura.App.Controls;

/// <summary>
/// Draws a small, dependency-free rolling metric chart. The caller owns sampling
/// and retention; this control only scales and renders the supplied window.
/// </summary>
public sealed class TimeSeriesChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double?>?> ValuesProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IReadOnlyList<double?>?>(
            nameof(Values));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TimeSeriesChart, double>(
            nameof(Maximum),
            defaultValue: double.NaN);

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(
            nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> AreaBrushProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(
            nameof(AreaBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TimeSeriesChart, IBrush?>(
            nameof(GridBrush));

    public IReadOnlyList<double?>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>
    /// The chart ceiling. Leave unset for an automatically padded ceiling.
    /// </summary>
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? AreaBrush
    {
        get => GetValue(AreaBrushProperty);
        set => SetValue(AreaBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        DrawGrid(context);
        if (Values is not { Count: > 0 } values
            || LineBrush is not { } lineBrush)
        {
            return;
        }

        var ceiling = ResolveCeiling(values);
        var segment = new List<Point>();
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is { } value && double.IsFinite(value) && value >= 0)
            {
                segment.Add(PointFor(index, value, values.Count, ceiling));
                continue;
            }

            DrawSegment(context, segment, lineBrush);
            segment.Clear();
        }

        DrawSegment(context, segment, lineBrush);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValuesProperty
            || change.Property == MaximumProperty
            || change.Property == LineBrushProperty
            || change.Property == AreaBrushProperty
            || change.Property == GridBrushProperty)
        {
            InvalidateVisual();
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        if (GridBrush is not { } gridBrush)
        {
            return;
        }

        var pen = new Pen(gridBrush, 1);
        for (var division = 1; division < 4; division++)
        {
            var y = Bounds.Height * division / 4;
            context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
        }
    }

    private double ResolveCeiling(IReadOnlyList<double?> values)
    {
        if (double.IsFinite(Maximum) && Maximum > 0)
        {
            return Maximum;
        }

        var peak = values
            .Where(value => value is >= 0 && double.IsFinite(value.Value))
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(1, peak * 1.1);
    }

    private Point PointFor(int index, double value, int count, double ceiling)
    {
        var x = count == 1 ? Bounds.Width : Bounds.Width * index / (count - 1);
        var y = Bounds.Height * (1 - Math.Clamp(value / ceiling, 0, 1));
        return new Point(x, y);
    }

    private void DrawSegment(
        DrawingContext context,
        IReadOnlyList<Point> points,
        IBrush lineBrush)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count == 1)
        {
            context.DrawEllipse(lineBrush, null, points[0], 2.5, 2.5);
            return;
        }

        if (AreaBrush is { } areaBrush)
        {
            var area = new StreamGeometry();
            using var areaContext = area.Open();
            areaContext.BeginFigure(
                new Point(points[0].X, Bounds.Height),
                isFilled: true);
            foreach (var point in points)
            {
                areaContext.LineTo(point);
            }

            areaContext.LineTo(new Point(points[^1].X, Bounds.Height));
            areaContext.EndFigure(isClosed: true);
            context.DrawGeometry(areaBrush, null, area);
        }

        var line = new StreamGeometry();
        using (var lineContext = line.Open())
        {
            lineContext.BeginFigure(points[0], isFilled: false);
            foreach (var point in points.Skip(1))
            {
                lineContext.LineTo(point);
            }

            lineContext.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(lineBrush, 1.5), line);
        context.DrawEllipse(lineBrush, null, points[^1], 2.5, 2.5);
    }
}
