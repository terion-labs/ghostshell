using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Asura.Packaging;

internal sealed record RegularPackageFile(long Length, UnixFileMode? UnixMode);

internal static class RegularPackageFileReader
{
    private const int UnixStatBufferSize = 512;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFileType = 0x8000;

    public static FileStream Open(string path, out RegularPackageFile inspection)
    {
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory
                    | FileAttributes.ReparsePoint
                    | FileAttributes.Device)) != FileAttributes.None)
            {
                throw new InvalidDataException(
                    "The publish payload contains a non-regular file.");
            }

            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.SequentialScan);
            inspection = new RegularPackageFile(stream.Length, null);
            return stream;
        }

        var flags = OperatingSystem.IsMacOS()
            ? 0x0004 | 0x00000100 | 0x01000000
            : 0x0800 | 0x00020000 | 0x00080000;
        var descriptor = OpenUnix(path, flags);
        if (descriptor < 0)
        {
            throw new IOException(
                "The publish file could not be opened without following a blocking stream.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        SafeFileHandle? handle = new((nint)descriptor, ownsHandle: true);
        try
        {
            var statBuffer = Marshal.AllocHGlobal(UnixStatBufferSize);
            try
            {
                if (FStat(descriptor, statBuffer) != 0)
                {
                    throw new IOException(
                        "The publish file type could not be inspected.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
                }

                var mode = ReadUnixMode(statBuffer);
                if ((mode & UnixFileTypeMask) != UnixRegularFileType)
                {
                    throw new InvalidDataException(
                        "The publish payload contains a non-regular file.");
                }

                var stream = new FileStream(handle, FileAccess.Read);
                handle = null;
                inspection = new RegularPackageFile(
                    stream.Length,
                    (UnixFileMode)(mode & ~UnixFileTypeMask));
                return stream;
            }
            finally
            {
                Marshal.FreeHGlobal(statBuffer);
            }
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static int ReadUnixMode(nint statBuffer)
    {
        var modeOffset = (OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, Architecture.X64 or Architecture.Arm64) => 4,
            (false, Architecture.X64) => 24,
            (false, Architecture.Arm64) => 16,
            _ => throw new PlatformNotSupportedException(
                "macOS packaging supports x64 and arm64 Unix validation hosts."),
        };
        return Marshal.ReadInt32(statBuffer, modeOffset) & 0xFFFF;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fileDescriptor, nint statBuffer);
}
