using System.Runtime.InteropServices;
using System.Text;
using Asura.Application;

namespace Asura.Infrastructure;

public sealed partial class FileSystemLocalArtifactControl
{
    private const int DarwinAtRemoveDirectory = 0x80;
    private const int DarwinDirectoryOpenFlags = 0x0010_0000 | 0x0100_0000 | 0x0000_0100;
    private const int DarwinEventOnlyOpenFlags = 0x0000_8000 | 0x0100_0000 | 0x0000_0100;
    private const uint DarwinRenameExclusive = 0x0000_0004;
    private const ushort DarwinFileTypeMask = 0xF000;
    private const ushort DarwinDirectoryType = 0x4000;
    private const ushort DarwinRegularFileType = 0x8000;
    private const int ErrorNotFound = 2;

    private MacOsFileIdentity? ReadMacOsIdentity(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        if (MacOsNative.LStat(path, out var status) != 0)
        {
            throw NativeMutationFailure();
        }

        return MacOsFileIdentity.From(status);
    }

    private HashSet<MacOsObjectIdentity> CaptureMacOsProtectedIdentities(
        LocalArtifactKind kind,
        MacOsFileIdentity? rootIdentity)
    {
        var identities = new HashSet<MacOsObjectIdentity>();
        if (!OperatingSystem.IsMacOS() || rootIdentity is null)
        {
            return identities;
        }

        AddProtectedDirectory(
            identities,
            kind == LocalArtifactKind.Cache ? null : _paths.CacheDirectory);
        AddProtectedDirectory(
            identities,
            kind == LocalArtifactKind.InactiveApplicationLogs
                ? null
                : _paths.ApplicationLogDirectory);
        AddProtectedDirectory(identities, _paths.DurableDataDirectory);
        AddProtectedFile(identities, _paths.ActiveApplicationLogPath);

        var parent = Path.GetDirectoryName(_paths.DirectoryFor(kind));
        if (parent is not null && Directory.Exists(parent))
        {
            var parentIdentity = ReadMacOsIdentity(parent);
            if (parentIdentity is not null
                && parentIdentity.Value.Device != rootIdentity.Value.Device)
            {
                throw UnsafeEntry();
            }
        }

        if (identities.Contains(rootIdentity.Value.ObjectIdentity))
        {
            throw UnsafeEntry();
        }

        return identities;
    }

    private void AddProtectedDirectory(
        HashSet<MacOsObjectIdentity> identities,
        string? path)
    {
        if (path is not null && Directory.Exists(path))
        {
            identities.Add(ReadMacOsIdentity(path)!.Value.ObjectIdentity);
        }
    }

    private void AddProtectedFile(
        HashSet<MacOsObjectIdentity> identities,
        string? path)
    {
        if (path is not null && File.Exists(path))
        {
            identities.Add(ReadMacOsIdentity(path)!.Value.ObjectIdentity);
        }
    }

    private static void ValidateMacOsPlannedIdentity(
        MacOsFileIdentity? rootIdentity,
        MacOsFileIdentity? identity,
        HashSet<MacOsObjectIdentity> protectedIdentities)
    {
        if (rootIdentity is null || identity is null)
        {
            return;
        }

        if (identity.Value.Device != rootIdentity.Value.Device
            || identity.Value.Owner != rootIdentity.Value.Owner
            || protectedIdentities.Contains(identity.Value.ObjectIdentity))
        {
            throw UnsafeEntry();
        }
    }

