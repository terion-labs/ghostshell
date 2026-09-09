namespace Asura.Application;

/// <summary>
/// A PIN on the application window: asked for at startup and whenever the
/// lock timeout elapses. The PIN is never stored — a verifier derived from it
/// together with a random pepper held in the OS keystore is, so the file on
/// disk alone is not enough to test guesses against, and wrong guesses are
/// throttled with a persisted, doubling delay.
///
/// This gates the window, not the data: encryption at rest is
/// <see cref="IApplicationEncryption"/>'s job, and the explainer in settings
/// says so in as many words. Releasing the encryption keys themselves only
/// after this unlock is the planned next step, together with biometrics.
/// </summary>
public interface IStartupProtection
{
    bool IsEnabled { get; }

    /// <summary>Whether the window is locked right now.</summary>
    bool IsLocked { get; }

    /// <summary>Idle time after which the window locks; null means never.</summary>
    TimeSpan? LockTimeout { get; }

    /// <summary>Seconds a further unlock attempt must wait, zero when none.</summary>
    int RetryDelaySeconds { get; }

    event EventHandler? Changed;

    /// <summary>
    /// Turns protection on with this PIN. Null on success, else a sentence
    /// saying what refused.
    /// </summary>
    ValueTask<string?> EnableAsync(string pin, CancellationToken cancellationToken);

    /// <summary>Turns protection off; the current PIN authorizes it.</summary>
    ValueTask<string?> DisableAsync(string pin, CancellationToken cancellationToken);

    /// <summary>True and unlocked on the right PIN; false counts a miss.</summary>
    ValueTask<bool> TryUnlockAsync(string pin, CancellationToken cancellationToken);

    /// <summary>Locks now; a lock while disabled does nothing.</summary>
    void Lock();

    /// <summary>
    /// Unlocks without the PIN, for a caller that just verified the person by
    /// other means — the OS biometric prompt. In-process only by nature: the
    /// boundary this class defends is the keyboard, not the process.
    /// </summary>
    void UnlockAuthenticated();

    ValueTask SetLockTimeoutAsync(TimeSpan? timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Seals the app-encryption keys under the PIN typed just now — the step
    /// after turning encryption on while protection already stands, so the
    /// keystore copies can be forgotten. Verifies the PIN itself; a no-op
    /// when protection is off or there is nothing to seal. Null on success.
    /// </summary>
    ValueTask<string?> SealEncryptionKeysAsync(string pin, CancellationToken cancellationToken);
}
