namespace Asura.Infrastructure;

public interface IConnectionCommandRunner
{
    ValueTask<ConnectionProbeResult> RunAsync(
        ConnectionProbeCommand command,
        CancellationToken cancellationToken);
}
