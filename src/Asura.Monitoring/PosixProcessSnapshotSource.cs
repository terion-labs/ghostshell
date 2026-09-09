using System.Globalization;

namespace Asura.Monitoring;

/// <summary>
/// Captures process counters through the portable <c>ps</c> surface. The command
/// transport decides where it runs, so the same parser can serve local, SSH,
/// container, and WSL-backed monitor sessions.
/// </summary>
internal sealed class PosixProcessSnapshotSource : IProcessSnapshotSource
{
    internal const int MaximumProcesses = 4_096;
    private const int MaximumProcessNameLength = 256;
    private static readonly PosixCommand ProcessSnapshotCommand = new(
        "ps",
        ["-A", "-o", "pid=", "-o", "rss=", "-o", "time=", "-o", "etime=", "-o", "comm="],
        TimeSpan.FromSeconds(5),
        maximumOutputCharacters: 1024 * 1024);

    private readonly IPosixCommandTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly int? _localProcessId;

    public PosixProcessSnapshotSource(
        IPosixCommandTransport transport,
        TimeProvider timeProvider,
        int? localProcessId)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _localProcessId = localProcessId;
    }

    public async ValueTask<RawProcessCapture> CaptureAsync(
        CancellationToken cancellationToken)
    {
        var result = await _transport
            .ExecuteAsync(ProcessSnapshotCommand, cancellationToken)
            .ConfigureAwait(false);
        if (result.Outcome == PosixCommandOutcome.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.Outcome == PosixCommandOutcome.StartFailed)
        {
            throw new PlatformNotSupportedException(
                "The target host does not expose the POSIX ps utility.");
        }

        if (result.Outcome != PosixCommandOutcome.Exited || result.ExitCode != 0)
        {
            throw new IOException("The target host process snapshot command failed.");
        }

        return Parse(result.StandardOutput, _timeProvider.GetUtcNow());
    }

    internal RawProcessCapture Parse(string output, DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(output);
        var observations = new List<RawProcessObservation>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseProcess(line, capturedAtUtc) is { } process)
            {
                observations.Add(process);
            }
        }

        if (observations.Count == 0)
        {
            throw new InvalidDataException(
                "The target host returned no readable process rows.");
        }

        var uptime = observations
            .Where(process => process.ProcessId == 1)
            .Select(process => capturedAtUtc - process.StartedAtUtc)
            .FirstOrDefault(value => value is not null)
            ?? observations
                .Select(process => capturedAtUtc - process.StartedAtUtc)
                .Where(value => value is not null)
                .Max()
            ?? TimeSpan.Zero;
        return new RawProcessCapture(
            uptime,
            observations.Count,
            Array.AsReadOnly(observations.Take(MaximumProcesses).ToArray()),
            observations.Count > MaximumProcesses);
    }

    private RawProcessObservation? TryParseProcess(
        string line,
        DateTimeOffset capturedAtUtc)
    {
        var columns = line.Split(
            (char[]?)null,
            5,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (columns.Length != 5
            || !int.TryParse(
                columns[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processId)
            || processId <= 0)
        {
            return null;
        }

        long? workingSetBytes = null;
        if (long.TryParse(
                columns[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var workingSetKib)
            && workingSetKib >= 0)
        {
            workingSetBytes = workingSetKib > long.MaxValue / 1024
                ? long.MaxValue
                : workingSetKib * 1024;
        }

        var processorTime = TryParseDuration(columns[2], out var parsedProcessorTime)
            ? parsedProcessorTime
            : (TimeSpan?)null;
        var elapsed = TryParseDuration(columns[3], out var parsedElapsed)
            ? parsedElapsed
            : (TimeSpan?)null;
        return new RawProcessObservation(
            processId,
            SanitizeName(columns[4], processId),
            workingSetBytes,
            processorTime,
            TryCalculateStartedAt(capturedAtUtc, elapsed),
            processId == _localProcessId);
    }

    private static DateTimeOffset? TryCalculateStartedAt(
        DateTimeOffset capturedAtUtc,
        TimeSpan? elapsed)
    {
        if (elapsed is not { } value)
        {
            return null;
        }

        try
        {
            return capturedAtUtc - value;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        var dayParts = value.Trim().Split('-', 2);
        var clock = dayParts[^1].Split(':');
        if (clock.Length is < 2 or > 3)
        {
            return false;
        }

        if (!int.TryParse(
                dayParts.Length == 2 ? dayParts[0] : "0",
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var days)
            || !int.TryParse(
                clock.Length == 3 ? clock[0] : "0",
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var hours)
            || !int.TryParse(
                clock[^2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var minutes)
            || !double.TryParse(
                clock[^1],
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var seconds)
            || days < 0
            || hours < 0
            || minutes is < 0 or >= 60
            || seconds is < 0 or >= 60)
        {
            return false;
        }

        try
        {
            duration = TimeSpan.FromDays(days)
                + TimeSpan.FromHours(hours)
                + TimeSpan.FromMinutes(minutes)
                + TimeSpan.FromSeconds(seconds);
            return true;
        }
        catch (Exception exception)
            when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string SanitizeName(string value, int processId)
    {
        var slash = value.LastIndexOf('/');
        var basename = slash >= 0 ? value[(slash + 1)..] : value;
        var sanitized = new string([.. basename
            .Where(character => !char.IsControl(character))
            .Take(MaximumProcessNameLength)]);
        return string.IsNullOrWhiteSpace(sanitized)
            ? $"Process {processId}"
            : sanitized;
    }
}
