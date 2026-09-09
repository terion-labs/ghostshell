using System.Globalization;
using Asura.Application;
using Asura.Core;
using Asura.Docker;

namespace Asura.App;

/// <summary>
/// Makes one Docker resource look like a normal file provider. Presentation is
/// deliberately absent: Docker files are rendered by the same File Viewer as
/// every other provider.
/// </summary>
public sealed class DockerFilePanelClient : IFilePanelClient
{
    private const string ProfileId = "docker-resource";
    private const int MaximumPageSize = 10_000;
    private const long MaximumPreviewBytes = 1024 * 1024;
    private readonly IDockerEngineClient _docker;
    private readonly ConnectionProfile _connection;
    private readonly DockerResourceReference _resource;
    private readonly FileProviderProfileDescriptor _profile;

    public DockerFilePanelClient(
        IDockerEngineClient docker,
        ConnectionProfile connection,
        DockerResourceReference resource)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _resource = resource ?? throw new ArgumentNullException(nameof(resource));
        if (resource.Kind == DockerResourceKind.Network)
        {
            throw new ArgumentException("Docker networks do not have files.", nameof(resource));
        }

        var root = Location("/");
        _profile = new FileProviderProfileDescriptor(
            ProfileId,
            resource.DisplayName,
            FileProviderFamily.Posix,
            root,
            FilePanelCapability.List
            | FilePanelCapability.Stat
            | FilePanelCapability.RangedRead
            | FilePanelCapability.Search
            | FilePanelCapability.Watch
            | FilePanelCapability.Pagination,
            MaximumPageSize,
            MaximumPreviewBytes,
            RequiresHostTransferForPreview: true);
        Profiles = Array.AsReadOnly([_profile]);
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
        if (!TryPath(request.Location, out var path, out var locationError))
        {
            return FilePanelResult<FilePanelPage>.Failure(locationError!);
        }

        var result = await _docker.ListFilesAsync(
            _connection,
            _resource,
            path!,
            cancellationToken).ConfigureAwait(false);
        if (result is DockerResult<DockerFileListing>.Failure failure)
        {
            return Failure<FilePanelPage>(failure.Error);
        }

        var listing = ((DockerResult<DockerFileListing>.Success)result).Value;
        var entries = listing.Entries
            .Where(entry => request.ShowHidden || !entry.Name.StartsWith('.'))
            .Select(MapEntry)
            .ToArray();
        if (!TryOffset(request.ContinuationToken, entries.Length, out var offset))
        {
            return Invalid<FilePanelPage>("The Docker file continuation token is invalid.");
        }

