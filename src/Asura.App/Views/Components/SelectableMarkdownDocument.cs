using System.Collections.Immutable;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Avalonia.VisualTree;

namespace Asura.App.Views.Components;

/// <summary>
/// A native Markdown prose surface with one selection range across every
/// paragraph, heading, quote, table row, and list item. Markdown's layout and
/// text selection are properties of the same document instead of competing
/// control trees, so a wrapped list can keep its hanging indent without
/// splitting copy selection at the item boundary.
/// </summary>
internal sealed class SelectableMarkdownDocument : Control
{
    private const double BodyFontSize = 13;
    private const double BodyLineHeight = 20;
    private const double BlockSpacing = 8;
    private const double ListLevelIndent = 18;
    private const double ListMarkerWidth = 20;
    private static readonly double[] HeadingSizes = [23, 19, 16, 14, 13, 13];
    private static WeakReference<SelectableMarkdownDocument>? s_selectionOwner;

    private readonly ImmutableArray<SourceBlock> _sourceBlocks;
    private readonly string _plainText;
    private ImmutableArray<LayoutBlock> _layoutBlocks = [];
    private double _layoutWidth = double.NaN;
    private double _documentHeight;
    private int _selectionAnchor;
    private int _selectionEnd;
    private bool _selecting;

    internal SelectableMarkdownDocument(ImmutableArray<MarkdownBlock> blocks)
    {
        if (blocks.Any(block => block.Kind == MarkdownBlockKind.Table))
        {
            throw new ArgumentException(
                "Tables must be rendered as embedded controls.",
                nameof(blocks));
        }

        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        (_sourceBlocks, _plainText) = BuildSource(blocks);
        ActualThemeVariantChanged += (_, _) => InvalidateDocumentLayout();
    }

    internal string Text => _plainText;

    internal string SelectedText
    {
        get
        {
            var start = Math.Min(_selectionAnchor, _selectionEnd);
            var length = Math.Abs(_selectionEnd - _selectionAnchor);
            return length == 0 ? string.Empty : _plainText.Substring(start, length);
        }
    }

    internal ImmutableArray<MarkdownListLayout> ListLayouts =>
        [.. _layoutBlocks
            .Where(block => block.Kind == MarkdownBlockKind.ListItem)
            .Select(block => new MarkdownListLayout(
                block.Origin.X,
                block.ContentOrigin.X,
                block.Layout.TextLines.Count,
                [.. block.Layout.TextLines.Skip(1).Select(line => line.Start)]))];

    internal int MathFormulaCount =>
        _sourceBlocks.Sum(block =>
            block.Runs.Count(run => run.Style.HasFlag(MarkdownRunStyle.Math)));

    internal void SelectAllText()
    {
        ActivateSelection();
        _selectionAnchor = 0;
        _selectionEnd = _plainText.Length;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(1, availableSize.Width)
            : 820;
        EnsureDocumentLayout(width);
        return new Size(width, _documentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureDocumentLayout(Math.Max(1, finalSize.Width));
        return new Size(finalSize.Width, _documentHeight);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var block in _layoutBlocks)
        {
            block.Dispose();
        }

        _layoutBlocks = [];
        _layoutWidth = double.NaN;
        if (s_selectionOwner?.TryGetTarget(out var owner) == true
            && ReferenceEquals(owner, this))
        {
            s_selectionOwner = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EnsureDocumentLayout(Math.Max(1, Bounds.Width));
        // A custom-drawn Control has no Background property. Register its
        // whole document rectangle with Avalonia's hit tester so whitespace
        // between Markdown blocks remains part of a continuous drag surface.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var selection = SelectionBrush();
        foreach (var block in _layoutBlocks)
        {
            DrawSelection(context, block, selection);
            if (block.Kind == MarkdownBlockKind.ThematicBreak)
            {
                context.DrawLine(
                    new Pen(BorderBrush(), 1),
                    new Point(block.Origin.X, block.Origin.Y + (block.Height / 2)),
                    new Point(Math.Max(block.Origin.X, Bounds.Width), block.Origin.Y + (block.Height / 2)));
                continue;
            }

            block.MarkerLayout?.Draw(context, block.Origin);
            block.Layout.Draw(context, block.ContentOrigin);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _ = Focus();
        ActivateSelection();
        _selectionAnchor = HitTest(e.GetPosition(this));
        _selectionEnd = _selectionAnchor;
        _selecting = true;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _selectionEnd = HitTest(e.GetPosition(this));
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting)
        {
            return;
        }

        _selectionEnd = HitTest(e.GetPosition(this));
        _selecting = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta)
            || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (command && e.Key == Key.A)
        {
            SelectAllText();
            e.Handled = true;
            return;
        }

        if (command && e.Key == Key.C && SelectedText.Length > 0)
        {
            _ = CopySelectionAsync();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private async Task CopySelectionAsync()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
                && SelectedText is { Length: > 0 } selected)
            {
                await clipboard.SetTextAsync(selected);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Clipboard ownership can disappear during shutdown. Selection is
            // still intact, so a later copy can be attempted normally.
        }
    }

