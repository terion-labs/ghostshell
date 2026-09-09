namespace Asura.Application;

/// <summary>
/// Writes caller-supplied recent-session definition metadata to a portable JSON document.
/// Implementations must not discover commands, terminal content, environment values,
/// credentials, paths, or any other runtime state. The destination remains open.
/// </summary>
public interface IRecentSessionHistoryExporter
{
    ValueTask<RecentSessionHistoryExportResult<RecentSessionHistoryExportReceipt>> ExportAsync(
        IReadOnlyList<RecentSessionRecord> recentSessions,
        Stream destination,
        CancellationToken cancellationToken);
}