        var pageSize = Math.Min(request.PageSize, _profile.MaximumPageSize);
        var page = entries.Skip(offset).Take(pageSize).ToArray();
        var nextOffset = offset + page.Length;
        var continuation = nextOffset < entries.Length
            ? nextOffset.ToString(CultureInfo.InvariantCulture)
            : null;
        return FilePanelResult<FilePanelPage>.Success(new FilePanelPage(page, continuation));
    }

    public async ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!TryPath(location, out var path, out var locationError))
        {
            return FilePanelResult<FilePanelEntry>.Failure(locationError!);
        }

        if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            return FilePanelResult<FilePanelEntry>.Success(new FilePanelEntry(
                location,
                _resource.DisplayName,
                FilePanelEntryKind.Directory,
                null,
                null,
                false));
        }

        var result = await _docker.StatFileAsync(
            _connection,
            _resource,
            path!,
            cancellationToken).ConfigureAwait(false);
        if (result is DockerResult<DockerFileEntry>.Failure failure)
        {
            return Failure<FilePanelEntry>(failure.Error);
        }

        return FilePanelResult<FilePanelEntry>.Success(MapEntry(
            ((DockerResult<DockerFileEntry>.Success)result).Value));
    }

    public async ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryPath(request.Location, out var path, out var locationError))
        {
            return FilePanelResult<FilePanelPreview>.Failure(locationError!);
        }

        var maximum = Math.Min(request.MaximumBytes, _profile.MaximumPreviewBytes);
        var result = await _docker.ReadFileAsync(
            _connection,
            _resource,
            path!,
            maximum,
            cancellationToken).ConfigureAwait(false);
        if (result is DockerResult<DockerFileContent>.Failure failure)
        {
            return Failure<FilePanelPreview>(failure.Error);
        }

        var content = ((DockerResult<DockerFileContent>.Success)result).Value;
        var (kind, mediaType) = FilePanelPreviewClassifier.Classify(
            request.Location,
            content.Content.Span);
        return FilePanelResult<FilePanelPreview>.Success(new FilePanelPreview(
            request.Location,
            kind,
            mediaType,
            content.Content.Span,
            content.IsTruncated));
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken) => Unsupported<FilePanelEntry>();

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken) => Unsupported<FilePanelEntry>();

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken) => Unsupported<FilePanelDeleteReceipt>();

    public ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken) => Unsupported<FilePanelTextWriteReceipt>();

    public ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken) => Unsupported<FilePanelCopyReceipt>();

    public ValueTask<FilePanelResult<FilePanelAccessControl>> GetAccessControlAsync(
        FilePanelAccessControlRequest request,
        CancellationToken cancellationToken) => Unsupported<FilePanelAccessControl>();

    public ValueTask<FilePanelResult<FilePanelAccessControl>> SetAccessControlAsync(
        FilePanelSetAccessControlRequest request,
        CancellationToken cancellationToken) => Unsupported<FilePanelAccessControl>();

    private FilePanelEntry MapEntry(DockerFileEntry entry) => new(
        Location(entry.Path),
        entry.Name,
        entry.Kind switch
        {
            DockerFileKind.File => FilePanelEntryKind.File,
            DockerFileKind.Directory => FilePanelEntryKind.Directory,
            DockerFileKind.Link => FilePanelEntryKind.Link,
            _ => FilePanelEntryKind.Other,
        },
        entry.Size,
        entry.ModifiedAt,
        entry.Name.StartsWith('.'));

    private static bool TryOffset(string? token, int count, out int offset)
    {
        if (token is null)
        {
            offset = 0;
            return true;
        }

        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            && offset >= 0
            && offset <= count;
    }

    private static bool TryPath(
        FilePanelLocation location,
        out string? path,
        out FilePanelError? error)
    {
        if (!string.Equals(location.ProviderProfileId, ProfileId
, StringComparison.Ordinal) || location.Authority is not null
            || location.Address is not FilePanelAddress.Hierarchical)
        {
            path = null;
            error = new FilePanelError(
                FilePanelErrorCode.InvalidLocation,
                "docker_file_location_invalid",
                "This location does not belong to the selected Docker resource.",
                false);
            return false;
        }

        path = Path(location);
        error = null;
        return true;
    }

    private static string Path(FilePanelLocation location)
    {
        var hierarchical = (FilePanelAddress.Hierarchical)location.Address;
        return hierarchical.Path.IsRoot
            ? "/"
            : $"/{string.Join('/', hierarchical.Path.Segments.Select(segment => segment.Value))}";
    }

    private static FilePanelLocation Location(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => new FilePanelPathSegment(segment));
        return new FilePanelLocation(
            ProfileId,
            null,
            new FilePanelAddress.Hierarchical(FilePanelPath.FromSegments(segments)));
    }

    private static FilePanelResult<T> Failure<T>(DockerError error) =>
        FilePanelResult<T>.Failure(new FilePanelError(
            error.Code switch
            {
                DockerErrorCode.ConnectionFailed or DockerErrorCode.RuntimeUnavailable =>
                    FilePanelErrorCode.Offline,
                DockerErrorCode.Cancelled => FilePanelErrorCode.Cancelled,
                DockerErrorCode.FileNotFound => FilePanelErrorCode.NotFound,
                DockerErrorCode.NotDirectory => FilePanelErrorCode.NotDirectory,
                DockerErrorCode.FileProtocolUnavailable =>
                    FilePanelErrorCode.UnsupportedCapability,
                _ => FilePanelErrorCode.IoFailure,
            },
            $"docker_{error.Code.ToString().ToLowerInvariant()}",
            error.Message,
            error.Retryable));

    private static FilePanelResult<T> Invalid<T>(string message) =>
        FilePanelResult<T>.Failure(new FilePanelError(
            FilePanelErrorCode.InvalidLocation,
            "docker_file_request_invalid",
            message,
            false));

    private static ValueTask<FilePanelResult<T>> Unsupported<T>() =>
        ValueTask.FromResult(FilePanelResult<T>.Failure(new FilePanelError(
            FilePanelErrorCode.UnsupportedCapability,
            "docker_file_mutation_unsupported",
            "Docker resource browsing is read-only.",
            false)));
}
