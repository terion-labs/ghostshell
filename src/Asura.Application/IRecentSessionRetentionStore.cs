namespace Asura.Application;

public interface IRecentSessionRetentionStore
{
    ValueTask<RecentSessionStoreResult<StoredRecentSessionRetentionPolicy>> GetRetentionAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the local retention policy and prunes history in the same transaction.
    /// </summary>
    ValueTask<RecentSessionStoreResult<RecentSessionRetentionUpdateResult>> UpdateRetentionAsync(
        RecentSessionRetentionPolicy policy,
        long expectedRevision,
        CancellationToken cancellationToken);
}
