using Asura.Application;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Asura.App.Views.Components;

/// <summary>
/// Paints language-worker diagnostics without changing AvaloniaEdit's TextMate
/// visual-line transformers. Offsets are UTF-16 document offsets on both sides
/// of this boundary, so no character-index translation occurs in the editor.
/// </summary>
internal sealed class SqlDiagnosticBackgroundRenderer : IBackgroundRenderer
{
    private IReadOnlyList<SqlDiagnostic> _diagnostics = [];
    private IBrush _errorBrush = new SolidColorBrush(Color.Parse("#FF5C33"));
    private IBrush _warningBrush = new SolidColorBrush(Color.Parse("#E1A45F"));
    private IBrush _informationBrush = new SolidColorBrush(Color.Parse("#5C9DFF"));

    public KnownLayer Layer => KnownLayer.Text;

    internal IReadOnlyList<SqlDiagnostic> Diagnostics => _diagnostics;

    internal void Update(
        IReadOnlyList<SqlDiagnostic> diagnostics,
        IBrush? errorBrush = null,
        IBrush? warningBrush = null,
        IBrush? informationBrush = null)
    {
        _diagnostics = diagnostics;
        _errorBrush = errorBrush ?? _errorBrush;
        _warningBrush = warningBrush ?? _warningBrush;
        _informationBrush = informationBrush ?? _informationBrush;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0)
        {
            return;
        }

        foreach (var diagnostic in _diagnostics)
        {
            var segment = CreateVisibleSegment(diagnostic, textView.Document.TextLength);
            if (segment is null)
            {
                continue;
            }

            var pen = new Pen(BrushFor(diagnostic.Severity), 1.25);
            foreach (var rectangle in BackgroundGeometryBuilder.GetRectsForSegment(
                         textView,
                         segment))
            {
                DrawWave(drawingContext, pen, rectangle);
            }
        }
    }

    internal static TextSegment? CreateVisibleSegment(
        SqlDiagnostic diagnostic,
        int textLength)
    {
        if (textLength == 0)
        {
            return null;
        }

        var start = Math.Clamp(diagnostic.Start, 0, textLength);
        var requestedEnd = Math.Max((long)diagnostic.Start + diagnostic.Length, start);
        var end = (int)Math.Clamp(requestedEnd, start, textLength);
        if (start == textLength)
        {
            start--;
        }

        return new TextSegment
        {
            StartOffset = start,
            Length = Math.Max(1, end - start),
        };
    }

    private IBrush BrushFor(SqlDiagnosticSeverity severity) => severity switch
    {
        SqlDiagnosticSeverity.Error => _errorBrush,
        SqlDiagnosticSeverity.Warning => _warningBrush,
        _ => _informationBrush,
    };

    private static void DrawWave(DrawingContext context, Pen pen, Rect rectangle)
    {
        const double step = 2;
        var baseline = rectangle.Bottom - 1;
        var x = rectangle.Left;
        var rises = true;
        while (x < rectangle.Right)
        {
            var next = Math.Min(x + step, rectangle.Right);
            context.DrawLine(
                pen,
                new Point(x, baseline + (rises ? 0 : -1.5)),
                new Point(next, baseline + (rises ? -1.5 : 0)));
            rises = !rises;
            x = next;
        }
    }
}
