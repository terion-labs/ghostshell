using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed record QuickTerminalSettingsSaveRequest(
    QuickTerminalSettings Settings,
    long ExpectedRevision);

public sealed record QuickTerminalMonitorOption(
    QuickTerminalMonitorPolicy Policy,
    string DisplayName);

public sealed class QuickTerminalSettingsEditorViewModel : ObservableObject
{
    private string _hotkeyText;
    private QuickTerminalMonitorPolicy _monitorPolicy;
    private double _heightPercent;
    private double _opacityPercent;
    private bool _isTranslucent;
    private bool _animateSlide;
    private int _animationDurationMilliseconds;
    private bool _reduceMotion;
    private bool _restoreLastSession;
    private bool _restoreOnStart;
    private bool _hideOnFocusLoss;
    private string _registrationStatus = "Shortcut registration has not been checked yet.";
    private string _registrationStatusBrush = "#8B8B91";

    public QuickTerminalSettingsEditorViewModel(
        QuickTerminalSettings settings,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SettingsId = settings.Id;
        Name = settings.Name;
        ExpectedRevision = expectedRevision;
        _hotkeyText = QuickTerminalHotkeyText.Format(settings.Hotkey);
        _monitorPolicy = settings.MonitorPolicy;
        _heightPercent = Math.Round(
            settings.HeightFraction * 100,
            MidpointRounding.AwayFromZero);
        _opacityPercent = settings.Opacity * 100;
        _isTranslucent = settings.IsTranslucent;
        _animateSlide = settings.AnimateSlide;
        _animationDurationMilliseconds = settings.AnimationDurationMilliseconds;
        _reduceMotion = settings.ReduceMotion;
        _restoreLastSession = settings.RestoreLastSession;
        _restoreOnStart = settings.RestoreOnStart;
        _hideOnFocusLoss = settings.HideOnFocusLoss;
    }

    public QuickTerminalSettingsId SettingsId { get; }

    public string Name { get; }

    public long ExpectedRevision { get; }

    public IReadOnlyList<QuickTerminalMonitorOption> MonitorOptions { get; } =
    [
        new(QuickTerminalMonitorPolicy.ActiveWindow, "Active window"),
        new(QuickTerminalMonitorPolicy.MainWindow, "Asura window"),
        new(QuickTerminalMonitorPolicy.Primary, "Primary display"),
    ];

    public QuickTerminalMonitorOption? SelectedMonitorOption
    {
        get => MonitorOptions.Single(option => option.Policy == MonitorPolicy);
        set
        {
            if (value is not null)
            {
                MonitorPolicy = value.Policy;
            }
        }
    }

    public string HotkeyExample => QuickTerminalHotkeyText.Example;

    public string HotkeyHelpText =>
        $"Enter a modifier and key, for example {HotkeyExample}.";

    public string HotkeyText
    {
        get => _hotkeyText;
        set => SetProperty(ref _hotkeyText, value);
    }

    public QuickTerminalMonitorPolicy MonitorPolicy
    {
        get => _monitorPolicy;
        set
        {
            if (SetProperty(ref _monitorPolicy, value))
            {
                OnPropertyChanged(nameof(SelectedMonitorOption));
            }
        }
    }

    public double HeightPercent
    {
        get => _heightPercent;
        set => SetProperty(ref _heightPercent, value);
    }

    public double OpacityPercent
    {
        get => _opacityPercent;
        set => SetProperty(ref _opacityPercent, value);
    }

    public bool IsTranslucent
    {
        get => _isTranslucent;
        set => SetProperty(ref _isTranslucent, value);
    }

    public bool AnimateSlide
    {
        get => _animateSlide;
        set => SetProperty(ref _animateSlide, value);
    }

    public int AnimationDurationMilliseconds
    {
        get => _animationDurationMilliseconds;
        set => SetProperty(ref _animationDurationMilliseconds, value);
    }

    public bool ReduceMotion
    {
        get => _reduceMotion;
        set => SetProperty(ref _reduceMotion, value);
    }

    public bool RestoreLastSession
    {
        get => _restoreLastSession;
        set => SetProperty(ref _restoreLastSession, value);
    }

    public bool RestoreOnStart
    {
        get => _restoreOnStart;
        set => SetProperty(ref _restoreOnStart, value);
    }

    public bool HideOnFocusLoss
    {
        get => _hideOnFocusLoss;
        set => SetProperty(ref _hideOnFocusLoss, value);
    }

    public string RegistrationStatus
    {
        get => _registrationStatus;
        private set => SetProperty(ref _registrationStatus, value);
    }

    public string RegistrationStatusBrush
    {
        get => _registrationStatusBrush;
        private set => SetProperty(ref _registrationStatusBrush, value);
    }

    public QuickTerminalSettingsSaveRequest CreateSaveRequest()
    {
        var settings = new QuickTerminalSettings(
            SettingsId,
            Name,
            QuickTerminalHotkeyText.Parse(HotkeyText),
            MonitorPolicy,
            HeightPercent / 100,
            OpacityPercent / 100,
            AnimateSlide,
            AnimationDurationMilliseconds,
            ReduceMotion,
            RestoreLastSession,
            HideOnFocusLoss,
            IsTranslucent,
            RestoreOnStart);
        return new QuickTerminalSettingsSaveRequest(settings, ExpectedRevision);
    }

    public void ApplyRegistration(
        KeyStroke configuredGesture,
        KeyStroke? activeGesture,
        GlobalHotkeyRegistrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var configured = QuickTerminalHotkeyText.Format(configuredGesture);
        if (result is GlobalHotkeyRegistrationResult.Success)
        {
            RegistrationStatus = $"{configured} is registered globally.";
            RegistrationStatusBrush = "#3FB950";
            return;
        }

        var error = ((GlobalHotkeyRegistrationResult.Failure)result).Error;
        var fallback = activeGesture is null
            ? string.Empty
            : $" {QuickTerminalHotkeyText.Format(activeGesture.Value)} remains active.";
        RegistrationStatus = $"{error.Message}{fallback}";
        RegistrationStatusBrush = "#FFB224";
    }
}