    private void EnsureDocumentLayout(double width)
    {
        if (Math.Abs(_layoutWidth - width) <= 0.5)
        {
            return;
        }

        foreach (var block in _layoutBlocks)
        {
            block.Dispose();
        }

        _layoutWidth = width;
        var blocks = ImmutableArray.CreateBuilder<LayoutBlock>(_sourceBlocks.Length);
        var y = 0d;
        for (var index = 0; index < _sourceBlocks.Length; index++)
        {
            var source = _sourceBlocks[index];
            if (index > 0)
            {
                y += BlockSpacing;
            }

            var top = TopMargin(source);
            var bottom = BottomMargin(source);
            y += top;
            var x = ContentOffset(source);
            var markerWidth = source.Kind == MarkdownBlockKind.ListItem
                ? ListMarkerWidth
                : 0;
            var contentX = x + markerWidth;
            var contentWidth = Math.Max(1, width - contentX);
            var fontSize = FontSize(source);
            var lineHeight = source.Kind == MarkdownBlockKind.Heading
                ? Math.Round(fontSize * 1.3)
                : BodyLineHeight;
            var layout = CreateTextLayout(source, contentWidth, fontSize, lineHeight);
            TextLayout? marker = null;
            if (source.Kind == MarkdownBlockKind.ListItem)
            {
                marker = new TextLayout(
                    source.Prefix.TrimEnd(),
                    Typeface(FontWeight.Normal),
                    BodyFontSize,
                    MutedBrush(),
                    textWrapping: TextWrapping.NoWrap,
                    maxWidth: ListMarkerWidth,
                    lineHeight: BodyLineHeight);
            }

            var height = source.Kind == MarkdownBlockKind.ThematicBreak
                ? 13
                : Math.Max(layout.Height, marker?.Height ?? 0);
            blocks.Add(new LayoutBlock(
                source.Kind,
                source.GlobalStart,
                source.Prefix.Length,
                source.Text.Length,
                new Point(x, y),
                new Point(contentX, y),
                height,
                layout,
                marker));
            y += height + bottom;
        }

        _layoutBlocks = blocks.MoveToImmutable();
        _documentHeight = Math.Max(0, y);
    }

    private TextLayout CreateTextLayout(
        SourceBlock source,
        double maxWidth,
        double fontSize,
        double lineHeight)
    {
        var weight = source.Kind == MarkdownBlockKind.Heading
            ? FontWeight.SemiBold
            : FontWeight.Normal;
        var defaultProperties = new GenericTextRunProperties(
            Typeface(weight),
            fontSize,
            foregroundBrush: source.Kind == MarkdownBlockKind.Quote
                ? MutedBrush()
                : ForegroundBrush());
        var textSource = new MarkdownTextSource(
            source.Text,
            source.Runs,
            run => RunProperties(run, source, fontSize, weight),
            source.Kind == MarkdownBlockKind.Math);
        return new TextLayout(
            textSource,
            new GenericTextParagraphProperties(
                defaultProperties,
                textWrapping: TextWrapping.Wrap,
                lineHeight: lineHeight),
            maxWidth: maxWidth,
            maxHeight: double.PositiveInfinity);
    }

    private TextRunProperties RunProperties(
        SourceRun run,
        SourceBlock source,
        double fontSize,
        FontWeight inheritedWeight)
    {
        var style = run.Style;
        var family = style.HasFlag(MarkdownRunStyle.Code)
            ? FontFamily("ShellDataFontFamily")
            : FontFamily("ShellUiFontFamily");
        var weight = style.HasFlag(MarkdownRunStyle.Bold)
            ? FontWeight.SemiBold
            : inheritedWeight;
        var fontStyle = style.HasFlag(MarkdownRunStyle.Italic)
            ? FontStyle.Italic
            : FontStyle.Normal;
        var foreground = style.HasFlag(MarkdownRunStyle.Code) || run.IsLink
            ? AccentBrush()
            : source.Kind == MarkdownBlockKind.Quote || run.IsMuted
                ? MutedBrush()
                : ForegroundBrush();
        TextDecorationCollection? decorations = null;
        if (run.IsLink)
        {
            decorations = TextDecorations.Underline;
        }
        else if (style.HasFlag(MarkdownRunStyle.Strikethrough))
        {
            decorations = TextDecorations.Strikethrough;
        }

        return new GenericTextRunProperties(
            new Typeface(family, fontStyle, weight),
            fontSize,
            decorations,
            foreground);
    }

