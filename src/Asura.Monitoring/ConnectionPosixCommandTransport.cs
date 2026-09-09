using Asura.Application;
using Asura.Core;

namespace Asura.Monitoring;

internal sealed class ConnectionPosixCommandTransport(
    IConnectionCommandExecutor executor,
    ConnectionProfile connection) : IPosixCommandTransport
{
    public async ValueTask<PosixCommandResult> ExecuteAsync(
        PosixCommand command,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
                new ConnectionCommand(
                    connection,
                    command.Executable,
                    command.Arguments,
                    command.Timeout,
                    command.MaximumOutputCharacters),
                cancellationToken)
            .ConfigureAwait(false);
        return new PosixCommandResult(
            result.Outcome switch
            {
                ConnectionCommandOutcome.Exited => PosixCommandOutcome.Exited,
                ConnectionCommandOutcome.TimedOut => PosixCommandOutcome.TimedOut,
                ConnectionCommandOutcome.Cancelled => PosixCommandOutcome.Cancelled,
                ConnectionCommandOutcome.StartFailed
                    or ConnectionCommandOutcome.ConnectionFailed => PosixCommandOutcome.StartFailed,
                _ => throw new ArgumentOutOfRangeException(),
            },
            result.ExitCode,
            result.StandardOutput);
    }
}
