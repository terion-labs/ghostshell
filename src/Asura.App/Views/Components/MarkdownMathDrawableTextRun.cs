using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using CSharpMath.Atom;
using CSharpMath.Avalonia;
using MathTextAlignment = CSharpMath.Rendering.FrontEnd.TextAlignment;

namespace Asura.App.Views.Components;

/// <summary>
/// An atomic formula inside Avalonia's native text formatter. Keeping math as
/// a text run means Markdown still has one wrapping, hit-testing, selection,
/// and copy surface instead of a tree of unrelated embedded controls.
/// </summary>
internal sealed class MarkdownMathDrawableTextRun : DrawableTextRun
{
    private readonly string _latex;
    private readonly MathPainter _painter;

    private MarkdownMathDrawableTextRun(
        string latex,
        TextRunProperties properties,
        bool displayStyle)
    {
        _latex = latex;
        Properties = properties;
        _painter = new MathPainter
        {
            DisplayErrorInline = false,
            FontSize = (float)properties.FontRenderingEmSize,
            LineStyle = displayStyle ? LineStyle.Display : LineStyle.Text,
            TextColor = properties.ForegroundBrush is ISolidColorBrush solid
                ? solid.Color
                : Colors.White,
            LaTeX = latex,
        };
        var measured = _painter.Measure();
        Size = new Size(
            Math.Max(1, measured.Width),
            Math.Max(properties.FontRenderingEmSize, measured.Height));
        Baseline = Math.Max(0, -measured.Y);
    }

    public override int Length => _latex.Length;

    public override ReadOnlyMemory<char> Text => _latex.AsMemory();

    public override TextRunProperties Properties { get; }

    public override Size Size { get; }

    public override double Baseline { get; }

    internal static bool TryCreate(
        string latex,
        TextRunProperties properties,
        bool displayStyle,
        out MarkdownMathDrawableTextRun run)
    {
        run = new MarkdownMathDrawableTextRun(latex, properties, displayStyle);
        return run._painter.ErrorMessage is null && run._painter.Display is not null;
    }

    public override void Draw(DrawingContext drawingContext, Point origin)
    {
        using var translated = drawingContext.PushTransform(
            Matrix.CreateTranslation(origin.X, origin.Y));
        _painter.Draw(
            new AvaloniaCanvas(drawingContext, Size),
            MathTextAlignment.TopLeft);
    }
}
