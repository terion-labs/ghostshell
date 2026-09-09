using System.Collections.ObjectModel;

namespace Asura.Application;

public sealed record TerminalScreenFindResult
{
    public const int MaximumLineTextCharacters = 2_048;

    public TerminalScreenFindResult(
        long ContentRevision,
        IReadOnlyList<Match> Matches,
        bool IsTruncated)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ContentRevision);
        ArgumentNullException.ThrowIfNull(Matches);
        if (Matches.Count > TerminalScreenFindInput.MaximumMatches)
        {
            throw new ArgumentException(
                $"A rendered-screen find result cannot contain more than {TerminalScreenFindInput.MaximumMatches} matches.",
                nameof(Matches));
        }

        this.ContentRevision = ContentRevision;
        this.Matches = new ReadOnlyCollection<Match>([.. Matches]);
        this.IsTruncated = IsTruncated;
    }

    public long ContentRevision { get; }

    public IReadOnlyList<Match> Matches { get; }

    public bool IsTruncated { get; }

    public static TerminalScreenFindResult Search(
        TerminalScreenSnapshot snapshot,
        TerminalScreenFindInput input)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(input);
        var matches = new List<Match>(input.MaximumMatchCount);
        var text = snapshot.PlainText;
        var searchOffset = 0;
        var line = 0;
        var lineStart = 0;
        var truncated = false;
        while (searchOffset <= text.Length - input.Query.Length)
        {
            var offset = text.IndexOf(input.Query, searchOffset, StringComparison.Ordinal);
            if (offset < 0)
            {
                break;
            }

            while (lineStart < offset)
            {
                var newline = text.IndexOf('\n', lineStart);
                if (newline < 0 || newline >= offset)
                {
                    break;
                }

                line++;
                lineStart = newline + 1;
            }

            if (matches.Count == input.MaximumMatchCount)
            {
                truncated = true;
                break;
            }

            var lineEnd = text.IndexOf('\n', offset);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var lineLength = Math.Min(
                lineEnd - lineStart,
                MaximumLineTextCharacters);
            matches.Add(new Match(
                offset,
                line,
                offset - lineStart,
                text.Substring(lineStart, lineLength),
                lineEnd - lineStart > lineLength));
            searchOffset = offset + Math.Max(1, input.Query.Length);
        }

        return new TerminalScreenFindResult(
            snapshot.ContentRevision,
            matches,
            truncated || snapshot.IsTruncated);
    }

    public sealed record Match
    {
        public Match(
            int Offset,
            int Line,
            int Column,
            string LineText,
            bool IsLineTruncated)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(Offset);
            ArgumentOutOfRangeException.ThrowIfNegative(Line);
            ArgumentOutOfRangeException.ThrowIfNegative(Column);
            ArgumentNullException.ThrowIfNull(LineText);
            if (LineText.Length > MaximumLineTextCharacters)
            {
                throw new ArgumentException(
                    $"A rendered-screen match line cannot exceed {MaximumLineTextCharacters} characters.",
                    nameof(LineText));
            }

            this.Offset = Offset;
            this.Line = Line;
            this.Column = Column;
            this.LineText = LineText;
            this.IsLineTruncated = IsLineTruncated;
        }

        public int Offset { get; }

        public int Line { get; }

        public int Column { get; }

        public string LineText { get; }

        public bool IsLineTruncated { get; }
    }
}
