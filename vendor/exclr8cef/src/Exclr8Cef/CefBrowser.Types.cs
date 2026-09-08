// Supporting types for CefBrowser — every record / EventArgs class that
// the main file declares as part of the browser's event + return-value
// surface. Lives as a separate file purely to keep CefBrowser.cs focused
// on the class itself; no namespace / API change.

using System.Runtime.InteropServices;
using System.Threading;
using Exclr8Cef.Native;

namespace Exclr8Cef;


/// <summary>Loading-state snapshot fired by <see cref="CefBrowser.LoadingStateChanged"/>.</summary>
public readonly record struct LoadingState(bool IsLoading, bool CanGoBack, bool CanGoForward);

/// <summary>
/// A snapshot of a rendered paint, delivered via
/// <see cref="CefBrowser.FrameStream"/>. <see cref="Bgra"/> is owned by
/// the consumer (independent copy, safe to retain).
/// </summary>
public sealed record PaintFrame(byte[] Bgra, int Width, int Height, DateTime Timestamp);

/// <summary>
/// Result of <see cref="CefBrowser.HitTestAtAsync"/>: the element found
/// at the probed coordinates and its bounding rect.
/// </summary>
public sealed record HitTestResult(
    string Tag,
    string? Id,
    string? ClassName,
    string? Text,
    string? Role,
    string? Href,
    double X, double Y, double Width, double Height);

