using Asura.Application;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

/// <summary>
/// The live file-preview settings, backed by one SQLite row. Loaded once at
/// startup; a change is written through and announced in the same call, so
/// "the stored settings" and "the settings in effect" are never two things.
///
/// Storage failures never take previews down with them: a row that cannot be
/// read means the defaults, and a write that fails still applies the change
/// in memory for this run.
/// </summary>
public sealed class SqliteFilePreviewPreferences : IFilePreviewPreferences
{
    private readonly AsuraDatabase _database;
    private volatile FilePreviewSettings _current = FilePreviewSettings.Default;

    public SqliteFilePreviewPreferences(AsuraDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public FilePreviewSettings Current => _current;

    public event EventHandler? Changed;

    /// <summary>
    /// Reads the stored row into <see cref="Current"/>. Called once during
    /// composition, before anything previews.
    /// </summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT auto_load_threshold_bytes,
                       keep_previews_between_runs,
                       cache_budget_bytes
                FROM file_preview_settings
                WHERE singleton_id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _current = new FilePreviewSettings(
                    reader.GetInt64(0),
                    reader.GetInt64(1) != 0,
                    reader.GetInt64(2));
            }
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // The defaults stand; the next successful apply writes over
            // whatever is wrong with the row.
        }
    }

    public async ValueTask ApplyAsync(
        FilePreviewSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE file_preview_settings
                SET auto_load_threshold_bytes = $threshold,
                    keep_previews_between_runs = $keep,
                    cache_budget_bytes = $budget
                WHERE singleton_id = 1;
                """;
            command.Parameters.AddWithValue("$threshold", settings.AutoLoadThresholdBytes);
            command.Parameters.AddWithValue("$keep", settings.KeepPreviewsBetweenRuns ? 1 : 0);
            command.Parameters.AddWithValue("$budget", settings.CacheBudgetBytes);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // Applied for this run either way; persistence catches up on the
            // next successful write.
        }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is SqliteException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException;
}
