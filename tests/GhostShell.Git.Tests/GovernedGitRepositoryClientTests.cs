using System.Runtime.Versioning;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Infrastructure;

namespace GhostShell.Git.Tests;

[SupportedOSPlatform("macos")]
public sealed class GovernedGitRepositoryClientTests
{
    [Fact]
    public async Task CancelledCaptureRemovesPrivateBytesAfterProcessTermination()
    {
        await using var repository = await LocalRepository.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "tracked.txt"), "approved\n");
        var observed = await repository.ReadStateAsync();
        var marker = Path.Combine(repository.Root, ".git", "capture-path");
        var client = new GitRepositoryClient(new CaptureScriptExecutor(repository.Executor, script =>
            script.Replace("cp -- \"$root/$path\" \"$temporary/candidate\"",
                "cp -- \"$root/$path\" \"$temporary/candidate\"\n"
                + "printf '%s' \"$temporary\" > \"$root/.git/capture-path\"\n"
                + "sleep 60", StringComparison.Ordinal)), TimeProvider.System);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var operation = client.StageGovernedAsync(repository.Handle, observed.Guard,
            Assert.Single(observed.Snapshot.UnstagedChanges), cancellation.Token).AsTask();
        while (!File.Exists(marker))
        {
            await Task.Delay(10, cancellation.Token);
        }
        var capturePath = await File.ReadAllTextAsync(marker, cancellation.Token);
        Assert.True(File.Exists(Path.Combine(capturePath, "candidate")));
        await cancellation.CancelAsync();
        try
        {
            _ = await operation;
        }
        catch (OperationCanceledException)
        {
            // Both an explicit cancellation and a rejected terminated command
            // are acceptable; neither may leave the private candidate behind.
        }
        Assert.False(Directory.Exists(capturePath));
    }

    [Theory]
    [InlineData("worktree")]
    [InlineData("info")]
    [InlineData("global")]
    [InlineData("config")]
    public async Task GovernedStageRejectsChangedNormalizationInputs(string input)
    {
        await using var repository = await LocalRepository.CreateAsync();
        var attributes = input switch
        {
            "info" => Path.Combine(repository.Root, ".git", "info", "attributes"),
            "global" => Path.Combine(repository.Root, ".git", "global-attributes"),
            _ => Path.Combine(repository.Root, ".gitattributes"),
        };
        if (input == "global")
        {
            await repository.RunGitAsync("config", "core.attributesFile", attributes);
        }
        if (input != "config")
        {
            await File.WriteAllTextAsync(attributes, "*.txt text eol=lf\n");
        }
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "tracked.txt"), "approved\r\n");
        var observed = await repository.ReadStateAsync();
        if (input == "config")
        {
            await repository.RunGitAsync("config", "core.autocrlf", "true");
        }
        else
        {
            await File.WriteAllTextAsync(attributes, "*.txt -text\n");
        }

        var receipt = await repository.Client.StageGovernedAsync(repository.Handle, observed.Guard,
            Assert.Single(observed.Snapshot.UnstagedChanges, change => change.Path == "tracked.txt"), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Rejected, receipt.Disposition);
        Assert.Equal(string.Empty, await repository.RunGitOutputAsync("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task GovernedCaptureDoesNotExecuteFilterAddedAfterSnapshot()
    {
        await using var repository = await LocalRepository.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "tracked.txt"), "approved\n");
        var observed = await repository.ReadStateAsync();
        var client = new GitRepositoryClient(new CaptureScriptExecutor(repository.Executor, script =>
            script.Replace("# Normalize the approved capture using only its bound snapshot.",
                "run_git config --local filter.race.clean 'touch filter-executed; cat'\n"
                + "printf '*.txt filter=race\\n' > \"$root/.git/info/attributes\"\n"
                + "# Normalize the approved capture using only its bound snapshot.", StringComparison.Ordinal)), TimeProvider.System);

        var receipt = await client.StageGovernedAsync(repository.Handle, observed.Guard,
            Assert.Single(observed.Snapshot.UnstagedChanges), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Rejected, receipt.Disposition);
        Assert.False(File.Exists(Path.Combine(repository.Root, "filter-executed")));
        Assert.Equal(string.Empty, await repository.RunGitOutputAsync("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task GovernedStageUsesIndexAttributeFallbackAndCharsetNormalization()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var attributes = Path.Combine(repository.Root, ".gitattributes");
        await File.WriteAllTextAsync(attributes, "*.txt text eol=lf working-tree-encoding=UTF-16LE ident\n");
        await repository.RunGitAsync("add", ".gitattributes");
        await repository.RunGitAsync("commit", "-m", "encoding attributes");
        File.Delete(attributes);
        await File.WriteAllBytesAsync(Path.Combine(repository.Root, "tracked.txt"),
            System.Text.Encoding.Unicode.GetBytes("$Id: fixture $\r\nUnicode: λ\r\n"));
        var observed = await repository.ReadStateAsync();

        var receipt = await repository.Client.StageGovernedAsync(repository.Handle, observed.Guard,
            Assert.Single(observed.Snapshot.UnstagedChanges, change => change.Path == "tracked.txt"), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Succeeded, receipt.Disposition);
        Assert.Equal("$Id$\nUnicode: λ", await repository.RunGitOutputAsync("show", ":tracked.txt"));
    }

    [Fact]
    public async Task GovernedStagePreservesNewlineAndSpaceInFileName()
    {
        await using var repository = await LocalRepository.CreateAsync();
        const string name = "directory\n/file with\nnewline.txt";
        Directory.CreateDirectory(Path.Combine(repository.Root, "directory\n"));
        await File.WriteAllTextAsync(Path.Combine(repository.Root, name), "approved\n");
        var observed = await repository.ReadStateAsync();

        var receipt = await repository.Client.StageGovernedAsync(repository.Handle, observed.Guard,
            Assert.Single(observed.Snapshot.UnstagedChanges), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Succeeded, receipt.Disposition);
        Assert.Equal("approved", await repository.RunGitOutputAsync("show", ":" + name));
    }

    [Fact]
    public async Task GovernedStagePreservesIndexModeWhenFileModeIsIgnored()
    {
        await using var repository = await LocalRepository.CreateAsync();
        await repository.RunGitAsync("config", "core.filemode", "false");
        var path = Path.Combine(repository.Root, "tracked.txt");
        await File.WriteAllTextAsync(path, "approved\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var observed = await repository.ReadStateAsync();

        var receipt = await repository.Client.StageGovernedAsync(repository.Handle, observed.Guard,
            Assert.Single(observed.Snapshot.UnstagedChanges), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Succeeded, receipt.Disposition);
        Assert.StartsWith("100644 ", await repository.RunGitOutputAsync("ls-files", "--stage", "tracked.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedCaptureDoesNotWriteRacedOutsideContentToSharedObjects()
    {
        await using var repository = await LocalRepository.CreateAsync();
        await using var outside = await LocalRepository.CreateAsync();
        var outsidePath = Path.Combine(outside.Root, "tracked.txt");
        await File.WriteAllTextAsync(outsidePath, "outside-private-fixture-" + Guid.NewGuid());
        var outsideObject = await repository.RunGitOutputAsync("hash-object", "--no-filters", "--", outsidePath);
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "tracked.txt"), "approved\n");
        var observed = await repository.ReadStateAsync();
        var escapedOutside = outsidePath.Replace("'", "'\\''", StringComparison.Ordinal);
        var client = new GitRepositoryClient(new CaptureScriptExecutor(repository.Executor, script =>
            script.Replace("elif [ -f \"$root/$path\" ]; then",
                "elif [ -f \"$root/$path\" ]; then\n"
                + "rm -f \"$root/$path\"\n"
                + $"ln -s '{escapedOutside}' \"$root/$path\"", StringComparison.Ordinal)), TimeProvider.System);

        var receipt = await client.StageGovernedAsync(repository.Handle, observed.Guard,
            Assert.Single(observed.Snapshot.UnstagedChanges), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Rejected, receipt.Disposition);
        Assert.Equal(string.Empty, await repository.RunGitOutputAsync("diff", "--cached", "--name-only"));
        var lookup = await repository.Executor.ExecuteAsync(new ConnectionCommand(BuiltInConnections.Local,
            "git", ["-C", repository.Root, "cat-file", "-e", outsideObject], TimeSpan.FromSeconds(10), 1024),
            CancellationToken.None);
        Assert.Equal(ConnectionCommandOutcome.Exited, lookup.Outcome);
        Assert.NotEqual(0, lookup.ExitCode);
    }

    [Fact]
    public async Task GovernedStageRetainsApprovedBytesWhenWorkingFileChangesAtIndexDispatch()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var path = Path.Combine(repository.Root, "tracked.txt");
        await File.WriteAllTextAsync(path, "approved content\n");
        var observed = await repository.ReadStateAsync();
        var client = new GitRepositoryClient(
            new BeforeIndexWriteExecutor(repository.Executor,
                () => File.WriteAllTextAsync(path, "unapproved concurrent content\n")),
            TimeProvider.System);

        var receipt = await client.StageGovernedAsync(repository.Handle,
            observed.Guard, Assert.Single(observed.Snapshot.UnstagedChanges), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.OutcomeUnknown, receipt.Disposition);
        Assert.Equal("approved content", await repository.RunGitOutputAsync("show", ":tracked.txt"));
        Assert.Equal("unapproved concurrent content\n", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GovernedStagePreservesExecutableModeAndBuiltInTextNormalization(bool executable)
    {
        await using var repository = await LocalRepository.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(repository.Root, ".gitattributes"), "*.txt text eol=lf\n");
        await repository.RunGitAsync("add", ".gitattributes");
        await repository.RunGitAsync("commit", "-m", "text attributes");
        var path = Path.Combine(repository.Root, "tracked.txt");
        await File.WriteAllTextAsync(path, "CRLF content\r\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | (executable ? UnixFileMode.UserExecute : 0));
        var observed = await repository.ReadStateAsync();

        var receipt = await repository.Client.StageGovernedAsync(repository.Handle,
            observed.Guard, Assert.Single(observed.Snapshot.UnstagedChanges), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Succeeded, receipt.Disposition);
        Assert.Equal("CRLF content", await repository.RunGitOutputAsync("show", ":tracked.txt"));
        Assert.StartsWith(executable ? "100755 " : "100644 ",
            await repository.RunGitOutputAsync("ls-files", "--stage", "tracked.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GovernedStageCapturesSymlinkTargetWithoutFollowingIt()
    {
        await using var repository = await LocalRepository.CreateAsync();
        File.CreateSymbolicLink(Path.Combine(repository.Root, "link.txt"), "tracked.txt");
        var observed = await repository.ReadStateAsync();

        var receipt = await repository.Client.StageGovernedAsync(repository.Handle,
            observed.Guard, Assert.Single(observed.Snapshot.UnstagedChanges), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Succeeded, receipt.Disposition);
        Assert.Equal("tracked.txt", await repository.RunGitOutputAsync("show", ":link.txt"));
        Assert.StartsWith("120000 ", await repository.RunGitOutputAsync("ls-files", "--stage", "link.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GovernedStageCapturesSubmoduleCommit()
    {
        await using var repository = await LocalRepository.CreateAsync();
        await using var source = await LocalRepository.CreateAsync();
        await repository.RunGitAsync("-c", "protocol.file.allow=always", "submodule", "add", source.Root, "module");
        await repository.RunGitAsync("commit", "-m", "add module");
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "module", "tracked.txt"), "module change\n");
        await repository.RunGitAsync("-C", "module", "add", "tracked.txt");
        await repository.RunGitAsync("-C", "module", "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "module change");
        var expectedCommit = await repository.RunGitOutputAsync("-C", "module", "rev-parse", "HEAD");
        var observed = await repository.ReadStateAsync();

        var receipt = await repository.Client.StageGovernedAsync(repository.Handle,
            observed.Guard, Assert.Single(observed.Snapshot.UnstagedChanges), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Succeeded, receipt.Disposition);
        Assert.StartsWith($"160000 {expectedCommit} ",
            await repository.RunGitOutputAsync("ls-files", "--stage", "module"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GovernedStagePreservesDeletion()
    {
        await using var repository = await LocalRepository.CreateAsync();
        File.Delete(Path.Combine(repository.Root, "tracked.txt"));
        var observed = await repository.ReadStateAsync();

        var receipt = await repository.Client.StageGovernedAsync(repository.Handle,
            observed.Guard, Assert.Single(observed.Snapshot.UnstagedChanges), CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Succeeded, receipt.Disposition);
        Assert.Equal(string.Empty, await repository.RunGitOutputAsync("ls-files", "--stage", "tracked.txt"));
    }

    [Fact]
    public async Task PassiveStatusDoesNotExecuteRepositoryFsmonitor()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var marker = Path.Combine(repository.Root, "fsmonitor-ran");
        var hook = Path.Combine(repository.Root, ".git", "fsmonitor-test");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await repository.RunGitAsync("config", "core.fsmonitor", hook);

        var workingSet = await repository.Client.ReadWorkingSetAsync(
            repository.Handle, 1, CancellationToken.None);
        var snapshot = await repository.Client.ReadSnapshotAsync(
            repository.Handle, 2, CancellationToken.None);

        Assert.IsType<GitResult<GitWorkingSet>.Success>(workingSet);
        Assert.IsType<GitResult<GitRepositorySnapshot>.Success>(snapshot);
        Assert.False(File.Exists(marker));
        // A deliberate human Git operation still uses the configured hook.
        await repository.RunGitAsync("status", "--porcelain");
        Assert.True(File.Exists(marker));
    }

    [Theory]
    [InlineData("tracked.txt")]
    [InlineData("untracked.txt")]
    public async Task GovernedStageRejectsContentRewrittenWithTheSameStatus(string path)
    {
        await using var repository = await LocalRepository.CreateAsync();
        var fullPath = Path.Combine(repository.Root, path);
        await File.WriteAllTextAsync(fullPath, "observed content\n");
        var observed = await repository.ReadStateAsync();
        var selected = Assert.Single(observed.Snapshot.UnstagedChanges);
        await File.WriteAllTextAsync(fullPath, "replaced content\n");
        var changed = await repository.ReadStateAsync();
        Assert.Equal(selected, Assert.Single(changed.Snapshot.UnstagedChanges));
        Assert.NotEqual(observed.Guard.WorktreeDigest, changed.Guard.WorktreeDigest, StringComparer.Ordinal);

        var receipt = await repository.Client.StageGovernedAsync(
            repository.Handle, observed.Guard, selected, CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Rejected, receipt.Disposition);
        Assert.Equal("git_state_changed", receipt.StableCode);
        Assert.Equal(string.Empty, await repository.RunGitOutputAsync("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task GovernedStateBindsHeadIndexWorktreeAndRefs()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var first = await repository.ReadStateAsync();
        var repeated = await repository.ReadStateAsync();

        Assert.Equal(first.Guard, repeated.Guard);

        await File.WriteAllTextAsync(
            Path.Combine(repository.Root, "untracked.txt"),
            "new content\n");
        var changed = await repository.ReadStateAsync();

        Assert.NotEqual(first.Guard, changed.Guard);
        Assert.NotEqual(
            first.Guard.WorktreeDigest,
            changed.Guard.WorktreeDigest,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task GovernedBranchCreationDoesNotSwitchHeadAndRejectsStaleState()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var initial = await repository.ReadStateAsync();

        var created = await repository.Client.CreateBranchGovernedAsync(
            repository.Handle,
            initial.Guard,
            "feature/governed",
            CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Succeeded, created.Disposition);
        Assert.Equal("main", created.State?.HeadFullName?["refs/heads/".Length..]);
        Assert.Equal(initial.Guard.HeadSha, created.HeadSha);
        Assert.Equal("feature/governed", created.BranchName);

        var stale = await repository.Client.CreateBranchGovernedAsync(
            repository.Handle,
            initial.Guard,
            "feature/stale",
            CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Rejected, stale.Disposition);
        Assert.Equal("git_state_changed", stale.StableCode);
    }

    [Fact]
    public async Task GovernedCommitDisablesHooksAndReturnsProvenReceipt()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var marker = Path.Combine(repository.Root, "hook-ran");
        var hook = Path.Combine(repository.Root, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\ntouch '{marker}'\nexit 41\n");
        File.SetUnixFileMode(
            hook,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var changedPath = Path.Combine(repository.Root, "tracked.txt");
        await File.AppendAllTextAsync(changedPath, "governed change\n");
        var beforeStage = await repository.ReadStateAsync();
        var selectedChange = Assert.Single(
            beforeStage.Snapshot.UnstagedChanges,
            change => string.Equals(
                change.Path,
                "tracked.txt",
                StringComparison.Ordinal));
        var staged = await repository.Client.StageGovernedAsync(
            repository.Handle,
            beforeStage.Guard,
            selectedChange,
            CancellationToken.None);
        Assert.Equal(GitGovernedMutationDisposition.Succeeded, staged.Disposition);
        var expectedParent = beforeStage.Guard.HeadSha;
        var expectedTree = await repository.RunGitOutputAsync("write-tree");

        var committed = await repository.Client.CommitGovernedAsync(
            repository.Handle,
            Assert.IsType<GitRepositoryGuard>(staged.State),
            "Governed commit",
            "Fixed message body",
            CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Succeeded, committed.Disposition);
        Assert.Equal("git_commit_succeeded", committed.StableCode);
        Assert.Equal(expectedParent, committed.ParentSha);
        Assert.Equal(expectedTree, committed.TreeSha);
        Assert.Equal(40, committed.HeadSha?.Length);
        Assert.Equal(40, committed.ParentSha?.Length);
        Assert.Equal(40, committed.TreeSha?.Length);
        Assert.Equal("main", committed.BranchName);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task GovernedHttpsReadIsSealedAndPushIsWithheld()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var initial = await repository.ReadStateAsync();
        await repository.RunGitAsync(
            "remote",
            "add",
            "origin",
            "https://example.invalid/ghostshell.git");
        var transport = new HttpsRemoteExecutor(
            repository.Executor,
            Assert.IsType<string>(initial.Guard.HeadSha));
        var client = new GitRepositoryClient(transport, TimeProvider.System);
        var observed = await client.ReadGovernedRemoteRefAsync(
            repository.Handle,
            "origin",
            "main",
            CancellationToken.None);
        var remote = Assert.IsType<GitResult<GitGovernedRemoteRef>.Success>(observed).Value;

        var pushed = await client.PushGovernedAsync(
            repository.Handle,
            new GitGovernedPushRequest(
                initial.Guard,
                "origin",
                "main",
                "main",
                Assert.IsType<string>(initial.Guard.HeadSha),
                remote.Sha),
            CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.Rejected, pushed.Disposition);
        Assert.Equal("git_push_transport_unavailable", pushed.StableCode);
        Assert.False(new GitPanelSessionFactory(
                client,
                new GitRepositoryMutationCoordinator(),
                TimeProvider.System).Capabilities.Contains(SessionCapabilities.GitPush));
        var remoteCommand = Assert.Single(transport.RemoteCommands);
        var workingDirectoryOption = Array.FindIndex(
            remoteCommand.Arguments.ToArray(),
            argument => string.Equals(argument, "-C", StringComparison.Ordinal));
        Assert.True(workingDirectoryOption >= 0);
        Assert.Equal("/", remoteCommand.Arguments[workingDirectoryOption + 1]);
        Assert.DoesNotContain(
            repository.Root,
            remoteCommand.Arguments,
            StringComparer.Ordinal);
        Assert.Contains(
            "http.extraHeader=",
            remoteCommand.Arguments,
            StringComparer.Ordinal);
        Assert.Contains(
            "http.https://example.invalid/ghostshell.git.extraHeader=",
            remoteCommand.Arguments,
            StringComparer.Ordinal);
        Assert.Contains(
            "http.followRedirects=false",
            remoteCommand.Arguments,
            StringComparer.Ordinal);
        Assert.Contains(
            "http.https://example.invalid/ghostshell.git.followRedirects=false",
            remoteCommand.Arguments,
            StringComparer.Ordinal);
        Assert.Contains("HTTPS_PROXY", remoteCommand.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(
            remoteCommand.Arguments,
            argument => argument.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("cookie.txt", StringComparison.Ordinal)
                || argument.Contains("client.pem", StringComparison.Ordinal)
                || argument.Contains("proxy.invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Governed_https_read_preserves_the_workspace_proxy_after_scrubbing_untrusted_configuration()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var initial = await repository.ReadStateAsync();
        await repository.RunGitAsync("remote", "add", "origin", "https://example.invalid/ghostshell.git");
        var transport = new HttpsRemoteExecutor(repository.Executor, Assert.IsType<string>(initial.Guard.HeadSha));
        var client = new GitRepositoryClient(transport, TimeProvider.System, new WorkspaceConnector());

        var result = await client.ReadGovernedRemoteRefAsync(repository.Handle, "origin", "main", CancellationToken.None);

        Assert.IsType<GitResult<GitGovernedRemoteRef>.Success>(result);
        var command = Assert.Single(transport.RemoteCommands);
        Assert.DoesNotContain("ALL_PROXY", command.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("HTTPS_PROXY", command.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("http.proxy=", command.Arguments, StringComparer.Ordinal);
        Assert.Contains("GIT_CONFIG_GLOBAL=/dev/null", command.Arguments, StringComparer.Ordinal);
        Assert.Contains("http.followRedirects=false", command.Arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("workspace-password", StringComparison.Ordinal));
    }

    private sealed class WorkspaceConnector : IWorkspaceNetworkConnector
    {
        public WorkspaceNetworkEgress Egress => WorkspaceNetworkEgress.Direct;
        public Uri LocalProxyEndpoint => new("socks5://127.0.0.1:45123");
        public WorkspaceNetworkProxyCredentials LocalProxyCredentials => new("workspace", "workspace-password");
        public ValueTask<Stream> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task GovernedPushRejectsLocalRemoteBeforeClientOrServerHooks()
    {
        await using var repository = await LocalRepository.CreateAsync(withBareRemote: true);
        var clientMarker = Path.Combine(repository.Root, "pre-push-ran");
        var serverMarker = Path.Combine(repository.Root, "pre-receive-ran");
        await WriteExecutableAsync(
            Path.Combine(repository.Root, ".git", "hooks", "pre-push"),
            $"#!/bin/sh\ntouch '{clientMarker}'\nexit 47\n");
        await WriteExecutableAsync(
            Path.Combine(Assert.IsType<string>(repository.BareRemote), "hooks", "pre-receive"),
            $"#!/bin/sh\ntouch '{serverMarker}'\nexit 47\n");
        var state = await repository.ReadStateAsync();

        var observed = await repository.Client.ReadGovernedRemoteRefAsync(
            repository.Handle,
            "origin",
            "main",
            CancellationToken.None);
        var pushed = await repository.Client.PushGovernedAsync(
            repository.Handle,
            new GitGovernedPushRequest(
                state.Guard,
                "origin",
                "main",
                "main",
                Assert.IsType<string>(state.Guard.HeadSha),
                ExpectedRemoteSha: null),
            CancellationToken.None);

        Assert.Equal(
            GitErrorCode.Unsupported,
            Assert.IsType<GitResult<GitGovernedRemoteRef>.Failure>(observed).Error.Code);
        Assert.Equal(GitGovernedMutationDisposition.Rejected, pushed.Disposition);
        Assert.False(File.Exists(clientMarker));
        Assert.False(File.Exists(serverMarker));
    }

    [Fact]
    public async Task GovernedStageRejectsConcurrentUnrelatedIndexWrite()
    {
        await using var repository = await LocalRepository.CreateAsync();
        await File.AppendAllTextAsync(
            Path.Combine(repository.Root, "tracked.txt"),
            "selected\n");
        await File.WriteAllTextAsync(
            Path.Combine(repository.Root, "other.txt"),
            "other\n");
        var state = await repository.ReadStateAsync();
        var selected = Assert.Single(
            state.Snapshot.UnstagedChanges,
            change => string.Equals(change.Path, "tracked.txt", StringComparison.Ordinal));
        var racing = new AfterCommandExecutor(
            repository.Executor,
            "update-index",
            () => new ValueTask(repository.RunGitAsync("add", "--", "other.txt")));
        var client = new GitRepositoryClient(racing, TimeProvider.System);

        var receipt = await client.StageGovernedAsync(
            repository.Handle,
            state.Guard,
            selected,
            CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.OutcomeUnknown, receipt.Disposition);
    }

    [Fact]
    public async Task GovernedUnstageRejectsConcurrentUnrelatedIndexWrite()
    {
        await using var repository = await LocalRepository.CreateAsync();
        await File.AppendAllTextAsync(
            Path.Combine(repository.Root, "tracked.txt"),
            "selected\n");
        await repository.RunGitAsync("add", "--", "tracked.txt");
        await File.WriteAllTextAsync(
            Path.Combine(repository.Root, "other.txt"),
            "other\n");
        var state = await repository.ReadStateAsync();
        var selected = Assert.Single(
            state.Snapshot.StagedChanges,
            change => string.Equals(change.Path, "tracked.txt", StringComparison.Ordinal));
        var racing = new AfterCommandExecutor(
            repository.Executor,
            "reset",
            () => new ValueTask(repository.RunGitAsync("add", "--", "other.txt")));
        var client = new GitRepositoryClient(racing, TimeProvider.System);

        var receipt = await client.UnstageGovernedAsync(
            repository.Handle,
            state.Guard,
            selected,
            CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.OutcomeUnknown, receipt.Disposition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GovernedCommitRejectsPostDispatchCommitObjectRace(bool amendTree)
    {
        await using var repository = await LocalRepository.CreateAsync();
        await File.AppendAllTextAsync(
            Path.Combine(repository.Root, "tracked.txt"),
            "selected\n");
        await repository.RunGitAsync("add", "--", "tracked.txt");
        var state = await repository.ReadStateAsync();
        var racing = new AfterCommandExecutor(
            repository.Executor,
            "commit",
            async () =>
            {
                if (amendTree)
                {
                    await File.AppendAllTextAsync(
                        Path.Combine(repository.Root, "tracked.txt"),
                        "concurrent tree\n");
                    await repository.RunGitAsync("add", "--", "tracked.txt");
                    await repository.RunGitAsync("commit", "--amend", "--no-edit");
                    return;
                }

                await repository.RunGitAsync(
                    "commit",
                    "--allow-empty",
                    "-m",
                    "Concurrent commit");
            });
        var client = new GitRepositoryClient(racing, TimeProvider.System);

        var receipt = await client.CommitGovernedAsync(
            repository.Handle,
            state.Guard,
            "Governed commit",
            body: null,
            CancellationToken.None);

        Assert.Equal(GitGovernedMutationDisposition.OutcomeUnknown, receipt.Disposition);
    }

    [Theory]
    [InlineData("filter.evil.clean")]
    [InlineData("filter.evil.smudge")]
    [InlineData("filter.evil.process")]
    [InlineData("diff.evil.command")]
    [InlineData("diff.evil.textconv")]
    public async Task GovernedStateRejectsExecutableRepositoryConfiguration(
        string key)
    {
        await using var repository = await LocalRepository.CreateAsync();
        var marker = Path.Combine(repository.Root, "executable-config-ran");
        var executable = Path.Combine(repository.Root, "executable-config.sh");
        await WriteExecutableAsync(
            executable,
            $"#!/bin/sh\ntouch '{marker}'\ncat\n");
        await repository.RunGitAsync("config", key, executable);

        var result = await repository.Client.ReadGovernedStateAsync(
            repository.Handle,
            generation: 10,
            CancellationToken.None);

        Assert.Equal(
            GitErrorCode.Unsupported,
            Assert.IsType<GitResult<GitGovernedState>.Failure>(result).Error.Code);
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData("global")]
    [InlineData("system")]
    public async Task GovernedStateIgnoresExecutableConfigurationOutsideTheRepository(
        string scope)
    {
        await using var repository = await LocalRepository.CreateAsync();
        var executor = new AdditionalConfigurationExecutor(
            repository.Executor,
            $"{scope}\0filter.lfs.process\ngit-lfs filter-process\0");
        var client = new GitRepositoryClient(executor, TimeProvider.System);

        var result = await client.ReadGovernedStateAsync(
            repository.Handle,
            generation: 10,
            CancellationToken.None);

        Assert.IsType<GitResult<GitGovernedState>.Success>(result);
    }

    [Fact]
    public async Task GovernedStateRejectsExecutableWorktreeConfiguration()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var executor = new AdditionalConfigurationExecutor(
            repository.Executor,
            "worktree\0filter.evil.process\nevil-filter\0");
        var client = new GitRepositoryClient(executor, TimeProvider.System);

        var result = await client.ReadGovernedStateAsync(
            repository.Handle,
            generation: 10,
            CancellationToken.None);

        Assert.Equal(
            GitErrorCode.Unsupported,
            Assert.IsType<GitResult<GitGovernedState>.Failure>(result).Error.Code);
    }

    [Fact]
    public async Task GovernedStateSuppressesConfiguredFsmonitorHook()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var marker = Path.Combine(repository.Root, "fsmonitor-ran");
        var executable = Path.Combine(repository.Root, "fsmonitor.sh");
        await WriteExecutableAsync(
            executable,
            $"#!/bin/sh\ntouch '{marker}'\nexit 1\n");
        await repository.RunGitAsync("config", "core.fsmonitor", executable);

        var result = await repository.Client.ReadGovernedStateAsync(
            repository.Handle,
            generation: 11,
            CancellationToken.None);

        Assert.IsType<GitResult<GitGovernedState>.Success>(result);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task GovernedRemoteRejectsHelpersAndSshCommandsBeforeExecution()
    {
        await using var repository = await LocalRepository.CreateAsync();
        var marker = Path.Combine(repository.Root, "transport-ran");
        var transport = Path.Combine(repository.Root, "transport.sh");
        await WriteExecutableAsync(transport, $"#!/bin/sh\ntouch '{marker}'\nexit 1\n");
        await repository.RunGitAsync("remote", "add", "sshremote", "ssh://example.invalid/repo");
        await repository.RunGitAsync("remote", "add", "helper", $"ext::{transport}");
        await repository.RunGitAsync("config", "core.sshCommand", transport);

        var ssh = await repository.Client.ReadGovernedRemoteRefAsync(
            repository.Handle,
            "sshremote",
            "main",
            CancellationToken.None);
        await repository.RunGitAsync("config", "--unset", "core.sshCommand");
        var helper = await repository.Client.ReadGovernedRemoteRefAsync(
            repository.Handle,
            "helper",
            "main",
            CancellationToken.None);

        Assert.Equal(
            GitErrorCode.Unsupported,
            Assert.IsType<GitResult<GitGovernedRemoteRef>.Failure>(ssh).Error.Code);
        Assert.Equal(
            GitErrorCode.Unsupported,
            Assert.IsType<GitResult<GitGovernedRemoteRef>.Failure>(helper).Error.Code);
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData("http.https://example.invalid/.extraHeader", "Authorization: Bearer secret")]
    [InlineData("http.cookieFile", "/tmp/cookie.txt")]
    [InlineData("http.sslCert", "/tmp/client.pem")]
    [InlineData("http.sslKey", "/tmp/client.key")]
    [InlineData("http.proxy", "https://proxy.invalid")]
    [InlineData("http.proxySSLCert", "/tmp/proxy-client.pem")]
    [InlineData("http.proxySSLKey", "/tmp/proxy-client.key")]
    [InlineData("credential.https://example.invalid.helper", "!credential-command")]
    [InlineData("credential.https://example.invalid.username", "injected-user")]
    [InlineData("remote.origin.proxy", "https://proxy.invalid")]
    public async Task GovernedRemoteRejectsRepositoryHttpIdentityConfiguration(
        string key,
        string value)
    {
        await using var repository = await LocalRepository.CreateAsync();
        var state = await repository.ReadStateAsync();
        await repository.RunGitAsync(
            "remote",
            "add",
            "origin",
            "https://example.invalid/ghostshell.git");
        await repository.RunGitAsync("config", "--local", key, value);
        var transport = new HttpsRemoteExecutor(
            repository.Executor,
            Assert.IsType<string>(state.Guard.HeadSha));
        var client = new GitRepositoryClient(transport, TimeProvider.System);

        var observed = await client.ReadGovernedRemoteRefAsync(
            repository.Handle,
            "origin",
            "main",
            CancellationToken.None);

        Assert.Equal(
            GitErrorCode.Unsupported,
            Assert.IsType<GitResult<GitGovernedRemoteRef>.Failure>(observed).Error.Code);
        Assert.Empty(transport.RemoteCommands);
    }

    [Fact]
    public async Task PostDispatchExceptionQuarantinesSessionAndInvalidatesReferences()
    {
        await using var repository = await LocalRepository.CreateAsync();
        await File.AppendAllTextAsync(
            Path.Combine(repository.Root, "tracked.txt"),
            "pending\n");
        var throwing = new ThrowAfterMutationExecutor(repository.Executor);
        var client = new GitRepositoryClient(throwing, TimeProvider.System);
        var factory = new GitPanelSessionFactory(
            client,
            new GitRepositoryMutationCoordinator(),
            TimeProvider.System);
        await using var session = await factory.CreateAsync(
            new SessionId("quarantine-session"),
            new GitSessionTarget(repository.Handle, bindingRevision: 1),
            CancellationToken.None);
        var observed = Assert.IsType<GitAgentOperationResult.State>(
            await session.ReadStateAsync(CancellationToken.None)).Value;
        var change = Assert.Single(
            observed.Changes,
            item => item.Area == GitChangeArea.Unstaged
                && string.Equals(item.DisplayPath, "tracked.txt", StringComparison.Ordinal));
        throwing.ThrowAfterMutation = true;

        var uncertain = await session.StageAsync(
            Assert.IsType<GitStateReferenceId>(observed.StateReference),
            change.Reference,
            CancellationToken.None);

        Assert.IsType<GitAgentOperationResult.OutcomeUnknown>(uncertain);
        Assert.True(session.State.Metadata.MutationsQuarantined);
        throwing.ThrowAfterMutation = false;
        _ = Assert.IsType<GitAgentOperationResult.State>(
            await session.ReadStateAsync(CancellationToken.None));
        var stale = await session.StageAsync(
            Assert.IsType<GitStateReferenceId>(observed.StateReference),
            change.Reference,
            CancellationToken.None);
        Assert.Equal(
            "git_reference_expired",
            Assert.IsType<GitAgentOperationResult.Rejected>(stale).StableCode);
    }

    private static async Task WriteExecutableAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class CaptureScriptExecutor(IConnectionCommandExecutor inner, Func<string, string> transform)
        : IConnectionCommandExecutor
    {
        public ValueTask<ConnectionCommandResult> ExecuteAsync(ConnectionCommand request, CancellationToken cancellationToken)
        {
            if (request.Arguments.Contains("ghostshell-worktree-capture", StringComparer.Ordinal)
                && request.Arguments.Contains("write", StringComparer.Ordinal))
            {
                request = new ConnectionCommand(request.Connection, request.Executable,
                    [.. request.Arguments.Select(argument => argument.Contains("run_git()", StringComparison.Ordinal)
                        ? transform(argument) : argument)], request.Timeout, request.MaximumOutputCharacters);
            }
            return inner.ExecuteAsync(request, cancellationToken);
        }

        public ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(ConnectionBinaryCommand request, CancellationToken cancellationToken) =>
            inner.ExecuteBinaryAsync(request, cancellationToken);

        public ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(ConnectionBinaryCommand request,
            Func<Stream, CancellationToken, ValueTask<T>> consumeOutput, CancellationToken cancellationToken) =>
            inner.ExecuteStreamingAsync(request, consumeOutput, cancellationToken);
    }

    private sealed class BeforeIndexWriteExecutor(IConnectionCommandExecutor inner, Func<Task> change)
        : IConnectionCommandExecutor
    {
        public async ValueTask<ConnectionCommandResult> ExecuteAsync(ConnectionCommand request, CancellationToken cancellationToken)
        {
            if (request.Arguments.Contains("update-index", StringComparer.Ordinal))
            {
                await change();
            }
            return await inner.ExecuteAsync(request, cancellationToken);
        }

        public ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(ConnectionBinaryCommand request, CancellationToken cancellationToken) =>
            inner.ExecuteBinaryAsync(request, cancellationToken);

        public ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(ConnectionBinaryCommand request,
            Func<Stream, CancellationToken, ValueTask<T>> consumeOutput, CancellationToken cancellationToken) =>
            inner.ExecuteStreamingAsync(request, consumeOutput, cancellationToken);
    }

    private sealed class AfterCommandExecutor(
        IConnectionCommandExecutor inner,
        string trigger,
        Func<ValueTask> afterCommand) : IConnectionCommandExecutor
    {
        private int _fired;

        public async ValueTask<ConnectionCommandResult> ExecuteAsync(
            ConnectionCommand request,
            CancellationToken cancellationToken)
        {
            var result = await inner.ExecuteAsync(request, cancellationToken);
            if (result.Outcome == ConnectionCommandOutcome.Exited
                && result.ExitCode == 0
                && request.Arguments.Contains(trigger, StringComparer.Ordinal)
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await afterCommand();
            }

            return result;
        }

        public ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(
            ConnectionBinaryCommand request,
            CancellationToken cancellationToken) =>
            inner.ExecuteBinaryAsync(request, cancellationToken);

        public ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(
            ConnectionBinaryCommand request,
            Func<Stream, CancellationToken, ValueTask<T>> consumeOutput,
            CancellationToken cancellationToken) =>
            inner.ExecuteStreamingAsync(request, consumeOutput, cancellationToken);
    }

    private sealed class AdditionalConfigurationExecutor(
        IConnectionCommandExecutor inner,
        string additionalConfiguration) : IConnectionCommandExecutor
    {
        public async ValueTask<ConnectionCommandResult> ExecuteAsync(
            ConnectionCommand request,
            CancellationToken cancellationToken)
        {
            var result = await inner.ExecuteAsync(request, cancellationToken);
            return request.Arguments.Contains("--show-scope", StringComparer.Ordinal)
                ? result with
                {
                    StandardOutput = result.StandardOutput + additionalConfiguration,
                }
                : result;
        }

        public ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(
            ConnectionBinaryCommand request,
            CancellationToken cancellationToken) =>
            inner.ExecuteBinaryAsync(request, cancellationToken);

        public ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(
            ConnectionBinaryCommand request,
            Func<Stream, CancellationToken, ValueTask<T>> consumeOutput,
            CancellationToken cancellationToken) =>
            inner.ExecuteStreamingAsync(request, consumeOutput, cancellationToken);
    }

    private sealed class HttpsRemoteExecutor(
        IConnectionCommandExecutor inner,
        string initialRemoteSha) : IConnectionCommandExecutor
    {
        private readonly string _remoteSha = initialRemoteSha;

        public List<ConnectionCommand> RemoteCommands { get; } = [];

        public ValueTask<ConnectionCommandResult> ExecuteAsync(
            ConnectionCommand request,
            CancellationToken cancellationToken)
        {
            if (request.Arguments.Contains("ls-remote", StringComparer.Ordinal))
            {
                RemoteCommands.Add(request);
                var destination = request.Arguments[^1];
                return ValueTask.FromResult(new ConnectionCommandResult(
                    ConnectionCommandOutcome.Exited,
                    ExitCode: 0,
                    $"{_remoteSha}\t{destination}\n"));
            }

            return inner.ExecuteAsync(request, cancellationToken);
        }

        public ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(
            ConnectionBinaryCommand request,
            CancellationToken cancellationToken) =>
            inner.ExecuteBinaryAsync(request, cancellationToken);

        public ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(
            ConnectionBinaryCommand request,
            Func<Stream, CancellationToken, ValueTask<T>> consumeOutput,
            CancellationToken cancellationToken) =>
            inner.ExecuteStreamingAsync(request, consumeOutput, cancellationToken);
    }

    private sealed class ThrowAfterMutationExecutor(IConnectionCommandExecutor inner)
        : IConnectionCommandExecutor
    {
        private int _throwNext;

        public bool ThrowAfterMutation { get; set; }

        public async ValueTask<ConnectionCommandResult> ExecuteAsync(
            ConnectionCommand request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _throwNext, 0) != 0)
            {
                throw new IOException("Simulated post-dispatch connection loss.");
            }

            var result = await inner.ExecuteAsync(request, cancellationToken);
            if (ThrowAfterMutation
                && request.Arguments.Contains("update-index", StringComparer.Ordinal))
            {
                Interlocked.Exchange(ref _throwNext, 1);
            }

            return result;
        }

        public ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(
            ConnectionBinaryCommand request,
            CancellationToken cancellationToken) =>
            inner.ExecuteBinaryAsync(request, cancellationToken);

        public ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(
            ConnectionBinaryCommand request,
            Func<Stream, CancellationToken, ValueTask<T>> consumeOutput,
            CancellationToken cancellationToken) =>
            inner.ExecuteStreamingAsync(request, consumeOutput, cancellationToken);
    }

    private sealed class LocalRepository : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly ConnectionCommandExecutor _executor;
        private long _generation;

        private LocalRepository(
            DirectoryInfo directory,
            ConnectionCommandExecutor executor,
            string? bareRemote)
        {
            _directory = directory;
            _executor = executor;
            BareRemote = bareRemote;
            Handle = new GitRepositoryHandle(BuiltInConnections.Local, directory.FullName);
            Client = new GitRepositoryClient(executor, TimeProvider.System);
        }

        public string Root => _directory.FullName;

        public string? BareRemote { get; }

        public GitRepositoryHandle Handle { get; }

        public GitRepositoryClient Client { get; }

        public IConnectionCommandExecutor Executor => _executor;

        public static async Task<LocalRepository> CreateAsync(bool withBareRemote = false)
        {
            if (!OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException(
                    "Governed Git integration tests require the supported macOS boundary.");
            }

            var directory = Directory.CreateTempSubdirectory("ghostshell-governed-git-");
            var executor = new ConnectionCommandExecutor(
                new LocalRuntime(),
                new PathConnectionExecutableLocator());
            string? bareRemote = null;
            var fixture = new LocalRepository(directory, executor, bareRemote);
            try
            {
                await fixture.RunGitAsync("init", "--initial-branch=main");
                await fixture.RunGitAsync("config", "user.name", "GhostShell Test");
                await fixture.RunGitAsync("config", "user.email", "ghostshell@example.invalid");
                await File.WriteAllTextAsync(
                    Path.Combine(fixture.Root, "tracked.txt"),
                    "initial\n");
                await fixture.RunGitAsync("add", "--", "tracked.txt");
                await fixture.RunGitAsync("commit", "-m", "Initial");
                if (withBareRemote)
                {
                    bareRemote = Path.Combine(fixture.Root, "remote.git");
                    await fixture.RunGitAsync("init", "--bare", bareRemote);
                    await fixture.RunGitAsync("remote", "add", "origin", bareRemote);
                    fixture = new LocalRepository(directory, executor, bareRemote);
                }

                return fixture;
            }
            catch
            {
                directory.Delete(recursive: true);
                throw;
            }
        }

        public async Task<GitGovernedState> ReadStateAsync()
        {
            var result = await Client.ReadGovernedStateAsync(
                Handle,
                Interlocked.Increment(ref _generation),
                CancellationToken.None);
            return Assert.IsType<GitResult<GitGovernedState>.Success>(result).Value;
        }

        public async Task RunGitAsync(params string[] arguments)
        {
            _ = await RunGitOutputAsync(arguments);
        }

        public async Task<string> RunGitOutputAsync(params string[] arguments)
        {
            var command = new List<string>
            {
                "--literal-pathspecs",
                "-C",
                Root,
            };
            command.AddRange(arguments);
            var result = await _executor.ExecuteAsync(
                new ConnectionCommand(
                    BuiltInConnections.Local,
                    "git",
                    command,
                    TimeSpan.FromSeconds(10),
                    1024 * 1024),
                CancellationToken.None);
            Assert.Equal(ConnectionCommandOutcome.Exited, result.Outcome);
            Assert.True(
                result.ExitCode == 0,
                $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
            return result.StandardOutput.Trim();
        }

        public ValueTask DisposeAsync()
        {
            _directory.Delete(recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LocalRuntime : IConnectionRuntime
    {
        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(BuiltInConnections.Local.Id, profile.Id);
            return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
                new ConnectionOpenPlan(
                    profile.Id,
                    ConnectionKind.Local,
                    new TerminalLaunchRequest(null),
                    ConnectionAuthenticationMode.None,
                    SshHostKeyPolicy.NotApplicable,
                    ConnectionReconnectMode.NotApplicable)));
        }

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
