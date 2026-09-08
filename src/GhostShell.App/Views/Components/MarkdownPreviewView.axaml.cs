using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Media;
using CSharpMath.Avalonia;
using GhostShell.Application;

namespace GhostShell.App.Views.Components;

/// <summary>
/// Renders Markdown as native controls rather than as a web page: the text is
/// real text, so it selects, copies, and scales with the shell's own type and
/// spacing instead of a browser's.
/// </summary>
public sealed partial class MarkdownPreviewView : UserControl
{
    private static readonly SemaphoreSlim ParseGate = new(1, 1);
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarkdownPreviewView, string?>(nameof(Text));

    public static readonly StyledProperty<bool> ContinuousSelectionProperty =
        AvaloniaProperty.Register<MarkdownPreviewView, bool>(nameof(ContinuousSelection));

    /// <summary>
    /// Heading sizes, largest first. Markdown allows six levels; the shell's
    /// type scale is what decides how big each one is here.
    /// </summary>
    private static readonly double[] HeadingSizes = [23, 19, 16, 14, 13, 13];

    /// <summary>Body size for prose, a step above the shell's dense UI text.</summary>
    private const double BodyFontSize = 13;

    /// <summary>
    /// Below this width a table column stops being readable. Tables with more
    /// columns keep this floor and scroll inside their own viewport.
    /// </summary>
    private const double MinimumTableColumnWidth = 120;

    public MarkdownPreviewView()
    {
        InitializeComponent();
        EffectiveViewportChanged += (_, e) =>
        {
            _effectiveViewport = e.EffectiveViewport;
            RenderWhenInViewport();
        };
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Renders the document as one selectable text surface. Chat messages use
    /// this because selection belongs to the message, not to Markdown's
    /// internal paragraph and list-item boundaries.
    /// </summary>
    public bool ContinuousSelection
    {
        get => GetValue(ContinuousSelectionProperty);
        set => SetValue(ContinuousSelectionProperty, value);
    }

    /// <summary>
    /// Whether the current text has reached a committed presentation. The
    /// design harness uses this instead of inferring completion from the
    /// transient rendering label, which can disappear before the resulting
    /// layout and enclosing scroll viewers have settled.
    /// </summary>
    internal bool IsPresentationReady => string.IsNullOrEmpty(Text) || _hasRendered;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Resources resolve against the tree, so a render before attachment
        // would silently draw every themed brush as nothing.
        _hasRendered = false;
        if (string.IsNullOrEmpty(Text))
        {
            Render();
        }
        else
        {
            RenderingState.IsVisible = true;
            PlainTextFallback.IsVisible = false;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _buildGeneration++;
        _building?.Cancel();
        _building?.Dispose();
        _building = null;
        Blocks.Children.Clear();
        RenderingState.IsVisible = false;
        PlainTextFallback.Text = null;
        PlainTextFallback.IsVisible = false;
        _hasRendered = false;
        _isInView = false;
        _effectiveViewport = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && !_isInView)
        {
            // A newly attached preview can receive its viewport before its
            // first arrange gives it nonzero bounds. Retry after layout so it
            // cannot remain a permanent empty placeholder.
            RenderWhenInViewport();
        }

        if ((change.Property == TextProperty || change.Property == ContinuousSelectionProperty)
            && VisualRoot is not null)
        {
            if (change.Property == ContinuousSelectionProperty)
            {
                _hasRendered = false;
            }

            if (_isInView || string.IsNullOrEmpty(Text))
            {
                Render();
            }
            else
            {
                RenderingState.IsVisible = true;
                PlainTextFallback.IsVisible = false;
            }
        }
    }

    /// <summary>The text the blocks on screen were built from.</summary>
    private string? _rendered;

    private bool _renderedContinuousSelection;

    private bool _hasRendered;

    private bool _isInView;

    private Rect? _effectiveViewport;

    private int _buildGeneration;

    private void RenderWhenInViewport()
    {
        if (_isInView || _effectiveViewport is not { } effectiveViewport)
        {
            return;
        }

        // EffectiveViewport is expressed in this control's coordinates and
        // can remain non-empty while the control sits outside the scroller.
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0
            || bounds.Height <= 0
            || !effectiveViewport.Intersects(bounds))
        {
            return;
        }

