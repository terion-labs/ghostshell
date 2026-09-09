using System.Security.Cryptography;

namespace Asura.Application;

/// <summary>
/// Validated, non-secret SSH public-key material at a transport/trust-store boundary. The
/// fingerprint is always derived from the canonical decoded bytes so callers cannot pair a key
/// with an unrelated display identity.
/// </summary>
public sealed record SshHostKeyCandidate
{
    public SshHostKeyCandidate(string algorithm, string publicKeyBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyBase64);
        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "The SSH public key is not valid Base64.",
                nameof(publicKeyBase64),
                exception);
        }

        if (keyBytes.Length is < 16 or > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(publicKeyBase64));
        }

        try
        {
            Identity = new SshHostKeyIdentity(algorithm, Fingerprint(keyBytes));
            PublicKeyBase64 = Convert.ToBase64String(keyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    public SshHostKeyIdentity Identity { get; }

    public string PublicKeyBase64 { get; }

    private static string Fingerprint(ReadOnlySpan<byte> publicKey)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(publicKey, digest);
        return $"SHA256:{Convert.ToBase64String(digest).TrimEnd('=')}";
    }
}
