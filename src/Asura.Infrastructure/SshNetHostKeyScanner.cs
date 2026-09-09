using System.Net.Sockets;
using Asura.Application;
using Asura.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Asura.Infrastructure;

/// <summary>
/// Performs a bounded SSH handshake only far enough to capture the server's public host key. It
/// deliberately rejects that key on the wire; trust is a separate compare-and-swap operation.
/// </summary>
internal sealed class SshNetHostKeyScanner(IWorkspaceNetworkConnector? networkConnector = null) : ISshHostKeyScanner
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(12);

    public async ValueTask<ConnectionRuntimeResult<SshHostKeyCandidate>> ScanAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Endpoint is not ConnectionEndpoint.Ssh endpoint)
        {
            return Fail(ConnectionRuntimeErrorCode.InvalidProfile);
        }

        var username = endpoint.Username ?? "asura-host-key-scan";
        var connection = WorkspaceSshConnectionInfo.Create(
            endpoint,
            username,
            new NoneAuthenticationMethod(username),
            networkConnector);
        connection.Timeout = ScanTimeout;
        connection.RetryAttempts = 1;
        using var client = new SshClient(connection);
        SshHostKeyCandidate? candidate = null;
        client.HostKeyReceived += (_, eventArgs) =>
        {
            try
            {
                candidate = new SshHostKeyCandidate(
                    eventArgs.HostKeyName,
                    Convert.ToBase64String(eventArgs.HostKey));
            }
            catch (ArgumentException)
            {
                candidate = null;
            }

            // Inspection never grants trust. The reviewed candidate is persisted separately.
            eventArgs.CanTrust = false;
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(ConnectionRuntimeErrorCode.Cancelled);
        }
        catch (Exception exception) when (candidate is not null && IsExpectedReviewAbort(exception))
        {
            return ConnectionRuntimeResult<SshHostKeyCandidate>.Succeed(candidate);
        }
        catch (SshOperationTimeoutException)
        {
            return Fail(ConnectionRuntimeErrorCode.Timeout);
        }
        catch (Exception exception) when (IsOffline(exception))
        {
            return Fail(ConnectionRuntimeErrorCode.Offline);
        }
        catch (Exception exception) when (exception is SshException or InvalidOperationException)
        {
            return Fail(ConnectionRuntimeErrorCode.ProcessFailed);
        }
        finally
        {
            try
            {
                client.Disconnect();
            }
            catch
            {
                // The deliberate handshake rejection commonly leaves no connected session.
            }
        }

        return candidate is null
            ? Fail(ConnectionRuntimeErrorCode.ProcessFailed)
            : ConnectionRuntimeResult<SshHostKeyCandidate>.Succeed(candidate);
    }

    private static bool IsExpectedReviewAbort(Exception exception) => exception is
        SshConnectionException or SshAuthenticationException;

    private static bool IsOffline(Exception exception) => exception is SocketException
        || exception.InnerException is SocketException;

    private static ConnectionRuntimeResult<SshHostKeyCandidate> Fail(
        ConnectionRuntimeErrorCode code) =>
        ConnectionRuntimeResult<SshHostKeyCandidate>.Fail(ConnectionRuntimeError.Create(code));
}
