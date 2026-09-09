using Asura.Application;

namespace Asura.App;

public sealed record RecentSessionHistoryFileExportReceipt(
    string Path,
    RecentSessionHistoryExportReceipt Export);

public sealed class RecentSessionHistoryExportController
{
    public const string SuggestedExportFileName = "asura-session-history.json";

    private readonly IRecentSessionHistoryExporter _exporter;
    private readonly IRecentSessionHistoryExportFileSystem _fileSystem;
    private readonly IRecentSessionHistoryPathPicker _pathPicker;

    public RecentSessionHistoryExportController(
        IRecentSessionHistoryExporter exporter,
        IRecentSessionHistoryPathPicker pathPicker,
        IRecentSessionHistoryExportFileSystem? fileSystem = null)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _pathPicker = pathPicker ?? throw new ArgumentNullException(nameof(pathPicker));
        _fileSystem = fileSystem ?? new LocalRecentSessionHistoryExportFileSystem();
    }

    public async ValueTask<
        RecentSessionHistoryExportResult<RecentSessionHistoryFileExportReceipt>> ExportAsync(
        IReadOnlyList<RecentSessionRecord> recentSessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recentSessions);
        var outcome = Failure(
            RecentSessionHistoryExportErrorCode.DestinationUnavailable,
            "The selected session-history export file could not be written.");
        string? temporaryPath = null;
        try
        {
            var selectedPath = await _pathPicker.PickExportPathAsync(
                    SuggestedExportFileName,
                    recentSessions.Count,
                    cancellationToken)
                .ConfigureAwait(false);
            if (selectedPath is null)
            {
                outcome = Failure(
                    RecentSessionHistoryExportErrorCode.Cancelled,
                    "The session-history export was cancelled.");
            }
            else
            {
                var path = NormalizePath(selectedPath);
                var directory = Path.GetDirectoryName(path)
                    ?? throw new ArgumentException(
                        "An export directory is required.",
                        nameof(selectedPath));
                _fileSystem.CreateDirectory(directory);
                temporaryPath = Path.Combine(
                    directory,
                    $".asura-session-history-{Guid.NewGuid():N}.tmp");

                RecentSessionHistoryExportResult<RecentSessionHistoryExportReceipt> exported;
                await using (var destination = _fileSystem.CreateTemporaryFile(temporaryPath))
                {
                    exported = await _exporter.ExportAsync(
                            recentSessions,
                            destination,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (exported.IsSuccess)
                    {
                        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                if (!exported.IsSuccess)
                {
                    outcome = Failure(exported.Error!);
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _fileSystem.Publish(temporaryPath, path);
                    temporaryPath = null;
                    outcome = RecentSessionHistoryExportResult<
                        RecentSessionHistoryFileExportReceipt>.Success(
                            new RecentSessionHistoryFileExportReceipt(path, exported.Value!));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = Failure(
                RecentSessionHistoryExportErrorCode.Cancelled,
                "The session-history export was cancelled.");
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            outcome = Failure(
                RecentSessionHistoryExportErrorCode.DestinationUnavailable,
                "The selected session-history export file could not be written.");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    _fileSystem.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
                {
                    outcome = Failure(
                        RecentSessionHistoryExportErrorCode.CleanupFailure,
                        $"The export did not complete and temporary metadata file '{Path.GetFileName(temporaryPath)}' could not be removed. Delete it from the selected folder before retrying.");
                }
            }
        }

        return outcome;
    }

    private static string NormalizePath(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        var path = Path.GetFullPath(selectedPath.Trim());
        return Path.HasExtension(path) ? path : $"{path}.json";
    }

    private static RecentSessionHistoryExportResult<RecentSessionHistoryFileExportReceipt>
        Failure(RecentSessionHistoryExportError error) =>
        RecentSessionHistoryExportResult<RecentSessionHistoryFileExportReceipt>.Failure(error);

    private static RecentSessionHistoryExportResult<RecentSessionHistoryFileExportReceipt>
        Failure(RecentSessionHistoryExportErrorCode code, string message) =>
        Failure(new RecentSessionHistoryExportError(code, message));
}
