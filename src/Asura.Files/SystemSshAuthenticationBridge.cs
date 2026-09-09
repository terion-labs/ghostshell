using Asura.Application;
using Asura.Core;
using Renci.SshNet;

namespace Asura.Files;

/// <summary>
/// Bridges system OpenSSH authentication into SDK-backed SSH channels without exposing
/// private-key material. OpenSSH remains responsible for configuration and platform
/// credential-store behavior; the agent supplies only delegated signing identities.
/// </summary>
internal sealed class SystemSshAuthenticationBridge(
    ConnectionProfile connection,
    IConnectionRuntime? connectionRuntime,
    ISshAgentIdentitySource identitySource)
{
    public async ValueTask<IPrivateKeySource[]> GetIdentitiesAsync(
        CancellationToken cancellationToken)
    {
        var identities = await ReadIdentitiesAsync(cancellationToken).ConfigureAwait(false);
        if (identities.Length > 0 || connectionRuntime is null)
        {
            return identities;
        }

        var result = await connectionRuntime
            .TestAsync(connection, progress: null, cancellationToken)
            .ConfigureAwait(false);
        if (result is ConnectionRuntimeResult<ConnectionTestReport>.Failure failure)
        {
            throw MapPreparationFailure(failure.Error, cancellationToken);
        }

        return await ReadIdentitiesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IPrivateKeySource[]> ReadIdentitiesAsync(
        CancellationToken cancellationToken)
        => await identitySource.ReadAsync(cancellationToken).ConfigureAwait(false);

    private static Exception MapPreparationFailure(
        ConnectionRuntimeError error,
        CancellationToken cancellationToken) =>
        error.Code switch
        {
            ConnectionRuntimeErrorCode.Cancelled =>
                new OperationCanceledException(cancellationToken),
            ConnectionRuntimeErrorCode.UnknownHostKey =>
                new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.HostKeyUnknown,
                    "The SSH server host key is not trusted."),
            ConnectionRuntimeErrorCode.HostKeyChanged =>
                new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.HostKeyChanged,
                    "The SSH server host key changed."),
            ConnectionRuntimeErrorCode.Timeout
                or ConnectionRuntimeErrorCode.Offline
                or ConnectionRuntimeErrorCode.ProcessFailed =>
                new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.Transient,
                    "The shared connection transport could not reach the SSH server.",
                    retryable: true),
            ConnectionRuntimeErrorCode.AuthenticationRequired
                or ConnectionRuntimeErrorCode.AuthenticationFailed
                or ConnectionRuntimeErrorCode.SecretNotFound
                or ConnectionRuntimeErrorCode.SecretAccessDenied
                or ConnectionRuntimeErrorCode.SecretInvalid
                or ConnectionRuntimeErrorCode.SecretVaultUnavailable
                or ConnectionRuntimeErrorCode.SecretVaultFailure =>
                new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.AuthenticationFailed,
                    "The shared connection transport could not authenticate this SSH connection."),
            _ => new RemoteFileSessionException(
                RemoteFileSessionErrorCode.InvalidConfiguration,
                "The shared SSH connection transport is unavailable."),
        };
}

internal interface ISshAgentIdentitySource
{
    ValueTask<IPrivateKeySource[]> ReadAsync(CancellationToken cancellationToken);
}
