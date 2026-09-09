using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Asura.Desktop;

[SupportedOSPlatform("macos10.12")]
internal sealed class MacOsHostAccessibilityPreferencesSource :
    HostAccessibilityPreferencesSource
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string AppKitLibrary =
        "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string ObserverClassName = "AsuraAccessibilityPreferenceObserver";
    private const string NotificationSymbol =
        "NSWorkspaceAccessibilityDisplayOptionsDidChangeNotification";

    private static readonly object NativeClassGate = new();
    private static readonly ConcurrentDictionary<nint, WeakReference<MacOsHostAccessibilityPreferencesSource>>
        Sources = new();
    private static readonly NotificationCallback Callback = OnNativeNotification;
    private static nint _observerClass;
    private static nint _appKitHandle;

    private nint _workspace;
    private nint _notificationCenter;
    private nint _observer;

    protected override void StartCore()
    {
        try
        {
            EnsureAppKitLoaded();
            var workspaceClass = objc_getClass("NSWorkspace");
            _workspace = SendObject(workspaceClass, Selector("sharedWorkspace"));
            _notificationCenter = SendObject(_workspace, Selector("notificationCenter"));
            _observer = SendObject(GetObserverClass(), Selector("new"));

            if (_workspace == 0 || _notificationCenter == 0 || _observer == 0)
            {
                DisposeNativeObserver();
                return;
            }

            Sources[_observer] = new WeakReference<MacOsHostAccessibilityPreferencesSource>(this);
            SendVoid(
                _notificationCenter,
                Selector("addObserver:selector:name:object:"),
                _observer,
                Selector("asuraAccessibilityPreferencesChanged:"),
                GetAccessibilityNotificationName(),
                0);
            Refresh();
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            DisposeNativeObserver();
        }
    }

    protected override void DisposeCore() => DisposeNativeObserver();

    private void Refresh()
    {
        var workspace = _workspace;
        if (workspace == 0 || IsDisposed)
        {
            return;
        }

        Publish(HostAccessibilityPreferenceMapping.FromMacOs(
            SendBoolean(
                workspace,
                Selector("accessibilityDisplayShouldReduceMotion")),
            SendBoolean(
                workspace,
                Selector("accessibilityDisplayShouldReduceTransparency"))));
    }

    private void DisposeNativeObserver()
    {
        var observer = Interlocked.Exchange(ref _observer, 0);
        var notificationCenter = Interlocked.Exchange(ref _notificationCenter, 0);
        _workspace = 0;
        if (observer == 0)
        {
            return;
        }

        Sources.TryRemove(observer, out _);
        if (notificationCenter != 0)
        {
            SendVoid(notificationCenter, Selector("removeObserver:"), observer);
        }

        SendVoid(observer, Selector("release"));
    }

    private static nint GetObserverClass()
    {
        lock (NativeClassGate)
        {
            if (_observerClass != 0)
            {
                return _observerClass;
            }

            _observerClass = objc_getClass(ObserverClassName);
            if (_observerClass != 0)
            {
                return _observerClass;
            }

            var superClass = objc_getClass("NSObject");
            var observerClass = objc_allocateClassPair(
                superClass,
                ObserverClassName,
                0);
            if (observerClass == 0)
            {
                throw new InvalidOperationException(
                    "Could not allocate the macOS accessibility observer class.");
            }

            var callbackPointer = Marshal.GetFunctionPointerForDelegate(Callback);
            if (!class_addMethod(
                    observerClass,
                    Selector("asuraAccessibilityPreferencesChanged:"),
                    callbackPointer,
                    "v@:@"))
            {
                objc_disposeClassPair(observerClass);
                throw new InvalidOperationException(
                    "Could not register the macOS accessibility observer callback.");
            }

            objc_registerClassPair(observerClass);
            _observerClass = observerClass;
            return observerClass;
        }
    }

    private static nint GetAccessibilityNotificationName()
    {
        lock (NativeClassGate)
        {
            EnsureAppKitLoaded();
            var symbol = NativeLibrary.GetExport(_appKitHandle, NotificationSymbol);
            return Marshal.ReadIntPtr(symbol);
        }
    }

    private static void EnsureAppKitLoaded()
    {
        lock (NativeClassGate)
        {
            if (_appKitHandle == 0)
            {
                _appKitHandle = NativeLibrary.Load(AppKitLibrary);
            }
        }
    }

    private static void OnNativeNotification(
        nint observer,
        nint selector,
        nint notification)
    {
        _ = selector;
        _ = notification;
        try
        {
            if (Sources.TryGetValue(observer, out var weakSource)
                && weakSource.TryGetTarget(out var source))
            {
                source.Refresh();
            }
        }
        catch (Exception)
        {
            // Managed exceptions must never cross an Objective-C callback boundary.
        }
    }

    private static nint Selector(string name) => sel_registerName(name);

    private static bool IsUnavailable(Exception exception) => exception is
        DllNotFoundException
        or EntryPointNotFoundException
        or BadImageFormatException
        or ExternalException
        or InvalidOperationException
        or TypeInitializationException;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotificationCallback(
        nint observer,
        nint selector,
        nint notification);

    [DllImport(ObjectiveCLibrary)]
    private static extern nint objc_getClass(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary)]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary)]
    private static extern nint objc_allocateClassPair(
        nint superClass,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        nuint extraBytes);

    [DllImport(ObjectiveCLibrary)]
    private static extern void objc_registerClassPair(nint targetClass);

    [DllImport(ObjectiveCLibrary)]
    private static extern void objc_disposeClassPair(nint targetClass);

    [DllImport(ObjectiveCLibrary)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(
        nint targetClass,
        nint selector,
        nint implementation,
        [MarshalAs(UnmanagedType.LPStr)] string types);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBoolean(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(
        nint receiver,
        nint selector,
        nint argument1,
        nint argument2,
        nint argument3,
        nint argument4);
}
