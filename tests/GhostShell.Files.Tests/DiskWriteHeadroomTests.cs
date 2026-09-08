namespace GhostShell.Files.Tests;

public sealed class DiskWriteHeadroomTests
{
    [Fact]
    public void Large_files_are_allowed_when_the_destination_has_room()
    {
        const long terabyte = 1024L * 1024 * 1024 * 1024;
        var headroom = new DiskWriteHeadroom(() => terabyte * 2, terabyte);
        headroom.BeforeWrite(1024 * 1024);
    }

    [Fact]
    public void Known_length_is_checked_before_writing()
    {
        Assert.Throws<IOException>(() => new DiskWriteHeadroom(
            () => DiskWriteHeadroom.ReservedBytes + 99, 100));
    }

    [Fact]
    public void Concurrent_disk_consumption_is_detected_during_streaming()
    {
        var available = 1024L * 1024 * 1024;
        var headroom = new DiskWriteHeadroom(() => available, 0);
        headroom.BeforeWrite(8 * 1024 * 1024);
        available = DiskWriteHeadroom.ReservedBytes;
        Assert.Throws<IOException>(() => headroom.BeforeWrite(1));
    }

    [Fact]
    public void A_real_destination_resolves_its_volume_including_the_root_mount()
    {
        var headroom = new DiskWriteHeadroom(Path.GetTempPath());
        headroom.BeforeWrite(1);
    }
}
