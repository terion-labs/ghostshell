using System.Security.Cryptography;
using System.Text;

namespace Asura.Git;

public sealed partial class GitRepositoryClient
{
    private const int GovernedStateOutputLimit = 16 * 1024 * 1024;
    private static readonly string[] ClearedGitEnvironment =
    [
        "-u", "GIT_DIR",
        "-u", "GIT_WORK_TREE",
        "-u", "GIT_INDEX_FILE",
        "-u", "GIT_OBJECT_DIRECTORY",
        "-u", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "-u", "GIT_COMMON_DIR",
        "-u", "GIT_NAMESPACE",
        "-u", "GIT_EXEC_PATH",
        "-u", "GIT_SSH",
        "-u", "GIT_SSH_COMMAND",
        "-u", "GIT_PROXY_COMMAND",
        "-u", "GIT_EXTERNAL_DIFF",
        "-u", "GIT_DIFF_OPTS",
        "-u", "GIT_CONFIG",
        "-u", "GIT_CONFIG_PARAMETERS",
        "-u", "GIT_CONFIG_COUNT",
        "-u", "GIT_SSL_NO_VERIFY",
        "-u", "GIT_SSL_CERT",
        "-u", "GIT_SSL_KEY",
        "-u", "GIT_SSL_CERT_PASSWORD_PROTECTED",
        "-u", "GIT_SSL_CAINFO",
        "-u", "GIT_SSL_CAPATH",
        "-u", "GIT_HTTP_PROXY_AUTHMETHOD",
        "-u", "HTTP_PROXY",
        "-u", "HTTPS_PROXY",
        "-u", "ALL_PROXY",
        "-u", "NO_PROXY",
        "-u", "http_proxy",
        "-u", "https_proxy",
        "-u", "all_proxy",
        "-u", "no_proxy",
        "-u", "SSL_CERT_FILE",
        "-u", "SSL_CERT_DIR",
        "-u", "CURL_HOME",
        "-u", "NETRC",
    ];
    private static readonly string[] SealedGitEnvironment =
    [
        .. ClearedGitEnvironment,
        "GIT_CONFIG=/dev/null",
        "GIT_CONFIG_SYSTEM=/dev/null",
        "GIT_CONFIG_GLOBAL=/dev/null",
        "GIT_CONFIG_NOSYSTEM=1",
        "GIT_ATTR_NOSYSTEM=1",
        "GIT_TERMINAL_PROMPT=0",
        "GIT_ASKPASS=/usr/bin/false",
        "SSH_ASKPASS=/usr/bin/false",
    ];
    private static readonly string[] ConfigurationReadEnvironment =
    [
        .. ClearedGitEnvironment,
        "GIT_TERMINAL_PROMPT=0",
        "GIT_ASKPASS=/usr/bin/false",
        "SSH_ASKPASS=/usr/bin/false",
    ];
    private static readonly string[] SealedGitOptions =
    [
        "--literal-pathspecs",
        "--no-pager",
        "-c", "core.hooksPath=/dev/null",
        "-c", "core.fsmonitor=false",
        "-c", "credential.helper=",
        "-c", "protocol.ext.allow=never",
        "-c", "commit.gpgSign=false",
        "-c", "tag.gpgSign=false",
    ];

    public async ValueTask<GitResult<GitGovernedState>> ReadGovernedStateAsync(
        GitRepositoryHandle repository,
        long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        var configuration = await ValidateGovernedConfigurationAsync(
                repository,
                cancellationToken)
            .ConfigureAwait(false);
        if (configuration is GitResult<GitUnit>.Failure configurationFailure)
        {
            return new GitResult<GitGovernedState>.Failure(configurationFailure.Error);
        }

        var before = await ReadRepositoryGuardAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        if (before is GitResult<GitRepositoryGuard>.Failure beforeFailure)
        {
            return new GitResult<GitGovernedState>.Failure(beforeFailure.Error);
        }

        var snapshot = await ReadGovernedSnapshotAsync(
                repository,
                generation,
                cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is GitResult<GitRepositorySnapshot>.Failure snapshotFailure)
        {
            return new GitResult<GitGovernedState>.Failure(snapshotFailure.Error);
        }

        var after = await ReadRepositoryGuardAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        if (after is GitResult<GitRepositoryGuard>.Failure afterFailure)
        {
            return new GitResult<GitGovernedState>.Failure(afterFailure.Error);
        }

        var beforeGuard = ((GitResult<GitRepositoryGuard>.Success)before).Value;
        var afterGuard = ((GitResult<GitRepositoryGuard>.Success)after).Value;
        var value = ((GitResult<GitRepositorySnapshot>.Success)snapshot).Value;
        if (beforeGuard != afterGuard
            || !string.Equals(
                value.Head.CommitSha,
                afterGuard.HeadSha,
                StringComparison.Ordinal))
        {
            return Failure<GitGovernedState>(
                GitErrorCode.InvalidResponse,
                "The repository changed while governed state was captured.");
        }

        var mutationEligible = !value.HasConflicts
            && value.UnstagedChanges.Count <= 200
            && value.StagedChanges.Count <= 200
            && value.Refs.Count(item => item.Kind == GitRefKind.LocalBranch) <= 200
            && value.Remotes.Count <= 32;
        return new GitResult<GitGovernedState>.Success(
            new GitGovernedState(value, afterGuard, mutationEligible));
    }

