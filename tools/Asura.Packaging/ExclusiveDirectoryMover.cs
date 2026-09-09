using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Asura.Packaging;

internal static class ExclusiveDirectoryMover
{
    private const int ExistingPathError = 17;
    private const int CurrentWorkingDirectory = -100;
    private const uint RenameExchange = 0x00000002;
    private const uint RenameNoReplace = 0x00000001;
    private const uint RenameExclusive = 0x00000004;
    private const uint RenameNoFollowAny = 0x00000010;

    public static void Move(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (OperatingSystem.IsMacOS())
        {
            MoveOnMacOs(sourcePath, destinationPath);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            MoveOnLinux(sourcePath, destinationPath);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.Move(sourcePath, destinationPath);
            return;
        }

        throw new PlatformNotSupportedException(
            "Exclusive directory publication is not supported on this operating system.");
    }

    /// <summary>
    /// Atomically exchanges two existing directories. On success each path
    /// names the tree that was previously at the other path; on failure both
    /// paths retain their original trees.
    /// </summary>
    public static void Exchange(string firstPath, string secondPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondPath);

        if (OperatingSystem.IsMacOS())
        {
            var result = RenameMacOs(
                firstPath,
                secondPath,
                RenameExchange | RenameNoFollowAny);
            ThrowIfMoveFailed(result);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            int result;
            try
            {
                result = RenameAt2(
                    CurrentWorkingDirectory,
                    firstPath,
                    CurrentWorkingDirectory,
                    secondPath,
                    RenameExchange);
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new PlatformNotSupportedException(
                    "This Linux host does not expose renameat2 for atomic exchange.",
                    exception);
            }

            ThrowIfMoveFailed(result);
            return;
        }

        throw new PlatformNotSupportedException(
            "Atomic directory exchange is not supported on this operating system.");
    }

    private static void MoveOnMacOs(string sourcePath, string destinationPath)
    {
        var result = RenameMacOs(
            sourcePath,
            destinationPath,
            RenameExclusive | RenameNoFollowAny);
        ThrowIfMoveFailed(result);
    }

    private static void MoveOnLinux(string sourcePath, string destinationPath)
    {
        int result;
        try
        {
            result = RenameAt2(
                CurrentWorkingDirectory,
                sourcePath,
                CurrentWorkingDirectory,
                destinationPath,
                RenameNoReplace);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new PlatformNotSupportedException(
                "This Linux host does not expose renameat2 for exclusive publication.",
                exception);
        }

        ThrowIfMoveFailed(result);
    }

    private static void ThrowIfMoveFailed(int result)
    {
        if (result == 0)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ExistingPathError)
        {
            throw new IOException(
                "The destination appeared during publication and will not be overwritten.");
        }

        throw new IOException(
            "The directory could not be published atomically.",
            new Win32Exception(error));
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport(
        "libSystem.B.dylib",
        EntryPoint = "renamex_np",
        SetLastError = true)]
    private static extern int RenameMacOs(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourcePath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationPath,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport(
        "libc",
        EntryPoint = "renameat2",
        SetLastError = true)]
    private static extern int RenameAt2(
        int sourceDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourcePath,
        int destinationDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationPath,
        uint flags);
}
