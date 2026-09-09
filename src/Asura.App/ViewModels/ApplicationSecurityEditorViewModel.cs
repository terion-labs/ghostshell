using Asura.Application;
using Avalonia.Threading;

namespace Asura.App.ViewModels;

/// <summary>
/// The application-security controls on the Security &amp; secrets page, and
/// the lock screen's state. Flipping the encryption switch converts what is
/// on disk right then; enabling startup protection takes a PIN typed twice;
/// the lock screen asks for that PIN whenever the shell is locked — at
/// startup, on the idle timeout, or by hand.
/// </summary>
public sealed class ApplicationSecurityEditorViewModel : ObservableObject
{
    /// <summary>Timeout choices, in minutes; zero means never.</summary>
    public static IReadOnlyList<LockTimeoutOption> LockTimeoutOptions { get; } =
    [
        new("Never", 0),
        new("After 1 minute", 1),
        new("After 5 minutes", 5),
        new("After 15 minutes", 15),
        new("After 1 hour", 60),
    ];

    private readonly IApplicationEncryption? _encryption;
    private readonly IStartupProtection? _protection;
    private readonly IBiometricAuthenticator? _biometrics;
    private readonly DispatcherTimer? _idleTimer;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private bool _busy;
    private string? _statusDetail;
    private string _newPin = string.Empty;
    private string _newPinConfirmation = string.Empty;
    private string _currentPin = string.Empty;
    private string _unlockPin = string.Empty;
    private string? _protectionStatus;
    private string? _unlockStatus;

