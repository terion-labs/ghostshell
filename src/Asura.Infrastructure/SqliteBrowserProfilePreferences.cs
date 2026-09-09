using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

/// <summary>
/// Live browser-profile sharing settings backed by one SQLite row. An
/// unavailable row falls back to the privacy-compatible product default and
/// never prevents the browser from starting.
/// </summary>
public sealed class SqliteBrowserProfilePreferences : IBrowserProfilePreferences
{
    private readonly AsuraDatabase _database;
    private volatile BrowserProfileSettings _current = BrowserProfileSettings.Default;

    public SqliteBrowserProfilePreferences(AsuraDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public BrowserProfileSettings Current => _current;

    public event EventHandler? Changed;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _database
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sharing, default_profile_id
                FROM browser_profile_preference
                WHERE singleton_id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _current = BrowserProfileSettings.Default;
                return;
            }

            var sharing = reader.GetInt64(0) switch
            {
                0L => BrowserProfileSharing.Shared,
                1L => BrowserProfileSharing.PerWorkspace,
                _ => throw new InvalidOperationException(
                    "The browser profile sharing preference is invalid."),
            };
            var defaultProfileId = reader.IsDBNull(1)
                ? (BrowserProfileId?)null
                : new BrowserProfileId(reader.GetString(1));
            _current = new BrowserProfileSettings(sharing, defaultProfileId);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            _current = BrowserProfileSettings.Default;
        }
    }

    public async ValueTask ApplyAsync(
        BrowserProfileSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _current = settings;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await using var connection = await _database
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE browser_profile_preference
                SET sharing = $sharing,
                    default_profile_id = $defaultProfileId
                WHERE singleton_id = 1;
                """;
            command.Parameters.AddWithValue(
                "$sharing",
                settings.Sharing == BrowserProfileSharing.PerWorkspace ? 1 : 0);
            command.Parameters.AddWithValue(
                "$defaultProfileId",
                settings.DefaultProfileId is { } profileId
                    ? profileId.Value
                    : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // Keep the live choice. The next successful edit persists it.
        }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is SqliteException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException;
}
