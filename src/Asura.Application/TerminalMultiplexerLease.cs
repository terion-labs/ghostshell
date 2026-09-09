using Asura.Core;

namespace Asura.Application;

public enum TerminalMultiplexerLeaseState
{
    Active = 0,
    TerminationPending = 1,
}

public sealed record TerminalMultiplexerLease(
    ConnectionId ConnectionId,
    TerminalMultiplexerSession Session,
    TerminalMultiplexerLeaseState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface ITerminalMultiplexingPreferenceStore
{
    ValueTask<ApplicationRunResult<TerminalMultiplexingMode>> ReadAsync(
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<Unit>> WriteAsync(
        TerminalMultiplexingMode mode,
        CancellationToken cancellationToken);
}

public interface ITerminalMultiplexerLeaseStore
{
    ValueTask<ApplicationRunResult<Unit>> UpsertAsync(
        TerminalMultiplexerLease lease,
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<Unit>> DeleteAsync(
        ConnectionId connectionId,
        string sessionName,
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<IReadOnlyList<TerminalMultiplexerLease>>> ListAsync(
        CancellationToken cancellationToken);
}

public sealed record TerminalMultiplexerTerminationResult(
    bool Terminated,
    bool Deferred,
    string Detail);
