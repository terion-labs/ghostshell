using GhostShell.Core;
using GhostShell.Git;

namespace GhostShell.Application;

public sealed record GitSessionMetadata(
    GitRepositoryIdentity RepositoryIdentity,
    long BindingRevision,
    string ConnectionDisplayName,
    ConnectionKind ConnectionKind,
    bool MutationsQuarantined);

public sealed record GitPanelSessionState(
    GitSessionMetadata Metadata,
    bool IsReady);

public sealed record GitChangeItem(
    GitChangeReferenceId Reference,
    string DisplayPath,
    GitChangeKind Kind,
    GitChangeArea Area);

public sealed record GitBranchItem(
    GitBranchReferenceId Reference,
    string Name,
    string Sha,
    bool IsCurrent);

public sealed record GitRemoteItemProjection(
    GitRemoteReferenceId Reference,
    string Name);

public sealed record GitAgentStateSnapshot(
    GitStateReferenceId? StateReference,
    string RepositoryLabel,
    string ConnectionLabel,
    string? CurrentBranch,
    string? HeadSha,
    bool IsDetached,
    bool IsUnborn,
    bool HasConflicts,
    bool IsDirty,
    IReadOnlyList<GitChangeItem> Changes,
    IReadOnlyList<GitBranchItem> Branches,
    IReadOnlyList<GitRemoteItemProjection> Remotes,
    bool IsTruncated,
    bool MutationsQuarantined,
    DateTimeOffset CapturedAtUtc);

public sealed record GitAgentDiffSnapshot(
    string DisplayPath,
    string? Text,
    bool IsBinary,
    bool IsTruncated,
    bool IsSensitive,
    int LineCount,
    int HunkCount);

public sealed record GitAgentRemoteRefSnapshot(
    GitRemoteStateReferenceId Reference,
    string RemoteName,
    string DestinationBranch,
    string? Sha,
    bool IsAbsent,
    DateTimeOffset CapturedAtUtc);

public sealed record GitAgentMutationReceipt(
    string Operation,
    GitStateReferenceId? StateReference,
    string? HeadSha,
    string? BranchName,
    string? RemoteName,
    string? RemoteSha,
    int ChangedPathCount);

public abstract record GitAgentOperationResult
{
    private GitAgentOperationResult()
    {
    }

    public sealed record State(GitAgentStateSnapshot Value) : GitAgentOperationResult;

    public sealed record Diff(GitAgentDiffSnapshot Value) : GitAgentOperationResult;

    public sealed record RemoteRef(GitAgentRemoteRefSnapshot Value) : GitAgentOperationResult;

    public sealed record Mutation(GitAgentMutationReceipt Value) : GitAgentOperationResult;

    public sealed record Rejected(string StableCode) : GitAgentOperationResult;

    public sealed record OutcomeUnknown(string StableCode) : GitAgentOperationResult;
}

public interface IGitPanelSession : IPanelSession
{
    GitSessionBinding Binding { get; }

    GitPanelSessionState State { get; }

    ValueTask<GitAgentOperationResult> ReadStateAsync(CancellationToken cancellationToken);

    ValueTask<GitAgentOperationResult> ReadDiffAsync(
        GitStateReferenceId state,
        GitChangeReferenceId change,
        GitChangeArea area,
        CancellationToken cancellationToken);

    ValueTask<GitAgentOperationResult> ReadRemoteRefAsync(
        GitStateReferenceId state,
        GitRemoteReferenceId remote,
        GitBranchReferenceId branch,
        CancellationToken cancellationToken);

    ValueTask<GitAgentOperationResult> StageAsync(
        GitStateReferenceId state,
        GitChangeReferenceId change,
        CancellationToken cancellationToken);

    ValueTask<GitAgentOperationResult> UnstageAsync(
        GitStateReferenceId state,
        GitChangeReferenceId change,
        CancellationToken cancellationToken);

    ValueTask<GitAgentOperationResult> CreateBranchAsync(
        GitStateReferenceId state,
        string name,
        CancellationToken cancellationToken);

    ValueTask<GitAgentOperationResult> CheckoutBranchAsync(
        GitStateReferenceId state,
        GitBranchReferenceId branch,
        CancellationToken cancellationToken);

    ValueTask<GitAgentOperationResult> CommitAsync(
        GitStateReferenceId state,
        string subject,
        string? body,
        CancellationToken cancellationToken);

    ValueTask<GitAgentOperationResult> PushAsync(
        GitStateReferenceId state,
        GitRemoteStateReferenceId remoteState,
        GitRemoteReferenceId remote,
        GitBranchReferenceId branch,
        CancellationToken cancellationToken);
}

public interface IGitPanelSessionFactory
{
    CapabilitySet Capabilities { get; }

    ValueTask<IGitPanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        GitSessionTarget target,
        CancellationToken cancellationToken);
}
