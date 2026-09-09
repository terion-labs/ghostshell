using System.Runtime.InteropServices;
using Asura.Application;
using Asura.Core;

namespace Asura.Desktop;

internal sealed class MacOsGlobalHotkeyService : IGlobalHotkeyService
{
    private const string CarbonLibrary =
        "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const int NoError = 0;
    private const int EventNotHandledError = -9874;
    private const int EventHotKeyExistsError = -9878;
    private const int EventHotKeyInvalidError = -9879;
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventHotKeyPressed = 5;
    private const uint EventParamDirectObject = 0x2D2D2D2D;
    private const uint TypeEventHotKeyId = 0x686B6964;
    private const uint AsuraSignature = 0x4753484C;
    private const uint AsuraHotKeyId = 1;
    private const uint AsuraEscapeHotKeyId = 2;
    private const uint EventHotKeyExclusive = 1;
    private const uint CommandKey = 1U << 8;
    private const uint ShiftKey = 1U << 9;
    private const uint OptionKey = 1U << 11;
    private const uint ControlKey = 1U << 12;
    private const uint EscapeVirtualKeyCode = 53;

    private readonly EventHandlerDelegate _eventHandler;
    private IntPtr _eventHandlerReference;
    private IntPtr _hotKeyReference;
    private IntPtr _escapeHotKeyReference;
    private bool _disposed;

