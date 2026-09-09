using System.Security.Cryptography;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class DeterministicRecentSessionHistoryExporterTests
{
    private static readonly DateTimeOffset ReferenceTime = new(
        2026,
        7,
        23,
        9,
        30,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task ExportWritesOnlyAllowlistedDefinitionMetadataAndLeavesDestinationOpen()
    {
        var exportedAt = ReferenceTime.ToOffset(TimeSpan.FromHours(3));
        var older = Completed(
            "session-older",
            DefinitionKind.Connection,
            "production",
            PanelKind.Terminal,
            "Production shell",
            ReferenceTime.AddHours(-2),
            ReferenceTime.AddHours(-1),
            RecentSessionOutcome.GracefullyClosed);
        var newer = Completed(
            "session-newer",
            DefinitionKind.Screen,
            "operations",
            PanelKind.FileViewer,
            "Operations screen",
            ReferenceTime.AddMinutes(-30),
            ReferenceTime.AddMinutes(-10),
            RecentSessionOutcome.Failed);
        await using var destination = new MemoryStream();

        var result = await Exporter(exportedAt).ExportAsync(
            [older, newer],
            destination,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value!.RecordCount);
        Assert.Equal(ReferenceTime, result.Value.ExportedAt);
        Assert.Equal(destination.Length, result.Value.ByteLength);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(destination.ToArray())),
            result.Value.Sha256);

        using var document = JsonDocument.Parse(destination.ToArray());
        var root = document.RootElement;
        Assert.Equal(
            ["schemaVersion", "contentPolicy", "exportedAt", "sessions"],
            root.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        Assert.Equal(
            RecentSessionHistoryExportFormat.CurrentSchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            RecentSessionHistoryExportFormat.ContentPolicy,
            root.GetProperty("contentPolicy").GetString());
        Assert.Equal(
            "2026-07-23T09:30:00.0000000Z",
            root.GetProperty("exportedAt").GetString());

        var sessions = root.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(
            ["session-newer", "session-older"],
            sessions.Select(session => session.GetProperty("sessionId").GetString()), StringComparer.Ordinal);
        Assert.All(
            sessions,
            session => Assert.Equal(
                [
                    "sessionId",
                    "sourceDefinitionKind",
                    "sourceDefinitionId",
                    "panelKind",
                    "title",
                    "startedAt",
                    "endedAt",
                    "outcome",
                ],
                session.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal));
        Assert.Equal("screen", sessions[0].GetProperty("sourceDefinitionKind").GetString());
        Assert.Equal("operations", sessions[0].GetProperty("sourceDefinitionId").GetString());
        Assert.Equal("file-viewer", sessions[0].GetProperty("panelKind").GetString());
        Assert.Equal("failed", sessions[0].GetProperty("outcome").GetString());
        Assert.False(sessions[0].TryGetProperty("lastUsedAt", out _));

        await destination.WriteAsync(new byte[] { 0x0A });
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public async Task EquivalentHistoriesProduceIdenticalNewestFirstJson()
    {
        var newest = Completed(
            "session-newest",
            DefinitionKind.Connection,
            "newest",
            PanelKind.Terminal,
            "Newest",
            ReferenceTime.AddMinutes(-20),
            ReferenceTime,
            RecentSessionOutcome.Interrupted);
        var tiedSecond = Active(
            "session-a",
            DefinitionKind.Workspace,
            "active-a",
            PanelKind.Statistics,
            "Active A",
            ReferenceTime.AddMinutes(-10));
        var tiedThird = Active(
            "session-b",
            DefinitionKind.Workspace,
            "active-b",
            PanelKind.ProcessMonitor,
            "Active B",
            ReferenceTime.AddMinutes(-10));

        var first = await ExportBytesAsync([tiedThird, newest, tiedSecond]);
        var second = await ExportBytesAsync([tiedSecond, tiedThird, newest]);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        Assert.Equal(
            ["session-newest", "session-a", "session-b"],
            document.RootElement
                .GetProperty("sessions")
                .EnumerateArray()
                .Select(session => session.GetProperty("sessionId").GetString()), StringComparer.Ordinal);
    }

    [Fact]
    public async Task RecordLimitAndInvalidEntriesAreRejectedBeforeWriting()
    {
        var tooMany = Enumerable
            .Range(0, RecentSessionHistoryExportFormat.MaximumRecordCount + 1)
            .Select(index => Active(
                $"session-{index:D4}",
                DefinitionKind.Connection,
                $"connection-{index:D4}",
                PanelKind.Terminal,
                $"Connection {index}",
                ReferenceTime.AddSeconds(index)))
            .ToArray();
        await using var oversizedDestination = SeededDestination();
        await using var invalidDestination = SeededDestination();
        await using var maximumDestination = new MemoryStream();
        RecentSessionRecord[] invalid = [null!];

        var maximum = await Exporter().ExportAsync(
            tooMany[..RecentSessionHistoryExportFormat.MaximumRecordCount],
            maximumDestination,
            CancellationToken.None);
        var oversized = await Exporter().ExportAsync(
            tooMany,
            oversizedDestination,
            CancellationToken.None);
        var invalidRecord = await Exporter().ExportAsync(
            invalid,
            invalidDestination,
            CancellationToken.None);

        Assert.True(maximum.IsSuccess, maximum.Error?.Message);
        Assert.Equal(
            RecentSessionHistoryExportFormat.MaximumRecordCount,
            maximum.Value!.RecordCount);
        Assert.Equal(
            RecentSessionHistoryExportErrorCode.TooManyRecords,
            oversized.Error!.Code);
        Assert.Equal(
            RecentSessionHistoryExportErrorCode.InvalidHistory,
            invalidRecord.Error!.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, oversizedDestination.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, invalidDestination.ToArray());
    }

    [Fact]
    public async Task CancellationAndNonWritableDestinationsReturnTypedFailures()
    {
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await using var cancelledDestination = new MemoryStream();
        await using var readOnlyDestination = new MemoryStream(new byte[1], writable: false);
        using var writeCancellation = new CancellationTokenSource();
        await using var cancellingDestination = new CancellingWriteStream(writeCancellation);

        var cancelled = await Exporter().ExportAsync(
            [],
            cancelledDestination,
            alreadyCancelled.Token);
        var unavailable = await Exporter().ExportAsync(
            [],
            readOnlyDestination,
            CancellationToken.None);
        var cancelledDuringWrite = await Exporter().ExportAsync(
            [],
            cancellingDestination,
            writeCancellation.Token);

        Assert.Equal(
            RecentSessionHistoryExportErrorCode.Cancelled,
            cancelled.Error!.Code);
        Assert.Empty(cancelledDestination.ToArray());
        Assert.Equal(
            RecentSessionHistoryExportErrorCode.DestinationUnavailable,
            unavailable.Error!.Code);
        Assert.Equal(
            RecentSessionHistoryExportErrorCode.Cancelled,
            cancelledDuringWrite.Error!.Code);
        Assert.True(cancellingDestination.CanWrite);
    }

    [Fact]
    public async Task DestinationFailureDoesNotExposeBoundaryDetails()
    {
        await using var destination = new FailingWriteStream();

        var result = await Exporter().ExportAsync(
            [],
            destination,
            CancellationToken.None);

        Assert.Equal(
            RecentSessionHistoryExportErrorCode.DestinationUnavailable,
            result.Error!.Code);
        Assert.DoesNotContain(
            "filesystem-canary",
            result.Error.Message,
            StringComparison.Ordinal);
        Assert.True(destination.CanWrite);
    }

    private static async Task<byte[]> ExportBytesAsync(
        IReadOnlyList<RecentSessionRecord> recentSessions)
    {
        await using var destination = new MemoryStream();
        var result = await Exporter().ExportAsync(
            recentSessions,
            destination,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return destination.ToArray();
    }

    private static DeterministicRecentSessionHistoryExporter Exporter(
        DateTimeOffset? exportedAt = null) =>
        new(new FixedTimeProvider(exportedAt ?? ReferenceTime));

    private static RecentSessionRecord Active(
        string sessionId,
        DefinitionKind sourceKind,
        string sourceId,
        PanelKind panelKind,
        string title,
        DateTimeOffset startedAt) =>
        new(
            new SessionId(sessionId),
            new DefinitionKey(sourceKind, sourceId),
            panelKind,
            title,
            startedAt,
            endedAt: null,
            RecentSessionOutcome.Active);

    private static RecentSessionRecord Completed(
        string sessionId,
        DefinitionKind sourceKind,
        string sourceId,
        PanelKind panelKind,
        string title,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        RecentSessionOutcome outcome) =>
        new(
            new SessionId(sessionId),
            new DefinitionKey(sourceKind, sourceId),
            panelKind,
            title,
            startedAt,
            endedAt,
            outcome);

    private static MemoryStream SeededDestination()
    {
        var destination = new MemoryStream([1, 2, 3]);
        destination.Position = destination.Length;
        return destination;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CancellingWriteStream(CancellationTokenSource cancellation)
        : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellationToken);
        }
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("filesystem-canary"));
    }
}
