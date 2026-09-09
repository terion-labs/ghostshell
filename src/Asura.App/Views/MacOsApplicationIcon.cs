using System.Runtime.InteropServices;
using System.Text;
using Asura.Core;

namespace Asura.App.Views;

/// <summary>
/// Supplies the Dock with the accent-aware rendition that macOS cannot encode
/// in a static asset catalog. Finder still receives the adaptive light, dark,
/// and tintable stacks from Assets.car; the running application can also use
/// the current system accent instead of baking one accent into those stacks.
/// </summary>
internal static class MacOsApplicationIcon
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string AppKitLibrary =
        "/System/Library/Frameworks/AppKit.framework/Versions/C/AppKit";
    private const string IconAppearancePreference = "AppleIconAppearanceTheme";
    private static nint _appKitHandle;
    private static RgbColor? _appliedAccent;
    private static bool? _appliedDark;

    public static bool TryApply(RgbColor accent)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(26))
        {
            return false;
        }

        if (_appKitHandle == 0
            && !NativeLibrary.TryLoad(AppKitLibrary, out _appKitHandle))
        {
            return false;
        }

        try
        {
            var dark = UsesDarkIcons();
            if (_appliedAccent == accent && _appliedDark == dark)
            {
                return true;
            }

            var svg = CreateSvg(accent, dark);
            var bytes = Encoding.UTF8.GetBytes(svg);
            var unmanagedBytes = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);
                var data = SendIdPointerLength(
                    objc_getClass("NSData"),
                    Selector("dataWithBytes:length:"),
                    unmanagedBytes,
                    (nuint)bytes.Length);
                var image = SendIdArgument(
                    SendId(objc_getClass("NSImage"), Selector("alloc")),
                    Selector("initWithData:"),
                    data);
                if (image == 0)
                {
                    return false;
                }

                try
                {
                    var application = SendId(
                        objc_getClass("NSApplication"),
                        Selector("sharedApplication"));
                    SendVoidIdArgument(
                        application,
                        Selector("setApplicationIconImage:"),
                        image);
                    _appliedAccent = accent;
                    _appliedDark = dark;
                    return true;
                }
                finally
                {
                    SendVoid(image, Selector("release"));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(unmanagedBytes);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            return false;
        }
    }

    internal static string CreateSvg(RgbColor accent, bool dark)
    {
        var background = dark ? "#000000" : accent.ToString();
        var mark = dark ? accent.ToString() : "#000000";
        return $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024" fill-rule="evenodd" clip-rule="evenodd" stroke-linejoin="round" stroke-miterlimit="2">
              <g transform="translate(86.71 86.71) scale(0.830645)">
                <rect x="16" y="16" width="992" height="992" rx="224" fill="{{background}}"/>
                <!-- Match the base asset's optical lift. The triangle's broad
                     base otherwise makes a geometrically centered mark look low. -->
                <g transform="translate(143.5 185) scale(1.416)" fill="{{mark}}">
                  <g transform="translate(-985.323 -598.176)">
                    <g transform="matrix(1.25124 0 0 1.00547 -583.894 9.76733)">
                      <path d="M1462.26 585.381L1670.14 1001.12L1254.39 1001.12L1462.26 585.381ZM1462.26 640.223L1299.53 971.126L1625 971.126L1462.26 640.223ZM1395.57 929.502L1386.59 947.854L1334.01 947.854L1343 929.502L1395.57 929.502Z"/>
                    </g>
                  </g>
                </g>
              </g>
            </svg>
            """;
    }

    private static bool UsesDarkIcons()
    {
        var defaults = SendId(
            objc_getClass("NSUserDefaults"),
            Selector("standardUserDefaults"));
        var key = SendIdUtf8Argument(
            objc_getClass("NSString"),
            Selector("stringWithUTF8String:"),
            IconAppearancePreference);
        var value = SendIdArgument(defaults, Selector("stringForKey:"), key);
        var utf8 = value == 0 ? 0 : SendId(value, Selector("UTF8String"));
        var theme = utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
        return theme?.Contains("Dark", StringComparison.OrdinalIgnoreCase) is true;
    }

    private static nint Selector(string name) => sel_registerName(name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendId(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdArgument(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdPointerLength(
        nint receiver,
        nint selector,
        nint bytes,
        nuint length);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdUtf8Argument(
        nint receiver,
        nint selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidIdArgument(nint receiver, nint selector, nint argument);
}
