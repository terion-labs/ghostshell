using System.Diagnostics;
using System.Runtime.InteropServices;
using Asura.Application;
using Asura.Core;

namespace Asura.Desktop;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct X11HotkeyGesture(nuint KeySymbol, uint Modifiers);

internal readonly record struct X11HotkeyNativeResult(bool Succeeded, byte ErrorCode)
{
    public static X11HotkeyNativeResult Success { get; } = new(true, 0);

    public static X11HotkeyNativeResult Failure(byte errorCode) => new(false, errorCode);
}

internal interface IX11HotkeyLoop : IDisposable
{
    event Action<int>? HotkeyPressed;

    X11HotkeyNativeResult Register(int id, X11HotkeyGesture gesture);

    void Unregister(int id);
}

internal sealed class X11HotkeyConnectionException(string message) : Exception(message)
{
}

internal sealed class LinuxX11GlobalHotkeyService : IGlobalHotkeyService
{
    private const int PrimaryHotkeyId = 1;
    private const int EscapeHotkeyId = 2;
    private const byte BadValue = 2;
    private const byte BadAccess = 10;
    private const uint ShiftMask = 1U << 0;
    private const uint ControlMask = 1U << 2;
    private const uint AltMask = 1U << 3;
    private const uint MetaMask = 1U << 6;
    private const nuint EscapeKeySymbol = 0xFF1B;
    private const nuint GraveKeySymbol = 0x0060;
    private const nuint F1KeySymbol = 0xFFBE;
    private const nuint F24KeySymbol = 0xFFD5;

    private readonly object _lifecycleGate = new();
    private readonly Func<IX11HotkeyLoop> _loopFactory;
    private IX11HotkeyLoop? _loop;
    private KeyStroke? _registeredGesture;
    private int _primaryRegistered;
    private int _escapeRegistered;
    private long _primaryGeneration;
    private long _escapeGeneration;
    private bool _disposed;

