using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteMcpServerDiagnosticStoreTests
{
    [Fact]
    public async Task SummaryRoundTripsAcrossDatabaseReopenAndClears()
    {
        await using var temporary = TemporaryDatabase.Create();
        var now = DateTimeOffset.UnixEpoch;
        var store = new SqliteMcpServerDiagnosticStore(
            temporary.Database,
            new FixedTimeProvider(now));
        var summary = new McpServerDiagnosticSummary(
            new McpServerProfileId("mcp.local-tools"),
            revision: 7,
            sessionId: "0123456789abcdef",
            McpServerSessionKind.Test,
            McpServerLifecycleState.Stopped,
            now,
            now,
            [
                new McpServerDiagnosticEvent(
                    now,
                    McpServerLifecycleState.Stopped,
                    "mcp_stopped",
                    "The MCP process stopped cleanly.",
                    observedStderrBytes: 42,
                    observedStderrLines: 2),
            ]);
        var snapshot = new McpServerDiagnosticsSnapshot(
            [summary],
            cleanupUncertain: false,
            cleanupUncertainAtUtc: null);

        Assert.True((await store.WriteAsync(
            snapshot,
            CancellationToken.None)).IsSuccess);
        await temporary.ReopenAsync();
        store = new SqliteMcpServerDiagnosticStore(
            temporary.Database,
            new FixedTimeProvider(now));

        var loaded = await store.ReadAsync(CancellationToken.None);
        Assert.True(loaded.IsSuccess);
        var persisted = Assert.Single(loaded.Value!.Summaries);
        Assert.Equal(summary.ProfileId, persisted.ProfileId);
        Assert.Equal(summary.SessionId, persisted.SessionId);
        Assert.Equal(42, Assert.Single(persisted.Events).ObservedStderrBytes);
        Assert.False(loaded.Value.CleanupUncertain);

        Assert.True((await store.ClearAsync(
            CancellationToken.None)).IsSuccess);
        Assert.Empty((await store.ReadAsync(
            CancellationToken.None)).Value!.Summaries);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
