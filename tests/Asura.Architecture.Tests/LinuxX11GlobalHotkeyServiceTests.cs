using Asura.Application;
using Asura.Core;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class LinuxX11GlobalHotkeyServiceTests
{
    private static readonly KeyStroke PrimaryGesture = new(
        "F24",
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);

    [Theory]
    [InlineData("GRAVE", KeyModifiers.Meta, 0x0060UL, 0x40U)]
    [InlineData("K", KeyModifiers.Control | KeyModifiers.Alt, 0x006BUL, 0x0CU)]
    [InlineData(
        "F24",
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta,
        0xFFD5UL,
        0x4DU)]
    public void Gesture_mapping_uses_x11_key_symbols_and_modifier_masks(
        string key,
        KeyModifiers modifiers,
        ulong expectedKeySymbol,
        uint expectedModifiers)
    {
        Assert.True(LinuxX11GlobalHotkeyService.TryMapGesture(
            new KeyStroke(key, modifiers),
            out var actual));
        Assert.Equal((nuint)expectedKeySymbol, actual.KeySymbol);
        Assert.Equal(expectedModifiers, actual.Modifiers);
    }

    [Fact]
    public void Gesture_mapping_rejects_modifierless_and_unknown_keys()
    {
        Assert.False(LinuxX11GlobalHotkeyService.TryMapGesture(
            new KeyStroke("K"),
            out _));
        Assert.False(LinuxX11GlobalHotkeyService.TryMapGesture(
            new KeyStroke("NOTAKEY", KeyModifiers.Control),
            out _));
    }

    [Fact]
    public void Primary_and_escape_registrations_route_events_and_release_transient_capture()
    {
        var loop = new FakeX11HotkeyLoop();
        using var service = new LinuxX11GlobalHotkeyService(loop);
        var primaryPresses = 0;
        var escapePresses = 0;
        service.Pressed += (_, _) => primaryPresses++;
        service.EscapePressed += (_, _) => escapePresses++;

        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(service.Register(PrimaryGesture));
        loop.Emit(1);
        Assert.True(SpinWait.SpinUntil(() => primaryPresses == 1, TimeSpan.FromSeconds(1)));

        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(service.BeginEscapeCapture());
        loop.Emit(2);
        Assert.True(SpinWait.SpinUntil(() => escapePresses == 1, TimeSpan.FromSeconds(1)));

        service.EndEscapeCapture();
        loop.Emit(2);
        Assert.Equal(1, escapePresses);
        Assert.Equal(PrimaryGesture, service.RegisteredGesture);
    }

    [Theory]
    [InlineData(10, GlobalHotkeyRegistrationErrorCode.Conflict, "global_hotkey_conflict")]
    [InlineData(2, GlobalHotkeyRegistrationErrorCode.InvalidGesture, "global_hotkey_invalid_gesture")]
    [InlineData(3, GlobalHotkeyRegistrationErrorCode.NativeFailure, "global_hotkey_native_failure")]
    public void Native_primary_errors_are_mapped_exactly(
        byte nativeError,
        GlobalHotkeyRegistrationErrorCode expectedCode,
        string expectedStableCode)
    {
        var loop = new FakeX11HotkeyLoop
        {
            NextResult = X11HotkeyNativeResult.Failure(nativeError),
        };
        using var service = new LinuxX11GlobalHotkeyService(loop);

        var failure = Assert.IsType<GlobalHotkeyRegistrationResult.Failure>(
            service.Register(PrimaryGesture));

        Assert.Equal(expectedCode, failure.Error.Code);
        Assert.Equal(expectedStableCode, failure.Error.StableCode);
        if (nativeError == 3)
        {
            Assert.Contains("X11 error 3", failure.Error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(10, GlobalHotkeyRegistrationErrorCode.Conflict, "escape_capture_conflict")]
    [InlineData(2, GlobalHotkeyRegistrationErrorCode.InvalidGesture, "escape_capture_invalid")]
    [InlineData(3, GlobalHotkeyRegistrationErrorCode.NativeFailure, "escape_capture_native_failure")]
    public void Native_escape_errors_are_mapped_exactly(
        byte nativeError,
        GlobalHotkeyRegistrationErrorCode expectedCode,
        string expectedStableCode)
    {
        var loop = new FakeX11HotkeyLoop
        {
            NextResult = X11HotkeyNativeResult.Failure(nativeError),
        };
        using var service = new LinuxX11GlobalHotkeyService(loop);

        var failure = Assert.IsType<GlobalHotkeyRegistrationResult.Failure>(
            service.BeginEscapeCapture());

        Assert.Equal(expectedCode, failure.Error.Code);
        Assert.Equal(expectedStableCode, failure.Error.StableCode);
    }

    [Fact]
    public void Dispose_unregisters_owned_ids_and_disposes_native_loop_once()
    {
        var loop = new FakeX11HotkeyLoop();
        var service = new LinuxX11GlobalHotkeyService(loop);
        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(service.Register(PrimaryGesture));
        Assert.IsType<GlobalHotkeyRegistrationResult.Success>(service.BeginEscapeCapture());

        service.Dispose();
        service.Dispose();

        Assert.Contains(1, loop.Unregistrations);
        Assert.Contains(2, loop.Unregistrations);
        Assert.Equal(1, loop.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => service.BeginEscapeCapture());
    }

    [Fact]
    public void X11_error_routing_captures_only_the_owned_display()
    {
        var capturedErrors = new List<byte>();
        var ownedDisplay = new IntPtr(101);
        var forwardedErrors = 0;

        Assert.Equal(0, X11HotkeyMessageLoop.RouteError(
            ownedDisplay,
            ownedDisplay,
            10,
            capturedErrors.Add,
            () => ++forwardedErrors));
        Assert.Equal(1, X11HotkeyMessageLoop.RouteError(
            new IntPtr(202),
            ownedDisplay,
            3,
            capturedErrors.Add,
            () => ++forwardedErrors));

        Assert.Equal([10], capturedErrors);
        Assert.Equal(1, forwardedErrors);
    }

    private sealed class FakeX11HotkeyLoop : IX11HotkeyLoop
    {
        public event Action<int>? HotkeyPressed;

        public X11HotkeyNativeResult NextResult { get; set; } = X11HotkeyNativeResult.Success;

        public List<int> Unregistrations { get; } = [];

        public int DisposeCount { get; private set; }

        public X11HotkeyNativeResult Register(int id, X11HotkeyGesture gesture)
        {
            _ = id;
            _ = gesture;
            var result = NextResult;
            NextResult = X11HotkeyNativeResult.Success;
            return result;
        }

        public void Unregister(int id) => Unregistrations.Add(id);

        public void Dispose() => DisposeCount++;

        public void Emit(int id) => HotkeyPressed?.Invoke(id);
    }
}
