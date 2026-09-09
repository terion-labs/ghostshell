using System.Collections.ObjectModel;
using Asura.Core;

namespace Asura.Application;

public enum McpServerLifecycleState
{
    Untrusted,
    Disabled,
    Testing,
    Starting,
    Healthy,
    Degraded,
    Failed,
    CleanupUncertain,
    Stopped,
}

public enum McpServerSessionKind
{
    Test,
    AgentRun,
}

/// <summary>
/// A bounded, app-authored MCP diagnostic event. Server stderr and arbitrary
/// provider text are deliberately excluded from this contract.
/// </summary>
public sealed record McpServerDiagnosticEvent
{
    public const int MaximumCodeLength = 128;
    public const int MaximumMessageLength = 512;

    public McpServerDiagnosticEvent(
        DateTimeOffset occurredAtUtc,
        McpServerLifecycleState state,
        string stableCode,
        string message,
        int observedStderrBytes = 0,
        int observedStderrLines = 0,
        bool stderrWasTruncated = false)
    {
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An MCP diagnostic timestamp must be UTC.",
                nameof(occurredAtUtc));
        }

        ValidateText(stableCode, MaximumCodeLength, nameof(stableCode));
        if (stableCode.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'))
        {
            throw new ArgumentException(
                "An MCP diagnostic code must be a stable identifier.",
                nameof(stableCode));
        }

        ValidateText(message, MaximumMessageLength, nameof(message));
        ArgumentOutOfRangeException.ThrowIfNegative(observedStderrBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(observedStderrLines);
        OccurredAtUtc = occurredAtUtc;
        State = state;
        StableCode = stableCode;
        Message = message;
        ObservedStderrBytes = observedStderrBytes;
        ObservedStderrLines = observedStderrLines;
        StderrWasTruncated = stderrWasTruncated;
    }

    public DateTimeOffset OccurredAtUtc { get; }

    public McpServerLifecycleState State { get; }

    public string StableCode { get; }

    public string Message { get; }

    public int ObservedStderrBytes { get; }

    public int ObservedStderrLines { get; }

    public bool StderrWasTruncated { get; }

    private static void ValidateText(string value, int maximum, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > maximum || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "MCP diagnostic text must be bounded and printable.",
                name);
        }
    }
}

public sealed record McpServerDiagnosticSummary
{
    public const int MaximumRetainedEvents = 32;

    public McpServerDiagnosticSummary(
        McpServerProfileId profileId,
        long revision,
        string sessionId,
        McpServerSessionKind sessionKind,
        McpServerLifecycleState state,
        DateTimeOffset startedAtUtc,
        DateTimeOffset updatedAtUtc,
        IReadOnlyList<McpServerDiagnosticEvent> events)
    {
        if (string.IsNullOrWhiteSpace(profileId.Value))
        {
            throw new ArgumentException(
                "An MCP diagnostic summary requires a profile identifier.",
                nameof(profileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.Length > 64
            || sessionId.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')))
        {
            throw new ArgumentException(
                "An MCP diagnostic session identifier must be opaque and bounded.",
                nameof(sessionId));
        }

        if (startedAtUtc.Offset != TimeSpan.Zero
            || updatedAtUtc.Offset != TimeSpan.Zero
            || updatedAtUtc < startedAtUtc)
        {
            throw new ArgumentException(
                "MCP diagnostic summary timestamps must be ordered UTC values.");
        }

        ArgumentNullException.ThrowIfNull(events);
        if (events.Count is < 1 or > MaximumRetainedEvents)
        {
            throw new ArgumentException(
                "An MCP diagnostic summary must contain a bounded event history.",
                nameof(events));
        }

        if (events[^1].State != state
            || events.Any(item =>
                item.OccurredAtUtc < startedAtUtc
                || item.OccurredAtUtc > updatedAtUtc)
            || events.Zip(events.Skip(1))
                .Any(pair => pair.First.OccurredAtUtc > pair.Second.OccurredAtUtc))
        {
            throw new ArgumentException(
                "MCP diagnostic events must be ordered and match the summary state.",
                nameof(events));
        }

        ProfileId = profileId;
        Revision = revision;
        SessionId = sessionId;
        SessionKind = sessionKind;
        State = state;
        StartedAtUtc = startedAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Events = new ReadOnlyCollection<McpServerDiagnosticEvent>([.. events]);
    }

    public McpServerProfileId ProfileId { get; }

    public long Revision { get; }

    public string SessionId { get; }

    public McpServerSessionKind SessionKind { get; }

    public McpServerLifecycleState State { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public IReadOnlyList<McpServerDiagnosticEvent> Events { get; }
}

public sealed record McpServerDiagnosticsSnapshot
{
    public const int MaximumRetainedProfiles = 256;

    public McpServerDiagnosticsSnapshot(
        IReadOnlyList<McpServerDiagnosticSummary> summaries,
        bool cleanupUncertain,
        DateTimeOffset? cleanupUncertainAtUtc)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        if (summaries.Count > MaximumRetainedProfiles
            || summaries.Select(item => item.ProfileId).Distinct().Count()
                != summaries.Count)
        {
            throw new ArgumentException(
                "An MCP diagnostic snapshot must contain a bounded unique profile set.",
                nameof(summaries));
        }
        if (cleanupUncertainAtUtc is { } cleanupTimestamp
            && cleanupTimestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An MCP cleanup-circuit timestamp must be UTC.",
                nameof(cleanupUncertainAtUtc));
        }

        if (cleanupUncertain != cleanupUncertainAtUtc.HasValue)
        {
            throw new ArgumentException(
                "The MCP cleanup-circuit state and timestamp must agree.");
        }

        Summaries = new ReadOnlyCollection<McpServerDiagnosticSummary>(
            [.. summaries]);
        CleanupUncertain = cleanupUncertain;
        CleanupUncertainAtUtc = cleanupUncertainAtUtc;
    }

    public IReadOnlyList<McpServerDiagnosticSummary> Summaries { get; }

    public bool CleanupUncertain { get; }

    public DateTimeOffset? CleanupUncertainAtUtc { get; }
}

public sealed class McpServerDiagnosticsChangedEventArgs(
    McpServerDiagnosticsSnapshot snapshot) : EventArgs
{
    public McpServerDiagnosticsSnapshot Snapshot { get; } =
        snapshot ?? throw new ArgumentNullException(nameof(snapshot));
}
