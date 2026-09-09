using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace Asura.Desktop;

internal sealed class LinuxHostAccessibilityPreferencesSource :
    HostAccessibilityPreferencesSource
{
    internal const string AppearanceNamespace = "org.freedesktop.appearance";
    internal const string ReducedMotionKey = "reduced-motion";
    internal const string GnomeInterfaceNamespace = "org.gnome.desktop.interface";
    internal const string GnomeAnimationsKey = "enable-animations";
    internal const string GnomeTextScaleKey = "text-scaling-factor";

    private readonly object _lifetimeGate = new();
    private readonly ILinuxPortalSettingsClient _client;
    private readonly bool _readGnomePreferences;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private IDisposable? _subscription;
    private bool _stopped;

    public LinuxHostAccessibilityPreferencesSource()
        : this(
            new LinuxPortalSettingsClient(),
            IsGnomeDesktop(
                Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
                Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"),
                Environment.GetEnvironmentVariable("DESKTOP_SESSION")))
    {
    }

    internal LinuxHostAccessibilityPreferencesSource(
        ILinuxPortalSettingsClient client,
        bool readGnomePreferences)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _readGnomePreferences = readGnomePreferences;
    }

    internal Task Initialization { get; private set; } = Task.CompletedTask;

    protected override void StartCore() => Initialization = InitializeAsync();

    protected override void DisposeCore()
    {
        IDisposable? subscription;
        lock (_lifetimeGate)
        {
            _stopped = true;
            subscription = _subscription;
            _subscription = null;
        }

        subscription?.Dispose();
        _client.Dispose();
    }

    internal static bool IsGnomeDesktop(params string?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Any(value =>
            value?.Contains("GNOME", StringComparison.OrdinalIgnoreCase) == true);
    }

    internal static bool? ParseReducedMotion(VariantValue value) =>
        TryRead(value, static item => item.GetUInt32()) switch
        {
            0 => false,
            1 => true,
            _ => null,
        };

    internal static bool? ParseAnimationsEnabled(VariantValue value) =>
        TryRead(value, static item => item.GetBool());

    internal static double? ParseTextScale(VariantValue value) =>
        TryRead(value, static item => item.GetDouble()) switch
        {
            > 0 and var scale when double.IsFinite(scale) => scale,
            _ => null,
        };

    private async Task InitializeAsync()
    {
        try
        {
            var subscription = await _client.WatchSettingChangedAsync(OnSettingChanged);
            lock (_lifetimeGate)
            {
                if (_stopped)
                {
                    subscription.Dispose();
                    return;
                }

                _subscription = subscription;
            }

            await RefreshAsync();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            // Portals are optional and may be absent on minimal Linux sessions.
        }
    }

    private void OnSettingChanged(Exception? exception, LinuxPortalSettingChanged setting)
    {
        if (exception is not null || !IsRelevant(setting))
        {
            return;
        }

        _ = RefreshAfterSignalAsync();
    }

    private async Task RefreshAfterSignalAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            // Preserve the last valid snapshot until a later host signal succeeds.
        }
    }

    private async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            if (IsDisposed)
            {
                return;
            }

            var version = await _client.GetVersionAsync();
            var reducedMotion = version >= 2
                ? await TryReadSettingAsync(
                    AppearanceNamespace,
                    ReducedMotionKey,
                    version,
                    ParseReducedMotion)
                : null;
            double? textScale = null;

            if (_readGnomePreferences)
            {
                if (reducedMotion is null)
                {
                    var animationsEnabled = await TryReadSettingAsync(
                        GnomeInterfaceNamespace,
                        GnomeAnimationsKey,
                        version,
                        ParseAnimationsEnabled);
                    reducedMotion = animationsEnabled is null
                        ? null
                        : !animationsEnabled.Value;
                }

                textScale = await TryReadSettingAsync(
                    GnomeInterfaceNamespace,
                    GnomeTextScaleKey,
                    version,
                    ParseTextScale);
            }

            Publish(HostAccessibilityPreferenceMapping.FromLinux(
                reducedMotion,
                textScale));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<T?> TryReadSettingAsync<T>(
        string settingNamespace,
        string key,
        uint version,
        Func<VariantValue, T?> parse)
        where T : struct
    {
        try
        {
            var value = await _client.ReadAsync(settingNamespace, key, version);
            return parse(value);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return null;
        }
    }

    private bool IsRelevant(LinuxPortalSettingChanged setting) =>
        setting is { Namespace: AppearanceNamespace, Key: ReducedMotionKey }
        || (_readGnomePreferences
            && string.Equals(setting.Namespace, GnomeInterfaceNamespace
, StringComparison.Ordinal) && setting.Key is GnomeAnimationsKey or GnomeTextScaleKey);

    private static T? TryRead<T>(VariantValue value, Func<VariantValue, T> read)
        where T : struct
    {
        try
        {
            return read(value);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or InvalidCastException
            or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsUnavailable(Exception exception) => exception is
        DBusConnectionException
        or DBusErrorReplyException
        or IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or ObjectDisposedException
        or ExternalException;
}
