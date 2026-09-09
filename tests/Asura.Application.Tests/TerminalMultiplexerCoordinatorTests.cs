using Asura.Application;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class TerminalMultiplexerCoordinatorTests
{
    [Fact]
    public async Task ExplicitTerminationDeletesLeaseAfterRemoteMultiplexerQuits()
    {
        var store = new MemoryStore();
        var commands = new RecordingCommandExecutor
        {
            Result = new ConnectionCommandResult(
                ConnectionCommandOutcome.Exited,
                0,
                string.Empty),
        };
        var coordinator = new TerminalMultiplexerCoordinator(store, store, commands);
        var connection = SshProfile();
        var session = EstablishedSession();
        await coordinator.RegisterAsync(connection, session, CancellationToken.None);

        var result = await coordinator.TerminateAsync(
            connection,
            session,
            CancellationToken.None);

        Assert.True(result.Terminated);
        Assert.False(result.Deferred);
        Assert.Empty(store.Leases);
        var command = Assert.Single(commands.Commands);
        Assert.Equal("/bin/sh", command.Executable);
        Assert.Equal("-c", command.Arguments[0]);
        Assert.Contains("tmux -L asura kill-session", command.Arguments[1]);
        Assert.Contains("screen -S", command.Arguments[1]);
        Assert.Equal("asura-multiplexer", command.Arguments[2]);
        Assert.Equal(session.SessionName, command.Arguments[3]);
    }

    [Fact]
    public async Task UnreachableHostKeepsRetryableCleanupLease()
    {
        var store = new MemoryStore();
        var commands = new RecordingCommandExecutor
        {
            Result = new ConnectionCommandResult(
                ConnectionCommandOutcome.ConnectionFailed,
                null,
                string.Empty),
        };
        var coordinator = new TerminalMultiplexerCoordinator(store, store, commands);
        var connection = SshProfile();
        var session = EstablishedSession();
        await coordinator.RegisterAsync(connection, session, CancellationToken.None);

        var result = await coordinator.TerminateAsync(
            connection,
            session,
            CancellationToken.None);

        Assert.False(result.Terminated);
        Assert.True(result.Deferred);
        Assert.Equal(
            TerminalMultiplexerLeaseState.TerminationPending,
            Assert.Single(store.Leases).State);
    }

    [Fact]
    public async Task AlreadyAbsentRemoteMultiplexerClearsStaleLease()
    {
        var store = new MemoryStore();
        var commands = new RecordingCommandExecutor();
        commands.Results.Enqueue(new ConnectionCommandResult(
            ConnectionCommandOutcome.Exited,
            44,
            string.Empty,
            string.Empty));
        var coordinator = new TerminalMultiplexerCoordinator(store, store, commands);
        var connection = SshProfile();
        var session = EstablishedSession();
        await coordinator.RegisterAsync(connection, session, CancellationToken.None);

        var result = await coordinator.TerminateAsync(
            connection,
            session,
            CancellationToken.None);

        Assert.True(result.Terminated);
        Assert.Empty(store.Leases);
        Assert.Single(commands.Commands);
    }

    private static TerminalMultiplexerSession EstablishedSession() =>
        new(
            TerminalMultiplexingMode.Automatic,
            "asura-1234abcd",
            isEstablished: true);

    private static ConnectionProfile SshProfile() => new(
        new ConnectionId("ssh-test"),
        ConnectionProfile.CurrentSchemaVersion,
        "SSH test",
        new ConnectionEndpoint.Ssh("host.example", username: "deploy"),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.Strict);

    private sealed class MemoryStore :
        ITerminalMultiplexingPreferenceStore,
        ITerminalMultiplexerLeaseStore
    {
        public List<TerminalMultiplexerLease> Leases { get; } = [];

        public ValueTask<ApplicationRunResult<TerminalMultiplexingMode>> ReadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApplicationRunResult<TerminalMultiplexingMode>.Success(
                TerminalMultiplexingMode.Disabled));

        public ValueTask<ApplicationRunResult<Unit>> WriteAsync(
            TerminalMultiplexingMode mode,
            CancellationToken cancellationToken) => Success();

        public ValueTask<ApplicationRunResult<Unit>> UpsertAsync(
            TerminalMultiplexerLease lease,
            CancellationToken cancellationToken)
        {
            Leases.RemoveAll(item => item.ConnectionId == lease.ConnectionId
                && string.Equals(item.Session.SessionName, lease.Session.SessionName, StringComparison.Ordinal));
            Leases.Add(lease);
            return Success();
        }

        public ValueTask<ApplicationRunResult<Unit>> DeleteAsync(
            ConnectionId connectionId,
            string sessionName,
            CancellationToken cancellationToken)
        {
            Leases.RemoveAll(item => item.ConnectionId == connectionId
                && string.Equals(item.Session.SessionName, sessionName, StringComparison.Ordinal));
            return Success();
        }

        public ValueTask<ApplicationRunResult<IReadOnlyList<TerminalMultiplexerLease>>> ListAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                ApplicationRunResult<IReadOnlyList<TerminalMultiplexerLease>>.Success(
                    [.. Leases]));

        private static ValueTask<ApplicationRunResult<Unit>> Success() =>
            ValueTask.FromResult(ApplicationRunResult<Unit>.Success(Unit.Value));
    }

    private sealed class RecordingCommandExecutor : IConnectionCommandExecutor
    {
        public ConnectionCommandResult Result { get; set; } = null!;

        public Queue<ConnectionCommandResult> Results { get; } = [];

        public List<ConnectionCommand> Commands { get; } = [];

        public ValueTask<ConnectionCommandResult> ExecuteAsync(
            ConnectionCommand request,
            CancellationToken cancellationToken)
        {
            Commands.Add(request);
            return ValueTask.FromResult(Results.Count > 0 ? Results.Dequeue() : Result);
        }

        public ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(
            ConnectionBinaryCommand request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(
            ConnectionBinaryCommand request,
            Func<Stream, CancellationToken, ValueTask<T>> consumeOutput,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