    private void DrawSelection(DrawingContext context, LayoutBlock block, IBrush brush)
    {
        var selectionStart = Math.Min(_selectionAnchor, _selectionEnd);
        var selectionEnd = Math.Max(_selectionAnchor, _selectionEnd);
        if (selectionStart == selectionEnd)
        {
            return;
        }

        var prefixStart = block.GlobalStart;
        var prefixEnd = prefixStart + block.PrefixLength;
        if (block.MarkerLayout is not null
            && selectionStart < prefixEnd
            && selectionEnd > prefixStart)
        {
            context.DrawRectangle(
                brush,
                null,
                new Rect(block.Origin, new Size(ListMarkerWidth, block.MarkerLayout.Height)));
        }

        var contentStart = prefixEnd;
        var contentEnd = contentStart + block.ContentLength;
        var start = Math.Max(selectionStart, contentStart);
        var end = Math.Min(selectionEnd, contentEnd);
        if (start >= end)
        {
            return;
        }

        foreach (var rect in block.Layout.HitTestTextRange(
                     start - contentStart,
                     end - start))
        {
            context.DrawRectangle(
                brush,
                null,
                new Rect(
                    rect.X + block.ContentOrigin.X,
                    rect.Y + block.ContentOrigin.Y,
                    rect.Width,
                    rect.Height));
        }
    }

    private int HitTest(Point point)
    {
        if (_layoutBlocks.IsEmpty || point.Y <= _layoutBlocks[0].Origin.Y)
        {
            return 0;
        }

        foreach (var block in _layoutBlocks)
        {
            if (point.Y > block.Origin.Y + block.Height)
            {
                continue;
            }

            if (point.X <= block.ContentOrigin.X)
            {
                return block.GlobalStart;
            }

            var hit = block.Layout.HitTestPoint(
                new Point(
                    point.X - block.ContentOrigin.X,
                    point.Y - block.ContentOrigin.Y));
            return Math.Clamp(
                block.GlobalStart + block.PrefixLength + hit.TextPosition,
                block.GlobalStart,
                block.GlobalStart + block.PrefixLength + block.ContentLength);
        }

        return _plainText.Length;
    }

    private void InvalidateDocumentLayout()
    {
        _layoutWidth = double.NaN;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ClearSelection()
    {
        _selecting = false;
        _selectionAnchor = 0;
        _selectionEnd = 0;
        InvalidateVisual();
    }

    private void ActivateSelection()
    {
        if (s_selectionOwner?.TryGetTarget(out var owner) == true
            && !ReferenceEquals(owner, this))
        {
            owner.ClearSelection();
        }

        s_selectionOwner = new WeakReference<SelectableMarkdownDocument>(this);
    }

    private static (ImmutableArray<SourceBlock> Blocks, string Text) BuildSource(
        ImmutableArray<MarkdownBlock> blocks)
    {
        var sources = ImmutableArray.CreateBuilder<SourceBlock>(blocks.Length);
        var document = new StringBuilder();
        MarkdownBlock? previous = null;
        foreach (var block in blocks)
        {
            if (previous is not null)
            {
                document.AppendLine();
                if (previous.Kind != MarkdownBlockKind.ListItem
                    || block.Kind != MarkdownBlockKind.ListItem)
                {
                    document.AppendLine();
                }
            }

            var prefix = block.Kind == MarkdownBlockKind.ListItem
                ? $"{block.Bullet} "
                : string.Empty;
            var runs = SourceRuns(block);
            var text = string.Concat(runs.Select(run => run.Text));
            var start = document.Length;
            document.Append(prefix);
            document.Append(text);
            sources.Add(new SourceBlock(
                block.Kind,
                block.Level,
                prefix,
                text,
                runs,
                start));
            previous = block;
        }

        return (sources.MoveToImmutable(), document.ToString());
    }

    private static ImmutableArray<SourceRun> SourceRuns(MarkdownBlock block)
    {
        if (block.Kind == MarkdownBlockKind.ThematicBreak)
        {
            return [new SourceRun("────────────────", MarkdownRunStyle.None, false, true)];
        }

        return [.. block.Runs
            .Select(run => new SourceRun(
                run.Text,
                run.Style,
                run.LinkTarget is not null,
                false))];
    }

    private static double ContentOffset(SourceBlock block) => block.Kind switch
    {
        MarkdownBlockKind.ListItem => Math.Max(0, block.Level - 1) * ListLevelIndent,
        MarkdownBlockKind.Quote => 14,
        _ => 0,
    };

    private static double FontSize(SourceBlock block) => block.Kind == MarkdownBlockKind.Heading
        ? HeadingSizes[Math.Clamp(block.Level, 1, HeadingSizes.Length) - 1]
        : BodyFontSize;

    private static double TopMargin(SourceBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => block.Level == 1 ? 2 : 14,
        MarkdownBlockKind.Math => 4,
        MarkdownBlockKind.Quote => 4,
        MarkdownBlockKind.ThematicBreak => 6,
        _ => 0,
    };

    private static double BottomMargin(SourceBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => 2,
        MarkdownBlockKind.Math => 4,
        MarkdownBlockKind.Quote => 4,
        MarkdownBlockKind.ThematicBreak => 6,
        _ => 0,
    };

    private Typeface Typeface(FontWeight weight) =>
        new(FontFamily("ShellUiFontFamily"), FontStyle.Normal, weight);

    private FontFamily FontFamily(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value)
            && value is FontFamily family
                ? family
                : global::Avalonia.Media.FontFamily.Default;

    private IBrush ForegroundBrush() => ResourceBrush("ShellTextBrush", Brushes.White);

    private IBrush MutedBrush() => ResourceBrush("ShellMutedBrush", Brushes.Gray);

    private IBrush AccentBrush() => ResourceBrush("ShellAccentBrush", Brushes.Orange);

    private IBrush BorderBrush() => ResourceBrush("ShellBorderBrush", Brushes.Gray);

    private IBrush SelectionBrush() =>
        ResourceBrush(
            "ShellAccentSoftBrush",
            new SolidColorBrush(Color.FromArgb(120, 202, 108, 24)));

    private IBrush ResourceBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value)
            && value is IBrush brush
                ? brush
                : fallback;

