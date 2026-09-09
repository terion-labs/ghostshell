using System.Globalization;

namespace Asura.Git;

/// <summary>
/// Parses commit listings and commit details. Records are newline-terminated
/// with NUL field separators: no field before the body can contain either
/// character, and the body is always the final field of a single record.
/// </summary>
public static class GitLogParser
{
    /// <summary>sha, short sha, parents, author, email, author time, refs, subject.</summary>
    public const string CommitPageFormat = "%H%x00%h%x00%P%x00%an%x00%ae%x00%at%x00%D%x00%s";

    /// <summary>The page format plus committer name, commit time, and body.</summary>
    public const string CommitDetailFormat = CommitPageFormat + "%x00%cn%x00%ct%x00%b";

    public static IReadOnlyList<GitCommitItem> ParseCommits(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var commits = new List<GitCommitItem>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            commits.Add(ParseCommitRecord(line));
        }

        return commits;
    }

    public static GitCommitDetail ParseCommitDetail(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var record = output.TrimEnd('\n');
        var fields = record.Split('\0', 11);
        if (fields.Length < 11)
        {
            throw new FormatException("Malformed commit detail record.");
        }

        return new GitCommitDetail(
            ParseCommitFields(fields),
            Body: fields[10].TrimEnd('\n'),
            CommitterName: fields[8],
            CommittedAt: ParseUnixTime(fields[9]),
            Changes: []);
    }

    private static GitCommitItem ParseCommitRecord(string record)
    {
        var fields = record.Split('\0');
        if (fields.Length < 8)
        {
            throw new FormatException("Malformed commit record.");
        }

        return ParseCommitFields(fields);
    }

    private static GitCommitItem ParseCommitFields(string[] fields) => new(
        Sha: fields[0],
        ShortSha: fields[1],
        ParentShas: fields[2].Length == 0 ? [] : fields[2].Split(' '),
        AuthorName: fields[3],
        AuthorEmail: fields[4],
        AuthoredAt: ParseUnixTime(fields[5]),
        Subject: fields[7],
        RefNames: fields[6].Length == 0 ? [] : ParseDecorations(fields[6]));

    private static string[] ParseDecorations(string decorations)
    {
        var names = decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < names.Length; index++)
        {
            // "HEAD -> main" decorates the checked-out branch.
            var arrow = names[index].IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                names[index] = names[index][(arrow + " -> ".Length)..];
            }
        }

        return names;
    }

    private static DateTimeOffset ParseUnixTime(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new FormatException("Malformed commit timestamp.");
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    /// <summary>
    /// Parses <c>git diff-tree -r -z --name-status</c>: a token stream of
    /// status, path, and — for renames and copies — a second path.
    /// </summary>
    public static IReadOnlyList<GitFileChange> ParseNameStatus(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var changes = new List<GitFileChange>();
        var tokens = output.Split('\0');
        var index = 0;
        while (index < tokens.Length && tokens[index].Length > 0)
        {
            var status = tokens[index++];
            var kind = status[0] switch
            {
                'M' => GitChangeKind.Modified,
                'A' => GitChangeKind.Added,
                'D' => GitChangeKind.Deleted,
                'R' => GitChangeKind.Renamed,
                'C' => GitChangeKind.Copied,
                'T' => GitChangeKind.TypeChanged,
                'U' => GitChangeKind.Conflicted,
                _ => throw new FormatException($"Unrecognized name-status '{status}'."),
            };

            if (index >= tokens.Length)
            {
                throw new FormatException("Name-status entry is missing its path.");
            }

            var path = tokens[index++];
            string? originalPath = null;
            if (kind is GitChangeKind.Renamed or GitChangeKind.Copied)
            {
                if (index >= tokens.Length)
                {
                    throw new FormatException("Rename entry is missing its destination path.");
                }

                // Rename tokens arrive source first, destination second.
                originalPath = path;
                path = tokens[index++];
            }

            changes.Add(new GitFileChange(path, originalPath, kind, GitChangeArea.Staged));
        }

        return changes;
    }
}
