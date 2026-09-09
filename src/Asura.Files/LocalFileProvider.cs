using System.Security.Cryptography;
using System.Text;

namespace Asura.Files;

/// <summary>
/// Confines local filesystem operations to one configured root. Locations are resolved from
/// validated path segments, and reparse points are metadata only: operations never follow them.
/// </summary>
public abstract partial class LocalFileProvider : IFileProvider, ILocalFilePathSource
{
    private const long MaximumReadBytes = 64L * 1024 * 1024;
    private const int MaximumBufferSize = 1024 * 1024;
    private readonly StringComparison _pathComparison;
    private readonly FilePageCursorStore<LocalPageCursor> _pageCursors = new(maximumEntries: 32);

    protected LocalFileProvider(
        LocalFileProviderOptions options,
        FileNameComparison nameComparison,
        StringComparison pathComparison,
        FileProviderCapability platformCapabilities = FileProviderCapability.None)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        var attributes = File.GetAttributes(rootPath);
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new ArgumentException("The local provider root must be a directory.", nameof(options));
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArgumentException(
                "The local provider root cannot be a symbolic link or reparse point.",
                nameof(options));
        }

        ProfileId = options.ProfileId;
        Authority = options.Authority;
        RootPath = rootPath;
        _pathComparison = pathComparison;
        Capabilities = new FileProviderCapabilities(
            FileProviderCapability.List
            | FileProviderCapability.Stat
            | FileProviderCapability.RangedRead
            | FileProviderCapability.StreamingWrite
            | FileProviderCapability.CreateDirectory
            | FileProviderCapability.Rename
            | FileProviderCapability.Copy
            | FileProviderCapability.Move
            | FileProviderCapability.Delete
            | FileProviderCapability.AtomicReplace
            | FileProviderCapability.Pagination
            | platformCapabilities,
            nameComparison,
            new FileProviderLimits(
                maximumListPageSize: 1_000,
                maximumReadBytes: MaximumReadBytes,
                maximumBufferSize: MaximumBufferSize));
    }

    public FileProviderProfileId ProfileId { get; }

    public FileAuthority Authority { get; }

    public FileProviderCapabilities Capabilities { get; }

    protected string RootPath { get; }

    /// <summary>
    /// The confined path for a location, or null when it does not resolve
    /// inside this provider's root. Links are allowed at the leaf because the
    /// caller wants the file the user selected, exactly as a bounded read of
    /// the same location would return it.
    /// </summary>
    string? ILocalFilePathSource.TryGetLocalPath(FileLocation location)
    {
        var resolved = ResolveLocation(location, allowLeafLink: true);
        return resolved.IsSuccess ? resolved.Value!.Path : null;
    }

    public static LocalFileProvider CreateForCurrentPlatform(LocalFileProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return OperatingSystem.IsWindows()
            ? new WindowsLocalFileProvider(options)
            : new PosixLocalFileProvider(options);
    }

    protected abstract FileProviderError? ValidatePlatformSegment(FilePathSegment segment);

    protected abstract bool IsHidden(FilePathSegment? name, FileAttributes attributes);

    private protected FileProviderResult<ResolvedLocalLocation> ResolveLocation(
        FileLocation location,
        bool allowLeafLink)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.ProviderProfileId != ProfileId || location.Authority != Authority)
        {
            return Failure<ResolvedLocalLocation>(
                FileProviderErrorCode.InvalidLocation,
                "The location belongs to a different file-provider profile or authority.");
        }

        if (location.Address is not FileLocationAddress.Hierarchical hierarchical)
        {
            return Failure<ResolvedLocalLocation>(
                FileProviderErrorCode.InvalidLocation,
                "The local file provider requires a hierarchical path location.");
        }

        var currentPath = RootPath;
        for (var index = 0; index < hierarchical.Path.Segments.Length; index++)
        {
            var segment = hierarchical.Path.Segments[index];
            var nameError = ValidatePlatformSegment(segment);
            if (nameError is not null)
            {
                return FileProviderResult<ResolvedLocalLocation>.Failure(nameError);
            }

            currentPath = Path.GetFullPath(Path.Combine(currentPath, segment.Value));
            if (!IsWithinRoot(currentPath))
            {
                return Failure<ResolvedLocalLocation>(
                    FileProviderErrorCode.OutsideRoot,
                    "The resolved location is outside the configured provider root.");
            }

            var attributes = TryGetAttributes(currentPath);
            var isLeaf = index == hierarchical.Path.Segments.Length - 1;
            if (attributes?.HasFlag(FileAttributes.ReparsePoint) == true
                && (!isLeaf || !allowLeafLink))
            {
                return Failure<ResolvedLocalLocation>(
                    FileProviderErrorCode.LinkNotAllowed,
                    "Following symbolic links or reparse points is not allowed by this provider.");
            }
        }

        return FileProviderResult<ResolvedLocalLocation>.Success(
            new ResolvedLocalLocation(location, hierarchical.Path, currentPath));
    }

    private protected FileProviderResult<FileEntry> ReadEntry(ResolvedLocalLocation resolved)
    {
        var attributes = File.GetAttributes(resolved.Path);
        var kind = GetEntryKind(attributes);
        var name = resolved.StructuredPath.Name;
        long? size = null;
        DateTimeOffset? lastModifiedAt = null;
        string versionSource;

        if (kind == FileEntryKind.Link)
        {
            var linkTarget = attributes.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(resolved.Path).LinkTarget
                : new FileInfo(resolved.Path).LinkTarget;
            versionSource = $"link:{(int)attributes}:{linkTarget}";
        }
        else if (kind == FileEntryKind.Directory)
        {
            var directory = new DirectoryInfo(resolved.Path);
            lastModifiedAt = directory.LastWriteTimeUtc;
            versionSource = $"directory:{(int)attributes}:{directory.LastWriteTimeUtc.Ticks}";
        }
        else
        {
            var file = new FileInfo(resolved.Path);
            size = file.Length;
            lastModifiedAt = file.LastWriteTimeUtc;
            versionSource = $"file:{(int)attributes}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }

        var versionBytes = SHA256.HashData(Encoding.UTF8.GetBytes(versionSource));
        var version = new FileVersion(Convert.ToHexString(versionBytes));
        var entry = new FileEntry(
            resolved.Location.WithVersion(version),
            kind,
            size,
            lastModifiedAt,
            version,
            IsHidden(name, attributes));

        if (resolved.Location.Version is { } expectedVersion && expectedVersion != version)
        {
            return Failure<FileEntry>(
                FileProviderErrorCode.PreconditionFailed,
                "The requested location version is stale.");
        }

        return FileProviderResult<FileEntry>.Success(entry);
    }

    private protected FileProviderResult<LocalEntryPresence> ReadEntryIfPresent(ResolvedLocalLocation resolved)
    {
        try
        {
            var result = ReadEntry(resolved);
            return result.IsSuccess
                ? FileProviderResult<LocalEntryPresence>.Success(new LocalEntryPresence(result.Value))
                : FileProviderResult<LocalEntryPresence>.Failure(result.Error!);
        }
        catch (FileNotFoundException)
        {
            return FileProviderResult<LocalEntryPresence>.Success(new LocalEntryPresence(null));
        }
        catch (DirectoryNotFoundException)
        {
            return FileProviderResult<LocalEntryPresence>.Success(new LocalEntryPresence(null));
        }
    }

    private protected FileProviderError? CheckPrecondition(
        FileLocation location,
        FileMutationPrecondition requestedPrecondition,
        FileEntry? existing)
    {
        var preconditionResult = MergeLocationVersion(location, requestedPrecondition);
        if (!preconditionResult.IsSuccess)
        {
            return preconditionResult.Error;
        }

        return preconditionResult.Value switch
        {
            FileMutationPrecondition.Any => null,
            FileMutationPrecondition.MustNotExist when existing is not null => FileProviderError.Create(
                FileProviderErrorCode.Conflict,
                "The destination already exists."),
            FileMutationPrecondition.MustExist when existing is null => FileProviderError.Create(
                FileProviderErrorCode.NotFound,
                "The destination does not exist."),
            FileMutationPrecondition.VersionMatches when existing is null => FileProviderError.Create(
                FileProviderErrorCode.PreconditionFailed,
                "The destination version cannot match because the destination does not exist."),
            FileMutationPrecondition.VersionMatches match when existing!.Version != match.Version =>
                FileProviderError.Create(
                    FileProviderErrorCode.PreconditionFailed,
                    "The destination version is stale."),
            _ => null,
        };
    }

    private protected bool PathsEqual(string left, string right) =>
        string.Equals(left, right, _pathComparison);

    private protected bool IsWithinPath(string candidate, string ancestor)
    {
        var relative = Path.GetRelativePath(ancestor, candidate);
        return !string.Equals(relative, "."
, StringComparison.Ordinal) && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private bool IsWithinRoot(string candidate)
    {
        if (PathsEqual(candidate, RootPath))
        {
            return true;
        }

        var relative = Path.GetRelativePath(RootPath, candidate);
        return !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static FileProviderResult<FileMutationPrecondition> MergeLocationVersion(
        FileLocation location,
        FileMutationPrecondition precondition)
    {
        if (location.Version is not { } locationVersion)
        {
            return FileProviderResult<FileMutationPrecondition>.Success(precondition);
        }

        if (precondition is FileMutationPrecondition.Any)
        {
            return FileProviderResult<FileMutationPrecondition>.Success(
                new FileMutationPrecondition.VersionMatches(locationVersion));
        }

        if (precondition is FileMutationPrecondition.VersionMatches match
            && match.Version == locationVersion)
        {
            return FileProviderResult<FileMutationPrecondition>.Success(precondition);
        }

        return Failure<FileMutationPrecondition>(
            FileProviderErrorCode.InvalidLocation,
            "A versioned destination requires the same version-match precondition.");
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static FileEntryKind GetEntryKind(FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return FileEntryKind.Link;
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            return FileEntryKind.Directory;
        }

        if (attributes.HasFlag(FileAttributes.Device))
        {
            return FileEntryKind.Other;
        }

        return FileEntryKind.File;
    }

    private protected static FileProviderResult<T> Failure<T>(
        FileProviderErrorCode code,
        string message,
        bool retryable = false) =>
        FileProviderResult<T>.Failure(FileProviderError.Create(code, message, retryable));

    private protected async ValueTask<FileProviderResult<T>> ExecuteFileSystemOperationAsync<T>(
        Func<CancellationToken, ValueTask<FileProviderResult<T>>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<T>(FileProviderErrorCode.Cancelled, "The file operation was cancelled.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure<T>(FileProviderErrorCode.AccessDenied, exception.Message);
        }
        catch (FileNotFoundException exception)
        {
            return Failure<T>(FileProviderErrorCode.NotFound, exception.Message);
        }
        catch (DirectoryNotFoundException exception)
        {
            return Failure<T>(FileProviderErrorCode.NotFound, exception.Message);
        }
        catch (PathTooLongException exception)
        {
            return Failure<T>(FileProviderErrorCode.InvalidLocation, exception.Message);
        }
        catch (IOException exception)
        {
            return FileProviderResult<T>.Failure(MapIOException(exception));
        }
    }

    private FileProviderError MapIOException(IOException exception)
    {
        var nativeCode = exception.HResult & 0xFFFF;
        return nativeCode switch
        {
            17 or 80 or 183 => FileProviderError.Create(
                FileProviderErrorCode.AlreadyExists,
                exception.Message),
            39 or 145 => FileProviderError.Create(
                FileProviderErrorCode.DirectoryNotEmpty,
                exception.Message),
            28 or 112 => FileProviderError.Create(
                FileProviderErrorCode.QuotaExceeded,
                exception.Message),
            32 or 33 when OperatingSystem.IsWindows() => FileProviderError.Create(
                FileProviderErrorCode.SharingViolation,
                exception.Message,
                retryable: true),
            _ => FileProviderError.Create(
                FileProviderErrorCode.IoFailure,
                exception.Message,
                retryable: true),
        };
    }

    private protected sealed record ResolvedLocalLocation(
        FileLocation Location,
        FilePath StructuredPath,
        string Path);

    private sealed record LocalPageCursor(
        string Scope,
        IReadOnlyList<string> Paths,
        int Offset);

    private protected sealed record LocalEntryPresence(FileEntry? Entry);
}
