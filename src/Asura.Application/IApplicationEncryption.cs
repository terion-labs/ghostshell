namespace Asura.Application;

/// <summary>
/// Encryption at rest for everything the application itself writes: the
/// configuration database, persistent preview cache, and saved browser
/// sessions. The keys live in
/// the operating system's keystore and nowhere else — the files on disk are
/// unreadable without an unlocked OS account, and turning the setting off
/// decrypts them and deletes the keys.
///
/// This is protection for data at rest — a stolen disk image, a synced backup,
/// another local account. It is not a lock on the running application; that is
/// startup protection's job, built on top of this.
/// </summary>
public interface IApplicationEncryption
{
    /// <summary>
    /// Whether encryption can be offered at all: it needs the OS keystore,
    /// because a key stored beside the files it locks is a decoration.
    /// </summary>
    bool IsSupported { get; }

    bool IsEnabled { get; }

    /// <summary>
    /// True while the keys wait sealed behind the startup PIN: the database
    /// cannot open, and no authenticator except the PIN can change that.
    /// </summary>
    bool AwaitingUnlock { get; }

    /// <summary>Why <see cref="IsSupported"/> is false, when it is.</summary>
    string? UnsupportedReason { get; }

    event EventHandler? Changed;

    /// <summary>
    /// The password for persistent encrypted content containers, present
    /// exactly while encryption is enabled. Held in memory by this service;
    /// its durable copy is the keystore's.
    /// </summary>
    string? PersistentCachePassword { get; }

    /// <summary>
    /// Turns encryption on or off, converting what is already on disk either
    /// way. Returns null on success, else a sentence saying what refused.
    /// </summary>
    ValueTask<string?> SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}
