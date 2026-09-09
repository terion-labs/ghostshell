using System.IO.Pipes;
using Asura.Application;
using Asura.Core;

namespace Asura.Files;

public sealed partial class FilePanelClient
{
    private const int TransferBufferSize = 64 * 1024;
    private static readonly TimeSpan ProgressPublicationInterval =
        TimeSpan.FromMilliseconds(100);
    private readonly object _transferGate = new();
    private readonly Dictionary<FilePanelTransferId, TransferRecord> _transfers = [];
    private readonly CancellationTokenSource _transferLifetime = new();
    private readonly SemaphoreSlim _transferExecutionGate = new(1, 1);
    private bool _disposed;

    public event EventHandler? TransfersChanged;

    public IReadOnlyList<FilePanelTransferSnapshot> Transfers
    {
        get
        {
            lock (_transferGate)
            {
                return Array.AsReadOnly(_transfers.Values
                    .Select(record => record.Snapshot)
                    .OrderByDescending(snapshot => snapshot.QueuedAt)
                    .ToArray());
            }
        }
    }

    public async ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled<FilePanelTransferSnapshot>();
        }

        if (!TryResolve(request.Source, out var sourceRegistration, out _, out var error))
        {
            return FilePanelResult<FilePanelTransferSnapshot>.Failure(error!);
        }

        if (!TryResolve(request.Destination, out var destinationRegistration, out _, out error))
        {
            return FilePanelResult<FilePanelTransferSnapshot>.Failure(error!);
        }

        var sourceProvider = sourceRegistration!;
        var destinationProvider = destinationRegistration!;
        var effectiveDestination = request.Destination.WithVersion(null);
        if (request.ConflictPolicy is FilePanelConflictPolicy.Skip or FilePanelConflictPolicy.KeepBoth)
        {
            var existence = await DestinationExistsAsync(
                    destinationProvider,
                    effectiveDestination,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!existence.IsSuccess)
            {
                return FilePanelResult<FilePanelTransferSnapshot>.Failure(existence.Error!);
            }

            if (existence.Value == true && request.ConflictPolicy == FilePanelConflictPolicy.Skip)
            {
                var skipped = CreateRecord(
                    request,
                    effectiveDestination,
                    FilePanelTransferState.Skipped,
                    "Skipped existing destination",
                    cancellationToken: null);
                skipped.Snapshot = skipped.Snapshot with
                {
                    CompletedAt = _timeProvider.GetUtcNow(),
                };
                AddRecord(skipped);
                return FilePanelResult<FilePanelTransferSnapshot>.Success(skipped.Snapshot);
            }

            if (existence.Value == true)
            {
                var alternative = await FindKeepBothDestinationAsync(
                        destinationProvider,
                        effectiveDestination,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!alternative.IsSuccess)
                {
                    return FilePanelResult<FilePanelTransferSnapshot>.Failure(alternative.Error!);
                }

                effectiveDestination = alternative.Value!;
            }
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_transferLifetime.Token);
        var record = CreateRecord(
            request,
            effectiveDestination,
            FilePanelTransferState.Queued,
            "Queued",
            linked);
        AddRecord(record);
        _ = RunTransferAsync(record, sourceProvider, destinationProvider);
        return FilePanelResult<FilePanelTransferSnapshot>.Success(record.Snapshot);
    }

    public ValueTask<FilePanelResult<Unit>> CancelAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Cancelled<Unit>());
        }

        CancellationTokenSource cancellation;
        lock (_transferGate)
        {
            if (!_transfers.TryGetValue(id, out var record))
            {
                return ValueTask.FromResult(Failure<Unit>(
                    FilePanelErrorCode.NotFound,
                    "file_transfer_not_found",
                    "The requested transfer is no longer in the queue."));
            }

            if (!record.Snapshot.CanCancel || record.Cancellation is null)
            {
                return ValueTask.FromResult(Failure<Unit>(
                    FilePanelErrorCode.Conflict,
                    "file_transfer_not_cancellable",
                    "This transfer has already reached a terminal state."));
            }

            record.Snapshot = record.Snapshot with
            {
                CancellationRequested = true,
                Stage = "Cancelling",
            };
            cancellation = record.Cancellation;
        }

        TransfersChanged?.Invoke(this, EventArgs.Empty);
        cancellation.Cancel();
        return ValueTask.FromResult(FilePanelResult<Unit>.Success(Unit.Value));
    }

    public async ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FilePanelTransferRequest? request;
        lock (_transferGate)
        {
            request = _transfers.TryGetValue(id, out var record) && record.Snapshot.CanRetry
                ? record.Snapshot.Request
                : null;
        }

        return request is null
            ? Failure<FilePanelTransferSnapshot>(
                FilePanelErrorCode.Conflict,
                "file_transfer_not_retryable",
                "Only failed or cancelled transfers can be retried.")
            : await EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transferLifetime.Cancel();
        TransferRecord[] records;
        lock (_transferGate)
        {
            records = [.. _transfers.Values];
        }

        foreach (var record in records)
        {
            record.Cancellation?.Cancel();
            record.Cancellation?.Dispose();
        }

        _transferLifetime.Dispose();
    }

    private async Task RunTransferAsync(
        TransferRecord record,
        FileProviderRegistration sourceRegistration,
        FileProviderRegistration destinationRegistration)
    {
        var cancellationToken = record.Cancellation!.Token;
        try
        {
            await _transferExecutionGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateRecord(record, snapshot => snapshot with
            {
                State = FilePanelTransferState.Cancelled,
                Stage = "Cancelled",
                Error = new FilePanelError(
                    FilePanelErrorCode.Cancelled,
                    "file_transfer_cancelled",
                    "The transfer was cancelled.",
                    true),
                CompletedAt = _timeProvider.GetUtcNow(),
            });
            return;
        }

        try
        {
            await RunTransferCoreAsync(
                    record,
                    sourceRegistration,
                    destinationRegistration)
                .ConfigureAwait(false);
        }
        finally
        {
            _transferExecutionGate.Release();
        }
    }

    private async Task RunTransferCoreAsync(
        TransferRecord record,
        FileProviderRegistration sourceRegistration,
        FileProviderRegistration destinationRegistration)
    {
        var cancellationToken = record.Cancellation!.Token;
        UpdateRecord(record, snapshot => snapshot with
        {
            State = FilePanelTransferState.Running,
            Stage = "Preparing transfer",
            StartedAt = _timeProvider.GetUtcNow(),
        });

        FilePanelResult<long> result;
        try
        {
            result = await RunResolvedTransferAsync(
                    record,
                    sourceRegistration,
                    destinationRegistration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = Cancelled<long>();
        }
        catch (Exception)
        {
            result = Failure<long>(
                FilePanelErrorCode.IoFailure,
                "file_transfer_unexpected_failure",
                "The transfer failed unexpectedly inside the provider boundary.",
                retryable: true);
        }

        if (cancellationToken.IsCancellationRequested
            || result.Error?.Code == FilePanelErrorCode.Cancelled)
        {
            UpdateRecord(record, snapshot => snapshot with
            {
                State = FilePanelTransferState.Cancelled,
                Stage = "Cancelled",
                Error = new FilePanelError(
                    FilePanelErrorCode.Cancelled,
                    "file_transfer_cancelled",
                    "The transfer was cancelled.",
                    true),
                CompletedAt = _timeProvider.GetUtcNow(),
            });
            return;
        }

        if (!result.IsSuccess)
        {
            UpdateRecord(record, snapshot => snapshot with
            {
                State = FilePanelTransferState.Failed,
                Stage = "Failed",
                Error = result.Error,
                CompletedAt = _timeProvider.GetUtcNow(),
            });
            return;
        }

        UpdateRecord(record, snapshot => snapshot with
        {
            State = FilePanelTransferState.Completed,
            Stage = "Completed",
            BytesTransferred = result.Value,
            TotalBytes = result.Value,
            CompletedAt = _timeProvider.GetUtcNow(),
        });
    }

    private async ValueTask<FilePanelResult<long>> RunResolvedTransferAsync(
        TransferRecord record,
        FileProviderRegistration sourceRegistration,
        FileProviderRegistration destinationRegistration,
        CancellationToken cancellationToken)
    {
        var sameProvider = ReferenceEquals(sourceRegistration, destinationRegistration);
        var requestedCapability = record.Snapshot.Request.Operation
            == FilePanelTransferOperation.Copy
            ? FileProviderCapability.Copy
            : FileProviderCapability.Move;
        if (sameProvider
            && sourceRegistration.Provider.Capabilities.Supports(
                requestedCapability))
        {
            return await RunSameProviderTransferAsync(
                    record,
                    sourceRegistration.Provider,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!TryResolve(record.Snapshot.Request.Source, out _, out var source, out var error)
            || !TryResolve(
                record.Snapshot.EffectiveDestination,
                out _,
                out var destination,
                out error))
        {
            return FilePanelResult<long>.Failure(error!);
        }

        var sourceStat = await sourceRegistration.Provider.StatAsync(
                new FileStatRequest(source!),
                cancellationToken)
            .ConfigureAwait(false);
        if (!sourceStat.IsSuccess)
        {
            return FilePanelResult<long>.Failure(MapError(sourceStat.Error!));
        }

        if (sourceStat.Value!.Kind == FileEntryKind.Directory)
        {
            return await RunDirectoryTransferAsync(
                    record,
                    sourceRegistration.Provider,
                    destinationRegistration.Provider,
                    source!,
                    destination!,
                    sourceStat.Value,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return sameProvider
            ? await RunSameProviderTransferAsync(
                    record,
                    sourceRegistration.Provider,
                    cancellationToken)
                .ConfigureAwait(false)
            : await RunCrossProviderTransferAsync(
                    record,
                    sourceRegistration.Provider,
                    destinationRegistration.Provider,
                    source!,
                    destination!,
                    sourceStat.Value,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private async ValueTask<FilePanelResult<long>> RunSameProviderTransferAsync(
        TransferRecord record,
        IFileProvider provider,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(record.Snapshot.Request.Source, out _, out var source, out var error)
            || !TryResolve(record.Snapshot.EffectiveDestination, out _, out var destination, out error))
        {
            return FilePanelResult<long>.Failure(error!);
        }

        var requestedCapability = record.Snapshot.Request.Operation == FilePanelTransferOperation.Copy
            ? FileProviderCapability.Copy
            : FileProviderCapability.Move;
        if (!provider.Capabilities.Supports(requestedCapability))
        {
            return Failure<long>(
                FilePanelErrorCode.UnsupportedCapability,
                "file_transfer_operation_unsupported",
                "This provider does not support the requested transfer operation.");
        }

        var progress = new InlineProgress<FileTransferProgress>(value =>
            ReportProgress(record, value.Stage.ToString(), value.BytesTransferred, value.TotalBytes));
        var result = await provider.TransferAsync(
                new FileTransferRequest(
                    source!,
                    destination!,
                    record.Snapshot.Request.Operation == FilePanelTransferOperation.Copy
                        ? FileTransferKind.Copy
                        : FileTransferKind.Move,
                    Math.Min(TransferBufferSize, provider.Capabilities.Limits.MaximumBufferSize),
                    DestinationPrecondition(record.Snapshot.Request.ConflictPolicy)),
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? FilePanelResult<long>.Success(result.Value!.BytesTransferred)
            : FilePanelResult<long>.Failure(MapError(result.Error!));
    }

    private async ValueTask<FilePanelResult<long>> RunCrossProviderTransferAsync(
        TransferRecord record,
        IFileProvider sourceProvider,
        IFileProvider destinationProvider,
        FileLocation source,
        FileLocation destination,
        FileEntry sourceEntry,
        CancellationToken cancellationToken)
    {
        if (!sourceProvider.Capabilities.Supports(FileProviderCapability.RangedRead)
            || !destinationProvider.Capabilities.Supports(FileProviderCapability.StreamingWrite))
        {
            return Failure<long>(
                FilePanelErrorCode.UnsupportedCapability,
                "file_cross_provider_streaming_unsupported",
                "Cross-provider transfer requires bounded reads and streaming writes.");
        }

        if (sourceEntry.Kind != FileEntryKind.File || sourceEntry.Size is not { } contentLength)
        {
            return Failure<long>(
                FilePanelErrorCode.UnsupportedCapability,
                "file_cross_provider_directory_unsupported",
                "Cross-provider queue transfers currently require a regular file with known size.");
        }

        return await StreamFileAsync(
                record,
                sourceProvider,
                destinationProvider,
                source,
                destination,
                sourceEntry,
                progressBase: 0,
                aggregateTotalBytes: contentLength,
                deleteSource: record.Snapshot.Request.Operation
                    == FilePanelTransferOperation.Move,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<FilePanelResult<long>> StreamFileAsync(
        TransferRecord record,
        IFileProvider sourceProvider,
        IFileProvider destinationProvider,
        FileLocation source,
        FileLocation destination,
        FileEntry sourceEntry,
        long progressBase,
        long aggregateTotalBytes,
        bool deleteSource,
        CancellationToken cancellationToken)
    {
        if (sourceEntry.Size is not { } contentLength)
        {
            return Failure<long>(
                FilePanelErrorCode.UnsupportedCapability,
                "file_cross_provider_size_unknown",
                "The source provider did not report a bounded file size.");
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var output = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.None);
        using var input = new AnonymousPipeClientStream(
            PipeDirection.In,
            output.GetClientHandleAsString());
        var bufferSize = Math.Min(
            TransferBufferSize,
            Math.Min(
                sourceProvider.Capabilities.Limits.MaximumBufferSize,
                destinationProvider.Capabilities.Limits.MaximumBufferSize));
        var writeProgress = new InlineProgress<FileTransferProgress>(value =>
            ReportProgress(
                record,
                "Writing destination",
                progressBase + value.BytesTransferred,
                aggregateTotalBytes));
        var readTask = ReadFileChunksAsync(
            record,
            sourceProvider,
            source,
            output,
            contentLength,
            bufferSize,
            progressBase,
            aggregateTotalBytes,
            operation.Token);
        var writeTask = destinationProvider.WriteAsync(
                new FileWriteRequest(
                    destination,
                    contentLength,
                    bufferSize,
                    DestinationPrecondition(record.Snapshot.Request.ConflictPolicy)),
                input,
                writeProgress,
                operation.Token)
            .AsTask();

        FileProviderResult<long>? readResult = null;
        FileProviderResult<FileWriteReceipt>? writeResult = null;
        try
        {
            var first = await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);
            if (ReferenceEquals(first, readTask))
            {
                readResult = await readTask.ConfigureAwait(false);
                if (!readResult.IsSuccess)
                {
                    operation.Cancel();
                }

                writeResult = await writeTask.ConfigureAwait(false);
            }
            else
            {
                writeResult = await writeTask.ConfigureAwait(false);
                if (!writeResult.IsSuccess)
                {
                    operation.Cancel();
                }

                readResult = await readTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled<long>();
            }
        }

        if (readResult is null || !readResult.IsSuccess)
        {
            return readResult?.Error is { } readError
                ? FilePanelResult<long>.Failure(MapError(readError))
                : Failure<long>(
                    FilePanelErrorCode.PartialTransfer,
                    "file_transfer_source_failed",
                    "The source stream failed before the transfer completed.");
        }

        if (writeResult is null || !writeResult.IsSuccess)
        {
            return writeResult?.Error is { } writeError
                ? FilePanelResult<long>.Failure(MapError(writeError))
                : Failure<long>(
                    FilePanelErrorCode.PartialTransfer,
                    "file_transfer_destination_failed",
                    "The destination stream failed before the transfer completed.");
        }

        if (deleteSource)
        {
            var delete = await sourceProvider.DeleteAsync(
                    new FileDeleteRequest(
                        source,
                        recursive: false,
                        new FileMutationPrecondition.VersionMatches(sourceEntry.Version)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!delete.IsSuccess)
            {
                return Failure<long>(
                    FilePanelErrorCode.PartialTransfer,
                    "file_move_source_delete_failed",
                    "The destination was written, but the source could not be deleted safely.");
            }
        }

        return FilePanelResult<long>.Success(writeResult.Value!.BytesWritten);
    }

    private async Task<FileProviderResult<long>> ReadFileChunksAsync(
        TransferRecord record,
        IFileProvider sourceProvider,
        FileLocation source,
        Stream output,
        long contentLength,
        int bufferSize,
        long progressBase,
        long aggregateTotalBytes,
        CancellationToken cancellationToken)
    {
        long offset = 0;
        while (offset < contentLength)
        {
            var chunkLength = Math.Min(
                contentLength - offset,
                sourceProvider.Capabilities.Limits.MaximumReadBytes);
            var chunkOffset = offset;
            var progress = new InlineProgress<FileTransferProgress>(value =>
                ReportProgress(
                    record,
                    "Reading source",
                    progressBase + chunkOffset + value.BytesTransferred,
                    aggregateTotalBytes));
            var result = await sourceProvider.ReadAsync(
                    new FileReadRequest(
                        source,
                        chunkOffset,
                        chunkLength,
                        bufferSize),
                    output,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return FileProviderResult<long>.Failure(result.Error!);
            }

            if (result.Value!.BytesRead <= 0)
            {
                return FileProviderResult<long>.Failure(
                    FileProviderError.Create(
                        FileProviderErrorCode.IoFailure,
                        "The source stream ended before the reported file size."));
            }

            offset = checked(offset + result.Value.BytesRead);
        }

        return FileProviderResult<long>.Success(offset);
    }

    private async ValueTask<FilePanelResult<bool>> DestinationExistsAsync(
        FileProviderRegistration registration,
        FilePanelLocation location,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(location, out _, out var mapped, out var error))
        {
            return FilePanelResult<bool>.Failure(error!);
        }

        var stat = await registration.Provider.StatAsync(
                new FileStatRequest(mapped!),
                cancellationToken)
            .ConfigureAwait(false);
        if (stat.IsSuccess)
        {
            return FilePanelResult<bool>.Success(true);
        }

        return stat.Error!.Code == FileProviderErrorCode.NotFound
            ? FilePanelResult<bool>.Success(false)
            : FilePanelResult<bool>.Failure(MapError(stat.Error));
    }

    private async ValueTask<FilePanelResult<FilePanelLocation>> FindKeepBothDestinationAsync(
        FileProviderRegistration registration,
        FilePanelLocation destination,
        CancellationToken cancellationToken)
    {
        for (var suffix = 2; suffix <= 1000; suffix++)
        {
            var candidate = KeepBothCandidate(destination, suffix);
            if (candidate is null)
            {
                return Failure<FilePanelLocation>(
                    FilePanelErrorCode.UnsupportedCapability,
                    "file_keep_both_unsupported",
                    "This destination shape cannot generate a safe keep-both name.");
            }

            var exists = await DestinationExistsAsync(registration, candidate, cancellationToken)
                .ConfigureAwait(false);
            if (!exists.IsSuccess)
            {
                return FilePanelResult<FilePanelLocation>.Failure(exists.Error!);
            }

            if (exists.Value == false)
            {
                return FilePanelResult<FilePanelLocation>.Success(candidate);
            }
        }

        return Failure<FilePanelLocation>(
            FilePanelErrorCode.Conflict,
            "file_keep_both_exhausted",
            "No available keep-both name could be reserved.");
    }

    private static FilePanelLocation? KeepBothCandidate(
        FilePanelLocation destination,
        int suffix) => destination.Address switch
        {
            FilePanelAddress.Hierarchical hierarchical when hierarchical.Path.Name is { } name =>
                destination.Parent.Child(new FilePanelPathSegment(WithCopySuffix(name.Value, suffix))),
            FilePanelAddress.ObjectKey objectKey => new FilePanelLocation(
                destination.ProviderProfileId,
                destination.Authority,
                new FilePanelAddress.ObjectKey(WithCopySuffix(objectKey.Key, suffix))),
            _ => null,
        };

    private static string WithCopySuffix(string value, int suffix)
    {
        var separator = value.LastIndexOf('/');
        var prefix = separator >= 0 ? value[..(separator + 1)] : string.Empty;
        var name = separator >= 0 ? value[(separator + 1)..] : value;
        var extensionSeparator = name.LastIndexOf('.');
        var extension = extensionSeparator > 0 && extensionSeparator < name.Length - 1
            ? name[extensionSeparator..]
            : string.Empty;
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        return $"{prefix}{stem} (copy {suffix}){extension}";
    }

    private static FileMutationPrecondition DestinationPrecondition(
        FilePanelConflictPolicy policy) => policy == FilePanelConflictPolicy.Replace
        ? new FileMutationPrecondition.Any()
        : new FileMutationPrecondition.MustNotExist();

    private TransferRecord CreateRecord(
        FilePanelTransferRequest request,
        FilePanelLocation destination,
        FilePanelTransferState state,
        string stage,
        CancellationTokenSource? cancellationToken) => new(
        new FilePanelTransferSnapshot(
            FilePanelTransferId.New(),
            request,
            destination,
            state,
            stage,
            0,
            null,
            null,
            _timeProvider.GetUtcNow(),
            null,
            null),
        cancellationToken);

    private void AddRecord(TransferRecord record)
    {
        lock (_transferGate)
        {
            _transfers.Add(record.Snapshot.Id, record);
        }

        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateRecord(
        TransferRecord record,
        Func<FilePanelTransferSnapshot, FilePanelTransferSnapshot> update)
    {
        lock (_transferGate)
        {
            if (!_transfers.ContainsKey(record.Snapshot.Id))
            {
                return;
            }

            record.Snapshot = update(record.Snapshot);
        }

        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReportProgress(
        TransferRecord record,
        string stage,
        long bytesTransferred,
        long? totalBytes)
    {
        var publish = false;
        lock (_transferGate)
        {
            if (!_transfers.ContainsKey(record.Snapshot.Id)
                || record.Snapshot.CancellationRequested)
            {
                return;
            }

            record.Snapshot = record.Snapshot with
            {
                Stage = stage,
                BytesTransferred = bytesTransferred,
                TotalBytes = totalBytes,
            };
            var timestamp = _timeProvider.GetTimestamp();
            publish = !record.HasPublishedProgress
                || _timeProvider.GetElapsedTime(
                    record.LastProgressPublishedTimestamp,
                    timestamp) >= ProgressPublicationInterval
                || totalBytes is { } total && bytesTransferred >= total;
            if (publish)
            {
                record.HasPublishedProgress = true;
                record.LastProgressPublishedTimestamp = timestamp;
            }
        }

        if (publish)
        {
            TransfersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static FilePanelResult<T> Cancelled<T>() =>
        Failure<T>(
            FilePanelErrorCode.Cancelled,
            "file_operation_cancelled",
            "The file operation was cancelled.",
            retryable: true);

    private sealed class TransferRecord(
        FilePanelTransferSnapshot snapshot,
        CancellationTokenSource? cancellation)
    {
        public FilePanelTransferSnapshot Snapshot { get; set; } = snapshot;

        public CancellationTokenSource? Cancellation { get; } = cancellation;

        public bool HasPublishedProgress { get; set; }

        public long LastProgressPublishedTimestamp { get; set; }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
