// CEF → managed dispatch trampolines. Every public `Cef.*` event the
// shim raises lands in a [UnmanagedCallersOnly] cdecl method here, which
// resolves the browser id via s_browsers + calls the matching
// CefBrowser.Raise* method (or, for process-wide hooks, the static
// callback field). Split out of Cef.cs purely to keep that file focused
// on the public API surface; partial class — no API change.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Exclr8Cef.Native;

namespace Exclr8Cef;

public static partial class Cef
{
    // ---- Trampolines: demux per-browser native callbacks to instances --

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AddressChangeTrampoline(int browserId, sbyte* url)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseAddressChanged(Marshal.PtrToStringUTF8((IntPtr)url) ?? ""); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void TitleChangeTrampoline(int browserId, sbyte* title)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseTitleChanged(Marshal.PtrToStringUTF8((IntPtr)title) ?? ""); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LoadingStateTrampoline(int browserId, int isLoading, int canGoBack, int canGoForward)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseLoadingStateChanged(isLoading != 0, canGoBack != 0, canGoForward != 0); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CursorChangeTrampoline(int browserId, int cursorType)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseCursorChanged((CefCursorType)cursorType); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ConsoleMessageTrampoline(int browserId, int level, sbyte* message, sbyte* source, int line)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try
        {
            b.RaiseConsoleMessage(
                (CefLogSeverity)level,
                Marshal.PtrToStringUTF8((IntPtr)message) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)source) ?? "",
                line);
        }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void LoadStartTrampoline(int browserId, int isMainFrame, sbyte* url)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseLoadStart(isMainFrame != 0, Marshal.PtrToStringUTF8((IntPtr)url) ?? ""); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void LoadEndTrampoline(int browserId, int isMainFrame, sbyte* url, int httpStatusCode)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseLoadEnd(isMainFrame != 0, Marshal.PtrToStringUTF8((IntPtr)url) ?? "", httpStatusCode); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void LoadErrorTrampoline(int browserId, int isMainFrame, int errorCode, sbyte* errorText, sbyte* failedUrl)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try
        {
            b.RaiseLoadError(
                isMainFrame != 0,
                (CefErrorCode)errorCode,
                Marshal.PtrToStringUTF8((IntPtr)errorText) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)failedUrl) ?? "");
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LoadingProgressTrampoline(int browserId, double progress)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseLoadingProgress(progress); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void StatusMessageTrampoline(int browserId, sbyte* value)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseStatusMessage(Marshal.PtrToStringUTF8((IntPtr)value) ?? ""); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void TooltipTrampoline(int browserId, sbyte* text)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseTooltipChanged(Marshal.PtrToStringUTF8((IntPtr)text) ?? ""); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void FaviconTrampoline(int browserId, sbyte* firstUrl)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseFaviconChanged(Marshal.PtrToStringUTF8((IntPtr)firstUrl) ?? ""); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FullscreenTrampoline(int browserId, int fullscreen)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseFullscreenChanged(fullscreen != 0); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BrowserInitializedTrampoline(int browserId)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseInitialized(); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ScrollOffsetTrampoline(int browserId, double x, double y)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseScrollOffset(x, y); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AutoResizeTrampoline(int browserId, int w, int h)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseAutoResize(w, h); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void JsDialogTrampoline(int browserId, ulong token, int dialogType, sbyte* message, sbyte* defaultPrompt)
    {
        if (!s_browsers.TryGetValue(browserId, out var b))
        {
            // Unknown browser: cancel the dialog so the renderer isn't hung.
            Excef.excef_resolve_js_dialog(token, 0, null);
            return;
        }
        if (!b.HasJsDialogSubscriber)
        {
            // No host listener: cancel so the renderer is unblocked. (CEF
            // would have fallen back to its default dialog if we'd returned
            // false from OnJSDialog, but we always return true now — so we
            // must resolve here ourselves.)
            Excef.excef_resolve_js_dialog(token, 0, null);
            return;
        }
        try
        {
            b.RaiseJsDialog(
                token,
                (JsDialogType)dialogType,
                Marshal.PtrToStringUTF8((IntPtr)message) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)defaultPrompt) ?? "");
        }
        catch { Excef.excef_resolve_js_dialog(token, 0, null); }  // safe even if the handler already resolved
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AuthRequestTrampoline(int browserId, ulong token, int isProxy, sbyte* host, int port, sbyte* realm, sbyte* scheme, sbyte* originUrl)
    {
        if (!s_browsers.TryGetValue(browserId, out var b))
        {
            Excef.excef_resolve_auth(token, null, null);
            return;
        }
        if (!b.HasAuthRequestSubscriber)
        {
            Excef.excef_resolve_auth(token, null, null);
            return;
        }
        try
        {
            b.RaiseAuthRequest(
                token, isProxy != 0,
                Marshal.PtrToStringUTF8((IntPtr)host) ?? "",
                port,
                Marshal.PtrToStringUTF8((IntPtr)realm) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)scheme) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)originUrl) ?? "");
        }
        catch { Excef.excef_resolve_auth(token, null, null); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FindResultTrampoline(int browserId, int identifier, int count, int activeMatchOrdinal, int finalUpdate)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseFindResult(identifier, count, activeMatchOrdinal, finalUpdate != 0); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PopupShowTrampoline(int browserId, int show)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaisePopupShow(show != 0); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PopupSizeTrampoline(int browserId, int x, int y, int w, int h)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaisePopupSize(x, y, w, h); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void PopupPaintTrampoline(int browserId, void* buffer, int width, int height)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaisePopupPainted((IntPtr)buffer, width, height); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void JsInvokeTrampoline(int browserId, ulong token, sbyte* method, sbyte* argsJson)
    {
        if (!s_browsers.TryGetValue(browserId, out var b))
        {
            // Unknown browser: auto-resolve so the renderer's Promise doesn't hang.
            Excef.excef_resolve_js_invoke(token, 0, null);
            return;
        }
        try
        {
            b.RaiseJsInvoke(new JsInvokeEventArgs(
                token,
                Marshal.PtrToStringUTF8((IntPtr)method) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)argsJson) ?? ""));
        }
        catch { Excef.excef_resolve_js_invoke(token, 0, null); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AccessibilityTreeTrampoline(int browserId, sbyte* json)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseAccessibilityTreeChange(Marshal.PtrToStringUTF8((IntPtr)json) ?? ""); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AccessibilityLocationTrampoline(int browserId, sbyte* json)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseAccessibilityLocationChange(Marshal.PtrToStringUTF8((IntPtr)json) ?? ""); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void DragImageTrampoline(
        int browserId, void* buffer, int width, int height, int hotspotX, int hotspotY)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseDragImage((IntPtr)buffer, width, height, hotspotX, hotspotY); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void PermissionPromptTrampoline(
        int browserId, ulong token, ulong promptId, sbyte* origin, int requestedPermissions)
    {
        if (!s_browsers.TryGetValue(browserId, out var b))
        {
            Excef.excef_resolve_permission_prompt(token, (int)PermissionResult.Deny);
            return;
        }
        if (!b.HasPermissionRequestSubscriber)
        {
            Excef.excef_resolve_permission_prompt(token, (int)PermissionResult.Deny);
            return;
        }
        var args = new PermissionRequestEventArgs(
            token, promptId,
            Marshal.PtrToStringUTF8((IntPtr)origin) ?? "",
            (PermissionRequestType)requestedPermissions);
        try { b.RaisePermissionRequest(args); }
        catch { args.Deny(); }  // idempotent — won't double-resolve if handler already did
    }

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<string>> s_stringVisitorRequests = new();
    internal static int s_nextStringVisitorId;

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<System.Collections.Generic.List<NavigationEntry>>> s_navEntryRequests = new();
    internal static int s_nextNavEntryId;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void NavEntryTrampoline(int requestId, int done, int isCurrent,
        sbyte* url, sbyte* displayUrl, sbyte* originalUrl, sbyte* title,
        int transition, int httpStatus, long completionMs, int isValid)
    {
        if (!s_navEntryRequests.TryGetValue(requestId, out var tcs))
            return;
        if (done != 0)
        {
            if (s_navEntryRequests.TryRemove(requestId, out var doneTcs))
            {
                if (!s_navEntryAccum.TryRemove(requestId, out var list))
                    list = new System.Collections.Generic.List<NavigationEntry>();
                try { doneTcs.TrySetResult(list); }
                catch { }
            }
            return;
        }
        var entry = new NavigationEntry(
            Marshal.PtrToStringUTF8((IntPtr)url) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)displayUrl) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)originalUrl) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)title) ?? "",
            transition, httpStatus, completionMs, isValid != 0, isCurrent != 0);
        s_navEntryAccum.GetOrAdd(requestId, _ => new System.Collections.Generic.List<NavigationEntry>()).Add(entry);
    }

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Collections.Generic.List<NavigationEntry>> s_navEntryAccum = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void StringVisitorTrampoline(int requestId, sbyte* value)
    {
        if (!s_stringVisitorRequests.TryRemove(requestId, out var tcs)) return;
        try { tcs.TrySetResult(Marshal.PtrToStringUTF8((IntPtr)value) ?? ""); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void DevToolsMessageTrampoline(int browserId, int isEvent, int messageId, sbyte* json)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        var s = Marshal.PtrToStringUTF8((IntPtr)json) ?? "";
        try { b.RaiseDevToolsMessage(isEvent != 0, messageId, s); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void TakeFocusTrampoline(int browserId, int next)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseTakeFocus(next != 0); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int SetFocusTrampoline(int browserId, int source)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return 0;
        var args = new SetFocusEventArgs((FocusSource)source);
        try { b.RaiseSetFocus(args); }
        catch { return 0; }
        return args.Cancel ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void GotFocusTrampoline(int browserId)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseGotFocus(); }
        catch { }  // an escaping exception in [UnmanagedCallersOnly] fail-fasts the process
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int PreKeyTrampoline(int browserId, int type, int mods, int vk, int native, int isSystem)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return 0;
        var args = new PreKeyEventArgs((CefKeyEventType)type, (CefModifiers)mods, vk, native, isSystem != 0);
        try { b.RaisePreKey(args); }
        catch { return 0; }
        return args.Handled ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int KeyEventTrampoline(int browserId, int type, int mods, int vk, int native, int isSystem)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return 0;
        var args = new PreKeyEventArgs((CefKeyEventType)type, (CefModifiers)mods, vk, native, isSystem != 0);
        try { b.RaiseKeyEvent(args); }
        catch { return 0; }
        return args.Handled ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void CertErrorTrampoline(
        int browserId, ulong token, int certError,
        sbyte* requestUrl, sbyte* subjectCn, sbyte* issuerCn)
    {
        if (!s_browsers.TryGetValue(browserId, out var b))
        {
            Excef.excef_resolve_cert_error(token, 0);
            return;
        }
        if (!b.HasCertErrorSubscriber)
        {
            Excef.excef_resolve_cert_error(token, 0);
            return;
        }
        var certArgs = new CertErrorEventArgs(
            token,
            (CefErrorCode)certError,
            Marshal.PtrToStringUTF8((IntPtr)requestUrl) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)subjectCn) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)issuerCn) ?? "");
        try { b.RaiseCertError(certArgs); }
        catch { certArgs.Cancel(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int HostPopupTrampoline(
        int browserId, int childBrowserId, sbyte* targetUrl, sbyte* targetFrameName,
        int disposition, int userGesture)
    {
        if (!s_browsers.TryGetValue(browserId, out var parent)) return 0;
        var child = new CefBrowser(childBrowserId);
        if (!s_browsers.TryAdd(childBrowserId, child)) return 0;
        try
        {
            var args = new HostPopupEventArgs(child,
                Marshal.PtrToStringUTF8((IntPtr)targetUrl) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)targetFrameName) ?? "",
                (WindowOpenDisposition)disposition, userGesture != 0);
            parent.RaiseHostPopup(args);
            if (args.IsHosted && !child.IsClosed) return 1;
        }
        catch { }
        if (s_browsers.TryRemove(childBrowserId, out var rejected))
        {
            try { rejected.RaiseClosed(); } catch { }
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void BeforePopupTrampoline(
        int browserId, sbyte* targetUrl, sbyte* targetFrameName,
        int disposition, int userGesture)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        // Fire-and-forget on the host side; swallow exceptions so a host
        // handler bug can't crash the CEF pump thread.
        try
        {
            b.RaiseBeforePopup(new BeforePopupEventArgs(
                Marshal.PtrToStringUTF8((IntPtr)targetUrl) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)targetFrameName) ?? "",
                (WindowOpenDisposition)disposition,
                userGesture != 0));
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int BeforeBrowseTrampoline(
        int browserId, sbyte* url, int userGesture, int isRedirect)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return 0;
        if (!b.HasBeforeBrowseSubscriber) return 0;

        try
        {
            var args = new BeforeBrowseEventArgs(
                Marshal.PtrToStringUTF8((IntPtr)url) ?? "",
                userGesture != 0,
                isRedirect != 0);
            return b.RaiseBeforeBrowse(args) ? 1 : 0;
        }
        catch
        {
            // This callback is an origin/policy boundary. A handler failure
            // must cancel the navigation instead of accidentally allowing it.
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void MediaAccessTrampoline(
        int browserId, ulong token, sbyte* origin, int requestedPermissions)
    {
        if (!s_browsers.TryGetValue(browserId, out var b))
        {
            Excef.excef_resolve_media_access(token, 0);
            return;
        }
        if (!b.HasMediaAccessRequestSubscriber)
        {
            Excef.excef_resolve_media_access(token, 0);
            return;
        }
        var args = new MediaAccessRequestEventArgs(
            token,
            Marshal.PtrToStringUTF8((IntPtr)origin) ?? "",
            (MediaAccessPermissions)requestedPermissions);
        try { b.RaiseMediaAccessRequest(args); }
        catch { args.Deny(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int StartDragTrampoline(
        int browserId, int allowedOps, int x, int y,
        sbyte* text, sbyte* html, sbyte* linkUrl, sbyte* linkTitle,
        sbyte** fileNames, int fileNameCount)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return 0;
        if (!b.HasDragStartedSubscriber) return 0;

        string[] files = fileNameCount > 0 ? new string[fileNameCount] : Array.Empty<string>();
        for (int i = 0; i < fileNameCount; ++i)
            files[i] = Marshal.PtrToStringUTF8((IntPtr)fileNames[i]) ?? "";

        var args = new DragStartedEventArgs(
            (DragOperations)allowedOps, x, y,
            Marshal.PtrToStringUTF8((IntPtr)text) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)html) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)linkUrl) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)linkTitle) ?? "",
            files);
        try { return b.RaiseDragStarted(args) ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ResourceRequestTrampoline(int browserId, ulong token, sbyte* url, sbyte* method, int resourceType, sbyte* headers)
    {
        if (!s_browsers.TryGetValue(browserId, out var b))
        {
            Excef.excef_resolve_resource_request(token, 0, null);
            return;
        }
        if (!b.HasResourceRequestSubscriber)
        {
            // Shouldn't reach here — the native side skips firing when no
            // subscriber is registered — but safe fallback.
            Excef.excef_resolve_resource_request(token, 0, null);
            return;
        }
        var urlStr     = Marshal.PtrToStringUTF8((IntPtr)url) ?? "";
        var methodStr  = Marshal.PtrToStringUTF8((IntPtr)method) ?? "GET";
        var headersStr = Marshal.PtrToStringUTF8((IntPtr)headers) ?? "";
        var typeEnum   = (ResourceType)resourceType;

        // Observer fires first and never participates in the gate
        // decision — it's pure notification (logging, devtools panels).
        // A misbehaving observer must not be able to wedge the gate.
        try { b.RaiseResourceRequestObserved(urlStr, methodStr, typeEnum, headersStr); }
        catch { /* swallow — never let an observer break the request */ }

        if (b.HasResourceRequestGate)
        {
            // Gate subscriber owns the resolve token — they must call
            // Continue() or Cancel() on the args.
            try { b.RaiseResourceRequest(token, urlStr, methodStr, typeEnum, headersStr); }
            catch
            {
                // A policy-gate failure must never silently turn into an
                // allow. Cancel the request and release the native token.
                Excef.excef_resolve_resource_request(token, 1, null);
            }
        }
        else
        {
            // Observer-only: auto-continue immediately so the request
            // doesn't stall waiting on a resolve that's never coming.
            Excef.excef_resolve_resource_request(token, 0, null);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void SchemeRequestTrampoline(int browserId, ulong token, sbyte* url, sbyte* method)
    {
        if (!HasSchemeRequestSubscriber)
        {
            // No host listener: 404. Otherwise the renderer hangs waiting.
            Excef.excef_resolve_scheme_request(token, 404, null, null, null, 0);
            return;
        }
        var args = new SchemeRequestEventArgs(
            token, browserId,
            Marshal.PtrToStringUTF8((IntPtr)url) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)method) ?? "GET");
        try { SchemeRequest?.Invoke(null, args); }
        catch { Excef.excef_resolve_scheme_request(token, 500, null, null, null, 0); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void RenderProcessGoneTrampoline(int browserId, int status, int errorCode, sbyte* errorString)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try
        {
            b.RaiseRenderProcessGone(
                (TerminationStatus)status,
                errorCode,
                Marshal.PtrToStringUTF8((IntPtr)errorString) ?? "");
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void DownloadStartingTrampoline(int browserId, ulong token, int downloadId, sbyte* url, sbyte* suggestedName, sbyte* mimeType, long totalBytes)
    {
        if (!s_browsers.TryGetValue(browserId, out var b))
        {
            // Cancel by sending empty path.
            Excef.excef_resolve_download_starting(token, null, 0);
            return;
        }
        if (!b.HasDownloadStartingSubscriber)
        {
            // No subscriber: let CEF write to the default Downloads folder
            // with the suggested filename (path="" + show_dialog=true is
            // CEF's "I don't care, you decide" signal).
            Excef.excef_resolve_download_starting(token, null, 1);
            return;
        }
        try
        {
            b.RaiseDownloadStarting(
                token, downloadId,
                Marshal.PtrToStringUTF8((IntPtr)url) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)suggestedName) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)mimeType) ?? "",
                totalBytes);
        }
        catch { Excef.excef_resolve_download_starting(token, null, 0); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void DownloadProgressTrampoline(int browserId, ulong token, int downloadId, int percent, long received, long total, long speed, int state, sbyte* fullPath)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try
        {
            b.RaiseDownloadProgress(
                token, downloadId,
                percent, received, total, speed,
                (DownloadState)state,
                Marshal.PtrToStringUTF8((IntPtr)fullPath) ?? "");
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ContextMenuTrampoline(int browserId, ulong token, int x, int y, sbyte* itemsJoined)
    {
        if (!s_browsers.TryGetValue(browserId, out var b))
        {
            Excef.excef_resolve_context_menu(token, -1);
            return;
        }
        if (!b.HasContextMenuSubscriber)
        {
            Excef.excef_resolve_context_menu(token, -1);
            return;
        }
        var raw = Marshal.PtrToStringUTF8((IntPtr)itemsJoined) ?? "";
        var items = ParseContextMenuItems(raw);
        try { b.RaiseContextMenu(token, x, y, items); }
        catch { Excef.excef_resolve_context_menu(token, -1); }
    }

    internal static ContextMenuItem[] ParseContextMenuItems(string raw)
    {
        if (raw.Length == 0) return Array.Empty<ContextMenuItem>();
        var lines = raw.Split('\n');
        var result = new ContextMenuItem[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            var fields = lines[i].Split('\t', 6);
            int.TryParse(fields.ElementAtOrDefault(0), out var id);
            int.TryParse(fields.ElementAtOrDefault(1), out var kindValue);
            var enabled = fields.ElementAtOrDefault(2) != "0";
            var isChecked = fields.ElementAtOrDefault(3) == "1";
            int.TryParse(fields.ElementAtOrDefault(4), out var depth);
            var label = fields.ElementAtOrDefault(5) ?? "";
            var kind = Enum.IsDefined(typeof(ContextMenuItemKind), kindValue)
                ? (ContextMenuItemKind)kindValue
                : ContextMenuItemKind.Command;
            result[i] = new ContextMenuItem(
                id,
                label,
                kind,
                enabled,
                isChecked,
                Math.Max(0, depth));
        }
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void FileDialogTrampoline(int browserId, ulong token, int mode, sbyte* title, sbyte* defaultPath, sbyte* filtersJoined)
    {
        if (!s_browsers.TryGetValue(browserId, out var b))
        {
            Excef.excef_resolve_file_dialog(token, null);
            return;
        }
        if (!b.HasFileDialogSubscriber)
        {
            Excef.excef_resolve_file_dialog(token, null);
            return;
        }
        var filters = Marshal.PtrToStringUTF8((IntPtr)filtersJoined) ?? "";
        var split = filters.Length == 0
            ? Array.Empty<string>()
            : filters.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            b.RaiseFileDialog(
                token,
                (FileDialogMode)mode,
                Marshal.PtrToStringUTF8((IntPtr)title) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)defaultPath) ?? "",
                split);
        }
        catch { Excef.excef_resolve_file_dialog(token, null); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BrowserClosedTrampoline(int browserId)
    {
        if (s_browsers.TryRemove(browserId, out var b))
        {
            try { b.RaiseClosed(); }
            catch { }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void EvalResultTrampoline(int browserId, int requestId, int success, sbyte* payload)
    {
        if (!s_evalRequests.TryRemove(requestId, out var tcs)) return;
        if (s_browsers.TryGetValue(browserId, out var browser)) browser.RemoveEvalRequest(requestId);
        var s = Marshal.PtrToStringUTF8((IntPtr)payload) ?? "";
        try
        {
            if (success != 0) tcs.TrySetResult(s);
            else tcs.TrySetException(new InvalidOperationException(string.IsNullOrEmpty(s) ? "JS eval failed" : s));
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void CookieVisitTrampoline(int requestId, int done,
        sbyte* name, sbyte* value, sbyte* domain, sbyte* path,
        int secure, int httpOnly)
    {
        if (done != 0)
        {
            if (s_cookieRequests.TryRemove(requestId, out var entry))
            {
                try { entry.Tcs.TrySetResult(entry.List); }
                catch { }
            }
            return;
        }
        if (s_cookieRequests.TryGetValue(requestId, out var e))
        {
            e.List.Add(new CookieInfo(
                Marshal.PtrToStringUTF8((IntPtr)name) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)value) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)domain) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)path) ?? "",
                secure != 0,
                httpOnly != 0));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RequestContextCompletionTrampoline(
        int requestId,
        int result)
    {
        if (s_requestContextOperations.TryRemove(requestId, out var completion))
        {
            try { completion.TrySetResult(result); }
            catch { }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void PdfDoneTrampoline(int browserId, int success)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        Action<int, int>? cb = null;
        lock (b.PdfQueue)
        {
            if (b.PdfQueue.Count > 0)
            {
                cb = b.PdfQueue[0];
                b.PdfQueue.RemoveAt(0);
            }
        }
        try { cb?.Invoke(browserId, success); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void PaintTrampoline(int browserId, void* buffer, int width, int height)
    {
        if (s_browsers.TryGetValue(browserId, out var b))
        {
            try { b.RaisePainted((IntPtr)buffer, width, height); }
            catch { }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void FrameLifecycleTrampoline(
        int browserId, int eventType, sbyte* frameId, sbyte* parentFrameId,
        sbyte* name, sbyte* url, int isMain)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        var args = new FrameLifecycleEventArgs(
            (FrameLifecycleEvent)eventType,
            Marshal.PtrToStringUTF8((IntPtr)frameId) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)parentFrameId) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)name) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)url) ?? "",
            isMain != 0);
        try { b.RaiseFrameLifecycle(args); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void MainFrameChangedTrampoline(
        int browserId, sbyte* oldFrameId, sbyte* newFrameId)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        var args = new MainFrameChangedEventArgs(
            Marshal.PtrToStringUTF8((IntPtr)oldFrameId) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)newFrameId) ?? "");
        try { b.RaiseMainFrameChanged(args); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AudioStreamStartedTrampoline(
        int browserId, int channelLayout, int sampleRate, int framesPerBuffer, int channels)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseAudioStarted(new AudioStreamStartedEventArgs(channelLayout, sampleRate, framesPerBuffer, channels)); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AudioStreamPacketTrampoline(
        int browserId, float* interleaved, int frames, int channels, long ptsUs)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        var args = new AudioPacketEventArgs((IntPtr)interleaved, frames, channels, ptsUs);
        try { b.RaiseAudioPacket(args); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AudioStreamStoppedTrampoline(int browserId)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseAudioStopped(); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AudioStreamErrorTrampoline(int browserId, sbyte* message)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        var msg = Marshal.PtrToStringUTF8((IntPtr)message) ?? "";
        try { b.RaiseAudioError(msg); }
        catch { }
    }

    // ---- Response-filter trampolines ----------------------------------
    //
    // The filter callbacks fire from CEF's network/IO thread, NOT the UI
    // thread. They MUST be fast — they sit in the critical path of every
    // response body chunk. Host handlers should treat them as inline
    // transforms, not as a place to do I/O or marshalling.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ShouldFilterResponseTrampoline(
        int browserId, ulong token, sbyte* url, int status, sbyte* mimeType)
    {
        var fn = s_shouldFilterResponse;
        if (fn is null) return 0;
        try
        {
            s_browsers.TryGetValue(browserId, out var b);
            var args = new ResponseFilterDecision(
                b, token,
                Marshal.PtrToStringUTF8((IntPtr)url) ?? "",
                status,
                Marshal.PtrToStringUTF8((IntPtr)mimeType) ?? "");
            return fn(args) ? 1 : 0;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ResponseFilterTrampoline(
        int browserId, ulong token,
        byte* dataIn, int sizeIn,
        byte* dataOut, int sizeOut,
        int* bytesRead, int* bytesWritten)
    {
        var fn = s_responseFilter;
        if (fn is null)
        {
            // Identity: copy what fits, signal DONE if nothing more incoming.
            int n = Math.Min(sizeIn, sizeOut);
            if (n > 0 && dataIn != null && dataOut != null)
                System.Buffer.MemoryCopy(dataIn, dataOut, sizeOut, n);
            *bytesRead = n;
            *bytesWritten = n;
            return sizeIn == 0 ? 1 : 0;
        }
        try
        {
            var input = new ReadOnlySpan<byte>(dataIn, sizeIn);
            var output = new Span<byte>(dataOut, sizeOut);
            var status = fn(browserId, token, input, output, out int br, out int bw);
            *bytesRead = br;
            *bytesWritten = bw;
            return status switch
            {
                ResponseFilterStatus.Done => 1,
                ResponseFilterStatus.Error => -1,
                _ => 0,
            };
        }
        catch
        {
            *bytesRead = 0;
            *bytesWritten = 0;
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ResponseFilterFinalizeTrampoline(int browserId, ulong token)
    {
        try { s_responseFilterFinalize?.Invoke(browserId, token); }
        catch { }
    }

    // ---- Command-handler trampolines ----------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ChromeCommandTrampoline(int browserId, int commandId, int disposition)
    {
        var fn = s_onChromeCommand;
        if (fn is null) return 0;
        try
        {
            s_browsers.TryGetValue(browserId, out var b);
            return fn(b, commandId, (WindowOpenDisposition)disposition) ? 1 : 0;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AppMenuVisibleTrampoline(int browserId, int commandId)
    {
        var fn = s_isAppMenuItemVisible;
        if (fn is null) return 1;
        try
        {
            s_browsers.TryGetValue(browserId, out var b);
            return fn(b, commandId) ? 1 : 0;
        }
        catch { return 1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AppMenuEnabledTrampoline(int browserId, int commandId)
    {
        var fn = s_isAppMenuItemEnabled;
        if (fn is null) return 1;
        try
        {
            s_browsers.TryGetValue(browserId, out var b);
            return fn(b, commandId) ? 1 : 0;
        }
        catch { return 1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int PageActionVisibleTrampoline(int iconType)
    {
        var fn = s_isPageActionIconVisible;
        if (fn is null) return 1;
        try { return fn((ChromePageActionIcon)iconType) ? 1 : 0; }
        catch { return 1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ToolbarButtonVisibleTrampoline(int buttonType)
    {
        var fn = s_isToolbarButtonVisible;
        if (fn is null) return 1;
        try { return fn((ChromeToolbarButton)buttonType) ? 1 : 0; }
        catch { return 1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ShouldHandleResourceTrampoline(
        int browserId, ulong token, sbyte* url, sbyte* method)
    {
        var fn = s_shouldHandleResource;
        if (fn is null) return 0;
        try
        {
            s_browsers.TryGetValue(browserId, out var b);
            var args = new ResourceHandlerDecision(
                b, token,
                Marshal.PtrToStringUTF8((IntPtr)url) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)method) ?? "");
            return fn(args) ? 1 : 0;
        }
        catch { return 0; }
    }

    // ---- OSR render-handler trampolines -------------------------------
    //
    // All fire from the CEF UI thread. Routed through s_browsers, then
    // the browser raises a per-instance event. Consumers HOP to UI
    // thread inside their handler if they want to touch UI state.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void TouchHandleSizeTrampoline(int browserId, int orientation,
                                                          int* outWidth, int* outHeight)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        var args = new TouchHandleSizeRequest((HorizontalAlignment)orientation);
        try { b.RaiseTouchHandleSizeRequest(args); }
        catch { return; }
        if (outWidth  != null) *outWidth  = args.Width;
        if (outHeight != null) *outHeight = args.Height;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TouchHandleStateTrampoline(
        int browserId, int handleId, uint flags, int enabled,
        int orientation, int mirrorV, int mirrorH,
        int originX, int originY, float alpha)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        var args = new TouchHandleStateEventArgs(
            handleId, flags, enabled != 0,
            (HorizontalAlignment)orientation, mirrorV != 0, mirrorH != 0,
            originX, originY, alpha);
        try { b.RaiseTouchHandleStateChanged(args); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ImeCompositionRangeTrampoline(
        int browserId, int selectionStart, int selectionEnd,
        int characterCount, int* characterBounds)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        var rects = new System.Drawing.Rectangle[characterCount];
        if (characterBounds != null)
        {
            for (int i = 0; i < characterCount; ++i)
                rects[i] = new System.Drawing.Rectangle(
                    characterBounds[i * 4 + 0],
                    characterBounds[i * 4 + 1],
                    characterBounds[i * 4 + 2],
                    characterBounds[i * 4 + 3]);
        }
        var args = new ImeCompositionRangeEventArgs(selectionStart, selectionEnd, rects);
        try { b.RaiseImeCompositionRangeChanged(args); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void VirtualKeyboardTrampoline(int browserId, int inputMode)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        try { b.RaiseVirtualKeyboardRequested((CefTextInputMode)inputMode); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AcceleratedPaintTrampoline(
        int browserId, int elementType, int codedWidth, int codedHeight,
        int format, ulong timestampUs, void* sharedHandle)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        var args = new AcceleratedPaintEventArgs(
            (PaintElementType)elementType,
            codedWidth, codedHeight,
            (CefColorType)format,
            (long)timestampUs,
            (IntPtr)sharedHandle);
        try { b.RaiseAcceleratedPaint(args); }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SchedulePumpWorkTrampoline(long delayMs)
        {
        try { s_scheduleCallback?.Invoke(delayMs); }
        catch { }
    }
}
