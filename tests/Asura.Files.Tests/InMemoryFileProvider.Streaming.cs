namespace Asura.Files.Tests;

internal sealed partial class InMemoryFileProvider
{
    public ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination must be writable.", nameof(destination));
        }

        return ExecuteAsync(async token =>
        {
            if (request.MaximumBytes > Capabilities.Limits.MaximumReadBytes
                || request.BufferSize > Capabilities.Limits.MaximumBufferSize)
            {
                return Failure<FileReadReceipt>(FileProviderErrorCode.LimitExceeded, "The read is too large.");
            }

            var locationError = ValidateLocation(request.Location);
            if (locationError is not null)
            {
                return FileProviderResult<FileReadReceipt>.Failure(locationError);
            }

            byte[] content;
            FileEntry entry;
            lock (_gate)
            {
                if (!_nodes.TryGetValue(request.Location.Path, out var node))
                {
                    return Failure<FileReadReceipt>(FileProviderErrorCode.NotFound, "The file was not found.");
                }

                var versionError = CheckLocationVersion(request.Location, node);
                if (versionError is not null)
                {
                    return FileProviderResult<FileReadReceipt>.Failure(versionError);
                }

                if (node.Kind == FileEntryKind.Directory)
                {
                    return Failure<FileReadReceipt>(FileProviderErrorCode.IsDirectory, "A directory cannot be read.");
                }

                content = [.. node.Content];
                entry = ToEntry(request.Location.Path, node);
            }

            if (request.Offset > content.LongLength)
            {
                return Failure<FileReadReceipt>(
                    FileProviderErrorCode.RangeNotSatisfiable,
                    "The read offset is beyond the end of the file.");
            }

            var count = (int)Math.Min(request.MaximumBytes, content.LongLength - request.Offset);
            var written = 0;
            while (written < count)
            {
                token.ThrowIfCancellationRequested();
                var chunkSize = Math.Min(request.BufferSize, count - written);
                await destination.WriteAsync(
                    content.AsMemory((int)request.Offset + written, chunkSize),
                    token);
                written += chunkSize;
                progress?.Report(new FileTransferProgress(
                    FileTransferStage.Reading,
                    written,
                    count));
                await Task.Yield();
            }

            return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
                entry.Location,
                request.Offset,
                written,
                content.LongLength - request.Offset > request.MaximumBytes));
        }, cancellationToken);
    }

    public ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!source.CanRead)
        {
            throw new ArgumentException("The source must be readable.", nameof(source));
        }

        return ExecuteAsync(async token =>
        {
            if (request.BufferSize > Capabilities.Limits.MaximumBufferSize)
            {
                return Failure<FileWriteReceipt>(FileProviderErrorCode.LimitExceeded, "The write is too large.");
            }

            var locationError = ValidateLocation(request.Location);
            if (locationError is not null)
            {
                return FileProviderResult<FileWriteReceipt>.Failure(locationError);
            }

            if (request.Location.Path.IsRoot)
            {
                return Failure<FileWriteReceipt>(
                    FileProviderErrorCode.RootMutationNotAllowed,
                    "The root cannot be replaced.");
            }

            var content = new byte[checked((int)request.ContentLength)];
            var read = 0;
            while (read < content.Length)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(request.BufferSize, content.Length - read);
                var chunk = await source.ReadAsync(content.AsMemory(read, count), token);
                if (chunk == 0)
                {
                    return Failure<FileWriteReceipt>(
                        FileProviderErrorCode.UnexpectedEndOfStream,
                        "The source ended before its declared length.");
                }

                read += chunk;
                progress?.Report(new FileTransferProgress(
                    FileTransferStage.Writing,
                    read,
                    content.Length));
                await Task.Yield();
            }

            lock (_gate)
            {
                var parentError = ValidateParent(request.Location.Path);
                if (parentError is not null)
                {
                    return FileProviderResult<FileWriteReceipt>.Failure(parentError);
                }

                _nodes.TryGetValue(request.Location.Path, out var existing);
                if (existing?.Kind == FileEntryKind.Directory)
                {
                    return Failure<FileWriteReceipt>(FileProviderErrorCode.IsDirectory, "A directory cannot be replaced.");
                }

                var preconditionError = CheckPrecondition(request.Location, request.Precondition, existing);
                if (preconditionError is not null)
                {
                    return FileProviderResult<FileWriteReceipt>.Failure(preconditionError);
                }

                var replaced = existing is not null;
                var node = new MemoryNode(FileEntryKind.File, content, NextRevision());
                _nodes[request.Location.Path] = node;
                TouchParents(request.Location.Path);
                progress?.Report(new FileTransferProgress(
                    FileTransferStage.Completed,
                    content.Length,
                    content.Length));
                return FileProviderResult<FileWriteReceipt>.Success(new FileWriteReceipt(
                    ToEntry(request.Location.Path, node),
                    content.Length,
                    replaced));
            }
        }, cancellationToken);
    }

    private static async ValueTask<FileProviderResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<FileProviderResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<T>(FileProviderErrorCode.Cancelled, "The file operation was cancelled.");
        }
    }
}
