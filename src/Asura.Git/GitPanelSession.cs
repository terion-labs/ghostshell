using System.Globalization;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Git;

internal sealed class GitPanelSession : IGitPanelSession
{
    private const int MaximumChanges = 200;
    private const int MaximumBranches = 200;
    private const int MaximumRemotes = 32;
    private const int MaximumDiffCharacters = 64 * 1024;
    private const int MaximumDiffHunks = 20;
    private const int MaximumDiffLines = 400;
    private const int MaximumMessageBytes = 32 * 1024;
    private readonly object _gate = new();
    private readonly IGitRepositoryClient _client;
    private readonly IGitRepositoryMutationCoordinator _coordinator;
    private readonly GitAgentReferencePool _references;
    private readonly GitPanelSessionLifetime _lifetime;
    private readonly GitSessionTarget _target;
    private GitGovernedState? _initialState;
    private bool _mutationsQuarantined;
    private long _generation = 1;

    public GitPanelSession(
        SessionId id,
        GitSessionTarget target,
        IGitRepositoryClient client,
        IGitRepositoryMutationCoordinator coordinator,
        GitGovernedState initialState,
        CapabilitySet capabilities,
        TimeProvider timeProvider)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _initialState = initialState ?? throw new ArgumentNullException(nameof(initialState));
        _references = new GitAgentReferencePool(timeProvider);
        _lifetime = new GitPanelSessionLifetime(id, capabilities, timeProvider);
        Binding = target.Binding;
        ConnectionLabel = BoundedLabel(target.Repository.Connection.Name, 256, "Local");
        RepositoryLabel = RepositoryDisplayName(target.Repository.WorkingTreeRoot);
    }

    public SessionId Id => _lifetime.Id;

    public PanelKind Kind => PanelKind.Git;

    public CapabilitySet Capabilities => _lifetime.Capabilities;

    public GitSessionBinding Binding { get; }

    public string ConnectionLabel { get; }

    public string RepositoryLabel { get; }

    public GitPanelSessionState State => new(
        new GitSessionMetadata(
            Binding.RepositoryIdentity,
            Binding.BindingRevision,
            ConnectionLabel,
            Binding.ConnectionKind,
            MutationsQuarantined),
        IsReady: true);

    private bool MutationsQuarantined
    {
        get
        {
            lock (_gate)
            {
                return _mutationsQuarantined;
            }
        }
    }

    public async ValueTask<GitAgentOperationResult> ReadStateAsync(
        CancellationToken cancellationToken)
    {
        using var operationCancellation =
            _lifetime.CreateOperationCancellation(cancellationToken);
        GitGovernedState governed;
        lock (_gate)
        {
            governed = _initialState!;
            _initialState = null;
        }

        if (governed is null)
        {
            var read = await _client.ReadGovernedStateAsync(
                    _target.Repository,
                    Interlocked.Increment(ref _generation),
                    operationCancellation.Token)
                .ConfigureAwait(false);
            if (read is not GitResult<GitGovernedState>.Success success)
            {
                return new GitAgentOperationResult.Rejected("git_state_unavailable");
            }

            governed = success.Value;
        }

        var snapshot = ProjectState(governed);
        lock (_gate)
        {
            _mutationsQuarantined = false;
        }

        return new GitAgentOperationResult.State(snapshot with
        {
            MutationsQuarantined = false,
        });
    }

    public async ValueTask<GitAgentOperationResult> ReadDiffAsync(
        GitStateReferenceId state,
        GitChangeReferenceId change,
        GitChangeArea area,
        CancellationToken cancellationToken)
    {
        if (!_references.TryGetState(state, out var lease)
            || !lease.Changes.TryGetValue(change, out var selected)
            || selected.Area != area)
        {
            return new GitAgentOperationResult.Rejected("git_reference_expired");
        }

        using var operationCancellation =
            _lifetime.CreateOperationCancellation(cancellationToken);
        var fresh = await _client.ReadGovernedStateAsync(
                _target.Repository,
                Interlocked.Increment(ref _generation),
                operationCancellation.Token)
            .ConfigureAwait(false);
        if (fresh is not GitResult<GitGovernedState>.Success stateSuccess
            || stateSuccess.Value.Guard != lease.Guard)
        {
            return new GitAgentOperationResult.Rejected("git_state_changed");
        }

        var request = new GitDiffRequest(
            area == GitChangeArea.Staged ? GitDiffArea.Index : GitDiffArea.Worktree,
            selected.Path,
            selected.OriginalPath,
            IsUntracked: selected.Kind == GitChangeKind.Untracked);
        var result = await _client.ReadGovernedDiffAsync(
                _target.Repository,
                request,
                operationCancellation.Token)
            .ConfigureAwait(false);
        return result is GitResult<GitDiffDocument>.Success success
            ? new GitAgentOperationResult.Diff(ProjectDiff(success.Value))
            : new GitAgentOperationResult.Rejected("git_diff_unavailable");
    }

    public async ValueTask<GitAgentOperationResult> ReadRemoteRefAsync(
        GitStateReferenceId state,
        GitRemoteReferenceId remote,
        GitBranchReferenceId branch,
        CancellationToken cancellationToken)
    {
        if (!_references.TryGetState(state, out var lease)
            || !lease.Remotes.TryGetValue(remote, out var selectedRemote)
            || !lease.Branches.TryGetValue(branch, out var selectedBranch))
        {
            return new GitAgentOperationResult.Rejected("git_reference_expired");
        }

        using var operationCancellation =
            _lifetime.CreateOperationCancellation(cancellationToken);
        var result = await _client.ReadGovernedRemoteRefAsync(
                _target.Repository,
                selectedRemote.Name,
                selectedBranch.ShortName,
                operationCancellation.Token)
            .ConfigureAwait(false);
        if (result is not GitResult<GitGovernedRemoteRef>.Success success)
        {
            return new GitAgentOperationResult.Rejected("git_remote_ref_unavailable");
        }

        var remoteState = _references.AddRemoteState(
            state,
            remote,
            branch,
            success.Value);
        lock (_gate)
        {
            _mutationsQuarantined = false;
        }

        return new GitAgentOperationResult.RemoteRef(new GitAgentRemoteRefSnapshot(
            remoteState,
            BoundedLabel(selectedRemote.Name, 128, "remote"),
            BoundedLabel(selectedBranch.ShortName, 256, "branch"),
            success.Value.Sha,
            success.Value.Sha is null,
            success.Value.CapturedAtUtc));
    }

    public ValueTask<GitAgentOperationResult> StageAsync(
        GitStateReferenceId state,
        GitChangeReferenceId change,
        CancellationToken cancellationToken) =>
        MutateChangeAsync(
            "git.stage",
            state,
            change,
            GitChangeArea.Unstaged,
            static (client, repository, guard, change, token) =>
                client.StageGovernedAsync(repository, guard, change, token),
            cancellationToken);

    public ValueTask<GitAgentOperationResult> UnstageAsync(
        GitStateReferenceId state,
        GitChangeReferenceId change,
        CancellationToken cancellationToken) =>
        MutateChangeAsync(
            "git.unstage",
            state,
            change,
            GitChangeArea.Staged,
            static (client, repository, guard, change, token) =>
                client.UnstageGovernedAsync(repository, guard, change, token),
            cancellationToken);

    public async ValueTask<GitAgentOperationResult> CreateBranchAsync(
        GitStateReferenceId state,
        string name,
        CancellationToken cancellationToken)
    {
        ValidateBranchName(name);
        if (!_references.TryGetState(state, out var lease))
        {
            return new GitAgentOperationResult.Rejected("git_reference_expired");
        }

        return await MutateAsync(
            "git.branch_create",
            token => _client.CreateBranchGovernedAsync(
                _target.Repository,
                lease.Guard,
                name,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GitAgentOperationResult> CheckoutBranchAsync(
        GitStateReferenceId state,
        GitBranchReferenceId branch,
        CancellationToken cancellationToken)
    {
        if (!_references.TryGetState(state, out var lease)
            || !lease.Branches.TryGetValue(branch, out var selected))
        {
            return new GitAgentOperationResult.Rejected("git_reference_expired");
        }

        return await MutateAsync(
            "git.branch_checkout",
            token => _client.CheckoutBranchGovernedAsync(
                _target.Repository,
                lease.Guard,
                selected.ShortName,
                selected.TargetSha,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GitAgentOperationResult> CommitAsync(
        GitStateReferenceId state,
        string subject,
        string? body,
        CancellationToken cancellationToken)
    {
        ValidateCommitText(subject, nameof(subject), allowEmpty: false);
        ValidateCommitText(body, nameof(body), allowEmpty: true);
        if (!_references.TryGetState(state, out var lease))
        {
            return new GitAgentOperationResult.Rejected("git_reference_expired");
        }

        return await MutateAsync(
            "git.commit",
            token => _client.CommitGovernedAsync(
                _target.Repository,
                lease.Guard,
                subject,
                body,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GitAgentOperationResult> PushAsync(
        GitStateReferenceId state,
        GitRemoteStateReferenceId remoteState,
        GitRemoteReferenceId remote,
        GitBranchReferenceId branch,
        CancellationToken cancellationToken)
    {
        if (!_references.TryGetState(state, out var lease)
            || !_references.TryGetRemoteState(remoteState, out var remoteLease)
            || remoteLease.State != state
            || remoteLease.Remote != remote
            || remoteLease.Branch != branch
            || !lease.Remotes.TryGetValue(remote, out var selectedRemote)
            || !lease.Branches.TryGetValue(branch, out var selectedBranch)
            || lease.Guard.HeadSha is not { } localSha)
        {
            return new GitAgentOperationResult.Rejected("git_reference_expired");
        }

        var request = new GitGovernedPushRequest(
            lease.Guard,
            selectedRemote.Name,
            selectedBranch.ShortName,
            selectedBranch.ShortName,
            selectedBranch.TargetSha,
            remoteLease.Observed.Sha);
        return await MutateAsync(
            "git.push",
            token => _client.PushGovernedAsync(_target.Repository, request, token),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
        _lifetime.SnapshotAsync(cancellationToken);

    public IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        CancellationToken cancellationToken) =>
        _lifetime.WatchAsync(afterSequence, cancellationToken);

    public ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        _references.InvalidateAll();
        return _lifetime.CloseAsync(mode, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _references.InvalidateAll();
        await _lifetime.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<GitAgentOperationResult> MutateChangeAsync(
        string operation,
        GitStateReferenceId state,
        GitChangeReferenceId change,
        GitChangeArea expectedArea,
        Func<
            IGitRepositoryClient,
            GitRepositoryHandle,
            GitRepositoryGuard,
            GitFileChange,
            CancellationToken,
            ValueTask<GitGovernedMutationReceipt>> execute,
        CancellationToken cancellationToken)
    {
        if (!_references.TryGetState(state, out var lease)
            || !lease.Changes.TryGetValue(change, out var selected)
            || selected.Area != expectedArea
            || selected.Kind == GitChangeKind.Conflicted)
        {
            return new GitAgentOperationResult.Rejected("git_reference_expired");
        }

        return await MutateAsync(
            operation,
            token => execute(
                _client,
                _target.Repository,
                lease.Guard,
                selected,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GitAgentOperationResult> MutateAsync(
        string operation,
        Func<CancellationToken, ValueTask<GitGovernedMutationReceipt>> execute,
        CancellationToken cancellationToken)
    {
        if (MutationsQuarantined)
        {
            return new GitAgentOperationResult.Rejected("git_mutations_quarantined");
        }

        using var operationCancellation =
            _lifetime.CreateOperationCancellation(cancellationToken);
        await using var mutation = await _coordinator
            .AcquireAsync(Binding.RepositoryIdentity, operationCancellation.Token)
            .ConfigureAwait(false);
        GitGovernedMutationReceipt receipt;
        try
        {
            receipt = await execute(operationCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return QuarantineMutation();
        }

        _references.InvalidateAll();
        if (receipt.Disposition == GitGovernedMutationDisposition.OutcomeUnknown)
        {
            return QuarantineMutation();
        }

        if (receipt.Disposition == GitGovernedMutationDisposition.Rejected)
        {
            return new GitAgentOperationResult.Rejected(receipt.StableCode);
        }

        GitStateReferenceId? stateReference = null;
        GitResult<GitGovernedState> refreshed;
        try
        {
            refreshed = await _client.ReadGovernedStateAsync(
                    _target.Repository,
                    Interlocked.Increment(ref _generation),
                    operationCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return QuarantineMutation();
        }

        if (refreshed is not GitResult<GitGovernedState>.Success refreshedSuccess)
        {
            return QuarantineMutation();
        }

        stateReference = ProjectState(refreshedSuccess.Value).StateReference;

        return new GitAgentOperationResult.Mutation(new GitAgentMutationReceipt(
            operation,
            stateReference,
            receipt.HeadSha,
            receipt.BranchName,
            receipt.RemoteName,
            receipt.RemoteSha,
            receipt.ChangedPathCount));
    }

    private GitAgentOperationResult QuarantineMutation()
    {
        _references.InvalidateAll();
        lock (_gate)
        {
            _mutationsQuarantined = true;
        }

        return new GitAgentOperationResult.OutcomeUnknown(
            "git_mutation_outcome_unknown");
    }

    private GitAgentStateSnapshot ProjectState(GitGovernedState governed)
    {
        var snapshot = governed.Snapshot;
        var branches = snapshot.Refs
            .Where(static item => item.Kind == GitRefKind.LocalBranch)
            .Take(MaximumBranches + 1)
            .ToArray();
        var remotes = snapshot.Remotes.Take(MaximumRemotes + 1).ToArray();
        var changes = snapshot.StagedChanges
            .Concat(snapshot.UnstagedChanges)
            .Take(MaximumChanges + 1)
            .ToArray();
        var truncated = branches.Length > MaximumBranches
            || remotes.Length > MaximumRemotes
            || changes.Length > MaximumChanges;
        branches = [.. branches.Take(MaximumBranches)];
        remotes = [.. remotes.Take(MaximumRemotes)];
        changes = [.. changes.Take(MaximumChanges)];

        GitStateReferenceId? stateReference = null;
        GitAgentReferencePool.StateLease? lease = null;
        if (!truncated && governed.MutationEligible)
        {
            stateReference = _references.AddState(
                governed.Guard,
                changes,
                branches,
                remotes);
            _ = _references.TryGetState(stateReference.Value, out lease!);
        }

        GitChangeItem[] projectedChanges = lease is null
            ? []
            : [.. lease.Changes.Select(pair => new GitChangeItem(
                    pair.Key,
                    BoundedLabel(pair.Value.Path, 1024, "[withheld path]"),
                    pair.Value.Kind,
                    pair.Value.Area))];
        GitBranchItem[] projectedBranches = lease is null
            ? []
            : [.. lease.Branches.Select(pair => new GitBranchItem(
                    pair.Key,
                    BoundedLabel(pair.Value.ShortName, 256, "[withheld branch]"),
                    pair.Value.TargetSha,
                    pair.Value.IsCurrent))];
        GitRemoteItemProjection[] projectedRemotes = lease is null
            ? []
            : [.. lease.Remotes.Select(pair => new GitRemoteItemProjection(
                    pair.Key,
                    BoundedLabel(pair.Value.Name, 128, "[withheld remote]")))];
        return new GitAgentStateSnapshot(
            stateReference,
            RepositoryLabel,
            ConnectionLabel,
            BoundedOptionalLabel(snapshot.Head.BranchName, 256),
            snapshot.Head.CommitSha,
            snapshot.Head.IsDetached,
            snapshot.Head.IsUnborn,
            snapshot.HasConflicts,
            snapshot.StagedChanges.Count > 0 || snapshot.UnstagedChanges.Count > 0,
            projectedChanges,
            projectedBranches,
            projectedRemotes,
            truncated,
            MutationsQuarantined,
            snapshot.CapturedAtUtc);
    }

    private static GitAgentDiffSnapshot ProjectDiff(GitDiffDocument document)
    {
        var text = new StringBuilder();
        var hunkCount = 0;
        var lineCount = 0;
        var truncated = document.IsTruncated;
        foreach (var hunk in document.Hunks)
        {
            if (hunkCount >= MaximumDiffHunks)
            {
                truncated = true;
                break;
            }

            AppendLine(text, hunk.Header);
            hunkCount++;
            foreach (var line in hunk.Lines)
            {
                if (lineCount >= MaximumDiffLines)
                {
                    truncated = true;
                    break;
                }

                var prefix = line.Kind switch
                {
                    GitDiffLineKind.Added => "+",
                    GitDiffLineKind.Removed => "-",
                    _ => " ",
                };
                if (text.Length + prefix.Length + line.Text.Length + 1
                    > MaximumDiffCharacters)
                {
                    truncated = true;
                    break;
                }

                AppendLine(text, string.Concat(prefix, line.Text));
                lineCount++;
            }
        }

        var value = text.ToString();
        var sensitive = AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value);
        return new GitAgentDiffSnapshot(
            BoundedLabel(document.Path, 1024, "[withheld path]"),
            sensitive || document.IsBinary ? null : value,
            document.IsBinary,
            truncated,
            sensitive,
            lineCount,
            hunkCount);
    }

    private static void AppendLine(StringBuilder builder, string value)
    {
        builder.Append(BoundedLabel(value, 4096, "[withheld line]"));
        builder.Append('\n');
    }

    private static void ValidateBranchName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Encoding.UTF8.GetByteCount(name) > 256
            || name.Any(IsUnsafeCharacter)
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(name))
        {
            throw new ArgumentException("A governed Git branch name is invalid.", nameof(name));
        }
    }

    private static void ValidateCommitText(
        string? value,
        string parameterName,
        bool allowEmpty)
    {
        if (value is null && allowEmpty)
        {
            return;
        }

        if (value is null
            || (!allowEmpty && string.IsNullOrWhiteSpace(value))
            || Encoding.UTF8.GetByteCount(value) > MaximumMessageBytes
            || value.Contains('\0', StringComparison.Ordinal)
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException("Governed Git commit text is invalid.", parameterName);
        }
    }

    private static string RepositoryDisplayName(string root)
    {
        var trimmed = root.TrimEnd('/', '\\');
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return BoundedLabel(
            separator >= 0 ? trimmed[(separator + 1)..] : trimmed,
            256,
            "repository");
    }

    private static string? BoundedOptionalLabel(string? value, int maximumBytes) =>
        value is null ? null : BoundedLabel(value, maximumBytes, "[withheld]");

    private static string BoundedLabel(string value, int maximumBytes, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(IsUnsafeCharacter)
            || Encoding.UTF8.GetByteCount(value) > maximumBytes
            || AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            return fallback;
        }

        return string.Concat(value);
    }

    private static bool IsUnsafeCharacter(char character) =>
        char.IsControl(character)
        || char.GetUnicodeCategory(character) is
            UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
}
