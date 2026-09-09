using Tmds.DBus.Protocol;
using Tmds.DBus.SourceGenerator;

namespace Asura.Desktop;

internal interface ILinuxPortalSettingsClient : IDisposable
{
    Task<IDisposable> WatchSettingChangedAsync(
        Action<Exception?, LinuxPortalSettingChanged> handler);

    Task<uint> GetVersionAsync();

    Task<VariantValue> ReadAsync(string settingNamespace, string key, uint version);
}

internal readonly record struct LinuxPortalSettingChanged(
    string Namespace,
    string Key,
    VariantValue Value);

internal sealed class LinuxPortalSettingsClient : ILinuxPortalSettingsClient
{
    private const string ServiceName = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";

#pragma warning disable CS0618 // Tmds source generator 0.0.22 still emits the legacy Connection API.
    private readonly OrgFreedesktopPortalSettingsProxy _proxy = new(
        Connection.Session,
        ServiceName,
        ObjectPath);
#pragma warning restore CS0618

    public async Task<IDisposable> WatchSettingChangedAsync(
        Action<Exception?, LinuxPortalSettingChanged> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return await _proxy.WatchSettingChangedAsync((exception, setting) =>
            handler(
                exception,
                new LinuxPortalSettingChanged(
                    setting.Namespace,
                    setting.Key,
                    setting.Value)));
    }

    public Task<uint> GetVersionAsync() => _proxy.GetVersionPropertyAsync();

    public async Task<VariantValue> ReadAsync(
        string settingNamespace,
        string key,
        uint version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (version >= 2)
        {
            return await _proxy.ReadOneAsync(settingNamespace, key);
        }

#pragma warning disable CS0618 // Settings.Read is the compatibility path for portal v1.
        return (await _proxy.ReadAsync(settingNamespace, key)).GetVariantValue();
#pragma warning restore CS0618
    }

    public void Dispose()
    {
        // Connection.Session is shared. The source owns only the match subscription.
    }
}
