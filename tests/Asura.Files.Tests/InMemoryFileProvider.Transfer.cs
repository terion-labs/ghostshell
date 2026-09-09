namespace Asura.Files.Tests;

internal sealed partial class InMemoryFileProvider
{
    public ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async token =>
        {
            if (request.BufferSize > Capabilities.Limits.MaximumBufferSize)
            {
                return Failure<FileTransferReceipt>(
                    FileProviderErrorCode.LimitExceeded,
                    "The transfer is too large.");
            }

            var sourceError = ValidateMutableLocation(request.Source);
            var destinationError = ValidateMutableLocation(request.Destination);
            if (sourceError is not null || destinationError is not null)
            {
                return FileProviderResult<FileTransferReceipt>.Failure(sourceError ?? destinationError!);
            }

            if (request.Source.Path.Equals(request.Destination.Path))
            {
                return Failure<FileTransferReceipt>(
                    FileProviderErrorCode.Conflict,
                    "The source and destination are the same.");
            }

            KeyValuePair<FilePath, MemoryNode>[] snapshot;
            FileEntry sourceEntry;
            MemoryNode? expectedDestination;
            lock (_gate)
            {
                if (!_nodes.TryGetValue(request.Source.Path, out var source))
                {
                    return Failure<FileTransferReceipt>(FileProviderErrorCode.NotFound, "The source was not found.");
                }

                var sourceVersionError = CheckLocationVersion(request.Source, source);
                if (sourceVersionError is not null)
                {
                    return FileProviderResult<FileTransferReceipt>.Failure(sourceVersionError);
                }

                if (source.Kind == FileEntryKind.Directory
                    && request.Destination.Path.IsDescendantOf(request.Source.Path))
                {
                    return Failure<FileTransferReceipt>(
                        FileProviderErrorCode.InvalidLocation,
                        "A directory cannot be transferred into its descendant.");
                }

                var parentError = ValidateParent(request.Destination.Path);
                if (parentError is not null)
                {
                    return FileProviderResult<FileTransferReceipt>.Failure(parentError);
                }

                _nodes.TryGetValue(request.Destination.Path, out expectedDestination);
                if (source.Kind == FileEntryKind.Directory && expectedDestination is not null)
                {
                    return Failure<FileTransferReceipt>(
                        FileProviderErrorCode.Conflict,
                        "Directory replacement is not supported.");
                }

                if (source.Kind == FileEntryKind.File
                    && expectedDestination?.Kind == FileEntryKind.Directory)
                {
                    return Failure<FileTransferReceipt>(
                        FileProviderErrorCode.IsDirectory,
                        "A file cannot replace a directory.");
                }

                var preconditionError = CheckPrecondition(
                    request.Destination,
                    request.DestinationPrecondition,
                    expectedDestination);
                if (preconditionError is not null)
                {
                    return FileProviderResult<FileTransferReceipt>.Failure(preconditionError);
                }

                snapshot = [.. _nodes
                    .Where(pair => pair.Key.Equals(request.Source.Path)
                        || pair.Key.IsDescendantOf(request.Source.Path))
                    .Select(pair => new KeyValuePair<FilePath, MemoryNode>(
                        pair.Key,
                        new MemoryNode(pair.Value.Kind, [.. pair.Value.Content], pair.Value.Revision)))];
                sourceEntry = ToEntry(request.Source.Path, source);
            }

            var totalBytes = snapshot.Sum(pair => pair.Value.Content.LongLength);
            long transferred = 0;
            foreach (var pair in snapshot.Where(pair => pair.Value.Kind == FileEntryKind.File))
            {
                var position = 0;
                while (position < pair.Value.Content.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var count = Math.Min(request.BufferSize, pair.Value.Content.Length - position);
                    position += count;
                    transferred += count;
                    progress?.Report(new FileTransferProgress(
                        FileTransferStage.Writing,
                        transferred,
                        totalBytes));
                    await Task.Yield();
                }
            }

            lock (_gate)
            {
                if (!_nodes.TryGetValue(request.Source.Path, out var currentSource)
                    || VersionOf(currentSource) != sourceEntry.Version)
                {
                    return Failure<FileTransferReceipt>(
                        FileProviderErrorCode.PreconditionFailed,
                        "The source changed during the transfer.");
                }

                _nodes.TryGetValue(request.Destination.Path, out var currentDestination);
                if (!ReferenceEquals(currentDestination, expectedDestination))
                {
                    return Failure<FileTransferReceipt>(
                        FileProviderErrorCode.PreconditionFailed,
                        "The destination changed during the transfer.");
                }

                var preconditionError = CheckPrecondition(
                    request.Destination,
                    request.DestinationPrecondition,
                    currentDestination);
                if (preconditionError is not null)
                {
                    return FileProviderResult<FileTransferReceipt>.Failure(preconditionError);
                }

                if (currentDestination is not null)
                {
                    _nodes.Remove(request.Destination.Path);
                }

                foreach (var pair in snapshot)
                {
                    var destinationPath = ReplacePrefix(
                        pair.Key,
                        request.Source.Path,
                        request.Destination.Path);
                    pair.Value.Revision = NextRevision();
                    _nodes[destinationPath] = pair.Value;
                }

                var sourceDeleted = request.Kind == FileTransferKind.Move;
                if (sourceDeleted)
                {
                    foreach (var pair in snapshot)
                    {
                        _nodes.Remove(pair.Key);
                    }
                }

                TouchParents(request.Source.Path);
                TouchParents(request.Destination.Path);
                var destinationNode = _nodes[request.Destination.Path];
                progress?.Report(new FileTransferProgress(
                    FileTransferStage.Completed,
                    transferred,
                    totalBytes));
                return FileProviderResult<FileTransferReceipt>.Success(new FileTransferReceipt(
                    sourceEntry.Location,
                    ToEntry(request.Destination.Path, destinationNode),
                    request.Kind,
                    transferred,
                    expectedDestination is not null,
                    sourceDeleted));
            }
        }, cancellationToken);
}
