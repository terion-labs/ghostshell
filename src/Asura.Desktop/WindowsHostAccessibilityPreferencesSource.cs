#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.UI.ViewManagement;

namespace Asura.Desktop;

[SupportedOSPlatform("windows10.0.19041")]
internal sealed class WindowsHostAccessibilityPreferencesSource :
    HostAccessibilityPreferencesSource
{
    private UISettings? _settings;

    protected override void StartCore()
    {
        try
        {
            var settings = new UISettings();
            settings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
            settings.AdvancedEffectsEnabledChanged += OnAdvancedEffectsEnabledChanged;
            settings.TextScaleFactorChanged += OnTextScaleFactorChanged;
            _settings = settings;
            Refresh(settings);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            Unsubscribe();
        }
    }

    protected override void DisposeCore() => Unsubscribe();

    private void OnAnimationsEnabledChanged(
        UISettings sender,
        UISettingsAnimationsEnabledChangedEventArgs arguments)
    {
        _ = arguments;
        Refresh(sender);
    }

    private void OnAdvancedEffectsEnabledChanged(UISettings sender, object arguments)
    {
        _ = arguments;
        Refresh(sender);
    }

    private void OnTextScaleFactorChanged(UISettings sender, object arguments)
    {
        _ = arguments;
        Refresh(sender);
    }

    private void Refresh(UISettings settings)
    {
        try
        {
            Publish(HostAccessibilityPreferenceMapping.FromWindows(
                settings.AnimationsEnabled,
                settings.AdvancedEffectsEnabled,
                settings.TextScaleFactor));
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            // A transient WinRT failure leaves the last valid snapshot in place.
        }
    }

    private void Unsubscribe()
    {
        var settings = Interlocked.Exchange(ref _settings, null);
        if (settings is null)
        {
            return;
        }

        try
        {
            settings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            settings.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsEnabledChanged;
            settings.TextScaleFactorChanged -= OnTextScaleFactorChanged;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            // The OS has already torn down the event source.
        }
    }

    private static bool IsUnavailable(Exception exception) => exception is
        COMException
        or InvalidCastException
        or InvalidOperationException
        or TypeInitializationException
        or TypeLoadException
        or DllNotFoundException
        or EntryPointNotFoundException;
}
#endif
