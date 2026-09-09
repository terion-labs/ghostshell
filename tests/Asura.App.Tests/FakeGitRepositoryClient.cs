using Asura.Core;
using Asura.Git;

namespace Asura.App.Tests;

/// <summary>
/// A structural Git client shared by the Git panel, connection editor, and
/// runtime-graph tests: every read answers with the same small repository,
/// and every mutation records what it was asked to do.
/// </summary>
internal sealed class FakeGitRepositoryClient : IGitRepositoryClient
{
    private static readonly GitCommitItem HeadCommit = new(
        "aaaa000000000000000000000000000000000000",
        "aaaa0000",
        ["bbbb000000000000000000000000000000000000"],
        "terion",
        "t@x",
        DateTimeOffset.FromUnixTimeSeconds(1_755_500_570),
        "browser new tab",
        ["dev"]);

    public int SnapshotReads { get; private set; }

    public int WorkingSetReads { get; private set; }

    public bool FailSnapshots { get; init; }

    /// <summary>When set, staging waits for the test to release it.</summary>
    public TaskCompletionSource? StageGate { get; set; }

    /// <summary>Replaces the default two-file unstaged list when set.</summary>
    public IReadOnlyList<GitFileChange>? UnstagedChangesOverride { get; init; }

    /// <summary>When set, the next working-set read answers with this.</summary>
    public GitWorkingSet? NextWorkingSet { get; set; }

    public GitHeadState CurrentHead => Head();

    public List<string> StagedPaths { get; } = [];

    public List<GitCommitRequest> Commits { get; } = [];

    public List<string> RefOperations { get; } = [];

    /// <summary>While set, opens refuse the way an untrusted owner does.</summary>
    public bool RefuseOpenForOwnership { get; set; }

    /// <summary>When set, the next open fails as a plain command failure.</summary>
    public bool FailNextOpen { get; set; }

    /// <summary>When set, opened handles carry this impersonated user.</summary>
    public string? OpenRunAsUser { get; init; }

    public List<string> TrustedPaths { get; } = [];

