using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using Renci.SshNet;

namespace GhostShell.Files;

/// <summary>
/// Opens SSH local port-forwards for database panels. Lives beside the SFTP
/// adapter because this project owns the SSH.NET boundary: credential
/// resolution against the vault, agent identities, and host-key enforcement
/// are shared with it rather than reimplemented.
/// </summary>
public sealed class SshNetDatabaseTunnelFactory(
    ISecretVault secretVault,
    ISshHostKeyTrustStore knownHosts,
    IConnectionRuntime? connectionRuntime = null,
    IWorkspaceNetworkConnector? networkConnector = null) : IDatabaseTunnelFactory
{
    public async ValueTask<IDatabaseTunnelLease> OpenAsync(
        ConnectionProfile connection,
        string targetHost,
        int targetPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        if (connection.Endpoint is not ConnectionEndpoint.Ssh endpoint)
        {
            throw new InvalidOperationException(
                $"Connection '{connection.Name}' is not an SSH connection and cannot tunnel a database.");
        }

        if (string.IsNullOrWhiteSpace(endpoint.Username))
        {
            throw new InvalidOperationException(
                "The SSH profile requires an explicit username to tunnel a database.");
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
                networkConnector);
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
                        "The SSH server host key changed. Review the connection before tunneling.",
                    RemoteFileSessionErrorCode.HostKeyStoreInvalid =>
                        "The trusted SSH host-key store is unavailable or malformed.",
                    _ => "The SSH server host key is not trusted. Review the connection before tunneling.",
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

            var forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            client.AddForwardedPort(forward);
            forward.Start();
            return new SshNetDatabaseTunnelLease(
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
                $"The SSH tunnel through '{connection.Name}' could not be established: {exception.Message}",
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
                {
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
                            "No SSH identity is available to tunnel this connection.");
                    }

                    return new PrivateKeyAuthenticationMethod(username, identities);
                }
            case ConnectionAuthentication.Password password:
                {
                    var bytes = await ResolveSecretAsync(
                        connection,
                        password.PasswordSecret,
                        cancellationToken).ConfigureAwait(false);
                    ownedBuffers.Add(bytes);
                    return new PasswordAuthenticationMethod(username, bytes);
                }
            case ConnectionAuthentication.PrivateKey privateKey:
                {
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
                }
            default:
                throw new InvalidOperationException("The SSH authentication mode is invalid.");
        }
    }

    private async ValueTask<byte[]> ResolveSecretAsync(
        ConnectionProfile connection,
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        var scope = new SecretScope(SecretScopeKind.Connection, connection.Id.Value);
        var purpose = new SecretUsePurpose(
            SecretUseKind.ConnectionAuthentication,
            connection.Id.Value);
        var result = await secretVault.ResolveAsync(
            new ResolveSecretRequest(reference, scope, purpose),
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
                // Cleanup must continue so every owned credential is released.
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

    private sealed class SshNetDatabaseTunnelLease(
        SshClient client,
        ForwardedPortLocal forward,
        List<byte[]> ownedBuffers,
        List<IDisposable> ownedDisposables) : IDatabaseTunnelLease
    {
        public int LocalPort { get; } = (int)forward.BoundPort;

        public ValueTask DisposeAsync()
        {
            try
            {
                forward.Stop();
            }
            catch
            {
                // The forward may already be down with the session.
            }

            TryDispose(client);
            DisposeAuthentication(ownedBuffers, ownedDisposables);
            return ValueTask.CompletedTask;
        }
    }
}
