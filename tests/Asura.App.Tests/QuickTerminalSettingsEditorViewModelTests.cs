using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class QuickTerminalSettingsEditorViewModelTests
{
    [Fact]
    public void Save_request_parses_hotkey_and_percent_fields()
    {
        var editor = new QuickTerminalSettingsEditorViewModel(
            QuickTerminalSettings.Default,
            expectedRevision: 7)
        {
            HotkeyText = "Control + Option + K",
            MonitorPolicy = QuickTerminalMonitorPolicy.ActiveWindow,
            HeightPercent = 40,
            OpacityPercent = 40,
            IsTranslucent = false,
            AnimateSlide = false,
            AnimationDurationMilliseconds = 90,
            ReduceMotion = true,
            RestoreLastSession = false,
            RestoreOnStart = false,
            HideOnFocusLoss = false,
        };

        var request = editor.CreateSaveRequest();

        Assert.Equal(7, request.ExpectedRevision);
        Assert.Equal(
            new KeyStroke("K", KeyModifiers.Control | KeyModifiers.Alt),
            request.Settings.Hotkey);
        Assert.Equal(QuickTerminalMonitorPolicy.ActiveWindow, request.Settings.MonitorPolicy);
        Assert.Equal(0.4, request.Settings.HeightFraction);
        Assert.Equal(0.4, request.Settings.Opacity);
        Assert.False(request.Settings.RestoreLastSession);
        Assert.False(request.Settings.RestoreOnStart);
        Assert.False(request.Settings.HideOnFocusLoss);
    }

    [Fact]
    public void Height_percentage_is_presented_as_a_whole_number()
    {
        var defaults = QuickTerminalSettings.Default;
        var settings = new QuickTerminalSettings(
            defaults.Id,
            defaults.Name,
            defaults.Hotkey,
            defaults.MonitorPolicy,
            heightFraction: 0.404981549815498,
            defaults.Opacity,
            defaults.AnimateSlide,
            defaults.AnimationDurationMilliseconds,
            defaults.ReduceMotion,
            defaults.RestoreLastSession,
            defaults.HideOnFocusLoss,
            defaults.IsTranslucent,
            defaults.RestoreOnStart);

        var editor = new QuickTerminalSettingsEditorViewModel(settings, expectedRevision: 1);

        Assert.Equal(40, editor.HeightPercent);
    }

    [Fact]
    public void Monitor_options_explain_each_distinct_display_policy()
    {
        var editor = new QuickTerminalSettingsEditorViewModel(
            QuickTerminalSettings.Default,
            expectedRevision: 1);

        Assert.Equal(
            new[]
            {
                (QuickTerminalMonitorPolicy.ActiveWindow, "Active window"),
                (QuickTerminalMonitorPolicy.MainWindow, "Asura window"),
                (QuickTerminalMonitorPolicy.Primary, "Primary display"),
            },
            editor.MonitorOptions
                .Select(option => (option.Policy, option.DisplayName))
                .ToArray());

        editor.SelectedMonitorOption = editor.MonitorOptions[0];

        Assert.Equal(QuickTerminalMonitorPolicy.ActiveWindow, editor.MonitorPolicy);
        Assert.Equal(
            QuickTerminalMonitorPolicy.ActiveWindow,
            editor.CreateSaveRequest().Settings.MonitorPolicy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(40)]
    public void Save_request_preserves_low_opacity_percentages(double opacityPercent)
    {
        var editor = new QuickTerminalSettingsEditorViewModel(
            QuickTerminalSettings.Default,
            expectedRevision: 1)
        {
            OpacityPercent = opacityPercent,
        };

        var request = editor.CreateSaveRequest();

        Assert.Equal(opacityPercent / 100, request.Settings.Opacity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GRAVE")]
    [InlineData("Command + K + L")]
    public void Invalid_hotkey_text_is_rejected_before_persistence(string text)
    {
        var editor = new QuickTerminalSettingsEditorViewModel(
            QuickTerminalSettings.Default,
            expectedRevision: 1)
        {
            HotkeyText = text,
        };

        var exception = Record.Exception(() => editor.CreateSaveRequest());
        Assert.True(exception is ArgumentException or FormatException);
    }

    [Fact]
    public void Conflict_status_reports_the_active_fallback()
    {
        var editor = new QuickTerminalSettingsEditorViewModel(
            QuickTerminalSettings.Default,
            expectedRevision: 1);
        var configured = new KeyStroke("K", KeyModifiers.Meta);
        var fallback = QuickTerminalSettings.Default.Hotkey;
        var failure = new GlobalHotkeyRegistrationResult.Failure(new(
            GlobalHotkeyRegistrationErrorCode.Conflict,
            "global_hotkey_conflict",
            "Another application owns the shortcut."));

        editor.ApplyRegistration(configured, fallback, failure);

        Assert.Contains("Another application", editor.RegistrationStatus);
        Assert.Contains(
            $"{QuickTerminalHotkeyText.Example} remains active",
            editor.RegistrationStatus);
        Assert.Equal("#FFB224", editor.RegistrationStatusBrush);
    }

    [Theory]
    [InlineData((int)ShortcutDisplayPlatform.MacOS, "Control + Option + Shift + Command + K")]
    [InlineData((int)ShortcutDisplayPlatform.Windows, "Ctrl + Alt + Shift + Win + K")]
    [InlineData((int)ShortcutDisplayPlatform.Linux, "Ctrl + Alt + Shift + Super + K")]
    public void Shortcut_format_uses_host_conventions(
        int platform,
        string expected)
    {
        var stroke = new KeyStroke(
            "K",
            KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);

        Assert.Equal(
            expected,
            QuickTerminalHotkeyText.Format(stroke, (ShortcutDisplayPlatform)platform));
    }

    [Theory]
    [InlineData((int)ShortcutDisplayPlatform.MacOS, "⌘ K")]
    [InlineData((int)ShortcutDisplayPlatform.Windows, "Ctrl+K")]
    [InlineData((int)ShortcutDisplayPlatform.Linux, "Ctrl+K")]
    public void Application_shortcut_format_uses_host_conventions(
        int platform,
        string expected)
    {
        Assert.Equal(
            expected,
            QuickTerminalHotkeyText.FormatApplicationCommand(
                "K",
                (ShortcutDisplayPlatform)platform));
    }
}
