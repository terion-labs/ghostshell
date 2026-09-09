using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Startup protection over a peppered PIN verifier.
///
/// What is on disk is a salt, an iteration count and a PBKDF2 verifier —
/// derived from the PIN <em>and</em> a random pepper that exists only in the
/// OS keystore. Testing guesses therefore needs both the file and the
/// keystore; the file alone, lifted from a backup, verifies nothing. Wrong
/// guesses are counted in the same file, and past a small allowance each
/// further attempt waits twice as long as the one before — a restart does
/// not reset the meter, because the meter is on disk.
/// </summary>
public sealed partial class StartupProtectionRuntime : IStartupProtection
{
    private const string FileName = "startup-protection.json";
    private const int Iterations = 600_000;
    private const int FreeAttempts = 5;

    /// <summary>
    /// One PBKDF2 run yields both halves: the stored verifier and the key
    /// that seals the app-encryption keys. The halves of a PRF output are
    /// independent, so publishing the first says nothing about the second.
    /// </summary>
    private const int DerivedBytes = 64;
    private const int VerifierBytes = 32;

    private static readonly SecretRef PepperReference = new("app.security.startup-pepper");

    private static readonly SecretUsePurpose Purpose = new(
        SecretUseKind.PlatformMaintenance,
        SecretUsePurpose.GlobalTargetId);

    private readonly object _gate = new();
    private readonly ISecretVault _vault;
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private ProtectionFile? _state;
    private bool _locked;

    private readonly ApplicationEncryptionRuntime? _encryption;

    public StartupProtectionRuntime(
        ISecretVault vault,
        string dataDirectory,
        TimeProvider? timeProvider = null,
        ApplicationEncryptionRuntime? encryption = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(Path.GetFullPath(dataDirectory), FileName);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _encryption = encryption;
        _state = Read();
        _locked = _state is not null;
        _encryption?.Changed += OnEncryptionChanged;
    }

    /// <summary>
    /// Whether the app-encryption keys live sealed under the PIN rather than
    /// in the keystore. Read at startup, before the database can open.
    /// </summary>
    public bool HoldsWrappedKeys => _state?.WrappedKeys is not null;

