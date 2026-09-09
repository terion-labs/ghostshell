using Asura.Application;

namespace Asura.Files;

public sealed partial class FilePanelClient
{
    private const int MaximumDirectoryTransferEntries = 100_000;

    private async ValueTask<FilePanelResult<long>> RunDirectoryTransferAsync(
        TransferRecord record,
        IFileProvider sourceProvider,
        IFileProvider destinationProvider,
        FileLocation source,
        FileLocation destination,
        FileEntry sourceEntry,
        CancellationToken cancellationToken)
    {
        if (!CanStreamDirectory(sourceProvider, destinationProvider))
        {
            return Failure<long>(
                FilePanelErrorCode.UnsupportedCapability,
                "file_directory_transfer_unsupported",
                "Directory transfer requires listing and reading at the source, "
                + "plus directory creation and streaming writes at the destination.");
        }

        if (IsDescendant(source, destination))
        {
            return Failure<long>(
                FilePanelErrorCode.InvalidLocation,
                "file_directory_destination_descendant",
                "A directory cannot be transferred into one of its descendants.");
        }

        var plan = await BuildDirectoryTransferPlanAsync(
                sourceProvider,
                source,
                destination,
                cancellationToken)
            .ConfigureAwait(false);
        if (!plan.IsSuccess)
        {
            return FilePanelResult<long>.Failure(plan.Error!);
        }

        ReportProgress(record, "Creating folders", 0, plan.Value!.TotalBytes);
        foreach (var directory in plan.Value.Directories)
        {
            var create = await destinationProvider.CreateDirectoryAsync(
                    new FileCreateDirectoryRequest(
                        directory,
                        directory == destination
                            ? DestinationPrecondition(record.Snapshot.Request.ConflictPolicy)
                            : new FileMutationPrecondition.Any()),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!create.IsSuccess)
            {
                return FilePanelResult<long>.Failure(MapError(create.Error!));
            }
        }

        long transferred = 0;
        foreach (var file in plan.Value.Files)
        {
            var copied = await StreamFileAsync(
                    record,
                    sourceProvider,
                    destinationProvider,
                    file.Source,
                    file.Destination,
                    file.SourceEntry,
                    transferred,
                    plan.Value.TotalBytes,
                    deleteSource: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!copied.IsSuccess)
            {
                return copied;
            }

            transferred = checked(transferred + copied.Value);
        }

        if (record.Snapshot.Request.Operation == FilePanelTransferOperation.Move)
        {
            ReportProgress(
                record,
                "Deleting source",
                transferred,
                plan.Value.TotalBytes);
            var delete = await sourceProvider.DeleteAsync(
                    new FileDeleteRequest(
                        source,
                        recursive: true,
                        new FileMutationPrecondition.VersionMatches(sourceEntry.Version)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!delete.IsSuccess)
            {
                return Failure<long>(
                    FilePanelErrorCode.PartialTransfer,
                    "file_directory_move_source_delete_failed",
                    "The destination was written, but the source directory could not be deleted safely.");
            }
        }

        return FilePanelResult<long>.Success(transferred);
    }

    private async ValueTask<FilePanelResult<DirectoryTransferPlan>>
        BuildDirectoryTransferPlanAsync(
            IFileProvider sourceProvider,
            FileLocation source,
            FileLocation destination,
            CancellationToken cancellationToken)
    {
        var directories = new List<FileLocation> { destination };
        var files = new List<DirectoryTransferFile>();
        var pending = new Queue<(FileLocation Source, FileLocation Destination)>();
        pending.Enqueue((source, destination));
        long totalBytes = 0;
        var entryCount = 0;

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            FilePageToken? continuation = null;
            do
            {
                var page = await sourceProvider.ListAsync(
                        new FileListRequest(
                            current.Source,
                            sourceProvider.Capabilities.Limits.MaximumListPageSize,
                            continuation),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!page.IsSuccess)
                {
                    return FilePanelResult<DirectoryTransferPlan>.Failure(
                        MapError(page.Error!));
                }

                foreach (var entry in page.Value!.Items)
                {
                    entryCount++;
                    if (entryCount > MaximumDirectoryTransferEntries)
                    {
                        return Failure<DirectoryTransferPlan>(
                            FilePanelErrorCode.LimitExceeded,
                            "file_directory_entry_limit_exceeded",
                            "The directory contains too many entries for one queued transfer.");
                    }

                    var name = EntryName(entry.Location);
                    if (name is null)
                    {
                        return Failure<DirectoryTransferPlan>(
                            FilePanelErrorCode.InvalidLocation,
                            "file_directory_child_name_invalid",
                            "A listed child does not expose a transferable name.");
                    }

                    var childDestination = AppendChild(current.Destination, name);
                    if (entry.Kind == FileEntryKind.Directory)
                    {
                        directories.Add(childDestination);
                        pending.Enqueue((entry.Location.WithVersion(null), childDestination));
                        continue;
                    }

                    if (entry.Kind != FileEntryKind.File || entry.Size is not { } size)
                    {
                        return Failure<DirectoryTransferPlan>(
                            FilePanelErrorCode.UnsupportedCapability,
                            "file_directory_entry_unsupported",
                            "Directory transfer supports regular files and folders only.");
                    }

                    totalBytes = checked(totalBytes + size);
                    files.Add(new DirectoryTransferFile(
                        entry.Location,
                        childDestination,
                        entry));
                }

                continuation = page.Value.ContinuationToken;
            }
            while (continuation is not null);
        }

        return FilePanelResult<DirectoryTransferPlan>.Success(
            new DirectoryTransferPlan(directories, files, totalBytes));
    }