    private LocalArtifactControlResult<LocalArtifactClearReceipt> ExecuteMacOsPlan(
        ArtifactPlan plan)
    {
        long filesRemoved = 0;
        long bytesRemoved = 0;
        var mutationStarted = false;
        try
        {
            if (plan.Identity is null)
            {
                return LocalArtifactControlResult<LocalArtifactClearReceipt>.Success(
                    new LocalArtifactClearReceipt(plan.Kind, 0, 0));
            }

            using var root = OpenDirectory(plan.Root);
            RequireIdentity(root.Descriptor, plan.Identity.Value, requireDirectory: true);
            if (plan.Identity.Value.Owner != MacOsNative.GetEffectiveUserId())
            {
                throw UnsafeEntry();
            }
            var directoryIdentities = plan.Directories.ToDictionary(
                directory => RelativePath(plan.Root, directory.Path),
                directory => directory.Identity!.Value,
                StringComparer.Ordinal);
            var stagingName = $".asura-clear-{Guid.NewGuid():N}";
            CreateStaging(root.Descriptor, stagingName);
            var stagingRemoved = false;
            try
            {
                using var staging = OpenDirectoryAt(root.Descriptor, stagingName);
                var stagingIdentity = ReadIdentity(staging.Descriptor);
                if (stagingIdentity.Owner != plan.Identity.Value.Owner
                    || stagingIdentity.Device != plan.Identity.Value.Device)
                {
                    throw UnsafeEntry();
                }

                foreach (var file in plan.Files)
                {
                    using var parent = OpenPlannedParent(
                        root,
                        RelativePath(plan.Root, file.Path),
                        directoryIdentities,
                        out var leafName);
                    if (!TryOpenAt(parent.Descriptor, leafName, DarwinEventOnlyOpenFlags, out var opened))
                    {
                        continue;
                    }

                    using (opened)
                    {
                        RequireIdentity(opened.Descriptor, file.Identity!.Value, requireDirectory: false);
                    }

                    if (!MoveVerifyAndDelete(
                        parent.Descriptor,
                        leafName,
                        staging.Descriptor,
                        file.Identity!.Value,
                        isDirectory: false,
                        ref mutationStarted))
                    {
                        continue;
                    }

                    filesRemoved++;
                    bytesRemoved = checked(bytesRemoved + file.Length);
                }

                foreach (var directory in plan.Directories)
                {
                    using var parent = OpenPlannedParent(
                        root,
                        RelativePath(plan.Root, directory.Path),
                        directoryIdentities,
                        out var leafName);
                    if (!TryOpenAt(parent.Descriptor, leafName, DarwinDirectoryOpenFlags, out var opened))
                    {
                        continue;
                    }

                    using (opened)
                    {
                        RequireIdentity(opened.Descriptor, directory.Identity!.Value, requireDirectory: true);
                        if (!IsEmptyDirectory(opened.Descriptor))
                        {
                            continue;
                        }
                    }

                    _ = MoveVerifyAndDelete(
                        parent.Descriptor,
                        leafName,
                        staging.Descriptor,
                        directory.Identity!.Value,
                        isDirectory: true,
                        ref mutationStarted);
                }
            }
            finally
            {
                stagingRemoved = MacOsNative.UnlinkAt(
                    root.Descriptor,
                    stagingName,
                    DarwinAtRemoveDirectory) == 0;
            }

            if (!stagingRemoved)
            {
                throw NativeMutationFailure();
            }

            return LocalArtifactControlResult<LocalArtifactClearReceipt>.Success(
                new LocalArtifactClearReceipt(plan.Kind, filesRemoved, bytesRemoved));
        }
        catch (ArtifactScanException exception)
        {
            return Failure<LocalArtifactClearReceipt>(
                mutationStarted ? LocalArtifactControlErrorCode.PartialRemoval : exception.Code,
                exception.Message,
                filesRemoved,
                bytesRemoved);
        }
        catch (Exception exception) when (IsAccessFailure(exception) || IsStorageFailure(exception))
        {
            return Failure<LocalArtifactClearReceipt>(
                mutationStarted
                    ? LocalArtifactControlErrorCode.PartialRemoval
                    : IsAccessFailure(exception)
                        ? LocalArtifactControlErrorCode.AccessDenied
                        : StorageErrorCode(exception),
                "Local artifacts could not be completely cleared.",
                filesRemoved,
                bytesRemoved);
        }
    }