    private void OnEncryptionChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            // Encryption turned off makes a sealed blob meaningless; holding
            // dead ciphertext would only confuse the next startup.
            if (_encryption is { IsEnabled: false }
                && _state is { WrappedKeys: not null } state)
            {
                _state = state with { WrappedKeys = null };
                Write(_state);
            }
        }
    }

    public bool IsEnabled => _state is not null;

    public bool IsLocked
    {
        get
        {
            lock (_gate)
            {
                return _locked;
            }
        }
    }

    public TimeSpan? LockTimeout => _state?.LockTimeoutSeconds is > 0
        ? TimeSpan.FromSeconds(_state.LockTimeoutSeconds.Value)
        : null;

    public int RetryDelaySeconds
    {
        get
        {
            lock (_gate)
            {
                if (_state?.RetryAfterUtc is not { } retryAfter)
                {
                    return 0;
                }

                var wait = retryAfter - _timeProvider.GetUtcNow();
                return wait > TimeSpan.Zero ? (int)Math.Ceiling(wait.TotalSeconds) : 0;
            }
        }
    }

    public event EventHandler? Changed;

    public async ValueTask<string?> EnableAsync(
        string pin,
        CancellationToken cancellationToken)
    {
        if (Requirement(pin) is { } refused)
        {
            return refused;
        }

        if (!_vault.Availability.CanPersist)
        {
            return "Startup protection needs the operating system's keystore, which is "
                + "unavailable: " + _vault.Availability.Message;
        }

        var pepper = await ResolveOrCreatePepperAsync(cancellationToken).ConfigureAwait(false);
        if (pepper is null)
        {
            return "The OS keystore refused to store the protection pepper.";
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var derived = Derive(pin, salt, pepper);
        var file = new ProtectionFile(
            Version: 2,
            Salt: Convert.ToHexStringLower(salt),
            Iterations,
            Verifier: Convert.ToHexStringLower(derived[..VerifierBytes]),
            LockTimeoutSeconds: _state?.LockTimeoutSeconds,
            FailedAttempts: 0,
            RetryAfterUtc: null,
            WrappedKeys: SealKeys(derived[VerifierBytes..]));
        lock (_gate)
        {
            _state = file;
            Write(file);
            // Enabling is itself an authenticated moment; the session stays
            // unlocked until the timeout or an explicit lock.
            _locked = false;
        }

        if (file.WrappedKeys is not null)
        {
            // Keep the sealed copy even if deletion partially fails: otherwise
            // a deleted key could become unrecoverable on the next startup.
            var deletion = await _encryption!.ForgetKeystoreCopiesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (deletion is SecretVaultResult<Unit>.Failure)
            {
                Changed?.Invoke(this, EventArgs.Empty);
                return "The PIN was saved, but the OS keystore could not remove all unsealed encryption-key copies. "
                    + "PIN-only key protection is incomplete; retry saving the PIN when the keystore is available.";
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// Seals the app-encryption keys under a PIN typed just now — the path
    /// for turning encryption on while protection already stands. Verifies
    /// the PIN itself: sealing under an unverified string would brick the
    /// next startup.
    /// </summary>
    public async ValueTask<string?> SealEncryptionKeysAsync(
        string pin,
        CancellationToken cancellationToken)
    {
        var state = _state;
        if (state is null || _encryption?.ExportKeys() is null)
        {
            return null;
        }

        if (state.Version < 2)
        {
            return "This PIN was set before sealed keys existed; turn protection "
                + "off and on again to refresh it.";
        }

        var pepper = await ResolvePepperAsync(cancellationToken).ConfigureAwait(false);
        if (pepper is null)
        {
            return "The OS keystore did not release the protection pepper.";
        }

        var derived = Derive(pin, Convert.FromHexString(state.Salt), pepper);
        if (!CryptographicOperations.FixedTimeEquals(
                derived.AsSpan(0, VerifierBytes),
                Convert.FromHexString(state.Verifier)))
        {
            return "That PIN was refused.";
        }

        lock (_gate)
        {
            _state = state with { WrappedKeys = SealKeys(derived[VerifierBytes..]) };
            Write(_state);
        }

        var deletion = await _encryption.ForgetKeystoreCopiesAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return deletion is SecretVaultResult<Unit>.Failure
            ? "The encryption keys were sealed, but the OS keystore could not remove all unsealed copies. "
                + "PIN-only key protection is incomplete; retry when the keystore is available."
            : null;
    }

    /// <summary>AES-GCM over "config\ncache", or null with nothing to seal.</summary>
    private string? SealKeys(byte[] wrappingKey)
    {
        if (_encryption?.ExportKeys() is not { } keys)
        {
            return null;
        }

        var plaintext = Encoding.UTF8.GetBytes($"{keys.Config}\n{keys.Cache}");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var cipher = new AesGcm(wrappingKey, tag.Length);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag);
        return Convert.ToHexStringLower(nonce)
            + ":" + Convert.ToHexStringLower(ciphertext)
            + ":" + Convert.ToHexStringLower(tag);
    }

    private static (string Config, string Cache)? UnsealKeys(
        string wrapped,
        byte[] wrappingKey)
    {
        try
        {
            var parts = wrapped.Split(':');
            var nonce = Convert.FromHexString(parts[0]);
            var ciphertext = Convert.FromHexString(parts[1]);
            var tag = Convert.FromHexString(parts[2]);
            var plaintext = new byte[ciphertext.Length];
            using var cipher = new AesGcm(wrappingKey, tag.Length);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext);
            var keys = Encoding.UTF8.GetString(plaintext).Split('\n');
            return (keys[0], keys[1]);
        }
        catch (Exception exception)
            when (exception is System.Security.Cryptography.AuthenticationTagMismatchException
                or FormatException
                or IndexOutOfRangeException)
        {
            // A blob the right PIN cannot open is corrupt, not mistyped; the
            // caller reports it rather than counting a miss.
            return null;
        }
    }

    public async ValueTask<string?> DisableAsync(
        string pin,
        CancellationToken cancellationToken)
    {
        if (_state is null)
        {
            return null;
        }

        if (!await TryUnlockAsync(pin, cancellationToken).ConfigureAwait(false))
        {
            return RetryDelaySeconds > 0
                ? $"That PIN was refused; the next attempt can be made in {RetryDelaySeconds}s."
                : "That PIN was refused.";
        }

        if (_state?.WrappedKeys is not null && _encryption?.ExportKeys() is { } keys)
        {
            // The sealed blob dies with the gate, so the keystore becomes the
            // keys' home again — and the disable is refused rather than let
            // the only durable copy vanish.
            if (!await _encryption.RestoreKeystoreCopiesAsync(
                        keys.Config,
                        keys.Cache,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return "The OS keystore refused to take the encryption keys back; "
                    + "protection stays on.";
            }
        }

        lock (_gate)
        {
            _state = null;
            _locked = false;
            try
            {
                File.Delete(_path);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        await _vault.DeleteAsync(
                new DeleteSecretRequest(PepperReference, SecretScope.Global, Purpose),
                cancellationToken)
            .ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public async ValueTask<bool> TryUnlockAsync(
        string pin,
        CancellationToken cancellationToken)
    {
        var state = _state;
        if (state is null)
        {
            return true;
        }

        lock (_gate)
        {
            if (state.RetryAfterUtc is { } retryAfter
                && retryAfter > _timeProvider.GetUtcNow())
            {
                return false;
            }
        }

        var pepper = await ResolvePepperAsync(cancellationToken).ConfigureAwait(false);
        if (pepper is null)
        {
            // Without the pepper nothing can verify; refusing every PIN would
            // lock the user out of their own machine's data for want of a
            // keystore glitch. The pepper is the brute-force defense, not the
            // authentication itself.
            return false;
        }

        var offered = Derive(pin ?? string.Empty, Convert.FromHexString(state.Salt), pepper);
        var expected = Convert.FromHexString(state.Verifier);
        var matches = CryptographicOperations.FixedTimeEquals(
            offered.AsSpan(0, expected.Length),
            expected);
        if (matches
            && state.WrappedKeys is { } wrapped
            && _encryption is not null
            && UnsealKeys(wrapped, offered[VerifierBytes..]) is { } keys)
        {
            // The PIN is the keys' release: this is the moment the encrypted
            // configuration database becomes openable.
            _encryption.AcceptUnwrappedKeys(keys.Config, keys.Cache);
        }
        lock (_gate)
        {
            if (matches)
            {
                _locked = false;
                if (state.FailedAttempts != 0 || state.RetryAfterUtc is not null)
                {
                    _state = state with { FailedAttempts = 0, RetryAfterUtc = null };
                    Write(_state);
                }
            }
            else
            {
                var attempts = state.FailedAttempts + 1;
                // 30s after the free allowance, doubling per miss, capped at
                // an hour: enough to make guessing hopeless, never enough to
                // lock the owner out for good.
                var delay = attempts <= FreeAttempts
                    ? (TimeSpan?)null
                    : TimeSpan.FromSeconds(Math.Min(
                        3600,
                        30 * Math.Pow(2, Math.Min(attempts - FreeAttempts - 1, 7))));
                _state = state with
                {
                    FailedAttempts = attempts,
                    RetryAfterUtc = delay is null
                        ? null
                        : _timeProvider.GetUtcNow() + delay,
                };
                Write(_state);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return matches;
    }

    public void UnlockAuthenticated()
    {
        // A sensor verdict cannot derive the wrapping key: while the
        // encryption keys wait sealed, only the PIN opens anything, and
        // lifting the curtain without them would start a profile that
        // cannot read its own database.
        if (_encryption is { AwaitingUnlock: true })
        {
            return;
        }

        lock (_gate)
        {
            if (_state is null || !_locked)
            {
                return;
            }

            _locked = false;
            if (_state.FailedAttempts != 0 || _state.RetryAfterUtc is not null)
            {
                // The person is verified; the miss meter has nothing left to
                // defend against.
                _state = _state with { FailedAttempts = 0, RetryAfterUtc = null };
                Write(_state);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Lock()
    {
        lock (_gate)
        {
            if (_state is null || _locked)
            {
                return;
            }

            _locked = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask SetLockTimeoutAsync(
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state is null)
            {
                return ValueTask.CompletedTask;
            }

            _state = _state with
            {
                LockTimeoutSeconds = timeout is { } value && value > TimeSpan.Zero
                    ? (long)value.TotalSeconds
                    : null,
            };
            Write(_state);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    private static string? Requirement(string pin) =>
        string.IsNullOrEmpty(pin) || pin.Trim().Length < 4
            ? "A PIN needs at least four characters."
            : null;

    private static byte[] Derive(string pin, byte[] salt, byte[] pepper)
    {
        // The pepper joins the salt rather than the PIN so a caller can never
        // weaken it by formatting; both are byte-appended, no encoding games.
        var seasoned = new byte[salt.Length + pepper.Length];
        salt.CopyTo(seasoned, 0);
        pepper.CopyTo(seasoned, salt.Length);
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin),
            seasoned,
            Iterations,
            HashAlgorithmName.SHA512,
            DerivedBytes);
    }

    private async ValueTask<byte[]?> ResolvePepperAsync(CancellationToken cancellationToken)
    {
        var resolved = await _vault.ResolveAsync(
                new ResolveSecretRequest(PepperReference, SecretScope.Global, Purpose),
                cancellationToken)
            .ConfigureAwait(false);
        if (resolved is not SecretVaultResult<SecretMaterial>.Success success)
        {
            return null;
        }

        using var material = success.Value;
        var pepper = new byte[material.Length];
        material.CopyTo(pepper);
        return pepper;
    }

    private async ValueTask<byte[]?> ResolveOrCreatePepperAsync(
        CancellationToken cancellationToken)
    {
        if (await ResolvePepperAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var pepper = RandomNumberGenerator.GetBytes(32);
        using var material = SecretMaterial.CopyFrom(pepper);
        var created = await _vault.CreateAsync(
                new CreateSecretRequest(
                    PepperReference,
                    "Startup protection pepper",
                    SecretKind.Other,
                    SecretScope.Global,
                    Purpose),
                material,
                cancellationToken)
            .ConfigureAwait(false);
        return created is SecretVaultResult<SecretMetadata>.Success ? pepper : null;
    }

    private ProtectionFile? Read()
    {
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(_path),
                StartupProtectionJsonContext.Default.ProtectionFile);
        }
        catch (Exception exception) when (exception
            is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }

    private void Write(ProtectionFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(
                _path,
                JsonSerializer.Serialize(
                    file,
                    StartupProtectionJsonContext.Default.ProtectionFile));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            // The attempt meter degrades to per-run; the verifier itself was
            // already read, so unlocking still works.
        }
    }

    /// <summary>
    /// Everything on disk. No secrets: the pepper never joins it, and
    /// <paramref name="WrappedKeys"/> is AES-GCM ciphertext under a key that
    /// only the PIN and the pepper together can derive.
    /// </summary>
    private sealed record ProtectionFile(
        int Version,
        string Salt,
        int Iterations,
        string Verifier,
        long? LockTimeoutSeconds,
        int FailedAttempts,
        DateTimeOffset? RetryAfterUtc,
        string? WrappedKeys = null);

    [JsonSerializable(typeof(ProtectionFile))]
    private sealed partial class StartupProtectionJsonContext : JsonSerializerContext;
}
