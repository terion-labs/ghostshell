using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Owns the durable lifecycle of app-created remote multiplexer sessions.
/// A failed explicit termination becomes a retryable tombstone; ordinary
/// disconnect and application shutdown never request remote termination.
/// </summary>
public sealed class TerminalMultiplexerCoordinator
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(12);
    private const int SessionAlreadyAbsentExitCode = 44;
    private const string TerminateScript =
        "if command -v tmux >/dev/null 2>&1 "
        + "&& tmux -L asura has-session -t \"$1\" 2>/dev/null; then "
        + "tmux -L asura kill-session -t \"$1\"; exit $?; fi; "
        + "if command -v screen >/dev/null 2>&1 "
        + "&& screen -S \"$1\" -X select . >/dev/null 2>&1; then "
        + "screen -S \"$1\" -X quit; exit $?; fi; "
        + "exit 44";
    private readonly ITerminalMultiplexingPreferenceStore _preferenceStore;
    private readonly ITerminalMultiplexerLeaseStore _leaseStore;
    private readonly IConnectionCommandExecutor _commands;
    private readonly TimeProvider _timeProvider;

    public event EventHandler? LeasesChanged;

    public TerminalMultiplexerCoordinator(
        ITerminalMultiplexingPreferenceStore preferenceStore,
        ITerminalMultiplexerLeaseStore leaseStore,
        IConnectionCommandExecutor commands,
        TimeProvider? timeProvider = null)
    {
        _preferenceStore = preferenceStore
            ?? throw new ArgumentNullException(nameof(preferenceStore));
        _leaseStore = leaseStore ?? throw new ArgumentNullException(nameof(leaseStore));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<ApplicationRunResult<TerminalMultiplexingMode>> ReadPreferenceAsync(
        CancellationToken cancellationToken) =>
        _preferenceStore.ReadAsync(cancellationToken);

    public ValueTask<ApplicationRunResult<Unit>> WritePreferenceAsync(
        TerminalMultiplexingMode mode,
        CancellationToken cancellationToken) =>
        _preferenceStore.WriteAsync(mode, cancellationToken);

    public ValueTask<ApplicationRunResult<IReadOnlyList<TerminalMultiplexerLease>>> ListAsync(
        CancellationToken cancellationToken) =>
        _leaseStore.ListAsync(cancellationToken);

    public async ValueTask RegisterAsync(
        ConnectionProfile connection,
        TerminalMultiplexerSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(session);
        if (connection.ConnectionKind != ConnectionKind.Ssh)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var existing = (await _leaseStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .ValueOrDefault()
            ?.FirstOrDefault(lease =>
                lease.ConnectionId == connection.Id
                && string.Equals(
                    lease.Session.SessionName,
                    session.SessionName,
                    StringComparison.Ordinal));
        var saved = await _leaseStore.UpsertAsync(
                new TerminalMultiplexerLease(
                    connection.Id,
                    session,
                    existing?.State ?? TerminalMultiplexerLeaseState.Active,
                    existing?.CreatedAt ?? now,
                    now),
                cancellationToken)
            .ConfigureAwait(false);
        if (saved.IsSuccess)
        {
            LeasesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask<TerminalMultiplexerTerminationResult> TerminateAsync(
        ConnectionProfile connection,
        TerminalMultiplexerSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(session);
        if (connection.ConnectionKind != ConnectionKind.Ssh)
        {
            return new(false, false, "Only SSH terminals can host managed multiplexer sessions.");
        }

        // The local close is allowed to complete before this network command.
        // Persist the intent first so an app exit or process failure cannot lose
        // the cleanup request while the unreachable host is still timing out.
        await MarkTerminationPendingAsync(connection, session).ConfigureAwait(false);
        var result = await _commands.ExecuteAsync(
                new ConnectionCommand(
                    connection,
                    "/bin/sh",
                    ["-c", TerminateScript, "asura-multiplexer", session.SessionName],
                    ControlTimeout,
                    maximumOutputCharacters: 4096),
                cancellationToken)
            .ConfigureAwait(false);
        if (result is { Outcome: ConnectionCommandOutcome.Exited, ExitCode: 0 })
        {
            await RemoveLeaseAsync(connection.Id, session.SessionName).ConfigureAwait(false);
            return new(true, false, "The remote multiplexer session was terminated.");
        }

        if (result is
            {
                Outcome: ConnectionCommandOutcome.Exited,
                ExitCode: SessionAlreadyAbsentExitCode,
            })
        {
            await RemoveLeaseAsync(connection.Id, session.SessionName).ConfigureAwait(false);
            return new(true, false, "The remote multiplexer session had already ended.");
        }

        return new(
            false,
            true,
            "The remote session could not be reached. Cleanup will be retried from Managed remote sessions.");
    }

    public async ValueTask<ApplicationRunResult<Unit>> ForgetAsync(
        TerminalMultiplexerLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var result = await _leaseStore.DeleteAsync(
            lease.ConnectionId,
            lease.Session.SessionName,
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            LeasesChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    private async ValueTask MarkTerminationPendingAsync(
        ConnectionProfile connection,
        TerminalMultiplexerSession session)
    {
        var now = _timeProvider.GetUtcNow();
        var leases = await _leaseStore.ListAsync(CancellationToken.None).ConfigureAwait(false);
        var existing = leases.ValueOrDefault()?.FirstOrDefault(lease =>
            lease.ConnectionId == connection.Id
            && string.Equals(
                lease.Session.SessionName,
                session.SessionName,
                StringComparison.Ordinal));
        var saved = await _leaseStore.UpsertAsync(
                new TerminalMultiplexerLease(
                    connection.Id,
                    session,
                    TerminalMultiplexerLeaseState.TerminationPending,
                    existing?.CreatedAt ?? now,
                    now),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (saved.IsSuccess)
        {
            LeasesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async ValueTask RemoveLeaseAsync(
        ConnectionId connectionId,
        string sessionName)
    {
        var deleted = await _leaseStore.DeleteAsync(
                connectionId,
                sessionName,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (deleted.IsSuccess)
        {
            LeasesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

internal static class ApplicationRunResultExtensions
{
    public static T? ValueOrDefault<T>(this ApplicationRunResult<T> result) =>
        result.IsSuccess ? result.Value : default;
}
