using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// Touch ID through LocalAuthentication, driven over the Objective-C runtime
/// directly — no binding library, just <c>objc_msgSend</c> and one hand-built
/// block for the asynchronous reply.
///
/// A dev build cannot use the biometry-gated keychain (that needs a signed
/// bundle with entitlements), so this authenticates the person and nothing
/// more: the system draws the prompt, the process keeps its keys.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsBiometricAuthenticator : IBiometricAuthenticator
{
    // LAPolicyDeviceOwnerAuthenticationWithBiometrics: strictly the sensor,
    // never the account password — the lock screen's PIN box is already the
    // knowledge factor here.
    private const long BiometryPolicy = 1;

    private static readonly bool FrameworkLoaded = LoadFramework();

    public bool IsAvailable
    {
        get
        {
            if (!FrameworkLoaded)
            {
                return false;
            }

            var context = NewContext();
            try
            {
                return ObjC.SendBoolLongOutPtr(
                    context,
                    Selectors.CanEvaluatePolicy,
                    BiometryPolicy,
                    out _);
            }
            finally
            {
                ObjC.Send(context, Selectors.Release);
            }
        }
    }

    public string MethodName => "Touch ID";

    public Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!IsAvailable)
        {
            return Task.FromResult(false);
        }

        return EvaluateAsync(BiometryPolicy, reason, cancellationToken);
    }

    /// <summary>
    /// Test seam: evaluating on an already-invalidated context makes the
    /// framework answer the reply block immediately with LAErrorInvalidContext
    /// — the whole hand-built block ABI runs without any UI existing.
    /// </summary>
    internal static Task<bool> EvaluateInvalidatedContextForTesting() =>
        EvaluateAsync(
            BiometryPolicy,
            "block round-trip probe",
            CancellationToken.None,
            invalidateFirst: true);

    private static Task<bool> EvaluateAsync(
        long policy,
        string reason,
        CancellationToken cancellationToken,
        bool invalidateFirst = false)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        if (context == IntPtr.Zero)
        {
            // Messaging nil is silent by design in Objective-C, which here
            // would read as a prompt that never answers. A missing class is
            // an answer: no.
            return Task.FromResult(false);
        }

        if (invalidateFirst)
        {
            ObjC.Send(context, Selectors.Invalidate);
        }

        var reasonString = ObjC.CreateString(reason);
        unsafe
        {
            var block = ReplyBlock.Create(context, completion);
            ObjC.SendVoidLongPtrPtr(
                context,
                Selectors.EvaluatePolicy,
                policy,
                reasonString,
                (IntPtr)block);
            // The framework copied the block; the original literal is done.
            ReplyBlock.Free(block);
        }

        var cancellation = cancellationToken.Register(() =>
        {
            // Invalidation dismisses the sheet; the reply block then answers
            // false through the ordinary path.
            ObjC.Send(context, Selectors.Invalidate);
        });
        _ = completion.Task.ContinueWith(
            _ =>
            {
                // Disposing first waits out any in-flight cancellation
                // callback, so the release below is the last touch.
                cancellation.Dispose();
                ObjC.Send(context, Selectors.Release);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion.Task;
    }

    private static IntPtr NewContext() =>
        // The field read is the load: without it, a path that skips
        // IsAvailable would ask the runtime for a class of a framework
        // nobody loaded, get nil, and message nil forever after.
        !FrameworkLoaded
            ? IntPtr.Zero
            : ObjC.Send(
                ObjC.Send(ObjC.GetClass("LAContext"), Selectors.Alloc),
                Selectors.Init);

    private static bool LoadFramework() =>
        OperatingSystem.IsMacOS() && ObjC.LoadLibrary(
                "/System/Library/Frameworks/LocalAuthentication.framework/LocalAuthentication")
                != IntPtr.Zero;

    private static class Selectors
    {
        public static readonly IntPtr Alloc = ObjC.Selector("alloc");
        public static readonly IntPtr Init = ObjC.Selector("init");
        public static readonly IntPtr Release = ObjC.Selector("release");
        public static readonly IntPtr Invalidate = ObjC.Selector("invalidate");
        public static readonly IntPtr CanEvaluatePolicy =
            ObjC.Selector("canEvaluatePolicy:error:");
        public static readonly IntPtr EvaluatePolicy =
            ObjC.Selector("evaluatePolicy:localizedReason:reply:");
    }

    /// <summary>
    /// The Objective-C block passed as the reply callback, laid out by hand:
    /// header, invoke pointer, descriptor, and one captured GC handle. The
    /// framework's <c>Block_copy</c> performs a plain byte copy — nothing
    /// captured here needs a copy helper — and the handle is freed by the
    /// invoke itself, exactly once.
    /// </summary>
    private static unsafe class ReplyBlock
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Literal
        {
            public IntPtr Isa;
            public int Flags;
            public int Reserved;
            public IntPtr Invoke;
            public IntPtr Descriptor;
            public IntPtr Context;
            public IntPtr GcHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Descriptor
        {
            public ulong Reserved;
            public ulong Size;

            /// <summary>
            /// The Objective-C type encoding of the reply. Present because the
            /// reply crosses XPC to coreauthd, and blocks without a signature
            /// are not invocable across that boundary — they are silently
            /// dropped, which reads as a reply that never comes.
            /// </summary>
            public IntPtr Signature;
        }

        private static readonly IntPtr StackBlockIsa =
            ObjC.LoadSymbol("_NSConcreteStackBlock");

        /// <summary>BLOCK_HAS_SIGNATURE: the descriptor carries the encoding.</summary>
        private const int BlockHasSignature = 1 << 30;

        private static readonly IntPtr DescriptorStorage = CreateDescriptor();

        private static IntPtr CreateDescriptor()
        {
            // void (^)(BOOL, NSError*): void return; the block itself at 0,
            // BOOL at 8, the error object at 16; 24 bytes of arguments.
            var encoding = "v24@?0B8@16"u8;
            var signature = (byte*)NativeMemory.AllocZeroed((nuint)(encoding.Length + 1));
            encoding.CopyTo(new Span<byte>(signature, encoding.Length));
            var descriptor = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
            descriptor->Size = (ulong)sizeof(Literal);
            descriptor->Signature = (IntPtr)signature;
            return (IntPtr)descriptor;
        }

        public static Literal* Create(IntPtr context, TaskCompletionSource<bool> completion)
        {
            var literal = (Literal*)NativeMemory.AllocZeroed((nuint)sizeof(Literal));
            literal->Isa = StackBlockIsa;
            literal->Flags = BlockHasSignature;
            literal->Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<Literal*, byte, IntPtr, void>)&Invoke;
            literal->Descriptor = DescriptorStorage;
            literal->Context = context;
            literal->GcHandle = GCHandle.ToIntPtr(GCHandle.Alloc(completion));
            return literal;
        }

        public static void Free(Literal* literal) => NativeMemory.Free(literal);

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static void Invoke(Literal* block, byte success, IntPtr error)
        {
            _ = error;
            var handle = GCHandle.FromIntPtr(block->GcHandle);
            try
            {
                if (handle.Target is TaskCompletionSource<bool> completion)
                {
                    completion.TrySetResult(success != 0);
                }
            }
            finally
            {
                // The context is released by the caller's continuation, not
                // here: a cancellation registration may still hold it.
                handle.Free();
            }
        }
    }

    /// <summary>The few Objective-C runtime entry points this file speaks.</summary>
    private static class ObjC
    {
        private const string Runtime = "/usr/lib/libobjc.dylib";
        private const string System = "/usr/lib/libSystem.dylib";
        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(Runtime, EntryPoint = "objc_getClass")]
        public static extern IntPtr GetClass(string name);

        [DllImport(Runtime, EntryPoint = "sel_registerName")]
        public static extern IntPtr Selector(string name);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern bool SendBoolLongOutPtr(
            IntPtr receiver,
            IntPtr selector,
            long value,
            out IntPtr error);

        [DllImport(Runtime, EntryPoint = "objc_msgSend")]
        public static extern void SendVoidLongPtrPtr(
            IntPtr receiver,
            IntPtr selector,
            long value,
            IntPtr first,
            IntPtr second);

        [DllImport(System, EntryPoint = "dlopen")]
        private static extern IntPtr DlOpen(string path, int mode);

        [DllImport(System, EntryPoint = "dlsym")]
        private static extern IntPtr DlSym(IntPtr handle, string symbol);

        /// <summary>dlsym's RTLD_DEFAULT: search every loaded image.</summary>
        private static readonly IntPtr GlobalScope = (IntPtr)(-2);

        public static IntPtr LoadLibrary(string path) => DlOpen(path, 0x1 /* RTLD_LAZY */);

        public static IntPtr LoadSymbol(string name) => DlSym(GlobalScope, name);

        [DllImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString")]
        private static extern IntPtr CFStringCreate(
            IntPtr allocator,
            string value,
            uint encoding);

        /// <summary>A toll-free-bridged NSString the callee may retain.</summary>
        public static IntPtr CreateString(string value) =>
            CFStringCreate(IntPtr.Zero, value, 0x08000100 /* UTF8 */);
    }
}
