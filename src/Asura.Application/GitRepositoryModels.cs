using Asura.Core;

namespace Asura.Git;

public enum GitErrorCode
{
    /// <summary>The connection kind cannot host a Git repository panel.</summary>
    Unsupported,

    /// <summary>The configured Git executable could not be started on the target.</summary>
    GitUnavailable,

    /// <summary>The requested path is not inside a Git working tree.</summary>
    NotARepository,

    /// <summary>Git exited non-zero; the message carries Git's own explanation.</summary>
    CommandFailed,

    /// <summary>Git produced output the adapter could not parse.</summary>
    InvalidResponse,

    ConnectionFailed,

    TimedOut,

    Cancelled,

    /// <summary>
    /// Git refused the repository because it belongs to a different user than
    /// the one the connection signs in as ("dubious ownership"). Trusting the
    /// repository applies Git's own remedy and makes the path openable.
    /// </summary>
    OwnershipUntrusted,
}

public sealed record GitError(
    GitErrorCode Code,
    string Message,
    bool Retryable);

public abstract record GitResult<T>
{
    private GitResult()
    {
    }

    public sealed record Success(T Value) : GitResult<T>;

    public sealed record Failure(GitError Error) : GitResult<T>;
}

/// <summary>
/// A repository opened on a connection target. The root is the working-tree
/// top level as Git reported it, so every later command can run with an
/// explicit <c>-C root</c> instead of inheriting a working directory.
/// When <paramref name="RunAsUser"/> is set, every command on this handle
/// runs as that user (the repository's owner) instead of the signed-in one,
/// so a root connection opens another user's repository without a trust
/// exception.
/// </summary>
public sealed record GitRepositoryHandle(
    ConnectionProfile Connection,
    string WorkingTreeRoot,
    string? RunAsUser = null);

public enum GitChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Conflicted,
}

public enum GitChangeArea
{
    Unstaged,
    Staged,
}

public sealed record GitFileChange(
    string Path,
    string? OriginalPath,
    GitChangeKind Kind,
    GitChangeArea Area,
    bool IsSubmodule = false);

/// <summary>
/// HEAD as one value: a born branch, a detached commit, or an unborn branch in
/// a fresh repository. Ahead/behind are null when there is no upstream.
/// </summary>
public sealed record GitHeadState(
    string BranchName,
    string? CommitSha,
    string? Upstream,
    int? Ahead,
    int? Behind,
    bool IsDetached)
{
    public bool IsUnborn => CommitSha is null;
}

public enum GitRefKind
{
    LocalBranch,
    RemoteBranch,
    Tag,
}

public sealed record GitRefItem(
    string FullName,
    string ShortName,
    GitRefKind Kind,
    string TargetSha,
    bool IsCurrent,
    string? Upstream = null,
    int? Ahead = null,
    int? Behind = null);

public sealed record GitRemoteItem(string Name, string FetchUrl);

public sealed record GitStashItem(string Reference, string Subject);

public sealed record GitWorktreeItem(
    string Path,
    string? Branch,
    string? HeadSha,
    bool IsMain);

public sealed record GitSubmoduleItem(string Path, string Sha, string State);

/// <summary>
/// One immutable generation of repository state. The generation number lets
/// mutations detect that they were composed against a snapshot the worktree
/// has since left behind.
/// </summary>
public sealed record GitRepositorySnapshot(
    long Generation,
    GitHeadState Head,
    IReadOnlyList<GitFileChange> UnstagedChanges,
    IReadOnlyList<GitFileChange> StagedChanges,
    IReadOnlyList<GitRefItem> Refs,
    IReadOnlyList<GitRemoteItem> Remotes,
    IReadOnlyList<GitStashItem> Stashes,
    IReadOnlyList<GitWorktreeItem> Worktrees,
    IReadOnlyList<GitSubmoduleItem> Submodules,
    DateTimeOffset CapturedAtUtc)
{
    public bool HasConflicts =>
        UnstagedChanges.Any(change => change.Kind == GitChangeKind.Conflicted);
}

