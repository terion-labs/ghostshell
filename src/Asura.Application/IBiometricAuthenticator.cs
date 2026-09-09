namespace Asura.Application;

/// <summary>
/// Asks the operating system to verify the person by biometrics — Touch ID
/// on a Mac. The OS draws its own prompt and answers yes or no; no secret
/// passes through here. That makes this an authenticator for the lock
/// screen's curtain, not a source of key material: keys wrapped under the
/// PIN still need the PIN.
/// </summary>
public interface IBiometricAuthenticator
{
    /// <summary>Whether the machine offers biometry to this process at all.</summary>
    bool IsAvailable { get; }

    /// <summary>What to call it on a button — "Touch ID".</summary>
    string MethodName { get; }

    /// <summary>
    /// Shows the OS prompt and answers whether the person passed. False for
    /// refusal, cancellation, or a machine that cannot ask.
    /// </summary>
    Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken);
}