    public ApplicationSecurityEditorViewModel(
        IApplicationEncryption? encryption = null,
        IStartupProtection? protection = null,
        IBiometricAuthenticator? biometrics = null)
    {
        _encryption = encryption;
        _protection = protection;
        _biometrics = biometrics;
        _encryption?.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(IsEncryptionEnabled));
                OnPropertyChanged(nameof(CanUseBiometrics));
            };

        if (_protection is not null)
        {
            _protection.Changed += (_, _) => AnnounceProtectionState();
            if (Dispatcher.UIThread.CheckAccess())
            {
                // One slow tick; the deadline math decides, the timer merely
                // asks. Runs only where a UI thread exists — the view models
                // are also composed headless in tests.
                _idleTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(10),
                };
                _idleTimer.Tick += (_, _) => LockIfIdle();
                _idleTimer.Start();
            }
        }
    }

    // ---- Encryption ----

    public bool IsEncryptionSupported => _encryption?.IsSupported ?? false;

    public string EncryptionAvailability => _encryption switch
    {
        null => "This build has no application-encryption support.",
        { IsSupported: false } unsupported => unsupported.UnsupportedReason
            ?? "Application encryption is unavailable.",
        _ => "Keys live in the OS keystore; nothing key-like is ever written beside the data.",
    };

    public bool IsEncryptionEnabled
    {
        get => _encryption?.IsEnabled ?? false;
        set
        {
            if (_encryption is null || _busy || value == _encryption.IsEnabled)
            {
                return;
            }

            _busy = true;
            StatusDetail = value
                ? "Encrypting the configuration database…"
                : "Decrypting the configuration database…";
            _ = ApplyEncryptionAsync(value);
        }
    }

    public string? StatusDetail
    {
        get => _statusDetail;
        private set
        {
            if (SetProperty(ref _statusDetail, value))
            {
                OnPropertyChanged(nameof(HasStatusDetail));
            }
        }
    }

    public bool HasStatusDetail => !string.IsNullOrEmpty(_statusDetail);

    private async Task ApplyEncryptionAsync(bool enabled)
    {
        try
        {
            if (enabled && _protection is { IsEnabled: true }
                && string.IsNullOrEmpty(CurrentPin))
            {
                // The new keys are sealed under the PIN the moment they
                // exist; without it they would sit in the keystore, which is
                // exactly what protection promises they do not.
                StatusDetail = "Enter your current PIN under Protect startup first: "
                    + "the new keys are sealed under it.";
                return;
            }

            var refusal = await _encryption!.SetEnabledAsync(enabled, CancellationToken.None);
            StatusDetail = refusal;
            if (refusal is null && enabled && _protection is { IsEnabled: true })
            {
                StatusDetail = await _protection.SealEncryptionKeysAsync(
                    CurrentPin,
                    CancellationToken.None);
                if (StatusDetail is null)
                {
                    CurrentPin = string.Empty;
                }
            }
        }
        catch (Exception exception)
        {
            StatusDetail = $"Changing application encryption failed: {exception.Message}";
        }
        finally
        {
            _busy = false;
            // The switch shows the disk's true state, whatever just happened.
            OnPropertyChanged(nameof(IsEncryptionEnabled));
        }
    }

    // ---- Startup protection ----

    public bool IsProtectionAvailable => _protection is not null;

    public bool IsProtectionEnabled => _protection?.IsEnabled ?? false;

    public bool ShowProtectionSetup => IsProtectionAvailable && !IsProtectionEnabled;

    public bool ShowProtectionControls => IsProtectionEnabled;

    public string NewPin
    {
        get => _newPin;
        set => SetProperty(ref _newPin, value);
    }

    public string NewPinConfirmation
    {
        get => _newPinConfirmation;
        set => SetProperty(ref _newPinConfirmation, value);
    }

    public string CurrentPin
    {
        get => _currentPin;
        set => SetProperty(ref _currentPin, value);
    }

    public string? ProtectionStatus
    {
        get => _protectionStatus;
        private set
        {
            if (SetProperty(ref _protectionStatus, value))
            {
                OnPropertyChanged(nameof(HasProtectionStatus));
            }
        }
    }

    public bool HasProtectionStatus => !string.IsNullOrEmpty(_protectionStatus);

    public LockTimeoutOption SelectedLockTimeout
    {
        get
        {
            var minutes = (long)(_protection?.LockTimeout?.TotalMinutes ?? 0);
            return LockTimeoutOptions.FirstOrDefault(option => option.Minutes == minutes)
                ?? LockTimeoutOptions[0];
        }
        set
        {
            if (_protection is not null && value is not null)
            {
                _ = _protection.SetLockTimeoutAsync(
                    value.Minutes == 0 ? null : TimeSpan.FromMinutes(value.Minutes),
                    CancellationToken.None).AsTask();
            }
        }
    }

    public async Task EnableProtectionAsync()
    {
        if (_protection is null)
        {
            return;
        }

        if (!string.Equals(NewPin, NewPinConfirmation, StringComparison.Ordinal))
        {
            ProtectionStatus = "The two PIN entries do not match.";
            return;
        }

        ProtectionStatus = await _protection.EnableAsync(NewPin, CancellationToken.None);
        if (ProtectionStatus is null)
        {
            NewPin = string.Empty;
            NewPinConfirmation = string.Empty;
        }
    }

    public async Task DisableProtectionAsync()
    {
        if (_protection is null)
        {
            return;
        }

        ProtectionStatus = await _protection.DisableAsync(CurrentPin, CancellationToken.None);
        if (ProtectionStatus is null)
        {
            CurrentPin = string.Empty;
        }
    }

    // ---- The lock screen ----

    public bool IsLocked => _protection?.IsLocked ?? false;

    public string UnlockPin
    {
        get => _unlockPin;
        set => SetProperty(ref _unlockPin, value);
    }

    public string? UnlockStatus
    {
        get => _unlockStatus;
        private set
        {
            if (SetProperty(ref _unlockStatus, value))
            {
                OnPropertyChanged(nameof(HasUnlockStatus));
            }
        }
    }

    public bool HasUnlockStatus => !string.IsNullOrEmpty(_unlockStatus);

    public async Task TryUnlockAsync()
    {
        if (_protection is null)
        {
            return;
        }

        if (_protection.RetryDelaySeconds is > 0 and var wait)
        {
            UnlockStatus = $"Wait {wait}s before the next attempt.";
            return;
        }

        var unlocked = await _protection.TryUnlockAsync(UnlockPin, CancellationToken.None);
        UnlockPin = string.Empty;
        UnlockStatus = unlocked
            ? null
            : _protection.RetryDelaySeconds > 0
                ? $"That PIN was refused. Wait {_protection.RetryDelaySeconds}s before the next attempt."
                : "That PIN was refused.";
        if (unlocked)
        {
            NoteActivity();
        }
    }

    public void LockNow() => _protection?.Lock();

    public bool CanUseBiometrics =>
        _biometrics?.IsAvailable == true
        // While the keys wait sealed under the PIN, only the PIN is an
        // unlock; offering the sensor would offer a button that cannot work.
        && _encryption is not { AwaitingUnlock: true };

    public string BiometricUnlockLabel =>
        $"Unlock with {_biometrics?.MethodName ?? "biometrics"}";

    public async Task TryUnlockWithBiometricsAsync()
    {
        if (_protection is null || _biometrics is null)
        {
            return;
        }

        // The OS draws the prompt and verdicts; a pass lifts the curtain the
        // same way the PIN would, meter and all.
        if (await _biometrics.AuthenticateAsync("unlock Asura", CancellationToken.None))
        {
            _protection.UnlockAuthenticated();
            UnlockStatus = null;
            NoteActivity();
        }
    }

    /// <summary>
    /// Called by the window for any pointer or key input, so "idle" means
    /// what a person means by it.
    /// </summary>
    public void NoteActivity() => _lastActivity = DateTimeOffset.UtcNow;

    private void LockIfIdle()
    {
        if (_protection is { IsEnabled: true, IsLocked: false, LockTimeout: { } timeout }
            && DateTimeOffset.UtcNow - _lastActivity >= timeout)
        {
            _protection.Lock();
        }
    }

    private void AnnounceProtectionState()
    {
        OnPropertyChanged(nameof(IsProtectionEnabled));
        OnPropertyChanged(nameof(ShowProtectionSetup));
        OnPropertyChanged(nameof(ShowProtectionControls));
        OnPropertyChanged(nameof(SelectedLockTimeout));
        OnPropertyChanged(nameof(IsLocked));
    }

    public sealed record LockTimeoutOption(string Label, long Minutes)
    {
        public override string ToString() => Label;
    }
}
