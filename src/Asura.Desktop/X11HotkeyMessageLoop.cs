using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Asura.Desktop;

/// <summary>
/// Owns one Xlib connection on one thread. XGrabKey errors are forced synchronously so BadAccess
/// can be reported as a conflict instead of surfacing later through Avalonia's X11 connection.
/// </summary>
internal sealed class X11HotkeyMessageLoop : IX11HotkeyLoop
{
    private const string X11Library = "libX11.so.6";
    private const int KeyPress = 2;
    private const byte BadValue = 2;
    private const uint LockMask = 1U << 1;
    private const nuint NumLockKeySymbol = 0xFF7F;
    private const int GrabModeAsync = 1;
    private const int PollIntervalMilliseconds = 15;
    private const uint ModifierMask = 0xFF;

    private static readonly object XErrorHandlerGate = new();
    private static readonly XErrorHandlerDelegate ErrorHandler = CaptureXError;
    private static readonly IntPtr ErrorHandlerPointer =
        Marshal.GetFunctionPointerForDelegate(ErrorHandler);

    private static XErrorCaptureContext? s_errorCapture;

    private readonly ConcurrentQueue<WorkItem> _workItems = new();
    private readonly AutoResetEvent _workAvailable = new(initialState: false);
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<int, Registration> _registrations = [];
    private readonly Thread _thread;
    private ExceptionDispatchInfo? _startupFailure;
    private IntPtr _display;
    private nuint _rootWindow;
    private uint _numLockMask;
    private int _disposed;

