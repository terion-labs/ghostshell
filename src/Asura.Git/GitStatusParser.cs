using System.Globalization;

namespace Asura.Git;

/// <summary>
/// Parses <c>git status --porcelain=v2 -z --branch</c>. The -z stream is a
/// sequence of NUL-terminated records; a rename record is followed by one
/// extra NUL-terminated token carrying the original path. This is the only
/// status format whose paths survive spaces, quotes, and non-ASCII intact.
/// </summary>
public static class GitStatusParser
{
    public sealed record Result(
        GitHeadState Head,
        IReadOnlyList<GitFileChange> UnstagedChanges,
        IReadOnlyList<GitFileChange> StagedChanges);

    public static Result Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string branchName = "";
        string? branchSha = null;
        string? upstream = null;
        int? ahead = null;
        int? behind = null;

        var unstaged = new List<GitFileChange>();
        var staged = new List<GitFileChange>();

        var records = output.Split('\0');
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length == 0)
            {
                continue;
            }

            if (record[0] == '#')
            {
                ParseHeader(record, ref branchName, ref branchSha, ref upstream, ref ahead, ref behind);
                continue;
            }

            switch (record[0])
            {
                case '1':
                    ParseOrdinaryEntry(record, unstaged, staged);
                    break;
                case '2':
                    // The record after the NUL is the rename source path.
                    var originalPath = index + 1 < records.Length ? records[++index] : "";
                    ParseRenameEntry(record, originalPath, unstaged, staged);
                    break;
                case 'u':
                    ParseConflictEntry(record, unstaged);
                    break;
                case '?':
                    unstaged.Add(new GitFileChange(
                        record[2..],
                        OriginalPath: null,
                        GitChangeKind.Untracked,
                        GitChangeArea.Unstaged));
                    break;
                case '!':
                    break;
                default:
                    throw new FormatException($"Unrecognized status record '{record[0]}'.");
            }
        }

        var isDetached = string.Equals(branchName, "(detached)", StringComparison.Ordinal);
        var displayName = branchName;
        if (isDetached)
        {
            displayName = branchSha is { Length: >= 8 } ? branchSha[..8] : branchSha ?? "";
        }

        var head = new GitHeadState(displayName, branchSha, upstream, ahead, behind, isDetached);
        return new Result(head, unstaged, staged);
    }

    private static void ParseHeader(
        string record,
        ref string branchName,
        ref string? branchSha,
        ref string? upstream,
        ref int? ahead,
        ref int? behind)
    {
        if (TryHeaderValue(record, "# branch.oid ", out var oid))
        {
            branchSha = string.Equals(oid, "(initial)", StringComparison.Ordinal) ? null : oid;
        }
        else if (TryHeaderValue(record, "# branch.head ", out var headValue))
        {
            branchName = headValue;
        }
        else if (TryHeaderValue(record, "# branch.upstream ", out var upstreamValue))
        {
            upstream = upstreamValue;
        }
        else if (TryHeaderValue(record, "# branch.ab ", out var abValue))
        {
            // Format: "+<ahead> -<behind>".
            var parts = abValue.Split(' ');
            if (parts.Length == 2
                && parts[0].Length > 1
                && parts[1].Length > 1
                && int.TryParse(parts[0][1..], NumberStyles.None, CultureInfo.InvariantCulture, out var aheadCount)
                && int.TryParse(parts[1][1..], NumberStyles.None, CultureInfo.InvariantCulture, out var behindCount))
            {
                ahead = aheadCount;
                behind = behindCount;
            }
        }
    }

    private static bool TryHeaderValue(string record, string prefix, out string value)
    {
        if (record.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = record[prefix.Length..];
            return true;
        }

        value = "";
        return false;
    }

    // "1 XY sub mH mI mW hH hI path" — a change without a rename.
    private static void ParseOrdinaryEntry(
        string record,
        List<GitFileChange> unstaged,
        List<GitFileChange> staged)
    {
        var fields = record.Split(' ', 9);
        if (fields.Length < 9)
        {
            throw new FormatException("Malformed ordinary status entry.");
        }

        AddEntry(fields[1], fields[2], fields[8], originalPath: null, unstaged, staged);
    }

    // "2 XY sub mH mI mW hH hI Xscore path" + separate original-path record.
    private static void ParseRenameEntry(
        string record,
        string originalPath,
        List<GitFileChange> unstaged,
        List<GitFileChange> staged)
    {
        var fields = record.Split(' ', 10);
        if (fields.Length < 10 || originalPath.Length == 0)
        {
            throw new FormatException("Malformed rename status entry.");
        }

        AddEntry(fields[1], fields[2], fields[9], originalPath, unstaged, staged);
    }

    // "u XY sub m1 m2 m3 mW h1 h2 h3 path" — an unmerged conflict.
    private static void ParseConflictEntry(string record, List<GitFileChange> unstaged)
    {
        var fields = record.Split(' ', 11);
        if (fields.Length < 11)
        {
            throw new FormatException("Malformed conflict status entry.");
        }

        unstaged.Add(new GitFileChange(
            fields[10],
            OriginalPath: null,
            GitChangeKind.Conflicted,
            GitChangeArea.Unstaged,
            IsSubmodule: fields[2][0] == 'S'));
    }

    private static void AddEntry(
        string xy,
        string submoduleState,
        string path,
        string? originalPath,
        List<GitFileChange> unstaged,
        List<GitFileChange> staged)
    {
        if (xy.Length != 2)
        {
            throw new FormatException("Malformed status XY field.");
        }

        var isSubmodule = submoduleState[0] == 'S';
        if (xy[0] != '.')
        {
            staged.Add(new GitFileChange(
                path,
                originalPath,
                KindOf(xy[0]),
                GitChangeArea.Staged,
                isSubmodule));
        }

        if (xy[1] != '.')
        {
            // The worktree side of a staged rename is a plain edit of the new path.
            unstaged.Add(new GitFileChange(
                path,
                OriginalPath: null,
                KindOf(xy[1]),
                GitChangeArea.Unstaged,
                isSubmodule));
        }
    }

    private static GitChangeKind KindOf(char state) => state switch
    {
        'M' or 'm' => GitChangeKind.Modified,
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        'U' => GitChangeKind.Conflicted,
        _ => throw new FormatException($"Unrecognized status state '{state}'."),
    };
}
