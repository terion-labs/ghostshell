using System.Security.Cryptography;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// Serializes a closed, metadata-only recent-session schema. Input is snapshotted before
/// serialization so the asynchronous destination write cannot observe caller mutation.
/// </summary>
public sealed class DeterministicRecentSessionHistoryExporter
    : IRecentSessionHistoryExporter
{
    private readonly TimeProvider _timeProvider;

    public DeterministicRecentSessionHistoryExporter(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public async ValueTask<
        RecentSessionHistoryExportResult<RecentSessionHistoryExportReceipt>> ExportAsync(
        IReadOnlyList<RecentSessionRecord> recentSessions,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recentSessions);
        ArgumentNullException.ThrowIfNull(destination);

        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        if (!CanWrite(destination))
        {
            return Failure(
                RecentSessionHistoryExportErrorCode.DestinationUnavailable,
                "The recent-session history destination is not writable.");
        }

        if (recentSessions.Count > RecentSessionHistoryExportFormat.MaximumRecordCount)
        {
            return Failure(
                RecentSessionHistoryExportErrorCode.TooManyRecords,
                $"Recent-session history exports are limited to {RecentSessionHistoryExportFormat.MaximumRecordCount} records.");
        }

        var snapshot = new RecentSessionRecord[recentSessions.Count];
        for (var index = 0; index < recentSessions.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled();
            }

            if (recentSessions[index] is not { } recentSession)
            {
                return Failure(
                    RecentSessionHistoryExportErrorCode.InvalidHistory,
                    "Recent-session history contains an invalid record.");
            }

            snapshot[index] = recentSession;
        }

        var exportedAt = _timeProvider.GetUtcNow().ToUniversalTime();

        byte[] json;
        try
        {
            json = RecentSessionHistoryJson.SerializeNewestFirst(
                snapshot,
                exportedAt,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        try
        {
            await destination.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (Exception exception) when (IsDestinationFailure(exception))
        {
            return Failure(
                RecentSessionHistoryExportErrorCode.DestinationUnavailable,
                "The recent-session history export could not be written.");
        }

        return RecentSessionHistoryExportResult<RecentSessionHistoryExportReceipt>.Success(
            new RecentSessionHistoryExportReceipt(
                snapshot.Length,
                exportedAt,
                json.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(json))));
    }

    private static bool CanWrite(Stream destination)
    {
        try
        {
            return destination.CanWrite;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsDestinationFailure(Exception exception) =>
        exception is IOException or NotSupportedException or ObjectDisposedException
        or UnauthorizedAccessException or InvalidOperationException;

    private static RecentSessionHistoryExportResult<RecentSessionHistoryExportReceipt>
        Cancelled() =>
        Failure(
            RecentSessionHistoryExportErrorCode.Cancelled,
            "The recent-session history export was cancelled.");

    private static RecentSessionHistoryExportResult<RecentSessionHistoryExportReceipt>
        Failure(
            RecentSessionHistoryExportErrorCode code,
            string message) =>
        RecentSessionHistoryExportResult<RecentSessionHistoryExportReceipt>.Failure(
            new RecentSessionHistoryExportError(code, message));
}
