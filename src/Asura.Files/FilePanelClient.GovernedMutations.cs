using System.Text;
using Asura.Application;

namespace Asura.Files;

public sealed partial class FilePanelClient
{
    public const int MaximumGovernedTextBytes = 8 * 1024;
    public const long MaximumGovernedCopyBytes = 64L * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryResolve(request.Location, out var registration, out var location, out var error))
        {
            return FilePanelResult<FilePanelTextWriteReceipt>.Failure(error!);
        }

        byte[] content;
        try
        {
            content = StrictUtf8.GetBytes(request.Content);
        }
        catch (EncoderFallbackException)
        {
            return Failure<FilePanelTextWriteReceipt>(
                FilePanelErrorCode.InvalidName,
                "file_text_invalid_unicode",
                "Text file content must contain valid Unicode text.");
        }

        if (content.Length > MaximumGovernedTextBytes)
        {
            return Failure<FilePanelTextWriteReceipt>(
                FilePanelErrorCode.LimitExceeded,
                "file_text_limit_exceeded",
                $"Text file content cannot exceed {MaximumGovernedTextBytes} UTF-8 bytes.");
        }

        var precondition = MapPrecondition(request.Precondition, out error);
        if (precondition is null)
        {
            return FilePanelResult<FilePanelTextWriteReceipt>.Failure(error!);
        }

        await using var source = new MemoryStream(content, writable: false);
        var provider = registration!.Provider;
        var result = await provider.WriteAsync(
                new FileWriteRequest(
                    location!,
                    content.LongLength,
                    Math.Min(64 * 1024, provider.Capabilities.Limits.MaximumBufferSize),
                    precondition),
                source,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return FilePanelResult<FilePanelTextWriteReceipt>.Failure(MapError(result.Error!));
        }

        var receipt = result.Value!;
        if (receipt.BytesWritten != content.LongLength
            || !SameLocationIgnoringVersion(
                receipt.Destination.Location.WithVersion(receipt.Destination.Version),
                location!))
        {
            return InvalidProviderReceipt<FilePanelTextWriteReceipt>();
        }

        return FilePanelResult<FilePanelTextWriteReceipt>.Success(new FilePanelTextWriteReceipt(
            FromProviderLocation(receipt.Destination.Location.WithVersion(receipt.Destination.Version)),
            receipt.BytesWritten,
            receipt.ReplacedExisting));
    }

    public async ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumBytes > MaximumGovernedCopyBytes)
        {
            return Failure<FilePanelCopyReceipt>(
                FilePanelErrorCode.LimitExceeded,
                "file_copy_limit_exceeded",
                $"A governed copy cannot exceed {MaximumGovernedCopyBytes} bytes.");
        }

        if (!TryResolve(request.Source, out var registration, out var source, out var error))
        {
            return FilePanelResult<FilePanelCopyReceipt>.Failure(error!);
        }


        if (source!.Version is null)
        {
            return Failure<FilePanelCopyReceipt>(
                FilePanelErrorCode.InvalidLocation,
                "file_copy_source_version_required",
                "Governed copy requires a version-bound source from files.stat.");
        }

        if (!TryResolve(
                request.Destination,
                out var destinationRegistration,
                out var destination,
                out error))
        {
            return FilePanelResult<FilePanelCopyReceipt>.Failure(error!);
        }

        if (!ReferenceEquals(registration, destinationRegistration))
        {
            return Failure<FilePanelCopyReceipt>(
                FilePanelErrorCode.UnsupportedCapability,
                "file_cross_provider_governed_copy_unsupported",
                "Governed copy currently requires one provider profile.");
        }

        var provider = registration!.Provider;
        if (!provider.Capabilities.Supports(FileProviderCapability.Copy))
        {
            return Failure<FilePanelCopyReceipt>(
                FilePanelErrorCode.UnsupportedCapability,
                "file_copy_unsupported",
                "This provider does not support copy.");
        }

        var stat = await provider.StatAsync(new FileStatRequest(source!), cancellationToken)
            .ConfigureAwait(false);
        if (!stat.IsSuccess)
        {
            return FilePanelResult<FilePanelCopyReceipt>.Failure(MapError(stat.Error!));
        }

        var entry = stat.Value!;
        if (entry.Version != source.Version)
        {
            return Failure<FilePanelCopyReceipt>(
                FilePanelErrorCode.PreconditionFailed,
                "file_copy_source_changed",
                "The copy source changed after it was observed.");
        }

        if (entry.Kind != FileEntryKind.File || entry.Size is not { } sourceLength)
        {
            return Failure<FilePanelCopyReceipt>(
                FilePanelErrorCode.UnsupportedCapability,
                "file_copy_source_not_regular_file",
                "Governed copy accepts only regular files with a known size.");
        }

        if (sourceLength > request.MaximumBytes)
        {
            return Failure<FilePanelCopyReceipt>(
                FilePanelErrorCode.LimitExceeded,
                "file_copy_source_limit_exceeded",
                "The source file exceeds the governed copy limit.");
        }

        var versionedSource = source!.WithVersion(entry.Version);
        var result = await provider.TransferAsync(
                new FileTransferRequest(
                    versionedSource,
                    destination!,
                    FileTransferKind.Copy,
                    Math.Min(64 * 1024, provider.Capabilities.Limits.MaximumBufferSize),
                    new FileMutationPrecondition.MustNotExist()),
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return FilePanelResult<FilePanelCopyReceipt>.Failure(MapError(result.Error!));
        }

        var receipt = result.Value!;
        if (receipt.Kind != FileTransferKind.Copy
            || receipt.SourceDeleted
            || receipt.ReplacedExisting
            || receipt.BytesTransferred != sourceLength
            || !SameLocation(receipt.Source, versionedSource)
            || !SameLocation(receipt.Destination.Location, destination!))
        {
            return InvalidProviderReceipt<FilePanelCopyReceipt>();
        }

        return FilePanelResult<FilePanelCopyReceipt>.Success(new FilePanelCopyReceipt(
            FromProviderLocation(receipt.Source),
            FromProviderLocation(receipt.Destination.Location.WithVersion(receipt.Destination.Version)),
            receipt.BytesTransferred));
    }

    private static bool SameLocation(FileLocation actual, FileLocation expected) =>
        SameLocationIgnoringVersion(actual, expected)
        && (expected.Version is null || actual.Version == expected.Version);

    private static bool SameLocationIgnoringVersion(
        FileLocation actual,
        FileLocation expected) =>
        actual.ProviderProfileId == expected.ProviderProfileId
        && actual.Authority == expected.Authority
        && actual.Address == expected.Address;

    private static FilePanelResult<T> InvalidProviderReceipt<T>() => Failure<T>(
        FilePanelErrorCode.IoFailure,
        "file_provider_receipt_invalid",
        "The file provider returned an invalid mutation receipt.");
}
