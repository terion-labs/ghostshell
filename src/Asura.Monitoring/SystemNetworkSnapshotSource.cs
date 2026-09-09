using System.Net.NetworkInformation;

namespace Asura.Monitoring;

/// <summary>
/// Reads cumulative counters through .NET for local Windows, macOS, and Linux hosts.
/// Loopback traffic is excluded so local IPC cannot dominate host network throughput.
/// </summary>
internal sealed class SystemNetworkSnapshotSource : INetworkSnapshotSource
{
    public ValueTask<IReadOnlyList<RawNetworkObservation>> CaptureAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observations = new List<RawNetworkObservation>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            try
            {
                var statistics = networkInterface.GetIPStatistics();
                observations.Add(new RawNetworkObservation(
                    networkInterface.Id,
                    Math.Max(0, statistics.BytesReceived),
                    Math.Max(0, statistics.BytesSent)));
            }
            catch (NetworkInformationException)
            {
                // An interface can disappear between enumeration and counter access.
                // Other active interfaces still form a useful host-level sample.
            }
        }

        return ValueTask.FromResult<IReadOnlyList<RawNetworkObservation>>(
            Array.AsReadOnly(observations.ToArray()));
    }
}
