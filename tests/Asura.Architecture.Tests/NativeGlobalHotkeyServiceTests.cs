using Asura.Application;
using Asura.Core;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

[Collection("native global hotkey")]
public sealed class NativeGlobalHotkeyServiceTests
{
    private static readonly KeyStroke IsolatedGesture = new(
        "F24",
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);

    [Fact]
    public void Windows_duplicate_registration_conflicts_and_release_allows_retry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var first = new WindowsGlobalHotkeyService();
        using var second = new WindowsGlobalHotkeyService();

        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(first.Register(IsolatedGesture));
        var conflict = Assert.IsType<GlobalHotkeyRegistrationResult.Failure>(
            second.Register(IsolatedGesture));
        Assert.Equal(GlobalHotkeyRegistrationErrorCode.Conflict, conflict.Error.Code);

        first.Unregister();
        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(second.Register(IsolatedGesture));
    }

    [Fact]
    public void X11_duplicate_registration_conflicts_and_release_allows_retry()
    {
        if (!OperatingSystem.IsLinux()
            || GlobalHotkeyServiceSelector.Select(
                GlobalHotkeyRuntimePlatform.Linux,
                LinuxDesktopSession.FromEnvironment()) != GlobalHotkeyBackend.LinuxX11)
        {
            return;
        }

        using var first = new LinuxX11GlobalHotkeyService();
        using var second = new LinuxX11GlobalHotkeyService();

        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(first.Register(IsolatedGesture));
        var conflict = Assert.IsType<GlobalHotkeyRegistrationResult.Failure>(
            second.Register(IsolatedGesture));
        Assert.Equal(GlobalHotkeyRegistrationErrorCode.Conflict, conflict.Error.Code);

        first.Unregister();
        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(second.Register(IsolatedGesture));
    }
}

[CollectionDefinition("native global hotkey", DisableParallelization = true)]
public sealed class NativeGlobalHotkeyCollection;
