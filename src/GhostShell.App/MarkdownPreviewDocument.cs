using System.Collections.Immutable;
using System.Text;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace GhostShell.App;

/// <summary>How a run of text is emphasized.</summary>
[Flags]
public enum MarkdownRunStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strikethrough = 8,
    Math = 16,
}

/// <summary>
/// A stretch of text with one appearance. A link keeps its target so the
/// presentation can offer it without re-parsing anything.
/// </summary>
public sealed record MarkdownRun(string Text, MarkdownRunStyle Style, string? LinkTarget = null);

public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    Code,
    Quote,
    ListItem,
    ThematicBreak,
    Table,
    Math,
}

/// <summary>
/// One rendered block. Headings carry their level, list items their depth and
/// bullet, code its language so the source preview can highlight it, and tables
/// their rows.
/// </summary>
public sealed record MarkdownBlock
{
    public required MarkdownBlockKind Kind { get; init; }

    public ImmutableArray<MarkdownRun> Runs { get; init; } = [];

    public int Level { get; init; }

    public string? Language { get; init; }

    public string? Text { get; init; }

    public string? Bullet { get; init; }

    public ImmutableArray<ImmutableArray<MarkdownRun>> HeaderCells { get; init; } = [];

    public ImmutableArray<ImmutableArray<ImmutableArray<MarkdownRun>>> Rows { get; init; } = [];

    public int SourceStart { get; init; } = -1;

    public int SourceLength { get; init; }
}

