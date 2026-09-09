using Asura.Application;
using Asura.Core;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class WindowsGlobalHotkeyServiceTests
{
    private static readonly KeyStroke PrimaryGesture = new(
        "F24",
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);

    [Theory]
    [InlineData("GRAVE", KeyModifiers.Meta, 0xC0U, 0x4008U)]
    [InlineData("K", KeyModifiers.Control | KeyModifiers.Alt, 0x4BU, 0x4003U)]
    [InlineData(
        "F24",
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta,
        0x87U,
        0x400FU)]
    public void Gesture_mapping_uses_win32_virtual_keys_and_modifiers(
        string key,
        KeyModifiers modifiers,
        uint expectedVirtualKey,
        uint expectedModifiers)
    {
        Assert.True(WindowsGlobalHotkeyService.TryMapGesture(
            new KeyStroke(key, modifiers),
            out var actual));
        Assert.Equal(expectedVirtualKey, actual.VirtualKey);
        Assert.Equal(expectedModifiers, actual.Modifiers);
    }

    [Fact]
    public void Gesture_mapping_rejects_modifierless_and_unknown_keys()
    {
        Assert.False(WindowsGlobalHotkeyService.TryMapGesture(
            new KeyStroke("K"),
            out _));
        Assert.False(WindowsGlobalHotkeyService.TryMapGesture(
            new KeyStroke("NOTAKEY", KeyModifiers.Control),
            out _));
    }

    [Fact]
    public void Primary_and_escape_registrations_route_events_and_release_transient_capture()
    {
        var loop = new FakeWindowsHotkeyLoop();
        using var service = new WindowsGlobalHotkeyService(loop);
        var primaryPresses = 0;
        var escapePresses = 0;
        service.Pressed += (_, _) => primaryPresses++;
        service.EscapePressed += (_, _) => escapePresses++;

        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(service.Register(PrimaryGesture));
        Assert.Equal(PrimaryGesture, service.RegisteredGesture);
        loop.Emit(1);
        Assert.True(SpinWait.SpinUntil(() => primaryPresses == 1, TimeSpan.FromSeconds(1)));

        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(service.BeginEscapeCapture());
        loop.Emit(2);
        Assert.True(SpinWait.SpinUntil(() => escapePresses == 1, TimeSpan.FromSeconds(1)));

        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(service.BeginEscapeCapture());
        Assert.Equal(1, loop.Unregistrations.Count(id => id == 2));
        service.EndEscapeCapture();
        loop.Emit(2);
        Assert.Equal(1, escapePresses);
        Assert.Equal(2, loop.Unregistrations.Count(id => id == 2));
    }

    [Theory]
    [InlineData(1409, GlobalHotkeyRegistrationErrorCode.Conflict, "global_hotkey_conflict")]
    [InlineData(1422, GlobalHotkeyRegistrationErrorCode.InvalidGesture, "global_hotkey_invalid_gesture")]
    [InlineData(5, GlobalHotkeyRegistrationErrorCode.NativeFailure, "global_hotkey_native_failure")]
    public void Native_primary_errors_are_mapped_exactly(
        int nativeError,
        GlobalHotkeyRegistrationErrorCode expectedCode,
        string expectedStableCode)
    {
        var loop = new FakeWindowsHotkeyLoop
        {
            NextResult = WindowsHotkeyNativeResult.Failure(nativeError),
        };
        using var service = new WindowsGlobalHotkeyService(loop);

        var failure = Assert.IsType<GlobalHotkeyRegistrationResult.Failure>(
            service.Register(PrimaryGesture));

        Assert.Equal(expectedCode, failure.Error.Code);
        Assert.Equal(expectedStableCode, failure.Error.StableCode);
        if (nativeError == 5)
        {
            Assert.Contains("Win32 error 5", failure.Error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1409, GlobalHotkeyRegistrationErrorCode.Conflict, "escape_capture_conflict")]
    [InlineData(1422, GlobalHotkeyRegistrationErrorCode.InvalidGesture, "escape_capture_invalid")]
    [InlineData(87, GlobalHotkeyRegistrationErrorCode.NativeFailure, "escape_capture_native_failure")]
    public void Native_escape_errors_are_mapped_exactly(
        int nativeError,
        GlobalHotkeyRegistrationErrorCode expectedCode,
        string expectedStableCode)
    {
        var loop = new FakeWindowsHotkeyLoop
        {
            NextResult = WindowsHotkeyNativeResult.Failure(nativeError),
        };
        using var service = new WindowsGlobalHotkeyService(loop);

        var failure = Assert.IsType<GlobalHotkeyRegistrationResult.Failure>(
            service.BeginEscapeCapture());

        Assert.Equal(expectedCode, failure.Error.Code);
        Assert.Equal(expectedStableCode, failure.Error.StableCode);
        if (nativeError == 87)
        {
            Assert.Contains("Win32 error 87", failure.Error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Dispose_unregisters_owned_ids_and_disposes_native_loop_once()
    {
        var loop = new FakeWindowsHotkeyLoop();
        var service = new WindowsGlobalHotkeyService(loop);
        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(service.Register(PrimaryGesture));
        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(service.BeginEscapeCapture());

        service.Dispose();
        service.Dispose();

        Assert.Contains(1, loop.Unregistrations);
        Assert.Contains(2, loop.Unregistrations);
        Assert.Equal(1, loop.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => service.Register(PrimaryGesture));
    }

    private sealed class FakeWindowsHotkeyLoop : IWindowsHotkeyLoop
    {
        public event Action<int>? HotkeyPressed;

        public WindowsHotkeyNativeResult NextResult { get; set; } =
            WindowsHotkeyNativeResult.Success;

        public List<int> Registrations { get; } = [];

        public List<int> Unregistrations { get; } = [];

        public int DisposeCount { get; private set; }

        public WindowsHotkeyNativeResult Register(int id, WindowsHotkeyGesture gesture)
        {
            _ = gesture;
            Registrations.Add(id);
            var result = NextResult;
            NextResult = WindowsHotkeyNativeResult.Success;
            return result;
        }

        public void Unregister(int id) => Unregistrations.Add(id);

        public void Dispose() => DisposeCount++;

        public void Emit(int id) => HotkeyPressed?.Invoke(id);
    }
}
