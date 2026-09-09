using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Asura.Desktop;

/// <summary>
/// Owns Objective-C completion blocks passed to asynchronous framework APIs.
/// The block runtime keeps its own reference while the framework is using a
/// block; this object keeps the caller's reference until managed completion.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOsObjectiveCBlock : IDisposable
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private static readonly nint StackBlockClass = GetStackBlockClass();
    private static readonly nint Descriptor = CreateDescriptor();
    private static readonly ConcurrentDictionary<nint, Action<bool, nint>>
        AuthorizationHandlers = new();
    private static readonly ConcurrentDictionary<nint, Action<nint>>
        ErrorHandlers = new();
    private static readonly AuthorizationInvoke AuthorizationCallback =
        OnAuthorizationCompleted;
    private static readonly ErrorInvoke ErrorCallback = OnOperationCompleted;

    private readonly Action<nint> _removeHandler;
    private nint _pointer;

    private MacOsObjectiveCBlock(nint pointer, Action<nint> removeHandler)
    {
        _pointer = pointer;
        _removeHandler = removeHandler;
    }

    public nint Pointer => _pointer;

    public static MacOsObjectiveCBlock CreateAuthorization(
        Action<bool, nint> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        var pointer = CreateBlock(AuthorizationCallback);
        AuthorizationHandlers[pointer] = completion;
        return new MacOsObjectiveCBlock(
            pointer,
            static block => AuthorizationHandlers.TryRemove(block, out _));
    }

    public static MacOsObjectiveCBlock CreateError(Action<nint> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        var pointer = CreateBlock(ErrorCallback);
        ErrorHandlers[pointer] = completion;
        return new MacOsObjectiveCBlock(
            pointer,
            static block => ErrorHandlers.TryRemove(block, out _));
    }

    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer == 0)
        {
            return;
        }

        _removeHandler(pointer);
        BlockRelease(pointer);
    }

    private static nint CreateBlock<TCallback>(TCallback callback)
        where TCallback : Delegate
    {
        var literal = new BlockLiteral
        {
            Isa = StackBlockClass,
            Invoke = Marshal.GetFunctionPointerForDelegate(callback),
            Descriptor = Descriptor,
        };
        var stackPointer = Marshal.AllocHGlobal(Marshal.SizeOf<BlockLiteral>());
        try
        {
            Marshal.StructureToPtr(literal, stackPointer, false);
            var pointer = BlockCopy(stackPointer);
            return pointer != 0
                ? pointer
                : throw new InvalidOperationException(
                    "The Objective-C block runtime could not copy a completion block.");
        }
        finally
        {
            Marshal.FreeHGlobal(stackPointer);
        }
    }

    private static nint GetStackBlockClass()
    {
        var library = NativeLibrary.Load(LibSystem);
        return NativeLibrary.GetExport(library, "_NSConcreteStackBlock");
    }

    private static nint CreateDescriptor()
    {
        var descriptor = new BlockDescriptor
        {
            Size = (nuint)Marshal.SizeOf<BlockLiteral>(),
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<BlockDescriptor>());
        Marshal.StructureToPtr(descriptor, pointer, false);
        return pointer;
    }

    private static void OnAuthorizationCompleted(
        nint block,
        [MarshalAs(UnmanagedType.I1)] bool granted,
        nint error)
    {
        if (AuthorizationHandlers.TryGetValue(block, out var completion))
        {
            completion(granted, error);
        }
    }

    private static void OnOperationCompleted(nint block, nint error)
    {
        if (ErrorHandlers.TryGetValue(block, out var completion))
        {
            completion(error);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AuthorizationInvoke(
        nint block,
        [MarshalAs(UnmanagedType.I1)] bool granted,
        nint error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ErrorInvoke(nint block, nint error);

    [DllImport(LibSystem, EntryPoint = "_Block_release")]
    private static extern void BlockRelease(nint block);

    [DllImport(LibSystem, EntryPoint = "_Block_copy")]
    private static extern nint BlockCopy(nint block);
}
