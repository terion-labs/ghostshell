namespace Asura.Git;

public sealed record GitRepositoryGuard(
    string Digest,
    string? HeadFullName,
    string? HeadSha,
    string IndexDigest,
    string WorktreeDigest,
    string RefsDigest,
    string WorktreeContentIdentity = "");

public sealed record GitGovernedState(
    GitRepositorySnapshot Snapshot,
    GitRepositoryGuard Guard,
    bool MutationEligible);

public sealed record GitGovernedRemoteRef(
    string RemoteName,
    string DestinationFullName,
    string? Sha,
    DateTimeOffset CapturedAtUtc);

public enum GitGovernedMutationDisposition
{
    Succeeded,
    Rejected,
    OutcomeUnknown,
}

public sealed record GitGovernedMutationReceipt(
    GitGovernedMutationDisposition Disposition,
    string StableCode,
    GitRepositoryGuard? State,
    string? HeadSha = null,
    string? ParentSha = null,
    string? TreeSha = null,
    string? BranchName = null,
    string? RemoteName = null,
    string? RemoteSha = null,
    int ChangedPathCount = 0);

public sealed record GitGovernedPushRequest(
    GitRepositoryGuard ExpectedState,
    string RemoteName,
    string DestinationBranch,
    string LocalBranch,
    string LocalSha,
    string? ExpectedRemoteSha);