    private static DescriptorHandle OpenPlannedParent(
        DescriptorHandle root,
        string relativePath,
        IReadOnlyDictionary<string, MacOsFileIdentity> directoryIdentities,
        out string leafName)
    {
        var components = relativePath.Split(Path.DirectorySeparatorChar);
        leafName = components[^1];
        var current = root.Duplicate();
        var traversed = string.Empty;
        try
        {
            for (var index = 0; index < components.Length - 1; index++)
            {
                traversed = traversed.Length == 0
                    ? components[index]
                    : Path.Combine(traversed, components[index]);
                var next = OpenDirectoryAt(current.Descriptor, components[index]);
                current.Dispose();
                current = next;
                if (!directoryIdentities.TryGetValue(traversed, out var expected))
                {
                    throw UnsafeEntry();
                }

                RequireIdentity(current.Descriptor, expected, requireDirectory: true);
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static bool MoveVerifyAndDelete(
        int parent,
        string leafName,
        int staging,
        MacOsFileIdentity expected,
        bool isDirectory,
        ref bool mutationStarted)
    {
        var detachedName = Guid.NewGuid().ToString("N");
        if (MacOsNative.RenameAtExclusive(
                parent,
                leafName,
                staging,
                detachedName,
                DarwinRenameExclusive) != 0)
        {
            if (Marshal.GetLastPInvokeError() == ErrorNotFound)
            {
                return false;
            }

            throw NativeMutationFailure();
        }

        mutationStarted = true;

        try
        {
            var flags = isDirectory ? DarwinDirectoryOpenFlags : DarwinEventOnlyOpenFlags;
            using var detached = OpenAt(staging, detachedName, flags);
            RequireIdentity(detached.Descriptor, expected, isDirectory);
            if (isDirectory && !IsEmptyDirectory(detached.Descriptor))
            {
                throw UnsafeEntry();
            }

            if (MacOsNative.UnlinkAt(
                    staging,
                    detachedName,
                    isDirectory ? DarwinAtRemoveDirectory : 0) != 0)
            {
                throw NativeMutationFailure();
            }

            return true;
        }
        catch
        {
            _ = MacOsNative.RenameAtExclusive(
                staging,
                detachedName,
                parent,
                leafName,
                DarwinRenameExclusive);
            throw;
        }
    }

    private static bool IsEmptyDirectory(int descriptor)
    {
        var duplicate = MacOsNative.Duplicate(descriptor);
        if (duplicate < 0)
        {
            throw NativeMutationFailure();
        }

        var directory = MacOsNative.FileDescriptorOpenDirectory(duplicate);
        if (directory == IntPtr.Zero)
        {
            _ = MacOsNative.Close(duplicate);
            throw NativeMutationFailure();
        }

        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = MacOsNative.ReadDirectory(directory);
                if (entry == IntPtr.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != 0)
                    {
                        throw NativeMutationFailure();
                    }

                    return true;
                }

                var nameLength = (ushort)Marshal.ReadInt16(entry, 18);
                var bytes = new byte[nameLength];
                Marshal.Copy(IntPtr.Add(entry, 21), bytes, 0, nameLength);
                var name = Encoding.UTF8.GetString(bytes);
                if (name is not "." and not "..")
                {
                    return false;
                }
            }
        }
        finally
        {
            _ = MacOsNative.CloseDirectory(directory);
        }
    }

    private static void CreateStaging(int root, string name)
    {
        if (MacOsNative.MakeDirectoryAt(root, name, 0x1C0) != 0)
        {
            throw NativeMutationFailure();
        }
    }

    private static DescriptorHandle OpenDirectory(string path) =>
        Open(path, DarwinDirectoryOpenFlags);

    private static DescriptorHandle OpenDirectoryAt(int parent, string name) =>
        OpenAt(parent, name, DarwinDirectoryOpenFlags);

    private static DescriptorHandle Open(string path, int flags)
    {
        var descriptor = MacOsNative.Open(path, flags);
        return descriptor >= 0
            ? new DescriptorHandle(descriptor)
            : throw NativeMutationFailure();
    }

    private static DescriptorHandle OpenAt(int parent, string name, int flags)
    {
        var descriptor = MacOsNative.OpenAt(parent, name, flags);
        return descriptor >= 0
            ? new DescriptorHandle(descriptor)
            : throw NativeMutationFailure();
    }

    private static bool TryOpenAt(
        int parent,
        string name,
        int flags,
        out DescriptorHandle handle)
    {
        var descriptor = MacOsNative.OpenAt(parent, name, flags);
        if (descriptor >= 0)
        {
            handle = new DescriptorHandle(descriptor);
            return true;
        }

        if (Marshal.GetLastPInvokeError() == ErrorNotFound)
        {
            handle = new DescriptorHandle(-1);
            return false;
        }

        throw NativeMutationFailure();
    }

