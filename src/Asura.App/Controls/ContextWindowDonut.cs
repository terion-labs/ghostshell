using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Asura.App.Controls;

/// <summary>
/// Draws context-window usage as a compact ring. Exact token counts belong to
/// the owning tooltip and flyout; this control only carries the glanceable
/// proportion needed in the constrained composer footer.
/// </summary>
public sealed class ContextWindowDonut : Control
{
    public static readonly StyledProperty<double> PercentageProperty =
        AvaloniaProperty.Register<ContextWindowDonut, double>(nameof(Percentage));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ContextWindowDonut, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> IndicatorBrushProperty =
        AvaloniaProperty.Register<ContextWindowDonut, IBrush?>(nameof(IndicatorBrush));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<ContextWindowDonut, double>(
            nameof(StrokeThickness),
            2);

    static ContextWindowDonut()
    {
        AffectsRender<ContextWindowDonut>(
            PercentageProperty,
            TrackBrushProperty,
            IndicatorBrushProperty,
            StrokeThicknessProperty);
    }

    public double Percentage
    {
        get => GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? IndicatorBrush
    {
        get => GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var stroke = Math.Max(0, StrokeThickness);
        var diameter = Math.Min(Bounds.Width, Bounds.Height) - stroke;
        if (diameter <= 0 || stroke <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = diameter / 2;
        if (TrackBrush is { } track)
        {
            context.DrawEllipse(
                null,
                new Pen(track, stroke),
                center,
                radius,
                radius);
        }

        if (IndicatorBrush is not { } indicator)
        {
            return;
        }

        var percentage = Math.Clamp(Percentage, 0, 100);
        if (percentage <= 0)
        {
            return;
        }

        var pen = new Pen(indicator, stroke, lineCap: PenLineCap.Round);
        if (percentage >= 100)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var start = new Point(center.X, center.Y - radius);
        var angle = (percentage * 3.6 - 90) * Math.PI / 180;
        var end = new Point(
            center.X + radius * Math.Cos(angle),
            center.Y + radius * Math.Sin(angle));
        var arc = new StreamGeometry();
        using (var arcContext = arc.Open())
        {
            arcContext.BeginFigure(start, isFilled: false);
            arcContext.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: percentage > 50,
                SweepDirection.Clockwise);
            arcContext.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, arc);
    }
}
