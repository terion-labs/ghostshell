using System.Runtime.InteropServices;

namespace Asura.Infrastructure;

/// <summary>
/// Distinguishes regular Unix files from sockets, FIFOs, and device nodes
/// without opening them. Opening a FIFO merely to classify it can block the
/// caller indefinitely.
/// </summary>
internal static class NativeFileKind
{
    private const ushort FileTypeMask = 0xF000;
    private const ushort RegularFileType = 0x8000;
    private const int LinuxCurrentDirectory = -100;
    private const int LinuxNoFollow = 0x100;
    private const uint LinuxStatxType = 0x0001;
    private const int LinuxModeOffset = 28;
    private const int DarwinModeOffset = 4;

    internal static bool IsRegularFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        var mode = OperatingSystem.IsLinux()
            ? ReadLinuxMode(path)
            : OperatingSystem.IsMacOS()
                ? ReadDarwinMode(path)
                : (ushort)0;
        return (mode & FileTypeMask) == RegularFileType;
    }

    private static ushort ReadLinuxMode(string path)
    {
        var buffer = new byte[256];
        int result;
        try
        {
            result = Statx(
                LinuxCurrentDirectory,
                path,
                LinuxNoFollow,
                LinuxStatxType,
                buffer);
        }
        catch (Exception exception)
            when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new NotSupportedException(
                "The platform cannot safely classify local artifact entries.",
                exception);
        }

        if (result != 0)
        {
            throw new IOException("A local artifact entry could not be classified.");
        }

        return BitConverter.ToUInt16(buffer, LinuxModeOffset);
    }

    private static ushort ReadDarwinMode(string path)
    {
        var buffer = new byte[256];
        int result;
        try
        {
            result = LStat(path, buffer);
        }
        catch (Exception exception)
            when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new NotSupportedException(
                "The platform cannot safely classify local artifact entries.",
                exception);
        }

        if (result != 0)
        {
            throw new IOException("A local artifact entry could not be classified.");
        }

        return BitConverter.ToUInt16(buffer, DarwinModeOffset);
    }

    [DllImport(
        "libc",
        EntryPoint = "statx",
        SetLastError = true,
        CharSet = CharSet.Ansi)]
    private static extern int Statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        byte[] buffer);

    [DllImport(
        "libc",
        EntryPoint = "lstat",
        SetLastError = true,
        CharSet = CharSet.Ansi)]
    private static extern int LStat(string path, byte[] buffer);
}