    private static MacOsFileIdentity ReadIdentity(int descriptor)
    {
        if (MacOsNative.FileStatus(descriptor, out var status) != 0)
        {
            throw NativeMutationFailure();
        }

        return MacOsFileIdentity.From(status);
    }

    private static void RequireIdentity(
        int descriptor,
        MacOsFileIdentity expected,
        bool requireDirectory)
    {
        var actual = ReadIdentity(descriptor);
        var expectedType = requireDirectory ? DarwinDirectoryType : DarwinRegularFileType;
        if (actual.ObjectIdentity != expected.ObjectIdentity
            || actual.Owner != expected.Owner
            || (actual.Mode & DarwinFileTypeMask) != expectedType
            || (!requireDirectory && actual.Size != expected.Size))
        {
            throw UnsafeEntry();
        }
    }

    private static string RelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.Length == 0
            || string.Equals(relative, ".", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || relative.Split(Path.DirectorySeparatorChar).Any(
                component => string.Equals(component, "..", StringComparison.Ordinal)))
        {
            throw UnsafeEntry();
        }

        return relative;
    }

    private static IOException NativeMutationFailure() =>
        new("A local artifact filesystem operation failed.");

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct MacOsObjectIdentity(int Device, ulong Inode);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct MacOsFileIdentity(
        int Device,
        ulong Inode,
        uint Owner,
        ushort Mode,
        long Size)
    {
        internal MacOsObjectIdentity ObjectIdentity => new(Device, Inode);

        internal static MacOsFileIdentity From(DarwinStat status) =>
            new(status.Device, status.Inode, status.Owner, status.Mode, status.Size);
    }

    private sealed class DescriptorHandle(int descriptor) : IDisposable
    {
        internal int Descriptor { get; } = descriptor;

        internal DescriptorHandle Duplicate()
        {
            var duplicate = MacOsNative.Duplicate(Descriptor);
            return duplicate >= 0
                ? new DescriptorHandle(duplicate)
                : throw NativeMutationFailure();
        }

        public void Dispose()
        {
            if (Descriptor >= 0)
            {
                _ = MacOsNative.Close(Descriptor);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinTimespec
    {
        internal long Seconds;
        internal long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStat
    {
        internal int Device;
        internal ushort Mode;
        internal ushort LinkCount;
        internal ulong Inode;
        internal uint Owner;
        internal uint Group;
        internal int SpecialDevice;
        internal DarwinTimespec AccessTime;
        internal DarwinTimespec ModificationTime;
        internal DarwinTimespec ChangeTime;
        internal DarwinTimespec BirthTime;
        internal long Size;
        internal long Blocks;
        internal int BlockSize;
        internal uint Flags;
        internal uint Generation;
        internal int Spare;
        internal long SpareOne;
        internal long SpareTwo;
    }

    private static partial class MacOsNative
    {
        [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Open(string path, int flags);

        [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int OpenAt(int directory, string path, int flags);

        [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int LStat(string path, out DarwinStat status);

        [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
        internal static partial int FileStatus(int descriptor, out DarwinStat status);

        [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int MakeDirectoryAt(int directory, string path, uint mode);

        [LibraryImport("libc", EntryPoint = "renameatx_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int RenameAtExclusive(
            int sourceDirectory,
            string source,
            int destinationDirectory,
            string destination,
            uint flags);

        [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int UnlinkAt(int directory, string path, int flags);

        [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
        internal static partial int Duplicate(int descriptor);

        [LibraryImport("libc", EntryPoint = "geteuid", SetLastError = false)]
        internal static partial uint GetEffectiveUserId();

        [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
        internal static partial IntPtr FileDescriptorOpenDirectory(int descriptor);

        [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
        internal static partial IntPtr ReadDirectory(IntPtr directory);

        [LibraryImport("libc", EntryPoint = "closedir", SetLastError = true)]
        internal static partial int CloseDirectory(IntPtr directory);

        [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static partial int Close(int descriptor);
    }
}
