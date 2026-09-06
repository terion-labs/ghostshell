using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using Renci.SshNet;

namespace GhostShell.Files;

/// <summary>
/// Opens a loopback-only SOCKS5 listener whose destinations are connected from
/// an SSH server. No process is installed remotely; SSH direct-tcpip channels
/// carry each browser connection.
/// </summary>
public sealed class SshNetBrowserTunnelFactory(
    ISecretVault secretVault,
    ISshHostKeyTrustStore knownHosts,
    IConnectionRuntime? connectionRuntime = null,
    IWorkspaceNetworkConnector? networkConnector = null)
{
    public async ValueTask<SshBrowserTunnel> OpenAsync(
        ConnectionProfile connection,
        CancellationToken cancellationToken) =>
        await OpenAsync(connection, networkConnector, cancellationToken).ConfigureAwait(false);

    public async ValueTask<SshBrowserTunnel> OpenAsync(
        ConnectionProfile connection,
        IWorkspaceNetworkConnector? routeConnector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Endpoint is not ConnectionEndpoint.Ssh endpoint)
        {
            throw new InvalidOperationException(
                $"Connection '{connection.Name}' is not an SSH connection and cannot route a browser.");
        }

        if (string.IsNullOrWhiteSpace(endpoint.Username))
        {
            throw new InvalidOperationException(
                "The SSH profile requires an explicit username to route a browser.");
        }

        var ownedBuffers = new List<byte[]>();
        var ownedDisposables = new List<IDisposable>();
        SshClient? client = null;
        try
        {
            var authentication = await CreateAuthenticationAsync(
                connection,
                endpoint.Username,
                ownedBuffers,
                ownedDisposables,
                cancellationToken).ConfigureAwait(false);
            ownedDisposables.Add(authentication);
            var info = SshNetConnectionInfoFactory.Create(
                endpoint,
                endpoint.Username,
                authentication,
                routeConnector);
            info.Timeout = TimeSpan.FromSeconds(15);
            info.RetryAttempts = 1;
            client = new SshClient(info)
            {
                KeepAliveInterval = connection.KeepAlive.Enabled
                    ? connection.KeepAlive.Interval
                    : TimeSpan.FromSeconds(30),
            };

            string? hostKeyFailure = null;
            client.HostKeyReceived += (_, eventArgs) =>
            {
                var candidate = new SshHostKeyCandidate(
                    eventArgs.HostKeyName,
                    Convert.ToBase64String(eventArgs.HostKey));
                var decision = SftpHostKeyPolicyEvaluator.Evaluate(
                    connection,
                    knownHosts,
                    candidate);
                hostKeyFailure = decision.Failure switch
                {
                    null => null,
                    RemoteFileSessionErrorCode.HostKeyChanged =>
                        "The SSH server host key changed. Review the connection before routing the browser.",
                    RemoteFileSessionErrorCode.HostKeyStoreInvalid =>
                        "The trusted SSH host-key store is unavailable or malformed.",
                    _ => "The SSH server host key is not trusted. Review the connection before routing the browser.",
                };
                eventArgs.CanTrust = decision.Trusted;
            };

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (hostKeyFailure is { } failure)
            {
                throw new InvalidOperationException(failure, exception);
            }

            var forward = new ForwardedPortDynamic("127.0.0.1", 0);
            client.AddForwardedPort(forward);
            forward.Start();
            return new SshBrowserTunnel(
                client,
                forward,
                ownedBuffers,
                ownedDisposables);
        }
        catch (Exception exception)
            when (exception is not InvalidOperationException
                && exception is not OperationCanceledException)
        {
            TryDispose(client);
            DisposeAuthentication(ownedBuffers, ownedDisposables);
            throw new InvalidOperationException(
                $"The browser route through '{connection.Name}' could not be established: {exception.Message}",
                exception);
        }
        catch
        {
            TryDispose(client);
            DisposeAuthentication(ownedBuffers, ownedDisposables);
            throw;
        }
    }

    private async ValueTask<AuthenticationMethod> CreateAuthenticationAsync(
        ConnectionProfile connection,
        string username,
        List<byte[]> ownedBuffers,
        List<IDisposable> ownedDisposables,
        CancellationToken cancellationToken)
    {
        switch (connection.Authentication)
        {
            case ConnectionAuthentication.None:
            case ConnectionAuthentication.SshAgent:
                var bridge = new SystemSshAuthenticationBridge(
                    connection,
                    connectionRuntime,
                    new SystemSshAgentIdentitySource());
                var identities = await bridge
                    .GetIdentitiesAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (identities.Length == 0)
                {
                    throw new InvalidOperationException(
                        "No SSH identity is available to route the browser.");
                }

                return new PrivateKeyAuthenticationMethod(username, identities);
            case ConnectionAuthentication.Password password:
                var passwordBytes = await ResolveSecretAsync(
                    connection,
                    password.PasswordSecret,
                    cancellationToken).ConfigureAwait(false);
                ownedBuffers.Add(passwordBytes);
                return new PasswordAuthenticationMethod(username, passwordBytes);
            case ConnectionAuthentication.PrivateKey privateKey:
                var keyBytes = await ResolveSecretAsync(
                    connection,
                    privateKey.PrivateKeySecret,
                    cancellationToken).ConfigureAwait(false);
                ownedBuffers.Add(keyBytes);
                string? passphrase = null;
                if (privateKey.PassphraseSecret is { } passphraseReference)
                {
                    var passphraseBytes = await ResolveSecretAsync(
                        connection,
                        passphraseReference,
                        cancellationToken).ConfigureAwait(false);
                    ownedBuffers.Add(passphraseBytes);
                    passphrase = Encoding.UTF8.GetString(passphraseBytes);
                }

                var keyStream = new MemoryStream(keyBytes, writable: false);
                ownedDisposables.Add(keyStream);
                var keyFile = passphrase is null
                    ? new PrivateKeyFile(keyStream)
                    : new PrivateKeyFile(keyStream, passphrase);
                ownedDisposables.Add(keyFile);
                return new PrivateKeyAuthenticationMethod(username, keyFile);
            default:
                throw new InvalidOperationException(
                    "The SSH authentication mode is invalid.");
        }
    }

    private async ValueTask<byte[]> ResolveSecretAsync(
        ConnectionProfile connection,
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        var result = await secretVault.ResolveAsync(
            new ResolveSecretRequest(
                reference,
                new SecretScope(SecretScopeKind.Connection, connection.Id.Value),
                new SecretUsePurpose(
                    SecretUseKind.ConnectionAuthentication,
                    connection.Id.Value)),
            cancellationToken).ConfigureAwait(false);
        if (result is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            throw failure.Error.Code is SecretVaultErrorCode.Cancelled
                or SecretVaultErrorCode.UserCancelled
                ? new OperationCanceledException(cancellationToken)
                : new InvalidOperationException(
                    "The SSH credential could not be resolved from the vault.");
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        return bytes;
    }

    private static void DisposeAuthentication(
        IEnumerable<byte[]> buffers,
        IEnumerable<IDisposable> disposables)
    {
        foreach (var disposable in disposables.Reverse())
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Cleanup continues so every credential buffer can be released.
            }
        }

        foreach (var buffer in buffers)
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void TryDispose(SshClient? client)
    {
        try
        {
            client?.Dispose();
        }
        catch
        {
            // A failed client has nothing left to release.
        }
    }

    public sealed class SshBrowserTunnel : IDisposable
    {
        private readonly SshClient _client;
        private readonly ForwardedPortDynamic _forward;
        private readonly IReadOnlyList<byte[]> _ownedBuffers;
        private readonly IReadOnlyList<IDisposable> _ownedDisposables;
        private bool _disposed;

        internal SshBrowserTunnel(
            SshClient client,
            ForwardedPortDynamic forward,
            IReadOnlyList<byte[]> ownedBuffers,
            IReadOnlyList<IDisposable> ownedDisposables)
        {
            _client = client;
            _forward = forward;
            _ownedBuffers = ownedBuffers;
            _ownedDisposables = ownedDisposables;
            LocalPort = checked((int)forward.BoundPort);
        }

        public int LocalPort { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _forward.Stop();
            }
            catch
            {
                // The SSH session may already have stopped the forward.
            }

            TryDispose(_client);
            DisposeAuthentication(_ownedBuffers, _ownedDisposables);
        }
    }
}
