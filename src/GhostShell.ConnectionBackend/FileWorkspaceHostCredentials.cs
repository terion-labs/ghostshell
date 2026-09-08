using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Files;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace GhostShell.ConnectionBackend;

/// <summary>Authorizes the exact profile's credential and SSH signing callbacks; no general vault or signing RPC is exposed.</summary>
internal sealed class FileWorkspaceHostCredentials(FileProviderProfile profile, ConnectionProfile? connection,
    ISecretVault vault, ISshHostKeyTrustStore knownHosts, IConnectionRuntime? connectionRuntime) : IDisposable
{
    private readonly List<HostAlgorithm> _algorithms = [];
    private readonly List<IDisposable> _ownedKeys = [];
    private bool _identitiesLoaded;
    private bool _trusted;
    private int _signatures;
    private int _secretResolutions;
    private int _trustChecks;
    private readonly object _ownership = new();
    private int _activeCallbacks;
    private bool _disposed;

    internal void BeginOperation()
    {
        _secretResolutions = 0;
        _signatures = 0;
        _trustChecks = 0;
    }

    internal ConnectionProfile? GuestConnection => connection?.Authentication is ConnectionAuthentication.PrivateKey
        ? new ConnectionProfile(connection.Id, connection.SchemaVersion, connection.Name, connection.Endpoint,
            new ConnectionAuthentication.SshAgent(), connection.Startup, connection.KeepAlive, connection.HostKeyPolicy,
            connection.Tags, connection.PreferredPanel, connection.HostConnectionId)
        : connection;

    internal async Task<FileWorkspaceReply> HandleAsync(FileWorkspaceMessage message, CancellationToken token)
    {
        lock (_ownership)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeCallbacks++;
        }
        try { return await HandleAuthorizedAsync(message, token).ConfigureAwait(false); }
        finally
        {
            lock (_ownership)
            {
                _activeCallbacks--;
                if (_disposed && _activeCallbacks == 0) { DisposeKeys(); }
            }
        }
    }

    private async Task<FileWorkspaceReply> HandleAuthorizedAsync(FileWorkspaceMessage message, CancellationToken token)
    {
        var empty = new FileWorkspaceMessage(message.Kind);
        switch (message.Kind)
        {
            case FileWorkspaceMessageKind.ResolveSecret:
                if (message with { Secret = null } != empty || message.Secret is null || !AllowsSecret(message.Secret) || ++_secretResolutions > 16)
                {
                    return new(message.Kind, SecretFailure: SecretVaultErrorCode.AccessDenied);
                }
                var resolved = await vault.ResolveAsync(message.Secret, token).ConfigureAwait(false);
                if (resolved is SecretVaultResult<SecretMaterial>.Failure failure) { return new(message.Kind, SecretFailure: failure.Error.Code); }
                using (var material = ((SecretVaultResult<SecretMaterial>.Success)resolved).Value)
                {
                    var bytes = new byte[material.Length];
                    material.CopyTo(bytes);
                    return new(message.Kind, Bytes: bytes);
                }
            case FileWorkspaceMessageKind.VerifyHostKey:
                if (message with { HostKey = null } != empty || connection is null || message.HostKey is not { } key || ++_trustChecks > 8)
                {
                    throw new InvalidDataException("The file backend trust callback is not authorized.");
                }
                var verification = knownHosts.Verify(connection.Id, connection.HostKeyPolicy, new(key.Algorithm, key.PublicKeyBase64));
                _trusted = verification == SshHostKeyVerification.Trusted;
                return new(message.Kind, Verification: verification);
            case FileWorkspaceMessageKind.Identities:
                if (message != empty || connection?.Authentication is not
                    (ConnectionAuthentication.None or ConnectionAuthentication.SshAgent or ConnectionAuthentication.PrivateKey))
                {
                    throw new InvalidDataException("The file backend identity callback is not authorized.");
                }
                await LoadIdentitiesAsync(token).ConfigureAwait(false);
                return new(message.Kind, Identities: [.. _algorithms.Select((algorithm, index) =>
                    new FileWorkspaceIdentity(index, algorithm.Name, algorithm.Data))]);
            case FileWorkspaceMessageKind.Sign:
                if (message with { Bytes = null, Identity = 0 } != empty || !_trusted || message.Bytes is not { } payload
                    || message.Identity < 0 || message.Identity >= _algorithms.Count || ++_signatures > 16)
                {
                    throw new InvalidDataException("The file backend signing callback is not authorized.");
                }
                var algorithm = _algorithms[message.Identity];
                var username = (connection?.Endpoint as ConnectionEndpoint.Ssh)?.Username;
                ValidateAuthenticationPayload(payload, username, algorithm.Name, algorithm.Data);
                token.ThrowIfCancellationRequested();
                return new(message.Kind, Bytes: algorithm.Sign(payload));
            default:
                throw new InvalidDataException("The file backend credential callback is invalid.");
        }
    }

    internal bool AllowsSecret(ResolveSecretRequest request)
    {
        var reference = profile.Configuration switch
        {
            FileProviderConfiguration.S3 s3 => s3.CredentialsSecret,
            FileProviderConfiguration.Ftp ftp => ftp.PasswordSecret,
            FileProviderConfiguration.Smb smb => smb.PasswordSecret,
            FileProviderConfiguration.WebDav webDav => webDav.PasswordSecret,
            _ => null,
        };
        if (reference is { } value)
        {
            return request == new ResolveSecretRequest(value,
                new(SecretScopeKind.FileProvider, profile.Id.Value), new(SecretUseKind.FileProviderAuthentication, profile.Id.Value));
        }
        return profile.Configuration is FileProviderConfiguration.Sftp
            && connection?.Authentication is ConnectionAuthentication.Password password
            && request == ConnectionSecret(password.PasswordSecret);
    }

    private ResolveSecretRequest ConnectionSecret(SecretRef reference) => new(reference,
        new(SecretScopeKind.Connection, connection!.Id.Value), new(SecretUseKind.ConnectionAuthentication, connection.Id.Value));

    private async Task LoadIdentitiesAsync(CancellationToken token)
    {
        if (_identitiesLoaded) { return; }
        IPrivateKeySource[] identities;
        if (connection!.Authentication is ConnectionAuthentication.PrivateKey privateKey)
        {
            var bytes = await ReadPrivateKeySecretAsync(privateKey.PrivateKeySecret, token).ConfigureAwait(false);
            byte[]? passphraseBytes = null;
            try
            {
                if (privateKey.PassphraseSecret is { } passphraseReference)
                {
                    passphraseBytes = await ReadPrivateKeySecretAsync(passphraseReference, token).ConfigureAwait(false);
                }
                using var stream = new MemoryStream(bytes, writable: false);
                var key = passphraseBytes is null ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, Encoding.UTF8.GetString(passphraseBytes));
                _ownedKeys.Add(key);
                identities = [key];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                if (passphraseBytes is not null) { CryptographicOperations.ZeroMemory(passphraseBytes); }
            }
        }
        else
        {
            identities = await new SystemSshAuthenticationBridge(connection, connectionRuntime, new SystemSshAgentIdentitySource())
                .GetIdentitiesAsync(token).ConfigureAwait(false);
        }
        foreach (var identity in identities)
        {
            foreach (var algorithm in identity.HostKeyAlgorithms)
            {
                if (_algorithms.Count == 64 || algorithm.Data.Length > 64 * 1024 || algorithm.Name.Length > 128)
                {
                    throw new InvalidDataException("The file backend identity budget was exceeded.");
                }
                _algorithms.Add(algorithm);
            }
        }
        _identitiesLoaded = true;
    }

    private async Task<byte[]> ReadPrivateKeySecretAsync(SecretRef reference, CancellationToken token)
    {
        var result = await vault.ResolveAsync(ConnectionSecret(reference), token).ConfigureAwait(false);
        if (result is not SecretVaultResult<SecretMaterial>.Success success)
        {
            throw new IOException("The selected SSH credential could not be resolved.");
        }
        using var material = success.Value;
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        return bytes;
    }

    internal static void ValidateAuthenticationPayload(byte[] data, string? username, string algorithm, byte[] key)
    {
        if (data.Length > 64 * 1024 || string.IsNullOrWhiteSpace(username)) { throw new InvalidDataException("The SSH signing request is invalid."); }
        ReadOnlySpan<byte> remaining = data;
        var sessionId = ReadString(ref remaining);
        if (sessionId.Length is < 16 or > 128 || remaining.IsEmpty || remaining[0] != 50)
        {
            throw new InvalidDataException("Only an SSH user-authentication request can be signed.");
        }
        remaining = remaining[1..];
        if (!ReadString(ref remaining).SequenceEqual(Encoding.UTF8.GetBytes(username))
            || !ReadString(ref remaining).SequenceEqual("ssh-connection"u8)
            || !ReadString(ref remaining).SequenceEqual("publickey"u8)
            || remaining.IsEmpty || remaining[0] != 1)
        {
            throw new InvalidDataException("The SSH signing request does not match the selected connection.");
        }
        remaining = remaining[1..];
        if (!ReadString(ref remaining).SequenceEqual(Encoding.UTF8.GetBytes(algorithm))
            || !ReadString(ref remaining).SequenceEqual(key) || !remaining.IsEmpty)
        {
            throw new InvalidDataException("The SSH signing request does not match the selected key.");
        }
    }

    private static ReadOnlySpan<byte> ReadString(ref ReadOnlySpan<byte> remaining)
    {
        if (remaining.Length < 4) { throw new InvalidDataException("The SSH signing request is truncated."); }
        var count = BinaryPrimitives.ReadUInt32BigEndian(remaining);
        remaining = remaining[4..];
        if (count > remaining.Length) { throw new InvalidDataException("The SSH signing request is truncated."); }
        var value = remaining[..(int)count];
        remaining = remaining[(int)count..];
        return value;
    }

    public void Dispose()
    {
        lock (_ownership)
        {
            _disposed = true;
            if (_activeCallbacks == 0) { DisposeKeys(); }
        }
    }

    private void DisposeKeys()
    {
        foreach (var key in _ownedKeys) { key.Dispose(); }
        _ownedKeys.Clear();
        _algorithms.Clear();
    }
}