    private static bool CanStreamDirectory(
        IFileProvider sourceProvider,
        IFileProvider destinationProvider) =>
        sourceProvider.Capabilities.Supports(
            FileProviderCapability.List
            | FileProviderCapability.Stat
            | FileProviderCapability.RangedRead)
        && destinationProvider.Capabilities.Supports(
            FileProviderCapability.CreateDirectory
            | FileProviderCapability.StreamingWrite);

    private static string? EntryName(FileLocation location) => location.Address switch
    {
        FileLocationAddress.Hierarchical hierarchical =>
            hierarchical.Path.Name?.Value,
        FileLocationAddress.Object value =>
            ObjectName(value.Key.Value),
        _ => null,
    };

    private static string? ObjectName(string key)
    {
        var value = key.TrimEnd('/');
        if (value.Length == 0)
        {
            return null;
        }

        var separator = value.LastIndexOf('/');
        return separator < 0 ? value : value[(separator + 1)..];
    }

    private static FileLocation AppendChild(FileLocation parent, string name) =>
        parent.Address switch
        {
            FileLocationAddress.Hierarchical =>
                parent.Child(new FilePathSegment(name)),
            FileLocationAddress.Object value => FileLocation.ForObjectKey(
                parent.ProviderProfileId,
                parent.Authority!.Value,
                new FileObjectKey($"{value.Key.Value.TrimEnd('/')}/{name}")),
            FileLocationAddress.ContainerRoot => FileLocation.ForObjectKey(
                parent.ProviderProfileId,
                parent.Authority!.Value,
                new FileObjectKey(name)),
            _ => throw new InvalidOperationException(
                "The destination address cannot contain children."),
        };

    private static bool IsDescendant(FileLocation source, FileLocation destination)
    {
        if (source.ProviderProfileId != destination.ProviderProfileId
            || source.Authority != destination.Authority)
        {
            return false;
        }

        return (source.Address, destination.Address) switch
        {
            (
                FileLocationAddress.Hierarchical sourcePath,
                FileLocationAddress.Hierarchical destinationPath) =>
                destinationPath.Path.IsDescendantOf(sourcePath.Path),
            (
                FileLocationAddress.Object sourceObject,
                FileLocationAddress.Object destinationObject) =>
                destinationObject.Key.Value.StartsWith(
                    $"{sourceObject.Key.Value.TrimEnd('/')}/",
                    StringComparison.Ordinal),
            _ => false,
        };
    }

    private sealed record DirectoryTransferPlan(
        IReadOnlyList<FileLocation> Directories,
        IReadOnlyList<DirectoryTransferFile> Files,
        long TotalBytes);

    private sealed record DirectoryTransferFile(
        FileLocation Source,
        FileLocation Destination,
        FileEntry SourceEntry);
}
