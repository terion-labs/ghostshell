using GhostShell.App;

namespace GhostShell.App.Tests;

public sealed class MarkdownPreviewDocumentTests
{
    [Fact]
    public void ManyAdjacentEscapedLiteralsRetainAllTextWithoutGrowingRunConcatenation()
    {
        var markdown = string.Concat(Enumerable.Repeat("a\\*", 20_000));
        var block = Assert.Single(MarkdownPreviewDocument.Parse(markdown));
        Assert.Equal(string.Concat(Enumerable.Repeat("a*", 20_000)), string.Concat(block.Runs.Select(run => run.Text)));
        Assert.All(block.Runs, run => Assert.InRange(run.Text.Length, 1, 4096));
        Assert.Equal(markdown.Length, block.SourceLength);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("notes.MARKDOWN")]
    [InlineData("design.mdown")]
    public void Markdown_files_are_recognized(string fileName) =>
        Assert.True(MarkdownPreviewDocument.IsMarkdown(fileName));

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("readme")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_files_are_not(string? fileName) =>
        Assert.False(MarkdownPreviewDocument.IsMarkdown(fileName));

    [Fact]
    public void Headings_keep_their_level_and_text()
    {
        var blocks = MarkdownPreviewDocument.Parse("# Title\n\n### Third");

        Assert.Collection(
            blocks,
            block =>
            {
                Assert.Equal(MarkdownBlockKind.Heading, block.Kind);
                Assert.Equal(1, block.Level);
                Assert.Equal("Title", Assert.Single(block.Runs).Text);
            },
            block =>
            {
                Assert.Equal(MarkdownBlockKind.Heading, block.Kind);
                Assert.Equal(3, block.Level);
                Assert.Equal("Third", Assert.Single(block.Runs).Text);
            });
    }

    [Fact]
    public void Emphasis_code_and_links_become_styled_runs()
    {
        var blocks = MarkdownPreviewDocument.Parse(
            "Plain **bold** *italic* `code` [link](https://example.com).");

        var runs = Assert.Single(blocks).Runs;
        Assert.Contains(runs, run => string.Equals(run.Text, "bold", StringComparison.Ordinal) && run.Style == MarkdownRunStyle.Bold);
        Assert.Contains(runs, run => string.Equals(run.Text, "italic", StringComparison.Ordinal) && run.Style == MarkdownRunStyle.Italic);
        Assert.Contains(runs, run => string.Equals(run.Text, "code", StringComparison.Ordinal) && run.Style == MarkdownRunStyle.Code);
        Assert.Contains(
            runs,
            run => string.Equals(run.Text, "link", StringComparison.Ordinal) && string.Equals(run.LinkTarget, "https://example.com", StringComparison.Ordinal));
        // Adjacent plain text is one run, not one per parsed literal.
        Assert.Equal("Plain ", runs[0].Text);
    }

    [Fact]
    public void A_fence_keeps_its_language_and_body()
    {
        var blocks = MarkdownPreviewDocument.Parse("```csharp\nvar x = 1;\nvar y = 2;\n```");

        var block = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Code, block.Kind);
        Assert.Equal("csharp", block.Language);
        Assert.Equal("var x = 1;\nvar y = 2;", block.Text?.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Lists_carry_a_bullet_and_a_depth()
    {
        var blocks = MarkdownPreviewDocument.Parse("- one\n- two\n\n1. first\n2. second");

        var items = blocks.Where(block => block.Kind == MarkdownBlockKind.ListItem).ToArray();
        Assert.Equal(4, items.Length);
        Assert.Equal("•", items[0].Bullet);
        Assert.Equal("one", items[0].Runs[0].Text);
        Assert.All(items, item => Assert.True(item.Level > 0));
        // An ordered list counts rather than repeating one marker.
        Assert.Equal("1.", items[2].Bullet);
        Assert.Equal("2.", items[3].Bullet);
    }

    [Fact]
    public void A_quote_is_marked_as_one()
    {
        var blocks = MarkdownPreviewDocument.Parse("> quoted words");

        var block = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Quote, block.Kind);
        Assert.Equal("quoted words", block.Runs[0].Text);
    }

    [Fact]
    public void A_table_keeps_its_header_and_rows()
    {
        var blocks = MarkdownPreviewDocument.Parse(
            "| Name | Size |\n| --- | --- |\n| a.txt | 1 KB |\n| b.txt | 2 KB |");

        var table = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Table, table.Kind);
        Assert.Equal(["Name", "Size"], table.HeaderCells.Select(cell => cell[0].Text), StringComparer.Ordinal);
        Assert.Equal(2, table.Rows.Length);
        Assert.Equal("b.txt", table.Rows[1][0][0].Text);
    }

    [Fact]
    public void Embedded_html_is_shown_as_text_rather_than_interpreted()
    {
        var blocks = MarkdownPreviewDocument.Parse("Before <b>bold?</b> after");

        var runs = Assert.Single(blocks).Runs;
        // The tag itself is visible: a Markdown preview renders Markdown, and
        // silently honouring embedded HTML would make the preview a renderer
        // for something the file never declared.
        Assert.Contains(runs, run => run.Text.Contains("<b>", StringComparison.Ordinal));
    }

    [Fact]
    public void An_image_is_described_rather_than_fetched()
    {
        var blocks = MarkdownPreviewDocument.Parse("![a diagram](https://example.com/x.png)");

        var runs = Assert.Single(blocks).Runs;
        var run = Assert.Single(runs);
        Assert.Equal("a diagram", run.Text);
        // No link target: nothing in the preview should invite a fetch.
        Assert.Null(run.LinkTarget);
    }

    [Fact]
    public void Backslash_delimited_inline_and_display_math_are_structured()
    {
        var blocks = MarkdownPreviewDocument.Parse(
            "Let \\(B\\) be true.\n\n\\[\nB \\rightarrow G_T\n\\]");

        Assert.Collection(
            blocks,
            paragraph =>
            {
                Assert.Equal(MarkdownBlockKind.Paragraph, paragraph.Kind);
                Assert.Contains(
                    paragraph.Runs,
                    run => string.Equals(run.Text, "B"
, StringComparison.Ordinal) && run.Style.HasFlag(MarkdownRunStyle.Math));
            },
            display =>
            {
                Assert.Equal(MarkdownBlockKind.Math, display.Kind);
                Assert.Equal("B \\rightarrow G_T", display.Text);
                Assert.True(Assert.Single(display.Runs).Style.HasFlag(MarkdownRunStyle.Math));
            });
    }

    [Fact]
    public void Formula_delimiters_inside_code_are_not_interpreted()
    {
        var blocks = MarkdownPreviewDocument.Parse(
            "`\\(not math\\)`\n\n```text\n\\[still not math\\]\n```");

        Assert.False(Assert.Single(blocks[0].Runs).Style.HasFlag(MarkdownRunStyle.Math));
        Assert.Equal(MarkdownBlockKind.Code, blocks[1].Kind);
        Assert.Contains("\\[still not math\\]", blocks[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_document_parses_to_nothing()
    {
        Assert.Empty(MarkdownPreviewDocument.Parse(string.Empty));
        Assert.Empty(MarkdownPreviewDocument.Parse(null));
    }
}
