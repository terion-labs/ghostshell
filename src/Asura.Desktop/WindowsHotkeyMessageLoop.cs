using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Asura.Application;

namespace Asura.Desktop;

/// <summary>
/// Owns a Win32 thread message queue so every RegisterHotKey call and WM_HOTKEY event stays
/// independent from Avalonia's native window handles and dispatcher lifecycle.
/// </summary>
internal sealed class WindowsHotkeyMessageLoop : IWindowsHotkeyLoop
{
    private const uint WindowMessageHotkey = 0x0312;
    private const uint WindowMessageRunWork = 0x8001;
    private const uint WindowMessageQuit = 0x0012;
    private const uint PeekMessageNoRemove = 0;

    private readonly ConcurrentQueue<WorkItem> _workItems = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private ExceptionDispatchInfo? _startupFailure;
    private uint _threadId;
    private int _disposed;

    public WindowsHotkeyMessageLoop()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The RegisterHotKey message loop requires Windows.");
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Asura global hot-key loop",
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
            _ready.Dispose();
            throw;
        }
    }

    public event Action<int>? HotkeyPressed;

    public WindowsHotkeyNativeResult Register(int id, WindowsHotkeyGesture gesture) => Invoke(() =>
    {
        if (RegisterHotKey(IntPtr.Zero, id, gesture.Modifiers, gesture.VirtualKey))
        {
            return WindowsHotkeyNativeResult.Success;
        }

        return WindowsHotkeyNativeResult.Failure(Marshal.GetLastPInvokeError());
    });

    public void Unregister(int id) => Invoke(() =>
    {
        _ = UnregisterHotKey(IntPtr.Zero, id);
        return true;
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_thread.IsAlive
            && !PostThreadMessageW(_threadId, WindowMessageQuit, UIntPtr.Zero, IntPtr.Zero))
        {
            SecretSafeDiagnosticProjection.WriteTrace(
                "desktop.hotkey.windows-loop-stop.failed",
                SecretSafeDiagnosticKind.Unexpected);
        }

        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
        {
            _thread.Join();
        }

        FailPendingWork(new ObjectDisposedException(nameof(WindowsHotkeyMessageLoop)));
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
        if (!PostThreadMessageW(_threadId, WindowMessageRunWork, UIntPtr.Zero, IntPtr.Zero))
        {
            workItem.Fail(new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return (T)workItem.GetResult()!;
    }

    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            _ = PeekMessageW(
                out _,
                IntPtr.Zero,
                0,
                0,
                PeekMessageNoRemove);
        }
        catch (Exception exception)
        {
            _startupFailure = ExceptionDispatchInfo.Capture(exception);
            _ready.Set();
            return;
        }

        _ready.Set();
        while (true)
        {
            var status = GetMessageW(out var message, IntPtr.Zero, 0, 0);
            if (status == 0)
            {
                break;
            }

            if (status < 0)
            {
                FailPendingWork(new Win32Exception(Marshal.GetLastPInvokeError()));
                break;
            }

            if (message.Message == WindowMessageRunWork)
            {
                DrainWork();
            }
            else if (message.Message == WindowMessageHotkey)
            {
                try
                {
                    HotkeyPressed?.Invoke(unchecked((int)message.WParam));
                }
                catch (Exception exception)
                {
                    Asura.Application.SecretSafeDiagnosticProjection.WriteTrace(
                        "desktop.hotkey.windows-callback.failed",
                        exception);
                }
            }
        }

        FailPendingWork(new ObjectDisposedException(nameof(WindowsHotkeyMessageLoop)));
    }

    private void DrainWork()
    {
        while (_workItems.TryDequeue(out var workItem))
        {
            workItem.Execute();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr window,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(
        uint threadId,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