    public X11HotkeyMessageLoop()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The XGrabKey loop requires Linux.");
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Asura X11 global hot-key loop",
        };
        _thread.Start();
        _ready.Wait();
        try
        {
            _startupFailure?.Throw();
        }
        catch
        {
            _thread.Join();
            _workAvailable.Dispose();
            _ready.Dispose();
            throw;
        }
    }

    public event Action<int>? HotkeyPressed;

    public X11HotkeyNativeResult Register(int id, X11HotkeyGesture gesture) => Invoke(() =>
    {
        UnregisterCore(id);
        var keyCode = XKeysymToKeycode(_display, gesture.KeySymbol);
        if (keyCode == 0)
        {
            return X11HotkeyNativeResult.Failure(BadValue);
        }

        var ignoredModifierVariants = BuildIgnoredModifierVariants(_numLockMask);
        var errorCode = CaptureErrors(_display, () =>
        {
            foreach (var ignoredModifiers in ignoredModifierVariants)
            {
                XGrabKey(
                    _display,
                    keyCode,
                    gesture.Modifiers | ignoredModifiers,
                    _rootWindow,
                    ownerEvents: false,
                    GrabModeAsync,
                    GrabModeAsync);
            }
        });

        if (errorCode != 0)
        {
            foreach (var ignoredModifiers in ignoredModifierVariants)
            {
                XUngrabKey(
                    _display,
                    keyCode,
                    gesture.Modifiers | ignoredModifiers,
                    _rootWindow);
            }

            _ = XSync(_display, discard: false);
            return X11HotkeyNativeResult.Failure(errorCode);
        }

        _registrations.Add(id, new Registration(
            keyCode,
            gesture.Modifiers,
            ignoredModifierVariants));
        return X11HotkeyNativeResult.Success;
    });

    public void Unregister(int id) => Invoke(() =>
    {
        UnregisterCore(id);
        return true;
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _workAvailable.Set();
        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join();
        }

        FailPendingWork(new ObjectDisposedException(nameof(X11HotkeyMessageLoop)));
        _workAvailable.Dispose();
        _ready.Dispose();
    }

    private T Invoke<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            return action();
        }

        using var workItem = new WorkItem(() => action());
        _workItems.Enqueue(workItem);
        _workAvailable.Set();
        return (T)workItem.GetResult()!;
    }

    private void Run()
    {
        try
        {
            _display = XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                throw new X11HotkeyConnectionException(
                    "XOpenDisplay could not connect to DISPLAY");
            }

            _rootWindow = XDefaultRootWindow(_display);
            _numLockMask = ReadNumLockMask();
        }
        catch (Exception exception)
        {
            _startupFailure = ExceptionDispatchInfo.Capture(exception);
            if (_display != IntPtr.Zero)
            {
                _ = XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }

            _ready.Set();
            return;
        }

        _ready.Set();
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                DrainWork();
                DrainX11Events();
                _workAvailable.WaitOne(PollIntervalMilliseconds);
            }

            DrainWork();
            foreach (var id in _registrations.Keys.ToArray())
            {
                UnregisterCore(id);
            }
        }
        finally
        {
            if (_display != IntPtr.Zero)
            {
                _ = XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }
        }
    }

    private void DrainWork()
    {
        while (_workItems.TryDequeue(out var workItem))
        {
            workItem.Execute();
        }
    }

    private void DrainX11Events()
    {
        while (XPending(_display) > 0)
        {
            _ = XNextEvent(_display, out var nativeEvent);
            if (nativeEvent.Type != KeyPress)
            {
                continue;
            }

            var keyEvent = nativeEvent.Key;
            foreach (var (id, registration) in _registrations)
            {
                var modifiers = keyEvent.State & ModifierMask & ~LockMask & ~_numLockMask;
                if (registration.KeyCode != keyEvent.KeyCode
                    || registration.Modifiers != modifiers)
                {
                    continue;
                }

                try
                {
                    HotkeyPressed?.Invoke(id);
                }
                catch (Exception exception)
                {
                    Asura.Application.SecretSafeDiagnosticProjection.WriteTrace(
                        "desktop.hotkey.x11-callback.failed",
                        exception);
                }

                break;
            }
        }
    }

    private void UnregisterCore(int id)
    {
        if (!_registrations.Remove(id, out var registration))
        {
            return;
        }

        foreach (var ignoredModifiers in registration.IgnoredModifierVariants)
        {
            XUngrabKey(
                _display,
                registration.KeyCode,
                registration.Modifiers | ignoredModifiers,
                _rootWindow);
        }

        _ = XSync(_display, discard: false);
    }

    private uint ReadNumLockMask()
    {
        var numLockKeyCode = XKeysymToKeycode(_display, NumLockKeySymbol);
        if (numLockKeyCode == 0)
        {
            return 0;
        }

        var modifierMapPointer = XGetModifierMapping(_display);
        if (modifierMapPointer == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var modifierMap = Marshal.PtrToStructure<XModifierKeymap>(modifierMapPointer);
            for (var modifierIndex = 0; modifierIndex < 8; modifierIndex++)
            {
                for (var keyIndex = 0; keyIndex < modifierMap.MaxKeysPerModifier; keyIndex++)
                {
                    var offset = (modifierIndex * modifierMap.MaxKeysPerModifier) + keyIndex;
                    if (Marshal.ReadByte(modifierMap.ModifierMap, offset) == numLockKeyCode)
                    {
                        return 1U << modifierIndex;
                    }
                }
            }

            return 0;
        }
        finally
        {
            _ = XFreeModifiermap(modifierMapPointer);
        }
    }

    private static uint[] BuildIgnoredModifierVariants(uint numLockMask) =>
        [.. new[] { 0U, LockMask, numLockMask, LockMask | numLockMask }.Distinct()];

    private static int CaptureXError(IntPtr display, ref XErrorEvent errorEvent)
    {
        var captureContext = Volatile.Read(ref s_errorCapture);
        if (captureContext is null)
        {
            Environment.FailFast(
                $"Xlib invoked Asura's temporary error handler without an active capture context (X11 error {errorEvent.ErrorCode}).");
        }

        var forwardedEvent = errorEvent;
        return RouteError(
            display,
            captureContext.OwnedDisplay,
            errorEvent.ErrorCode,
            captureContext.Capture,
            () => ForwardToPreviousHandler(
                captureContext.PreviousHandler,
                display,
                forwardedEvent));
    }

    internal static int RouteError(
        IntPtr errorDisplay,
        IntPtr ownedDisplay,
        byte errorCode,
        Action<byte> capture,
        Func<int> forward)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(forward);
        if (errorDisplay != ownedDisplay)
        {
            return forward();
        }

        capture(errorCode);
        return 0;
    }

    internal static byte CaptureErrors(IntPtr display, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (display == IntPtr.Zero)
        {
            throw new ArgumentException("An open X11 display is required.", nameof(display));
        }

        lock (XErrorHandlerGate)
        {
            _ = XSync(display, discard: false);
            byte capturedError = 0;
            var captureContext = new XErrorCaptureContext(
                display,
                errorCode => capturedError = capturedError == 0
                    ? errorCode
                    : capturedError);
            var previousHandler = XSetErrorHandler(ErrorHandlerPointer);
            try
            {
                captureContext.PreviousHandler = previousHandler;
                Volatile.Write(ref s_errorCapture, captureContext);
                try
                {
                    action();
                }
                finally
                {
                    // X errors are asynchronous. Flush every request while our temporary
                    // handler is still installed, including when the managed action fails.
                    _ = XSync(display, discard: false);
                }

                return capturedError;
            }
            finally
            {
                _ = XSetErrorHandler(previousHandler);
                // Keep the previous-handler pointer available for a callback that another Xlib
                // thread entered just before restoration. Deactivation drops the loop reference.
                captureContext.Deactivate();
            }
        }
    }

    private static int ForwardToPreviousHandler(
        IntPtr previousHandler,
        IntPtr display,
        XErrorEvent errorEvent)
    {
        if (previousHandler == IntPtr.Zero || previousHandler == ErrorHandlerPointer)
        {
            Environment.FailFast(
                $"An unrelated X11 error {errorEvent.ErrorCode} occurred while no callable previous Xlib error handler was installed.");
        }

        try
        {
            var handler = Marshal.GetDelegateForFunctionPointer<XErrorHandlerDelegate>(
                previousHandler);
            return handler(display, ref errorEvent);
        }
        catch (Exception exception)
        {
            Environment.FailFast(
                "The previous Xlib error handler failed while Asura was synchronously checking XGrabKey.",
                exception);
            return 0;
        }
    }

    private void FailPendingWork(Exception exception)
    {
        while (_workItems.TryDequeue(out var workItem))
        {
            workItem.Fail(exception);
        }
    }

    private sealed class WorkItem(Func<object?> action) : IDisposable
    {
        private readonly ManualResetEventSlim _completed = new();
        private ExceptionDispatchInfo? _failure;
        private object? _result;
        private int _claimed;

        public void Execute()
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
            {
                return;
            }

            try
            {
                _result = action();
            }
            catch (Exception exception)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                _completed.Set();
            }
        }

        public void Fail(Exception exception)
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
            {
                return;
            }

            _failure = ExceptionDispatchInfo.Capture(exception);
            _completed.Set();
        }

        public object? GetResult()
        {
            _completed.Wait();
            _failure?.Throw();
            return _result;
        }

        public void Dispose() => _completed.Dispose();
    }

    private sealed record Registration(
        uint KeyCode,
        uint Modifiers,
        IReadOnlyList<uint> IgnoredModifierVariants);

    private sealed class XErrorCaptureContext(
        IntPtr ownedDisplay,
        Action<byte> capture)
    {
        private Action<byte>? _capture = capture;

        public IntPtr OwnedDisplay { get; } = ownedDisplay;

        public IntPtr PreviousHandler { get; set; }

        public void Capture(byte errorCode) => Volatile.Read(ref _capture)?.Invoke(errorCode);

        public void Deactivate() => Volatile.Write(ref _capture, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XModifierKeymap
    {
        public int MaxKeysPerModifier;
        public IntPtr ModifierMap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XErrorEvent
    {
        public int Type;
        public IntPtr Display;
        public nuint ResourceId;
        public nuint Serial;
        public byte ErrorCode;
        public byte RequestCode;
        public byte MinorCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XKeyEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public IntPtr Display;
        public nuint Window;
        public nuint Root;
        public nuint Subwindow;
        public nuint Time;
        public int X;
        public int Y;
        public int RootX;
        public int RootY;
        public uint State;
        public uint KeyCode;
        public int SameScreen;
    }

    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private readonly struct XEvent
    {
        [FieldOffset(0)]
        public readonly int Type;

        [FieldOffset(0)]
        public readonly XKeyEvent Key;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandlerDelegate(IntPtr display, ref XErrorEvent errorEvent);

    [DllImport(X11Library)]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(X11Library)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(X11Library)]
    private static extern nuint XDefaultRootWindow(IntPtr display);

    [DllImport(X11Library)]
    private static extern uint XKeysymToKeycode(IntPtr display, nuint keySymbol);

    [DllImport(X11Library)]
    private static extern void XGrabKey(
        IntPtr display,
        uint keyCode,
        uint modifiers,
        nuint grabWindow,
        [MarshalAs(UnmanagedType.Bool)] bool ownerEvents,
        int pointerMode,
        int keyboardMode);

    [DllImport(X11Library)]
    private static extern void XUngrabKey(
        IntPtr display,
        uint keyCode,
        uint modifiers,
        nuint grabWindow);

    [DllImport(X11Library)]
    private static extern int XSync(
        IntPtr display,
        [MarshalAs(UnmanagedType.Bool)] bool discard);

    [DllImport(X11Library)]
    private static extern int XPending(IntPtr display);

    [DllImport(X11Library)]
    private static extern int XNextEvent(IntPtr display, out XEvent nativeEvent);

    [DllImport(X11Library)]
    private static extern IntPtr XSetErrorHandler(IntPtr handler);

    [DllImport(X11Library)]
    private static extern IntPtr XGetModifierMapping(IntPtr display);

    [DllImport(X11Library)]
    private static extern int XFreeModifiermap(IntPtr modifierMap);
}
