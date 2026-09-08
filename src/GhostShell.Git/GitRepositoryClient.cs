using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Git;

/// <summary>
/// Reads and mutates Git repositories through the target's own Git CLI via
/// Ghostshell's connection command boundary. Local and SSH repositories share
/// one code path, and the user's configuration, hooks, credential helpers,
/// and filters all apply because the target's Git does the work.
/// </summary>
public sealed partial class GitRepositoryClient(
    IConnectionCommandExecutor executor,
    TimeProvider timeProvider,
    IWorkspaceNetworkConnector? networkConnector = null)
    : IGitRepositoryClient
{
    private const string GitExecutable = "git";
    private const int ReadOutputLimit = 16 * 1024 * 1024;
    private const int DiffOutputLimit = 4 * 1024 * 1024;
    private const int BlobOutputLimit = 1024 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DiffTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromSeconds(30);

    // Hooks run inside commit, so it gets the most patient limit.
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromMinutes(1);

    // Fetch and push talk to another machine; the network gets its own budget.
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(2);

    // The one status invocation both the full snapshot and the scoped
    // working-set read speak, so the two can never drift apart.
    private static readonly string[] StatusArguments =
    [
        // Passive inspection must not execute a repository-supplied fsmonitor.
        // Human mutation commands retain their normal hooks and configuration.
        "-c", "core.fsmonitor=false",
        "--no-optional-locks", "status", "--porcelain=v2", "-z",
        "--branch", "--untracked-files=all",
    ];

    public async ValueTask<GitResult<GitRepositoryHandle>> OpenRepositoryAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Supports(connection))
        {
            return Failure<GitRepositoryHandle>(
                GitErrorCode.Unsupported,
                "Git panels support local and SSH connections.");
        }

        var result = await ExecuteAsync(
            connection,
            ["-C", path, "rev-parse", "--show-toplevel"],
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).ConfigureAwait(false);
        if (result is GitResult<CommandOutput>.Failure failure)
        {
            // An ownership refusal on a root connection first tries the
            // transparent path: run Git as the repository's owner. Only when
            // that cannot be established does the refusal surface, so the
            // trust-exception flow stays the fallback.
            if (failure.Error.Code == GitErrorCode.OwnershipUntrusted
                && await TryOpenAsOwnerAsync(connection, path, cancellationToken)
                    .ConfigureAwait(false) is { } impersonated)
            {
                return new GitResult<GitRepositoryHandle>.Success(impersonated);
            }

            return new GitResult<GitRepositoryHandle>.Failure(failure.Error);
        }

        return new GitResult<GitRepositoryHandle>.Success(
            new GitRepositoryHandle(connection, Value(result).Text.TrimEnd('\n', '\r')));
    }

    /// <summary>
    /// The transparent way around an ownership refusal: when the connection
    /// signs in as root, resolve the repository's owner and re-open through
    /// sudo as that user. Null when impersonation cannot be established —
    /// not root, no resolvable non-root owner, or sudo refused — in which
    /// case the caller surfaces the original refusal.
    /// </summary>
    private async ValueTask<GitRepositoryHandle?> TryOpenAsOwnerAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken)
    {
        var user = await ReadPlainAsync(connection, "id", ["-un"], cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(user, "root", StringComparison.Ordinal))
        {
            return null;
        }

        // GNU stat first, then the BSD/macOS spelling of the same question.
        var owner = await ReadPlainAsync(connection, "stat", ["-c", "%U", path], cancellationToken)
                .ConfigureAwait(false)
            ?? await ReadPlainAsync(connection, "stat", ["-f", "%Su", path], cancellationToken)
                .ConfigureAwait(false);
        if (string.IsNullOrEmpty(owner) || string.Equals(owner, "root", StringComparison.Ordinal))
        {
            return null;
        }

        var probe = new GitRepositoryHandle(connection, path, owner);
        var result = await ExecuteAsync(
            probe,
            ["rev-parse", "--show-toplevel"],
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).ConfigureAwait(false);
        return result is GitResult<CommandOutput>.Success success
            ? new GitRepositoryHandle(connection, success.Value.Text.TrimEnd('\n', '\r'), owner)
            : null;
    }

    /// <summary>
    /// One non-Git probe command; its trimmed output on a clean exit, null on
    /// any refusal. Probes inform a decision, so they never surface errors.
    /// </summary>
    private async ValueTask<string?> ReadPlainAsync(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new ConnectionCommand(connection, executable, arguments, ReadTimeout, ReadOutputLimit),
            cancellationToken).ConfigureAwait(false);
        return result is { Outcome: ConnectionCommandOutcome.Exited, ExitCode: 0 }
            ? result.StandardOutput.Trim()
            : null;
    }

    public async ValueTask<GitResult<GitUnit>> TrustRepositoryAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Supports(connection))
        {
            return Failure<GitUnit>(
                GitErrorCode.Unsupported,
                "Git panels support local and SSH connections.");
        }

        // Exactly the remedy Git's own refusal recommends, run for the
        // signed-in user on the target. No -C prefix: there is no openable
        // repository yet, and the global config needs none.
        var result = await ExecuteAsync(
            connection,
            ["config", "--global", "--add", "safe.directory", path.Trim()],
            MutationTimeout,
            ReadOutputLimit,
            cancellationToken).ConfigureAwait(false);
        return result switch
        {
            GitResult<CommandOutput>.Failure failure => new GitResult<GitUnit>.Failure(failure.Error),
            _ => new GitResult<GitUnit>.Success(GitUnit.Value),
        };
    }

    /// <summary>
    /// The directory probe spoken over the connection's shell, in the style of
    /// the Docker file protocol: the path travels as argv, records are
    /// NUL-delimited so legal Unix names cannot corrupt boundaries, and a
    /// repository is any directory carrying .git (a directory in a normal
    /// clone, a file in a linked worktree).
    /// </summary>
    private const string ListDirectoriesScript = """
        if [ -z "$1" ]; then cd || exit 9; else cd -- "$1" || exit 9; fi
        printf '%s\0' "$(pwd)"
        for entry in ./* ./.[!.]* ./..?*; do
          [ -d "$entry" ] || continue
          name=${entry#./}
          if [ -e "$entry/.git" ]; then kind=r; else kind=d; fi
          printf '%s\0%s\0' "$name" "$kind"
        done
        """;

    public async ValueTask<GitResult<GitDirectoryListing>> ListDirectoriesAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(path);
        if (!Supports(connection))
        {
            return Failure<GitDirectoryListing>(
                GitErrorCode.Unsupported,
                "Git panels support local and SSH connections.");
        }

        var command = new ConnectionCommand(
            connection,
            "/bin/sh",
            ["-c", ListDirectoriesScript, "ghostshell-git-ls", path.Trim()],
            ReadTimeout,
            ReadOutputLimit);
        var result = await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ConnectionCommandOutcome.Exited && result.ExitCode == 9)
        {
            return Failure<GitDirectoryListing>(
                GitErrorCode.CommandFailed,
                "That directory cannot be opened on this connection.");
        }

        if (result.Outcome != ConnectionCommandOutcome.Exited || result.ExitCode != 0)
        {
            return new GitResult<GitDirectoryListing>.Failure(NonSuccessError(result));
        }

        var tokens = result.StandardOutput.Split('\0');
        if (tokens.Length == 0 || tokens[0].Length == 0)
        {
            return Failure<GitDirectoryListing>(
                GitErrorCode.InvalidResponse,
                "The directory probe returned an invalid response.");
        }

        var directories = new List<GitDirectoryEntry>();
        for (var index = 1; index + 1 < tokens.Length; index += 2)
        {
            if (tokens[index].Length > 0)
            {
                directories.Add(new GitDirectoryEntry(
                    tokens[index],
                    string.Equals(tokens[index + 1], "r", StringComparison.Ordinal)));
            }
        }

        return new GitResult<GitDirectoryListing>.Success(new GitDirectoryListing(
            tokens[0],
            [.. directories.OrderByDescending(entry => entry.IsRepository)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)]));
    }

    public async ValueTask<GitResult<GitRepositorySnapshot>> ReadSnapshotAsync(
        GitRepositoryHandle repository,
        long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var statusTask = ExecuteAsync(
            repository,
            StatusArguments,
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).AsTask();
        var refsTask = ExecuteAsync(
            repository,
            [
                "-c", "core.fsmonitor=false", "for-each-ref", $"--format={GitRefsParser.ForEachRefFormat}",
                "refs/heads", "refs/remotes", "refs/tags",
            ],
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).AsTask();
        var remotesTask = ExecuteAsync(
            repository,
            ["-c", "core.fsmonitor=false", "remote", "-v"],
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).AsTask();
        var stashesTask = ExecuteAsync(
            repository,
            ["-c", "core.fsmonitor=false", "stash", "list", "-z", "--format=%gd%x00%gs"],
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).AsTask();
        var worktreesTask = ExecuteAsync(
            repository,
            ["-c", "core.fsmonitor=false", "worktree", "list", "--porcelain", "-z"],
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).AsTask();
        var submodulesTask = ExecuteAsync(
            repository,
            ["-c", "core.fsmonitor=false", "submodule", "status"],
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).AsTask();

        await Task.WhenAll(
            statusTask,
            refsTask,
            remotesTask,
            stashesTask,
            worktreesTask,
            submodulesTask).ConfigureAwait(false);
        var status = await statusTask.ConfigureAwait(false);
        var refs = await refsTask.ConfigureAwait(false);
        var remotes = await remotesTask.ConfigureAwait(false);
        var stashes = await stashesTask.ConfigureAwait(false);
        var worktrees = await worktreesTask.ConfigureAwait(false);
        var submodules = await submodulesTask.ConfigureAwait(false);

        var required = new[] { status, refs, remotes, stashes, worktrees };
        if (required.OfType<GitResult<CommandOutput>.Failure>().FirstOrDefault() is { } failure)
        {
            return new GitResult<GitRepositorySnapshot>.Failure(failure.Error);
        }

        try
        {
            var parsedStatus = GitStatusParser.Parse(Value(status).Text);
            var snapshot = new GitRepositorySnapshot(
                generation,
                parsedStatus.Head,
                parsedStatus.UnstagedChanges,
                parsedStatus.StagedChanges,
                GitRefsParser.ParseRefs(Value(refs).Text),
                GitRefsParser.ParseRemotes(Value(remotes).Text),
                GitRefsParser.ParseStashes(Value(stashes).Text),
                GitRefsParser.ParseWorktrees(Value(worktrees).Text),
                // Submodule listing shells into each submodule, so it can fail
                // (or crawl) independently of the repository proper; a missing
                // listing must not take the whole snapshot down with it.
                submodules is GitResult<CommandOutput>.Success submodulesSuccess
                    ? GitRefsParser.ParseSubmodules(submodulesSuccess.Value.Text)
                    : [],
                timeProvider.GetUtcNow());
            return new GitResult<GitRepositorySnapshot>.Success(snapshot);
        }
        catch (FormatException exception)
        {
            return Failure<GitRepositorySnapshot>(GitErrorCode.InvalidResponse, exception.Message);
        }
    }

    public async ValueTask<GitResult<GitWorkingSet>> ReadWorkingSetAsync(
        GitRepositoryHandle repository,
        long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var result = await ExecuteAsync(
            repository,
            StatusArguments,
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).ConfigureAwait(false);
        if (result is GitResult<CommandOutput>.Failure failure)
        {
            return new GitResult<GitWorkingSet>.Failure(failure.Error);
        }

        try
        {
            var parsed = GitStatusParser.Parse(Value(result).Text);
            return new GitResult<GitWorkingSet>.Success(new GitWorkingSet(
                generation,
                parsed.Head,
                parsed.UnstagedChanges,
                parsed.StagedChanges));
        }
        catch (FormatException exception)
        {
            return Failure<GitWorkingSet>(GitErrorCode.InvalidResponse, exception.Message);
        }
    }

    public async ValueTask<GitResult<GitCommitPage>> ReadCommitPageAsync(
        GitRepositoryHandle repository,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        // One extra record answers "is there another page" without a second
        // history walk.
        var result = await ExecuteAsync(
            repository,
            [
                "log",
                $"--format={GitLogParser.CommitPageFormat}",
                $"--skip={offset}",
                $"--max-count={count + 1}",
            ],
            DiffTimeout,
            ReadOutputLimit,
            cancellationToken).ConfigureAwait(false);
        if (result is GitResult<CommandOutput>.Failure failure)
        {
            return IsUnbornHistoryError(failure.Error)
                ? new GitResult<GitCommitPage>.Success(new GitCommitPage([], offset, HasMore: false))
                : new GitResult<GitCommitPage>.Failure(failure.Error);
        }

        try
        {
            var commits = GitLogParser.ParseCommits(Value(result).Text);
            var hasMore = commits.Count > count;
            return new GitResult<GitCommitPage>.Success(new GitCommitPage(
                hasMore ? [.. commits.Take(count)] : commits,
                offset,
                hasMore));
        }
        catch (FormatException exception)
        {
            return Failure<GitCommitPage>(GitErrorCode.InvalidResponse, exception.Message);
        }
    }

    public async ValueTask<GitResult<GitCommitDetail>> ReadCommitDetailAsync(
        GitRepositoryHandle repository,
        string sha,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        var detailResult = await ExecuteAsync(
            repository,
            ["show", "--no-patch", $"--format={GitLogParser.CommitDetailFormat}", sha],
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).ConfigureAwait(false);
        if (detailResult is GitResult<CommandOutput>.Failure detailFailure)
        {
            return new GitResult<GitCommitDetail>.Failure(detailFailure.Error);
        }

        GitCommitDetail detail;
        try
        {
            detail = GitLogParser.ParseCommitDetail(Value(detailResult).Text);
        }
        catch (FormatException exception)
        {
            return Failure<GitCommitDetail>(GitErrorCode.InvalidResponse, exception.Message);
        }

        // Diffing against the first parent covers merges as well; only a root
        // commit has no parent tree to compare with.
        var changesResult = await ExecuteAsync(
            repository,
            detail.Commit.ParentShas.Count == 0
                ? ["diff-tree", "--no-commit-id", "-r", "-z", "-M", "--name-status", "--root", sha]
                : ["diff", "--name-status", "-z", "-M", $"{sha}^1", sha],
            DiffTimeout,
            ReadOutputLimit,
            cancellationToken).ConfigureAwait(false);
        if (changesResult is GitResult<CommandOutput>.Failure changesFailure)
        {
            return new GitResult<GitCommitDetail>.Failure(changesFailure.Error);
        }

        try
        {
            return new GitResult<GitCommitDetail>.Success(
                detail with { Changes = GitLogParser.ParseNameStatus(Value(changesResult).Text) });
        }
        catch (FormatException exception)
        {
            return Failure<GitCommitDetail>(GitErrorCode.InvalidResponse, exception.Message);
        }
    }

    public async ValueTask<GitResult<GitDiffDocument>> ReadDiffAsync(
        GitRepositoryHandle repository,
        GitDiffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(request);

        var result = await ExecuteAsync(
            repository,
            ["-c", "core.fsmonitor=false", .. DiffArguments(request)],
            DiffTimeout,
            DiffOutputLimit,
            cancellationToken,
            // Plain "git diff" exits 1 when the files differ; that is the
            // expected answer here, not a failure. An oversized diff is shown
            // cut short rather than refused.
            acceptExitOne: true,
            allowTruncated: true).ConfigureAwait(false);
        if (result is GitResult<CommandOutput>.Failure failure)
        {
            return new GitResult<GitDiffDocument>.Failure(failure.Error);
        }

        var output = Value(result);
        try
        {
            return new GitResult<GitDiffDocument>.Success(GitDiffParser.Parse(
                request.Path,
                request.OriginalPath,
                output.Text,
                output.Truncated));
        }
        catch (FormatException exception)
        {
            return Failure<GitDiffDocument>(GitErrorCode.InvalidResponse, exception.Message);
        }
    }

    public async ValueTask<GitResult<GitTreeListing>> ReadTreeAsync(
        GitRepositoryHandle repository,
        string sha,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentNullException.ThrowIfNull(path);

        // "<sha>:<path>" names the subtree directly, so entry names come back
        // relative and no client-side path filtering is needed.
        var treeish = path.Length == 0 ? sha : $"{sha}:{path}";
        var result = await ExecuteAsync(
            repository,
            ["ls-tree", "-z", "-l", treeish],
            ReadTimeout,
            ReadOutputLimit,
            cancellationToken).ConfigureAwait(false);
        if (result is GitResult<CommandOutput>.Failure failure)
        {
            return new GitResult<GitTreeListing>.Failure(failure.Error);
        }

        try
        {
            return new GitResult<GitTreeListing>.Success(new GitTreeListing(
                path,
                GitTreeParser.Parse(Value(result).Text)));
        }
        catch (FormatException exception)
        {
            return Failure<GitTreeListing>(GitErrorCode.InvalidResponse, exception.Message);
        }
    }

    public async ValueTask<GitResult<GitBlobSnapshot>> ReadBlobAsync(
        GitRepositoryHandle repository,
        string sha,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // cat-file reads the recorded blob raw: no smudge filters, no textconv,
        // so the preview shows what the commit actually stores.
        var result = await ExecuteAsync(
            repository,
            ["cat-file", "blob", $"{sha}:{path}"],
            DiffTimeout,
            BlobOutputLimit,
            cancellationToken,
            allowTruncated: true).ConfigureAwait(false);
        if (result is GitResult<CommandOutput>.Failure failure)
        {
            return new GitResult<GitBlobSnapshot>.Failure(failure.Error);
        }

        var output = Value(result);
        var isBinary = output.Text.Contains('\0', StringComparison.Ordinal);
        return new GitResult<GitBlobSnapshot>.Success(new GitBlobSnapshot(
            path,
            isBinary ? "" : output.Text,
            isBinary,
            output.Truncated));
    }

    public ValueTask<GitResult<GitUnit>> StageAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) =>
        MutateAsync(repository, ["add", "--", .. RequirePaths(paths)], cancellationToken);

    public async ValueTask<GitResult<GitUnit>> UnstageAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var result = await MutateAsync(
            repository,
            ["restore", "--staged", "--", .. RequirePaths(paths)],
            cancellationToken).ConfigureAwait(false);

        // Before the first commit there is no HEAD for restore to read from;
        // dropping the paths from the index is the same gesture there.
        if (result is GitResult<GitUnit>.Failure { Error.Code: GitErrorCode.CommandFailed } failure
            && failure.Error.Message.Contains("HEAD", StringComparison.Ordinal))
        {
            return await MutateAsync(
                repository,
                ["rm", "--cached", "-r", "-q", "--", .. paths],
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask<GitResult<GitUnit>> DiscardAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<GitFileChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var untracked = changes
            .Where(change => change.Kind == GitChangeKind.Untracked)
            .Select(change => change.Path)
            .ToArray();
        var tracked = changes
            .Where(change => change.Kind != GitChangeKind.Untracked)
            .Select(change => change.Path)
            .ToArray();
        if (untracked.Length == 0 && tracked.Length == 0)
        {
            throw new ArgumentException("Discard requires at least one change.", nameof(changes));
        }

        if (tracked.Length > 0)
        {
            var result = await MutateAsync(
                repository,
                ["restore", "--worktree", "--", .. tracked],
                cancellationToken).ConfigureAwait(false);
            if (result is GitResult<GitUnit>.Failure)
            {
                return result;
            }
        }

        if (untracked.Length > 0)
        {
            return await MutateAsync(
                repository,
                ["clean", "-fd", "--", .. untracked],
                cancellationToken).ConfigureAwait(false);
        }

        return new GitResult<GitUnit>.Success(GitUnit.Value);
    }

    public ValueTask<GitResult<GitUnit>> CommitAsync(
        GitRepositoryHandle repository,
        GitCommitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Subject);

        var arguments = new List<string> { "commit", "-m", request.Subject };
        if (request.Body.Length > 0)
        {
            arguments.Add("-m");
            arguments.Add(request.Body);
        }

        if (request.Amend)
        {
            arguments.Add("--amend");
        }

        return MutateAsync(repository, arguments, cancellationToken, CommitTimeout);
    }

    private static IReadOnlyList<string> DiffArguments(GitDiffRequest request)
    {
        // "-w" rides immediately after the subcommand in every variant, so
        // whitespace-only changes disappear from the comparison uniformly.
        IReadOnlyList<string> whitespace = request.IgnoreWhitespace ? ["-w"] : [];
        return request.Area switch
        {
            GitDiffArea.Worktree when request.IsUntracked =>
                ["diff", .. whitespace, "--no-color", "--no-index", "--", "/dev/null", request.Path],
            GitDiffArea.Worktree =>
                ["diff", .. whitespace, "--no-color", "--", request.Path],
            GitDiffArea.Index when request.OriginalPath is { } original =>
                ["diff", .. whitespace, "--no-color", "--cached", "-M", "--", original, request.Path],
            GitDiffArea.Index =>
                ["diff", .. whitespace, "--no-color", "--cached", "-M", "--", request.Path],
            GitDiffArea.Commit when request.CommitSha is { } sha =>
                CommitDiffArguments(request, sha, whitespace),
            _ => throw new ArgumentException("The diff request is incomplete.", nameof(request)),
        };
    }

    private static IReadOnlyList<string> CommitDiffArguments(
        GitDiffRequest request,
        string sha,
        IReadOnlyList<string> whitespace)
    {
        var arguments = new List<string> { "diff-tree" };
        arguments.AddRange(whitespace);
        arguments.AddRange(
        [
            "-p", "--no-color", "-r", "-M", "--root", "--no-commit-id", sha, "--",
            request.Path,
        ]);
        if (request.OriginalPath is { } original)
        {
            arguments.Add(original);
        }

        return arguments;
    }

    public ValueTask<GitResult<GitUnit>> CheckoutBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return MutateAsync(repository, ["switch", name], cancellationToken);
    }

    public ValueTask<GitResult<GitUnit>> CreateBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return MutateAsync(repository, ["switch", "-c", name], cancellationToken);
    }

    public ValueTask<GitResult<GitUnit>> RenameBranchAsync(
        GitRepositoryHandle repository,
        string oldName,
        string newName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        return MutateAsync(repository, ["branch", "-m", oldName, newName], cancellationToken);
    }

    public ValueTask<GitResult<GitUnit>> DeleteBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return MutateAsync(repository, ["branch", "-D", name], cancellationToken);
    }

    public ValueTask<GitResult<GitUnit>> MergeBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // --no-edit keeps the target's editor out of a panel gesture; hooks
        // still run, so the merge shares commit's patient limit.
        return MutateAsync(repository, ["merge", "--no-edit", name], cancellationToken, CommitTimeout);
    }

    public ValueTask<GitResult<GitUnit>> WorktreeAddAsync(
        GitRepositoryHandle repository,
        string path,
        string branch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        return MutateAsync(repository, ["worktree", "add", path, branch], cancellationToken);
    }

    public ValueTask<GitResult<GitUnit>> FastForwardAsync(
        GitRepositoryHandle repository,
        string branch,
        string upstream,
        bool isCurrent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstream);

        // A non-current branch cannot be merged, but fetching from the
        // repository itself moves its ref: the fully qualified refspec
        // "refs/remotes/<upstream>:refs/heads/<branch>" names both sides
        // unambiguously, and a plain (non-forced) fetch refspec refuses any
        // move that is not a fast-forward. Both the qualified and the short
        // form were verified against real Git in a scratch repository; the
        // qualified one is used because it cannot collide with a tag or a
        // like-named local ref.
        return MutateAsync(
            repository,
            isCurrent
                ? ["merge", "--ff-only", upstream]
                : ["fetch", ".", $"refs/remotes/{upstream}:refs/heads/{branch}"],
            cancellationToken);
    }

    public async ValueTask<GitResult<GitUnit>> PushBranchAsync(
        GitRepositoryHandle repository,
        string remote,
        string branch,
        CancellationToken cancellationToken)
    {
        ValidateRemoteOperand(remote, nameof(remote));
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        return AsRetryable(await MutateAsync(
            repository,
            ["push", remote, branch],
            cancellationToken,
            NetworkTimeout).ConfigureAwait(false));
    }

    public async ValueTask<GitResult<GitUnit>> RebaseAsync(
        GitRepositoryHandle repository,
        string onto,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onto);
        var result = await MutateAsync(repository, ["rebase", onto], cancellationToken, CommitTimeout)
            .ConfigureAwait(false);
        if (result is not GitResult<GitUnit>.Failure { Error.Code: GitErrorCode.CommandFailed } failure)
        {
            return result;
        }

        // A conflicted rebase would strand the worktree mid-rebase behind a
        // panel gesture; abort it and surface the original refusal.
        await MutateAsync(repository, ["rebase", "--abort"], cancellationToken).ConfigureAwait(false);
        return new GitResult<GitUnit>.Failure(failure.Error with
        {
            Message = $"{failure.Error.Message} (rebase aborted)",
        });
    }

    public ValueTask<GitResult<GitUnit>> CreateTagAsync(
        GitRepositoryHandle repository,
        string name,
        string? message,
        string? revision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        IReadOnlyList<string> arguments = string.IsNullOrEmpty(message)
            ? ["tag", name]
            : ["tag", "-a", name, "-m", message];
        return MutateAsync(
            repository,
            revision is null ? arguments : [.. arguments, revision],
            cancellationToken);
    }

    public async ValueTask<GitResult<GitUnit>> DeleteTagAsync(
        GitRepositoryHandle repository,
        string name,
        IReadOnlyList<string> alsoOnRemotes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(alsoOnRemotes);
        foreach (var remote in alsoOnRemotes)
        {
            ValidateRemoteOperand(remote, nameof(alsoOnRemotes));
        }

        var result = await MutateAsync(repository, ["tag", "-d", name], cancellationToken)
            .ConfigureAwait(false);
        if (result is GitResult<GitUnit>.Failure)
        {
            return result;
        }

        foreach (var remote in alsoOnRemotes)
        {
            // The fully qualified ref cannot be mistaken for a branch of the
            // same name on the remote side.
            result = await MutateAsync(
                repository,
                ["push", remote, "--delete", $"refs/tags/{name}"],
                cancellationToken,
                NetworkTimeout).ConfigureAwait(false);
            if (result is GitResult<GitUnit>.Failure)
            {
                return result;
            }
        }

        return result;
    }

    public ValueTask<GitResult<GitUnit>> AddRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        string url,
        CancellationToken cancellationToken)
    {
        ValidateRemoteOperand(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return MutateAsync(repository, ["remote", "add", name, url], cancellationToken);
    }

    public async ValueTask<GitResult<GitUnit>> EditRemoteAsync(
        GitRepositoryHandle repository,
        string oldName,
        string newName,
        string url,
        CancellationToken cancellationToken)
    {
        ValidateRemoteOperand(oldName, nameof(oldName));
        ValidateRemoteOperand(newName, nameof(newName));
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            var renamed = await MutateAsync(
                repository,
                ["remote", "rename", oldName, newName],
                cancellationToken).ConfigureAwait(false);
            if (renamed is GitResult<GitUnit>.Failure)
            {
                return renamed;
            }
        }

        return await MutateAsync(
            repository,
            ["remote", "set-url", newName, url],
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<GitResult<GitUnit>> RemoveRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken)
    {
        ValidateRemoteOperand(name, nameof(name));
        return MutateAsync(repository, ["remote", "remove", name], cancellationToken);
    }

    public async ValueTask<GitResult<GitUnit>> FetchRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken)
    {
        ValidateRemoteOperand(name, nameof(name));
        return AsRetryable(await MutateAsync(
            repository,
            ["fetch", name],
            cancellationToken,
            NetworkTimeout).ConfigureAwait(false));
    }

    private static void ValidateRemoteOperand(string remote, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote, parameterName);
        if (remote.StartsWith('-'))
        {
            throw new ArgumentException(
                "A Git remote cannot begin with an option prefix.",
                parameterName);
        }
    }

    public async ValueTask<GitResult<GitUnit>> PullAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken) =>
        AsRetryable(await MutateAsync(repository, ["pull"], cancellationToken, NetworkTimeout)
            .ConfigureAwait(false));

    public ValueTask<GitResult<GitUnit>> PushAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken) =>
        MutateAsync(repository, ["push"], cancellationToken, NetworkTimeout);

    public ValueTask<GitResult<GitUnit>> StashPushAsync(
        GitRepositoryHandle repository,
        string? message,
        CancellationToken cancellationToken) =>
        MutateAsync(
            repository,
            string.IsNullOrEmpty(message)
                ? ["stash", "push"]
                : ["stash", "push", "-m", message],
            cancellationToken);

    public ValueTask<GitResult<GitUnit>> StashApplyAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return MutateAsync(repository, ["stash", "apply", reference], cancellationToken);
    }

    public ValueTask<GitResult<GitUnit>> StashPopAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return MutateAsync(repository, ["stash", "pop", reference], cancellationToken);
    }

    public ValueTask<GitResult<GitUnit>> StashDropAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return MutateAsync(repository, ["stash", "drop", reference], cancellationToken);
    }

    // A failed network conversation is transient, not a broken repository:
    // the same gesture is worth pressing again.
    private static GitResult<GitUnit> AsRetryable(GitResult<GitUnit> result) =>
        result is GitResult<GitUnit>.Failure { Error: { Code: GitErrorCode.CommandFailed } error }
            ? new GitResult<GitUnit>.Failure(error with { Retryable = true })
            : result;

    private async ValueTask<GitResult<GitUnit>> MutateAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var result = await ExecuteAsync(
            repository,
            arguments,
            timeout ?? MutationTimeout,
            ReadOutputLimit,
            cancellationToken).ConfigureAwait(false);
        return result switch
        {
            GitResult<CommandOutput>.Failure failure => new GitResult<GitUnit>.Failure(failure.Error),
            _ => new GitResult<GitUnit>.Success(GitUnit.Value),
        };
    }

    private static IReadOnlyList<string> RequirePaths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("The operation requires at least one path.", nameof(paths));
        }

        return paths;
    }

    private sealed record CommandOutput(string Text, bool Truncated);

    private ValueTask<GitResult<CommandOutput>> ExecuteAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken cancellationToken,
        bool acceptExitOne = false,
        bool allowTruncated = false) =>
        repository.RunAsUser is { } owner
            // Non-interactive sudo keeps the impersonation a plain argv — no
            // shell strings — and "--" pins where Git's own argv begins.
            ? ExecuteCoreAsync(
                repository.Connection,
                "sudo",
                [
                    "-n", "-u", owner, "-H", .. WorkspaceSudoEnvironment(repository),
                    "--", GitExecutable, "--literal-pathspecs",
                    "-C", repository.WorkingTreeRoot, .. arguments,
                ],
                timeout,
                outputLimit,
                cancellationToken,
                acceptExitOne,
                allowTruncated)
            : ExecuteAsync(
                repository.Connection,
                ["-C", repository.WorkingTreeRoot, .. arguments],
                timeout,
                outputLimit,
                cancellationToken,
                acceptExitOne,
                allowTruncated);

    private ValueTask<GitResult<CommandOutput>> ExecuteAsync(
        ConnectionProfile connection,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken cancellationToken,
        bool acceptExitOne = false,
        bool allowTruncated = false) =>
        // Literal pathspecs keep '*' and friends in file names from acting as
        // globs when they come back around as arguments.
        ExecuteCoreAsync(
            connection,
            GitExecutable,
            ["--literal-pathspecs", .. arguments],
            timeout,
            outputLimit,
            cancellationToken,
            acceptExitOne,
            allowTruncated);

    private async ValueTask<GitResult<CommandOutput>> ExecuteCoreAsync(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken cancellationToken,
        bool acceptExitOne = false,
        bool allowTruncated = false)
    {
        var command = new ConnectionCommand(
            connection,
            executable,
            arguments,
            timeout,
            outputLimit);
        var result = await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ConnectionCommandOutcome.Exited
            && (result.ExitCode == 0 || (acceptExitOne && result.ExitCode == 1)))
        {
            // A cut-off machine listing would parse as a shorter, wrong answer,
            // so truncation is only survivable where the caller can present it.
            if (result.OutputTruncated && !allowTruncated)
            {
                return Failure<CommandOutput>(
                    GitErrorCode.InvalidResponse,
                    "Git produced more output than the panel can read.");
            }

            return new GitResult<CommandOutput>.Success(
                new CommandOutput(result.StandardOutput, result.OutputTruncated));
        }

        return new GitResult<CommandOutput>.Failure(NonSuccessError(result));
    }

    private static GitError NonSuccessError(ConnectionCommandResult result)
    {
        switch (result.Outcome)
        {
            case ConnectionCommandOutcome.StartFailed:
                return new GitError(
                    GitErrorCode.GitUnavailable,
                    "Git could not be started on this connection.",
                    Retryable: false);
            case ConnectionCommandOutcome.TimedOut:
                return new GitError(
                    GitErrorCode.TimedOut,
                    "Git did not answer in time.",
                    Retryable: true);
            case ConnectionCommandOutcome.Cancelled:
                return new GitError(
                    GitErrorCode.Cancelled,
                    "The operation was cancelled.",
                    Retryable: false);
            case ConnectionCommandOutcome.ConnectionFailed:
                return new GitError(
                    GitErrorCode.ConnectionFailed,
                    "The connection failed.",
                    Retryable: true);
            default:
                var message = result.StandardError.Length > 0
                    ? result.StandardError.Trim()
                    : result.StandardOutput.Trim();
                if (message.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
                {
                    return new GitError(GitErrorCode.NotARepository, message, Retryable: false);
                }

                // Git's ownership refusal keeps its own explanation; the code
                // is what lets the panel offer the trust remedy.
                if (message.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase))
                {
                    return new GitError(GitErrorCode.OwnershipUntrusted, message, Retryable: false);
                }

                return new GitError(
                    GitErrorCode.CommandFailed,
                    message.Length > 0 ? message : $"Git exited with code {result.ExitCode}.",
                    Retryable: false);
        }
    }

    private static bool IsUnbornHistoryError(GitError error) =>
        error.Code == GitErrorCode.CommandFailed
        && (error.Message.Contains("does not have any commits yet", StringComparison.Ordinal)
            || error.Message.Contains("bad default revision", StringComparison.Ordinal));

    private static GitResult<T>.Failure Failure<T>(
        GitErrorCode code,
        string message,
        bool retryable = false) =>
        new(new GitError(code, message, retryable));

    private static bool Supports(ConnectionProfile connection) =>
        connection.Endpoint is ConnectionEndpoint.Local or ConnectionEndpoint.Ssh;

    private static CommandOutput Value(GitResult<CommandOutput> result) =>
        ((GitResult<CommandOutput>.Success)result).Value;
}