    public MacOsGlobalHotkeyService()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The Carbon hot-key adapter requires macOS.");
        }

        _eventHandler = HandleEvent;
    }

    public event EventHandler? Pressed;

    public event EventHandler? EscapePressed;

    public KeyStroke? RegisteredGesture { get; private set; }

    public GlobalHotkeyRegistrationResult Register(KeyStroke gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister();
        if (!TryMapGesture(gesture, out var nativeGesture))
        {
            return Failure(
                GlobalHotkeyRegistrationErrorCode.InvalidGesture,
                "global_hotkey_invalid_gesture",
                "The selected shortcut is not supported by the macOS hot-key adapter.");
        }

        var installStatus = EnsureHandler();
        if (installStatus != NoError)
        {
            return NativeFailure(installStatus);
        }

        var hotKeyId = new EventHotKeyId(AsuraSignature, AsuraHotKeyId);
        var registrationStatus = RegisterEventHotKey(
            nativeGesture.VirtualKey,
            nativeGesture.Modifiers,
            hotKeyId,
            GetApplicationEventTarget(),
            EventHotKeyExclusive,
            out _hotKeyReference);
        if (registrationStatus != NoError)
        {
            ReleaseUnexpectedRegistration(ref _hotKeyReference);
            RemoveHandlerIfUnused();
            return registrationStatus switch
            {
                EventHotKeyExistsError => Failure(
                    GlobalHotkeyRegistrationErrorCode.Conflict,
                    "global_hotkey_conflict",
                    "Another application already owns this global shortcut."),
                EventHotKeyInvalidError => Failure(
                    GlobalHotkeyRegistrationErrorCode.InvalidGesture,
                    "global_hotkey_invalid_gesture",
                    "macOS rejected the selected global shortcut."),
                _ => NativeFailure(registrationStatus),
            };
        }

        RegisteredGesture = gesture;
        return new GlobalHotkeyRegistrationResult.Success(gesture);
    }

    public void Unregister()
    {
        if (_hotKeyReference != IntPtr.Zero)
        {
            _ = UnregisterEventHotKey(_hotKeyReference);
            _hotKeyReference = IntPtr.Zero;
        }

        RemoveHandlerIfUnused();
        RegisteredGesture = null;
    }

    public GlobalHotkeyRegistrationResult BeginEscapeCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EndEscapeCapture();
        var installStatus = EnsureHandler();
        if (installStatus != NoError)
        {
            return NativeFailure(installStatus);
        }

        var hotKeyId = new EventHotKeyId(AsuraSignature, AsuraEscapeHotKeyId);
        var registrationStatus = RegisterEventHotKey(
            EscapeVirtualKeyCode,
            0,
            hotKeyId,
            GetApplicationEventTarget(),
            EventHotKeyExclusive,
            out _escapeHotKeyReference);
        if (registrationStatus != NoError)
        {
            ReleaseUnexpectedRegistration(ref _escapeHotKeyReference);
            RemoveHandlerIfUnused();
            return registrationStatus switch
            {
                EventHotKeyExistsError => Failure(
                    GlobalHotkeyRegistrationErrorCode.Conflict,
                    "escape_capture_conflict",
                    "Another application already owns Escape while Quick Terminal is open."),
                EventHotKeyInvalidError => Failure(
                    GlobalHotkeyRegistrationErrorCode.InvalidGesture,
                    "escape_capture_invalid",
                    "macOS rejected transient Escape capture."),
                _ => NativeFailure(registrationStatus),
            };
        }

        return new GlobalHotkeyRegistrationResult.Success(new KeyStroke("ESCAPE"));
    }

    public void EndEscapeCapture()
    {
        if (_escapeHotKeyReference != IntPtr.Zero)
        {
            _ = UnregisterEventHotKey(_escapeHotKeyReference);
            _escapeHotKeyReference = IntPtr.Zero;
        }

        RemoveHandlerIfUnused();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EndEscapeCapture();
        Unregister();
        _disposed = true;
    }

    private int HandleEvent(IntPtr nextHandler, IntPtr eventReference, IntPtr userData)
    {
        _ = nextHandler;
        _ = userData;
        var status = GetEventParameter(
            eventReference,
            EventParamDirectObject,
            TypeEventHotKeyId,
            IntPtr.Zero,
            (uint)Marshal.SizeOf<EventHotKeyId>(),
            IntPtr.Zero,
            out var hotKeyId);
        if (status != NoError || hotKeyId.Signature != AsuraSignature)
        {
            return EventNotHandledError;
        }

        switch (hotKeyId.Id)
        {
            case AsuraHotKeyId:
                Pressed?.Invoke(this, EventArgs.Empty);
                return NoError;
            case AsuraEscapeHotKeyId:
                EscapePressed?.Invoke(this, EventArgs.Empty);
                return NoError;
            default:
                return EventNotHandledError;
        }
    }

    private int EnsureHandler()
    {
        if (_eventHandlerReference != IntPtr.Zero)
        {
            return NoError;
        }

        var eventType = new EventTypeSpec(EventClassKeyboard, EventHotKeyPressed);
        var status = InstallEventHandler(
            GetApplicationEventTarget(),
            _eventHandler,
            1,
            ref eventType,
            IntPtr.Zero,
            out _eventHandlerReference);
        if (status != NoError)
        {
            _eventHandlerReference = IntPtr.Zero;
        }

        return status;
    }

    private static void ReleaseUnexpectedRegistration(ref IntPtr hotKeyReference)
    {
        if (hotKeyReference == IntPtr.Zero)
        {
            return;
        }

        _ = UnregisterEventHotKey(hotKeyReference);
        hotKeyReference = IntPtr.Zero;
    }

    private void RemoveHandlerIfUnused()
    {
        if (_eventHandlerReference == IntPtr.Zero
            || _hotKeyReference != IntPtr.Zero
            || _escapeHotKeyReference != IntPtr.Zero)
        {
            return;
        }

        _ = RemoveEventHandler(_eventHandlerReference);
        _eventHandlerReference = IntPtr.Zero;
    }

    internal static bool TryMapGesture(
        KeyStroke gesture,
        out MacOsHotkeyGesture nativeGesture)
    {
        nativeGesture = default;
        if (gesture.Modifiers == KeyModifiers.None
            || !TryMapVirtualKey(gesture.Key, out var virtualKey))
        {
            return false;
        }

        var modifiers = 0U;
        if ((gesture.Modifiers & KeyModifiers.Meta) != KeyModifiers.None)
        {
            modifiers |= CommandKey;
        }

        if ((gesture.Modifiers & KeyModifiers.Shift) != KeyModifiers.None)
        {
            modifiers |= ShiftKey;
        }

        if ((gesture.Modifiers & KeyModifiers.Alt) != KeyModifiers.None)
        {
            modifiers |= OptionKey;
        }

        if ((gesture.Modifiers & KeyModifiers.Control) != KeyModifiers.None)
        {
            modifiers |= ControlKey;
        }

        nativeGesture = new MacOsHotkeyGesture(virtualKey, modifiers);
        return true;
    }

    private static bool TryMapVirtualKey(string key, out uint virtualKey)
    {
        virtualKey = key switch
        {
            "A" => 0,
            "S" => 1,
            "D" => 2,
            "F" => 3,
            "H" => 4,
            "G" => 5,
            "Z" => 6,
            "X" => 7,
            "C" => 8,
            "V" => 9,
            "B" => 11,
            "Q" => 12,
            "W" => 13,
            "E" => 14,
            "R" => 15,
            "Y" => 16,
            "T" => 17,
            "1" => 18,
            "2" => 19,
            "3" => 20,
            "4" => 21,
            "6" => 22,
            "5" => 23,
            "9" => 25,
            "7" => 26,
            "8" => 28,
            "0" => 29,
            "O" => 31,
            "U" => 32,
            "I" => 34,
            "P" => 35,
            "L" => 37,
            "J" => 38,
            "K" => 40,
            "N" => 45,
            "M" => 46,
            "SPACE" => 49,
            "`" or "GRAVE" or "OEMTILDE" or "OEM3" => 50,
            "HOME" => 115,
            "PAGEUP" => 116,
            "DELETE" => 117,
            "END" => 119,
            "PAGEDOWN" => 121,
            "LEFT" or "ARROWLEFT" => 123,
            "RIGHT" or "ARROWRIGHT" => 124,
            "DOWN" or "ARROWDOWN" => 125,
            "UP" or "ARROWUP" => 126,
            _ => FunctionVirtualKey(key),
        };
        return virtualKey != uint.MaxValue;
    }

    private static uint FunctionVirtualKey(string key) => key switch
    {
        "F1" => 122,
        "F2" => 120,
        "F3" => 99,
        "F4" => 118,
        "F5" => 96,
        "F6" => 97,
        "F7" => 98,
        "F8" => 100,
        "F9" => 101,
        "F10" => 109,
        "F11" => 103,
        "F12" => 111,
        "F13" => 105,
        "F14" => 107,
        "F15" => 113,
        "F16" => 106,
        "F17" => 64,
        "F18" => 79,
        "F19" => 80,
        "F20" => 90,
        _ => uint.MaxValue,
    };

    private static GlobalHotkeyRegistrationResult Failure(
        GlobalHotkeyRegistrationErrorCode code,
        string stableCode,
        string message) =>
        new GlobalHotkeyRegistrationResult.Failure(new(code, stableCode, message));

    private static GlobalHotkeyRegistrationResult NativeFailure(int status) => Failure(
        GlobalHotkeyRegistrationErrorCode.NativeFailure,
        "global_hotkey_native_failure",
        $"macOS could not register the global shortcut (OSStatus {status}).");

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventTypeSpec(uint EventClass, uint EventKind);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventHotKeyId(uint Signature, uint Id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerDelegate(
        IntPtr nextHandler,
        IntPtr eventReference,
        IntPtr userData);

    [DllImport(CarbonLibrary)]
    private static extern int InstallEventHandler(
        IntPtr eventTarget,
        EventHandlerDelegate handler,
        uint eventTypeCount,
        ref EventTypeSpec eventTypes,
        IntPtr userData,
        out IntPtr eventHandlerReference);

    [DllImport(CarbonLibrary)]
    private static extern int RemoveEventHandler(IntPtr eventHandlerReference);

    [DllImport(CarbonLibrary)]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(CarbonLibrary)]
    private static extern int RegisterEventHotKey(
        uint hotKeyCode,
        uint hotKeyModifiers,
        EventHotKeyId hotKeyId,
        IntPtr target,
        uint options,
        out IntPtr hotKeyReference);

    [DllImport(CarbonLibrary)]
    private static extern int UnregisterEventHotKey(IntPtr hotKeyReference);

    [DllImport(CarbonLibrary)]
    private static extern int GetEventParameter(
        IntPtr eventReference,
        uint parameterName,
        uint desiredType,
        IntPtr actualType,
        uint bufferSize,
        IntPtr actualSize,
        out EventHotKeyId data);
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct MacOsHotkeyGesture(uint VirtualKey, uint Modifiers);
