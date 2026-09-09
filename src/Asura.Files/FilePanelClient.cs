using Asura.Application;

namespace Asura.Files;

/// <summary>
/// Maps the provider contract onto presentation-safe application DTOs without exposing provider
/// SDK types or weakening structured-location semantics.
/// </summary>
public sealed partial class FilePanelClient : IFilePanelClient, IFileTransferQueueClient, IDisposable
{
    private const int MaximumPreviewLength = 1024 * 1024;
    private readonly IReadOnlyDictionary<string, FileProviderRegistration> _registrations;
    private readonly TimeProvider _timeProvider;
    private readonly PreviewContentCache? _contentCache;

    public FilePanelClient(IEnumerable<FileProviderRegistration> registrations)
        : this(registrations, TimeProvider.System)
    {
    }

    public FilePanelClient(
        IEnumerable<FileProviderRegistration> registrations,
        TimeProvider timeProvider,
        PreviewContentCache? contentCache = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _contentCache = contentCache;
        var byId = new Dictionary<string, FileProviderRegistration>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            if (!byId.TryAdd(registration.Provider.ProfileId.Value, registration))
            {
                throw new ArgumentException(
                    $"File-provider profile '{registration.Provider.ProfileId}' is registered more than once.",
                    nameof(registrations));
            }
        }

