using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Asura.Packaging;

internal static class MacOsPackagePaths
{
    public const string BundleName = "Asura.app";

    public static string RequireExistingDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException(
                $"The {parameterName} directory does not exist.");
        }

        if (directory.LinkTarget is not null
            || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"The {parameterName} directory cannot be a symbolic link or reparse point.");
        }

        return ResolvePhysicalDirectory(fullPath);
    }

    public static string RequireDestination(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(
                Path.GetFileName(fullPath),
                BundleName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The macOS bundle destination must be named {BundleName}.",
                nameof(path));
        }

        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "The macOS bundle destination must have a parent directory.",
                nameof(path));
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                "The macOS bundle destination parent does not exist.");
        }

        var physicalPath = Path.Combine(
            ResolvePhysicalDirectory(parent),
            BundleName);
        if (File.Exists(physicalPath) || Directory.Exists(physicalPath))
        {
            throw new IOException(
                "The macOS bundle destination already exists and will not be overwritten.");
        }

        return physicalPath;
    }

    public static void ValidateSeparateTrees(string first, string second)
    {
        if (IsSameOrDescendant(first, second)
            || IsSameOrDescendant(second, first))
        {
            throw new ArgumentException(
                "The publish directory and macOS bundle destination cannot contain one another.",
                nameof(second));
        }
    }

    public static bool AreSameDirectory(string first, string second) => string.Equals(Path.GetRelativePath(first, second), ".", StringComparison.Ordinal);

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return true;
        }

        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, ".."
, StringComparison.Ordinal) && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private static string ResolvePhysicalDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RejectWindowsReparseAncestors(path);
            return Path.TrimEndingDirectorySeparator(new DirectoryInfo(path).FullName);
        }

        var resolvedPointer = RealPath(path, 0);
        if (resolvedPointer == 0)
        {
            throw new IOException(
                "The package directory physical path could not be resolved.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(
                Marshal.PtrToStringUTF8(resolvedPointer)
                ?? throw new IOException(
                    "The package directory physical path was invalid."));
        }
        finally
        {
            Free(resolvedPointer);
        }
    }

    private static void RejectWindowsReparseAncestors(string path)
    {
        var root = Path.GetPathRoot(path)
            ?? throw new InvalidDataException("The package path has no filesystem root.");
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current)
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "The package path contains a symbolic link or reparse point.");
            }
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern nint RealPath(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        nint resolvedPath);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(nint pointer);
}
