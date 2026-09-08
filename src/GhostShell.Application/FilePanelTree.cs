using System.Runtime.CompilerServices;

namespace GhostShell.Application;

/// <summary>
/// Walks a provider tree while hiding page size, continuation, and protocol differences. It is the
/// common discovery primitive behind search and observation; callers see every returned entry.
/// </summary>
internal static class FilePanelTree
{
    public static async IAsyncEnumerable<FilePanelResult<FilePanelEntry>> EnumerateAsync(
        IFilePanelClient client,
        FilePanelLocation root,
        FilePanelDiscoveryScope scope,
        bool showHidden,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(root);
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, null);
        }

        var pageSize = Math.Min(500, client.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, root.ProviderProfileId, StringComparison.Ordinal))
            ?.MaximumPageSize ?? 250);
        var ancestors = new HashSet<FilePanelLocation>();
        await foreach (var entry in WalkAsync(root.WithVersion(null)).ConfigureAwait(false))
        {
            yield return entry;
        }

        async IAsyncEnumerable<FilePanelResult<FilePanelEntry>> WalkAsync(FilePanelLocation directory)
        {
            if (!ancestors.Add(directory))
            {
                yield break;
            }

            try
            {
                string? continuation = null;
                string? checkpoint = null;
                long checkpointWindow = 1;
                long checkpointDistance = 0;
                do
                {
                    var page = await client.ListAsync(
                        new FilePanelListRequest(
                            directory,
                            pageSize,
                            continuation,
                            showHidden),
                        cancellationToken).ConfigureAwait(false);
                    if (!page.IsSuccess)
                    {
                        yield return FilePanelResult<FilePanelEntry>.Failure(page.Error!);
                        yield break;
                    }

                    foreach (var entry in page.Value!.Entries)
                    {
                        if (showHidden || !entry.IsHidden)
                        {
                            yield return FilePanelResult<FilePanelEntry>.Success(entry);
                            if (scope == FilePanelDiscoveryScope.Subtree && entry.Kind == FilePanelEntryKind.Directory)
                            {
                                // Demand-driven depth-first traversal retains one provider page per
                                // ancestor, not every sibling and every file in the whole subtree.
                                await foreach (var child in WalkAsync(entry.Location.WithVersion(null)).ConfigureAwait(false))
                                {
                                    yield return child;
                                }
                            }
                        }
                    }

                    continuation = page.Value.ContinuationToken;
                    if (continuation is not null && string.Equals(continuation, checkpoint, StringComparison.Ordinal))
                    {
                        yield return InvalidContinuation();
                        yield break;
                    }
                    if (++checkpointDistance == checkpointWindow)
                    {
                        // Brent's cycle checkpoint needs constant retained token
                        // space even when a search scans many nonmatching pages.
                        checkpoint = continuation;
                        checkpointDistance = 0;
                        checkpointWindow = Math.Min(long.MaxValue / 2, checkpointWindow) * 2;
                    }
                }
                while (continuation is not null);

            }
            finally
            {
                ancestors.Remove(directory);
            }
        }
    }

    private static FilePanelResult<FilePanelEntry> InvalidContinuation() =>
        FilePanelResult<FilePanelEntry>.Failure(new FilePanelError(
            FilePanelErrorCode.InvalidLocation,
            "file_list_continuation_cycle",
            "The file provider repeated a continuation token while traversing the location.",
            Retryable: false));
}
