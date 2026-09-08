using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace GhostShell.Files;

/// <summary>
/// Establishes the local-user trust boundary for encrypted content paths
/// before LiteDB or a cleanup operation is allowed to follow them.
/// </summary>
public static class PrivateContentPathGuard
{
    private const ushort FileTypeMask = 0xF000;
    private const ushort DirectoryFileType = 0x4000;
    private const ushort RegularFileType = 0x8000;
    private const ushort PermissionMask = 0x01FF;
    private const int LinuxCurrentDirectory = -100;
    private const int LinuxNoFollow = 0x100;
    private const uint LinuxRequiredFields = 0x000B;
    private const int LinuxUidOffset = 20;
    private const int LinuxModeOffset = 28;
    private const int DarwinModeOffset = 4;
    private const int DarwinUidOffset = 16;

    internal const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal const UnixFileMode OwnerFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static void EnsurePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path))
        {
            throw new InvalidDataException("The private-content path is not a directory.");
        }

        if (!Directory.Exists(path))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(path, OwnerDirectoryMode);
            }

            RestrictDirectoryPermissions(path);
        }
        else if (OperatingSystem.IsWindows())
        {
            ValidateOwnerAndType(path, DirectoryFileType, isDirectory: true);
            RestrictDirectoryPermissions(path);
        }

        ValidatePrivateDirectory(path);
    }

    public static void ValidatePrivateDirectory(string path) =>
        ValidatePrivatePath(path, DirectoryFileType, OwnerDirectoryMode, isDirectory: true);

    public static void ValidatePrivateFile(string path) =>
        ValidatePrivatePath(path, RegularFileType, OwnerFileMode, isDirectory: false);

    internal static FileStream CreatePrivateFile(string path, FileShare share)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = share,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFileMode;
        }

        var stream = new FileStream(path, options);
        try
        {
            RestrictFilePermissions(path);
            ValidatePrivateFile(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static void EnsurePrivateFile(string path)
    {
        if (File.Exists(path))
        {
            ValidatePrivateFile(path);
            return;
        }

        using var stream = CreatePrivateFile(path, FileShare.None);
    }

    internal static void ValidateOptionalPrivateFile(string path)
    {
        if (File.Exists(path))
        {
            ValidatePrivateFile(path);
        }
    }

    internal static void HardenGeneratedFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        ValidateOwnerAndType(path, RegularFileType, isDirectory: false);
        RestrictFilePermissions(path);
        ValidatePrivateFile(path);
    }

    private static void ValidatePrivatePath(
        string path,
        ushort expectedType,
        UnixFileMode expectedMode,
        bool isDirectory)
    {
        ValidateOwnerAndType(path, expectedType, isDirectory);
        if (OperatingSystem.IsWindows())
        {
            ValidateWindowsAcl(path, isDirectory);
            return;
        }

        var entry = ReadUnixEntry(path);
        if ((entry.Mode & PermissionMask) != (ushort)expectedMode)
        {
            throw new UnauthorizedAccessException(
                "Private-content paths must be accessible only to their owner.");
        }
    }

    private static void ValidateOwnerAndType(string path, ushort expectedType, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = isDirectory
                ? (FileSystemInfo)new DirectoryInfo(path)
                : new FileInfo(path);
            info.Refresh();
            if (!info.Exists
                || info.LinkTarget is not null
                || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || info.Attributes.HasFlag(FileAttributes.Directory) != isDirectory)
            {
                throw new InvalidDataException(
                    "Private content storage contains a linked or non-regular path.");
            }

            return;
        }

        var entry = ReadUnixEntry(path);
        if ((entry.Mode & FileTypeMask) != expectedType)
        {
            throw new InvalidDataException(
                "Private content storage contains a linked or non-regular path.");
        }

        if (entry.UserId != GetEffectiveUserId())
        {
            throw new UnauthorizedAccessException(
                "The private-content path is not owned by the current user.");
        }
    }

    private static void RestrictDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsPermissions(path, isDirectory: true);
        }
        else
        {
            File.SetUnixFileMode(path, OwnerDirectoryMode);
        }
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsPermissions(path, isDirectory: false);
        }
        else
        {
            File.SetUnixFileMode(path, OwnerFileMode);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsPermissions(string path, bool isDirectory)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException(
                "The current Windows user has no security identifier.");
        FileSystemSecurity permissions = isDirectory
            ? new DirectorySecurity()
            : new FileSecurity();
        permissions.SetOwner(owner);
        permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        permissions.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            isDirectory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        if (isDirectory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)permissions);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)permissions);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsAcl(string path, bool isDirectory)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException(
                "The current Windows user has no security identifier.");
        FileSystemSecurity permissions = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!owner.Equals(permissions.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException(
                "The private-content path is not owned by the current user.");
        }

        foreach (FileSystemAccessRule rule in permissions.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && !owner.Equals(rule.IdentityReference))
            {
                throw new UnauthorizedAccessException(
                    "Private-content paths must be accessible only to their owner.");
            }
        }
    }

    private static UnixEntry ReadUnixEntry(string path)
    {
        var buffer = new byte[256];
        int result;
        int modeOffset;
        int uidOffset;
        if (OperatingSystem.IsLinux())
        {
            result = Statx(
                LinuxCurrentDirectory,
                path,
                LinuxNoFollow,
                LinuxRequiredFields,
                buffer);
            modeOffset = LinuxModeOffset;
            uidOffset = LinuxUidOffset;
        }
        else if (OperatingSystem.IsMacOS())
        {
            result = LStat(path, buffer);
            modeOffset = DarwinModeOffset;
            uidOffset = DarwinUidOffset;
        }
        else
        {
            throw new PlatformNotSupportedException(
                "The platform cannot validate private-content ownership safely.");
        }

        if (result != 0)
        {
            throw new IOException("A private-content path could not be classified.");
        }

        return new UnixEntry(
            BitConverter.ToUInt16(buffer, modeOffset),
            BitConverter.ToUInt32(buffer, uidOffset));
    }

    [DllImport(
        "libc",
        EntryPoint = "statx",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        byte[] buffer);

    [DllImport(
        "libc",
        EntryPoint = "lstat",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int LStat(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        byte[] buffer);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct UnixEntry(ushort Mode, uint UserId);
}