    public LinuxX11GlobalHotkeyService()
        : this(CreateNativeLoop)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The XGrabKey adapter requires Linux.");
        }
    }

    internal LinuxX11GlobalHotkeyService(IX11HotkeyLoop loop)
        : this(() => loop)
    {
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _loop.HotkeyPressed += OnHotkeyPressed;
    }

    private LinuxX11GlobalHotkeyService(Func<IX11HotkeyLoop> loopFactory)
    {
        _loopFactory = loopFactory ?? throw new ArgumentNullException(nameof(loopFactory));
    }

    public event EventHandler? Pressed;

    public event EventHandler? EscapePressed;

    public KeyStroke? RegisteredGesture
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _registeredGesture;
            }
        }
    }

    public GlobalHotkeyRegistrationResult Register(KeyStroke gesture)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            UnregisterPrimary();
            if (!TryMapGesture(gesture, out var nativeGesture))
            {
                return Failure(
                    GlobalHotkeyRegistrationErrorCode.InvalidGesture,
                    "global_hotkey_invalid_gesture",
                    "The selected shortcut is not supported by the X11 hot-key adapter.");
            }

            var loopResult = TryGetLoop(escapeCapture: false);
            if (loopResult.Failure is not null)
            {
                return loopResult.Failure;
            }

            var nativeResult = loopResult.Loop!.Register(PrimaryHotkeyId, nativeGesture);
            if (!nativeResult.Succeeded)
            {
                return MapRegistrationFailure(nativeResult.ErrorCode, escapeCapture: false);
            }

            _registeredGesture = gesture;
            _ = Interlocked.Increment(ref _primaryGeneration);
            Volatile.Write(ref _primaryRegistered, 1);
            return new GlobalHotkeyRegistrationResult.Success(gesture);
        }
    }

    public void Unregister()
    {
        lock (_lifecycleGate)
        {
            UnregisterPrimary();
        }
    }

    public GlobalHotkeyRegistrationResult BeginEscapeCapture()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EndEscapeCaptureCore();
            var loopResult = TryGetLoop(escapeCapture: true);
            if (loopResult.Failure is not null)
            {
                return loopResult.Failure;
            }

            var nativeResult = loopResult.Loop!.Register(
                EscapeHotkeyId,
                new X11HotkeyGesture(EscapeKeySymbol, 0));
            if (!nativeResult.Succeeded)
            {
                return MapRegistrationFailure(nativeResult.ErrorCode, escapeCapture: true);
            }

            _ = Interlocked.Increment(ref _escapeGeneration);
            Volatile.Write(ref _escapeRegistered, 1);
            return new GlobalHotkeyRegistrationResult.Success(new KeyStroke("ESCAPE"));
        }
    }

    public void EndEscapeCapture()
    {
        lock (_lifecycleGate)
        {
            EndEscapeCaptureCore();
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            EndEscapeCaptureCore();
            UnregisterPrimary();
            if (_loop is not null)
            {
                _loop.HotkeyPressed -= OnHotkeyPressed;
                _loop.Dispose();
                _loop = null;
            }
        }
    }

    internal static bool TryMapGesture(KeyStroke gesture, out X11HotkeyGesture nativeGesture)
    {
        nativeGesture = default;
        if (gesture.Modifiers == KeyModifiers.None
            || !TryMapKeySymbol(gesture.Key, out var keySymbol))
        {
            return false;
        }

        var modifiers = 0U;
        if ((gesture.Modifiers & KeyModifiers.Shift) != KeyModifiers.None)
        {
            modifiers |= ShiftMask;
        }

        if ((gesture.Modifiers & KeyModifiers.Control) != KeyModifiers.None)
        {
            modifiers |= ControlMask;
        }

        if ((gesture.Modifiers & KeyModifiers.Alt) != KeyModifiers.None)
        {
            modifiers |= AltMask;
        }

        if ((gesture.Modifiers & KeyModifiers.Meta) != KeyModifiers.None)
        {
            modifiers |= MetaMask;
        }

        nativeGesture = new X11HotkeyGesture(keySymbol, modifiers);
        return true;
    }

    private static IX11HotkeyLoop CreateNativeLoop() => new X11HotkeyMessageLoop();

    private static bool TryMapKeySymbol(string key, out nuint keySymbol)
    {
        keySymbol = key switch
        {
            "`" or "GRAVE" or "OEMTILDE" => GraveKeySymbol,
            "SPACE" => 0x0020,
            "PAGEUP" => 0xFF55,
            "PAGEDOWN" => 0xFF56,
            "END" => 0xFF57,
            "HOME" => 0xFF50,
            "LEFT" => 0xFF51,
            "UP" => 0xFF52,
            "RIGHT" => 0xFF53,
            "DOWN" => 0xFF54,
            "INSERT" => 0xFF63,
            "DELETE" => 0xFFFF,
            _ => 0,
        };
        if (keySymbol != 0)
        {
            return true;
        }

        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            keySymbol = char.IsAsciiLetter(key[0])
                ? char.ToLowerInvariant(key[0])
                : key[0];
            return true;
        }

        if (key.Length >= 2
            && key[0] == 'F'
            && int.TryParse(key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture, out var functionNumber) && functionNumber is >= 1 and <= 24)
        {
            keySymbol = F1KeySymbol + (nuint)(functionNumber - 1);
            return keySymbol <= F24KeySymbol;
        }

        return false;
    }

    private (IX11HotkeyLoop? Loop, GlobalHotkeyRegistrationResult? Failure) TryGetLoop(
        bool escapeCapture)
    {
        if (_loop is not null)
        {
            return (_loop, null);
        }

        try
        {
            _loop = _loopFactory();
            _loop.HotkeyPressed += OnHotkeyPressed;
            return (_loop, null);
        }
        catch (X11HotkeyConnectionException exception)
        {
            return (null, NativeFailure(exception.Message, escapeCapture));
        }
        catch (DllNotFoundException exception)
        {
            return (null, NativeFailure(exception.Message, escapeCapture));
        }
        catch (EntryPointNotFoundException exception)
        {
            return (null, NativeFailure(exception.Message, escapeCapture));
        }
    }

    private void UnregisterPrimary()
    {
        if (Interlocked.Exchange(ref _primaryRegistered, 0) != 0)
        {
            _ = Interlocked.Increment(ref _primaryGeneration);
            _loop!.Unregister(PrimaryHotkeyId);
        }

        _registeredGesture = null;
    }

    private void EndEscapeCaptureCore()
    {
        if (Interlocked.Exchange(ref _escapeRegistered, 0) != 0)
        {
            _ = Interlocked.Increment(ref _escapeGeneration);
            _loop!.Unregister(EscapeHotkeyId);
        }
    }

    private void OnHotkeyPressed(int id)
    {
        var generation = id switch
        {
            PrimaryHotkeyId when Volatile.Read(ref _primaryRegistered) != 0 =>
                Volatile.Read(ref _primaryGeneration),
            EscapeHotkeyId when Volatile.Read(ref _escapeRegistered) != 0 =>
                Volatile.Read(ref _escapeGeneration),
            _ => -1,
        };
        if (generation < 0)
        {
            return;
        }

        // Keep Xlib's event thread independent from callbacks that may synchronously change grabs.
        ThreadPool.QueueUserWorkItem(
            static state => state.Service.DispatchHotkey(state.Id, state.Generation),
            (Service: this, Id: id, Generation: generation),
            preferLocal: false);
    }

    private void DispatchHotkey(int id, long generation)
    {
        try
        {
            if (id == PrimaryHotkeyId
                && Volatile.Read(ref _primaryRegistered) != 0
                && Volatile.Read(ref _primaryGeneration) == generation)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            else if (id == EscapeHotkeyId
                && Volatile.Read(ref _escapeRegistered) != 0
                && Volatile.Read(ref _escapeGeneration) == generation)
            {
                EscapePressed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            Asura.Application.SecretSafeDiagnosticProjection.WriteTrace(
                "desktop.hotkey.x11-service-callback.failed",
                exception);
        }
    }

    private static GlobalHotkeyRegistrationResult MapRegistrationFailure(
        byte errorCode,
        bool escapeCapture) => errorCode switch
        {
            BadAccess => Failure(
                GlobalHotkeyRegistrationErrorCode.Conflict,
                escapeCapture ? "escape_capture_conflict" : "global_hotkey_conflict",
                escapeCapture
                    ? "Another X11 client already owns Escape while Quick Terminal is open."
                    : "Another X11 client already owns this global shortcut."),
            BadValue => Failure(
                GlobalHotkeyRegistrationErrorCode.InvalidGesture,
                escapeCapture ? "escape_capture_invalid" : "global_hotkey_invalid_gesture",
                escapeCapture
                    ? "X11 rejected transient Escape capture."
                    : "X11 rejected the selected global shortcut."),
            _ => NativeFailure($"X11 error {errorCode}", escapeCapture),
        };

    private static GlobalHotkeyRegistrationResult NativeFailure(
        string nativeDetail,
        bool escapeCapture) => Failure(
        GlobalHotkeyRegistrationErrorCode.NativeFailure,
        escapeCapture ? "escape_capture_native_failure" : "global_hotkey_native_failure",
        escapeCapture
            ? $"X11 could not capture Escape ({nativeDetail})."
            : $"X11 could not register the global shortcut ({nativeDetail}).");

    private static GlobalHotkeyRegistrationResult Failure(
        GlobalHotkeyRegistrationErrorCode code,
        string stableCode,
        string message) =>
        new GlobalHotkeyRegistrationResult.Failure(new(code, stableCode, message));
}
