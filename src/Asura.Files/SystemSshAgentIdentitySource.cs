using System.Net.Sockets;
using Renci.SshNet;
using SshNet.Agent;

namespace Asura.Files;

internal sealed class SystemSshAgentIdentitySource : ISshAgentIdentitySource
{
    public async ValueTask<IPrivateKeySource[]> ReadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var agent = new SshAgent();
            return [.. (await agent
                    .RequestIdentitiesAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Cast<IPrivateKeySource>()];
        }
        catch (Exception exception) when (exception is
            SshAgentException or IOException or SocketException or InvalidOperationException)
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.AuthenticationFailed,
                "The system SSH agent is unavailable.",
                innerException: exception);
        }
    }
}
