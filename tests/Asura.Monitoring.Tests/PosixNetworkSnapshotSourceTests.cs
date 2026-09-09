namespace Asura.Monitoring.Tests;

public sealed class PosixNetworkSnapshotSourceTests
{
    [Fact]
    public void LinuxParserReadsByteCountersAndExcludesLoopback()
    {
        var observations = PosixNetworkSnapshotSource.ParseLinux(
            """
            Inter-|   Receive                                                |  Transmit
             face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
                lo: 9000 1 0 0 0 0 0 0 8000 1 0 0 0 0 0 0
              eth0: 1234 2 0 0 0 0 0 0 5678 3 0 0 0 0 0 0
             wlan0: 2222 4 0 0 0 0 0 0 3333 5 0 0 0 0 0 0
            """);

        Assert.Equal(2, observations.Count);
        Assert.Equal(new RawNetworkObservation("eth0", 1_234, 5_678), observations[0]);
        Assert.Equal(new RawNetworkObservation("wlan0", 2_222, 3_333), observations[1]);
    }

    [Fact]
    public void BsdParserDeduplicatesAddressRowsAndExcludesLoopback()
    {
        var observations = PosixNetworkSnapshotSource.ParseBsd(
            """
            Name  Mtu   Network       Address            Ipkts Ierrs     Ibytes    Opkts Oerrs     Obytes  Coll
            en0   1500  <Link#6>      aa:bb:cc              10     0       1000       20     0       2000     0
            en0   1500  192.0.2       192.0.2.10             9     -        900       18     -       1800     -
            lo0   16384 <Link#1>      lo0                  100     0       9000      100     0       9000     0
            """);

        var observation = Assert.Single(observations);
        Assert.Equal(new RawNetworkObservation("en0", 1_000, 2_000), observation);
    }

    [Fact]
    public async Task CaptureFallsBackToNetstatAndRetainsTheSuccessfulSurface()
    {
        var transport = new RecordingPosixCommandTransport();
        transport.Results.Enqueue(new PosixCommandResult(
            PosixCommandOutcome.Exited,
            1,
            string.Empty));
        transport.Results.Enqueue(Success(BsdSample(received: 1_000, sent: 2_000)));
        transport.Results.Enqueue(Success(BsdSample(received: 2_000, sent: 4_000)));
        var source = new PosixNetworkSnapshotSource(transport);

        _ = await source.CaptureAsync(CancellationToken.None);
        var second = await source.CaptureAsync(CancellationToken.None);

        Assert.Equal(["cat", "netstat", "netstat"],
            transport.Commands.Select(command => command.Executable), StringComparer.Ordinal);
        Assert.Equal(2_000, Assert.Single(second).ReceivedBytes);
    }

    private static string BsdSample(long received, long sent) =>
        $"""
        Name  Mtu   Network       Address     Ipkts Ierrs Ibytes Opkts Oerrs Obytes Coll
        en0   1500  <Link#6>      aa:bb:cc       10     0 {received}    20     0 {sent}     0
        """;

    private static PosixCommandResult Success(string output) =>
        new(PosixCommandOutcome.Exited, 0, output);
}
