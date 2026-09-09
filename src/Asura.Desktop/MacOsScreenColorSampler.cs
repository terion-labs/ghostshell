using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Asura.App;
using Avalonia.Media;

namespace Asura.Desktop;

/// <summary>
/// Screen colour picking through AppKit's own <c>NSColorSampler</c>.
///
/// This is the system loupe the user already knows from the macOS colour panel.
/// It is deliberately used in preference to capturing the screen ourselves:
/// <c>CGDisplayCreateImage</c> needs the screen-recording permission and returns
/// nothing useful without it, whereas the sampler is mediated by the system and
/// needs no permission at all.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOsScreenColorSampler : IScreenColorSampler
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string AppKitFramework =
        "/System/Library/Frameworks/AppKit.framework/AppKit";

    /// <summary>Marks a block that lives for the program's lifetime.</summary>
    private const int BlockIsGlobal = 1 << 28;

    /// <summary>
    /// AppKit must be loaded before its classes exist in the Objective-C runtime.
    /// The host has normally loaded it already, but looking it up first makes the
    /// lookups below independent of when this type happens to be initialised.
    /// </summary>
    private static readonly IntPtr AppKitHandle = LoadAppKit();

    private static readonly IntPtr ColorSamplerClass = objc_getClass("NSColorSampler");
    private static readonly IntPtr ColorSpaceClass = objc_getClass("NSColorSpace");

    private static IntPtr LoadAppKit() =>
        NativeLibrary.TryLoad(AppKitFramework, out var handle) ? handle : IntPtr.Zero;

    public bool IsAvailable => OperatingSystem.IsMacOSVersionAtLeast(10, 15)
        && AppKitHandle != IntPtr.Zero
        && ColorSamplerClass != IntPtr.Zero
        && ColorSpaceClass != IntPtr.Zero;

    public ValueTask<Color?> SampleAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return ValueTask.FromResult<Color?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<Color?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return new ValueTask<Color?>(ShowSamplerAsync(completion, cancellationToken));
    }

    private static async Task<Color?> ShowSamplerAsync(
        TaskCompletionSource<Color?> completion,
        CancellationToken cancellationToken)
    {
        // The handler is kept alive for as long as the sampler can call it; the
        // block itself is a global block, so AppKit never copies or frees it.
        var handle = default(GCHandle);
        SelectionHandler handler = colorPointer =>
        {
            try
            {
                completion.TrySetResult(ToColor(colorPointer));
            }
            finally
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
        };
        handle = GCHandle.Alloc(handler);

        var descriptor = new BlockDescriptor
        {
            Reserved = 0,
            Size = (nuint)Marshal.SizeOf<BlockLiteral>(),
        };
        var descriptorPointer = Marshal.AllocHGlobal(Marshal.SizeOf<BlockDescriptor>());
        Marshal.StructureToPtr(descriptor, descriptorPointer, false);

        var block = new BlockLiteral
        {
            Isa = GetGlobalBlockClass(),
            Flags = BlockIsGlobal,
            Reserved = 0,
            Invoke = Marshal.GetFunctionPointerForDelegate(InvokeHandler),
            Descriptor = descriptorPointer,
        };
        var blockPointer = Marshal.AllocHGlobal(Marshal.SizeOf<BlockLiteral>());
        Marshal.StructureToPtr(block, blockPointer, false);
        BlockHandlers[blockPointer] = handler;

        try
        {
            var sampler = objc_msgSend_retIntPtr(
                objc_msgSend_retIntPtr(ColorSamplerClass, GetSelector("alloc")),
                GetSelector("init"));
            objc_msgSend_void_IntPtr(
                sampler,
                GetSelector("showSamplerWithSelectionHandler:"),
                blockPointer);

            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(true);
        }
        finally
        {
            BlockHandlers.TryRemove(blockPointer, out _);
            Marshal.FreeHGlobal(blockPointer);
            Marshal.FreeHGlobal(descriptorPointer);
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    private delegate void SelectionHandler(IntPtr color);

    private delegate void BlockInvoke(IntPtr block, IntPtr color);

    private static readonly BlockInvoke InvokeHandler = OnSelection;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, SelectionHandler>
        BlockHandlers = new();

    private static void OnSelection(IntPtr block, IntPtr color)
    {
        if (BlockHandlers.TryGetValue(block, out var handler))
        {
            handler(color);
        }
    }

    /// <summary>
    /// Converts the sampled <c>NSColor</c> through sRGB, because a colour taken
    /// from a wide-gamut display has no meaningful RGB components until it is
    /// converted into the space the palette stores.
    /// </summary>
    private static Color? ToColor(IntPtr color)
    {
        if (color == IntPtr.Zero)
        {
            return null;
        }

        var srgb = objc_msgSend_retIntPtr(ColorSpaceClass, GetSelector("sRGBColorSpace"));
        var converted = objc_msgSend_retIntPtr_IntPtr(
            color,
            GetSelector("colorUsingColorSpace:"),
            srgb);
        if (converted == IntPtr.Zero)
        {
            return null;
        }

        var red = objc_msgSend_retDouble(converted, GetSelector("redComponent"));
        var green = objc_msgSend_retDouble(converted, GetSelector("greenComponent"));
        var blue = objc_msgSend_retDouble(converted, GetSelector("blueComponent"));
        return Color.FromRgb(ToByte(red), ToByte(green), ToByte(blue));
    }

    private static byte ToByte(double component) =>
        (byte)Math.Clamp(Math.Round(component * 255), 0, 255);

    private static IntPtr GetGlobalBlockClass() =>
        NativeLibrary.GetExport(AppKitHandle, "_NSConcreteGlobalBlock");

    private static IntPtr GetSelector(string name) => sel_registerName(name);

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [DllImport(ObjCLibrary)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLibrary)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_retIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_retIntPtr_IntPtr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern double objc_msgSend_retDouble(IntPtr receiver, IntPtr selector);
}