        _isInView = true;
        Render();
    }

    private void Render()
    {
        // Rebuilding costs a syntax-highlighting installation per fenced block,
        // so the same text is never laid out twice. Switching a preview to its
        // source and back hands us the identical string.
        var continuousSelection = ContinuousSelection;
        if ((_hasRendered || _building is not null)
            && string.Equals(_rendered, Text, StringComparison.Ordinal)
            && _renderedContinuousSelection == continuousSelection)
        {
            return;
        }

        _rendered = Text;
        _renderedContinuousSelection = continuousSelection;
        _hasRendered = false;
        var generation = ++_buildGeneration;
        _building?.Cancel();
        _building?.Dispose();
        if (string.IsNullOrEmpty(Text))
        {
            _building = null;
            Blocks.Children.Clear();
            RenderingState.IsVisible = false;
            PlainTextFallback.IsVisible = false;
            _hasRendered = true;
            return;
        }

        RenderingState.IsVisible = Blocks.Children.Count == 0;
        PlainTextFallback.Text = null;
        PlainTextFallback.IsVisible = false;
        _building = new CancellationTokenSource();
        _ = BuildAsync(Text, continuousSelection, generation, _building.Token);
    }

    /// <summary>
    /// Blocks arrive a few at a time. A document is laid out on the thread that
    /// draws — there is nowhere else to build controls — so the work is cut
    /// into steps and the thread handed back between them; a document twice as
    /// long then takes twice as many steps rather than one twice as long.
    /// </summary>
    private async Task BuildAsync(
        string? markdown,
        bool continuousSelection,
        int generation,
        CancellationToken token)
    {
        try
        {
            // Provider streams can update a partial Markdown block many times
            // in one frame. Parse only the latest value, away from Avalonia's
            // UI thread, then build native controls in short UI-thread steps.
            await Task.Delay(TimeSpan.FromMilliseconds(24), token);
            ImmutableArray<MarkdownBlock> blocks;
            await ParseGate.WaitAsync(token);
            try
            {
                // Markdig cannot abort a parse already running. Serial admission
                // prevents rapid streamed revisions accumulating parallel ASTs;
                // canceled waiting revisions never start another parse.
                blocks = await Task.Run(() => MarkdownPreviewDocument.Parse(markdown), token);
            }
            finally
            {
                ParseGate.Release();
            }
            token.ThrowIfCancellationRequested();
            if (blocks.Length > 32 || markdown?.Length > 64 * 1024 || blocks.Any(RequiresSourceViewport))
            {
                CommitDemandDocument(blocks, markdown!, generation, token);
                return;
            }
            if (continuousSelection)
            {
                CommitContinuousDocument(blocks, generation, token);
                return;
            }

            BeginBlockCommit(generation, token);
            for (var start = 0; start < blocks.Length; start += BlocksPerStep)
            {
                token.ThrowIfCancellationRequested();
                var first = start;
                var count = Math.Min(BlocksPerStep, blocks.Length - start);
                CommitBlockBatch(blocks, first, count, generation, token);
                if (start + count < blocks.Length)
                {
                    await Task.Yield();
                }
            }

            CompleteBuild(generation, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "markdown.render.failed",
                SecretSafeDiagnosticKind.Unexpected);
            CommitPlainTextFallback(generation, markdown);
        }
    }

    private bool IsCurrentBuild(int generation, CancellationToken token) =>
        generation == _buildGeneration
        && !token.IsCancellationRequested
        && VisualRoot is not null;

    private void CommitDemandDocument(
        ImmutableArray<MarkdownBlock> blocks, string markdown, int generation, CancellationToken token)
    {
        if (!IsCurrentBuild(generation, token))
        {
            return;
        }

        var copy = new Button { Content = "Copy all Markdown", HorizontalAlignment = HorizontalAlignment.Right };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(markdown);
            }
        };
        var list = new ListBox
        {
            ItemsSource = blocks,
            MaxHeight = 560,
            ItemTemplate = new FuncDataTemplate<MarkdownBlock>((block, _) =>
                block is null ? null : DemandBlock(block, markdown)),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(list, "Markdown document, scroll to read all blocks");
        var document = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), MaxHeight = 600 };
        document.Children.Add(copy);
        Grid.SetRow(list, 1);
        document.Children.Add(list);
        Blocks.Children.Clear();
        Blocks.Children.Add(document);
        CompleteBuild(generation, token);
    }

    private Control? DemandBlock(MarkdownBlock block, string markdown)
    {
        if (RequiresSourceViewport(block))
        {
            if (block.Kind == MarkdownBlockKind.Table)
            {
                return PagedTable(block);
            }

            var start = Math.Clamp(block.SourceStart, 0, markdown.Length);
            var length = Math.Clamp(block.SourceLength, 0, markdown.Length - start);
            var source = new CodePreviewView
            {
                Text = markdown.Substring(start, length),
                FitsContent = false,
                Height = 320,
            };
            var surface = new StackPanel();
            surface.Children.Add(new TextBlock { Text = "Large block shown as scrollable source. All text is retained.", TextWrapping = TextWrapping.Wrap });
            surface.Children.Add(source);
            return surface;
        }

        return Build(block);
    }

    private static bool RequiresSourceViewport(MarkdownBlock block) =>
        block.SourceLength > 16 * 1024
        || block.Runs.Length > 256
        || block.HeaderCells.Length > 32
        || block.Rows.Length > 64
        || block.Rows.Sum(row => row.Length) > 128;

    private Control PagedTable(MarkdownBlock block)
    {
        const int rowsPerPage = 16;
        const int columnsPerPage = 8;
        var rowOffset = 0;
        var columnOffset = 0;
        var columnCount = Math.Max(block.HeaderCells.Length, block.Rows.IsEmpty ? 0 : block.Rows.Max(row => row.Length));
        var surface = new StackPanel();
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var previousRows = new Button { Content = "Previous rows" };
        var nextRows = new Button { Content = "Next rows" };
        var previousColumns = new Button { Content = "Previous columns" };
        var nextColumns = new Button { Content = "Next columns" };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new ContentControl();
        navigation.Children.Add(previousRows);
        navigation.Children.Add(nextRows);
        navigation.Children.Add(previousColumns);
        navigation.Children.Add(nextColumns);
        surface.Children.Add(summary);
        surface.Children.Add(navigation);
        surface.Children.Add(content);
        previousRows.Click += (_, _) => { rowOffset = Math.Max(0, rowOffset - rowsPerPage); RenderPage(); };
        nextRows.Click += (_, _) => { rowOffset += rowsPerPage; RenderPage(); };
        previousColumns.Click += (_, _) => { columnOffset = Math.Max(0, columnOffset - columnsPerPage); RenderPage(); };
        nextColumns.Click += (_, _) => { columnOffset += columnsPerPage; RenderPage(); };
        RenderPage();
        return surface;

        void RenderPage()
        {
            previousRows.IsEnabled = rowOffset > 0;
            nextRows.IsEnabled = rowOffset + rowsPerPage < block.Rows.Length;
            previousColumns.IsEnabled = columnOffset > 0;
            nextColumns.IsEnabled = columnOffset + columnsPerPage < columnCount;
            summary.Text = $"Table rows {rowOffset + 1}–{Math.Min(rowOffset + rowsPerPage, block.Rows.Length)} of {block.Rows.Length}; columns {columnOffset + 1}–{Math.Min(columnOffset + columnsPerPage, columnCount)} of {columnCount}.";
            content.Content = Table(block with
            {
                HeaderCells = [.. block.HeaderCells.Skip(columnOffset).Take(columnsPerPage)],
                Rows = [.. block.Rows.Skip(rowOffset).Take(rowsPerPage)
                    .Select(row => row.Skip(columnOffset).Take(columnsPerPage).ToImmutableArray())],
            });
        }
    }

    private void CommitContinuousDocument(
        ImmutableArray<MarkdownBlock> blocks,
        int generation,
        CancellationToken token)
    {
        if (!IsCurrentBuild(generation, token))
        {
            return;
        }

        Blocks.Children.Clear();
        if (!blocks.IsEmpty)
        {
            Blocks.Children.Add(ContinuousDocument(blocks));
        }

        CompleteBuild(generation, token);
    }

    private void BeginBlockCommit(int generation, CancellationToken token)
    {
        if (IsCurrentBuild(generation, token))
        {
            Blocks.Children.Clear();
        }
    }

    private void CommitBlockBatch(
        ImmutableArray<MarkdownBlock> blocks,
        int start,
        int count,
        int generation,
        CancellationToken token)
    {
        if (!IsCurrentBuild(generation, token))
        {
            return;
        }

        for (var index = start; index < start + count; index++)
        {
            if (Build(blocks[index]) is { } control)
            {
                Blocks.Children.Add(control);
            }
        }
    }

    private void CompleteBuild(int generation, CancellationToken token)
    {
        if (!IsCurrentBuild(generation, token))
        {
            return;
        }

        RenderingState.IsVisible = false;
        PlainTextFallback.IsVisible = false;
        _hasRendered = true;
        _building?.Dispose();
        _building = null;
    }

    private void CommitPlainTextFallback(int generation, string? markdown)
    {
        if (generation != _buildGeneration || VisualRoot is null)
        {
            return;
        }

        Blocks.Children.Clear();
        PlainTextFallback.Text = markdown;
        PlainTextFallback.IsVisible = !string.IsNullOrEmpty(markdown);
        RenderingState.IsVisible = false;
        _hasRendered = true;
        _building?.Dispose();
        _building = null;
    }

    /// <summary>
    /// Blocks laid out before the thread is handed back. A fenced block is the
    /// expensive one, so the step is small.
    /// </summary>
    private const int BlocksPerStep = 8;

    private CancellationTokenSource? _building;

    /// <summary>
    /// Avalonia selection cannot cross control boundaries. Keeping all prose
    /// in one native document gives it one drag-selection range while its
    /// block layout retains headings and hanging list indents. Markdown syntax
    /// markers are not exposed in the text being selected.
    /// </summary>
    private Control ContinuousDocument(ImmutableArray<MarkdownBlock> blocks)
    {
        if (!blocks.Any(IsEmbeddedBlock))
        {
            return ContinuousText(blocks);
        }

        // Fenced code and diagrams are real controls, so they cannot live
        // inside the prose document. Keep every contiguous prose region as one
        // selection surface and place embedded blocks at their Markdown
        // position. This is the same code/diagram renderer file previews use.
        var document = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = Metric("ShellSpaceSm", 8),
        };
        var prose = ImmutableArray.CreateBuilder<MarkdownBlock>();
        foreach (var block in blocks)
        {
            if (!IsEmbeddedBlock(block))
            {
                prose.Add(block);
                continue;
            }

            AddContinuousText(document, prose);
            document.Children.Add(EmbeddedBlock(block));
        }

        AddContinuousText(document, prose);
        return document;
    }

    private void AddContinuousText(
        Panel document,
        ImmutableArray<MarkdownBlock>.Builder prose)
    {
        if (prose.Count == 0)
        {
            return;
        }

        document.Children.Add(ContinuousText(prose.ToImmutable()));
        prose.Clear();
    }

    private static Control ContinuousText(ImmutableArray<MarkdownBlock> blocks) =>
        new SelectableMarkdownDocument(blocks);

    private Control? Build(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => Heading(block),
        MarkdownBlockKind.Paragraph => Paragraph(block),
        MarkdownBlockKind.ListItem => ListItem(block),
        MarkdownBlockKind.Quote => Quote(block),
        MarkdownBlockKind.Code => IsMermaid(block) ? Mermaid(block) : Code(block),
        MarkdownBlockKind.ThematicBreak => ThematicBreak(),
        MarkdownBlockKind.Table => Table(block),
        MarkdownBlockKind.Math => Formula(block),
        _ => null,
    };

    private static bool IsMermaid(MarkdownBlock block) =>
        block.Kind == MarkdownBlockKind.Code
        && string.Equals(block.Language?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmbeddedBlock(MarkdownBlock block) =>
        block.Kind is MarkdownBlockKind.Code or MarkdownBlockKind.Table;

    private Control EmbeddedBlock(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Table => Table(block),
        _ when IsMermaid(block) => Mermaid(block),
        _ => Code(block),
    };

    private Control Mermaid(MarkdownBlock block)
    {
        var diagram = new DatabaseMermaidDiagramView
        {
            MermaidSource = block.Text ?? string.Empty,
            Height = 320,
            MinHeight = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(diagram, "Rendered Mermaid diagram");
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Margin = new Thickness(0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = diagram,
            Background = Brush("ShellBackgroundBrush") ?? Brushes.Transparent,
            BorderBrush = Brush("ShellBorderBrush") ?? Brushes.Gray
        };
        return frame;
    }

    private Control Heading(MarkdownBlock block)
    {
        var text = Prose(block.Runs);
        var size = HeadingSizes[Math.Clamp(block.Level, 1, HeadingSizes.Length) - 1];
        text.FontSize = size;
        text.LineHeight = Math.Round(size * 1.3);
        text.FontWeight = FontWeight.SemiBold;
        // Space above a heading, not below: a heading belongs to what follows
        // it, and an even gap on both sides makes it float between sections.
        text.Margin = new Thickness(0, block.Level == 1 ? 2 : 14, 0, 2);
        return text;
    }

    private Control Paragraph(MarkdownBlock block) => Prose(block.Runs);

    private Control ListItem(MarkdownBlock block)
    {
        // The bullet is its own column so wrapped lines line up under the text
        // rather than under the marker.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness((Math.Max(0, block.Level - 1) * 18) + 2, 0, 0, 0),
        };
        var bullet = new SelectableTextBlock
        {
            Text = string.IsNullOrEmpty(block.Bullet) ? string.Empty : block.Bullet,
            MinWidth = 20,
            // The same size and line height as the text beside it, or the
            // marker rides above the first line instead of sitting on it.
            FontSize = BodyFontSize,
            LineHeight = Math.Round(BodyFontSize * 1.55),
        };
        Paint(bullet, "ShellMutedBrush");
        var content = Prose(block.Runs);
        Grid.SetColumn(content, 1);
        grid.Children.Add(bullet);
        grid.Children.Add(content);
        return grid;
    }

    private Control Quote(MarkdownBlock block)
    {
        var content = Prose(block.Runs);
        Paint(content, "ShellMutedBrush");
        content.Margin = new Thickness(12, 4, 0, 4);
        var quote = new Border
        {
            BorderThickness = new Thickness(2, 0, 0, 0),
            Child = content,
        };
        if (Brush("ShellAccentBrush") is { } accent)
        {
            quote.BorderBrush = accent;
        }

        return quote;
    }

    private Control Code(MarkdownBlock block)
    {
        // The same source view the file preview uses, so a fenced block is
        // highlighted exactly like the file it was copied from.
        var editor = new CodePreviewView
        {
            Text = block.Text,
            FileName = block.Language is null ? null : $"fenced.{block.Language}",
            // Sized by the editor's own line height: a guessed height clips the
            // last line or leaves a screen of empty gutter under three lines.
            FitsContent = true,
        };
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(6, 4),
            Child = editor,
        };
        if (Brush("ShellBackgroundBrush") is { } fill)
        {
            frame.Background = fill;
        }

        if (Brush("ShellBorderBrush") is { } edge)
        {
            frame.BorderBrush = edge;
        }

        return frame;
    }

    private Control Formula(MarkdownBlock block)
    {
        var formula = new MathView
        {
            LaTeX = block.Text,
            DisplayErrorInline = false,
            FontSize = (float)BodyFontSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4),
        };
        if (Brush("ShellTextBrush") is ISolidColorBrush foreground)
        {
            formula.TextColor = foreground.Color;
        }

        return formula.ErrorMessage is null
            ? formula
            : new SelectableTextBlock
            {
                Text = block.Text,
                FontSize = BodyFontSize,
                TextWrapping = TextWrapping.Wrap,
            };
    }

    private Control ThematicBreak()
    {
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 6, 0, 6),
            Background = Brush("ShellBorderBrush") ?? Brushes.Gray
        };
        return rule;
    }

    private Control Table(MarkdownBlock block)
    {
        var columns = Math.Max(
            block.HeaderCells.Length,
            block.Rows.Length == 0 ? 0 : block.Rows.Max(row => row.Length));
        if (columns == 0)
        {
            return new Border();
        }

        var grid = new Grid();
        for (var column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        }

        var row = 0;
        if (!block.HeaderCells.IsEmpty)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < block.HeaderCells.Length; column++)
            {
                var cell = Cell(block.HeaderCells[column], isHeader: true);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }

            row++;
        }

        foreach (var cells in block.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < cells.Length; column++)
            {
                var cell = Cell(cells[column], isHeader: false);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }

            row++;
        }

        var table = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MinWidth = columns * MinimumTableColumnWidth,
            Width = columns * MinimumTableColumnWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = grid,
        };
        if (Brush("ShellBorderBrush") is { } outline)
        {
            table.BorderBrush = outline;
        }

        var viewport = new ScrollViewer
        {
            Margin = new Thickness(0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BringIntoViewOnFocusChange = false,
            Content = table,
        };
        AutomationProperties.SetName(viewport, "Scrollable Markdown table");
        AutomationProperties.SetHelpText(
            viewport,
            "Scroll horizontally to read every table column.");
        viewport.SizeChanged += (_, e) =>
        {
            table.Width = Math.Max(table.MinWidth, e.NewSize.Width);
        };
        return viewport;
    }

    private Control Cell(ImmutableArray<MarkdownRun> runs, bool isHeader)
    {
        if (runs.Length > 128 || runs.Sum(run => run.Text.Length) > 16 * 1024)
        {
            return new CodePreviewView
            {
                Text = string.Concat(runs.Select(run => run.Text)),
                FitsContent = false,
                Height = 160,
            };
        }

        var content = Prose(runs);
        content.Margin = new Thickness(10, 5);
        if (isHeader)
        {
            content.FontWeight = FontWeight.SemiBold;
        }

        var cell = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = content,
        };
        if (Brush("ShellBorderBrush") is { } grid)
        {
            cell.BorderBrush = grid;
        }

        return cell;
    }

    /// <summary>
    /// One selectable block of text carrying every run's appearance as inlines,
    /// so a whole paragraph selects and copies as one piece of prose.
    /// </summary>
    private SelectableTextBlock Prose(ImmutableArray<MarkdownRun> runs)
    {
        var text = new SelectableTextBlock
        {
            FontSize = BodyFontSize,
            // Prose at a terminal's line spacing reads as a wall; 1.55 is the
            // ratio the shell's own documentation surfaces use.
            LineHeight = Math.Round(BodyFontSize * 1.55),
            TextWrapping = TextWrapping.Wrap,
        };
        foreach (var run in runs)
        {
            text.Inlines?.Add(Inline(run));
        }

        return text;
    }

    private Inline Inline(MarkdownRun run)
    {
        if (run.Style.HasFlag(MarkdownRunStyle.Math))
        {
            var formula = new MathView
            {
                LaTeX = run.Text,
                DisplayErrorInline = false,
                FontSize = (float)BodyFontSize,
            };
            if (Brush("ShellTextBrush") is ISolidColorBrush foreground)
            {
                formula.TextColor = foreground.Color;
            }

            if (formula.ErrorMessage is null)
            {
                return new InlineUIContainer { Child = formula };
            }
        }

        var inline = new Run(run.Text);
        if (run.Style.HasFlag(MarkdownRunStyle.Bold))
        {
            inline.FontWeight = FontWeight.SemiBold;
        }

        if (run.Style.HasFlag(MarkdownRunStyle.Italic))
        {
            inline.FontStyle = FontStyle.Italic;
        }

        if (run.Style.HasFlag(MarkdownRunStyle.Strikethrough))
        {
            inline.TextDecorations = TextDecorations.Strikethrough;
        }

        if (run.Style.HasFlag(MarkdownRunStyle.Code))
        {
            if (Resource<FontFamily>("ShellDataFontFamily") is { } mono)
            {
                inline.FontFamily = mono;
            }

            Tint(inline, "ShellAccentBrush");
        }

        if (run.LinkTarget is not null)
        {
            // Shown as a link and copyable as text; a preview does not open
            // things on the user's behalf.
            Tint(inline, "ShellAccentBrush");
            inline.TextDecorations = TextDecorations.Underline;
        }

        return inline;
    }

    /// <summary>
    /// Applies a themed foreground only when the theme actually has one:
    /// assigning a missing resource would paint the text with nothing, which
    /// draws as nothing.
    /// </summary>
    private void Paint(TextBlock text, string key)
    {
        if (Brush(key) is { } brush)
        {
            text.Foreground = brush;
        }
    }

    private void Tint(Run inline, string key)
    {
        if (Brush(key) is { } brush)
        {
            inline.Foreground = brush;
        }
    }

    private IBrush? Brush(string key) => Resource<IBrush>(key);

    private double Metric(string key, double fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is double metric
            ? metric
            : fallback;

    private T? Resource<T>(string key)
        where T : class =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as T : null;
}