/// <summary>
/// Markdown parsed into blocks the presentation can lay out directly.
///
/// The parse is kept separate from the rendering so what a document becomes is
/// testable without a display: the tests here are about structure — that a
/// heading is a heading and a fence keeps its language — not about pixels.
/// </summary>
public static class MarkdownPreviewDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseGridTables()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .UseMathematics()
        .UseBackslashMathematics()
        // Deliberately no raw-HTML rendering: an HTML block in a Markdown file
        // is shown as the text it is, never interpreted.
        .Build();

    private static readonly string[] MarkdownExtensions =
        [".md", ".markdown", ".mdown", ".mkd", ".mdx"];

    /// <summary>Whether a file should be previewed as Markdown.</summary>
    public static bool IsMarkdown(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && MarkdownExtensions.Contains(
            Path.GetExtension(fileName.Trim()),
            StringComparer.OrdinalIgnoreCase);

    public static ImmutableArray<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return [];
        }

        var document = Markdown.Parse(markdown, Pipeline);
        var blocks = ImmutableArray.CreateBuilder<MarkdownBlock>();
        foreach (var block in document)
        {
            AppendBlock(blocks, block, depth: 0);
        }

        return blocks.ToImmutable();
    }

    private static void AppendBlock(
        ImmutableArray<MarkdownBlock>.Builder blocks,
        Block block,
        int depth,
        string? bullet = null)
    {
        var first = blocks.Count;
        AppendBlockCore(blocks, block, depth, bullet);
        for (var index = first; index < blocks.Count; index++)
        {
            if (blocks[index].SourceStart < 0)
            {
                blocks[index] = blocks[index] with
                {
                    SourceStart = block.Span.Start,
                    SourceLength = block.Span.Length,
                };
            }
        }
    }

    private static void AppendBlockCore(
        ImmutableArray<MarkdownBlock>.Builder blocks,
        Block block,
        int depth,
        string? bullet)
    {
        switch (block)
        {
            case HeadingBlock heading:
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Heading,
                    Level = Math.Clamp(heading.Level, 1, 6),
                    Runs = Inlines(heading.Inline),
                });
                break;
            case ParagraphBlock paragraph:
                blocks.Add(new MarkdownBlock
                {
                    Kind = bullet is null
                        ? MarkdownBlockKind.Paragraph
                        : MarkdownBlockKind.ListItem,
                    Level = depth,
                    Bullet = bullet,
                    Runs = Inlines(paragraph.Inline),
                });
                break;
            case MathBlock math:
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Math,
                    Text = Lines(math),
                    Runs = [new MarkdownRun(Lines(math), MarkdownRunStyle.Math)],
                });
                break;
            case FencedCodeBlock fenced:
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Code,
                    Language = string.IsNullOrWhiteSpace(fenced.Info) ? null : fenced.Info,
                    Text = Lines(fenced),
                });
                break;
            case CodeBlock code:
                blocks.Add(new MarkdownBlock
                {
                    Kind = MarkdownBlockKind.Code,
                    Text = Lines(code),
                });
                break;
            case ThematicBreakBlock:
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.ThematicBreak });
                break;
            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    var start = blocks.Count;
                    AppendBlock(blocks, child, depth, bullet);
                    for (var index = start; index < blocks.Count; index++)
                    {
                        blocks[index] = blocks[index] with { Kind = MarkdownBlockKind.Quote };
                    }
                }

                break;
            case Table table:
                blocks.Add(BuildTable(table));
                break;
            case ListBlock list:
                var ordinal = list.OrderedStart is { } startText
                    && int.TryParse(startText, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed
                    : 1;
                foreach (var item in list)
                {
                    if (item is not ListItemBlock listItem)
                    {
                        continue;
                    }

                    var marker = list.IsOrdered
                        ? $"{ordinal++}."
                        : "•";
                    var first = true;
                    foreach (var child in listItem)
                    {
                        AppendBlock(blocks, child, depth + 1, first ? marker : string.Empty);
                        first = false;
                    }
                }

                break;
            case ContainerBlock container:
                foreach (var child in container)
                {
                    AppendBlock(blocks, child, depth, bullet);
                }

                break;
        }
    }

    private static MarkdownBlock BuildTable(Table table)
    {
        var header = ImmutableArray<ImmutableArray<MarkdownRun>>.Empty;
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<ImmutableArray<MarkdownRun>>>();
        foreach (var child in table)
        {
            if (child is not TableRow row)
            {
                continue;
            }

            var cells = ImmutableArray.CreateBuilder<ImmutableArray<MarkdownRun>>();
            foreach (var cellBlock in row)
            {
                if (cellBlock is not TableCell cell)
                {
                    continue;
                }

                var runs = ImmutableArray.CreateBuilder<MarkdownRun>();
                foreach (var content in cell)
                {
                    if (content is ParagraphBlock paragraph)
                    {
                        runs.AddRange(Inlines(paragraph.Inline));
                    }
                }

                cells.Add(runs.ToImmutable());
            }

            if (row.IsHeader && header.IsEmpty)
            {
                header = cells.ToImmutable();
            }
            else
            {
                rows.Add(cells.ToImmutable());
            }
        }

        return new MarkdownBlock
        {
            Kind = MarkdownBlockKind.Table,
            HeaderCells = header,
            Rows = rows.ToImmutable(),
        };
    }

    private static string Lines(LeafBlock block)
    {
        var builder = new StringBuilder();
        foreach (var line in block.Lines.Lines)
        {
            if (line.Slice.Text is null)
            {
                continue;
            }

            builder.AppendLine(line.Slice.ToString());
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static ImmutableArray<MarkdownRun> Inlines(ContainerInline? container)
    {
        if (container is null)
        {
            return [];
        }

        var runs = ImmutableArray.CreateBuilder<MarkdownRun>();
        AppendInlines(runs, container, MarkdownRunStyle.None, null);
        return runs.ToImmutable();
    }

    private static void AppendInlines(
        ImmutableArray<MarkdownRun>.Builder runs,
        ContainerInline container,
        MarkdownRunStyle style,
        string? link)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Append(runs, literal.Content.ToString(), style, link);
                    break;
                case CodeInline code:
                    Append(runs, code.Content, style | MarkdownRunStyle.Code, link);
                    break;
                case MathInline math:
                    Append(runs, math.Content.ToString(), style | MarkdownRunStyle.Math, link);
                    break;
                case EmphasisInline emphasis:
                    AppendInlines(
                        runs,
                        emphasis,
                        style | EmphasisStyle(emphasis),
                        link);
                    break;
                case LinkInline { IsImage: true } image:
                    // An image inside Markdown is described, not fetched: a
                    // preview must not reach out to the network on its own.
                    var alt = Flatten(image);
                    Append(
                        runs,
                        string.IsNullOrWhiteSpace(alt) ? image.Url ?? "image" : alt,
                        style | MarkdownRunStyle.Italic,
                        null);
                    break;
                case LinkInline anchor:
                    AppendInlines(runs, anchor, style, anchor.Url);
                    break;
                case AutolinkInline autolink:
                    Append(runs, autolink.Url, style, autolink.Url);
                    break;
                case LineBreakInline lineBreak:
                    Append(runs, lineBreak.IsHard ? "\n" : " ", style, link);
                    break;
                case ContainerInline nested:
                    AppendInlines(runs, nested, style, link);
                    break;
                case HtmlInline html:
                    // Shown as written rather than interpreted.
                    Append(runs, html.Tag, style | MarkdownRunStyle.Code, link);
                    break;
            }
        }
    }

    private static MarkdownRunStyle EmphasisStyle(EmphasisInline emphasis) =>
        emphasis.DelimiterChar switch
        {
            '~' => MarkdownRunStyle.Strikethrough,
            _ => emphasis.DelimiterCount >= 2
                ? MarkdownRunStyle.Bold
                : MarkdownRunStyle.Italic,
        };

    private static string Flatten(ContainerInline container)
    {
        var builder = new StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case ContainerInline nested:
                    builder.Append(Flatten(nested));
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Adjacent runs that look the same are merged, so a paragraph split into
    /// dozens of literals by the parser does not become dozens of text controls.
    /// </summary>
    private static void Append(
        ImmutableArray<MarkdownRun>.Builder runs,
        string text,
        MarkdownRunStyle style,
        string? link)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (runs.Count > 0
            && runs[^1].Style == style
            && string.Equals(runs[^1].LinkTarget, link, StringComparison.Ordinal)
            && runs[^1].Text.Length <= 4096 - text.Length)
        {
            // Keep coalescing linear in source size: many tiny escaped literals
            // must not repeatedly copy an ever-growing paragraph string.
            runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            return;
        }

        runs.Add(new MarkdownRun(text, style, link));
    }
}
