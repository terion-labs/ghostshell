using System.Net;
using System.Net.Http.Headers;

namespace Asura.Files;

public sealed partial class WebDavFileProvider
{
    public ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The read destination stream must be writable.", nameof(destination));
        }

        return ExecuteAsync(
            token => ReadCoreAsync(request, destination, progress, token),
            cancellationToken);
    }

    public ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The write source stream must be readable.", nameof(source));
        }

        return ExecuteAsync(
            token => WriteCoreAsync(request, source, progress, token),
            cancellationToken);
    }

    private async ValueTask<FileProviderResult<FileReadReceipt>> ReadCoreAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var limitError = RemoteFileProviderUtilities.ValidateBufferSize(
            request.BufferSize,
            Capabilities.Limits.MaximumBufferSize);
        if (request.MaximumBytes > Capabilities.Limits.MaximumReadBytes)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.LimitExceeded,
                "The requested read exceeds the provider's bounded-read limit.");
        }

        if (limitError is not null)
        {
            return FileProviderResult<FileReadReceipt>.Failure(limitError);
        }

        var stat = await StatCoreAsync(request.Location, cancellationToken).ConfigureAwait(false);
        if (!stat.IsSuccess)
        {
            return FileProviderResult<FileReadReceipt>.Failure(stat.Error!);
        }

        var entry = stat.Value!;
        if (entry.Kind == FileEntryKind.Directory)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.IsDirectory,
                "A WebDAV collection cannot be read as a file.");
        }

        var size = entry.Size!.Value;
        if (request.Offset > size)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.RangeNotSatisfiable,
                "The requested offset is beyond the end of the WebDAV resource.");
        }

        var remaining = size - request.Offset;
        var bytesToRead = Math.Min(remaining, request.MaximumBytes);
        if (bytesToRead == 0)
        {
            return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
                entry.Location,
                request.Offset,
                BytesRead: 0,
                IsTruncated: false));
        }

        var resolved = ResolveLocation(entry.Location.WithVersion(null));
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileReadReceipt>.Failure(resolved.Error!);
        }

        using var get = new HttpRequestMessage(HttpMethod.Get, resolved.Value!.Uri);
        get.Headers.Range = new RangeHeaderValue(
            request.Offset,
            request.Offset + bytesToRead - 1);
        get.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        get.Headers.TryAddWithoutValidation("If-Match", entry.Version.Value);
        AddNoCacheHeaders(get);
        using var response = await _client.SendAsync(
            get,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var responseScopeError = ValidateResponseScope(response);
        if (responseScopeError is not null)
        {
            return FileProviderResult<FileReadReceipt>.Failure(responseScopeError);
        }

        if (response.StatusCode is not (HttpStatusCode.PartialContent or HttpStatusCode.OK))
        {
            return HttpFailure<FileReadReceipt>(response);
        }

        if (response.StatusCode == HttpStatusCode.OK && request.Offset != 0)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.IoFailure,
                "The WebDAV server ignored a nonzero byte range; consuming the full prefix would violate the read bound.");
        }

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var expectedEnd = request.Offset + bytesToRead - 1;
            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange is null
                || !string.Equals(contentRange.Unit, "bytes", StringComparison.Ordinal)
                || contentRange.From != request.Offset
                || contentRange.To != expectedEnd
                || contentRange.Length is { } completeLength && completeLength != size
                || response.Content.Headers.ContentLength is { } responseLength
                    && responseLength != bytesToRead)
            {
                return Failure<FileReadReceipt>(
                    FileProviderErrorCode.IoFailure,
                    "The WebDAV server returned a partial response for a different byte range.");
            }
        }

        if (response.Headers.ETag is { } responseEtag
            && !string.Equals(responseEtag.ToString(), entry.Version.Value, StringComparison.Ordinal))
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.PreconditionFailed,
                "The WebDAV resource changed before its requested range was read.");
        }

        await using var content = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var copied = await RemoteFileProviderUtilities.CopyAtMostAsync(
            content,
            destination,
            bytesToRead,
            Math.Min(request.BufferSize, Capabilities.Limits.MaximumBufferSize),
            FileTransferStage.Reading,
            progress,
            bytesToRead,
            cancellationToken).ConfigureAwait(false);
        if (copied != bytesToRead)
        {
            return Failure<FileReadReceipt>(
                FileProviderErrorCode.UnexpectedEndOfStream,
                "The WebDAV response ended before all requested bytes were received.");
        }

        return FileProviderResult<FileReadReceipt>.Success(new FileReadReceipt(
            entry.Location,
            request.Offset,
            copied,
            remaining > request.MaximumBytes));
    }

    private async ValueTask<FileProviderResult<FileWriteReceipt>> WriteCoreAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var limitError = RemoteFileProviderUtilities.ValidateBufferSize(
            request.BufferSize,
            Capabilities.Limits.MaximumBufferSize);
        if (limitError is not null)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(limitError);
        }

        var resolved = ResolveLocation(request.Location);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(resolved.Error!);
        }

        if (resolved.Value!.Path.IsRoot)
        {
            return Failure<FileWriteReceipt>(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The WebDAV provider root cannot be replaced with a file.");
        }

        var precondition = RemoteFileProviderUtilities.MergeLocationVersion(
            request.Location,
            request.Precondition);
        if (!precondition.IsSuccess)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(precondition.Error!);
        }

        using var exactSource = new ExactLengthReadStream(
            source,
            request.ContentLength,
            bytes => progress?.Report(new FileTransferProgress(
                FileTransferStage.Writing,
                bytes,
                request.ContentLength)));
        using var content = new StreamContent(exactSource, request.BufferSize);
        content.Headers.ContentLength = request.ContentLength;
        using var put = new HttpRequestMessage(HttpMethod.Put, resolved.Value.Uri)
        {
            Content = content,
        };
        AddMutationPrecondition(put, precondition.Value!);
        AddNoCacheHeaders(put);
        using var response = await _client.SendAsync(
            put,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var responseScopeError = ValidateResponseScope(response);
        if (responseScopeError is not null)
        {
            return FileProviderResult<FileWriteReceipt>.Failure(responseScopeError);
        }

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent))
        {
            return HttpFailure<FileWriteReceipt>(response, precondition.Value);
        }

        if (exactSource.Position != request.ContentLength)
        {
            return Failure<FileWriteReceipt>(
                FileProviderErrorCode.UnexpectedEndOfStream,
                "The WebDAV transport did not consume the declared upload content.");
        }

        FileEntry entry;
        var responseVersion = ParseEtag(response.Headers.ETag?.ToString());
        if (responseVersion.IsSuccess)
        {
            entry = new FileEntry(
                request.Location.WithVersion(responseVersion.Value),
                FileEntryKind.File,
                request.ContentLength,
                LastModifiedAt: null,
                responseVersion.Value,
                resolved.Value.Path.Name is { } name
                    && name.Value.StartsWith('.'));
        }
        else
        {
            var stat = await StatCoreAsync(
                request.Location.WithVersion(null),
                cancellationToken).ConfigureAwait(false);
            if (!stat.IsSuccess)
            {
                return FileProviderResult<FileWriteReceipt>.Failure(stat.Error!);
            }

            entry = stat.Value!;
        }

        progress?.Report(new FileTransferProgress(
            FileTransferStage.Completed,
            request.ContentLength,
            request.ContentLength));
        return FileProviderResult<FileWriteReceipt>.Success(new FileWriteReceipt(
            entry,
            request.ContentLength,
            ReplacedExisting: response.StatusCode != HttpStatusCode.Created));
    }

    private static void AddMutationPrecondition(
        HttpRequestMessage request,
        FileMutationPrecondition precondition)
    {
        var value = precondition switch
        {
            FileMutationPrecondition.Any => null,
            FileMutationPrecondition.MustNotExist => "*",
            FileMutationPrecondition.MustExist => "*",
            FileMutationPrecondition.VersionMatches match => match.Version.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(precondition), precondition, null),
        };
        if (value is null)
        {
            return;
        }

        var header = precondition is FileMutationPrecondition.MustNotExist
            ? "If-None-Match"
            : "If-Match";
        request.Headers.TryAddWithoutValidation(header, value);
    }
}