/// <summary>
/// The index-and-worktree slice of a snapshot: what one status read answers.
/// Staging-shaped mutations refresh this instead of the full snapshot because
/// they cannot move refs, remotes, stashes, worktrees, or submodules.
/// </summary>
public sealed record GitWorkingSet(
    long Generation,
    GitHeadState Head,
    IReadOnlyList<GitFileChange> UnstagedChanges,
    IReadOnlyList<GitFileChange> StagedChanges);

public sealed record GitCommitItem(
    string Sha,
    string ShortSha,
    IReadOnlyList<string> ParentShas,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string Subject,
    IReadOnlyList<string> RefNames);

public sealed record GitCommitPage(
    IReadOnlyList<GitCommitItem> Commits,
    int Offset,
    bool HasMore);

public sealed record GitCommitDetail(
    GitCommitItem Commit,
    string Body,
    string CommitterName,
    DateTimeOffset CommittedAt,
    IReadOnlyList<GitFileChange> Changes);

/// <summary>Which two trees a file diff compares.</summary>
public enum GitDiffArea
{
    /// <summary>Working tree against the index (an unstaged change).</summary>
    Worktree,

    /// <summary>Index against HEAD (a staged change).</summary>
    Index,

    /// <summary>A commit against its first parent.</summary>
    Commit,
}

public sealed record GitDiffRequest(
    GitDiffArea Area,
    string Path,
    string? OriginalPath = null,
    string? CommitSha = null,
    bool IsUntracked = false,
    bool IgnoreWhitespace = false);

public enum GitDiffLineKind
{
    Context,
    Added,
    Removed,
}

public sealed record GitDiffLine(
    GitDiffLineKind Kind,
    string Text,
    int? OldLineNumber,
    int? NewLineNumber);

public sealed record GitDiffHunk(
    string Header,
    IReadOnlyList<GitDiffLine> Lines);

public sealed record GitDiffDocument(
    string Path,
    string? OriginalPath,
    bool IsBinary,
    bool IsTruncated,
    IReadOnlyList<GitDiffHunk> Hunks);

public sealed record GitCommitRequest(
    string Subject,
    string Body,
    bool Amend);

public sealed record GitTreeEntry(string Name, bool IsTree, long? Size);

/// <summary>One directory level of a commit's tree.</summary>
public sealed record GitTreeListing(
    string Path,
    IReadOnlyList<GitTreeEntry> Entries);

public sealed record GitBlobSnapshot(
    string Path,
    string Text,
    bool IsBinary,
    bool IsTruncated);

public sealed record GitDirectoryEntry(string Name, bool IsRepository);

/// <summary>
/// One directory level on the connection target, for choosing a repository
/// without a local file dialog. The path is the canonical directory the
/// target resolved, so callers can navigate relative to it.
/// </summary>
public sealed record GitDirectoryListing(
    string Path,
    IReadOnlyList<GitDirectoryEntry> Directories);

/// <summary>Result value for operations whose success carries no data.</summary>
public sealed record GitUnit
{
    public static GitUnit Value { get; } = new();

    private GitUnit()
    {
    }
}

