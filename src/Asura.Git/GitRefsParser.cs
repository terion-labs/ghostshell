using System.Globalization;

namespace Asura.Git;

/// <summary>
/// Parses ref, remote, stash, and worktree listings. Every listing uses a
/// machine format: NUL field separators inside newline-terminated records
/// (ref names cannot contain NUL or newline), or Git's own porcelain output.
/// </summary>
public static class GitRefsParser
{
    /// <summary>
    /// Field order requested from for-each-ref:
    /// refname, refname:short, objectname, *objectname, upstream:short,
    /// upstream:track,nobracket, HEAD.
    /// </summary>
    public const string ForEachRefFormat =
        "%(refname)%00%(refname:short)%00%(objectname)%00%(*objectname)%00"
        + "%(upstream:short)%00%(upstream:track,nobracket)%00%(HEAD)";

    public static IReadOnlyList<GitRefItem> ParseRefs(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var refs = new List<GitRefItem>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\0');
            if (fields.Length < 7)
            {
                throw new FormatException("Malformed for-each-ref record.");
            }

            var fullName = fields[0];
            GitRefKind kind;
            if (fullName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                kind = GitRefKind.LocalBranch;
            }
            else if (fullName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                kind = GitRefKind.RemoteBranch;
            }
            else if (fullName.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                kind = GitRefKind.Tag;
            }
            else
            {
                continue;
            }

            // Annotated tags carry the peeled commit in *objectname.
            var target = fields[3].Length > 0 ? fields[3] : fields[2];
            var hasUpstream = fields[4].Length > 0;
            var (ahead, behind) = hasUpstream ? ParseTrack(fields[5]) : (null, null);
            refs.Add(new GitRefItem(
                fullName,
                fields[1],
                kind,
                target,
                IsCurrent: string.Equals(fields[6], "*", StringComparison.Ordinal),
                Upstream: fields[4].Length > 0 ? fields[4] : null,
                Ahead: ahead,
                Behind: behind));
        }

        return refs;
    }

    private static (int? Ahead, int? Behind) ParseTrack(string track)
    {
        if (string.Equals(track, "gone", StringComparison.Ordinal))
        {
            return (null, null);
        }

        int? ahead = null;
        int? behind = null;
        foreach (var part in track.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("ahead ", StringComparison.Ordinal)
                && int.TryParse(part[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var aheadCount))
            {
                ahead = aheadCount;
            }
            else if (part.StartsWith("behind ", StringComparison.Ordinal)
                && int.TryParse(part[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var behindCount))
            {
                behind = behindCount;
            }
        }

        return (ahead ?? 0, behind ?? 0);
    }

    /// <summary>Parses <c>git remote -v</c>, keeping one fetch URL per remote.</summary>
    public static IReadOnlyList<GitRemoteItem> ParseRemotes(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var remotes = new List<GitRemoteItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.EndsWith(" (fetch)", StringComparison.Ordinal))
            {
                continue;
            }

            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0)
            {
                continue;
            }

            var name = line[..tab];
            if (name.StartsWith('-'))
            {
                continue;
            }

            if (seen.Add(name))
            {
                remotes.Add(new GitRemoteItem(name, line[(tab + 1)..^" (fetch)".Length]));
            }
        }

        return remotes;
    }

    /// <summary>Parses <c>git stash list -z --format=%gd%x00%gs</c>.</summary>
    public static IReadOnlyList<GitStashItem> ParseStashes(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        // -z terminates records with NUL and %x00 separates the two fields,
        // so tokens strictly alternate: reference, subject, reference, …
        var stashes = new List<GitStashItem>();
        var tokens = output.Split('\0');
        for (var index = 0; index + 1 < tokens.Length; index += 2)
        {
            if (tokens[index].Length > 0)
            {
                stashes.Add(new GitStashItem(tokens[index], tokens[index + 1]));
            }
        }

        return stashes;
    }

    /// <summary>Parses <c>git worktree list --porcelain -z</c>.</summary>
    public static IReadOnlyList<GitWorktreeItem> ParseWorktrees(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var worktrees = new List<GitWorktreeItem>();
        string? path = null;
        string? branch = null;
        string? sha = null;

        foreach (var line in output.Split('\0'))
        {
            if (line.Length == 0)
            {
                if (path is not null)
                {
                    worktrees.Add(new GitWorktreeItem(path, branch, sha, worktrees.Count == 0));
                }

                path = null;
                branch = null;
                sha = null;
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                sha = line["HEAD ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = ShortBranch(line["branch ".Length..]);
            }
        }

        if (path is not null)
        {
            worktrees.Add(new GitWorktreeItem(path, branch, sha, worktrees.Count == 0));
        }

        return worktrees;
    }

    private static string ShortBranch(string refName) =>
        refName.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? refName["refs/heads/".Length..]
            : refName;

    /// <summary>Parses <c>git submodule status</c> lines.</summary>
    public static IReadOnlyList<GitSubmoduleItem> ParseSubmodules(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var submodules = new List<GitSubmoduleItem>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // " <sha> <path> (<describe>)" with a one-character state prefix:
            // '-' uninitialized, '+' out of sync, 'U' conflicted, ' ' clean.
            if (line.Length < 3)
            {
                continue;
            }

            var state = line[0] switch
            {
                '-' => "uninitialized",
                '+' => "modified",
                'U' => "conflicted",
                _ => "clean",
            };
            var rest = line[1..];
            var firstSpace = rest.IndexOf(' ', StringComparison.Ordinal);
            if (firstSpace <= 0)
            {
                continue;
            }

            var sha = rest[..firstSpace];
            var pathAndDescribe = rest[(firstSpace + 1)..];
            var describeStart = pathAndDescribe.LastIndexOf(" (", StringComparison.Ordinal);
            var submodulePath = describeStart > 0 && pathAndDescribe.EndsWith(')')
                ? pathAndDescribe[..describeStart]
                : pathAndDescribe;
            submodules.Add(new GitSubmoduleItem(submodulePath, sha, state));
        }

        return submodules;
    }
}
