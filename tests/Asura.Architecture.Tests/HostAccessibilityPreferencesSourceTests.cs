using Asura.Application;
using Asura.Core;
using Asura.Desktop;
using Microsoft.Extensions.DependencyInjection;
using Tmds.DBus.Protocol;

namespace Asura.Architecture.Tests;

public sealed class HostAccessibilityPreferencesSourceTests
{
    [Fact]
    public void SnapshotRejectsInvalidTextScale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HostAccessibilityPreferences(false, false, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HostAccessibilityPreferences(false, false, 0.49));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HostAccessibilityPreferences(false, false, 4.01));
    }

    [Fact]
    public void PlatformMappingsUseConservativeDefaultsAndValidatedScales()
    {
        Assert.Equal(
            HostAccessibilityPreferences.Default,
            HostAccessibilityPreferenceMapping.FromMacOs(null, null));
        Assert.Equal(
            new HostAccessibilityPreferences(true, true, 1.5),
            HostAccessibilityPreferenceMapping.FromWindows(
                animationsEnabled: false,
                transparencyEnabled: false,
                textScale: 1.5));
        Assert.Equal(
            HostAccessibilityPreferences.Default,
            HostAccessibilityPreferenceMapping.FromWindows(
                animationsEnabled: null,
                transparencyEnabled: null,
                textScale: 9));
        Assert.Equal(
            new HostAccessibilityPreferences(true, false, 1.25),
            HostAccessibilityPreferenceMapping.FromLinux(
                reducedMotion: true,
                textScale: 1.25));
    }

    [Fact]
    public void SourceStartsOncePublishesOnlyRealChangesAndStopsAfterDisposal()
    {
        var source = new TestHostAccessibilityPreferencesSource();
        var changes = 0;
        source.Changed += (_, _) => changes++;

        source.Start();
        source.Start();
        source.Push(HostAccessibilityPreferences.Default);
        source.Push(new HostAccessibilityPreferences(true, true, 1.5));
        source.Push(new HostAccessibilityPreferences(true, true, 1.5));

        Assert.Equal(1, source.StartCount);
        Assert.Equal(1, changes);
        Assert.Equal(
            new HostAccessibilityPreferences(true, true, 1.5),
            source.Current);

        source.Dispose();
        source.Push(HostAccessibilityPreferences.Default);

        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(1, changes);
        Assert.Throws<ObjectDisposedException>(source.Start);
    }

    [Fact]
    public void LinuxParsersRejectUnknownOrMismatchedPortalValues()
    {
        Assert.False(LinuxHostAccessibilityPreferencesSource.ParseReducedMotion(0u));
        Assert.True(LinuxHostAccessibilityPreferencesSource.ParseReducedMotion(1u));
        Assert.Null(LinuxHostAccessibilityPreferencesSource.ParseReducedMotion(2u));
        Assert.Null(LinuxHostAccessibilityPreferencesSource.ParseReducedMotion("1"));
        Assert.False(LinuxHostAccessibilityPreferencesSource.ParseAnimationsEnabled(false));
        Assert.Null(LinuxHostAccessibilityPreferencesSource.ParseAnimationsEnabled(0u));
        Assert.Equal(1.25, LinuxHostAccessibilityPreferencesSource.ParseTextScale(1.25));
        Assert.Null(LinuxHostAccessibilityPreferencesSource.ParseTextScale(-1d));
    }

    [Fact]
    public void LinuxDesktopDetectionOnlyEnablesGnomeImplementationDetailsOnGnome()
    {
        Assert.True(LinuxHostAccessibilityPreferencesSource.IsGnomeDesktop(
            "ubuntu:GNOME",
            null));
        Assert.False(LinuxHostAccessibilityPreferencesSource.IsGnomeDesktop(
            "KDE",
            "plasma"));
    }

    [Fact]
    public async Task LinuxSourceReadsPortalAndPublishesRelevantSignals()
    {
        var client = new FakeLinuxPortalSettingsClient();
        client.Set(
            LinuxHostAccessibilityPreferencesSource.AppearanceNamespace,
            LinuxHostAccessibilityPreferencesSource.ReducedMotionKey,
            1u);
        client.Set(
            LinuxHostAccessibilityPreferencesSource.GnomeInterfaceNamespace,
            LinuxHostAccessibilityPreferencesSource.GnomeTextScaleKey,
            1.25);
        using var source = new LinuxHostAccessibilityPreferencesSource(
            client,
            readGnomePreferences: true);
        source.Start();
        await source.Initialization;

        Assert.Equal(
            new HostAccessibilityPreferences(true, false, 1.25),
            source.Current);

        var changed = new TaskCompletionSource<HostAccessibilityPreferences>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += (_, _) => changed.TrySetResult(source.Current);
        client.Set(
            LinuxHostAccessibilityPreferencesSource.AppearanceNamespace,
            LinuxHostAccessibilityPreferencesSource.ReducedMotionKey,
            0u);
        client.Set(
            LinuxHostAccessibilityPreferencesSource.GnomeInterfaceNamespace,
            LinuxHostAccessibilityPreferencesSource.GnomeTextScaleKey,
            1.5);
        client.Emit(
            LinuxHostAccessibilityPreferencesSource.AppearanceNamespace,
            LinuxHostAccessibilityPreferencesSource.ReducedMotionKey,
            0u);

        Assert.Equal(
            new HostAccessibilityPreferences(false, false, 1.5),
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task LinuxSourceFallsBackToGnomeAnimationPreference()
    {
        var client = new FakeLinuxPortalSettingsClient();
        client.Set(
            LinuxHostAccessibilityPreferencesSource.GnomeInterfaceNamespace,
            LinuxHostAccessibilityPreferencesSource.GnomeAnimationsKey,
            false);
        using var source = new LinuxHostAccessibilityPreferencesSource(
            client,
            readGnomePreferences: true);

        source.Start();
        await source.Initialization;

        Assert.Equal(
            new HostAccessibilityPreferences(true, false, 1),
            source.Current);
    }

    [Fact]
    public async Task LinuxSourceDisposesItsSignalSubscriptionAndClient()
    {
        var client = new FakeLinuxPortalSettingsClient();
        var source = new LinuxHostAccessibilityPreferencesSource(
            client,
            readGnomePreferences: false);
        source.Start();
        await source.Initialization;

        source.Dispose();

        Assert.True(client.SubscriptionDisposed);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task DesktopCompositionRegistersOneUnstartedDisposableSource()
    {
        await using var services = DesktopComposition.CreateServiceProvider();

        var first = services.GetRequiredService<IHostAccessibilityPreferencesSource>();
        var second = services.GetRequiredService<IHostAccessibilityPreferencesSource>();

        Assert.Same(first, second);
        Assert.Equal(HostAccessibilityPreferences.Default, first.Current);
    }

    [Fact]
    public void MacOsSourceCanReadAndReleaseTheDocumentedWorkspacePreferences()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(10, 12))
        {
            return;
        }

        using var source = new MacOsHostAccessibilityPreferencesSource();

        source.Start();

        Assert.InRange(source.Current.TextScale, 0.5, 4);
    }

    private sealed class TestHostAccessibilityPreferencesSource :
        HostAccessibilityPreferencesSource
    {
        public int StartCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void Push(HostAccessibilityPreferences next) => Publish(next);

        protected override void StartCore() => StartCount++;

        protected override void DisposeCore() => DisposeCount++;
    }

    private sealed class FakeLinuxPortalSettingsClient : ILinuxPortalSettingsClient
    {
        private readonly Dictionary<(string Namespace, string Key), VariantValue> _values = [];
        private Action<Exception?, LinuxPortalSettingChanged>? _handler;

        public bool Disposed { get; private set; }

        public bool SubscriptionDisposed { get; private set; }

        public void Set(string settingNamespace, string key, VariantValue value) =>
            _values[(settingNamespace, key)] = value;

        public void Emit(string settingNamespace, string key, VariantValue value) =>
            _handler?.Invoke(
                null,
                new LinuxPortalSettingChanged(settingNamespace, key, value));

        public Task<IDisposable> WatchSettingChangedAsync(
            Action<Exception?, LinuxPortalSettingChanged> handler)
        {
            _handler = handler;
            return Task.FromResult<IDisposable>(new CallbackDisposable(
                () => SubscriptionDisposed = true));
        }

        public Task<uint> GetVersionAsync() => Task.FromResult(2u);

        public Task<VariantValue> ReadAsync(
            string settingNamespace,
            string key,
            uint version)
        {
            _ = version;
            return _values.TryGetValue((settingNamespace, key), out var value)
                ? Task.FromResult(value)
                : Task.FromException<VariantValue>(new DBusErrorReplyException(
                    "org.freedesktop.portal.Error.NotFound",
                    "Setting not found."));
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
