using System.Runtime.InteropServices;

namespace Asura.App.Views;

/// <summary>
/// Returns focus to the application that was active before Quick Terminal.
/// Restoration occurs after the native window hides because AppKit otherwise
/// promotes Asura's main window after an earlier activation attempt.
/// </summary>
internal static class MacOsQuickTerminalFocus
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const nint ActivateIgnoringOtherApps = 1 << 1;
    private static nint _previousApplicationProcessId;

    public static void CaptureFrontmostApplication()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var application = FrontmostApplication();
        var processId = application == 0
            ? 0
            : SendNInt(application, Selector("processIdentifier"));
        _previousApplicationProcessId = processId > 0 && processId != Environment.ProcessId
            ? processId
            : 0;
    }

    public static bool TryRestoreFrontmostApplication()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var processId = Interlocked.Exchange(ref _previousApplicationProcessId, 0);
        if (processId <= 0)
        {
            return false;
        }

        var frontmost = FrontmostApplication();
        if (frontmost == 0
            || SendNInt(frontmost, Selector("processIdentifier")) != Environment.ProcessId)
        {
            // Focus already moved elsewhere; do not override the user's choice.
            return false;
        }

        var application = SendIdNIntArgument(
            objc_getClass("NSRunningApplication"),
            Selector("runningApplicationWithProcessIdentifier:"),
            processId);
        return application != 0
            && SendBoolNIntArgument(
                application,
                Selector("activateWithOptions:"),
                ActivateIgnoringOtherApps);
    }

    private static nint FrontmostApplication()
    {
        var workspace = SendId(objc_getClass("NSWorkspace"), Selector("sharedWorkspace"));
        return SendId(workspace, Selector("frontmostApplication"));
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
    private static extern nint SendNInt(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint SendIdNIntArgument(
        nint receiver,
        nint selector,
        nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SendBoolNIntArgument(
        nint receiver,
        nint selector,
        nint argument);
}
