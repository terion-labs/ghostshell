using GhostShell.Application;

namespace GhostShell.Files;

// Re-stated rather than inherited: an interface's default implementation is
// bound where the interface is first implemented, so a method added further
// down the hierarchy is not the one that gets called unless the class says it
// implements the interface itself.
public sealed partial class PosixLocalFileProvider : LocalFileProvider, IFileProvider
{
    public PosixLocalFileProvider(LocalFileProviderOptions options)
        : base(
            options,
            OperatingSystem.IsMacOS()
                ? FileNameComparison.ProviderDefined
                : FileNameComparison.CaseSensitive,
            StringComparison.Ordinal,
            // The one local capability that is not shared with Windows: nine
            // bits, read and written by the runtime itself.
            FileProviderCapability.Permissions)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The POSIX local file provider requires a Unix host.");
        }
    }

    protected override FileProviderError? ValidatePlatformSegment(FilePathSegment segment) => null;

    protected override bool IsHidden(FilePathSegment? name, FileAttributes attributes) =>
        name is { } value && value.Value.StartsWith('.');

    /// <summary>
    /// The mode as the runtime reads it. The owning account's name is not
    /// offered: .NET exposes the bits but not the uid behind them, and a name
    /// guessed from the current process would be wrong for every file that
    /// belongs to somebody else.
    /// </summary>
    public ValueTask<FileProviderResult<FileAccessControl>> GetAccessControlAsync(
        FileAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(request.Location));
    }

    public ValueTask<FileProviderResult<FileAccessControl>> SetAccessControlAsync(
        FileSetAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Mode is not { } mode)
        {
            return ValueTask.FromResult(FileProviderResult<FileAccessControl>.Failure(
                FileProviderError.Create(
                    FileProviderErrorCode.UnsupportedCapability,
                    "A local filesystem is described by its permission bits, "
                    + "not by a list of grants.")));
        }

        var resolved = ResolveLocation(request.Location, allowLeafLink: false);
        if (!resolved.IsSuccess)
        {
            return ValueTask.FromResult(
                FileProviderResult<FileAccessControl>.Failure(resolved.Error!));
        }

        // Unreachable — the constructor refuses to run on Windows — but the
        // runtime's own annotation is what the analyzer reads, not the ctor.
        if (OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(FileProviderResult<FileAccessControl>.Failure(
                FileProviderErrors.UnsupportedAccessControl));
        }

        try
        {
            // The bits above the nine are the file's own — setuid, sticky —
            // and are put back exactly as they were found. A permissions
            // dialog is not where somebody sets those, and it is certainly
            // not where they lose them.
            var path = resolved.Value!.Path;
            var current = (int)File.GetUnixFileMode(path);
            var replaced = (current & ~FilePanelPosixMode.PermissionMask) | mode.Permissions;
            var error = SetModeNoFollow(resolved.Value.StructuredPath, (uint)replaced);
            if (error is not null)
            {
                return ValueTask.FromResult(FileProviderResult<FileAccessControl>.Failure(error));
            }
            return ValueTask.FromResult(Read(request.Location));
        }
        catch (Exception exception) when (IsExpectedFilesystemFailure(exception))
        {
            return ValueTask.FromResult(FileProviderResult<FileAccessControl>.Failure(
                Translate(exception)));
        }
    }

    private FileProviderResult<FileAccessControl> Read(FileLocation location)
    {
        var resolved = ResolveLocation(location, allowLeafLink: false);
        if (!resolved.IsSuccess)
        {
            return FileProviderResult<FileAccessControl>.Failure(resolved.Error!);
        }

        if (OperatingSystem.IsWindows())
        {
            return FileProviderResult<FileAccessControl>.Failure(
                FileProviderErrors.UnsupportedAccessControl);
        }

        try
        {
            var mode = (int)File.GetUnixFileMode(resolved.Value!.Path);
            return FileProviderResult<FileAccessControl>.Success(
                new FileAccessControl(new FilePanelPosixMode(mode & 0xFFF)));
        }
        catch (Exception exception) when (IsExpectedFilesystemFailure(exception))
        {
            return FileProviderResult<FileAccessControl>.Failure(Translate(exception));
        }
    }

    private static bool IsExpectedFilesystemFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or PlatformNotSupportedException;

    private static FileProviderError Translate(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => FileProviderError.Create(
            FileProviderErrorCode.NotFound,
            "This item is no longer there."),
        UnauthorizedAccessException => FileProviderError.Create(
            FileProviderErrorCode.AccessDenied,
            "This account is not allowed to change this item's permissions."),
        PlatformNotSupportedException => FileProviderError.Create(
            FileProviderErrorCode.UnsupportedCapability,
            "This filesystem does not carry permission bits."),
        _ => FileProviderError.Create(
            FileProviderErrorCode.IoFailure,
            "The filesystem refused to read or change this item's permissions."),
    };
}