    private sealed record SourceRun(
        string Text,
        MarkdownRunStyle Style,
        bool IsLink,
        bool IsMuted);

    private sealed record SourceBlock(
        MarkdownBlockKind Kind,
        int Level,
        string Prefix,
        string Text,
        ImmutableArray<SourceRun> Runs,
        int GlobalStart);

    private sealed class MarkdownTextSource : ITextSource
    {
        private readonly string _text;
        private readonly ImmutableArray<TextSegment> _segments;

        internal MarkdownTextSource(
            string text,
            ImmutableArray<SourceRun> runs,
            Func<SourceRun, TextRunProperties> properties,
            bool displayMath)
        {
            _text = text;
            var segments = ImmutableArray.CreateBuilder<TextSegment>(runs.Length);
            var offset = 0;
            foreach (var run in runs)
            {
                if (run.Text.Length == 0)
                {
                    continue;
                }

                var runProperties = properties(run);
                TextRun textRun = run.Style.HasFlag(MarkdownRunStyle.Math)
                    && MarkdownMathDrawableTextRun.TryCreate(
                        run.Text,
                        runProperties,
                        displayMath,
                        out var math)
                            ? math
                            : new TextCharacters(
                                text.AsMemory(offset, run.Text.Length),
                                runProperties);
                segments.Add(new TextSegment(offset, run.Text.Length, textRun));
                offset += run.Text.Length;
            }

            _segments = segments.ToImmutable();
        }

        public TextRun GetTextRun(int textSourceIndex)
        {
            if (textSourceIndex >= _text.Length)
            {
                return new TextEndOfParagraph();
            }

            foreach (var segment in _segments)
            {
                if (textSourceIndex < segment.Start
                    || textSourceIndex >= segment.Start + segment.Length)
                {
                    continue;
                }

                if (textSourceIndex == segment.Start)
                {
                    return segment.Run;
                }

                if (segment.Run is TextCharacters characters)
                {
                    var consumed = textSourceIndex - segment.Start;
                    return new TextCharacters(
                        characters.Text[consumed..],
                        characters.Properties);
                }

                // Math is atomic. The formatter never splits a drawable run,
                // but returning the paragraph terminator is safer than drawing
                // the same formula twice if a future formatter probes inside it.
                return new TextEndOfParagraph();
            }

            return new TextEndOfParagraph();
        }

        private sealed record TextSegment(int Start, int Length, TextRun Run);
    }

    private sealed record LayoutBlock(
        MarkdownBlockKind Kind,
        int GlobalStart,
        int PrefixLength,
        int ContentLength,
        Point Origin,
        Point ContentOrigin,
        double Height,
        TextLayout Layout,
        TextLayout? MarkerLayout) : IDisposable
    {
        public void Dispose()
        {
            Layout.Dispose();
            MarkerLayout?.Dispose();
        }
    }
}

internal readonly record struct MarkdownListLayout(
    double MarkerX,
    double ContentX,
    int VisualLineCount,
    ImmutableArray<double> WrappedLineStarts);