    public async ValueTask<GitResult<GitGovernedRemoteRef>> ReadGovernedRemoteRefAsync(
        GitRepositoryHandle repository,
        string remoteName,
        string destinationBranch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ValidateRemoteOperand(remoteName, nameof(remoteName));
        var configuration = await ValidateGovernedConfigurationAsync(
                repository,
                cancellationToken)
            .ConfigureAwait(false);
        if (configuration is GitResult<GitUnit>.Failure configurationFailure)
        {
            return new GitResult<GitGovernedRemoteRef>.Failure(
                configurationFailure.Error);
        }

        if (!IsSafeBranchName(destinationBranch))
        {
            throw new ArgumentException(
                "A governed destination branch is invalid.",
                nameof(destinationBranch));
        }

        var remoteUrl = await ResolveGovernedRemoteUrlAsync(
                repository,
                remoteName,
                cancellationToken)
            .ConfigureAwait(false);
        if (remoteUrl is GitResult<string>.Failure remoteFailure)
        {
            return new GitResult<GitGovernedRemoteRef>.Failure(remoteFailure.Error);
        }

        var safeRemoteUrl = ((GitResult<string>.Success)remoteUrl).Value;
        var finalConfiguration = await ValidateGovernedConfigurationAsync(
                repository,
                cancellationToken)
            .ConfigureAwait(false);
        if (finalConfiguration is GitResult<GitUnit>.Failure finalFailure)
        {
            return new GitResult<GitGovernedRemoteRef>.Failure(finalFailure.Error);
        }

        var destination = $"refs/heads/{destinationBranch}";
        var result = await ExecuteGovernedRemoteReadAsync(
                repository,
                safeRemoteUrl,
                [
                    "ls-remote", "--refs",
                    safeRemoteUrl,
                    destination,
                ],
                NetworkTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is GitResult<CommandOutput>.Failure failure)
        {
            return new GitResult<GitGovernedRemoteRef>.Failure(
                failure.Error with { Retryable = false });
        }

        var text = Value(result).Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return new GitResult<GitGovernedRemoteRef>.Success(new GitGovernedRemoteRef(
                remoteName,
                destination,
                Sha: null,
                timeProvider.GetUtcNow()));
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1)
        {
            return Failure<GitGovernedRemoteRef>(
                GitErrorCode.InvalidResponse,
                "Git returned an ambiguous remote branch observation.");
        }

        var fields = lines[0].Split('\t');
        if (fields.Length != 2
            || !IsObjectId(fields[0])
            || !string.Equals(fields[1], destination, StringComparison.Ordinal))
        {
            return Failure<GitGovernedRemoteRef>(
                GitErrorCode.InvalidResponse,
                "Git returned an invalid remote branch observation.");
        }

