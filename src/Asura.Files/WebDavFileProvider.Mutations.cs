using System.Net;

namespace Asura.Files;

public sealed partial class WebDavFileProvider
{
    public ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
        FileCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(token => CreateDirectoryCoreAsync(request, token), cancellationToken);
    }

    public ValueTask<FileProviderResult<FileEntry>> RenameAsync(
        FileRenameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(token => RenameCoreAsync(request, token), cancellationToken);
    }

    public ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            token => TransferCoreAsync(request, progress, token),
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(token => DeleteCoreAsync(request, token), cancellationToken);
    }

    private async ValueTask<FileProviderResult<FileEntry>> CreateDirectoryCoreAsync(
        FileCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveLocation(request.Location, appendDirectorySlash: true);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(resolved.Error!);
        }

        if (resolved.Value!.Path.IsRoot)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The WebDAV provider root cannot be created or replaced.");
        }

        var precondition = RemoteFileProviderUtilities.MergeLocationVersion(
            request.Location,
            request.Precondition);
        if (!precondition.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(precondition.Error!);
        }

        if (precondition.Value is not FileMutationPrecondition.MustNotExist)
        {
            var existing = await StatCoreAsync(request.Location, cancellationToken).ConfigureAwait(false);
            if (existing.IsSuccess)
            {
                if (existing.Value!.Kind != FileEntryKind.Directory)
                {
                    return Failure<FileEntry>(
                        FileProviderErrorCode.Conflict,
                        "A non-collection resource already exists at the WebDAV destination.");
                }

                if (precondition.Value is FileMutationPrecondition.VersionMatches match
                    && existing.Value.Version != match.Version)
                {
                    return Failure<FileEntry>(
                        FileProviderErrorCode.PreconditionFailed,
                        "The existing WebDAV collection does not match the requested version.");
                }

                return existing;
            }

            if (existing.Error!.Code != FileProviderErrorCode.NotFound)
            {
                return existing;
            }

            if (precondition.Value is FileMutationPrecondition.MustExist)
            {
                return Failure<FileEntry>(
                    FileProviderErrorCode.NotFound,
                    "The WebDAV collection required by the precondition does not exist.");
            }

            if (precondition.Value is FileMutationPrecondition.VersionMatches)
            {
                return Failure<FileEntry>(
                    FileProviderErrorCode.PreconditionFailed,
                    "The WebDAV collection version cannot match because it does not exist.");
            }
        }

        using var mkcol = new HttpRequestMessage(MkColMethod, resolved.Value.Uri)
        {
            // A non-null, zero-length body prevents SocketsHttpHandler from replaying this
            // mutation after a response-less disconnect.
            Content = new ByteArrayContent([]),
        };
        AddNoCacheHeaders(mkcol);
        using var response = await _client.SendAsync(
            mkcol,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var responseScopeError = ValidateResponseScope(response);
        if (responseScopeError is not null)
        {
            return FileProviderResult<FileEntry>.Failure(responseScopeError);
        }

        if (response.StatusCode != HttpStatusCode.Created)
        {
            return HttpFailure<FileEntry>(
                response,
                new FileMutationPrecondition.MustNotExist(),
                methodNotAllowed: FileProviderErrorCode.Conflict);
        }

        return await StatCoreAsync(request.Location.WithVersion(null), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<FileProviderResult<FileEntry>> RenameCoreAsync(
        FileRenameRequest request,
        CancellationToken cancellationToken)
    {
        var source = ResolveLocation(request.Source);
        var destination = ResolveLocation(request.Destination);
        if (!source.IsSuccess || !destination.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(source.Error ?? destination.Error!);
        }

        if (source.Value!.Path.IsRoot || destination.Value!.Path.IsRoot)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The WebDAV provider root cannot be renamed or replaced.");
        }

        if (source.Value.Uri == destination.Value.Uri)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.Conflict,
                "The WebDAV rename source and destination are identical.");
        }

        var sourceStat = await StatCoreAsync(request.Source, cancellationToken).ConfigureAwait(false);
        if (!sourceStat.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(sourceStat.Error!);
        }

        if (sourceStat.Value!.Kind == FileEntryKind.Directory)
        {
            source = ResolveLocation(request.Source, appendDirectorySlash: true);
            destination = ResolveLocation(request.Destination, appendDirectorySlash: true);
            if (!source.IsSuccess || !destination.IsSuccess)
            {
                return FileProviderResult<FileEntry>.Failure(source.Error ?? destination.Error!);
            }
        }

        var destinationCondition = await PrepareDestinationAsync(
            request.Destination,
            request.DestinationPrecondition,
            cancellationToken).ConfigureAwait(false);
        if (!destinationCondition.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(destinationCondition.Error!);
        }

        var move = await SendCopyMoveAsync(
            MoveMethod,
            source.Value!,
            destination.Value!,
            sourceStat.Value!.Version,
            destinationCondition.Value!,
            cancellationToken).ConfigureAwait(false);
        if (!move.IsSuccess)
        {
            return FileProviderResult<FileEntry>.Failure(move.Error!);
        }

        return await StatCoreAsync(request.Destination.WithVersion(null), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<FileProviderResult<FileTransferReceipt>> TransferCoreAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var limitError = RemoteFileProviderUtilities.ValidateBufferSize(
            request.BufferSize,
            Capabilities.Limits.MaximumBufferSize);
        if (limitError is not null)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(limitError);
        }

        var source = ResolveLocation(request.Source);
        var destination = ResolveLocation(request.Destination);
        if (!source.IsSuccess || !destination.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(source.Error ?? destination.Error!);
        }

        if (source.Value!.Path.IsRoot || destination.Value!.Path.IsRoot)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The WebDAV provider root cannot be transferred or replaced.");
        }

        if (source.Value.Uri == destination.Value.Uri)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.Conflict,
                "The WebDAV transfer source and destination are identical.");
        }

        var sourceStat = await StatCoreAsync(request.Source, cancellationToken).ConfigureAwait(false);
        if (!sourceStat.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(sourceStat.Error!);
        }

        var sourceEntry = sourceStat.Value!;
        if (sourceEntry.Kind != FileEntryKind.File)
        {
            return Failure<FileTransferReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                "WebDAV transfer does not claim recursive collection COPY or MOVE.");
        }

        var size = sourceEntry.Size!.Value;
        var destinationCondition = await PrepareDestinationAsync(
            request.Destination,
            request.DestinationPrecondition,
            cancellationToken).ConfigureAwait(false);
        if (!destinationCondition.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(destinationCondition.Error!);
        }

        progress?.Report(new FileTransferProgress(FileTransferStage.Committing, 0, size));
        var method = request.Kind == FileTransferKind.Copy ? CopyMethod : MoveMethod;
        var mutation = await SendCopyMoveAsync(
            method,
            source.Value,
            destination.Value,
            sourceEntry.Version,
            destinationCondition.Value!,
            cancellationToken).ConfigureAwait(false);
        if (!mutation.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(mutation.Error!);
        }

        var destinationStat = await StatCoreAsync(
            request.Destination.WithVersion(null),
            cancellationToken).ConfigureAwait(false);
        if (!destinationStat.IsSuccess)
        {
            return FileProviderResult<FileTransferReceipt>.Failure(destinationStat.Error!);
        }

        progress?.Report(new FileTransferProgress(FileTransferStage.Completed, size, size));
        var replacedExisting = destinationCondition.Value!.Precondition is FileMutationPrecondition.Any
            ? mutation.Value == HttpStatusCode.NoContent
            : destinationCondition.Value.ReplacedExisting;
        return FileProviderResult<FileTransferReceipt>.Success(new FileTransferReceipt(
            sourceEntry.Location,
            destinationStat.Value!,
            request.Kind,
            size,
            replacedExisting,
            SourceDeleted: request.Kind == FileTransferKind.Move));
    }

    private async ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteCoreAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveLocation(request.Location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileDeleteReceipt>.Failure(resolved.Error!);
        }

        if (resolved.Value!.Path.IsRoot)
        {
            return Failure<FileDeleteReceipt>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The WebDAV provider root cannot be deleted.");
        }

        var stat = await StatCoreAsync(request.Location, cancellationToken).ConfigureAwait(false);
        if (!stat.IsSuccess)
        {
            return FileProviderResult<FileDeleteReceipt>.Failure(stat.Error!);
        }

        var entry = stat.Value!;
        var precondition = RemoteFileProviderUtilities.MergeLocationVersion(
            request.Location,
            request.Precondition);
        if (!precondition.IsSuccess)
        {
            return FileProviderResult<FileDeleteReceipt>.Failure(precondition.Error!);
        }

        if (precondition.Value is FileMutationPrecondition.MustNotExist)
        {
            return Failure<FileDeleteReceipt>(
                FileProviderErrorCode.Conflict,
                "The WebDAV resource exists but the delete required it not to exist.");
        }

        if (entry.Kind == FileEntryKind.Directory && !request.Recursive)
        {
            return Failure<FileDeleteReceipt>(
                FileProviderErrorCode.UnsupportedCapability,
                "RFC 4918 DELETE is recursive for collections; a race-free shallow delete is unavailable.");
        }

        if (entry.Kind == FileEntryKind.Directory)
        {
            resolved = ResolveLocation(request.Location, appendDirectorySlash: true);
            if (!resolved.IsSuccess)
            {
                return FileProviderResult<FileDeleteReceipt>.Failure(resolved.Error!);
            }
        }

        using var delete = new HttpRequestMessage(HttpMethod.Delete, resolved.Value!.Uri)
        {
            // DELETE has no WebDAV request body, but the explicit empty body preserves the
            // governed single-dispatch boundary in SocketsHttpHandler.
            Content = new ByteArrayContent([]),
        };
        AddMutationPrecondition(delete, precondition.Value!);
        if (entry.Kind == FileEntryKind.Directory)
        {
            delete.Headers.TryAddWithoutValidation("Depth", "infinity");
        }

        AddNoCacheHeaders(delete);
        using var response = await _client.SendAsync(
            delete,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var responseScopeError = ValidateResponseScope(response);
        if (responseScopeError is not null)
        {
            return FileProviderResult<FileDeleteReceipt>.Failure(responseScopeError);
        }

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
        {
            return HttpFailure<FileDeleteReceipt>(response, precondition.Value);
        }

        return FileProviderResult<FileDeleteReceipt>.Success(new FileDeleteReceipt(
            request.Location,
            WasDirectory: entry.Kind == FileEntryKind.Directory));
    }

    private async ValueTask<FileProviderResult<DestinationCondition>> PrepareDestinationAsync(
        FileLocation destination,
        FileMutationPrecondition requestedPrecondition,
        CancellationToken cancellationToken)
    {
        var precondition = RemoteFileProviderUtilities.MergeLocationVersion(
            destination,
            requestedPrecondition);
        if (!precondition.IsSuccess)
        {
            return FileProviderResult<DestinationCondition>.Failure(precondition.Error!);
        }

        return precondition.Value switch
        {
            FileMutationPrecondition.Any => FileProviderResult<DestinationCondition>.Success(
                new DestinationCondition(precondition.Value, Overwrite: true, ETag: null, ReplacedExisting: true)),
            FileMutationPrecondition.MustNotExist => FileProviderResult<DestinationCondition>.Success(
                new DestinationCondition(precondition.Value, Overwrite: false, ETag: null, ReplacedExisting: false)),
            FileMutationPrecondition.VersionMatches match => FileProviderResult<DestinationCondition>.Success(
                new DestinationCondition(precondition.Value, Overwrite: true, match.Version, ReplacedExisting: true)),
            FileMutationPrecondition.MustExist => await ExistingDestinationConditionAsync(
                destination,
                precondition.Value,
                cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(requestedPrecondition), requestedPrecondition, null),
        };
    }

    private async ValueTask<FileProviderResult<DestinationCondition>> ExistingDestinationConditionAsync(
        FileLocation destination,
        FileMutationPrecondition precondition,
        CancellationToken cancellationToken)
    {
        var stat = await StatCoreAsync(destination.WithVersion(null), cancellationToken).ConfigureAwait(false);
        return stat.IsSuccess
            ? FileProviderResult<DestinationCondition>.Success(new DestinationCondition(
                precondition,
                Overwrite: true,
                stat.Value!.Version,
                ReplacedExisting: true))
            : FileProviderResult<DestinationCondition>.Failure(stat.Error!);
    }

    private async ValueTask<FileProviderResult<HttpStatusCode>> SendCopyMoveAsync(
        HttpMethod method,
        ResolvedWebDavLocation source,
        ResolvedWebDavLocation destination,
        FileVersion sourceVersion,
        DestinationCondition destinationCondition,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, source.Uri)
        {
            // COPY and MOVE have no request body. The explicit empty body prevents
            // SocketsHttpHandler from replaying an ambiguous mutation.
            Content = new ByteArrayContent([]),
        };
        request.Headers.TryAddWithoutValidation("Destination", destination.Uri.AbsoluteUri);
        request.Headers.TryAddWithoutValidation(
            "Overwrite",
            destinationCondition.Overwrite ? "T" : "F");
        var conditions = $"<{source.Uri.AbsoluteUri}> ([{sourceVersion.Value}])";
        if (destinationCondition.ETag is { } destinationEtag)
        {
            conditions += $" <{destination.Uri.AbsoluteUri}> ([{destinationEtag.Value}])";
        }

        request.Headers.TryAddWithoutValidation("If", conditions);
        AddNoCacheHeaders(request);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var responseScopeError = ValidateResponseScope(response);
        if (responseScopeError is not null)
        {
            return FileProviderResult<HttpStatusCode>.Failure(responseScopeError);
        }

        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.NoContent))
        {
            return HttpFailure<HttpStatusCode>(response, destinationCondition.Precondition);
        }

        return FileProviderResult<HttpStatusCode>.Success(response.StatusCode);
    }

    private sealed record DestinationCondition(
        FileMutationPrecondition Precondition,
        bool Overwrite,
        FileVersion? ETag,
        bool ReplacedExisting);
}
