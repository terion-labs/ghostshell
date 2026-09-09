using Asura.Application;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

/// <summary>
/// The live Git panel presentation preference, backed by one SQLite row and
/// read lazily on first ask. Storage failures never take the panel down with
/// them: a row that cannot be read means the defaults, and a write that fails
/// still applies the change in memory for this run.
/// </summary>
public sealed class SqliteGitPanelPreferences : IGitPanelPreferences
{
    private readonly AsuraDatabase _database;
    private volatile GitPanelPreferenceState? _current;

    public SqliteGitPanelPreferences(AsuraDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public event EventHandler? Changed;

    public async ValueTask<GitPanelPreferenceState> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (_current is { } current)
        {
            return current;
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT unstaged_view_is_tree,
                       staged_view_is_tree
                FROM git_panel_preference
                WHERE singleton_id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _current = new GitPanelPreferenceState(
                    reader.GetInt64(0) != 0,
                    reader.GetInt64(1) != 0);
            }
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // The defaults stand; the next successful apply writes over
            // whatever is wrong with the row.
        }

        return _current ??= GitPanelPreferenceState.Default;
    }

    public async ValueTask ApplyAsync(
        GitPanelPreferenceState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        _current = state;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE git_panel_preference
                SET unstaged_view_is_tree = $unstaged,
                    staged_view_is_tree = $staged
                WHERE singleton_id = 1;
                """;
            command.Parameters.AddWithValue("$unstaged", state.UnstagedViewIsTree ? 1 : 0);
            command.Parameters.AddWithValue("$staged", state.StagedViewIsTree ? 1 : 0);
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
