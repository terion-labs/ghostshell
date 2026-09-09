namespace Asura.Application;

/// <summary>A security key enrolled for this profile.</summary>
/// <param name="CredentialId">
/// What the key gave back at enrollment. Not a secret — it names the
/// credential so the key knows which one to use, and it is useless without
/// the key that holds the matching private material.
/// </param>
/// <param name="Salt">
/// The 32 bytes handed to the key on every derivation. Also not a secret:
/// the key mixes it with material only the hardware holds, so the same salt
/// on a different key yields a different secret.
/// </param>
public sealed record SecurityKeyEnrollment(byte[] CredentialId, byte[] Salt);

/// <summary>
/// A FIDO2 security key used as a source of key material, not as a gate.
///
/// The distinction matters. A key that merely signs a challenge proves
/// someone touched it, but yields nothing to encrypt with — the data keys
/// would still have to sit somewhere the application can read them, which
/// is what the OS keystore already does. The <c>hmac-secret</c> extension
/// instead derives a stable 32-byte secret from a salt, and that secret can
/// wrap the keys: without the hardware there is nothing to unwrap with.
///
/// Every call here blocks on a human touching the key, so callers must
/// treat these as long-running and cancellable.
/// </summary>
public interface ISecurityKeyAuthenticator
{
    /// <summary>Whether this build can talk to security keys at all.</summary>
    bool IsSupported { get; }

    /// <summary>Whether a key is plugged in right now.</summary>
    ValueTask<bool> IsKeyPresentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates a credential on the attached key. The returned enrollment is
    /// safe to store beside the encrypted data; the secret it later derives
    /// is not, and never leaves memory. Null when no key answered — the
    /// reason is in <paramref name="failure"/>.
    /// </summary>
    ValueTask<(SecurityKeyEnrollment? Enrollment, string? Failure)> EnrollAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks the key for this enrollment's secret. The same key and salt give
    /// the same 32 bytes every time; a different key gives different bytes,
    /// which is exactly why it can hold a wrapping key. Null when the key
    /// refused or was absent.
    /// </summary>
    ValueTask<(byte[]? Secret, string? Failure)> DeriveSecretAsync(
        SecurityKeyEnrollment enrollment,
        CancellationToken cancellationToken);
}
