using System.Runtime.InteropServices;

namespace Asura.Files;

public sealed partial class PosixLocalFileProvider
{
    private const int AtRemoveDirectoryLinux = 0x200;
    private const int AtRemoveDirectoryMacOs = 0x80;
    private const int ErrorAccessDenied = 13;
    private const int ErrorAlreadyExists = 17;
    private const int ErrorDirectoryNotEmptyLinux = 39;
    private const int ErrorDirectoryNotEmptyMacOs = 66;
    private const int ErrorInvalidPath = 20;
    private const int ErrorLinkTraversalLinux = 40;
    private const int ErrorLinkTraversalMacOs = 62;
    private const int ErrorNotFound = 2;
    private const int ErrorOperationNotPermitted = 1;

    private protected override FileProviderError? CreateDirectoryEntry(
        ResolvedLocalLocation resolved)
    {
        using var parent = OpenParentDirectory(resolved.StructuredPath);
        if (parent.Error is not null)
        {
            return parent.Error;
        }

        return mkdirat(parent.Descriptor, parent.LeafName!, mode: 0x1FF) == 0
            ? null
            : NativeError("create the directory");
    }

    private protected override FileProviderError? DeleteEntry(
        ResolvedLocalLocation resolved,
        FileEntry entry,
        bool recursive,
        CancellationToken cancellationToken)
    {
        if (recursive)
        {
            return base.DeleteEntry(resolved, entry, recursive, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var parent = OpenParentDirectory(resolved.StructuredPath);
        if (parent.Error is not null)
        {
            return parent.Error;
        }

        var flags = entry.Kind == FileEntryKind.Directory
            ? OperatingSystem.IsMacOS()
                ? AtRemoveDirectoryMacOs
                : AtRemoveDirectoryLinux
            : 0;
        return unlinkat(parent.Descriptor, parent.LeafName!, flags) == 0
            ? null
            : NativeError("delete the entry");
    }

    internal FileProviderError? SetModeNoFollow(FilePath path, uint mode)
    {
        using var parent = path.IsRoot
            ? OpenRootForAccessControl()
            : OpenParentDirectory(path);
        if (parent.Error is not null)
        {
            return parent.Error;
        }

        // Unlike opening the leaf for reading, fchmodat permits an owner to
        // repair unreadable modes. Never retry without NOFOLLOW on failure.
        return fchmodat(parent.Descriptor, parent.LeafName!, mode,
            OperatingSystem.IsMacOS() ? 0x20 : 0x100) == 0
            ? null
            : NativeError("change the permissions without following links");
    }

    private ParentDirectoryHandle OpenRootForAccessControl()
    {
        var descriptor = open(RootPath, DirectoryOpenFlags());
        return descriptor < 0
            ? ParentDirectoryHandle.Failed(NativeError("open the configured provider root"))
            : ParentDirectoryHandle.Opened(descriptor, ".");
    }

    private ParentDirectoryHandle OpenParentDirectory(FilePath path)
    {
        if (path.IsRoot)
        {
            return ParentDirectoryHandle.Failed(FileProviderError.Create(
                FileProviderErrorCode.RootMutationNotAllowed,
                "The provider root cannot be mutated."));
        }

        var descriptor = open(RootPath, DirectoryOpenFlags());
        if (descriptor < 0)
        {
            return ParentDirectoryHandle.Failed(
                NativeError("open the configured provider root"));
        }

        for (var index = 0; index < path.Segments.Length - 1; index++)
        {
            var next = openat(
                descriptor,
                path.Segments[index].Value,
                DirectoryOpenFlags());
            if (next < 0)
            {
                var error = NativeError("open a parent directory");
                _ = close(descriptor);
                return ParentDirectoryHandle.Failed(error);
            }

            _ = close(descriptor);
            descriptor = next;
        }

        return ParentDirectoryHandle.Opened(
            descriptor,
            path.Segments[^1].Value);
    }

    private static int DirectoryOpenFlags() => OperatingSystem.IsMacOS()
        // O_SEARCH on macOS and O_PATH on Linux do not require directory read
        // permission; openat still enforces search permission for each child.
        ? 0x4000_0000 | 0x0010_0000 | 0x0100_0000 | 0x0000_0100
        : 0x0020_0000 | 0x0001_0000 | 0x0008_0000 | 0x0002_0000;

    private static FileProviderError NativeError(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        var message = $"Unable to {operation}.";
        return error switch
        {
            ErrorNotFound => FileProviderError.Create(
                FileProviderErrorCode.NotFound,
                message),
            ErrorOperationNotPermitted or ErrorAccessDenied => FileProviderError.Create(
                FileProviderErrorCode.AccessDenied,
                message),
            ErrorAlreadyExists => FileProviderError.Create(
                FileProviderErrorCode.AlreadyExists,
                message),
            ErrorInvalidPath => FileProviderError.Create(
                FileProviderErrorCode.InvalidLocation,
                message),
            ErrorLinkTraversalLinux or ErrorLinkTraversalMacOs => FileProviderError.Create(
                FileProviderErrorCode.LinkNotAllowed,
                message),
            ErrorDirectoryNotEmptyLinux or ErrorDirectoryNotEmptyMacOs =>
                FileProviderError.Create(
                    FileProviderErrorCode.DirectoryNotEmpty,
                    message),
            _ => FileProviderError.Create(
                FileProviderErrorCode.IoFailure,
                message,
                retryable: true),
        };
    }

    private sealed class ParentDirectoryHandle : IDisposable
    {
        private ParentDirectoryHandle(
            int descriptor,
            string? leafName,
            FileProviderError? error)
        {
            Descriptor = descriptor;
            LeafName = leafName;
            Error = error;
        }

        public int Descriptor { get; }

        public string? LeafName { get; }

        public FileProviderError? Error { get; }

        public static ParentDirectoryHandle Opened(int descriptor, string leafName) =>
            new(descriptor, leafName, error: null);

        public static ParentDirectoryHandle Failed(FileProviderError error) =>
            new(descriptor: -1, leafName: null, error);

        public void Dispose()
        {
            if (Descriptor >= 0)
            {
                _ = close(Descriptor);
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int directory, string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkdirat(int directory, string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlinkat(int directory, string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmodat(int directory, string path, uint mode, int flags);

    [DllImport("libc")]
    private static extern int close(int descriptor);
}
