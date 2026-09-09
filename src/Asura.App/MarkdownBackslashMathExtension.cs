using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax;

namespace Asura.App;

/// <summary>
/// Adds the CommonMark-adjacent LaTeX delimiters emitted by chat providers:
/// <c>\(...\)</c> for inline math and <c>\[...\]</c> for display math.
/// Markdig's built-in mathematics extension remains responsible for the math
/// AST and for dollar-delimited input.
/// </summary>
internal sealed class MarkdownBackslashMathExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.BlockParsers.Contains<BackslashMathBlockParser>())
        {
            pipeline.BlockParsers.Insert(0, new BackslashMathBlockParser());
        }

        if (!pipeline.InlineParsers.Contains<BackslashMathInlineParser>())
        {
            pipeline.InlineParsers.Insert(0, new BackslashMathInlineParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}

internal static class MarkdownBackslashMathExtensions
{
    internal static MarkdownPipelineBuilder UseBackslashMathematics(
        this MarkdownPipelineBuilder pipeline)
    {
        pipeline.Extensions.AddIfNotAlready<MarkdownBackslashMathExtension>();
        return pipeline;
    }
}

internal sealed class BackslashMathInlineParser : InlineParser
{
    internal BackslashMathInlineParser()
    {
        OpeningCharacters = ['\\'];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var openingDelimiter = slice.PeekChar(1);
        var closingDelimiter = openingDelimiter switch
        {
            '(' => ')',
            '[' => ']',
            _ => '\0',
        };
        if (closingDelimiter == '\0')
        {
            return false;
        }

        var openingStart = slice.Start;
        var contentStart = openingStart + 2;
        var closingStart = FindClosingDelimiter(slice, contentStart, closingDelimiter);
        if (closingStart < 0)
        {
            return false;
        }

        var closingEnd = closingStart + 1;
        processor.Inline = new MathInline
        {
            Delimiter = '\\',
            DelimiterCount = 2,
            Content = contentStart < closingStart
                ? new StringSlice(slice.Text, contentStart, closingStart - 1)
                : StringSlice.Empty,
            Span = new SourceSpan(openingStart, closingEnd),
        };
        slice.Start = closingEnd + 1;
        return true;
    }

    private static int FindClosingDelimiter(
        StringSlice slice,
        int start,
        char closingDelimiter)
    {
        for (var index = start; index <= slice.End; index++)
        {
            var character = slice.Text[index];
            if (character is '\r' or '\n')
            {
                return -1;
            }

            if (character == '\\'
                && index < slice.End
                && slice.Text[index + 1] == closingDelimiter)
            {
                return index;
            }
        }

        return -1;
    }
}

internal sealed class BackslashMathBlockParser : BlockParser
{
    internal BackslashMathBlockParser()
    {
        OpeningCharacters = ['\\'];
    }

    public override bool CanInterrupt(BlockProcessor processor, Block block) =>
        IsOpeningLine(processor);

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (!IsOpeningLine(processor))
        {
            return BlockState.None;
        }

        var block = new MathBlock(this)
        {
            FencedChar = '\\',
            OpeningFencedCharCount = 2,
            Line = processor.LineIndex,
            Column = processor.Column,
            Span = new SourceSpan(processor.Line.Start, processor.Line.End),
        };
        processor.NewBlocks.Push(block);
        return AppendUntilClosingDelimiter(processor, block, processor.Start + 2);
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block) =>
        block is MathBlock math
            ? AppendUntilClosingDelimiter(processor, math, processor.Line.Start)
            : BlockState.None;

    private static bool IsOpeningLine(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
        {
            return false;
        }

        var line = processor.Line;
        return line.Start + 1 <= line.End
            && line.Text[line.Start] == '\\'
            && line.Text[line.Start + 1] == '[';
    }

    private static BlockState AppendUntilClosingDelimiter(
        BlockProcessor processor,
        MathBlock block,
        int start)
    {
        var line = processor.Line;
        var closingStart = FindClosingDelimiter(line, start);
        if (closingStart >= 0)
        {
            AppendLine(block, processor, start, closingStart - 1);
            block.ClosingFencedCharCount = 2;
            block.Span = new SourceSpan(block.Span.Start, closingStart + 1);
            return BlockState.BreakDiscard;
        }

        AppendLine(block, processor, start, line.End);
        block.UpdateSpanEnd(line.End);
        return BlockState.ContinueDiscard;
    }

    private static int FindClosingDelimiter(StringSlice line, int start)
    {
        for (var index = start; index < line.End; index++)
        {
            if (line.Text[index] == '\\' && line.Text[index + 1] == ']')
            {
                return index;
            }
        }

        return -1;
    }

    private static void AppendLine(
        MathBlock block,
        BlockProcessor processor,
        int start,
        int end)
    {
        var line = processor.Line;
        if (start > end || IsWhitespace(line.Text, start, end))
        {
            return;
        }

        var slice = new StringSlice(line.Text, start, end);
        block.AppendLine(
            ref slice,
            processor.Column,
            processor.LineIndex,
            processor.CurrentLineStartPosition + start - line.Start,
            processor.TrackTrivia);
    }

    private static bool IsWhitespace(string text, int start, int end)
    {
        for (var index = start; index <= end; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                return false;
            }
        }

        return true;
    }
}
