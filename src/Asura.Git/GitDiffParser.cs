using System.Globalization;

namespace Asura.Git;

/// <summary>
/// Parses one file's unified diff into a structured document with paired
/// line numbers. The parser reads hunk headers for positioning and treats
/// everything else literally, so it never re-interprets file content.
/// </summary>
public static class GitDiffParser
{
    public static GitDiffDocument Parse(
        string path,
        string? originalPath,
        string output,
        bool isTruncated)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(output);

        var hunks = new List<GitDiffHunk>();
        List<GitDiffLine>? lines = null;
        string? header = null;
        var oldLine = 0;
        var newLine = 0;
        var isBinary = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Length > 0 && raw[^1] == '\r' ? raw[..^1] : raw;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (header is not null && lines is not null)
                {
                    hunks.Add(new GitDiffHunk(header, lines));
                }

                header = line;
                lines = [];
                (oldLine, newLine) = ParseHunkHeader(line);
                continue;
            }

            if (lines is null)
            {
                // Before the first hunk only file headers appear; binary
                // content produces no hunks at all.
                if (line.StartsWith("Binary files ", StringComparison.Ordinal)
                    || string.Equals(line, "GIT binary patch", StringComparison.Ordinal))
                {
                    isBinary = true;
                }

                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            switch (line[0])
            {
                case '+':
                    lines.Add(new GitDiffLine(GitDiffLineKind.Added, line[1..], null, newLine));
                    newLine++;
                    break;
                case '-':
                    lines.Add(new GitDiffLine(GitDiffLineKind.Removed, line[1..], oldLine, null));
                    oldLine++;
                    break;
                case ' ':
                    lines.Add(new GitDiffLine(GitDiffLineKind.Context, line[1..], oldLine, newLine));
                    oldLine++;
                    newLine++;
                    break;
                case '\\':
                    // "\ No newline at end of file" annotates the previous line.
                    break;
                default:
                    // A stray header between hunks ends the diff body.
                    break;
            }
        }

        if (header is not null && lines is not null)
        {
            hunks.Add(new GitDiffHunk(header, lines));
        }

        return new GitDiffDocument(path, originalPath, isBinary, isTruncated, hunks);
    }

    private static (int OldStart, int NewStart) ParseHunkHeader(string header)
    {
        // "@@ -oldStart[,oldCount] +newStart[,newCount] @@ context".
        var parts = header.Split(' ');
        if (parts.Length < 3
            || parts[1].Length < 2
            || parts[2].Length < 2)
        {
            throw new FormatException("Malformed hunk header.");
        }

        return (ParseStart(parts[1][1..]), ParseStart(parts[2][1..]));
    }

    private static int ParseStart(string range)
    {
        var comma = range.IndexOf(',', StringComparison.Ordinal);
        var start = comma >= 0 ? range[..comma] : range;
        if (!int.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException("Malformed hunk range.");
        }

        return value;
    }
}
