using System.Globalization;

namespace Asura.Monitoring;

/// <summary>
/// Reads cumulative interface counters from Linux procfs or the BSD/macOS netstat
/// surface. The successful command shape is retained after the first capture.
/// </summary>
internal sealed class PosixNetworkSnapshotSource(
    IPosixCommandTransport transport) : INetworkSnapshotSource
{
    private const int MaximumOutputCharacters = 256 * 1024;
    private static readonly PosixCommand LinuxCommand = new(
        "cat",
        ["/proc/net/dev"],
        TimeSpan.FromSeconds(5),
        MaximumOutputCharacters);
    private static readonly PosixCommand BsdCommand = new(
        "netstat",
        ["-ibn"],
        TimeSpan.FromSeconds(5),
        MaximumOutputCharacters);

    private NetworkCounterSurface _surface;

    public async ValueTask<IReadOnlyList<RawNetworkObservation>> CaptureAsync(
        CancellationToken cancellationToken)
    {
        if (_surface == NetworkCounterSurface.LinuxProcfs)
        {
            return await CaptureLinuxAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_surface == NetworkCounterSurface.BsdNetstat)
        {
            return await CaptureBsdAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var linux = await CaptureLinuxAsync(cancellationToken).ConfigureAwait(false);
            _surface = NetworkCounterSurface.LinuxProcfs;
            return linux;
        }
        catch (NetworkCounterSurfaceUnavailableException)
        {
            // Linux procfs is not present on BSD and macOS; try their native surface.
        }

        try
        {
            var bsd = await CaptureBsdAsync(cancellationToken).ConfigureAwait(false);
            _surface = NetworkCounterSurface.BsdNetstat;
            return bsd;
        }
        catch (NetworkCounterSurfaceUnavailableException exception)
        {
            throw new PlatformNotSupportedException(
                "The target host does not expose readable network interface counters.",
                exception);
        }
    }

    internal static IReadOnlyList<RawNetworkObservation> ParseLinux(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var observations = new List<RawNetworkObservation>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var interfaceId = line[..separator].Trim();
            var counters = line[(separator + 1)..].Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!IsIncludedInterface(interfaceId)
                || counters.Length < 9
                || !TryParseCounter(counters[0], out var receivedBytes)
                || !TryParseCounter(counters[8], out var sentBytes))
            {
                continue;
            }

            observations.Add(new RawNetworkObservation(
                interfaceId,
                receivedBytes,
                sentBytes));
        }

        return Array.AsReadOnly(observations.ToArray());
    }

    internal static IReadOnlyList<RawNetworkObservation> ParseBsd(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headerIndex = Array.FindIndex(lines, line =>
            line.Contains("Name", StringComparison.Ordinal)
            && line.Contains("Ibytes", StringComparison.Ordinal)
            && line.Contains("Obytes", StringComparison.Ordinal));
        if (headerIndex < 0)
        {
            return [];
        }

        var header = SplitColumns(lines[headerIndex]);
        var nameIndex = Array.IndexOf(header, "Name");
        var receivedIndex = Array.IndexOf(header, "Ibytes");
        var sentIndex = Array.IndexOf(header, "Obytes");
        if (nameIndex < 0 || receivedIndex < 0 || sentIndex < 0)
        {
            return [];
        }

        var requiredColumns = Math.Max(nameIndex, Math.Max(receivedIndex, sentIndex)) + 1;
        var byInterface = new Dictionary<string, RawNetworkObservation>(StringComparer.Ordinal);

        foreach (var line in lines.Skip(headerIndex + 1))
        {
            var columns = SplitColumns(line);
            if (columns.Length < requiredColumns)
            {
                continue;
            }

            var interfaceId = columns[nameIndex];
            if (!IsIncludedInterface(interfaceId)
                || !TryParseCounter(columns[receivedIndex], out var receivedBytes)
                || !TryParseCounter(columns[sentIndex], out var sentBytes))
            {
                continue;
            }

            if (byInterface.TryGetValue(interfaceId, out var previous))
            {
                receivedBytes = Math.Max(receivedBytes, previous.ReceivedBytes);
                sentBytes = Math.Max(sentBytes, previous.SentBytes);
            }

            byInterface[interfaceId] = new RawNetworkObservation(
                interfaceId,
                receivedBytes,
                sentBytes);
        }

        return Array.AsReadOnly(byInterface.Values.ToArray());
    }

    private async ValueTask<IReadOnlyList<RawNetworkObservation>> CaptureLinuxAsync(
        CancellationToken cancellationToken) =>
        await CaptureAsync(LinuxCommand, ParseLinux, cancellationToken).ConfigureAwait(false);

    private async ValueTask<IReadOnlyList<RawNetworkObservation>> CaptureBsdAsync(
        CancellationToken cancellationToken) =>
        await CaptureAsync(BsdCommand, ParseBsd, cancellationToken).ConfigureAwait(false);

    private async ValueTask<IReadOnlyList<RawNetworkObservation>> CaptureAsync(
        PosixCommand command,
        Func<string, IReadOnlyList<RawNetworkObservation>> parse,
        CancellationToken cancellationToken)
    {
        var result = await transport.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == PosixCommandOutcome.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.Outcome != PosixCommandOutcome.Exited || result.ExitCode != 0)
        {
            throw new NetworkCounterSurfaceUnavailableException();
        }

        var observations = parse(result.StandardOutput);
        if (observations.Count == 0)
        {
            throw new NetworkCounterSurfaceUnavailableException();
        }

        return observations;
    }

    private static string[] SplitColumns(string value) => value.Split(
        (char[]?)null,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseCounter(string value, out long counter) =>
        long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out counter)
        && counter >= 0;

    private static bool IsIncludedInterface(string interfaceId)
    {
        if (interfaceId.Length is 0 or > 256 || interfaceId.Any(char.IsControl))
        {
            return false;
        }

        var isLoopback = interfaceId.Equals("lo", StringComparison.Ordinal)
            || interfaceId.StartsWith("lo", StringComparison.Ordinal)
                && interfaceId.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;
        return !isLoopback;
    }

    private enum NetworkCounterSurface
    {
        Unknown,
        LinuxProcfs,
        BsdNetstat,
    }

    private sealed class NetworkCounterSurfaceUnavailableException : Exception;
}
