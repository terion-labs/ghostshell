using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Asura.Desktop;

internal sealed partial class MacOsUserNotificationCenter
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string UserNotificationsFramework =
        "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";
    private const string DelegateClassName = "AsuraUserNotificationCenterDelegate";
    private const nuint ForegroundPresentationOptions = 2 | 4 | 8 | 16;

    private static readonly object NativeClassGate = new();
    private static readonly ConcurrentDictionary<nint, WeakReference<MacOsUserNotificationCenter>>
        Centers = new();
    private static readonly NotificationResponseCallback ResponseCallback =
        OnNotificationResponse;
    private static readonly NotificationPresentationCallback PresentationCallback =
        OnNotificationPresentation;
    private static nint _delegateClass;
    private static nint _frameworkHandle;

    private static void OnNotificationResponse(
        nint nativeDelegate,
        nint selector,
        nint center,
        nint response,
        nint completion)
    {
        _ = selector;
        _ = center;
        try
        {
            if (Centers.TryGetValue(nativeDelegate, out var weakCenter)
                && weakCenter.TryGetTarget(out var notificationCenter))
            {
                var eventArgs = RunWithAutoreleasePool(
                    () => ReadActivation(response));
                if (eventArgs is not null)
                {
                    notificationCenter.PublishActivation(eventArgs);
                }
            }
        }
        catch (Exception exception)
        {
            ReportCallbackFailure(exception);
        }
        finally
        {
            try
            {
                RunWithAutoreleasePool(
                    () => InvokeCompletion(completion));
            }
            catch (Exception exception)
            {
                ReportCallbackFailure(exception);
            }
        }
    }

    private static void OnNotificationPresentation(
        nint nativeDelegate,
        nint selector,
        nint center,
        nint notification,
        nint completion)
    {
        _ = nativeDelegate;
        _ = selector;
        _ = center;
        _ = notification;
        try
        {
            RunWithAutoreleasePool(() =>
                InvokePresentationCompletion(
                    completion,
                    ForegroundPresentationOptions));
        }
        catch (Exception exception)
        {
            ReportCallbackFailure(exception);
        }
    }

    private static nint GetDelegateClass()
    {
        lock (NativeClassGate)
        {
            if (_delegateClass != 0)
            {
                return _delegateClass;
            }

            _delegateClass = objc_getClass(DelegateClassName);
            if (_delegateClass != 0)
            {
                return _delegateClass;
            }

            var delegateClass = objc_allocateClassPair(
                RequireClass("NSObject"),
                DelegateClassName,
                0);
            if (delegateClass == 0)
            {
                throw new InvalidOperationException(
                    "Could not allocate the macOS notification delegate class.");
            }

            try
            {
                AddDelegateMethod(
                    delegateClass,
                    "userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:",
                    ResponseCallback);
                AddDelegateMethod(
                    delegateClass,
                    "userNotificationCenter:willPresentNotification:withCompletionHandler:",
                    PresentationCallback);
                var protocol = objc_getProtocol("UNUserNotificationCenterDelegate");
                if (protocol != 0)
                {
                    _ = class_addProtocol(delegateClass, protocol);
                }

                objc_registerClassPair(delegateClass);
                _delegateClass = delegateClass;
                return delegateClass;
            }
            catch
            {
                objc_disposeClassPair(delegateClass);
                throw;
            }
        }
    }

    private static void AddDelegateMethod<TCallback>(
        nint delegateClass,
        string selector,
        TCallback callback)
        where TCallback : Delegate
    {
        if (!class_addMethod(
                delegateClass,
                Selector(selector),
                Marshal.GetFunctionPointerForDelegate(callback),
                "v@:@@@"))
        {
            throw new InvalidOperationException(
                $"Could not register the macOS notification callback '{selector}'.");
        }
    }

    private static void EnsureFrameworkLoaded()
    {
        lock (NativeClassGate)
        {
            if (_frameworkHandle == 0)
            {
                _frameworkHandle = NativeLibrary.Load(UserNotificationsFramework);
            }
        }
    }

    private static void RequireApplicationBundle()
    {
        var bundle = SendObject(
            RequireClass("NSBundle"),
            Selector("mainBundle"));
        var bundleIdentifier = ToManagedString(
            SendObject(bundle, Selector("bundleIdentifier")));
        if (string.IsNullOrWhiteSpace(bundleIdentifier))
        {
            // currentNotificationCenter raises an Objective-C exception outside
            // an application bundle. Rejecting the host first keeps that native
            // exception from terminating an otherwise healthy .NET process.
            throw new InvalidOperationException(
                "macOS notifications require a bundled application identity.");
        }
    }

    private static nint RequireClass(string name)
    {
        var nativeClass = objc_getClass(name);
        return nativeClass != 0
            ? nativeClass
            : throw new InvalidOperationException(
                $"The Objective-C class '{name}' is unavailable.");
    }

    private static nint Selector(string name) => sel_registerName(name);

    private static void RunWithAutoreleasePool(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = RunWithAutoreleasePool(() =>
        {
            action();
            return true;
        });
    }

    private static T RunWithAutoreleasePool<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var pool = SendObject(
            RequireClass("NSAutoreleasePool"),
            Selector("new"));
        if (pool == 0)
        {
            throw new InvalidOperationException(
                "Could not create a macOS autorelease pool.");
        }

        try
        {
            return action();
        }
        finally
        {
            SendVoid(pool, Selector("drain"));
        }
    }

    private static void Release(nint value)
    {
        if (value != 0)
        {
            SendVoid(value, Selector("release"));
        }
    }

    private static void InvokeCompletion(nint block)
    {
        if (block == 0)
        {
            return;
        }

        var literal = Marshal.PtrToStructure<BlockLiteral>(block);
        Marshal.GetDelegateForFunctionPointer<VoidCompletion>(literal.Invoke)(block);
    }

    private static void InvokePresentationCompletion(nint block, nuint options)
    {
        if (block == 0)
        {
            return;
        }

        var literal = Marshal.PtrToStructure<BlockLiteral>(block);
        Marshal.GetDelegateForFunctionPointer<PresentationCompletion>(literal.Invoke)(
            block,
            options);
    }

    private static void ReportCallbackFailure(Exception exception)
    {
        try
        {
            Asura.Application.SecretSafeDiagnosticProjection.WriteStandardError(
                "notifications.macos-callback.failed",
                exception);
        }
        catch
        {
            // A diagnostics sink must not let a managed exception cross an
            // unmanaged Objective-C callback boundary.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct BlockLiteral
    {
        public readonly nint Isa;
        public readonly int Flags;
        public readonly int Reserved;
        public readonly nint Invoke;
        public readonly nint Descriptor;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotificationResponseCallback(
        nint nativeDelegate,
        nint selector,
        nint center,
        nint response,
        nint completion);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotificationPresentationCallback(
        nint nativeDelegate,
        nint selector,
        nint center,
        nint notification,
        nint completion);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidCompletion(nint block);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PresentationCompletion(nint block, nuint options);

    [DllImport(ObjectiveCLibrary)]
    private static extern nint objc_getClass(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjectiveCLibrary)]
    private static extern nint objc_getProtocol(
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
    private static extern bool class_addProtocol(nint targetClass, nint protocol);

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
    private static extern nint SendObject(
        nint receiver,
        nint selector,
        nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(
        nint receiver,
        nint selector,
        nint argument1,
        nint argument2,
        nint argument3);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendObject(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(
        nint receiver,
        nint selector,
        nint argument1,
        nint argument2);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(
        nint receiver,
        nint selector,
        nuint argument1,
        nint argument2);
}
