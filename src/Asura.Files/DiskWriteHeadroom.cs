namespace Asura.Files;

/// <summary>
/// Rechecks the destination volume while streaming. This is free-space
/// backpressure, not a maximum file size: large transfers proceed when space
/// exists and stop before consuming the volume's operating headroom.
/// </summary>
public sealed class DiskWriteHeadroom
{
    internal const long ReservedBytes = 64L * 1024 * 1024;
    private const long RecheckIntervalBytes = 8L * 1024 * 1024;
    private readonly Func<long> _availableBytes;
    private long _untilRecheck;

    public DiskWriteHeadroom(string destinationDirectory, long expectedBytes = 0)
        : this(AvailableBytesSource(destinationDirectory), expectedBytes)
    {
    }

    internal DiskWriteHeadroom(Func<long> availableBytes, long expectedBytes)
    {
        _availableBytes = availableBytes;
        EnsureAvailable(expectedBytes);
    }

    public void BeforeWrite(int count)
    {
        if (_untilRecheck < count)
        {
            EnsureAvailable(Math.Max(count, RecheckIntervalBytes));
            _untilRecheck = RecheckIntervalBytes;
        }
        _untilRecheck -= count;
    }

    private void EnsureAvailable(long neededBytes)
    {
        if (_availableBytes() - ReservedBytes < neededBytes)
        {
            throw new IOException("The destination disk does not have enough free space. Free some space and retry the transfer.");
        }
    }

    private static Func<long> AvailableBytesSource(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var drive = DriveInfo.GetDrives()
            .Where(candidate => fullPath.Equals(Path.TrimEndingDirectorySeparator(candidate.Name), comparison)
                || fullPath.StartsWith(Path.EndsInDirectorySeparator(candidate.Name)
                    ? candidate.Name
                    : candidate.Name + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(candidate => candidate.Name.Length)
            .FirstOrDefault() ?? throw new IOException("The destination disk's free space could not be determined.");
        return () => drive.AvailableFreeSpace;
    }
}