/// <summary>
/// Args for <see cref="CefBrowser.JsInvoke"/>. The renderer's
/// <c>window.exclr8cef.invoke(method, argsJson)</c> call returns a
/// Promise — the host MUST call <see cref="Reply"/> or <see cref="ReplyError"/>
/// so the Promise eventually resolves / rejects. If the host never
/// replies, the Promise stays pending forever (no leak in the browser
/// process — the entry is cleaned up on browser close).
/// </summary>
public sealed class JsInvokeEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    /// <summary>The method name JS passed as the first argument.</summary>
    public string Method { get; }
    /// <summary>The arguments JS passed as the second argument (a string — typically <c>JSON.stringify(...)</c> of the call args).</summary>
    public string ArgsJson { get; }
    internal JsInvokeEventArgs(ulong token, string method, string argsJson)
    {
        _token = token;
        Method = method;
        ArgsJson = argsJson;
    }

    /// <summary>
    /// Resolve the renderer's Promise. <paramref name="resultJson"/>
    /// will be <c>JSON.parse</c>d on the JS side. Pass <c>null</c> to
    /// resolve with <c>null</c>. Idempotent — only the first call wins.
    /// </summary>
    public void Reply(string? resultJson)
    {
        if (Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe
        {
            sbyte* p = resultJson is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(resultJson);
            try { Excef.excef_resolve_js_invoke(_token, 1, p); }
            finally { if (p != null) Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    /// <summary>
    /// Reject the renderer's Promise with the given message. Idempotent.
    /// </summary>
    public void ReplyError(string message)
    {
        if (Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(message ?? "");
            try { Excef.excef_resolve_js_invoke(_token, 0, p); }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }
}

/// <summary>
/// Popup geometry delivered to <see cref="CefBrowser.PopupSize"/> —
/// position and size in DIP / CSS pixels relative to the browser's
/// main view origin.
/// </summary>
public readonly record struct PopupRect(int X, int Y, int Width, int Height);

/// <summary>Paint frame delivered to <see cref="CefBrowser.Painted"/>.</summary>
public sealed class PaintEventArgs : EventArgs
{
    public IntPtr Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public PaintEventArgs(IntPtr buffer, int width, int height)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.JsDialog"/>. Hosts MUST call exactly one
/// of <see cref="Continue"/> / <see cref="Cancel"/> to dismiss the dialog
/// — leaving it unresolved hangs the renderer.
/// </summary>
public sealed class JsDialogEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved; // 0 = pending, 1 = resolved (atomic flip)

    /// <summary>Which dialog primitive triggered the event.</summary>
    public Cef.JsDialogType Type { get; }
    /// <summary>The message text from the page (the alert/confirm/prompt argument).</summary>
    public string Message { get; }
    /// <summary>Default text for prompt() dialogs; empty otherwise.</summary>
    public string DefaultPromptText { get; }

    internal JsDialogEventArgs(ulong token, Cef.JsDialogType type, string message, string defaultPrompt)
    {
        _token = token;
        Type = type;
        Message = message;
        DefaultPromptText = defaultPrompt;
    }

    /// <summary>
    /// Accept the dialog. For prompts, <paramref name="userInput"/> is the
    /// text the user typed; ignored for alert / confirm / onbeforeunload.
    /// Idempotent — subsequent calls are no-ops.
    /// </summary>
    public void Continue(string? userInput = null)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe
        {
            sbyte* p = userInput is null ? null : (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(userInput);
            try { Native.Excef.excef_resolve_js_dialog(_token, 1, p); }
            finally { if (p is not null) System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    /// <summary>Cancel the dialog (user clicked Cancel / closed). Idempotent.</summary>
    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe { Native.Excef.excef_resolve_js_dialog(_token, 0, null); }
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.AuthRequest"/>. Host MUST call
/// <see cref="Continue"/> with credentials or <see cref="Cancel"/>.
/// </summary>
public sealed class AuthRequestEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    public bool IsProxy { get; }
    public string Host { get; }
    public int Port { get; }
    public string Realm { get; }
    /// <summary>Auth scheme — "basic", "digest", "ntlm", "negotiate".</summary>
    public string Scheme { get; }
    public string OriginUrl { get; }

    internal AuthRequestEventArgs(ulong token, bool isProxy, string host, int port, string realm, string scheme, string originUrl)
    {
        _token = token; IsProxy = isProxy; Host = host; Port = port; Realm = realm; Scheme = scheme;
        OriginUrl = originUrl;
    }

    public void Continue(string username, string password)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe
        {
            sbyte* u = (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(username ?? "");
            sbyte* p = (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(password ?? "");
            try { Native.Excef.excef_resolve_auth(_token, u, p); }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)u);
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)p);
            }
        }
    }

    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe { Native.Excef.excef_resolve_auth(_token, null, null); }
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.ResourceRequest"/>. Host MUST call
/// either <see cref="Continue"/> or <see cref="Cancel"/>.
/// </summary>
public sealed class ResourceRequestEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    public string Url { get; }
    public string Method { get; }
    public Cef.ResourceType Type { get; }
    /// <summary>The request's current header set ("Name: Value\n" per line).</summary>
    public string CurrentHeaders { get; }

    internal ResourceRequestEventArgs(ulong token, string url, string method, Cef.ResourceType type, string headers)
    {
        _token = token; Url = url; Method = method; Type = type; CurrentHeaders = headers;
    }

    /// <summary>
    /// Let the request proceed. If <paramref name="newHeaders"/> is non-null,
    /// the request's entire header set is REPLACED (same `Name: Value\n` format
    /// as <see cref="CurrentHeaders"/>); otherwise headers are left as-is.
    /// </summary>
    public void Continue(string? newHeaders = null)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe
        {
            sbyte* p = newHeaders is null ? null : (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(newHeaders);
            try { Native.Excef.excef_resolve_resource_request(_token, 0, p); }
            finally { if (p is not null) System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe { Native.Excef.excef_resolve_resource_request(_token, 1, null); }
    }
}

/// <summary>Args for the synchronous <see cref="CefBrowser.BeforeBrowse"/> gate.</summary>
public sealed class BeforeBrowseEventArgs : EventArgs
{
    /// <summary>The destination URL Chromium is about to navigate to.</summary>
    public string Url { get; }

    /// <summary>
    /// True for explicit user activation (for example a clicked link); false
    /// for scripted or otherwise automatic navigation.
    /// </summary>
    public bool UserGesture { get; }

    /// <summary>True when this navigation continues a redirect chain.</summary>
    public bool IsRedirect { get; }

    /// <summary>Set to true inside the event handler to cancel navigation.</summary>
    public bool Cancel { get; set; }

    internal BeforeBrowseEventArgs(string url, bool userGesture, bool isRedirect)
    {
        Url = url;
        UserGesture = userGesture;
        IsRedirect = isRedirect;
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.ResourceRequestObserved"/> — pure
/// notification, no gate participation. Same fields as
/// <see cref="ResourceRequestEventArgs"/> minus the resolve methods,
/// since the trampoline auto-continues these.
/// </summary>
public sealed class ResourceRequestObservedEventArgs : EventArgs
{
    public string Url { get; }
    public string Method { get; }
    public Cef.ResourceType Type { get; }
    /// <summary>The request's current header set ("Name: Value\n" per line).</summary>
    public string CurrentHeaders { get; }

    internal ResourceRequestObservedEventArgs(string url, string method, Cef.ResourceType type, string headers)
    {
        Url = url; Method = method; Type = type; CurrentHeaders = headers;
    }
}

/// <summary>Args for <see cref="CefBrowser.RenderProcessGone"/>.</summary>
public sealed class RenderProcessGoneEventArgs : EventArgs
{
    public Cef.TerminationStatus Status { get; }
    /// <summary>Platform-specific exit / signal code (0 if not applicable).</summary>
    public int ErrorCode { get; }
    /// <summary>Human-readable status string from Chromium (may be empty).</summary>
    public string ErrorString { get; }
    internal RenderProcessGoneEventArgs(Cef.TerminationStatus status, int errorCode, string errorString)
    {
        Status = status; ErrorCode = errorCode; ErrorString = errorString;
    }
}

/// <summary>Args for <see cref="CefBrowser.FindResult"/>.</summary>
public sealed class FindResultEventArgs : EventArgs
{
    /// <summary>CEF-internal search session id.</summary>
    public int Identifier { get; }
    /// <summary>Total match count.</summary>
    public int Count { get; }
    /// <summary>1-based ordinal of the currently-active (highlighted) match. 0 if no matches.</summary>
    public int ActiveMatchOrdinal { get; }
    /// <summary>True if this is the last update for the current Find session.</summary>
    public bool FinalUpdate { get; }
    internal FindResultEventArgs(int identifier, int count, int activeMatchOrdinal, bool finalUpdate)
    {
        Identifier = identifier; Count = count; ActiveMatchOrdinal = activeMatchOrdinal; FinalUpdate = finalUpdate;
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.DownloadStarting"/>. Host MUST call
/// <see cref="Continue"/> with a save path or <see cref="Cancel"/>.
/// </summary>
public sealed class DownloadStartingEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    public int DownloadId { get; }
    public string Url { get; }
    public string SuggestedName { get; }
    public string MimeType { get; }
    /// <summary>Total content length, -1 if unknown.</summary>
    public long TotalBytes { get; }

    internal DownloadStartingEventArgs(ulong token, int downloadId, string url, string suggestedName, string mimeType, long totalBytes)
    {
        _token = token; DownloadId = downloadId; Url = url; SuggestedName = suggestedName;
        MimeType = mimeType; TotalBytes = totalBytes;
    }

    /// <summary>Start the download to <paramref name="path"/>. If <paramref name="showDialog"/> is true, the OS save-as dialog will appear as well.</summary>
    public void Continue(string path, bool showDialog = false)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe
        {
            sbyte* p = (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(path);
            try { Native.Excef.excef_resolve_download_starting(_token, p, showDialog ? 1 : 0); }
            finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe { Native.Excef.excef_resolve_download_starting(_token, null, 0); }
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.DownloadProgress"/>. Action methods
/// (<see cref="Cancel"/> / <see cref="Pause"/> / <see cref="Resume"/>)
/// MUST be called synchronously inside the event handler.
/// </summary>
public sealed class DownloadProgressEventArgs : EventArgs
{
    private readonly ulong _token;

    public int DownloadId { get; }
    /// <summary>0–100, or -1 if total size is unknown.</summary>
    public int PercentComplete { get; }
    public long ReceivedBytes { get; }
    public long TotalBytes { get; }
    public long CurrentSpeedBytesPerSec { get; }
    public Cef.DownloadState State { get; }
    public string FullPath { get; }

    internal DownloadProgressEventArgs(ulong token, int downloadId, int percent, long received, long total, long speed, Cef.DownloadState state, string fullPath)
    {
        _token = token; DownloadId = downloadId; PercentComplete = percent;
        ReceivedBytes = received; TotalBytes = total; CurrentSpeedBytesPerSec = speed;
        State = state; FullPath = fullPath;
    }

    public void Cancel() => Native.Excef.excef_download_action(_token, 0);
    public void Pause()  => Native.Excef.excef_download_action(_token, 1);
    public void Resume() => Native.Excef.excef_download_action(_token, 2);
}

/// <summary>The visual behavior of a <see cref="ContextMenuItem"/>.</summary>
public enum ContextMenuItemKind
{
    Command = 0,
    Check = 1,
    Radio = 2,
    Separator = 3,
    Submenu = 4,
}

/// <summary>One entry in <see cref="ContextMenuEventArgs.Items"/>.</summary>
/// <param name="CommandId">CEF's command id; 0 for separators.</param>
/// <param name="Label">The display text in the system locale.</param>
/// <param name="Kind">How the host should render the item.</param>
/// <param name="IsEnabled">Whether the command can currently run.</param>
/// <param name="IsChecked">The current check/radio state.</param>
/// <param name="Depth">Zero for root items; one or more for submenu children.</param>
public readonly record struct ContextMenuItem(
    int CommandId,
    string Label,
    ContextMenuItemKind Kind,
    bool IsEnabled,
    bool IsChecked,
    int Depth)
{
    public bool IsSeparator => Kind == ContextMenuItemKind.Separator;
}

/// <summary>
/// Args for <see cref="CefBrowser.ContextMenu"/>. Hosts MUST call exactly
/// one of <see cref="Continue"/> / <see cref="Cancel"/>.
/// </summary>
public sealed class ContextMenuEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    /// <summary>Click position in CSS pixels relative to the page.</summary>
    public int X { get; }
    /// <summary>Click position in CSS pixels relative to the page.</summary>
    public int Y { get; }
    /// <summary>The menu items CEF would have shown. Render these in the host UI.</summary>
    public IReadOnlyList<ContextMenuItem> Items { get; }

    internal ContextMenuEventArgs(ulong token, int x, int y, ContextMenuItem[] items)
    {
        _token = token; X = x; Y = y; Items = items;
    }

    /// <summary>Resolve with the chosen command id (must match one of <see cref="Items"/>).</summary>
    public void Continue(int commandId)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        Native.Excef.excef_resolve_context_menu(_token, commandId);
    }

    /// <summary>Dismiss without executing any command.</summary>
    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        Native.Excef.excef_resolve_context_menu(_token, -1);
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.FileDialog"/>. Hosts MUST call exactly
/// one of <see cref="Continue"/> / <see cref="Cancel"/>.
/// </summary>
public sealed class FileDialogEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    public Cef.FileDialogMode Mode { get; }
    /// <summary>Title for the picker (empty = host default).</summary>
    public string Title { get; }
    /// <summary>Default file/folder path (empty if no hint).</summary>
    public string DefaultPath { get; }
    /// <summary>
    /// Accept-filter list from the page (MIME types like "image/*", or
    /// globs like "*.txt"). Empty if no filter.
    /// </summary>
    public IReadOnlyList<string> AcceptFilters { get; }

    internal FileDialogEventArgs(ulong token, Cef.FileDialogMode mode, string title, string defaultPath, string[] filters)
    {
        _token = token;
        Mode = mode;
        Title = title;
        DefaultPath = defaultPath;
        AcceptFilters = filters;
    }

    /// <summary>
    /// Resolve with the user's selection. Pass one path for Open/Save/OpenFolder,
    /// one or more for OpenMultiple. Pass an empty array (or call <see cref="Cancel"/>)
    /// for "user cancelled".
    /// </summary>
    public void Continue(params string[] paths)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        var joined = paths.Length == 0 ? null : string.Join('\n', paths);
        unsafe
        {
            sbyte* p = joined is null ? null : (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(joined);
            try { Native.Excef.excef_resolve_file_dialog(_token, p); }
            finally { if (p is not null) System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe { Native.Excef.excef_resolve_file_dialog(_token, null); }
    }
}

/// <summary>Args for <see cref="CefBrowser.ScrollOffsetChanged"/>.</summary>
public sealed class ScrollOffsetEventArgs : EventArgs
{
    public double X { get; }
    public double Y { get; }
    public ScrollOffsetEventArgs(double x, double y) { X = x; Y = y; }
}

/// <summary>Args for <see cref="CefBrowser.AutoResize"/>.</summary>
public sealed class AutoResizeEventArgs : EventArgs
{
    public int Width { get; }
    public int Height { get; }
    public AutoResizeEventArgs(int w, int h) { Width = w; Height = h; }
}

/// <summary>Args for <see cref="CefBrowser.DragImage"/>.</summary>
public sealed class DragImageEventArgs : EventArgs
{
    /// <summary>BGRA8888 premultiplied pixel buffer. <see cref="IntPtr.Zero"/> + <c>Width=Height=0</c> means "clear the overlay".</summary>
    public IntPtr Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Offset in pixels from cursor to image top-left.</summary>
    public int HotspotX { get; }
    public int HotspotY { get; }
    /// <summary>Convenience: true when this event clears the overlay.</summary>
    public bool IsClear => Buffer == IntPtr.Zero || Width <= 0 || Height <= 0;

    public DragImageEventArgs(IntPtr buffer, int width, int height, int hotspotX, int hotspotY)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
    }
}

/// <summary>One entry from <see cref="CefBrowser.GetNavigationEntriesAsync"/>.</summary>
public sealed record NavigationEntry(
    string Url,
    string DisplayUrl,
    string OriginalUrl,
    string Title,
    int TransitionType,
    int HttpStatusCode,
    long CompletionTimeMs,
    bool IsValid,
    bool IsCurrent);

/// <summary>Args for <see cref="CefBrowser.DevToolsMessage"/>.</summary>
public sealed class DevToolsMessageEventArgs : EventArgs
{
    /// <summary>True if this is an unsolicited server event (e.g. Network.requestWillBeSent), false if it's a reply.</summary>
    public bool IsEvent { get; }
    /// <summary>For replies, the message_id of the request. 0 for events.</summary>
    public int MessageId { get; }
    /// <summary>The raw JSON message.</summary>
    public string Json { get; }
    internal DevToolsMessageEventArgs(bool isEvent, int messageId, string json)
    {
        IsEvent = isEvent;
        MessageId = messageId;
        Json = json;
    }
}

/// <summary>Args for <see cref="CefBrowser.FocusRequested"/>.</summary>
public sealed class SetFocusEventArgs : EventArgs
{
    public Cef.FocusSource Source { get; }
    /// <summary>Set true to deny CEF the focus.</summary>
    public bool Cancel { get; set; }
    internal SetFocusEventArgs(Cef.FocusSource source) { Source = source; }
}

/// <summary>Args for <see cref="CefBrowser.PreKeyEvent"/> and <see cref="CefBrowser.KeyEvent"/>.</summary>
public sealed class PreKeyEventArgs : EventArgs
{
    public Cef.CefKeyEventType Type { get; }
    public Cef.CefModifiers Modifiers { get; }
    public int WindowsKeyCode { get; }
    public int NativeKeyCode { get; }
    public bool IsSystemKey { get; }
    /// <summary>Set true to indicate the host handled the key — page is not notified.</summary>
    public bool Handled { get; set; }

    internal PreKeyEventArgs(Cef.CefKeyEventType type, Cef.CefModifiers mods,
                              int vk, int native, bool isSystem)
    {
        Type = type;
        Modifiers = mods;
        WindowsKeyCode = vk;
        NativeKeyCode = native;
        IsSystemKey = isSystem;
    }
}

/// <summary>Args for <see cref="CefBrowser.CertError"/>.</summary>
public sealed class CertErrorEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    /// <summary>Specific cert error from <see cref="Cef.CefErrorCode"/> (e.g. CertAuthorityInvalid, CertDateInvalid).</summary>
    public Cef.CefErrorCode ErrorCode { get; }
    public string RequestUrl { get; }
    public string SubjectCommonName { get; }
    public string IssuerCommonName { get; }

    internal CertErrorEventArgs(ulong token, Cef.CefErrorCode errorCode,
                                  string requestUrl, string subjectCn, string issuerCn)
    {
        _token = token;
        ErrorCode = errorCode;
        RequestUrl = requestUrl;
        SubjectCommonName = subjectCn;
        IssuerCommonName = issuerCn;
    }

    /// <summary>Trust this cert for the current request. Idempotent.</summary>
    public void Proceed()
    {
        if (Interlocked.Exchange(ref _resolved, 1) != 0) return;
        Excef.excef_resolve_cert_error(_token, 1);
    }

    /// <summary>Block the load (page sees the error).</summary>
    public void Cancel()
    {
        if (Interlocked.Exchange(ref _resolved, 1) != 0) return;
        Excef.excef_resolve_cert_error(_token, 0);
    }
}

/// <summary>Args for <see cref="CefBrowser.BeforePopup"/>.</summary>
public sealed class BeforePopupEventArgs : EventArgs
{
    /// <summary>URL the page asked to open.</summary>
    public string TargetUrl { get; }
    /// <summary><c>window.open</c>'s second argument; empty if not provided.</summary>
    public string TargetFrameName { get; }
    public Cef.WindowOpenDisposition Disposition { get; }
    /// <summary>True if a user gesture (click) initiated the open; false for scripted opens.</summary>
    public bool UserGesture { get; }

    internal BeforePopupEventArgs(string targetUrl, string targetFrameName,
                                   Cef.WindowOpenDisposition disposition, bool userGesture)
    {
        TargetUrl = targetUrl;
        TargetFrameName = targetFrameName;
        Disposition = disposition;
        UserGesture = userGesture;
    }
}

/// <summary>The actual reserved popup browser; adopt it synchronously and set IsHosted.</summary>
public sealed class HostPopupEventArgs(CefBrowser browser, string targetUrl,
    string targetFrameName, Cef.WindowOpenDisposition disposition, bool userGesture) : EventArgs
{
    public CefBrowser Browser { get; } = browser;
    public string TargetUrl { get; } = targetUrl;
    public string TargetFrameName { get; } = targetFrameName;
    public Cef.WindowOpenDisposition Disposition { get; } = disposition;
    public bool UserGesture { get; } = userGesture;
    public bool IsHosted { get; set; }
}

/// <summary>Args for <see cref="CefBrowser.PermissionRequest"/>.</summary>
public sealed class PermissionRequestEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    /// <summary>CEF prompt id, stable across re-prompts for the same page request.</summary>
    public ulong PromptId { get; }
    /// <summary>Origin (scheme + host + port) of the page requesting the permission.</summary>
    public string Origin { get; }
    /// <summary>Bitmask of requested permission types.</summary>
    public Cef.PermissionRequestType RequestedPermissions { get; }

    internal PermissionRequestEventArgs(ulong token, ulong promptId, string origin, Cef.PermissionRequestType requested)
    {
        _token = token;
        PromptId = promptId;
        Origin = origin;
        RequestedPermissions = requested;
    }

    /// <summary>Resolve with the given result. Idempotent.</summary>
    public void Continue(Cef.PermissionResult result)
    {
        if (Interlocked.Exchange(ref _resolved, 1) != 0) return;
        Excef.excef_resolve_permission_prompt(_token, (int)result);
    }
    public void Allow()   => Continue(Cef.PermissionResult.Accept);
    public void Deny()    => Continue(Cef.PermissionResult.Deny);
    public void Dismiss() => Continue(Cef.PermissionResult.Dismiss);
}

/// <summary>Args for <see cref="CefBrowser.MediaAccessRequest"/>.</summary>
public sealed class MediaAccessRequestEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    public string Origin { get; }
    public Cef.MediaAccessPermissions RequestedPermissions { get; }

    internal MediaAccessRequestEventArgs(ulong token, string origin, Cef.MediaAccessPermissions requested)
    {
        _token = token;
        Origin = origin;
        RequestedPermissions = requested;
    }

    /// <summary>Resolve with a subset of <see cref="RequestedPermissions"/> granted, or <c>None</c> to deny.</summary>
    public void Continue(Cef.MediaAccessPermissions granted)
    {
        if (Interlocked.Exchange(ref _resolved, 1) != 0) return;
        Excef.excef_resolve_media_access(_token, (int)granted);
    }
    public void AllowAll() => Continue(RequestedPermissions);
    public void Deny()     => Continue(Cef.MediaAccessPermissions.None);
}

/// <summary>Args for <see cref="CefBrowser.DragStarted"/>.</summary>
public sealed class DragStartedEventArgs : EventArgs
{
    /// <summary>Drag operations the source allows.</summary>
    public Cef.DragOperations AllowedOperations { get; }
    /// <summary>Start position in view DIPs.</summary>
    public int X { get; }
    public int Y { get; }
    /// <summary>Plain-text drag content. Empty if not provided.</summary>
    public string Text { get; }
    /// <summary>HTML fragment drag content. Empty if not provided.</summary>
    public string Html { get; }
    /// <summary>For link drags, the URL being dragged. Empty otherwise.</summary>
    public string LinkUrl { get; }
    /// <summary>For link drags, the displayed title. Empty otherwise.</summary>
    public string LinkTitle { get; }
    /// <summary>File paths being dragged (HTML <c>&lt;input type=file&gt;</c> drag-out).</summary>
    public IReadOnlyList<string> FileNames { get; }
    /// <summary>
    /// Set to true to take ownership of the drag. The host must drive the
    /// OS-level drag itself and call <see cref="CefBrowser.DragSourceEndedAt"/>
    /// + <see cref="CefBrowser.DragSourceSystemDragEnded"/> when it ends.
    /// Leave false to let the shim self-target (internal-only DnD).
    /// </summary>
    public bool Handled { get; set; }

    public DragStartedEventArgs(
        Cef.DragOperations allowedOps, int x, int y,
        string text, string html, string linkUrl, string linkTitle,
        IReadOnlyList<string> fileNames)
    {
        AllowedOperations = allowedOps;
        X = x; Y = y;
        Text = text; Html = html;
        LinkUrl = linkUrl; LinkTitle = linkTitle;
        FileNames = fileNames;
    }
}

/// <summary>Args for <see cref="CefBrowser.LoadStart"/>.</summary>
public sealed class LoadStartEventArgs : EventArgs
{
    public bool IsMainFrame { get; }
    public string Url { get; }
    public LoadStartEventArgs(bool isMainFrame, string url) { IsMainFrame = isMainFrame; Url = url; }
}

/// <summary>Args for <see cref="CefBrowser.LoadEnd"/>.</summary>
public sealed class LoadEndEventArgs : EventArgs
{
    public bool IsMainFrame { get; }
    public string Url { get; }
    /// <summary>HTTP status code (200, 404, …). 0 for non-HTTP loads (data:, file:, about:).</summary>
    public int HttpStatusCode { get; }
    public LoadEndEventArgs(bool isMainFrame, string url, int status)
    {
        IsMainFrame = isMainFrame; Url = url; HttpStatusCode = status;
    }
}

/// <summary>Args for <see cref="CefBrowser.LoadError"/>.</summary>
public sealed class LoadErrorEventArgs : EventArgs
{
    public bool IsMainFrame { get; }
    /// <summary>Error code from <see cref="Cef.CefErrorCode"/>. ERR_ABORTED (-3) means intentional cancel.</summary>
    public Cef.CefErrorCode ErrorCode { get; }
    public string ErrorText { get; }
    public string FailedUrl { get; }
    public LoadErrorEventArgs(bool isMainFrame, Cef.CefErrorCode code, string text, string failedUrl)
    {
        IsMainFrame = isMainFrame;
        ErrorCode = code;
        ErrorText = text;
        FailedUrl = failedUrl;
    }
}

/// <summary>Console message delivered to <see cref="CefBrowser.ConsoleMessage"/>.</summary>
public sealed class ConsoleMessageEventArgs : EventArgs
{
    /// <summary>Severity level from <c>cef_log_severity_t</c>.</summary>
    public Cef.CefLogSeverity Level { get; }
    /// <summary>The formatted message text.</summary>
    public string Message { get; }
    /// <summary>Source script URL (empty for inline scripts).</summary>
    public string Source { get; }
    /// <summary>1-based line number.</summary>
    public int Line { get; }
    public ConsoleMessageEventArgs(Cef.CefLogSeverity level, string message, string source, int line)
    {
        Level = level;
        Message = message;
        Source = source;
        Line = line;
    }
}

/// <summary>Which CefFrameHandler event was raised.</summary>
public enum FrameLifecycleEvent
{
    Created = 0,
    Attached = 1,
    Detached = 2,
}

/// <summary>
/// Args for CefFrameHandler frame events. The IDs are CefFrame::GetIdentifier
/// strings (stable across the frame's lifetime within a process).
/// </summary>
public sealed class FrameLifecycleEventArgs : EventArgs
{
    public FrameLifecycleEvent Event { get; }
    public string FrameId { get; }
    /// <summary>Empty for the main frame (no parent).</summary>
    public string ParentFrameId { get; }
    /// <summary>Frame name from &lt;iframe name=...&gt; or window.name. May be empty.</summary>
    public string Name { get; }
    public string Url { get; }
    public bool IsMain { get; }
    public FrameLifecycleEventArgs(FrameLifecycleEvent ev, string frameId, string parentFrameId, string name, string url, bool isMain)
    {
        Event = ev;
        FrameId = frameId;
        ParentFrameId = parentFrameId;
        Name = name;
        Url = url;
        IsMain = isMain;
    }
}

/// <summary>Args for CefFrameHandler::OnMainFrameChanged.</summary>
public sealed class MainFrameChangedEventArgs : EventArgs
{
    /// <summary>Empty when the main frame is being created for the first time.</summary>
    public string OldFrameId { get; }
    /// <summary>Empty when the main frame is going away (browser closing).</summary>
    public string NewFrameId { get; }
    public MainFrameChangedEventArgs(string oldFrameId, string newFrameId)
    {
        OldFrameId = oldFrameId;
        NewFrameId = newFrameId;
    }
}

/// <summary>
/// Stream parameters reported by CefAudioHandler::OnAudioStreamStarted.
/// <see cref="Channels"/> is what subsequent <see cref="AudioPacketEventArgs"/>
/// callbacks will deliver in interleaved order.
/// </summary>
public sealed class AudioStreamStartedEventArgs : EventArgs
{
    /// <summary>cef_channel_layout_t enum value (raw integer).</summary>
    public int ChannelLayout { get; }
    public int SampleRate { get; }
    public int FramesPerBuffer { get; }
    public int Channels { get; }
    public AudioStreamStartedEventArgs(int channelLayout, int sampleRate, int framesPerBuffer, int channels)
    {
        ChannelLayout = channelLayout;
        SampleRate = sampleRate;
        FramesPerBuffer = framesPerBuffer;
        Channels = channels;
    }
}

/// <summary>
/// A single audio packet of interleaved float PCM. <see cref="Buffer"/>
/// points at <c>Frames × Channels</c> floats; the pointer is only valid
/// for the duration of the handler. Copy what you need.
/// </summary>
public sealed class AudioPacketEventArgs : EventArgs
{
    /// <summary>Pointer to <c>Frames × Channels</c> interleaved float32 samples.</summary>
    public IntPtr Buffer { get; }
    public int Frames { get; }
    public int Channels { get; }
    /// <summary>Presentation timestamp in microseconds since stream start.</summary>
    public long PtsMicroseconds { get; }
    public AudioPacketEventArgs(IntPtr buffer, int frames, int channels, long ptsUs)
    {
        Buffer = buffer;
        Frames = frames;
        Channels = channels;
        PtsMicroseconds = ptsUs;
    }

    /// <summary>Convenience: copy the PCM into a managed float[] sized Frames*Channels.</summary>
    public unsafe float[] CopyToArray()
    {
        var arr = new float[Frames * Channels];
        fixed (float* dst = arr)
        {
            System.Buffer.MemoryCopy((void*)Buffer, dst, arr.Length * sizeof(float), arr.Length * sizeof(float));
        }
        return arr;
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.TouchHandleSizeRequested"/>. The handler
/// sets <see cref="Width"/> and <see cref="Height"/> in DIPs; leaving them
/// at 0 means "use Chromium's default."
/// </summary>
public sealed class TouchHandleSizeRequest : EventArgs
{
    public Cef.HorizontalAlignment Orientation { get; }
    public int Width { get; set; }
    public int Height { get; set; }
    public TouchHandleSizeRequest(Cef.HorizontalAlignment orientation)
    {
        Orientation = orientation;
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.TouchHandleStateChanged"/>. Mirrors
/// <c>cef_touch_handle_state_t</c>. Each call only updates the fields
/// whose flag bit is set in <see cref="Flags"/>; the rest are stale but
/// still echoed for convenience.
/// </summary>
public sealed class TouchHandleStateEventArgs : EventArgs
{
    /// <summary>Touch-handle id, incremented for each new handle.</summary>
    public int HandleId { get; }
    /// <summary>Bitmask of cef_touch_handle_state_flags_t values (which fields are present).</summary>
    public uint Flags { get; }
    public bool Enabled { get; }
    public Cef.HorizontalAlignment Orientation { get; }
    public bool MirrorVertical { get; }
    public bool MirrorHorizontal { get; }
    public int OriginX { get; }
    public int OriginY { get; }
    public float Alpha { get; }

    public TouchHandleStateEventArgs(int handleId, uint flags, bool enabled,
        Cef.HorizontalAlignment orientation, bool mirrorV, bool mirrorH,
        int originX, int originY, float alpha)
    {
        HandleId = handleId;
        Flags = flags;
        Enabled = enabled;
        Orientation = orientation;
        MirrorVertical = mirrorV;
        MirrorHorizontal = mirrorH;
        OriginX = originX;
        OriginY = originY;
        Alpha = alpha;
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.ImeCompositionRangeChanged"/>. Selection
/// is in character offsets; <see cref="CharacterBounds"/> rects are in
/// view DIPs and snapshot-copied (safe to retain after the handler exits).
/// </summary>
public sealed class ImeCompositionRangeEventArgs : EventArgs
{
    public int SelectionStart { get; }
    public int SelectionEnd { get; }
    public System.Drawing.Rectangle[] CharacterBounds { get; }
    public ImeCompositionRangeEventArgs(int selectionStart, int selectionEnd, System.Drawing.Rectangle[] bounds)
    {
        SelectionStart = selectionStart;
        SelectionEnd = selectionEnd;
        CharacterBounds = bounds;
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.AcceleratedPaint"/>. The shared-texture
/// handle is platform-specific and only valid for the duration of the
/// event handler — the host MUST copy / consume before returning.
/// </summary>
public sealed class AcceleratedPaintEventArgs : EventArgs
{
    public Cef.PaintElementType ElementType { get; }
    /// <summary>Full coded width of the frame in physical pixels.</summary>
    public int CodedWidth { get; }
    /// <summary>Full coded height of the frame in physical pixels.</summary>
    public int CodedHeight { get; }
    public Cef.CefColorType Format { get; }
    /// <summary>Timestamp in microseconds since capture start.</summary>
    public long TimestampMicroseconds { get; }
    /// <summary>
    /// Platform-specific shared-texture handle:
    /// macOS: <c>IOSurfaceRef</c>. Windows: NT shared handle (HANDLE).
    /// Linux: dma-buf fd (int).
    /// </summary>
    public IntPtr SharedHandle { get; }

    public AcceleratedPaintEventArgs(Cef.PaintElementType type, int codedWidth, int codedHeight,
        Cef.CefColorType format, long timestampUs, IntPtr sharedHandle)
    {
        ElementType = type;
        CodedWidth = codedWidth;
        CodedHeight = codedHeight;
        Format = format;
        TimestampMicroseconds = timestampUs;
        SharedHandle = sharedHandle;
    }
}
