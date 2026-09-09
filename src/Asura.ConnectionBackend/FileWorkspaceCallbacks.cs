using System.Security.Cryptography;
using Asura.Application;
using Asura.Core;
using Asura.Files;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Asura.ConnectionBackend;

/// <summary>
/// Guest-only callbacks over the same private control pipe. The host checks each
/// request against the selected immutable profile before consulting its vault or agent.
/// </summary>
internal sealed class FileWorkspaceCallbacks(Stream input, Stream output) : ISecretVault, ISshHostKeyTrustStore, ISshAgentIdentitySource,
    IProgress<FileTransferProgress>
{
    internal const int MaximumChunkBytes = 64 * 1024;
    private readonly object _exchangeLock = new();

    internal FileWorkspaceReply Exchange(FileWorkspaceMessage request)
    {
        lock (_exchangeLock)
        {
            BackendJsonFrames.Write(output, request, FileWorkspaceJsonContext.Default.FileWorkspaceMessage);
            var reply = BackendJsonFrames.Read(input, FileWorkspaceJsonContext.Default.FileWorkspaceReply);
            var empty = new FileWorkspaceReply(request.Kind);
            var valid = request.Kind switch
            {
                FileWorkspaceMessageKind.ResolveSecret => reply with { Bytes = null, SecretFailure = null } == empty
                    && (reply.Bytes is not null) != (reply.SecretFailure is not null)
                    && (reply.SecretFailure is null || Enum.IsDefined(reply.SecretFailure.Value)),
                FileWorkspaceMessageKind.VerifyHostKey => reply with { Verification = null } == empty
                    && reply.Verification is { } verification && Enum.IsDefined(verification),
                FileWorkspaceMessageKind.Identities => reply with { Identities = null } == empty && reply.Identities is not null,
                FileWorkspaceMessageKind.Sign or FileWorkspaceMessageKind.Upload => reply with { Bytes = null } == empty && reply.Bytes is not null,
                FileWorkspaceMessageKind.Download or FileWorkspaceMessageKind.Progress => reply == empty,
                _ => false,
            };
            if (!valid)
            {
                if (reply.Bytes is not null) { CryptographicOperations.ZeroMemory(reply.Bytes); }
                throw new InvalidDataException("The file backend callback response is invalid.");
            }
            return reply;
        }
    }

    public SecretVaultAvailability Availability { get; } = new(SecretVaultAvailabilityState.Available,
        SecretVaultPersistenceKind.None, SecretVaultCapabilities.Resolve, "backend-callback", "scoped_callback", "Scoped host credential resolution.");

    public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(ResolveSecretRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = Exchange(new(FileWorkspaceMessageKind.ResolveSecret, Secret: request));
        return ValueTask.FromResult(reply.SecretFailure is { } failure
            ? SecretVaultResult<SecretMaterial>.Fail(SecretVaultError.Create(failure))
            : SecretVaultResult<SecretMaterial>.Succeed(SecretMaterial.TakeOwnership(
                reply.Bytes ?? throw new InvalidDataException("The file backend credential response is missing."))));
    }

    public SshHostKeyVerification Verify(ConnectionId connectionId, SshHostKeyPolicy policy, SshHostKeyCandidate presented)
    {
        // Identity and policy are supplied by the host's captured profile, not by this
        // callback. The child cannot nominate another connection's trust-store entry.
        return Exchange(new(FileWorkspaceMessageKind.VerifyHostKey, HostKey: new(presented.Identity.Algorithm, presented.PublicKeyBase64))).Verification
            ?? throw new InvalidDataException("The file backend trust response is missing.");
    }

    public ValueTask<IPrivateKeySource[]> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identities = Exchange(new(FileWorkspaceMessageKind.Identities)).Identities
            ?? throw new InvalidDataException("The file backend identity response is missing.");
        if (identities.Length > 64) { throw new InvalidDataException("The file backend identity limit was exceeded."); }
        return ValueTask.FromResult<IPrivateKeySource[]>([.. identities.Select(identity =>
            (IPrivateKeySource)new DelegatedIdentity(new DelegatedAlgorithm(identity, this)))]);
    }

    public void Report(FileTransferProgress value) => _ = Exchange(new(FileWorkspaceMessageKind.Progress, Progress: value));

    public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(CreateSecretRequest request, SecretMaterial material, CancellationToken cancellationToken) => Denied<SecretMetadata>();
    public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(ReplaceSecretRequest request, SecretMaterial material, CancellationToken cancellationToken) => Denied<SecretMetadata>();
    public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(RelabelSecretRequest request, CancellationToken cancellationToken) => Denied<SecretMetadata>();
    public ValueTask<SecretVaultResult<Unit>> DeleteAsync(DeleteSecretRequest request, CancellationToken cancellationToken) => Denied<Unit>();
    public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(GetSecretMetadataRequest request, CancellationToken cancellationToken) => Denied<SecretMetadata>();
    public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(ListSecretMetadataRequest request, CancellationToken cancellationToken) => Denied<IReadOnlyList<SecretMetadata>>();
    public void Dispose() { }
    private static ValueTask<SecretVaultResult<T>> Denied<T>() =>
        ValueTask.FromResult(SecretVaultResult<T>.Fail(SecretVaultError.Create(SecretVaultErrorCode.AccessDenied)));

    private sealed class DelegatedIdentity(HostAlgorithm algorithm) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = [algorithm];
    }

    private sealed class DelegatedAlgorithm(FileWorkspaceIdentity identity, FileWorkspaceCallbacks callbacks) : HostAlgorithm(identity.Algorithm)
    {
        public override byte[] Data => identity.PublicKey;
        public override byte[] Sign(byte[] data) => callbacks.Exchange(new(FileWorkspaceMessageKind.Sign, Bytes: data, Identity: identity.Id)).Bytes
            ?? throw new InvalidDataException("The file backend signature response is missing.");
        public override bool VerifySignature(byte[] data, byte[] signature) =>
            throw new NotSupportedException("This delegated SSH authentication key only signs user-authentication requests.");
    }
}

/// <summary>Pulls upload bytes and acknowledges download chunks without buffering a complete file in either process.</summary>
internal sealed class FileWorkspaceTransferStream(FileWorkspaceCallbacks callbacks, bool upload) : Stream
{
    public override bool CanRead => upload;
    public override bool CanWrite => !upload;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (!upload) { throw new NotSupportedException(); }
        if (buffer.IsEmpty) { return 0; }
        var maximum = Math.Min(buffer.Length, FileWorkspaceCallbacks.MaximumChunkBytes);
        var bytes = callbacks.Exchange(new(FileWorkspaceMessageKind.Upload, Count: maximum)).Bytes
            ?? throw new InvalidDataException("The file upload response is missing.");
        try
        {
            if (bytes.Length > maximum) { throw new InvalidDataException("The file upload exceeded its requested chunk size."); }
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (upload) { throw new NotSupportedException(); }
        while (!buffer.IsEmpty)
        {
            var count = Math.Min(buffer.Length, FileWorkspaceCallbacks.MaximumChunkBytes);
            var bytes = buffer[..count].ToArray();
            try { _ = callbacks.Exchange(new(FileWorkspaceMessageKind.Download, Bytes: bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            buffer = buffer[count..];
        }
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}