    public ValueTask<GitResult<GitRepositoryHandle>> OpenRepositoryAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken)
    {
        if (RefuseOpenForOwnership)
        {
            return ValueTask.FromResult<GitResult<GitRepositoryHandle>>(
                new GitResult<GitRepositoryHandle>.Failure(new GitError(
                    GitErrorCode.OwnershipUntrusted,
                    $"fatal: detected dubious ownership in repository at '{path}'",
                    Retryable: false)));
        }

        if (FailNextOpen)
        {
            FailNextOpen = false;
            return ValueTask.FromResult<GitResult<GitRepositoryHandle>>(
                new GitResult<GitRepositoryHandle>.Failure(new GitError(
                    GitErrorCode.CommandFailed,
                    "fatal: unexpected refusal",
                    Retryable: false)));
        }

        return ValueTask.FromResult<GitResult<GitRepositoryHandle>>(
            new GitResult<GitRepositoryHandle>.Success(
                new GitRepositoryHandle(connection, "/repo", OpenRunAsUser)));
    }

    public ValueTask<GitResult<GitUnit>> TrustRepositoryAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken)
    {
        TrustedPaths.Add(path);

        // Trusting is what makes the same path openable, as it does for
        // the real client.
        RefuseOpenForOwnership = false;
        return Success();
    }

    public ValueTask<GitResult<GitTreeListing>> ReadTreeAsync(
        GitRepositoryHandle repository,
        string sha,
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitTreeListing>>(
            new GitResult<GitTreeListing>.Success(new GitTreeListing(
                path,
                [
                    new GitTreeEntry("src", IsTree: true, Size: null),
                    new GitTreeEntry("README.md", IsTree: false, Size: 1204),
                ])));

    public ValueTask<GitResult<GitBlobSnapshot>> ReadBlobAsync(
        GitRepositoryHandle repository,
        string sha,
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitBlobSnapshot>>(
            new GitResult<GitBlobSnapshot>.Success(new GitBlobSnapshot(
                path,
                "# Asura\n",
                IsBinary: false,
                IsTruncated: false)));

    public ValueTask<GitResult<GitDirectoryListing>> ListDirectoriesAsync(
        ConnectionProfile connection,
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitDirectoryListing>>(
            new GitResult<GitDirectoryListing>.Success(new GitDirectoryListing(
                "/home/qa",
                [
                    new GitDirectoryEntry("asura", IsRepository: true),
                    new GitDirectoryEntry("notes", IsRepository: false),
                ])));

    private static GitHeadState Head() =>
        new("dev", HeadCommit.Sha, "origin/dev", Ahead: 2, Behind: 1, IsDetached: false);

    private IReadOnlyList<GitFileChange> UnstagedChanges() =>
        UnstagedChangesOverride ??
        [
            new("src/a.cs", null, GitChangeKind.Modified, GitChangeArea.Unstaged),
            new("new.txt", null, GitChangeKind.Untracked, GitChangeArea.Unstaged),
        ];

    private static GitFileChange[] StagedChanges() =>
        [new("docs/readme.md", null, GitChangeKind.Modified, GitChangeArea.Staged)];

    public ValueTask<GitResult<GitRepositorySnapshot>> ReadSnapshotAsync(
        GitRepositoryHandle repository,
        long generation,
        CancellationToken cancellationToken)
    {
        SnapshotReads++;
        if (FailSnapshots)
        {
            return ValueTask.FromResult<GitResult<GitRepositorySnapshot>>(
                new GitResult<GitRepositorySnapshot>.Failure(
                    new GitError(GitErrorCode.GitUnavailable, "git is missing", Retryable: false)));
        }

        var snapshot = new GitRepositorySnapshot(
            generation,
            Head(),
            UnstagedChanges(),
            StagedChanges(),
            [new GitRefItem("refs/heads/dev", "dev", GitRefKind.LocalBranch, HeadCommit.Sha, IsCurrent: true)],
            [new GitRemoteItem("origin", "git@github.com:t/x.git")],
            [],
            [new GitWorktreeItem("/repo", "dev", HeadCommit.Sha, IsMain: true)],
            [],
            DateTimeOffset.FromUnixTimeSeconds(1_755_500_600));
        return ValueTask.FromResult<GitResult<GitRepositorySnapshot>>(
            new GitResult<GitRepositorySnapshot>.Success(snapshot));
    }

    public ValueTask<GitResult<GitWorkingSet>> ReadWorkingSetAsync(
        GitRepositoryHandle repository,
        long generation,
        CancellationToken cancellationToken)
    {
        WorkingSetReads++;
        return ValueTask.FromResult<GitResult<GitWorkingSet>>(
            new GitResult<GitWorkingSet>.Success(
                NextWorkingSet is { } prepared
                    ? prepared with { Generation = generation }
                    : new GitWorkingSet(
                        generation,
                        Head(),
                        UnstagedChanges(),
                        StagedChanges())));
    }

    public ValueTask<GitResult<GitCommitPage>> ReadCommitPageAsync(
        GitRepositoryHandle repository,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitCommitPage>>(
            new GitResult<GitCommitPage>.Success(new GitCommitPage(
                [
                    HeadCommit,
                    HeadCommit with { Sha = "bbbb000000000000000000000000000000000000", ShortSha = "bbbb0000", Subject = "first", ParentShas = [], RefNames = [] },
                ],
                offset,
                HasMore: false)));

    public ValueTask<GitResult<GitCommitDetail>> ReadCommitDetailAsync(
        GitRepositoryHandle repository,
        string sha,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitCommitDetail>>(
            new GitResult<GitCommitDetail>.Success(new GitCommitDetail(
                HeadCommit,
                "body text",
                "terion",
                DateTimeOffset.FromUnixTimeSeconds(1_755_500_600),
                [new GitFileChange("src/a.cs", null, GitChangeKind.Modified, GitChangeArea.Staged)])));

    public ValueTask<GitResult<GitDiffDocument>> ReadDiffAsync(
        GitRepositoryHandle repository,
        GitDiffRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<GitResult<GitDiffDocument>>(
            new GitResult<GitDiffDocument>.Success(new GitDiffDocument(
                request.Path,
                request.OriginalPath,
                IsBinary: false,
                IsTruncated: false,
                [
                    new GitDiffHunk(
                        "@@ -1,2 +1,2 @@",
                        [
                            new GitDiffLine(GitDiffLineKind.Context, "line", 1, 1),
                            new GitDiffLine(GitDiffLineKind.Removed, "old", 2, null),
                            new GitDiffLine(GitDiffLineKind.Added, "new", null, 2),
                        ]),
                ])));

    public async ValueTask<GitResult<GitUnit>> StageAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (StageGate is { } gate)
        {
            await gate.Task;
        }

        StagedPaths.AddRange(paths);
        return new GitResult<GitUnit>.Success(GitUnit.Value);
    }

    public ValueTask<GitResult<GitUnit>> UnstageAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) => Success();

    public ValueTask<GitResult<GitUnit>> DiscardAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<GitFileChange> changes,
        CancellationToken cancellationToken) => Success();

    public ValueTask<GitResult<GitUnit>> CommitAsync(
        GitRepositoryHandle repository,
        GitCommitRequest request,
        CancellationToken cancellationToken)
    {
        Commits.Add(request);
        return Success();
    }

    public ValueTask<GitResult<GitUnit>> CheckoutBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"checkout {name}");

    public ValueTask<GitResult<GitUnit>> CreateBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"create-branch {name}");

    public ValueTask<GitResult<GitUnit>> RenameBranchAsync(
        GitRepositoryHandle repository,
        string oldName,
        string newName,
        CancellationToken cancellationToken) => Record($"rename-branch {oldName} {newName}");

    public ValueTask<GitResult<GitUnit>> DeleteBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"delete-branch {name}");

    public ValueTask<GitResult<GitUnit>> MergeBranchAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"merge {name}");

    public ValueTask<GitResult<GitUnit>> CreateTagAsync(
        GitRepositoryHandle repository,
        string name,
        string? message,
        string? revision,
        CancellationToken cancellationToken) =>
        Record(string.IsNullOrEmpty(message) ? $"create-tag {name}" : $"create-tag {name} {message}");

    public ValueTask<GitResult<GitUnit>> DeleteTagAsync(
        GitRepositoryHandle repository,
        string name,
        IReadOnlyList<string> alsoOnRemotes,
        CancellationToken cancellationToken) =>
        Record($"delete-tag {name} [{string.Join(",", alsoOnRemotes)}]");

    public ValueTask<GitResult<GitUnit>> AddRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        string url,
        CancellationToken cancellationToken) => Record($"add-remote {name} {url}");

    public ValueTask<GitResult<GitUnit>> EditRemoteAsync(
        GitRepositoryHandle repository,
        string oldName,
        string newName,
        string url,
        CancellationToken cancellationToken) => Record($"edit-remote {oldName} {newName} {url}");

    public ValueTask<GitResult<GitUnit>> RemoveRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"remove-remote {name}");

    public ValueTask<GitResult<GitUnit>> FetchRemoteAsync(
        GitRepositoryHandle repository,
        string name,
        CancellationToken cancellationToken) => Record($"fetch {name}");

    public ValueTask<GitResult<GitUnit>> WorktreeAddAsync(
        GitRepositoryHandle repository,
        string path,
        string branch,
        CancellationToken cancellationToken) => Record($"worktree-add {path} {branch}");

    public ValueTask<GitResult<GitUnit>> FastForwardAsync(
        GitRepositoryHandle repository,
        string branch,
        string upstream,
        bool isCurrent,
        CancellationToken cancellationToken) => Record($"fast-forward {branch} {upstream}");

    public ValueTask<GitResult<GitUnit>> PushBranchAsync(
        GitRepositoryHandle repository,
        string remote,
        string branch,
        CancellationToken cancellationToken) => Record($"push-branch {remote} {branch}");

    public ValueTask<GitResult<GitUnit>> RebaseAsync(
        GitRepositoryHandle repository,
        string onto,
        CancellationToken cancellationToken) => Record($"rebase {onto}");

    public ValueTask<GitResult<GitUnit>> PullAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken) => Record("pull");

    public ValueTask<GitResult<GitUnit>> PushAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken) => Record("push");

    public ValueTask<GitResult<GitUnit>> StashPushAsync(
        GitRepositoryHandle repository,
        string? message,
        CancellationToken cancellationToken) => Record("stash-push");

    public ValueTask<GitResult<GitUnit>> StashApplyAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken) => Record($"stash-apply {reference}");

    public ValueTask<GitResult<GitUnit>> StashPopAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken) => Record($"stash-pop {reference}");

    public ValueTask<GitResult<GitUnit>> StashDropAsync(
        GitRepositoryHandle repository,
        string reference,
        CancellationToken cancellationToken) => Record($"stash-drop {reference}");

    private ValueTask<GitResult<GitUnit>> Record(string operation)
    {
        RefOperations.Add(operation);
        return Success();
    }

    private static ValueTask<GitResult<GitUnit>> Success() =>
        ValueTask.FromResult<GitResult<GitUnit>>(new GitResult<GitUnit>.Success(GitUnit.Value));
}
