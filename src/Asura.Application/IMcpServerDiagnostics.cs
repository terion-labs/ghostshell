using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Performs an explicit user-authenticated, non-tool MCP initialization and
/// discovery probe without exposing process, vault, or SDK objects to clients.
/// </summary>
public interface IMcpServerDiagnostics
{
    event EventHandler<McpServerDiagnosticsChangedEventArgs>? Changed;

    McpServerDiagnosticsSnapshot Snapshot { get; }

    ValueTask RefreshAsync(CancellationToken cancellationToken);

    ValueTask<bool> ClearHistoryAsync(
        OperationContext context,
        CancellationToken cancellationToken);

    ValueTask<McpServerTestResult> TestAsync(
        McpServerTestRequest request,
        OperationContext context,
        CancellationToken cancellationToken);
}

public interface IMcpServerDiagnosticStore
{
    ValueTask<ApplicationRunResult<McpServerDiagnosticsSnapshot>> ReadAsync(
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<Unit>> WriteAsync(
        McpServerDiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<Unit>> ClearAsync(
        CancellationToken cancellationToken);
}

public sealed record McpServerTestRequest
{
    public McpServerTestRequest(
        McpServerProfileId profileId,
        long expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(profileId.Value))
        {
            throw new ArgumentException(
                "An MCP server test requires a profile identifier.",
                nameof(profileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            expectedRevision);
        ProfileId = profileId;
        ExpectedRevision = expectedRevision;
    }

    public McpServerProfileId ProfileId { get; }

    public long ExpectedRevision { get; }
}

public sealed record McpServerTestReport
{
    public McpServerTestReport(
        McpServerProfileId profileId,
        long revision,
        int discoveredToolCount,
        int enabledToolCount,
        DateTimeOffset completedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(profileId.Value))
        {
            throw new ArgumentException(
                "An MCP server test report requires a profile identifier.",
                nameof(profileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentOutOfRangeException.ThrowIfNegative(discoveredToolCount);
        if (discoveredToolCount
            > McpServerProfile.MaximumEnabledToolCount)
        {
            throw new ArgumentException(
                "An MCP server test report contains too many tools.",
                nameof(discoveredToolCount));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(enabledToolCount);
        if (enabledToolCount > discoveredToolCount)
        {
            throw new ArgumentException(
                "Enabled MCP tools cannot exceed discovered tools.",
                nameof(enabledToolCount));
        }

        if (completedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "An MCP server test completion timestamp must be UTC.",
                nameof(completedAtUtc));
        }

        ProfileId = profileId;
        Revision = revision;
        DiscoveredToolCount = discoveredToolCount;
        EnabledToolCount = enabledToolCount;
        CompletedAtUtc = completedAtUtc;
    }

    public McpServerProfileId ProfileId { get; }

    public long Revision { get; }

    public int DiscoveredToolCount { get; }

    public int EnabledToolCount { get; }

    public DateTimeOffset CompletedAtUtc { get; }
}

public sealed record McpServerTestError
{
    public McpServerTestError(
        string stableCode,
        string message,
        bool retryable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableCode);
        if (stableCode.Length > 128
            || stableCode.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '_'))
        {
            throw new ArgumentException(
                "An MCP server test error code must be a bounded stable identifier.",
                nameof(stableCode));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > 512 || message.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An MCP server test message must be bounded and printable.",
                nameof(message));
        }

        StableCode = stableCode;
        Message = message;
        Retryable = retryable;
    }

    public string StableCode { get; }

    public string Message { get; }

    public bool Retryable { get; }
}

public abstract record McpServerTestResult
{
    private McpServerTestResult()
    {
    }

    public sealed record Success(McpServerTestReport Report)
        : McpServerTestResult;

    public sealed record Failure(McpServerTestError Error)
        : McpServerTestResult;
}