        return new GitResult<GitGovernedRemoteRef>.Success(new GitGovernedRemoteRef(
            remoteName,
            destination,
            fields[0],
            timeProvider.GetUtcNow()));
    }

    public async ValueTask<GitResult<GitDiffDocument>> ReadGovernedDiffAsync(
        GitRepositoryHandle repository,
        GitDiffRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(request);
        var configuration = await ValidateGovernedConfigurationAsync(
                repository,
                cancellationToken)
            .ConfigureAwait(false);
        if (configuration is GitResult<GitUnit>.Failure configurationFailure)
        {
            return new GitResult<GitDiffDocument>.Failure(configurationFailure.Error);
        }

        var diffArguments = DiffArguments(request);
        var result = await ExecuteGovernedAsync(
                repository,
                [diffArguments[0], "--no-ext-diff", .. diffArguments.Skip(1)],
                DiffTimeout,
                DiffOutputLimit,
                cancellationToken,
                acceptExitOne: true,
                allowTruncated: true)
            .ConfigureAwait(false);
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
            return Failure<GitDiffDocument>(
                GitErrorCode.InvalidResponse,
                exception.Message);
        }
    }

    public ValueTask<GitGovernedMutationReceipt> StageGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        GitFileChange expectedChange,
        CancellationToken cancellationToken) =>
        MutateIndexGovernedAsync(
            repository,
            expectedState,
            expectedChange,
            GitChangeArea.Unstaged,
            ["add", "--", expectedChange.Path],
            "git_stage_failed",
            cancellationToken);

    public ValueTask<GitGovernedMutationReceipt> UnstageGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        GitFileChange expectedChange,
        CancellationToken cancellationToken) =>
        MutateIndexGovernedAsync(
            repository,
            expectedState,
            expectedChange,
            GitChangeArea.Staged,
            expectedState.HeadSha is null
                ? ["rm", "-q", "--cached", "--", expectedChange.Path]
                : ["reset", "-q", "HEAD", "--", expectedChange.Path],
            "git_unstage_failed",
            cancellationToken);

    public async ValueTask<GitGovernedMutationReceipt> CreateBranchGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        string name,
        CancellationToken cancellationToken)
    {
        if (!IsSafeBranchName(name))
        {
            throw new ArgumentException("A governed branch name is invalid.", nameof(name));
        }

        var exact = await ReadExactStateAsync(repository, expectedState, cancellationToken)
            .ConfigureAwait(false);
        if (exact is null)
        {
            return Rejected("git_state_changed");
        }

        if (!IsCleanAttachedBorn(exact))
        {
            return Rejected("git_branch_precondition_failed");
        }

        var nameCheck = await ExecuteGovernedAsync(
                repository,
                ["check-ref-format", "--branch", name],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (nameCheck is GitResult<CommandOutput>.Failure)
        {
            return Rejected("git_branch_name_invalid");
        }

        var command = await ExecuteGovernedAsync(
                repository,
                ["branch", name, expectedState.HeadSha!],
                MutationTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var after = await ReadStateAfterDispatchAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        if (after is null)
        {
            return OutcomeUnknown();
        }

        var created = after.Snapshot.Refs.SingleOrDefault(item =>
            item.Kind == GitRefKind.LocalBranch
            && string.Equals(item.ShortName, name, StringComparison.Ordinal));
        var success = string.Equals(
                created?.TargetSha,
                expectedState.HeadSha,
                StringComparison.Ordinal)
            && string.Equals(
                after.Guard.HeadSha,
                expectedState.HeadSha,
                StringComparison.Ordinal)
            && string.Equals(
                after.Guard.HeadFullName,
                expectedState.HeadFullName,
                StringComparison.Ordinal);
        return CompleteMutation(
            command,
            expectedState,
            after,
            success,
            "git_branch_create_failed",
            headSha: after.Guard.HeadSha,
            branchName: name);
    }

    public async ValueTask<GitGovernedMutationReceipt> CheckoutBranchGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        string branchName,
        string expectedBranchSha,
        CancellationToken cancellationToken)
    {
        if (!IsSafeBranchName(branchName) || !IsObjectId(expectedBranchSha))
        {
            throw new ArgumentException("A governed branch target is invalid.");
        }

        var exact = await ReadExactStateAsync(repository, expectedState, cancellationToken)
            .ConfigureAwait(false);
        if (exact is null)
        {
            return Rejected("git_state_changed");
        }

        if (!IsCleanAttachedBorn(exact)
            || !exact.Snapshot.Refs.Any(item =>
                item.Kind == GitRefKind.LocalBranch
                && string.Equals(item.ShortName, branchName, StringComparison.Ordinal)
                && string.Equals(item.TargetSha, expectedBranchSha, StringComparison.Ordinal)))
        {
            return Rejected("git_branch_precondition_failed");
        }

        var command = await ExecuteGovernedAsync(
                repository,
                ["switch", branchName],
                MutationTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var after = await ReadStateAfterDispatchAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        if (after is null)
        {
            return OutcomeUnknown();
        }

        var success = string.Equals(
                after.Guard.HeadFullName,
                $"refs/heads/{branchName}",
                StringComparison.Ordinal)
            && string.Equals(
                after.Guard.HeadSha,
                expectedBranchSha,
                StringComparison.Ordinal);
        return CompleteMutation(
            command,
            expectedState,
            after,
            success,
            "git_branch_checkout_failed",
            headSha: after.Guard.HeadSha,
            branchName: branchName);
    }

    public async ValueTask<GitGovernedMutationReceipt> CommitGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        string subject,
        string? body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(expectedState);
        var exact = await ReadExactStateAsync(repository, expectedState, cancellationToken)
            .ConfigureAwait(false);
        if (exact is null)
        {
            return Rejected("git_state_changed");
        }

        if (exact.Snapshot.Head.IsDetached
            || exact.Snapshot.Head.IsUnborn
            || exact.Snapshot.HasConflicts
            || exact.Snapshot.StagedChanges.Count == 0)
        {
            return Rejected("git_commit_precondition_failed");
        }

        var expectedTree = await WriteIndexTreeAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        if (expectedTree is null)
        {
            return Rejected("git_commit_precondition_failed");
        }

        // write-tree captures the exact staged tree. Rechecking the complete
        // guard closes a concurrent-index window before commit dispatch; the
        // immutable commit object is checked against both values afterward.
        if (await ReadExactStateAsync(repository, expectedState, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Rejected("git_state_changed");
        }

        List<string> arguments =
        [
            "-c", "user.name=Asura Agent",
            "-c", "user.email=agent@asura.local",
            "commit", "--no-gpg-sign", "--no-verify", "-m", subject,
        ];
        if (!string.IsNullOrEmpty(body))
        {
            arguments.Add("-m");
            arguments.Add(body);
        }

        var command = await ExecuteGovernedAsync(
                repository,
                arguments,
                CommitTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var after = await ReadStateAfterDispatchAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        if (after is null)
        {
            return OutcomeUnknown();
        }

        var candidateCreated = command is GitResult<CommandOutput>.Success
            && after.Guard.HeadSha is { } newHead
            && !string.Equals(newHead, expectedState.HeadSha, StringComparison.Ordinal)
            && string.Equals(
                after.Guard.HeadFullName,
                expectedState.HeadFullName,
                StringComparison.Ordinal)
            && after.Snapshot.StagedChanges.Count == 0;
        if (!candidateCreated)
        {
            return CompleteMutation(
                command,
                expectedState,
                after,
                success: false,
                "git_commit_failed");
        }

        var parent = await ReadSingleCommitParentAsync(
                repository,
                after.Guard.HeadSha!,
                cancellationToken)
            .ConfigureAwait(false);
        var tree = await ReadSingleObjectIdAsync(
                repository,
                $"{after.Guard.HeadSha}^{{tree}}",
                cancellationToken)
            .ConfigureAwait(false);
        if (parent is null
            || tree is null
            || !string.Equals(parent, expectedState.HeadSha, StringComparison.Ordinal)
            || !string.Equals(tree, expectedTree, StringComparison.Ordinal))
        {
            return OutcomeUnknown();
        }

        return new GitGovernedMutationReceipt(
            GitGovernedMutationDisposition.Succeeded,
            "git_commit_succeeded",
            after.Guard,
            after.Guard.HeadSha,
            parent,
            tree,
            after.Snapshot.Head.BranchName,
            ChangedPathCount: exact.Snapshot.StagedChanges.Count);
    }

    public ValueTask<GitGovernedMutationReceipt> PushGovernedAsync(
        GitRepositoryHandle repository,
        GitGovernedPushRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(request);
        _ = cancellationToken;
        return ValueTask.FromResult(Rejected("git_push_transport_unavailable"));
    }

    private async ValueTask<GitGovernedMutationReceipt> MutateIndexGovernedAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        GitFileChange expectedChange,
        GitChangeArea expectedArea,
        IReadOnlyList<string> arguments,
        string failureCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedChange);
        if (expectedChange.Area != expectedArea)
        {
            throw new ArgumentException(
                "The governed change has the wrong index area.",
                nameof(expectedChange));
        }

        var exact = await ReadExactStateAsync(repository, expectedState, cancellationToken)
            .ConfigureAwait(false);
        var expectedChanges = expectedArea == GitChangeArea.Unstaged
            ? exact?.Snapshot.UnstagedChanges
            : exact?.Snapshot.StagedChanges;
        if (exact is null || expectedChanges is null || !expectedChanges.Contains(expectedChange))
        {
            return Rejected("git_state_changed");
        }

        var beforeIndex = await ReadIndexEntriesAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        var desiredPathEntries = expectedArea == GitChangeArea.Staged
            ? await ReadHeadIndexEntriesAsync(
                    repository,
                    expectedChange.Path,
                    expectedState.HeadSha,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        var expectedWorktree = expectedArea == GitChangeArea.Unstaged
            && expectedChange.Kind != GitChangeKind.Deleted
                ? ObservedWorktreeCapture(expectedState, expectedChange.Path)
                : null;
        var expectedWorktreeObject = expectedWorktree?.RawObjectId;
        if (beforeIndex is null
            || expectedArea == GitChangeArea.Staged && desiredPathEntries is null
            || expectedArea == GitChangeArea.Unstaged
                && expectedChange.Kind != GitChangeKind.Deleted
                && expectedWorktreeObject is null)
        {
            return Rejected(failureCode);
        }

        WorktreeCapture? captured = null;
        if (expectedArea == GitChangeArea.Unstaged)
        {
            if (expectedChange.Kind == GitChangeKind.Deleted)
            {
                arguments = ["update-index", "--force-remove", "--", expectedChange.Path];
            }
            else
            {
                captured = await CaptureWorktreeAsync(
                        repository, expectedChange.Path, writeObjects: true, cancellationToken, expectedWorktree)
                    .ConfigureAwait(false);
                if (captured is null || !string.Equals(
                        captured.RawObjectId, expectedWorktreeObject, StringComparison.Ordinal)
                    || !string.Equals(captured.Mode, expectedWorktree?.Mode, StringComparison.Ordinal)
                    || !string.Equals(captured.NormalizationIdentity, expectedWorktree?.NormalizationIdentity, StringComparison.Ordinal))
                {
                    return Rejected("git_state_changed");
                }

                arguments = ["update-index", "--add", "--cacheinfo", $"{captured.Mode},{captured.IndexObjectId},{expectedChange.Path}"];
            }
        }

        if (await ReadExactStateAsync(repository, expectedState, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Rejected("git_state_changed");
        }

        var command = await ExecuteGovernedAsync(
                repository,
                arguments,
                MutationTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var after = await ReadStateAfterDispatchAsync(repository, cancellationToken)
            .ConfigureAwait(false);
        if (after is null)
        {
            return OutcomeUnknown();
        }

        var afterIndex = await ReadIndexEntriesAsync(repository, CancellationToken.None)
            .ConfigureAwait(false);
        var afterWorktreeObject = expectedArea == GitChangeArea.Unstaged
            && expectedChange.Kind != GitChangeKind.Deleted
                ? await ReadWorktreeObjectIdAsync(
                        repository,
                        expectedChange.Path,
                        CancellationToken.None)
                    .ConfigureAwait(false)
                : null;
        if (afterIndex is null
            || expectedArea == GitChangeArea.Unstaged
                && expectedChange.Kind != GitChangeKind.Deleted
                && afterWorktreeObject is null)
        {
            return OutcomeUnknown();
        }

        var path = expectedChange.Path;
        var unrelatedIndexUnchanged = beforeIndex
            .Where(entry => !string.Equals(entry.Path, path, StringComparison.Ordinal))
            .SequenceEqual(afterIndex.Where(
                entry => !string.Equals(entry.Path, path, StringComparison.Ordinal)));
        var selectedEntries = afterIndex
            .Where(entry => string.Equals(entry.Path, path, StringComparison.Ordinal))
            .ToArray();
        var selectedPathProven = expectedArea == GitChangeArea.Unstaged
            ? StageResultMatchesExpectedChange(
                after,
                expectedChange,
                expectedWorktreeObject,
                captured?.IndexObjectId,
                afterWorktreeObject,
                selectedEntries)
            : UnstageResultMatchesHead(
                after,
                path,
                desiredPathEntries!,
                selectedEntries);
        var success = command is GitResult<CommandOutput>.Success
            && unrelatedIndexUnchanged
            && selectedPathProven
            && string.Equals(
                after.Guard.HeadFullName,
                expectedState.HeadFullName,
                StringComparison.Ordinal)
            && string.Equals(
                after.Guard.HeadSha,
                expectedState.HeadSha,
                StringComparison.Ordinal)
            && string.Equals(
                after.Guard.RefsDigest,
                expectedState.RefsDigest,
                StringComparison.Ordinal);
        return CompleteMutation(
            command,
            expectedState,
            after,
            success,
            failureCode,
            headSha: after.Guard.HeadSha,
            changedPathCount: 1);
    }

    private async ValueTask<IReadOnlyList<GitIndexEntry>?> ReadIndexEntriesAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteGovernedAsync(
                repository,
                ["ls-files", "--stage", "-z"],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        return result is GitResult<CommandOutput>.Success success
            ? ParseIndexEntries(success.Value.Text, treeEntries: false)
            : null;
    }

    private async ValueTask<IReadOnlyList<GitIndexEntry>?> ReadHeadIndexEntriesAsync(
        GitRepositoryHandle repository,
        string path,
        string? headSha,
        CancellationToken cancellationToken)
    {
        if (headSha is null)
        {
            return [];
        }

        var result = await ExecuteGovernedAsync(
                repository,
                ["ls-tree", "-z", headSha, "--", path],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        return result is GitResult<CommandOutput>.Success success
            ? ParseIndexEntries(success.Value.Text, treeEntries: true)
            : null;
    }

    private async ValueTask<string?> ReadWorktreeObjectIdAsync(
        GitRepositoryHandle repository,
        string path,
        CancellationToken cancellationToken)
    {
        var capture = await CaptureWorktreeAsync(repository, path, writeObjects: false, cancellationToken)
            .ConfigureAwait(false);
        return capture?.RawObjectId;
    }

    private async ValueTask<string?> WriteIndexTreeAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteGovernedAsync(
                repository,
                ["write-tree"],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is not GitResult<CommandOutput>.Success success)
        {
            return null;
        }

        var objectId = success.Value.Text.Trim();
        return IsObjectId(objectId) ? objectId : null;
    }

    private static IReadOnlyList<GitIndexEntry>? ParseIndexEntries(
        string output,
        bool treeEntries)
    {
        var entries = new List<GitIndexEntry>();
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = record.IndexOf('\t', StringComparison.Ordinal);
            if (separator <= 0 || separator == record.Length - 1)
            {
                return null;
            }

            var metadata = record[..separator]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 3)
            {
                return null;
            }

            var objectId = treeEntries ? metadata[2] : metadata[1];
            var stageText = treeEntries ? "0" : metadata[2];
            if (!IsObjectId(objectId)
                || !int.TryParse(
                    stageText,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var stage)
                || stage is < 0 or > 3)
            {
                return null;
            }

            entries.Add(new GitIndexEntry(
                metadata[0],
                objectId,
                stage,
                record[(separator + 1)..]));
        }

        return entries;
    }

    private static bool StageResultMatchesExpectedChange(
        GitGovernedState after,
        GitFileChange expectedChange,
        string? expectedWorktreeObject,
        string? expectedIndexObject,
        string? afterWorktreeObject,
        IReadOnlyList<GitIndexEntry> selectedEntries)
    {
        if (after.Snapshot.UnstagedChanges.Any(change =>
                string.Equals(change.Path, expectedChange.Path, StringComparison.Ordinal)))
        {
            return false;
        }

        if (expectedChange.Kind == GitChangeKind.Deleted)
        {
            return selectedEntries.Count == 0;
        }

        return expectedWorktreeObject is not null
            && string.Equals(
                afterWorktreeObject,
                expectedWorktreeObject,
                StringComparison.Ordinal)
            && selectedEntries is [{ Stage: 0 } selected]
            && string.Equals(
                selected.ObjectId,
                expectedIndexObject,
                StringComparison.Ordinal);
    }

    private static bool UnstageResultMatchesHead(
        GitGovernedState after,
        string path,
        IReadOnlyList<GitIndexEntry> desiredPathEntries,
        IReadOnlyList<GitIndexEntry> selectedEntries) =>
        !after.Snapshot.StagedChanges.Any(change =>
            string.Equals(change.Path, path, StringComparison.Ordinal))
        && desiredPathEntries.SequenceEqual(selectedEntries);

    private async ValueTask<GitGovernedState?> ReadExactStateAsync(
        GitRepositoryHandle repository,
        GitRepositoryGuard expectedState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedState);
        var current = await ReadGovernedStateAsync(repository, 0, cancellationToken)
            .ConfigureAwait(false);
        return current is GitResult<GitGovernedState>.Success success
            && success.Value.Guard == expectedState
            ? success.Value
            : null;
    }

    private async ValueTask<GitGovernedState?> ReadStateAfterDispatchAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await ReadGovernedStateAsync(
                    repository,
                    0,
                    cancellationToken.IsCancellationRequested
                        ? CancellationToken.None
                        : cancellationToken)
                .ConfigureAwait(false);
            return current is GitResult<GitGovernedState>.Success success
                ? success.Value
                : null;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async ValueTask<GitResult<GitRepositorySnapshot>>
        ReadGovernedSnapshotAsync(
            GitRepositoryHandle repository,
            long generation,
            CancellationToken cancellationToken)
    {
        var statusTask = ExecuteGovernedAsync(
            repository,
            StatusArguments,
            ReadTimeout,
            GovernedStateOutputLimit,
            cancellationToken).AsTask();
        var refsTask = ExecuteGovernedAsync(
            repository,
            [
                "for-each-ref",
                $"--format={GitRefsParser.ForEachRefFormat}",
                "refs/heads",
                "refs/remotes",
            ],
            ReadTimeout,
            GovernedStateOutputLimit,
            cancellationToken).AsTask();
        var remotesTask = ReadGovernedRemoteNamesAsync(
            repository,
            cancellationToken).AsTask();
        await Task.WhenAll(statusTask, refsTask, remotesTask).ConfigureAwait(false);
        var status = await statusTask.ConfigureAwait(false);
        var refs = await refsTask.ConfigureAwait(false);
        var remotes = await remotesTask.ConfigureAwait(false);
        if (status is GitResult<CommandOutput>.Failure statusFailure)
        {
            return new GitResult<GitRepositorySnapshot>.Failure(statusFailure.Error);
        }

        if (refs is GitResult<CommandOutput>.Failure refsFailure)
        {
            return new GitResult<GitRepositorySnapshot>.Failure(refsFailure.Error);
        }

        if (remotes is GitResult<IReadOnlyList<GitRemoteItem>>.Failure remoteFailure)
        {
            return new GitResult<GitRepositorySnapshot>.Failure(remoteFailure.Error);
        }

        try
        {
            var parsedStatus = GitStatusParser.Parse(Value(status).Text);
            return new GitResult<GitRepositorySnapshot>.Success(
                new GitRepositorySnapshot(
                    generation,
                    parsedStatus.Head,
                    parsedStatus.UnstagedChanges,
                    parsedStatus.StagedChanges,
                    GitRefsParser.ParseRefs(Value(refs).Text),
                    ((GitResult<IReadOnlyList<GitRemoteItem>>.Success)remotes).Value,
                    Stashes: [],
                    Worktrees: [],
                    Submodules: [],
                    timeProvider.GetUtcNow()));
        }
        catch (FormatException exception)
        {
            return Failure<GitRepositorySnapshot>(
                GitErrorCode.InvalidResponse,
                exception.Message);
        }
    }

    private async ValueTask<GitResult<GitUnit>> ValidateGovernedConfigurationAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteConfigurationReadAsync(
                repository,
                ["config", "--includes", "--show-scope", "--null", "--list"],
                cancellationToken)
            .ConfigureAwait(false);
        if (result is GitResult<CommandOutput>.Failure failure)
        {
            return new GitResult<GitUnit>.Failure(failure.Error);
        }

        var records = Value(result).Text.Split(
            '\0',
            StringSplitOptions.RemoveEmptyEntries);
        if (records.Length % 2 != 0)
        {
            return Failure<GitUnit>(
                GitErrorCode.InvalidResponse,
                "Git returned an invalid scoped configuration listing.");
        }

        for (var index = 0; index < records.Length; index += 2)
        {
            var scope = records[index];
            var record = records[index + 1];
            var separator = record.IndexOf('\n', StringComparison.Ordinal);
            var key = separator < 0 ? record : record[..separator];
            var value = separator < 0 ? string.Empty : record[(separator + 1)..];
            if (IsRepositoryControlledScope(scope)
                && (IsExecutableConfiguration(key, value)
                    || IsSensitiveHttpConfiguration(key, value)))
            {
                return Failure<GitUnit>(
                    GitErrorCode.Unsupported,
                    "The repository has executable Git configuration and cannot be governed.");
            }
        }

        return new GitResult<GitUnit>.Success(GitUnit.Value);
    }

    private static bool IsRepositoryControlledScope(string scope) =>
        string.Equals(scope, "local", StringComparison.Ordinal)
        || string.Equals(scope, "worktree", StringComparison.Ordinal);

    private static bool IsExecutableConfiguration(string key, string value)
    {
        var normalized = key.ToLowerInvariant();
        var populated = !string.IsNullOrWhiteSpace(value);
        return populated && (
            string.Equals(normalized, "diff.external", StringComparison.Ordinal)
            || normalized.StartsWith("filter.", StringComparison.Ordinal)
                && (normalized.EndsWith(".clean", StringComparison.Ordinal)
                    || normalized.EndsWith(".smudge", StringComparison.Ordinal)
                    || normalized.EndsWith(".process", StringComparison.Ordinal))
            || normalized.StartsWith("diff.", StringComparison.Ordinal)
                && (normalized.EndsWith(".command", StringComparison.Ordinal)
                    || normalized.EndsWith(".textconv", StringComparison.Ordinal))
            || normalized.StartsWith("url.", StringComparison.Ordinal)
                && (normalized.EndsWith(".insteadof", StringComparison.Ordinal)
                    || normalized.EndsWith(".pushinsteadof", StringComparison.Ordinal)));
    }

    private static bool IsSensitiveHttpConfiguration(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = key.ToLowerInvariant();
        return normalized.StartsWith("http.", StringComparison.Ordinal)
            || normalized.StartsWith("credential.", StringComparison.Ordinal)
            || normalized.StartsWith("remote.", StringComparison.Ordinal)
                && (normalized.EndsWith(".proxy", StringComparison.Ordinal)
                    || normalized.EndsWith(".proxyauthmethod", StringComparison.Ordinal));
    }

    private async ValueTask<GitResult<IReadOnlyList<GitRemoteItem>>>
        ReadGovernedRemoteNamesAsync(
            GitRepositoryHandle repository,
            CancellationToken cancellationToken)
    {
        var result = await ExecuteConfigurationReadAsync(
                repository,
                [
                    "config", "--local", "--no-includes", "--name-only", "--null",
                    "--get-regexp", "^remote\\..*\\.url$",
                ],
                cancellationToken,
                acceptExitOne: true)
            .ConfigureAwait(false);
        if (result is GitResult<CommandOutput>.Failure failure)
        {
            return new GitResult<IReadOnlyList<GitRemoteItem>>.Failure(failure.Error);
        }

        var remotes = new List<GitRemoteItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in Value(result).Text.Split(
                     '\0',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "remote.";
            const string suffix = ".url";
            if (!key.StartsWith(prefix, StringComparison.Ordinal)
                || !key.EndsWith(suffix, StringComparison.Ordinal))
            {
                return Failure<IReadOnlyList<GitRemoteItem>>(
                    GitErrorCode.InvalidResponse,
                    "Git returned an invalid remote configuration key.");
            }

            var name = key[prefix.Length..^suffix.Length];
            if (!IsSafeRemoteName(name))
            {
                return Failure<IReadOnlyList<GitRemoteItem>>(
                    GitErrorCode.Unsupported,
                    "A configured Git remote name is unsupported.");
            }

            if (seen.Add(name))
            {
                remotes.Add(new GitRemoteItem(name, string.Empty));
            }
        }

        return new GitResult<IReadOnlyList<GitRemoteItem>>.Success(remotes);
    }

    private async ValueTask<GitResult<string>> ResolveGovernedRemoteUrlAsync(
        GitRepositoryHandle repository,
        string remoteName,
        CancellationToken cancellationToken)
    {
        if (!IsSafeRemoteName(remoteName))
        {
            return Failure<string>(
                GitErrorCode.Unsupported,
                "The governed Git remote is unsupported.");
        }

        var pushUrls = await ReadConfigurationValuesAsync(
                repository,
                $"remote.{remoteName}.pushurl",
                cancellationToken)
            .ConfigureAwait(false);
        if (pushUrls is GitResult<IReadOnlyList<string>>.Failure pushFailure)
        {
            return new GitResult<string>.Failure(pushFailure.Error);
        }

        var values = ((GitResult<IReadOnlyList<string>>.Success)pushUrls).Value;
        if (values.Count == 0)
        {
            var urls = await ReadConfigurationValuesAsync(
                    repository,
                    $"remote.{remoteName}.url",
                    cancellationToken)
                .ConfigureAwait(false);
            if (urls is GitResult<IReadOnlyList<string>>.Failure urlFailure)
            {
                return new GitResult<string>.Failure(urlFailure.Error);
            }

            values = ((GitResult<IReadOnlyList<string>>.Success)urls).Value;
        }

        if (values.Count != 1
            || NormalizeGovernedHttpsRemoteUrl(values[0]) is not { } safeUrl)
        {
            return Failure<string>(
                GitErrorCode.Unsupported,
                "The configured Git remote transport is unsupported.");
        }

        return new GitResult<string>.Success(safeUrl);
    }

    private async ValueTask<GitResult<IReadOnlyList<string>>>
        ReadConfigurationValuesAsync(
            GitRepositoryHandle repository,
            string key,
            CancellationToken cancellationToken)
    {
        var result = await ExecuteConfigurationReadAsync(
                repository,
                ["config", "--local", "--no-includes", "--null", "--get-all", key],
                cancellationToken,
                acceptExitOne: true)
            .ConfigureAwait(false);
        if (result is GitResult<CommandOutput>.Failure failure)
        {
            return new GitResult<IReadOnlyList<string>>.Failure(failure.Error);
        }

        return new GitResult<IReadOnlyList<string>>.Success(
            Value(result).Text.Split(
                '\0',
                StringSplitOptions.RemoveEmptyEntries));
    }

    private ValueTask<GitResult<CommandOutput>> ExecuteGovernedAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken cancellationToken,
        bool acceptExitOne = false,
        bool allowTruncated = false) =>
        ExecuteIsolatedGitAsync(
            repository,
            SealedGitEnvironment,
            [.. SealedGitOptions, "-C", repository.WorkingTreeRoot, .. arguments],
            timeout,
            outputLimit,
            cancellationToken,
            acceptExitOne,
            allowTruncated);

    private ValueTask<GitResult<CommandOutput>> ExecuteGovernedRemoteReadAsync(
        GitRepositoryHandle repository,
        string remoteUrl,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken cancellationToken) =>
        // Running outside the worktree means Git cannot discover .git/config.
        // The URL is already resolved to a credential-free HTTPS endpoint.
        ExecuteIsolatedGitAsync(
            repository,
            RemoteReadEnvironment(repository),
            [
                .. SealedGitOptions,
                "-C", "/",
                .. HttpsTransportOptions(remoteUrl, UsesWorkspaceProxy(repository)),
                .. arguments,
            ],
            timeout,
            outputLimit,
            cancellationToken,
            acceptExitOne: false,
            allowTruncated: false);

    private ValueTask<GitResult<CommandOutput>> ExecuteConfigurationReadAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool acceptExitOne = false) =>
        ExecuteIsolatedGitAsync(
            repository,
            ConfigurationReadEnvironment,
            ["--no-pager", "-C", repository.WorkingTreeRoot, .. arguments],
            ReadTimeout,
            GovernedStateOutputLimit,
            cancellationToken,
            acceptExitOne,
            allowTruncated: false);

    private ValueTask<GitResult<CommandOutput>> ExecuteIsolatedGitAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> environmentArguments,
        IReadOnlyList<string> gitArguments,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken cancellationToken,
        bool acceptExitOne,
        bool allowTruncated) =>
        ExecuteIsolatedCommandAsync(repository, environmentArguments, GitExecutable, gitArguments,
            timeout, outputLimit, cancellationToken, acceptExitOne, allowTruncated);

    private ValueTask<GitResult<CommandOutput>> ExecuteIsolatedCommandAsync(
        GitRepositoryHandle repository,
        IReadOnlyList<string> environmentArguments,
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int outputLimit,
        CancellationToken cancellationToken,
        bool acceptExitOne = false,
        bool allowTruncated = false) =>
        repository.RunAsUser is { } owner
            ? ExecuteCoreAsync(
                repository.Connection,
                "sudo",
                [
                    "-n", "-u", owner, "-H", .. WorkspaceSudoEnvironment(repository),
                    "--", "env",
                    .. environmentArguments,
                    executable,
                    .. arguments,
                ],
                timeout,
                outputLimit,
                cancellationToken,
                acceptExitOne,
                allowTruncated)
            : ExecuteCoreAsync(
                repository.Connection,
                "env",
                [.. environmentArguments, executable, .. arguments],
                timeout,
                outputLimit,
                cancellationToken,
                acceptExitOne,
                allowTruncated);

    private static string? NormalizeGovernedHttpsRemoteUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 4096
            || value.Any(character => char.IsControl(character)
                || char.GetUnicodeCategory(character) ==
                    System.Globalization.UnicodeCategory.Format))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Fragment.Length != 0
            || uri.UserInfo.Length != 0)
        {
            return null;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            return uri.AbsoluteUri;
        }

        return null;
    }

    private bool UsesWorkspaceProxy(GitRepositoryHandle repository) =>
        networkConnector is not null && repository.Connection.Endpoint is Asura.Core.ConnectionEndpoint.Local;

    private IReadOnlyList<string> WorkspaceSudoEnvironment(GitRepositoryHandle repository) =>
        UsesWorkspaceProxy(repository)
            ? ["--preserve-env=ALL_PROXY,all_proxy,HTTP_PROXY,http_proxy,HTTPS_PROXY,https_proxy,NO_PROXY,no_proxy,GIT_SSH_COMMAND,GIT_SSH_VARIANT"]
            : [];

    private IReadOnlyList<string> RemoteReadEnvironment(GitRepositoryHandle repository)
    {
        if (!UsesWorkspaceProxy(repository))
        {
            return SealedGitEnvironment;
        }

        // The workspace executor supplies these values after planning. Preserve
        // them rather than copying credentials into env's command-line arguments.
        var environment = new List<string>();
        for (var index = 0; index < SealedGitEnvironment.Length; index++)
        {
            if (string.Equals(SealedGitEnvironment[index], "-u", StringComparison.Ordinal)
                && index + 1 < SealedGitEnvironment.Length
                && IsProxyVariable(SealedGitEnvironment[index + 1]))
            {
                index++;
                continue;
            }
            environment.Add(SealedGitEnvironment[index]);
        }
        return environment;
    }

    private static bool IsProxyVariable(string name) => name is
        "HTTP_PROXY" or "HTTPS_PROXY" or "ALL_PROXY" or "NO_PROXY"
        or "http_proxy" or "https_proxy" or "all_proxy" or "no_proxy";

    private static IReadOnlyList<string> HttpsTransportOptions(string remoteUrl, bool workspaceProxy)
    {
        var httpScope = $"http.{remoteUrl}";
        var credentialScope = $"credential.{remoteUrl}";
        return
        [
            "-c", "http.extraHeader=",
            "-c", $"{httpScope}.extraHeader=",
            "-c", "http.cookieFile=",
            "-c", $"{httpScope}.cookieFile=",
            "-c", "http.saveCookies=false",
            "-c", $"{httpScope}.saveCookies=false",
            .. workspaceProxy ? (IReadOnlyList<string>)[] :
            [
                "-c", "http.proxy=",
                "-c", $"{httpScope}.proxy=",
            ],
            "-c", "http.proxySSLCert=",
            "-c", $"{httpScope}.proxySSLCert=",
            "-c", "http.proxySSLKey=",
            "-c", $"{httpScope}.proxySSLKey=",
            "-c", "http.sslVerify=true",
            "-c", $"{httpScope}.sslVerify=true",
            "-c", "http.emptyAuth=false",
            "-c", $"{httpScope}.emptyAuth=false",
            "-c", "http.followRedirects=false",
            "-c", $"{httpScope}.followRedirects=false",
            "-c", "credential.helper=",
            "-c", $"{credentialScope}.helper=",
            "-c", "credential.username=",
            "-c", $"{credentialScope}.username=",
        ];
    }

    private static bool IsSafeRemoteName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value[0] != '-'
        && !value.Contains("..", StringComparison.Ordinal)
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or '/');

    private async ValueTask<GitResult<GitRepositoryGuard>> ReadRepositoryGuardAsync(
        GitRepositoryHandle repository,
        CancellationToken cancellationToken)
    {
        var headName = await ExecuteGovernedAsync(
                repository,
                ["symbolic-ref", "--quiet", "HEAD"],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken,
                acceptExitOne: true)
            .ConfigureAwait(false);
        if (headName is GitResult<CommandOutput>.Failure headNameFailure)
        {
            return new GitResult<GitRepositoryGuard>.Failure(headNameFailure.Error);
        }

        var headSha = await ExecuteGovernedAsync(
                repository,
                ["rev-parse", "--verify", "-q", "HEAD"],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken,
                acceptExitOne: true)
            .ConfigureAwait(false);
        if (headSha is GitResult<CommandOutput>.Failure headShaFailure)
        {
            return new GitResult<GitRepositoryGuard>.Failure(headShaFailure.Error);
        }

        var status = await ExecuteGovernedAsync(
                repository,
                StatusArguments,
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (status is GitResult<CommandOutput>.Failure statusFailure)
        {
            return new GitResult<GitRepositoryGuard>.Failure(statusFailure.Error);
        }

        var index = await ExecuteGovernedAsync(
                repository,
                ["ls-files", "--stage", "-z"],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (index is GitResult<CommandOutput>.Failure indexFailure)
        {
            return new GitResult<GitRepositoryGuard>.Failure(indexFailure.Error);
        }

        var refs = await ExecuteGovernedAsync(
                repository,
                [
                    "for-each-ref",
                    "--format=%(refname)%00%(objectname)",
                    "refs/heads",
                    "refs/remotes",
                ],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (refs is GitResult<CommandOutput>.Failure refsFailure)
        {
            return new GitResult<GitRepositoryGuard>.Failure(refsFailure.Error);
        }

        var headNameValue = EmptyToNull(Value(headName).Text.Trim());
        var headShaValue = EmptyToNull(Value(headSha).Text.Trim());
        if (headShaValue is not null && !IsObjectId(headShaValue))
        {
            return Failure<GitRepositoryGuard>(
                GitErrorCode.InvalidResponse,
                "Git returned an invalid HEAD object ID.");
        }

        GitStatusParser.Result parsedStatus;
        try
        {
            parsedStatus = GitStatusParser.Parse(Value(status).Text);
        }
        catch (FormatException exception)
        {
            return Failure<GitRepositoryGuard>(GitErrorCode.InvalidResponse, exception.Message);
        }

        // Porcelain records describe change kinds, not the bytes an agent saw.
        // Git streams raw bytes and binds their built-in normalization inputs;
        // only object IDs are retained here. An unreadable/racing file makes the
        // observation invalid rather than weakening it to a status-only guard.
        var worktreeIdentity = new StringBuilder();
        foreach (var change in parsedStatus.UnstagedChanges.OrderBy(
                     change => change.Path, StringComparer.Ordinal))
        {
            if (change.Kind == GitChangeKind.Deleted)
            {
                continue;
            }

            var capture = await CaptureWorktreeAsync(
                    repository, change.Path, writeObjects: false, cancellationToken)
                .ConfigureAwait(false);
            if (capture is null)
            {
                return Failure<GitRepositoryGuard>(
                    GitErrorCode.InvalidResponse,
                    "The working file changed or could not be read while capturing its content identity.");
            }

            worktreeIdentity.Append(change.Path).Append('\0')
                .Append(capture.Mode).Append('\0').Append(capture.RawObjectId).Append('\0')
                .Append(capture.NormalizationIdentity).Append('\0');
        }

        var worktreeContent = worktreeIdentity.ToString();
        var statusDigest = Digest(Value(status).Text + '\0' + worktreeContent);
        var indexDigest = Digest(Value(index).Text);
        var refsDigest = Digest(Value(refs).Text);
        var digest = Digest(string.Join(
            '\0',
            headNameValue ?? string.Empty,
            headShaValue ?? string.Empty,
            statusDigest,
            indexDigest,
            refsDigest,
            worktreeContent));
        return new GitResult<GitRepositoryGuard>.Success(new GitRepositoryGuard(
            digest,
            headNameValue,
            headShaValue,
            indexDigest,
            statusDigest,
            refsDigest,
            worktreeContent));
    }

    private static GitGovernedMutationReceipt CompleteMutation(
        GitResult<CommandOutput> command,
        GitRepositoryGuard before,
        GitGovernedState after,
        bool success,
        string failureCode,
        string? headSha = null,
        string? branchName = null,
        int changedPathCount = 0)
    {
        if (success)
        {
            return new GitGovernedMutationReceipt(
                GitGovernedMutationDisposition.Succeeded,
                failureCode.Replace("failed", "succeeded", StringComparison.Ordinal),
                after.Guard,
                HeadSha: headSha,
                BranchName: branchName,
                ChangedPathCount: changedPathCount);
        }

        return command is GitResult<CommandOutput>.Failure
            && after.Guard == before
            ? Rejected(failureCode)
            : OutcomeUnknown();
    }

    private static GitGovernedMutationReceipt Rejected(string stableCode) =>
        new(
            GitGovernedMutationDisposition.Rejected,
            stableCode,
            State: null);

    private static GitGovernedMutationReceipt OutcomeUnknown() =>
        new(
            GitGovernedMutationDisposition.OutcomeUnknown,
            "git_mutation_outcome_unknown",
            State: null);

    private async ValueTask<string?> ReadSingleObjectIdAsync(
        GitRepositoryHandle repository,
        string revision,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteGovernedAsync(
                repository,
                ["rev-parse", "--verify", revision],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is not GitResult<CommandOutput>.Success success)
        {
            return null;
        }

        var value = success.Value.Text.Trim();
        return IsObjectId(value) ? value : null;
    }

    private async ValueTask<string?> ReadSingleCommitParentAsync(
        GitRepositoryHandle repository,
        string commitSha,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteGovernedAsync(
                repository,
                ["rev-list", "--parents", "-n", "1", commitSha],
                ReadTimeout,
                GovernedStateOutputLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is not GitResult<CommandOutput>.Success success)
        {
            return null;
        }

        var objectIds = success.Value.Text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        return objectIds is [var observedCommit, var parent]
            && string.Equals(observedCommit, commitSha, StringComparison.Ordinal)
            && IsObjectId(parent)
                ? parent
                : null;
    }

    private static bool IsCleanAttachedBorn(GitGovernedState state) =>
        !state.Snapshot.Head.IsDetached
        && !state.Snapshot.Head.IsUnborn
        && !state.Snapshot.HasConflicts
        && state.Snapshot.StagedChanges.Count == 0
        && state.Snapshot.UnstagedChanges.Count == 0;

    private static bool IsSafeBranchName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= 256
        && value[0] != '-'
        && !value.StartsWith("refs/", StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && !value.Any(character => char.IsControl(character)
            || char.IsWhiteSpace(character)
            || character is '~' or '^' or ':' or '?' or '*' or '[' or '\\');

    private static bool IsObjectId(string value) =>
        value.Length is 40 or 64
        && value.All(static character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string? EmptyToNull(string value) =>
        value.Length == 0 ? null : value;

    private sealed record GitIndexEntry(
        string Mode,
        string ObjectId,
        int Stage,
        string Path);
}
