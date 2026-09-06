using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GhostShell.Infrastructure;

/// <summary>
/// Resolves credentials only inside the SSH adapter boundary, authenticates a bounded diagnostic
/// connection, and clears every mutable credential buffer before returning a non-secret report.
/// </summary>
internal sealed class SshNetAuthenticationProbe(
    ISecretVault secretVault,
    SshKnownHostStore knownHosts,
    IWorkspaceNetworkConnector? networkConnector = null) : ISshAuthenticationProbe
{
    private static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromSeconds(12);

    public async ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> AuthenticateAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Endpoint is not ConnectionEndpoint.Ssh { Username: { } username } endpoint)
        {
            return Fail(ConnectionRuntimeErrorCode.InvalidProfile);
        }

        SshHostKeyCandidate? trusted;
        try
        {
            trusted = await knownHosts.ReadAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Fail(ConnectionRuntimeErrorCode.Cancelled);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Fail(ConnectionRuntimeErrorCode.ProcessFailed);
        }

        if (profile.HostKeyPolicy != SshHostKeyPolicy.InsecureIgnore && trusted is null)
        {
            return Fail(ConnectionRuntimeErrorCode.UnknownHostKey);
        }

        var buffers = new List<byte[]>();
        var disposables = new List<IDisposable>();
        SshClient? client = null;
        try
        {
            var authentication = await CreateAuthenticationAsync(
                    profile,
                    username,
                    buffers,
                    disposables,
                    cancellationToken)
                .ConfigureAwait(false);
            if (authentication is ConnectionRuntimeResult<AuthenticationMethod>.Failure authenticationFailure)
            {
                return ConnectionRuntimeResult<ConnectionTestReport>.Fail(authenticationFailure.Error);
            }

            var method = ((ConnectionRuntimeResult<AuthenticationMethod>.Success)authentication).Value;
            disposables.Add(method);
            var connection = WorkspaceSshConnectionInfo.Create(
                endpoint,
                username,
                method,
                networkConnector);
            connection.Timeout = AuthenticationTimeout;
            connection.RetryAttempts = 1;
            client = new SshClient(connection)
            {
                KeepAliveInterval = profile.KeepAlive.Enabled
                    ? profile.KeepAlive.Interval
                    : Timeout.InfiniteTimeSpan,
            };
            var hostKeyChanged = false;
            client.HostKeyReceived += (_, eventArgs) =>
            {
                var presented = new SshHostKeyCandidate(
                    eventArgs.HostKeyName,
                    Convert.ToBase64String(eventArgs.HostKey));
                var accepted = profile.HostKeyPolicy == SshHostKeyPolicy.InsecureIgnore
                    || trusted == presented;
                hostKeyChanged = trusted is not null && !accepted;
                eventArgs.CanTrust = accepted;
            };

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (hostKeyChanged && exception is SshException)
            {
                return Fail(ConnectionRuntimeErrorCode.HostKeyChanged);
            }

            return ConnectionRuntimeResult<ConnectionTestReport>.Succeed(new ConnectionTestReport(
                profile.Id,
                ConnectionKind.Ssh,
                ConnectionTestVerification.EndpointAuthenticated,
                endpointReached: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(ConnectionRuntimeErrorCode.Cancelled);
        }
        catch (SshAuthenticationException)
        {
            return Fail(ConnectionRuntimeErrorCode.AuthenticationFailed);
        }
        catch (Exception exception) when (exception is
            FormatException or CryptographicException or DecoderFallbackException)
        {
            return Fail(ConnectionRuntimeErrorCode.SecretInvalid);
        }
        catch (SshOperationTimeoutException)
        {
            return Fail(ConnectionRuntimeErrorCode.Timeout);
        }
        catch (Exception exception) when (IsOffline(exception))
        {
            return Fail(ConnectionRuntimeErrorCode.Offline);
        }
        catch (Exception exception) when (exception is SshException or InvalidOperationException or ArgumentException)
        {
            return Fail(ConnectionRuntimeErrorCode.ProcessFailed);
        }
        finally
        {
            TryDispose(client);
            DisposeAuthentication(buffers, disposables);
        }
    }

    private async ValueTask<ConnectionRuntimeResult<AuthenticationMethod>> CreateAuthenticationAsync(
        ConnectionProfile profile,
        string username,
        List<byte[]> buffers,
        List<IDisposable> disposables,
        CancellationToken cancellationToken)
    {
        switch (profile.Authentication)
        {
            case ConnectionAuthentication.None:
                return ConnectionRuntimeResult<AuthenticationMethod>.Succeed(
                    new NoneAuthenticationMethod(username));
            case ConnectionAuthentication.SshAgent:
                return ConnectionRuntimeResult<AuthenticationMethod>.Fail(
                    ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.AuthenticationRequired));
            case ConnectionAuthentication.Password password:
                {
                    var resolved = await ResolveAsync(
                            profile.Id,
                            password.PasswordSecret,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (resolved is ConnectionRuntimeResult<byte[]>.Failure failure)
                    {
                        return ConnectionRuntimeResult<AuthenticationMethod>.Fail(failure.Error);
                    }

                    var bytes = ((ConnectionRuntimeResult<byte[]>.Success)resolved).Value;
                    buffers.Add(bytes);
                    return ConnectionRuntimeResult<AuthenticationMethod>.Succeed(
                        new PasswordAuthenticationMethod(username, bytes));
                }
            case ConnectionAuthentication.PrivateKey privateKey:
                {
                    var keyResult = await ResolveAsync(
                            profile.Id,
                            privateKey.PrivateKeySecret,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (keyResult is ConnectionRuntimeResult<byte[]>.Failure keyFailure)
                    {
                        return ConnectionRuntimeResult<AuthenticationMethod>.Fail(keyFailure.Error);
                    }

                    var keyBytes = ((ConnectionRuntimeResult<byte[]>.Success)keyResult).Value;
                    buffers.Add(keyBytes);
                    try
                    {
                        string? passphrase = null;
                        if (privateKey.PassphraseSecret is { } passphraseReference)
                        {
                            var passphraseResult = await ResolveAsync(
                                    profile.Id,
                                    passphraseReference,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            if (passphraseResult is ConnectionRuntimeResult<byte[]>.Failure passphraseFailure)
                            {
                                return ConnectionRuntimeResult<AuthenticationMethod>.Fail(passphraseFailure.Error);
                            }

                            var passphraseBytes = ((ConnectionRuntimeResult<byte[]>.Success)passphraseResult).Value;
                            buffers.Add(passphraseBytes);
                            passphrase = new UTF8Encoding(
                                encoderShouldEmitUTF8Identifier: false,
                                throwOnInvalidBytes: true).GetString(passphraseBytes);
                        }

                        var keyStream = new MemoryStream(keyBytes, writable: false);
                        disposables.Add(keyStream);
                        var keyFile = passphrase is null
                            ? new PrivateKeyFile(keyStream)
                            : new PrivateKeyFile(keyStream, passphrase);
                        disposables.Add(keyFile);
                        return ConnectionRuntimeResult<AuthenticationMethod>.Succeed(
                            new PrivateKeyAuthenticationMethod(username, keyFile));
                    }
                    catch (Exception exception) when (exception is
                        SshException or
                        FormatException or
                        CryptographicException or
                        DecoderFallbackException)
                    {
                        return ConnectionRuntimeResult<AuthenticationMethod>.Fail(
                            ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.SecretInvalid));
                    }
                }
            default:
                return ConnectionRuntimeResult<AuthenticationMethod>.Fail(
                    ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.InvalidProfile));
        }
    }

    private async ValueTask<ConnectionRuntimeResult<byte[]>> ResolveAsync(
        ConnectionId connectionId,
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        SecretVaultResult<SecretMaterial> result;
        try
        {
            result = await secretVault.ResolveAsync(
                    new ResolveSecretRequest(
                        reference,
                        new SecretScope(SecretScopeKind.Connection, connectionId.Value),
                        new SecretUsePurpose(
                            SecretUseKind.ConnectionAuthentication,
                            connectionId.Value)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ConnectionRuntimeResult<byte[]>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Cancelled));
        }

        if (result is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            return ConnectionRuntimeResult<byte[]>.Fail(MapVaultError(failure.Error.Code));
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        return ConnectionRuntimeResult<byte[]>.Succeed(bytes);
    }

    private static ConnectionRuntimeError MapVaultError(SecretVaultErrorCode code) =>
        ConnectionRuntimeError.Create(code switch
        {
            SecretVaultErrorCode.InvalidRequest => ConnectionRuntimeErrorCode.InvalidProfile,
            SecretVaultErrorCode.Unavailable => ConnectionRuntimeErrorCode.SecretVaultUnavailable,
            SecretVaultErrorCode.NotFound => ConnectionRuntimeErrorCode.SecretNotFound,
            SecretVaultErrorCode.AccessDenied => ConnectionRuntimeErrorCode.SecretAccessDenied,
            SecretVaultErrorCode.AuthenticationRequired => ConnectionRuntimeErrorCode.AuthenticationRequired,
            SecretVaultErrorCode.UserCancelled or SecretVaultErrorCode.Cancelled =>
                ConnectionRuntimeErrorCode.Cancelled,
            SecretVaultErrorCode.CorruptEntry => ConnectionRuntimeErrorCode.SecretInvalid,
            _ => ConnectionRuntimeErrorCode.SecretVaultFailure,
        });

    private static bool IsOffline(Exception exception) => exception is SocketException
        || exception.InnerException is SocketException;

    private static void DisposeAuthentication(
        IEnumerable<byte[]> buffers,
        IEnumerable<IDisposable> disposables)
    {
        try
        {
            foreach (var disposable in disposables.Reverse())
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Cleanup continues so all mutable credential buffers are cleared.
                }
            }
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private static void TryDispose(SshClient? client)
    {
        try
        {
            client?.Disconnect();
            client?.Dispose();
        }
        catch
        {
            // Cleanup must not replace the classified connection result.
        }
    }

    private static ConnectionRuntimeResult<ConnectionTestReport> Fail(
        ConnectionRuntimeErrorCode code) =>
        ConnectionRuntimeResult<ConnectionTestReport>.Fail(ConnectionRuntimeError.Create(code));
}
