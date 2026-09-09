using Asura.Application;
using Asura.Core;

namespace Asura.Git.Tests;

public sealed class GitRepositoryClientTests
{
    [Fact]
    public async Task ListDirectoriesParsesProbeOutputAndRanksRepositoriesFirst()
    {
        var executor = new RecordingExecutor(new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            ExitCode: 0,
            "/home/qa\0zeta\0d\0asura\0r\0archive with space\0d\0alpha\0r\0"));
        var client = new GitRepositoryClient(executor, TimeProvider.System);

        var result = await client.ListDirectoriesAsync(
            BuiltInConnections.Local,
            "~/projects",
            CancellationToken.None);

        var listing = Assert.IsType<GitResult<GitDirectoryListing>.Success>(result).Value;
        Assert.Equal("/home/qa", listing.Path);
        Assert.Equal(
            ["alpha", "asura", "archive with space", "zeta"],
            listing.Directories.Select(entry => entry.Name),
            StringComparer.Ordinal);
        Assert.True(listing.Directories[0].IsRepository);
        Assert.False(listing.Directories[^1].IsRepository);

        // The path travels as a positional argument, never inside the script.
        var command = Assert.Single(executor.Commands);
        Assert.Equal("/bin/sh", command.Executable);
        Assert.Equal("-c", command.Arguments[0]);
        Assert.Equal("~/projects", command.Arguments[^1]);
        Assert.DoesNotContain("~/projects", command.Arguments[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListDirectoriesReportsAnUnopenableFolder()
    {
        var executor = new RecordingExecutor(new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            ExitCode: 9,
            ""));
        var client = new GitRepositoryClient(executor, TimeProvider.System);

        var result = await client.ListDirectoriesAsync(
            BuiltInConnections.Local,
            "/nope",
            CancellationToken.None);

        var failure = Assert.IsType<GitResult<GitDirectoryListing>.Failure>(result);
        Assert.Equal(GitErrorCode.CommandFailed, failure.Error.Code);
    }

    [Fact]
    public async Task ReadingTheWorkingSetSpeaksOneStatusCommand()
    {
        var executor = new RecordingExecutor(new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            ExitCode: 0,
            "# branch.oid aaaa000000000000000000000000000000000000\0"
            + "# branch.head dev\0"
            + "# branch.upstream origin/dev\0"
            + "# branch.ab +2 -1\0"
            + "1 .M N... 100644 100644 100644 aaaa aaaa src/a.cs\0"
            + "1 M. N... 100644 100644 100644 aaaa bbbb docs/readme.md\0"
            + "? new.txt\0"));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.ReadWorkingSetAsync(repository, 7, CancellationToken.None);

        var workingSet = Assert.IsType<GitResult<GitWorkingSet>.Success>(result).Value;
        Assert.Equal(7, workingSet.Generation);
        Assert.Equal("dev", workingSet.Head.BranchName);
        Assert.Equal(2, workingSet.Head.Ahead);
        Assert.Equal(1, workingSet.Head.Behind);
        Assert.Equal(
            ["src/a.cs", "new.txt"],
            workingSet.UnstagedChanges.Select(change => change.Path),
            StringComparer.Ordinal);
        var staged = Assert.Single(workingSet.StagedChanges);
        Assert.Equal("docs/readme.md", staged.Path);

        // One command, and exactly the argv the full snapshot's status read
        // speaks, so both paths parse the same stream.
        var command = Assert.Single(executor.Commands);
        Assert.Equal("git", command.Executable);
        Assert.Equal(
            [
                "--literal-pathspecs", "-C", "/repo", "-c", "core.fsmonitor=false", "--no-optional-locks", "status",
                "--porcelain=v2", "-z", "--branch", "--untracked-files=all",
            ],
            command.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task CheckoutSpeaksGitSwitchOverTheRepositoryRoot()
    {
        var executor = new RecordingExecutor(new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            ExitCode: 0,
            ""));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.CheckoutBranchAsync(repository, "feature/x", CancellationToken.None);

        Assert.IsType<GitResult<GitUnit>.Success>(result);
        var command = Assert.Single(executor.Commands);
        Assert.Equal("git", command.Executable);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "switch", "feature/x"],
            command.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task DeletingATagAlsoOnARemoteRunsTheLocalAndPushDeletes()
    {
        var executor = new RecordingExecutor(new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            ExitCode: 0,
            ""));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.DeleteTagAsync(
            repository,
            "v1.0",
            ["origin"],
            CancellationToken.None);

        Assert.IsType<GitResult<GitUnit>.Success>(result);
        Assert.Equal(2, executor.Commands.Count);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "tag", "-d", "v1.0"],
            executor.Commands[0].Arguments,
            StringComparer.Ordinal);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "push", "origin", "--delete", "refs/tags/v1.0"],
            executor.Commands[1].Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task EditingARemoteWithANewNameRenamesBeforeSettingTheUrl()
    {
        var executor = new RecordingExecutor(new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            ExitCode: 0,
            ""));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.EditRemoteAsync(
            repository,
            "origin",
            "upstream",
            "git@github.com:t/x.git",
            CancellationToken.None);

        Assert.IsType<GitResult<GitUnit>.Success>(result);
        Assert.Equal(2, executor.Commands.Count);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "remote", "rename", "origin", "upstream"],
            executor.Commands[0].Arguments,
            StringComparer.Ordinal);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "remote", "set-url", "upstream", "git@github.com:t/x.git"],
            executor.Commands[1].Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task IgnoringWhitespaceAddsTheFlagToTheDiffInvocation()
    {
        var executor = new RecordingExecutor(new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            ExitCode: 0,
            ""));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.ReadDiffAsync(
            repository,
            new GitDiffRequest(GitDiffArea.Worktree, "src/a.cs", IgnoreWhitespace: true),
            CancellationToken.None);

        Assert.IsType<GitResult<GitDiffDocument>.Success>(result);
        var command = Assert.Single(executor.Commands);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "-c", "core.fsmonitor=false", "diff", "-w", "--no-color", "--", "src/a.cs"],
            command.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task CheckingOutAWorktreeSpeaksWorktreeAddWithPathThenBranch()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.WorktreeAddAsync(
            repository,
            "/repo-feature-x",
            "feature/x",
            CancellationToken.None);

        Assert.IsType<GitResult<GitUnit>.Success>(result);
        var command = Assert.Single(executor.Commands);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "worktree", "add", "/repo-feature-x", "feature/x"],
            command.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task FastForwardingTheCurrentBranchMergesFfOnly()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.FastForwardAsync(
            repository,
            "dev",
            "origin/dev",
            isCurrent: true,
            CancellationToken.None);

        Assert.IsType<GitResult<GitUnit>.Success>(result);
        var command = Assert.Single(executor.Commands);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "merge", "--ff-only", "origin/dev"],
            command.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task FastForwardingAnotherBranchMovesItsRefWithAFetchRefspec()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.FastForwardAsync(
            repository,
            "feature",
            "origin/feature",
            isCurrent: false,
            CancellationToken.None);

        // Verified against real Git in a scratch repository: fetching from
        // "." with an unforced, fully qualified refspec advances the local
        // ref and refuses any move that is not a fast-forward.
        Assert.IsType<GitResult<GitUnit>.Success>(result);
        var command = Assert.Single(executor.Commands);
        Assert.Equal(
            [
                "--literal-pathspecs", "-C", "/repo", "fetch", ".",
                "refs/remotes/origin/feature:refs/heads/feature",
            ],
            command.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task PushingABranchNamesTheRemoteAndTheBranch()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.PushBranchAsync(
            repository,
            "origin",
            "feature/x",
            CancellationToken.None);

        Assert.IsType<GitResult<GitUnit>.Success>(result);
        var command = Assert.Single(executor.Commands);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "push", "origin", "feature/x"],
            command.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task RemoteOperationsRejectOptionShapedOperandsBeforeExecutingGit()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.PushBranchAsync(repository, "--all", "main", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.DeleteTagAsync(repository, "v1", ["--mirror"], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.AddRemoteAsync(repository, "-f", "file:///repo", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.EditRemoteAsync(repository, "-f", "safe", "file:///repo", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.EditRemoteAsync(repository, "safe", "--add", "file:///repo", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.RemoveRemoteAsync(repository, "--all", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.FetchRemoteAsync(repository, "--multiple", CancellationToken.None));

        Assert.Empty(executor.Commands);
    }

    [Fact]
    public async Task AConflictedRebaseIsAbortedAndTheErrorSaysSo()
    {
        var executor = new RecordingExecutor(
            new ConnectionCommandResult(
                ConnectionCommandOutcome.Exited,
                ExitCode: 1,
                "",
                "CONFLICT (content): could not apply 1234abcd"),
            Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.RebaseAsync(repository, "main", CancellationToken.None);

        var failure = Assert.IsType<GitResult<GitUnit>.Failure>(result);
        Assert.Equal(GitErrorCode.CommandFailed, failure.Error.Code);
        Assert.EndsWith("(rebase aborted)", failure.Error.Message, StringComparison.Ordinal);
        Assert.Contains("could not apply", failure.Error.Message, StringComparison.Ordinal);
        Assert.Equal(2, executor.Commands.Count);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "rebase", "main"],
            executor.Commands[0].Arguments,
            StringComparer.Ordinal);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "rebase", "--abort"],
            executor.Commands[1].Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task CreatingATagAtARevisionTagsThatRevision()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        var result = await client.CreateTagAsync(
            repository,
            "v1.1",
            "release",
            "feature/x",
            CancellationToken.None);

        Assert.IsType<GitResult<GitUnit>.Success>(result);
        var command = Assert.Single(executor.Commands);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "tag", "-a", "v1.1", "-m", "release", "feature/x"],
            command.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task PullAndPushSpeakTheirPlainInvocations()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        Assert.IsType<GitResult<GitUnit>.Success>(
            await client.PullAsync(repository, CancellationToken.None));
        Assert.IsType<GitResult<GitUnit>.Success>(
            await client.PushAsync(repository, CancellationToken.None));

        Assert.Equal(2, executor.Commands.Count);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "pull"],
            executor.Commands[0].Arguments,
            StringComparer.Ordinal);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "push"],
            executor.Commands[1].Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task StashPushCarriesTheMessageOnlyWhenGiven()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        await client.StashPushAsync(repository, null, CancellationToken.None);
        await client.StashPushAsync(repository, "wip: diff polish", CancellationToken.None);

        Assert.Equal(2, executor.Commands.Count);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "stash", "push"],
            executor.Commands[0].Arguments,
            StringComparer.Ordinal);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "stash", "push", "-m", "wip: diff polish"],
            executor.Commands[1].Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task StashRowOperationsNameTheReference()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo");

        await client.StashApplyAsync(repository, "stash@{0}", CancellationToken.None);
        await client.StashPopAsync(repository, "stash@{1}", CancellationToken.None);
        await client.StashDropAsync(repository, "stash@{2}", CancellationToken.None);

        Assert.Equal(3, executor.Commands.Count);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "stash", "apply", "stash@{0}"],
            executor.Commands[0].Arguments,
            StringComparer.Ordinal);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "stash", "pop", "stash@{1}"],
            executor.Commands[1].Arguments,
            StringComparer.Ordinal);
        Assert.Equal(
            ["--literal-pathspecs", "-C", "/repo", "stash", "drop", "stash@{2}"],
            executor.Commands[2].Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task TrustingARepositorySpeaksGitsOwnSafeDirectoryRemedy()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);

        var result = await client.TrustRepositoryAsync(
            BuiltInConnections.Local,
            "/srv/repo",
            CancellationToken.None);

        Assert.IsType<GitResult<GitUnit>.Success>(result);

        // No -C prefix: the repository is not openable yet, and the remedy
        // edits the signed-in user's global configuration.
        var command = Assert.Single(executor.Commands);
        Assert.Equal("git", command.Executable);
        Assert.Equal(
            ["--literal-pathspecs", "config", "--global", "--add", "safe.directory", "/srv/repo"],
            command.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task ADubiousOwnershipRefusalMapsToOwnershipUntrusted()
    {
        var executor = new RecordingExecutor(new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            ExitCode: 128,
            "",
            "fatal: detected dubious ownership in repository at '/srv/repo'"));
        var client = new GitRepositoryClient(executor, TimeProvider.System);

        var result = await client.OpenRepositoryAsync(
            BuiltInConnections.Local,
            "/srv/repo",
            CancellationToken.None);

        // Git's own message survives; only the code changes, so the panel
        // can offer the trust remedy.
        var failure = Assert.IsType<GitResult<GitRepositoryHandle>.Failure>(result);
        Assert.Equal(GitErrorCode.OwnershipUntrusted, failure.Error.Code);
        Assert.Contains("dubious ownership", failure.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHandleRunningAsAnotherUserSpeaksThroughSudo()
    {
        var executor = new RecordingExecutor(Exited(0));
        var client = new GitRepositoryClient(executor, TimeProvider.System);
        var repository = new GitRepositoryHandle(BuiltInConnections.Local, "/repo", RunAsUser: "coder");

        var result = await client.CheckoutBranchAsync(repository, "main", CancellationToken.None);

        // A plain argv end to end: non-interactive sudo, and "--" pinning
        // where Git's own arguments begin.
        Assert.IsType<GitResult<GitUnit>.Success>(result);
        var command = Assert.Single(executor.Commands);
        Assert.Equal("sudo", command.Executable);
        Assert.Equal(
            [
                "-n", "-u", "coder", "-H", "--", "git", "--literal-pathspecs",
                "-C", "/repo", "switch", "main",
            ],
            command.Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task OpeningAsRootImpersonatesTheOwnerInsteadOfRefusing()
    {
        var executor = new RecordingExecutor(
            new ConnectionCommandResult(
                ConnectionCommandOutcome.Exited,
                ExitCode: 128,
                "",
                "fatal: detected dubious ownership in repository at '/srv/repo'"),
            new ConnectionCommandResult(ConnectionCommandOutcome.Exited, ExitCode: 0, "root\n"),
            new ConnectionCommandResult(ConnectionCommandOutcome.Exited, ExitCode: 0, "coder\n"),
            new ConnectionCommandResult(ConnectionCommandOutcome.Exited, ExitCode: 0, "/srv/repo\n"));
        var client = new GitRepositoryClient(executor, TimeProvider.System);

        var result = await client.OpenRepositoryAsync(
            BuiltInConnections.Local,
            "/srv/repo",
            CancellationToken.None);

        var handle = Assert.IsType<GitResult<GitRepositoryHandle>.Success>(result).Value;
        Assert.Equal("/srv/repo", handle.WorkingTreeRoot);
        Assert.Equal("coder", handle.RunAsUser);

        // The probe sequence: the refusal, who am I, who owns it, then the
        // same open again through sudo as the owner.
        Assert.Equal(4, executor.Commands.Count);
        Assert.Equal("id", executor.Commands[1].Executable);
        Assert.Equal(["-un"], executor.Commands[1].Arguments, StringComparer.Ordinal);
        Assert.Equal("stat", executor.Commands[2].Executable);
        Assert.Equal(["-c", "%U", "/srv/repo"], executor.Commands[2].Arguments, StringComparer.Ordinal);
        Assert.Equal("sudo", executor.Commands[3].Executable);
        Assert.Equal(
            [
                "-n", "-u", "coder", "-H", "--", "git", "--literal-pathspecs",
                "-C", "/srv/repo", "rev-parse", "--show-toplevel",
            ],
            executor.Commands[3].Arguments,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task AFailedImpersonationFallsBackToTheOwnershipRefusal()
    {
        var executor = new RecordingExecutor(
            new ConnectionCommandResult(
                ConnectionCommandOutcome.Exited,
                ExitCode: 128,
                "",
                "fatal: detected dubious ownership in repository at '/srv/repo'"),
            new ConnectionCommandResult(ConnectionCommandOutcome.Exited, ExitCode: 0, "root\n"),
            new ConnectionCommandResult(ConnectionCommandOutcome.Exited, ExitCode: 0, "coder\n"),
            // sudo is absent or refuses non-interactively.
            new ConnectionCommandResult(ConnectionCommandOutcome.StartFailed, ExitCode: null, ""));
        var client = new GitRepositoryClient(executor, TimeProvider.System);

        var result = await client.OpenRepositoryAsync(
            BuiltInConnections.Local,
            "/srv/repo",
            CancellationToken.None);

        // The original refusal surfaces untouched, so the trust-exception
        // flow stays available as the fallback.
        var failure = Assert.IsType<GitResult<GitRepositoryHandle>.Failure>(result);
        Assert.Equal(GitErrorCode.OwnershipUntrusted, failure.Error.Code);
        Assert.Contains("dubious ownership", failure.Error.Message, StringComparison.Ordinal);
    }

    private static ConnectionCommandResult Exited(int exitCode) =>
        new(ConnectionCommandOutcome.Exited, exitCode, "");

    private sealed class RecordingExecutor(params ConnectionCommandResult[] results)
        : IConnectionCommandExecutor
    {
        public List<ConnectionCommand> Commands { get; } = [];

        public ValueTask<ConnectionCommandResult> ExecuteAsync(
            ConnectionCommand request,
            CancellationToken cancellationToken)
        {
            Commands.Add(request);

            // Sequenced answers serve multi-command operations; the last one
            // repeats so single-answer tests stay as terse as before.
            return ValueTask.FromResult(results[Math.Min(Commands.Count, results.Length) - 1]);
        }

        public ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(
            ConnectionBinaryCommand request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(
            ConnectionBinaryCommand request,
            Func<Stream, CancellationToken, ValueTask<T>> consumeOutput,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