/// <summary>
/// Typed Git operations over one repository on one connection target. All
/// members take the handle rather than holding state so a single client
/// instance serves every panel; the panel session owns snapshots and the
/// one-mutation-at-a-time gate.
/// </summary>
public interface IGitRepositoryClient
{
    /// <summary>Resolves a path to the repository containing it.</summary>
    ValueTask<GitResult<GitRepositoryHandle>> OpenRepositoryAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks the path as a safe directory for the user the connection signs
    /// in as — Git's own remedy for an <see cref="GitErrorCode.OwnershipUntrusted"/>
    /// refusal — so a repository owned by another user can be opened.
    /// </summary>
    ValueTask<GitResult<GitUnit>> TrustRepositoryAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists a directory on the connection target, marking entries that hold
    /// a repository. An empty path means the target's home directory.
    /// </summary>
    ValueTask<GitResult<GitDirectoryListing>> ListDirectoriesAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitRepositorySnapshot>> ReadSnapshotAsync(
        GitRepositoryHandle repository,
        long generation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads only the working set — one status command instead of the full
    /// snapshot — for refreshing after an index-only mutation.
    /// </summary>
    ValueTask<GitResult<GitWorkingSet>> ReadWorkingSetAsync(
        GitRepositoryHandle repository,
        long generation,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitCommitPage>> ReadCommitPageAsync(
        GitRepositoryHandle repository,
        int offset,
        int count,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitCommitDetail>> ReadCommitDetailAsync(
        GitRepositoryHandle repository,
        string sha,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitDiffDocument>> ReadDiffAsync(
        GitRepositoryHandle repository,
        GitDiffRequest request,
        CancellationToken cancellationToken);

    /// <summary>Lists one directory of a commit's tree; empty path is the root.</summary>
    ValueTask<GitResult<GitTreeListing>> ReadTreeAsync(
        GitRepositoryHandle repository,
        string sha,
        string path,
        CancellationToken cancellationToken);

    /// <summary>Reads one file's content as recorded in a commit.</summary>
    ValueTask<GitResult<GitBlobSnapshot>> ReadBlobAsync(
        GitRepositoryHandle repository,
        string sha,
        string path,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitUnit>> StageAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitUnit>> UnstageAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken);

    /// <summary>
    /// Restores tracked files to their index state and deletes untracked ones.
    /// Destructive; callers own the confirmation flow.
    /// </summary>
    ValueTask<GitResult<GitUnit>> DiscardAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<GitFileChange> changes,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitUnit>> CommitAsync(
        GitRepositoryHandle repository,
        GitCommitRequest request,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitUnit>> CheckoutBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken);

    /// <summary>Creates a branch at HEAD and switches to it.</summary>
    ValueTask<GitResult<GitUnit>> CreateBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitUnit>> RenameBranchAsync(
        GitRepositoryHandle repository,
        string oldName,
        string newName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Forces the branch away even when unmerged. Destructive; callers own
    /// the confirmation flow.
    /// </summary>
    ValueTask<GitResult<GitUnit>> DeleteBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken);

    /// <summary>Merges the named branch into the current one.</summary>
    ValueTask<GitResult<GitUnit>> MergeBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken);

    /// <summary>Checks the branch out into a new linked worktree at the path.</summary>
    ValueTask<GitResult<GitUnit>> WorktreeAddAsync(
        GitRepositoryHandle repository,
        string path,
        string branch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fast-forwards the branch to its upstream. The current branch merges
    /// with --ff-only; any other branch has its ref advanced in place, and
    /// Git refuses the move when it would not be a fast-forward.
    /// </summary>
    ValueTask<GitResult<GitUnit>> FastForwardAsync(
        GitRepositoryHandle repository,
        string branch,
        string upstream,
        bool isCurrent,
        CancellationToken cancellationToken);

    /// <summary>Pushes one branch to the named remote.</summary>
    ValueTask<GitResult<GitUnit>> PushBranchAsync(
        GitRepositoryHandle repository,
        string remote,
        string branch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Rebases the current branch onto the named revision. A failed rebase is
    /// aborted before the error surfaces, so the worktree never stays mid-rebase.
    /// </summary>
    ValueTask<GitResult<GitUnit>> RebaseAsync(
        GitRepositoryHandle repository,
        string onto,
        CancellationToken cancellationToken);

    /// <summary>
    /// Tags a revision (HEAD when null): lightweight without a message,
    /// annotated with one.
    /// </summary>
    ValueTask<GitResult<GitUnit>> CreateTagAsync(
        GitRepositoryHandle repository,
        string name,
        string? message,
        string? revision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the tag locally, then from each named remote. Stops at the
    /// first failure so a half-finished deletion is reported, not hidden.
    /// </summary>
    ValueTask<GitResult<GitUnit>> DeleteTagAsync(
        GitRepositoryHandle repository,
        string name,
        IReadOnlyList<string> alsoOnRemotes,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitUnit>> AddRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        string url,
        CancellationToken cancellationToken);

    /// <summary>Renames the remote when the names differ, then sets its URL.</summary>
    ValueTask<GitResult<GitUnit>> EditRemoteAsync(
        GitRepositoryHandle repository,
        string oldName,
        string newName,
        string url,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitUnit>> RemoveRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitUnit>> FetchRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken);

    /// <summary>Pulls the current branch from its upstream.</summary>
    ValueTask<GitResult<GitUnit>> PullAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken);

    /// <summary>Pushes the current branch to its upstream.</summary>
    ValueTask<GitResult<GitUnit>> PushAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken);

    /// <summary>Stashes the working changes, optionally with a message.</summary>
    ValueTask<GitResult<GitUnit>> StashPushAsync(
        GitRepositoryHandle repository,
        string? message,
        CancellationToken cancellationToken);

    /// <summary>Applies a stash, keeping it on the stash list.</summary>
    ValueTask<GitResult<GitUnit>> StashApplyAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken);

    /// <summary>Applies a stash and drops it when the apply succeeds.</summary>
    ValueTask<GitResult<GitUnit>> StashPopAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken);

    /// <summary>
    /// Drops a stash without applying it. Destructive; callers own the
    /// confirmation flow.
    /// </summary>
    ValueTask<GitResult<GitUnit>> StashDropAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken);

    ValueTask<GitResult<GitGovernedState>> ReadGovernedStateAsync(
        GitRepositoryHandle repository,
        long generation,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitGovernedState>>(
            new GitResult<GitGovernedState>.Failure(new GitError(
                GitErrorCode.Unsupported,
                "Governed Git state is unavailable.",
                Retryable: false)));

    ValueTask<GitResult<GitGovernedRemoteRef>> ReadGovernedRemoteRefAsync(
        GitRepositoryHandle repository,
        string remoteName,
        string destinationBranch,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitGovernedRemoteRef>>(
            new GitResult<GitGovernedRemoteRef>.Failure(new GitError(
                GitErrorCode.Unsupported,
                "Governed remote observation is unavailable.",
                Retryable: false)));

    ValueTask<GitResult<GitDiffDocument>> ReadGovernedDiffAsync(
        GitRepositoryHandle repository,
        GitDiffRequest request,
        CancellationToken cancellationToken) =>
        ReadDiffAsync(repository, request, cancellationToken);

    ValueTask<GitGovernedMutationReceipt> StageGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        GitFileChange expectedChange,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(UnsupportedGovernedMutation());

    ValueTask<GitGovernedMutationReceipt> UnstageGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        GitFileChange expectedChange,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(UnsupportedGovernedMutation());

    ValueTask<GitGovernedMutationReceipt> CreateBranchGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        string name,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(UnsupportedGovernedMutation());

    ValueTask<GitGovernedMutationReceipt> CheckoutBranchGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        string branchName,
        string expectedBranchSha,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(UnsupportedGovernedMutation());

    ValueTask<GitGovernedMutationReceipt> CommitGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        string subject,
        string? body,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(UnsupportedGovernedMutation());

    ValueTask<GitGovernedMutationReceipt> PushGovernedAsync(
        GitRepositoryHandle repository,
        GitGovernedPushRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(UnsupportedGovernedMutation());

    private static GitGovernedMutationReceipt UnsupportedGovernedMutation() =>
        new(
            GitGovernedMutationDisposition.Rejected,
            "git_governed_operation_unavailable",
            State: null);
}
