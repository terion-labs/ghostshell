namespace Asura.Application;

/// <summary>
/// Converts a non-secret connection plan into a one-use helper launch whose credentials are
/// resolved only when the helper starts. Implementations must keep secret values out of the
/// returned executable, arguments, environment snapshot, errors, and diagnostics.
/// </summary>
public interface IConnectionCredentialBroker : IAsyncDisposable
{
    ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PrepareLaunchAsync(
        ConnectionCredentialBrokerRequest request,
        CancellationToken cancellationToken);
}