        _registrations = byId;
        Profiles = Array.AsReadOnly(byId.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDescriptor)
            .ToArray());
    }

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; }

    public IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
        FilePanelSearchRequest request,
        CancellationToken cancellationToken) =>
        FilePanelSearch.FindAsync(this, request, cancellationToken);

    public IAsyncEnumerable<FilePanelResult<FilePanelChange>> WatchAsync(
        FilePanelWatchRequest request,
        CancellationToken cancellationToken) =>
        FilePanelWatch.ObserveAsync(this, request, cancellationToken);

    public async ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryResolve(request.Location, out var registration, out var location, out var error))
        {
            return FilePanelResult<FilePanelPage>.Failure(error!);
        }

        if (request.PageSize <= 0
            || request.PageSize > registration!.Provider.Capabilities.Limits.MaximumListPageSize)
        {
            return Failure<FilePanelPage>(
                FilePanelErrorCode.LimitExceeded,
                "file_list_page_limit_exceeded",
                "The requested page size exceeds this provider's limit.");
        }

        FilePageToken? token;
        try
        {
            token = request.ContinuationToken is null
                ? null
                : new FilePageToken(request.ContinuationToken);
        }
        catch (ArgumentException exception)
        {
            return Invalid<FilePanelPage>(exception.Message);
        }

        var result = await registration.Provider.ListAsync(
                new FileListRequest(location!, request.PageSize, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return FilePanelResult<FilePanelPage>.Failure(MapError(result.Error!));
        }

        var entries = result.Value!.Items
            .Where(entry => request.ShowHidden || !entry.IsHidden)
            .Select(ToEntry);
        return FilePanelResult<FilePanelPage>.Success(new FilePanelPage(
            entries,
            result.Value.ContinuationToken?.Value));
    }

    public async ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!TryResolve(location, out var registration, out var mapped, out var error))
        {
            return FilePanelResult<FilePanelEntry>.Failure(error!);
        }

        var result = await registration!.Provider.StatAsync(
                new FileStatRequest(mapped!),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? FilePanelResult<FilePanelEntry>.Success(ToEntry(result.Value!))
            : FilePanelResult<FilePanelEntry>.Failure(MapError(result.Error!));
    }

    public async ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryResolve(request.Location, out var registration, out var location, out var error))
        {
            return FilePanelResult<FilePanelPreview>.Failure(error!);
        }

        var limits = registration!.Provider.Capabilities.Limits;
        if (request.MaximumBytes <= 0
            || request.MaximumBytes > Math.Min(limits.MaximumReadBytes, MaximumPreviewLength))
        {
            return Failure<FilePanelPreview>(
                FilePanelErrorCode.LimitExceeded,
                "file_preview_limit_exceeded",
                "The requested preview exceeds this provider's bounded-read limit.");
        }

        var capacity = checked((int)request.MaximumBytes);
        var previewBuffer = new byte[capacity];
        await using var destination = new MemoryStream(
            previewBuffer,
            index: 0,
            count: previewBuffer.Length,
            writable: true,
            publiclyVisible: true);
        destination.SetLength(0);
        var readRequest = new FileReadRequest(
            location!,
            offset: 0,
            request.MaximumBytes,
            Math.Min(64 * 1024, limits.MaximumBufferSize));
        var result = await registration.Provider.ReadAsync(
                readRequest,
                destination,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return FilePanelResult<FilePanelPreview>.Failure(MapError(result.Error!));
        }

        var receipt = result.Value!;
        var sourceMatchesRequest = receipt.Source is not null
            && receipt.Source.ProviderProfileId == readRequest.Location.ProviderProfileId
            && receipt.Source.Authority == readRequest.Location.Authority
            && receipt.Source.Address == readRequest.Location.Address
            && (readRequest.Location.Version is null
                || receipt.Source.Version == readRequest.Location.Version);
        var byteCountIsValid = receipt.BytesRead >= 0
            && receipt.BytesRead <= readRequest.MaximumBytes
            && receipt.BytesRead == destination.Length;
        if (!sourceMatchesRequest
            || receipt.Offset != readRequest.Offset
            || !byteCountIsValid
            || destination.Length > capacity)
        {
            return Failure<FilePanelPreview>(
                FilePanelErrorCode.IoFailure,
                "file_provider_receipt_invalid",
                "The file provider returned an invalid bounded-read receipt.");
        }

        var source = FromProviderLocation(receipt.Source!);
        var content = destination.ToArray();
        var (kind, mediaType) = FilePanelPreviewClassifier.Classify(source, content);
        return FilePanelResult<FilePanelPreview>.Success(new FilePanelPreview(
            source,
            kind,
            mediaType,
            content,
            receipt.IsTruncated));
    }

    public async ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryResolve(request.Location, out var registration, out var location, out var error))
        {
            return FilePanelResult<FilePanelEntry>.Failure(error!);
        }

        var precondition = MapPrecondition(request.Precondition, out error);
        if (precondition is null)
        {
            return FilePanelResult<FilePanelEntry>.Failure(error!);
        }

        var result = await registration!.Provider.CreateDirectoryAsync(
                new FileCreateDirectoryRequest(location!, precondition),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? FilePanelResult<FilePanelEntry>.Success(ToEntry(result.Value!))
            : FilePanelResult<FilePanelEntry>.Failure(MapError(result.Error!));
    }

    public async ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryResolve(request.Source, out var registration, out var source, out var error))
        {
            return FilePanelResult<FilePanelEntry>.Failure(error!);
        }

        if (!TryResolve(request.Destination, out var destinationRegistration, out var destination, out error))
        {
            return FilePanelResult<FilePanelEntry>.Failure(error!);
        }

        if (!ReferenceEquals(registration, destinationRegistration))
        {
            return Failure<FilePanelEntry>(
                FilePanelErrorCode.UnsupportedCapability,
                "file_cross_provider_rename_unsupported",
                "Rename requires source and destination to use the same provider profile.");
        }

        var precondition = MapPrecondition(request.DestinationPrecondition, out error);
        if (precondition is null)
        {
            return FilePanelResult<FilePanelEntry>.Failure(error!);
        }

        var result = await registration!.Provider.RenameAsync(
                new FileRenameRequest(source!, destination!, precondition),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? FilePanelResult<FilePanelEntry>.Success(ToEntry(result.Value!))
            : FilePanelResult<FilePanelEntry>.Failure(MapError(result.Error!));
    }

    public async ValueTask<FilePanelResult<FilePanelAccessControl>> GetAccessControlAsync(
        FilePanelAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryResolve(request.Location, out var registration, out var location, out var error))
        {
            return FilePanelResult<FilePanelAccessControl>.Failure(error!);
        }

        var result = await registration!.Provider.GetAccessControlAsync(
                new FileAccessControlRequest(location!),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? FilePanelResult<FilePanelAccessControl>.Success(
                ToAccessControl(request.Location, result.Value!))
            : FilePanelResult<FilePanelAccessControl>.Failure(MapError(result.Error!));
    }

    public async ValueTask<FilePanelResult<FilePanelAccessControl>> SetAccessControlAsync(
        FilePanelSetAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryResolve(request.Location, out var registration, out var location, out var error))
        {
            return FilePanelResult<FilePanelAccessControl>.Failure(error!);
        }

        var result = await registration!.Provider.SetAccessControlAsync(
                new FileSetAccessControlRequest(
                    location!,
                    request.Mode,
                    request.Grants,
                    request.Version),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? FilePanelResult<FilePanelAccessControl>.Success(
                ToAccessControl(request.Location, result.Value!))
            : FilePanelResult<FilePanelAccessControl>.Failure(MapError(result.Error!));
    }

    private static FilePanelAccessControl ToAccessControl(
        FilePanelLocation location,
        FileAccessControl value) =>
        new(location, value.Mode, value.Owner, value.Group, value.Grants, value.Version);

    public async ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryResolve(request.Location, out var registration, out var location, out var error))
        {
            return FilePanelResult<FilePanelDeleteReceipt>.Failure(error!);
        }

        var precondition = MapPrecondition(request.Precondition, out error);
        if (precondition is null)
        {
            return FilePanelResult<FilePanelDeleteReceipt>.Failure(error!);
        }

        var result = await registration!.Provider.DeleteAsync(
                new FileDeleteRequest(location!, request.Recursive, precondition),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? FilePanelResult<FilePanelDeleteReceipt>.Success(new FilePanelDeleteReceipt(
                FromProviderLocation(result.Value!.DeletedLocation),
                result.Value.WasDirectory))
            : FilePanelResult<FilePanelDeleteReceipt>.Failure(MapError(result.Error!));
    }

    private bool TryResolve(
        FilePanelLocation source,
        out FileProviderRegistration? registration,
        out FileLocation? location,
        out FilePanelError? error)
    {
        if (!_registrations.TryGetValue(source.ProviderProfileId, out registration))
        {
            location = null;
            error = new FilePanelError(
                FilePanelErrorCode.UnknownProfile,
                "file_provider_profile_unknown",
                "The selected file-provider profile no longer exists.",
                false);
            return false;
        }

        try
        {
            location = ToProviderLocation(source);
            error = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            location = null;
            error = new FilePanelError(
                FilePanelErrorCode.InvalidLocation,
                "file_location_invalid",
                exception.Message,
                false);
            return false;
        }
    }

    private static FileProviderProfileDescriptor ToDescriptor(FileProviderRegistration registration)
    {
        var limits = registration.Provider.Capabilities.Limits;
        return new FileProviderProfileDescriptor(
            registration.Provider.ProfileId.Value,
            registration.Name,
            registration.Family,
            FromProviderLocation(registration.Root),
            MapCapabilities(registration.Provider.Capabilities.Supported)
                // Search and observation are panel-level discovery operations. Their common
                // implementations traverse ListAsync, so every listed protocol supports them
                // even when its wire protocol has no server-side equivalent.
                | FilePanelCapability.Search
                | FilePanelCapability.Watch
                | registration.GovernedMutationCapabilities,
            limits.MaximumListPageSize,
            Math.Min(limits.MaximumReadBytes, MaximumPreviewLength),
            FromProviderLocation(registration.Start));
    }

    private static FilePanelCapability MapCapabilities(FileProviderCapability capabilities)
    {
        var mapped = FilePanelCapability.None;
        foreach (var capability in Enum.GetValues<FileProviderCapability>())
        {
            if (capability == FileProviderCapability.None || !capabilities.HasFlag(capability))
            {
                continue;
            }

            mapped |= capability switch
            {
                FileProviderCapability.List => FilePanelCapability.List,
                FileProviderCapability.Stat => FilePanelCapability.Stat,
                FileProviderCapability.RangedRead => FilePanelCapability.RangedRead,
                FileProviderCapability.StreamingWrite => FilePanelCapability.StreamingWrite,
                FileProviderCapability.CreateDirectory => FilePanelCapability.CreateDirectory,
                FileProviderCapability.CreateContainer => FilePanelCapability.CreateContainer,
                FileProviderCapability.Rename => FilePanelCapability.Rename,
                FileProviderCapability.Copy => FilePanelCapability.Copy,
                FileProviderCapability.Move => FilePanelCapability.Move,
                FileProviderCapability.Delete => FilePanelCapability.Delete,
                FileProviderCapability.Search => FilePanelCapability.Search,
                FileProviderCapability.Watch => FilePanelCapability.Watch,
                FileProviderCapability.Checksum => FilePanelCapability.Checksum,
                FileProviderCapability.ResumableTransfer => FilePanelCapability.ResumableTransfer,
                FileProviderCapability.Versioning => FilePanelCapability.Versioning,
                FileProviderCapability.Symlinks => FilePanelCapability.Symlinks,
                FileProviderCapability.Permissions => FilePanelCapability.Permissions,
                FileProviderCapability.AccessControlLists => FilePanelCapability.AccessControlLists,
                FileProviderCapability.AtomicReplace => FilePanelCapability.AtomicReplace,
                FileProviderCapability.ServerSideCopy => FilePanelCapability.ServerSideCopy,
                FileProviderCapability.Pagination => FilePanelCapability.Pagination,
                _ => throw new ArgumentOutOfRangeException(nameof(capabilities), capability, null),
            };
        }

        return mapped;
    }

    private static FileLocation ToProviderLocation(FilePanelLocation source)
    {
        var profileId = new FileProviderProfileId(source.ProviderProfileId);
        FileAuthority? authority = source.Authority is null
            ? null
            : new FileAuthority(source.Authority);
        FileVersion? version = source.Version is null
            ? null
            : new FileVersion(source.Version);
        return source.Address switch
        {
            FilePanelAddress.Hierarchical hierarchical => new FileLocation(
                profileId,
                authority,
                FilePath.FromSegments(hierarchical.Path.Segments.Select(segment =>
                    new FilePathSegment(segment.Value))),
                version),
            FilePanelAddress.ObjectKey objectKey when authority is { } value =>
                FileLocation.ForObjectKey(profileId, value, new FileObjectKey(objectKey.Key), version),
            FilePanelAddress.ContainerRoot when authority is { } value =>
                FileLocation.ForContainerRoot(profileId, value, version),
            FilePanelAddress.ObjectKey or FilePanelAddress.ContainerRoot =>
                throw new ArgumentException("Object and container locations require an authority."),
            _ => throw new ArgumentException("The file location address is unsupported."),
        };
    }

    private static FilePanelLocation FromProviderLocation(FileLocation source)
    {
        FilePanelAddress address = source.Address switch
        {
            FileLocationAddress.Hierarchical hierarchical => new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments(hierarchical.Path.Segments.Select(segment =>
                    new FilePanelPathSegment(segment.Value)))),
            FileLocationAddress.Object value => new FilePanelAddress.ObjectKey(value.Key.Value),
            FileLocationAddress.ContainerRoot => new FilePanelAddress.ContainerRoot(),
            _ => throw new ArgumentException("The provider returned an unsupported file address."),
        };
        return new FilePanelLocation(
            source.ProviderProfileId.Value,
            source.Authority?.Value,
            address,
            source.Version?.Value);
    }

    private static FilePanelEntry ToEntry(FileEntry source) => new(
        FromProviderLocation(source.Location.WithVersion(source.Version)),
        DisplayName(source.Location),
        source.Kind switch
        {
            FileEntryKind.File => FilePanelEntryKind.File,
            FileEntryKind.Directory => FilePanelEntryKind.Directory,
            FileEntryKind.Link => FilePanelEntryKind.Link,
            FileEntryKind.Other => FilePanelEntryKind.Other,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, null),
        },
        source.Size,
        source.LastModifiedAt,
        source.IsHidden);

    private static string DisplayName(FileLocation location) => location.Address switch
    {
        FileLocationAddress.Hierarchical hierarchical =>
            hierarchical.Path.Name?.Value ?? location.Authority?.Value ?? "/",
        FileLocationAddress.Object value => ObjectDisplayName(value.Key.Value),
        FileLocationAddress.ContainerRoot => location.Authority?.Value ?? "Container root",
        _ => "Item",
    };

    private static string ObjectDisplayName(string key)
    {
        var trimmed = key.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }

    private static FileMutationPrecondition? MapPrecondition(
        FilePanelMutationPrecondition source,
        out FilePanelError? error)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            error = null;
            return source.Kind switch
            {
                FilePanelMutationPreconditionKind.Any => new FileMutationPrecondition.Any(),
                FilePanelMutationPreconditionKind.MustNotExist => new FileMutationPrecondition.MustNotExist(),
                FilePanelMutationPreconditionKind.MustExist => new FileMutationPrecondition.MustExist(),
                FilePanelMutationPreconditionKind.VersionMatches =>
                    new FileMutationPrecondition.VersionMatches(new FileVersion(source.Version!)),
                _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, null),
            };
        }
        catch (ArgumentException exception)
        {
            error = new FilePanelError(
                FilePanelErrorCode.InvalidLocation,
                "file_precondition_invalid",
                exception.Message,
                false);
            return null;
        }
    }

    private static FilePanelError MapError(FileProviderError source) => new(
        source.Code switch
        {
            FileProviderErrorCode.UnsupportedCapability => FilePanelErrorCode.UnsupportedCapability,
            FileProviderErrorCode.InvalidLocation => FilePanelErrorCode.InvalidLocation,
            FileProviderErrorCode.InvalidName => FilePanelErrorCode.InvalidName,
            FileProviderErrorCode.OutsideRoot => FilePanelErrorCode.OutsideRoot,
            FileProviderErrorCode.RootMutationNotAllowed => FilePanelErrorCode.RootMutationNotAllowed,
            FileProviderErrorCode.NotFound => FilePanelErrorCode.NotFound,
            FileProviderErrorCode.AlreadyExists => FilePanelErrorCode.AlreadyExists,
            FileProviderErrorCode.Conflict => FilePanelErrorCode.Conflict,
            FileProviderErrorCode.PreconditionFailed => FilePanelErrorCode.PreconditionFailed,
            FileProviderErrorCode.RangeNotSatisfiable => FilePanelErrorCode.RangeNotSatisfiable,
            FileProviderErrorCode.LimitExceeded => FilePanelErrorCode.LimitExceeded,
            FileProviderErrorCode.AuthenticationRequired =>
                FilePanelErrorCode.AuthenticationRequired,
            FileProviderErrorCode.AccessDenied => FilePanelErrorCode.AccessDenied,
            FileProviderErrorCode.HostKeyUnknown => FilePanelErrorCode.HostKeyUnknown,
            FileProviderErrorCode.HostKeyChanged => FilePanelErrorCode.HostKeyChanged,
            FileProviderErrorCode.HostKeyStoreInvalid => FilePanelErrorCode.HostKeyStoreInvalid,
            FileProviderErrorCode.NotDirectory => FilePanelErrorCode.NotDirectory,
            FileProviderErrorCode.IsDirectory => FilePanelErrorCode.IsDirectory,
            FileProviderErrorCode.DirectoryNotEmpty => FilePanelErrorCode.DirectoryNotEmpty,
            FileProviderErrorCode.LinkNotAllowed => FilePanelErrorCode.LinkNotAllowed,
            FileProviderErrorCode.SharingViolation => FilePanelErrorCode.SharingViolation,
            FileProviderErrorCode.QuotaExceeded => FilePanelErrorCode.QuotaExceeded,
            FileProviderErrorCode.UnexpectedEndOfStream => FilePanelErrorCode.UnexpectedEndOfStream,
            FileProviderErrorCode.PartialTransfer => FilePanelErrorCode.PartialTransfer,
            FileProviderErrorCode.Cancelled => FilePanelErrorCode.Cancelled,
            FileProviderErrorCode.IoFailure => FilePanelErrorCode.IoFailure,
            _ => FilePanelErrorCode.IoFailure,
        },
        $"file_{source.StableCode}",
        source.Message,
        source.Retryable);

    private static FilePanelResult<T> Invalid<T>(string message) =>
        Failure<T>(FilePanelErrorCode.InvalidLocation, "file_request_invalid", message);

    private static FilePanelResult<T> Failure<T>(
        FilePanelErrorCode code,
        string stableCode,
        string message,
        bool retryable = false) =>
        FilePanelResult<T>.Failure(new FilePanelError(code, stableCode, message, retryable));
}
