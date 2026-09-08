using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Exclr8Cef.Native;

namespace Exclr8Cef;

/// <summary>
/// A single off-screen Chromium browser instance. Created via
/// <see cref="Cef.CreateOffscreenBrowser"/>. Exposes the full per-browser
/// event stack (navigation, title, loading, cursor, paint, …) and the
/// per-browser command surface (navigation, input forwarding, JavaScript,
/// clipboard, zoom, IME, PDF). UI-tech specific controls
/// (<c>Exclr8Cef.WebView</c> for Avalonia, hypothetical WPF/MAUI/headless
/// variants) wrap an instance of this class and forward only the bits
/// they need to wire to their host framework.
/// </summary>
public sealed partial class CefBrowser : IDisposable
{
    /// <summary>Browser id assigned by Exclr8Cef (≥ 1). Zero after close.</summary>
    public int Id { get; private set; }

    private string _url = "about:blank";
    private string _title = "";
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private double _zoomLevel;
    private bool _closed;
    private int _closeRequestState;

    /// <summary>The currently-loaded URL (kept in sync with CEF's AddressChange).</summary>
    public string Url => _url;

    /// <summary>The currently-loaded page title.</summary>
    public string Title => _title;

    public bool IsLoading => _isLoading;
    public bool CanGoBack => _canGoBack;
    public bool CanGoForward => _canGoForward;
    public bool IsClosed => _closed;

    private bool _initialized;
    /// <summary>
    /// True after CEF has constructed the underlying browser and it is
    /// ready for operations that need a CefBrowser ref (Copy/Paste/Cut,
    /// frame access, clipboard, dev-tools open, …). The numeric
    /// <see cref="Id"/> is valid immediately on return from
    /// <see cref="Cef.CreateOffscreenBrowser"/>, but those ops are
    /// no-ops until <see cref="Initialized"/> fires.
    /// </summary>
    public bool IsInitialized => _initialized;

    // ---- Events ---------------------------------------------------------
    //
    // All instance events. Sender is this CefBrowser. Fires on the CEF
    // pump thread — hosts that update UI should marshal to their UI
    // thread themselves (e.g. Dispatcher.UIThread.Post in Avalonia).

    /// <summary>Fires when the main-frame URL changes.</summary>
    public event EventHandler<string>? AddressChanged;

    /// <summary>Fires when the page title changes.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>Fires when loading state changes (isLoading / canGoBack / canGoForward).</summary>
    public event EventHandler<LoadingState>? LoadingStateChanged;

    /// <summary>Fires when the page requests a different cursor type (CSS <c>cursor:</c>).</summary>
    public event EventHandler<Cef.CefCursorType>? CursorChanged;

    /// <summary>
    /// Fires for every <c>console.{log,info,warn,error,debug}</c> call from
    /// the page and for Chromium-emitted runtime warnings (CORS, deprecation, …).
    /// </summary>
    public event EventHandler<ConsoleMessageEventArgs>? ConsoleMessage;

    /// <summary>Per-frame load started. Fires once per frame; check <c>IsMainFrame</c> for top-level.</summary>
    public event EventHandler<LoadStartEventArgs>? LoadStart;

    /// <summary>Per-frame load finished. <c>HttpStatusCode</c> is 0 for non-HTTP loads (data:/file:/about:).</summary>
    public event EventHandler<LoadEndEventArgs>? LoadEnd;

    /// <summary>Per-frame load error. ErrorCode is from <see cref="Cef.CefErrorCode"/>; ERR_ABORTED (-3) means navigation was intentionally cancelled.</summary>
    public event EventHandler<LoadErrorEventArgs>? LoadError;

    /// <summary>Main-frame loading progress as a value in [0.0, 1.0]. Fires repeatedly during a load.</summary>
    public event EventHandler<double>? LoadingProgress;

    /// <summary>Status-bar text, typically the URL of the link under the mouse cursor (empty when not hovering a link).</summary>
    public event EventHandler<string>? StatusMessage;

    /// <summary>Tooltip text from <c>title=</c> attributes / <c>aria-describedby</c>.</summary>
    public event EventHandler<string>? TooltipChanged;

    /// <summary>The highest-priority favicon URL declared by the page (empty if none).</summary>
    public event EventHandler<string>? FaviconChanged;

    /// <summary>Page entered or left HTML5 fullscreen.</summary>
    public event EventHandler<bool>? FullscreenModeChanged;

    /// <summary>
    /// Fires whenever the page's scroll position changes (in CSS pixels).
    /// May fire many times per second during smooth-scrolling — hosts that
    /// want a coarser stream should throttle locally.
    /// </summary>
    public event EventHandler<ScrollOffsetEventArgs>? ScrollOffsetChanged;

    /// <summary>
    /// Fires when the page invokes <c>alert()</c>, <c>confirm()</c>,
    /// <c>prompt()</c>, or <c>onbeforeunload</c>. Hosts MUST call either
    /// <see cref="JsDialogEventArgs.Continue"/> or
    /// <see cref="JsDialogEventArgs.Cancel"/> to dismiss the dialog and
    /// unblock the renderer; failing to do so will leave the page hung
    /// at the dialog call. If no subscriber exists, CEF's default
    /// behaviour (Chromium-rendered system-style dialog) runs instead.
    /// </summary>
    public event EventHandler<JsDialogEventArgs>? JsDialog;

    /// <summary>
    /// Fires when the page triggers a file chooser (<c>&lt;input type=file&gt;</c>,
    /// <c>showOpenFilePicker</c>, save-as on a download). Hosts MUST call
    /// either <see cref="FileDialogEventArgs.Continue"/> with one or more
    /// paths, or <see cref="FileDialogEventArgs.Cancel"/>. Without a
    /// subscriber, CEF's default file picker runs.
    /// </summary>
    public event EventHandler<FileDialogEventArgs>? FileDialog;

    /// <summary>
    /// Fires on right-click / long-press. In OSR mode CEF cannot render
    /// the menu itself, so the host MUST render its own using the
    /// supplied items and call <see cref="ContextMenuEventArgs.Continue"/>
    /// with the chosen id, or <see cref="ContextMenuEventArgs.Cancel"/>.
    /// Without a subscriber the menu is suppressed entirely.
    /// </summary>
    public event EventHandler<ContextMenuEventArgs>? ContextMenu;

    /// <summary>
    /// Fires once when a download begins, before any bytes are written.
    /// Host must call <see cref="DownloadStartingEventArgs.Continue"/>
    /// with a save path, or <see cref="DownloadStartingEventArgs.Cancel"/>.
    /// Without a subscriber the file is saved with a default name.
    /// </summary>
    public event EventHandler<DownloadStartingEventArgs>? DownloadStarting;

    /// <summary>
    /// Fires repeatedly while a download is in flight (typically a few
    /// times per second). The args carry the current snapshot plus
    /// Cancel / Pause / Resume actions. Actions must be called
    /// synchronously inside the event handler — the underlying CEF
    /// callback is per-invocation.
    /// </summary>
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgress;

    /// <summary>
    /// Fires when the server (or proxy) returns a 401/407 with an
    /// HTTP-Basic / Digest / NTLM challenge. Host MUST call
    /// <see cref="AuthRequestEventArgs.Continue"/> with credentials,
    /// or <see cref="AuthRequestEventArgs.Cancel"/>. Without a
    /// subscriber the request fails.
    /// </summary>
    public event EventHandler<AuthRequestEventArgs>? AuthRequest;

    /// <summary>
    /// Fires once or more per <see cref="Find"/> call with the current
    /// match count + ordinal. Observational only — no callback to dismiss.
    /// </summary>
    public event EventHandler<FindResultEventArgs>? FindResult;

    /// <summary>
    /// Fires when the renderer subprocess hosting this browser's page
    /// terminates unexpectedly (crash, OOM, killed). The OSR paint buffer
    /// freezes — hosts typically respond by calling <see cref="Reload"/>
    /// to spawn a fresh renderer.
    /// </summary>
    public event EventHandler<RenderProcessGoneEventArgs>? RenderProcessGone;

    /// <summary>
    /// Synchronous, cancellable gate for every main-frame navigation before
    /// Chromium commits it. Set <see cref="BeforeBrowseEventArgs.Cancel"/> to
    /// true to block the navigation. This is the durable place for top-level
    /// scheme/origin policy; keep <see cref="ResourceRequest"/> for subresource
    /// file/network policy.
    /// </summary>
    /// <remarks>
    /// Fires on CEF's UI thread and must return synchronously. If a subscriber
    /// throws, the navigation is canceled (fail closed). With no subscriber,
    /// navigation proceeds normally.
    /// </remarks>
    public event EventHandler<BeforeBrowseEventArgs>? BeforeBrowse;

    /// <summary>
    /// Fires once per outgoing network request — main-frame navigations,
    /// sub-resources (CSS / JS / images / favicons), XHR / fetch, web
    /// workers. Host can inspect the URL / method / current headers and
    /// either let the request proceed (optionally replacing headers via
    /// <see cref="ResourceRequestEventArgs.Continue"/>) or cancel via
    /// <see cref="ResourceRequestEventArgs.Cancel"/>. Without a
    /// subscriber the request goes through with no overhead.
    /// </summary>
    /// <remarks>
    /// This event is a GATE — every request blocks until your handler
    /// calls <c>Continue()</c> or <c>Cancel()</c>. Subscribe only when
    /// you actually want to intervene (header injection, block-list,
    /// rewrite). For pure observation (logging, devtools-style network
    /// panels) use <see cref="ResourceRequestObserved"/> instead, which
    /// auto-continues every request.
    /// </remarks>
    public event EventHandler<ResourceRequestEventArgs>? ResourceRequest;

    /// <summary>
    /// Non-gating sibling of <see cref="ResourceRequest"/>: fires for
    /// the same set of network requests but the request continues
    /// immediately — your handler can't block, cancel, or rewrite
    /// headers. Use this for `network_recent`-style logging,
    /// devtools-style activity panels, or anywhere you only need to
    /// watch traffic without participating in the request lifecycle.
    /// </summary>
    /// <remarks>
    /// Fires on CEF's network/IO thread (same as <see cref="ResourceRequest"/>) —
    /// keep handlers fast and marshal to your UI thread if you need to
    /// touch UI state.
    ///
    /// If both <see cref="ResourceRequest"/> and this event are
    /// subscribed, the observer fires first, then the gate runs. The
    /// observer sees every request, including ones the gate later
    /// cancels.
    /// </remarks>
    public event EventHandler<ResourceRequestObservedEventArgs>? ResourceRequestObserved;

    /// <summary>
    /// Fires when the page shows or hides a popup (HTML <c>&lt;select&gt;</c>
    /// dropdowns, autocomplete suggestions, etc.). Hosts that don't
    /// subscribe + render the popup buffer will see popups silently
    /// dropped — the WebView control wires this automatically.
    /// </summary>
    public event EventHandler<bool>? PopupShow;

    /// <summary>
    /// Popup geometry — position + size in DIP / CSS pixels relative to
    /// the browser's main view origin. Fires before <see cref="PopupShow"/>
    /// = true and again whenever the popup moves / resizes.
    /// </summary>
    public event EventHandler<PopupRect>? PopupSize;

    /// <summary>
    /// New pixels for the popup. BGRA8888, top-left, stride = width × 4 —
    /// same shape as <see cref="Painted"/> but for the popup buffer.
    /// Buffer is only valid for the duration of the handler call.
    /// </summary>
    public event EventHandler<PaintEventArgs>? PopupPainted;

    /// <summary>
    /// Fires when the page calls <c>window.exclr8cef.invoke(method, argsJson)</c>.
    /// The bridge is absent unless the process was initialized with
    /// <see cref="Cef.CefSettings.EnableJavascriptBridge"/> = true. Enable it
    /// only when every navigable document is trusted or protected by a strict
    /// <see cref="BeforeBrowse"/> origin policy.
    /// </summary>
    public event EventHandler<JsInvokeEventArgs>? JsInvoke;

    /// <summary>
    /// Fires with Chromium's serialized accessibility tree as JSON
    /// whenever the page's a11y structure changes. Only delivered after
    /// <see cref="SetAccessibilityEnabled"/>(true). Hosts that want to
    /// drive screen readers translate this tree into their UI
    /// framework's AutomationPeer hierarchy.
    /// </summary>
    public event EventHandler<string>? AccessibilityTreeChange;

    /// <summary>
    /// Fires with serialized location updates for previously-reported
    /// a11y nodes (bounding-box changes from scroll / resize). Only
    /// delivered after <see cref="SetAccessibilityEnabled"/>(true).
    /// </summary>
    public event EventHandler<string>? AccessibilityLocationChange;

    /// <summary>
    /// Fires after the page's natural content size changes, but only when
    /// auto-resize is enabled (see <see cref="SetAutoResizeEnabled"/>).
    /// Width / height are in CSS pixels.
    /// </summary>
    public event EventHandler<AutoResizeEventArgs>? AutoResize;

    /// <summary>
    /// The page wants to start dragging something (CefRenderHandler::StartDragging).
    /// If no handler subscribes, the shim falls back to internal-only DnD
    /// (self-targets the same browser as the drop target). A handler that
    /// sets <see cref="DragStartedEventArgs.Handled"/> = true takes
    /// responsibility for completing the drag and MUST eventually call
    /// <see cref="DragSourceEndedAt"/> + <see cref="DragSourceSystemDragEnded"/>.
    /// </summary>
    public event EventHandler<DragStartedEventArgs>? DragStarted;

    /// <summary>
    /// Delivers the drag-preview ("ghost") bitmap CEF generated for the
    /// active drag, so the host can overlay it under the cursor. Fires
    /// once when the drag starts (with the bitmap, or width=height=0 if
    /// no preview is available), and once when the drag ends (always
    /// width=height=0). Buffer is BGRA8888 premultiplied, valid only
    /// for the duration of the handler — copy what you need.
    /// </summary>
    public event EventHandler<DragImageEventArgs>? DragImage;

    /// <summary>
    /// The page requested a permission (notifications, geolocation, etc.)
    /// — CefPermissionHandler::OnShowPermissionPrompt. Host MUST call
    /// <see cref="PermissionRequestEventArgs.Continue"/> /
    /// <see cref="PermissionRequestEventArgs.Allow"/> /
    /// <see cref="PermissionRequestEventArgs.Deny"/>. If no handler subscribes,
    /// permissions are denied automatically (Alloy-style default).
    /// </summary>
    public event EventHandler<PermissionRequestEventArgs>? PermissionRequest;

    /// <summary>
    /// The page called <c>navigator.mediaDevices.getUserMedia</c> —
    /// CefPermissionHandler::OnRequestMediaAccessPermission. Host MUST call
    /// <see cref="MediaAccessRequestEventArgs.Continue"/> /
    /// <see cref="MediaAccessRequestEventArgs.AllowAll"/> /
    /// <see cref="MediaAccessRequestEventArgs.Deny"/>.
    /// </summary>
    public event EventHandler<MediaAccessRequestEventArgs>? MediaAccessRequest;

    /// <summary>
    /// The page tried to open a new browser (window.open, target=_blank).
    /// CefLifeSpanHandler::OnBeforePopup. When subscribed, the popup is
    /// ALWAYS cancelled at the CEF layer and the host decides what to do
    /// with the URL — open in a real browser, load in another WebView,
    /// suppress, etc. With no subscriber, CEF creates a popup browser
    /// (mostly unusable in OSR).
    /// </summary>
    public event EventHandler<BeforePopupEventArgs>? BeforePopup;
    public event EventHandler<HostPopupEventArgs>? HostPopup;

    /// <summary>
    /// TLS verification failed (self-signed, expired, hostname mismatch).
    /// CefRequestHandler::OnCertificateError. Host MUST call
    /// <see cref="CertErrorEventArgs.Proceed"/> or
    /// <see cref="CertErrorEventArgs.Cancel"/>. With no subscriber the
    /// load fails normally.
    /// </summary>
    public event EventHandler<CertErrorEventArgs>? CertError;

    /// <summary>
    /// The page wants to move focus OUT (Tab past last form field, etc.).
    /// CefFocusHandler::OnTakeFocus. Argument: <c>true</c> = move forward
    /// (Tab), <c>false</c> = backward (Shift+Tab). Host should pass focus
    /// to the next / previous Avalonia control around the WebView.
    /// </summary>
    public event EventHandler<bool>? TakeFocus;

    /// <summary>
    /// CEF is about to receive focus. CefFocusHandler::OnSetFocus.
    /// Host can cancel via <see cref="SetFocusEventArgs.Cancel"/>.
    /// </summary>
    public event EventHandler<SetFocusEventArgs>? FocusRequested;

    /// <summary>
    /// CEF received focus successfully. CefFocusHandler::OnGotFocus.
    /// </summary>
    public event EventHandler? GotFocus;

    /// <summary>
    /// CefKeyboardHandler::OnPreKeyEvent. Fires before the page sees the
    /// key event. Set <see cref="PreKeyEventArgs.Handled"/> = true to
    /// suppress page dispatch (use for global accelerators).
    /// </summary>
    public event EventHandler<PreKeyEventArgs>? PreKeyEvent;

    /// <summary>
    /// CefKeyboardHandler::OnKeyEvent. Fires AFTER the page sees the key
    /// (and didn't handle it). Set <see cref="PreKeyEventArgs.Handled"/>
    /// = true to indicate the host consumed the event.
    /// </summary>
    public event EventHandler<PreKeyEventArgs>? KeyEvent;

    /// <summary>
    /// CefFrameHandler frame lifecycle: created / attached / detached.
    /// Fires for every frame in the browser (main + iframes).
    /// </summary>
    public event EventHandler<FrameLifecycleEventArgs>? FrameLifecycle;

    /// <summary>
    /// CefFrameHandler::OnMainFrameChanged. Fires on cross-process navigations
    /// when the main frame is replaced by a new one (rare but real).
    /// </summary>
    public event EventHandler<MainFrameChangedEventArgs>? MainFrameChanged;

    /// <summary>CefAudioHandler::OnAudioStreamStarted.</summary>
    public event EventHandler<AudioStreamStartedEventArgs>? AudioStreamStarted;

    /// <summary>
    /// CefAudioHandler::OnAudioStreamPacket. Interleaved float PCM in the
    /// channel count + sample rate reported by AudioStreamStarted. The
    /// buffer pointer is only valid for the duration of the handler — copy
    /// what you need.
    /// </summary>
    public event EventHandler<AudioPacketEventArgs>? AudioPacket;

    /// <summary>CefAudioHandler::OnAudioStreamStopped.</summary>
    public event EventHandler? AudioStreamStopped;

    /// <summary>CefAudioHandler::OnAudioStreamError — message describes the cause.</summary>
    public event EventHandler<string>? AudioStreamError;

    // ---- OSR render-handler events (OSR-mode only) -----------------------
    //
    // None fire on the embedded NSView/HWND path — the platform widget owns
    // its paint, touch, IME, etc. They're meaningful only when CefBrowser
    // was created via CreateOffscreenBrowser / CreateOffscreenBrowserEx.

    /// <summary>
    /// CEF needs the size of a touch-selection handle in DIPs. Set
    /// <see cref="TouchHandleSizeRequest.Width"/> and
    /// <see cref="TouchHandleSizeRequest.Height"/> in the handler; default
    /// (no subscriber or handler leaves zero) means "use Chromium's default."
    /// </summary>
    public event EventHandler<TouchHandleSizeRequest>? TouchHandleSizeRequested;

    /// <summary>
    /// Touch-selection handle state update — orientation, position, alpha.
    /// Check <c>Flags</c> to know which fields are present on this update.
    /// </summary>
    public event EventHandler<TouchHandleStateEventArgs>? TouchHandleStateChanged;

    /// <summary>
    /// Chromium telling the host where the IME caret/composition rect is in
    /// view coordinates. Wire to the OS IME so its candidate window
    /// positions correctly.
    /// </summary>
    public event EventHandler<ImeCompositionRangeEventArgs>? ImeCompositionRangeChanged;

    /// <summary>
    /// Page requested an on-screen keyboard. Argument is the
    /// <see cref="Cef.CefTextInputMode"/> (None = hide).
    /// </summary>
    public event EventHandler<Cef.CefTextInputMode>? VirtualKeyboardRequested;

    /// <summary>
    /// GPU shared-texture paint. Only fires when the browser was created
    /// via <see cref="Cef.CreateOffscreenBrowserEx"/> with
    /// <see cref="Cef.OffscreenFlags.SharedTexture"/>. The shared handle
    /// in <see cref="AcceleratedPaintEventArgs.SharedHandle"/> is
    /// platform-specific (IOSurfaceRef on macOS, NT handle on Windows,
    /// dma-buf fd on Linux) and is only valid for the duration of the
    /// handler — copy / consume before returning.
    /// </summary>
    public event EventHandler<AcceleratedPaintEventArgs>? AcceleratedPaint;

    /// <summary>
    /// Fires once, when the underlying CefBrowser is constructed and ready
    /// for operations that need a CefBrowser ref. See <see cref="IsInitialized"/>.
    /// If subscription happens after the browser is already initialised,
    /// the handler is invoked synchronously on the subscribing thread —
    /// safe to use as a "do this once the browser is up" pattern.
    /// </summary>
    public event EventHandler? Initialized
    {
        add
        {
            _initializedHandlers += value;
            if (_initialized) value?.Invoke(this, EventArgs.Empty);
        }
        remove { _initializedHandlers -= value; }
    }
    private EventHandler? _initializedHandlers;

    /// <summary>Fires after the underlying CEF browser has been fully closed.</summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Fires when CEF has new pixels for this browser. Buffer is BGRA8888
    /// (top-left origin, stride = width × 4) and only valid for the duration
    /// of the handler — copy what you need.
    /// </summary>
    public event EventHandler<PaintEventArgs>? Painted;

    internal CefBrowser(int id) { Id = id; }

    // ---- Navigation -----------------------------------------------------

    public void LoadUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try { Excef.excef_load_url(Id, p); }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void GoBack()    { if (!_closed) Excef.excef_go_back(Id); }
    public void GoForward() { if (!_closed) Excef.excef_go_forward(Id); }
    public void Reload(bool ignoreCache = false)
    {
        if (!_closed) Excef.excef_reload(Id, ignoreCache ? 1 : 0);
    }
    public void StopLoad() { if (!_closed) Excef.excef_stop_load(Id); }

    /// <summary>
    /// Async — returns the rendered HTML source of the main frame.
    /// CefFrame::GetSource. Useful for debugging / page-scraping scenarios.
    /// </summary>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread.
    /// If you call another <see cref="CefBrowser"/> method on the awaited
    /// result, marshal back to the UI thread first (e.g.
    /// <c>Avalonia.Threading.Dispatcher.UIThread.Post</c>).
    /// </remarks>
    public Task<string> GetSourceAsync() => GetVisitorString(Excef.excef_get_frame_source);
    /// <summary>
    /// Async — returns the rendered plain text of the main frame
    /// (innerText-equivalent). CefFrame::GetText.
    /// </summary>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread.
    /// Marshal back to the UI thread before any follow-up
    /// <see cref="CefBrowser"/> call.
    /// </remarks>
    public Task<string> GetTextAsync() => GetVisitorString(Excef.excef_get_frame_text);

    private Task<string> GetVisitorString(Func<int, int, int> caller)
    {
        if (_closed) return Task.FromException<string>(new InvalidOperationException("browser closed"));
        int reqId = Interlocked.Increment(ref Cef.s_nextStringVisitorId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Cef.s_stringVisitorRequests[reqId] = tcs;
        // Track per-browser so RaiseClosed can fail the awaiter instead of
        // letting it hang when the browser closes mid-visit.
        _stringVisitorRequestIds[reqId] = 0;
        if (caller(Id, reqId) == 0)
        {
            Cef.s_stringVisitorRequests.TryRemove(reqId, out _);
            _stringVisitorRequestIds.TryRemove(reqId, out _);
            tcs.TrySetException(new InvalidOperationException("frame call failed"));
        }
        else
        {
            tcs.Task.ContinueWith(
                t => _stringVisitorRequestIds.TryRemove(reqId, out _),
                TaskContinuationOptions.ExecuteSynchronously);
        }
        return tcs.Task;
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _stringVisitorRequestIds = new();

    /// <summary>
    /// Load a fully-configured HTTP request (custom method, post body,
    /// headers). For simple GETs use <see cref="LoadUrl"/>.
    /// </summary>
    /// <param name="method">HTTP method ("GET", "POST", ...)</param>
    /// <param name="url">target URL</param>
    /// <param name="postBody">optional request body</param>
    /// <param name="headers">optional headers as "Name: Value\nName: Value"</param>
    public void LoadRequest(string method, string url, byte[]? postBody = null, string? headers = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);
        if (_closed) return;
        unsafe
        {
            sbyte* m = (sbyte*)Marshal.StringToCoTaskMemUTF8(method);
            sbyte* u = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            sbyte* h = headers is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(headers);
            try
            {
                if (postBody is null || postBody.Length == 0)
                {
                    Excef.excef_load_request(Id, m, u, null, 0, h);
                }
                else
                {
                    fixed (byte* p = postBody)
                        Excef.excef_load_request(Id, m, u, p, postBody.Length, h);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)m);
                Marshal.FreeCoTaskMem((IntPtr)u);
                if (h != null) Marshal.FreeCoTaskMem((IntPtr)h);
            }
        }
    }

    /// <summary>
    /// Returns the back/forward navigation entries for this browser via
    /// CefBrowserHost::GetNavigationEntries.
    /// </summary>
    /// <param name="currentOnly">If true, returns just the current entry; otherwise the whole history.</param>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread.
    /// Marshal back to the UI thread before any follow-up
    /// <see cref="CefBrowser"/> call.
    /// </remarks>
    public Task<System.Collections.Generic.List<NavigationEntry>> GetNavigationEntriesAsync(bool currentOnly = false)
    {
        if (_closed) return Task.FromException<System.Collections.Generic.List<NavigationEntry>>(new InvalidOperationException("browser closed"));
        int reqId = Interlocked.Increment(ref Cef.s_nextNavEntryId);
        var tcs = new TaskCompletionSource<System.Collections.Generic.List<NavigationEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Cef.s_navEntryRequests[reqId] = tcs;
        _navEntryRequestIds[reqId] = 0;
        if (Excef.excef_get_navigation_entries(Id, reqId, currentOnly ? 1 : 0) == 0)
        {
            Cef.s_navEntryRequests.TryRemove(reqId, out _);
            _navEntryRequestIds.TryRemove(reqId, out _);
            tcs.TrySetException(new InvalidOperationException("browser unknown"));
        }
        else
        {
            tcs.Task.ContinueWith(
                t => _navEntryRequestIds.TryRemove(reqId, out _),
                TaskContinuationOptions.ExecuteSynchronously);
        }
        return tcs.Task;
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _navEntryRequestIds = new();

    /// <summary>
    /// Load an HTML string into the main frame, served as a
    /// <c>data:text/html;base64,…</c> URL. No network round-trip and no
    /// way to forge a real-looking origin — the location bar will show
    /// the data: URL and relative-link resolution is against it.
    /// </summary>
    /// <remarks>
    /// If you need the page to behave as if served from a particular
    /// origin (for relative URLs, document.origin, cookies, etc.),
    /// register a custom scheme via <c>EXCLR8CEF_SCHEMES</c> + your own
    /// scheme handler factory and navigate to <c>your-scheme://…</c>
    /// instead. <see cref="LoadString"/> is intentionally minimal.
    ///
    /// Don't call this from <c>BrowserReady</c>: CEF's initial
    /// about:blank load may still be pending and the navigation will
    /// be dropped silently. Wait for the first <c>LoadEnd</c> (or any
    /// load past about:blank) before issuing it.
    ///
    /// <para><b>Large documents.</b> Chromium hard-caps URLs at 2 MB, so
    /// HTML over ~1.5 MB (base64 inflation) cannot travel as a
    /// <c>data:</c> URL — Chromium would drop the navigation without any
    /// load event. Such documents are served automatically through an
    /// internal in-memory resource handler instead: same content, same
    /// load events, no size limit — but the location bar shows
    /// <c>https://loadstring.exclr8cef.internal/&lt;token&gt;</c> rather
    /// than the data: URL, and the document origin is that synthetic
    /// (secure) origin instead of an opaque data: origin.</para>
    /// </remarks>
    public void LoadString(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (_closed) return;
        unsafe
        {
            sbyte* h = (sbyte*)Marshal.StringToCoTaskMemUTF8(html);
            try { Excef.excef_load_string(Id, h); }
            finally { Marshal.FreeCoTaskMem((IntPtr)h); }
        }
    }

    // ---- Sizing / paint pipeline ---------------------------------------

    /// <summary>Notify CEF that the off-screen view rect has changed (DIP size).</summary>
    public void Resize(int width, int height)
    {
        if (!_closed) Excef.excef_resize_offscreen_browser(Id, width, height);
    }

    /// <summary>
    /// Update the device scale factor (e.g. dragging across monitors with
    /// different DPI). CEF re-lays-out and emits a fresh paint at the new
    /// physical-pixel size.
    /// </summary>
    public void SetDeviceScaleFactor(float scale)
    {
        if (!_closed) Excef.excef_set_device_scale_factor(Id, scale);
    }

    public void WasHidden(bool hidden)
    {
        if (!_closed) Excef.excef_was_hidden(Id, hidden ? 1 : 0);
    }

    // ---- OSR low-level controls (mirror of CefBrowserHost::*) ---------

    /// <summary>
    /// Force CEF to repaint the given element. <see cref="Cef.PaintElementType.Main"/>
    /// for the main view, <see cref="Cef.PaintElementType.Popup"/> for a
    /// visible &lt;select&gt; / autocomplete / picker.
    /// </summary>
    public void Invalidate(Cef.PaintElementType type = Cef.PaintElementType.Main)
    {
        if (!_closed) Excef.excef_invalidate(Id, (int)type);
    }

    /// <summary>
    /// Tell CEF the OS captured the mouse away from the page (alt-tab,
    /// lock screen, modal dialog). Resets in-flight mousedown state.
    /// </summary>
    public void SendCaptureLostEvent()
    {
        if (!_closed) Excef.excef_send_capture_lost_event(Id);
    }

    /// <summary>
    /// Open the OS print dialog for this browser. Different from
    /// <c>PrintToPdfAsync</c> (which is silent + fixed settings) —
    /// <c>Print()</c> shows the system printer picker.
    /// </summary>
    public void Print()
    {
        if (!_closed) Excef.excef_print(Id);
    }

    /// <summary>Programmatically start a download for the given URL.</summary>
    public void StartDownload(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try { Excef.excef_start_download(Id, p); }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    /// <summary>
    /// Windowless frame rate cap. Browser-creation default is 30 fps;
    /// runtime-bump for media playback, drop to 1 for backgrounded tabs.
    /// </summary>
    public int WindowlessFrameRate
    {
        get => _closed ? 0 : Excef.excef_get_windowless_frame_rate(Id);
        set { if (!_closed) Excef.excef_set_windowless_frame_rate(Id, value); }
    }

    /// <summary>
    /// Forward a touch event to the embedded page. Required for
    /// touch-screen Avalonia builds — CEF doesn't see OS touch events
    /// directly in OSR mode.
    /// </summary>
    public void SendTouchEvent(int id, float x, float y,
                                Cef.CefTouchEventType type,
                                float radiusX = 0, float radiusY = 0,
                                float rotationAngle = 0, float pressure = 1.0f,
                                Cef.CefModifiers modifiers = Cef.CefModifiers.None,
                                Cef.CefPointerType pointerType = Cef.CefPointerType.Touch)
    {
        if (!_closed)
            Excef.excef_send_touch_event(Id, id, x, y, radiusX, radiusY,
                                          rotationAngle, pressure,
                                          (int)type, (int)modifiers,
                                          (int)pointerType);
    }

    /// <summary>
    /// Drive a single begin-frame to Chromium's compositor. Requires the
    /// browser to have been created via <see cref="Cef.CreateOffscreenBrowserEx"/>
    /// with <see cref="Cef.OffscreenFlags.ExternalBeginFrame"/> set —
    /// otherwise the standard <see cref="WindowlessFrameRate"/> timer
    /// drives frames and this is a no-op.
    /// </summary>
    public void SendExternalBeginFrame()
    {
        if (!_closed) Excef.excef_send_external_begin_frame(Id);
    }

    /// <summary>
    /// Start a platform display-link clock that drives external begin frames.
    /// Returns <see langword="false"/> when the platform has no qualified
    /// display-link implementation or the clock could not be created.
    /// </summary>
    public unsafe bool StartExternalBeginFrameClock(nint windowHandle) =>
        !_closed
        && Excef.excef_start_external_begin_frame_clock(
            Id,
            (void*)windowHandle) != 0;

    /// <summary>
    /// Stop the platform display-link clock started for this browser.
    /// </summary>
    public void StopExternalBeginFrameClock()
    {
        if (!_closed) Excef.excef_stop_external_begin_frame_clock(Id);
    }

    /// <summary>
    /// Exit page-driven HTML5 fullscreen. Equivalent to JS
    /// <c>document.exitFullscreen()</c>. Pass <paramref name="willCauseResize"/> = true
    /// (the default) so Chromium knows the host will resize the view in response —
    /// suppresses redundant layout flicker.
    /// </summary>
    public void ExitFullscreen(bool willCauseResize = true)
    {
        if (!_closed) Excef.excef_exit_fullscreen(Id, willCauseResize ? 1 : 0);
    }

    /// <summary>
    /// Enable / disable Chromium's auto-resize. When enabled, Chromium
    /// takes over the browser's view sizing — the page lays out at its
    /// natural content size (clamped to the min/max bounds), <see cref="AutoResize"/>
    /// fires with that size, and the host is expected to resize its
    /// control to match. Used for embed/iframe scenarios where the host
    /// wants the browser to be exactly as tall as the content.
    ///
    /// <para><b>Important:</b> if you enable auto-resize but don't resize
    /// the host control in response to the <see cref="AutoResize"/> event,
    /// the page won't render correctly and input may stop working — the
    /// host's view rect and Chromium's chosen rect diverge. For standard
    /// "fixed-size embed" scenarios leave auto-resize disabled.</para>
    ///
    /// Pass <c>enabled=false</c> to disable; min/max are ignored.
    /// </summary>
    public void SetAutoResizeEnabled(bool enabled,
                                      int minWidth = 1, int minHeight = 1,
                                      int maxWidth = 4096, int maxHeight = 4096)
    {
        if (!_closed) Excef.excef_set_auto_resize_enabled(Id, enabled ? 1 : 0,
            minWidth, minHeight, maxWidth, maxHeight);
    }

    // ---- Zoom -----------------------------------------------------------

    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            _zoomLevel = value;
            if (!_closed) Excef.excef_set_zoom_level(Id, value);
        }
    }

    /// <summary>
    /// Read the browser's current zoom level directly from CEF (rather
    /// than the cached host-side value). 0.0 == 100%; each ±1 ≈ 1.2×.
    /// </summary>
    public double GetZoomLevel()
        => _closed ? _zoomLevel : Excef.excef_get_zoom_level(Id);

    /// <summary>
    /// Tell CEF the hosting window has started a move/resize gesture. CEF
    /// uses this as a hint to defer IME composition windows and to lower
    /// some perf timers until the gesture ends.
    /// </summary>
    public void NotifyMoveOrResizeStarted()
    {
        if (!_closed) Excef.excef_notify_move_or_resize_started(Id);
    }

    /// <summary>
    /// Tell CEF the screen info changed (DPI / display / monitor switch).
    /// CEF will re-query <see cref="SetDeviceScaleFactor"/> and viewport size.
    /// </summary>
    public void NotifyScreenInfoChanged()
    {
        if (!_closed) Excef.excef_notify_screen_info_changed(Id);
    }

    // ---- Spellcheck -----------------------------------------------------

    /// <summary>
    /// Replace the misspelled word at the current selection with the
    /// provided replacement. Pair with <see cref="ContextMenuEventArgs"/>
    /// — read MisspelledWord and Suggestions from the params dict.
    /// </summary>
    public void ReplaceMisspelling(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(word);
            try { Excef.excef_replace_misspelling(Id, p); }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    /// <summary>
    /// Add a word to the browser's custom spellcheck dictionary so it
    /// stops being flagged as misspelled in this profile.
    /// </summary>
    public void AddWordToDictionary(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(word);
            try { Excef.excef_add_word_to_dictionary(Id, p); }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    // ---- Audio ----------------------------------------------------------

    /// <summary>
    /// Enable / disable PCM audio capture for this browser. Off by default.
    /// When enabled, AudioStreamStarted fires once a media element begins
    /// playing, followed by AudioPacket per chunk, ending with AudioStreamStopped.
    /// </summary>
    public void EnableAudioCapture(bool enable)
    {
        if (!_closed) Excef.excef_enable_audio_capture(Id, enable ? 1 : 0);
    }

    /// <summary>Mute / unmute this browser's audio output (independent of capture).</summary>
    public bool AudioMuted
    {
        get => !_closed && Excef.excef_is_audio_muted(Id) != 0;
        set { if (!_closed) Excef.excef_set_audio_muted(Id, value ? 1 : 0); }
    }

    /// <summary>
    /// Whether the given zoom command is currently available. Useful for
    /// graying out zoom buttons at min/max zoom (zoom in / zoom out
    /// return false once the limit is reached).
    /// </summary>
    public bool CanZoom(Cef.CefZoomCommand command)
        => !_closed && Excef.excef_can_zoom(Id, (int)command) != 0;

    public bool CanZoomIn    => CanZoom(Cef.CefZoomCommand.In);
    public bool CanZoomOut   => CanZoom(Cef.CefZoomCommand.Out);
    public bool CanZoomReset => CanZoom(Cef.CefZoomCommand.Reset);

    // ---- Clipboard / editing -------------------------------------------

    public void Copy()      { if (!_closed) Excef.excef_copy(Id); }
    public void Paste()     { if (!_closed) Excef.excef_paste(Id); }
    public void Cut()       { if (!_closed) Excef.excef_cut(Id); }
    public void SelectAll() { if (!_closed) Excef.excef_select_all(Id); }
    public void Undo()      { if (!_closed) Excef.excef_undo(Id); }
    public void Redo()      { if (!_closed) Excef.excef_redo(Id); }

    // ---- Input forwarding ----------------------------------------------

    public void SendMouseMove(int x, int y, Cef.CefModifiers modifiers, bool mouseLeave)
    {
        if (!_closed) Excef.excef_send_mouse_move(Id, x, y, (int)modifiers, mouseLeave ? 1 : 0);
    }

    public void SendMouseClick(int x, int y, Cef.CefMouseButton button, bool mouseUp, int clickCount, Cef.CefModifiers modifiers)
    {
        if (!_closed) Excef.excef_send_mouse_click(Id, x, y, (int)button, mouseUp ? 1 : 0, clickCount, (int)modifiers);
    }

    public void SendMouseWheel(int x, int y, int deltaX, int deltaY, Cef.CefModifiers modifiers)
    {
        if (!_closed) Excef.excef_send_mouse_wheel(Id, x, y, deltaX, deltaY, (int)modifiers);
    }

    public void SendKeyEvent(
        Cef.CefKeyEventType type,
        int windowsKeyCode,
        int nativeKeyCode,
        Cef.CefModifiers modifiers,
        char character,
        char unmodifiedCharacter,
        bool isSystemKey)
    {
        if (_closed) return;
        Excef.excef_send_key_event(
            Id, (int)type,
            windowsKeyCode, nativeKeyCode,
            (int)modifiers,
            character, unmodifiedCharacter,
            isSystemKey ? 1 : 0);
    }

    public void SetFocus(bool focus)
    {
        if (!_closed) Excef.excef_set_browser_focus(Id, focus ? 1 : 0);
    }

    // ---- Drag and drop --------------------------------------------------
    //
    // For OS-level drags arriving from outside the WebView (Finder file
    // drop, browser link drag, etc.) the host's UI framework reports
    // drag events; forward each to the matching DragTarget* method.

    /// <summary>
    /// Tell CEF an external drag has entered the view. Coordinates are in
    /// DIPs relative to the view's top-left. Any of <paramref name="text"/>,
    /// <paramref name="html"/>, <paramref name="url"/>, <paramref name="filePaths"/>
    /// may be null or empty.
    /// </summary>
    public void DragTargetEnter(int x, int y,
                                Cef.CefModifiers modifiers,
                                Cef.DragOperations allowedOps,
                                string? text = null,
                                string? html = null,
                                string? url = null,
                                IReadOnlyList<string>? filePaths = null)
    {
        if (_closed) return;
        unsafe
        {
            sbyte* pText = text is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(text);
            sbyte* pHtml = html is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(html);
            sbyte* pUrl  = url  is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            int fileCount = filePaths?.Count ?? 0;
            IntPtr* fileStorage = stackalloc IntPtr[fileCount > 0 ? fileCount : 1];
            for (int i = 0; i < fileCount; ++i)
                fileStorage[i] = Marshal.StringToCoTaskMemUTF8(filePaths![i]);
            try
            {
                Excef.excef_drag_target_drag_enter(
                    Id, x, y, (int)modifiers, (int)allowedOps,
                    pText, pHtml, pUrl,
                    fileCount > 0 ? (sbyte**)fileStorage : null,
                    fileCount);
            }
            finally
            {
                if (pText != null) Marshal.FreeCoTaskMem((IntPtr)pText);
                if (pHtml != null) Marshal.FreeCoTaskMem((IntPtr)pHtml);
                if (pUrl  != null) Marshal.FreeCoTaskMem((IntPtr)pUrl);
                for (int i = 0; i < fileCount; ++i)
                    Marshal.FreeCoTaskMem(fileStorage[i]);
            }
        }
    }

    public void DragTargetOver(int x, int y, Cef.CefModifiers modifiers, Cef.DragOperations allowedOps)
    {
        if (!_closed) Excef.excef_drag_target_drag_over(Id, x, y, (int)modifiers, (int)allowedOps);
    }

    public void DragTargetDrop(int x, int y, Cef.CefModifiers modifiers)
    {
        if (!_closed) Excef.excef_drag_target_drop(Id, x, y, (int)modifiers);
    }

    public void DragTargetLeave()
    {
        if (!_closed) Excef.excef_drag_target_drag_leave(Id);
    }

    /// <summary>
    /// Notify CEF the page-initiated drag has ended at the given coordinates
    /// (in view DIPs) with the given completed operation. Must be paired with
    /// <see cref="DragSourceSystemDragEnded"/>. Only call after handling a
    /// <see cref="DragStarted"/> event with Handled=true.
    /// </summary>
    public void DragSourceEndedAt(int x, int y, Cef.DragOperations op)
    {
        if (!_closed) Excef.excef_drag_source_ended_at(Id, x, y, (int)op);
    }

    public void DragSourceSystemDragEnded()
    {
        if (!_closed) Excef.excef_drag_source_system_drag_ended(Id);
    }

    // ---- DevTools -------------------------------------------------------

    /// <summary>Open DevTools in CEF's default location for the host
    /// platform — a separate native OS window on macOS / Windows /
    /// Linux. Uses the DevTools front-end assets baked into the CEF
    /// binary; works fully offline. For embedded-alongside-the-browser
    /// DevTools, hosts should spawn a second NativeWebView pointed
    /// at the loopback inspector URL
    /// (<c>http://127.0.0.1:&lt;remote-debug-port&gt;/devtools/inspector.html?ws=…</c>)
    /// instead — CEF serves that URL from its bundled resources too.</summary>
    public void ShowDevTools()  { if (!_closed) Excef.excef_show_dev_tools(Id); }
    public void CloseDevTools() { if (!_closed) Excef.excef_close_dev_tools(Id); }

    /// <summary>Raised for every CDP message the browser emits — replies to host requests AND server-pushed events.</summary>
    public event EventHandler<DevToolsMessageEventArgs>? DevToolsMessage;

    internal void RaiseDevToolsMessage(bool isEvent, int messageId, string json)
    {
        // Resolve any pending ExecuteDevToolsMethodAsync awaiter for this id.
        if (!isEvent && messageId > 0 &&
            _devtoolsPending.TryRemove(messageId, out var tcs))
        {
            tcs.TrySetResult(json);
        }
        DevToolsMessage?.Invoke(this, new DevToolsMessageEventArgs(isEvent, messageId, json));
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<string>> _devtoolsPending = new();
    private int _nextDevToolsId;

    /// <summary>
    /// Capture a PNG screenshot of the page via CDP <c>Page.captureScreenshot</c>.
    /// Works in any rendering mode (OSR, embedded, windowed) and doesn't
    /// require <see cref="EnableFrameCapture"/>. Returns PNG bytes ready
    /// to write to disk or send to a vision API.
    /// </summary>
    /// <param name="format">"png" | "jpeg" | "webp" (default png)</param>
    /// <param name="quality">0-100 for jpeg/webp (ignored for png)</param>
    /// <param name="captureBeyondViewport">If true, captures the full document height, not just the visible viewport.</param>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread.
    /// Marshal back to the UI thread before any follow-up
    /// <see cref="CefBrowser"/> call.
    /// </remarks>
    public async Task<byte[]> CapturePageAsync(string format = "png", int? quality = null, bool captureBeyondViewport = false)
    {
        var sb = new System.Text.StringBuilder("{\"format\":").Append(System.Text.Json.JsonSerializer.Serialize(format));
        if (quality is int q) sb.Append(",\"quality\":").Append(q);
        if (captureBeyondViewport) sb.Append(",\"captureBeyondViewport\":true");
        sb.Append("}");
        var reply = await ExecuteDevToolsMethodAsync("Page.captureScreenshot", sb.ToString());
        // Reply is `{"id":N, "result":{"data":"<base64>"}}`. Crude
        // extraction — avoids a JSON dependency. CDP guarantees the
        // shape, so this is reliable enough for v0.
        int idx = reply.IndexOf("\"data\":\"", StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("CapturePage: no data in CDP reply: " + reply);
        idx += "\"data\":\"".Length;
        int end = reply.IndexOf('"', idx);
        if (end < 0) throw new InvalidOperationException("CapturePage: unterminated data in CDP reply");
        return Convert.FromBase64String(reply.Substring(idx, end - idx));
    }

    /// <summary>Send a raw CDP JSON message to the browser. Fire-and-forget; use <see cref="ExecuteDevToolsMethodAsync"/> for round-trip.</summary>
    public bool SendDevToolsMessageRaw(string messageJson)
    {
        ArgumentNullException.ThrowIfNull(messageJson);
        if (_closed) return false;
        unsafe
        {
            // The native side wants the UTF-8 BYTE length — messageJson.Length
            // is the UTF-16 char count, which truncates any non-ASCII payload.
            int byteLen = System.Text.Encoding.UTF8.GetByteCount(messageJson);
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(messageJson);
            try { return Excef.excef_send_devtools_message(Id, p, byteLen) != 0; }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    /// <summary>
    /// Execute a CDP method (e.g. <c>Network.setUserAgentOverride</c>,
    /// <c>Emulation.setDeviceMetricsOverride</c>, <c>Page.captureScreenshot</c>)
    /// and await the JSON reply. <paramref name="paramsJson"/> is a
    /// JSON-stringified object; pass <c>null</c> for no params.
    /// </summary>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread.
    /// Marshal back to the UI thread before any follow-up
    /// <see cref="CefBrowser"/> call.
    /// </remarks>
    public Task<string> ExecuteDevToolsMethodAsync(string method, string? paramsJson = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (_closed) return Task.FromException<string>(new InvalidOperationException("browser closed"));

        int messageId = Interlocked.Increment(ref _nextDevToolsId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _devtoolsPending[messageId] = tcs;

        unsafe
        {
            sbyte* m = (sbyte*)Marshal.StringToCoTaskMemUTF8(method);
            sbyte* p = paramsJson is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(paramsJson);
            try
            {
                int rc = Excef.excef_execute_devtools_method(Id, messageId, m, p);
                if (rc == 0)
                {
                    _devtoolsPending.TryRemove(messageId, out _);
                    tcs.TrySetException(new InvalidOperationException("ExecuteDevToolsMethod failed (browser unknown)"));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)m);
                if (p != null) Marshal.FreeCoTaskMem((IntPtr)p);
            }
        }
        return tcs.Task;
    }

    // ---- JavaScript -----------------------------------------------------

    public void ExecuteJavaScript(string code, string? scriptUrl = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (_closed) return;
        unsafe
        {
            sbyte* codePtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(code);
            sbyte* urlPtr = scriptUrl is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(scriptUrl);
            try { Excef.excef_execute_javascript(Id, codePtr, urlPtr); }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)codePtr);
                if (urlPtr is not null) Marshal.FreeCoTaskMem((IntPtr)urlPtr);
            }
        }
    }

    /// <summary>
    /// Evaluate JS in the main frame and return the result as a JSON string.
    /// </summary>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread.
    /// Marshal back to the UI thread before any follow-up
    /// <see cref="CefBrowser"/> call.
    /// </remarks>
    // Eval request IDs owned by this browser, so RaiseClosed can fail any
    // in-flight TaskCompletionSources instead of leaving them hanging.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _evalRequestIds = new();

    public Task<string> EvaluateJavaScriptAsync(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (_closed) return Task.FromException<string>(new InvalidOperationException("browser closed"));

        int reqId = Interlocked.Increment(ref Cef.s_nextEvalRequestId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Cef.s_evalRequests[reqId] = tcs;
        _evalRequestIds[reqId] = 0;

        unsafe
        {
            sbyte* codePtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(code);
            try
            {
                int scheduled = Excef.excef_eval_javascript(Id, reqId, codePtr);
                if (scheduled == 0)
                {
                    Cef.s_evalRequests.TryRemove(reqId, out _);
                    _evalRequestIds.TryRemove(reqId, out _);
                    tcs.TrySetException(new InvalidOperationException("eval not scheduled (browser unknown or closed)"));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)codePtr);
            }
        }
        return tcs.Task;
    }

    internal void RemoveEvalRequest(int reqId) => _evalRequestIds.TryRemove(reqId, out _);

    // ---- PDF ------------------------------------------------------------

    // The CEF callback only carries (browserId, success), so concurrent
    // prints on the same browser are demuxed via a FIFO queue.
    private readonly List<Action<int, int>> _pdfQueue = new();
    internal List<Action<int, int>> PdfQueue => _pdfQueue;

    /// <summary>
    /// Render the current page as PDF at <paramref name="path"/>. Default
    /// settings; use the <c>Exclr8Cef.Print</c> package for full control.
    /// </summary>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread.
    /// Marshal back to the UI thread before any follow-up
    /// <see cref="CefBrowser"/> call.
    /// </remarks>
    public Task<bool> PrintToPdfAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_closed) return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int, int> cb = (_, ok) => tcs.TrySetResult(ok != 0);

        unsafe
        {
            sbyte* pathPtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                lock (_pdfQueue)
                {
                    _pdfQueue.Add(cb);
                    int scheduled = Excef.excef_print_to_pdf(Id, pathPtr, &Cef.PdfDoneTrampoline);
                    if (scheduled == 0)
                    {
                        _pdfQueue.RemoveAt(_pdfQueue.Count - 1);
                        tcs.TrySetResult(false);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)pathPtr);
            }
        }
        return tcs.Task;
    }

    // ---- IME ------------------------------------------------------------

    public void ImeSetComposition(string text,
                                   int replacementRangeStart = 0,
                                   int replacementRangeLength = 0,
                                   int selectionStart = 0,
                                   int selectionLength = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(text);
            try
            {
                Excef.excef_ime_set_composition(Id, p,
                    replacementRangeStart, replacementRangeLength,
                    selectionStart, selectionLength);
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void ImeCommitText(string text,
                               int replacementRangeStart = 0,
                               int replacementRangeLength = 0,
                               int relativeCursorPos = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(text);
            try
            {
                Excef.excef_ime_commit_text(Id, p,
                    replacementRangeStart, replacementRangeLength,
                    relativeCursorPos);
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void ImeFinishComposing(bool keepSelection = false)
    {
        if (!_closed) Excef.excef_ime_finish_composing(Id, keepSelection ? 1 : 0);
    }

    public void ImeCancel() { if (!_closed) Excef.excef_ime_cancel(Id); }

    // ---- Find in page ---------------------------------------------------

    /// <summary>
    /// Begin (or continue) an in-page search. Calling with
    /// <paramref name="findNext"/> = true while a search is active jumps
    /// to the next match without restarting; <paramref name="findNext"/>
    /// = false starts a new search. Passing an empty <paramref name="searchText"/>
    /// stops the search. Results stream back on <see cref="FindResult"/>.
    /// </summary>
    public void Find(string searchText, bool forward = true, bool matchCase = false, bool findNext = false)
    {
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(searchText ?? "");
            try { Excef.excef_find(Id, p, forward ? 1 : 0, matchCase ? 1 : 0, findNext ? 1 : 0); }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void StopFinding(bool clearSelection = true)
    {
        if (!_closed) Excef.excef_stop_finding(Id, clearSelection ? 1 : 0);
    }

    // ---- Close ----------------------------------------------------------

    /// <summary>Request the browser close. Idempotent.</summary>
    public void Close(bool force = false)
    {
        if (_closed) return;

        var requestedState = force ? 2 : 1;
        while (true)
        {
            var currentState = Volatile.Read(ref _closeRequestState);
            if (currentState >= requestedState) return;
            if (Interlocked.CompareExchange(
                    ref _closeRequestState,
                    requestedState,
                    currentState) == currentState)
            {
                break;
            }
        }

        Excef.excef_close_browser(Id, force ? 1 : 0);
        // Actual transition to _closed happens in RaiseClosed (from the
        // OnBeforeClose native callback).
    }

    /// <summary>Equivalent to <c>Close(force: true)</c>.</summary>
    public void Dispose() => Close(force: true);

    // ---- Internal event raisers (called from Cef trampolines) ----------

    internal void RaiseAddressChanged(string url)
    {
        _url = url;
        AddressChanged?.Invoke(this, url);
    }

    internal void RaiseTitleChanged(string title)
    {
        _title = title;
        TitleChanged?.Invoke(this, title);
    }

    internal void RaiseLoadingStateChanged(bool isLoading, bool canGoBack, bool canGoForward)
    {
        _isLoading = isLoading;
        _canGoBack = canGoBack;
        _canGoForward = canGoForward;
        LoadingStateChanged?.Invoke(this, new LoadingState(isLoading, canGoBack, canGoForward));
    }

    internal void RaiseCursorChanged(Cef.CefCursorType type)
        => CursorChanged?.Invoke(this, type);

    internal void RaiseConsoleMessage(Cef.CefLogSeverity level, string message, string source, int line)
        => ConsoleMessage?.Invoke(this, new ConsoleMessageEventArgs(level, message, source, line));

    internal void RaiseLoadStart(bool isMainFrame, string url)
        => LoadStart?.Invoke(this, new LoadStartEventArgs(isMainFrame, url));

    internal void RaiseLoadEnd(bool isMainFrame, string url, int httpStatusCode)
        => LoadEnd?.Invoke(this, new LoadEndEventArgs(isMainFrame, url, httpStatusCode));

    internal void RaiseLoadError(bool isMainFrame, Cef.CefErrorCode errorCode, string errorText, string failedUrl)
        => LoadError?.Invoke(this, new LoadErrorEventArgs(isMainFrame, errorCode, errorText, failedUrl));

    internal void RaiseLoadingProgress(double progress)
        => LoadingProgress?.Invoke(this, progress);

    internal void RaiseInitialized()
    {
        _initialized = true;
        var closeState = Volatile.Read(ref _closeRequestState);
        if (closeState != 0)
        {
            Excef.excef_close_browser(Id, closeState == 2 ? 1 : 0);
            return;
        }
        _initializedHandlers?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseStatusMessage(string value) => StatusMessage?.Invoke(this, value);
    internal void RaiseTooltipChanged(string text) => TooltipChanged?.Invoke(this, text);
    internal void RaiseFaviconChanged(string url) => FaviconChanged?.Invoke(this, url);
    internal void RaiseFullscreenChanged(bool fullscreen) => FullscreenModeChanged?.Invoke(this, fullscreen);

    internal void RaiseScrollOffset(double x, double y)
        => ScrollOffsetChanged?.Invoke(this, new ScrollOffsetEventArgs(x, y));

    internal void RaiseAutoResize(int w, int h)
        => AutoResize?.Invoke(this, new AutoResizeEventArgs(w, h));

    internal bool HasJsDialogSubscriber => JsDialog is not null;
    internal bool HasFileDialogSubscriber => FileDialog is not null;
    internal bool HasContextMenuSubscriber => ContextMenu is not null;

    internal void RaiseJsDialog(ulong token, Cef.JsDialogType type, string message, string defaultPrompt)
        => JsDialog?.Invoke(this, new JsDialogEventArgs(token, type, message, defaultPrompt));

    internal void RaiseFileDialog(ulong token, Cef.FileDialogMode mode, string title, string defaultPath, string[] filters)
        => FileDialog?.Invoke(this, new FileDialogEventArgs(token, mode, title, defaultPath, filters));

    internal void RaiseContextMenu(ulong token, int x, int y, ContextMenuItem[] items)
        => ContextMenu?.Invoke(this, new ContextMenuEventArgs(token, x, y, items));

    internal bool HasDownloadStartingSubscriber => DownloadStarting is not null;

    internal void RaiseDownloadStarting(ulong token, int downloadId, string url, string suggestedName, string mimeType, long totalBytes)
        => DownloadStarting?.Invoke(this, new DownloadStartingEventArgs(token, downloadId, url, suggestedName, mimeType, totalBytes));

    internal void RaiseDownloadProgress(ulong token, int downloadId, int percent, long received, long total, long speed, Cef.DownloadState state, string fullPath)
        => DownloadProgress?.Invoke(this, new DownloadProgressEventArgs(token, downloadId, percent, received, total, speed, state, fullPath));

    internal bool HasAuthRequestSubscriber => AuthRequest is not null;

    internal void RaiseAuthRequest(ulong token, bool isProxy, string host, int port, string realm, string scheme, string originUrl)
        => AuthRequest?.Invoke(this, new AuthRequestEventArgs(token, isProxy, host, port, realm, scheme, originUrl));

    internal void RaiseFindResult(int identifier, int count, int activeMatchOrdinal, bool finalUpdate)
        => FindResult?.Invoke(this, new FindResultEventArgs(identifier, count, activeMatchOrdinal, finalUpdate));

    internal void RaiseRenderProcessGone(Cef.TerminationStatus status, int errorCode, string errorString)
        => RenderProcessGone?.Invoke(this, new RenderProcessGoneEventArgs(status, errorCode, errorString));

    internal bool HasBeforeBrowseSubscriber => BeforeBrowse is not null;
    internal bool RaiseBeforeBrowse(BeforeBrowseEventArgs args)
    {
        try
        {
            BeforeBrowse?.Invoke(this, args);
            return args.Cancel;
        }
        catch
        {
            return true;
        }
    }

    internal bool HasResourceRequestSubscriber
        => ResourceRequest is not null || ResourceRequestObserved is not null;
    internal bool HasResourceRequestGate => ResourceRequest is not null;

    internal void RaiseResourceRequest(ulong token, string url, string method, Cef.ResourceType type, string headers)
        => ResourceRequest?.Invoke(this, new ResourceRequestEventArgs(token, url, method, type, headers));

    internal void RaiseResourceRequestObserved(string url, string method, Cef.ResourceType type, string headers)
        => ResourceRequestObserved?.Invoke(this, new ResourceRequestObservedEventArgs(url, method, type, headers));

    internal void RaisePopupShow(bool show) => PopupShow?.Invoke(this, show);
    internal void RaisePopupSize(int x, int y, int w, int h) => PopupSize?.Invoke(this, new PopupRect(x, y, w, h));
    internal void RaisePopupPainted(IntPtr buffer, int width, int height)
        => PopupPainted?.Invoke(this, new PaintEventArgs(buffer, width, height));

    internal void RaiseJsInvoke(JsInvokeEventArgs args)
    {
        if (JsInvoke is null)
        {
            // No subscriber — auto-resolve with null so the renderer's
            // Promise doesn't hang.
            args.Reply(null);
            return;
        }
        try { JsInvoke.Invoke(this, args); }
        catch (Exception ex) { args.ReplyError(ex.Message); }
    }

    internal void RaiseAccessibilityTreeChange(string json)
        => AccessibilityTreeChange?.Invoke(this, json);
    internal void RaiseAccessibilityLocationChange(string json)
        => AccessibilityLocationChange?.Invoke(this, json);

    internal bool HasDragStartedSubscriber => DragStarted is not null;

    internal bool RaiseDragStarted(DragStartedEventArgs args)
    {
        DragStarted?.Invoke(this, args);
        return args.Handled;
    }

    internal void RaiseDragImage(IntPtr buffer, int width, int height, int hotspotX, int hotspotY)
        => DragImage?.Invoke(this, new DragImageEventArgs(buffer, width, height, hotspotX, hotspotY));

    internal bool HasPermissionRequestSubscriber => PermissionRequest is not null;
    internal void RaisePermissionRequest(PermissionRequestEventArgs args)
        => PermissionRequest?.Invoke(this, args);

    internal bool HasMediaAccessRequestSubscriber => MediaAccessRequest is not null;
    internal void RaiseMediaAccessRequest(MediaAccessRequestEventArgs args)
        => MediaAccessRequest?.Invoke(this, args);

    internal void RaiseBeforePopup(BeforePopupEventArgs args)
        => BeforePopup?.Invoke(this, args);

    internal void RaiseHostPopup(HostPopupEventArgs args)
        => HostPopup?.Invoke(this, args);

    internal bool HasCertErrorSubscriber => CertError is not null;
    internal void RaiseCertError(CertErrorEventArgs args)
        => CertError?.Invoke(this, args);

    internal void RaiseTakeFocus(bool next) => TakeFocus?.Invoke(this, next);
    internal void RaiseSetFocus(SetFocusEventArgs args) => FocusRequested?.Invoke(this, args);
    internal void RaiseGotFocus() => GotFocus?.Invoke(this, EventArgs.Empty);
    internal void RaisePreKey(PreKeyEventArgs args) => PreKeyEvent?.Invoke(this, args);
    internal void RaiseKeyEvent(PreKeyEventArgs args) => KeyEvent?.Invoke(this, args);

    internal void RaiseFrameLifecycle(FrameLifecycleEventArgs args)
        => FrameLifecycle?.Invoke(this, args);
    internal void RaiseMainFrameChanged(MainFrameChangedEventArgs args)
        => MainFrameChanged?.Invoke(this, args);
    internal void RaiseAudioStarted(AudioStreamStartedEventArgs args)
        => AudioStreamStarted?.Invoke(this, args);
    internal void RaiseAudioPacket(AudioPacketEventArgs args)
        => AudioPacket?.Invoke(this, args);
    internal void RaiseAudioStopped()
        => AudioStreamStopped?.Invoke(this, EventArgs.Empty);
    internal void RaiseAudioError(string message)
        => AudioStreamError?.Invoke(this, message);

    internal void RaiseTouchHandleSizeRequest(TouchHandleSizeRequest args)
        => TouchHandleSizeRequested?.Invoke(this, args);
    internal void RaiseTouchHandleStateChanged(TouchHandleStateEventArgs args)
        => TouchHandleStateChanged?.Invoke(this, args);
    internal void RaiseImeCompositionRangeChanged(ImeCompositionRangeEventArgs args)
        => ImeCompositionRangeChanged?.Invoke(this, args);
    internal void RaiseVirtualKeyboardRequested(Cef.CefTextInputMode mode)
        => VirtualKeyboardRequested?.Invoke(this, mode);
    internal void RaiseAcceleratedPaint(AcceleratedPaintEventArgs args)
        => AcceleratedPaint?.Invoke(this, args);

    /// <summary>
    /// Enable / disable the accessibility-tree event stream. Off by
    /// default. With it enabled, <see cref="AccessibilityTreeChange"/>
    /// + <see cref="AccessibilityLocationChange"/> fire as the page's
    /// a11y structure changes.
    /// </summary>
    public void SetAccessibilityEnabled(bool enabled)
    {
        if (!_closed) Excef.excef_set_accessibility_enabled(Id, enabled ? 1 : 0);
    }

    // ---- Vision / automation surface -----------------------------------
    //
    // For in-process AI / automation hosts that want direct access to the
    // rendered pixels without going through CDP screenshots.
    //
    // Opt-in: EnableFrameCapture(true) allocates a managed copy of every
    // paint buffer (~3MB for 1280x720). When enabled, TryCaptureLastFrame
    // returns the most recent frame, and FrameStream yields each new
    // paint as it arrives (bounded drop-oldest channel, no backpressure).

    private readonly object _frameLock = new();
    private byte[]? _lastFrame;
    private int _lastFrameWidth;
    private int _lastFrameHeight;
    private bool _frameCaptureEnabled;
    private System.Threading.Channels.Channel<PaintFrame>? _frameChannel;

    /// <summary>
    /// Toggle the latest-frame cache and frame-stream channel. Off by
    /// default — paint buffers are ~3MB each. Idempotent.
    /// </summary>
    public void EnableFrameCapture(bool enabled)
    {
        lock (_frameLock)
        {
            if (_frameCaptureEnabled == enabled) return;
            _frameCaptureEnabled = enabled;
            if (!enabled)
            {
                _lastFrame = null;
                _lastFrameWidth = _lastFrameHeight = 0;
                _frameChannel?.Writer.TryComplete();
                _frameChannel = null;
            }
        }
    }

    /// <summary>
    /// Copy the most-recent paint buffer into <paramref name="bgra"/>.
    /// Returns false if no frame has been seen yet or capture is disabled.
    /// Buffer is BGRA8888 in physical pixels (multiply DIPs by device scale).
    /// </summary>
    public bool TryCaptureLastFrame(out byte[]? bgra, out int width, out int height)
    {
        lock (_frameLock)
        {
            if (_lastFrame is null || _lastFrameWidth == 0)
            {
                bgra = null; width = height = 0;
                return false;
            }
            bgra = (byte[])_lastFrame.Clone();
            width = _lastFrameWidth;
            height = _lastFrameHeight;
            return true;
        }
    }

    /// <summary>
    /// Reader for a bounded, drop-oldest channel that yields each new paint
    /// frame. Use for vision/automation loops that want to consume frames
    /// at their own rate without blocking the renderer. Requires
    /// <see cref="EnableFrameCapture"/>(true) first.
    /// </summary>
    public System.Threading.Channels.ChannelReader<PaintFrame> FrameStream
    {
        get
        {
            lock (_frameLock)
            {
                if (!_frameCaptureEnabled)
                    throw new InvalidOperationException("Call EnableFrameCapture(true) first.");
                _frameChannel ??= System.Threading.Channels.Channel.CreateBounded<PaintFrame>(
                    new System.Threading.Channels.BoundedChannelOptions(1)
                    {
                        FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                        SingleReader = false,
                        SingleWriter = true,
                    });
                return _frameChannel.Reader;
            }
        }
    }

    /// <summary>
    /// DOM-level hit-test via the JS bridge: returns the element under the
    /// given DIP coordinates (CSS pixels) along with its tag, id, classes,
    /// text snippet, and bounding rect. Null if nothing's there.
    /// </summary>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread.
    /// Marshal back to the UI thread before any follow-up
    /// <see cref="CefBrowser"/> call.
    /// </remarks>
    public async Task<HitTestResult?> HitTestAtAsync(int x, int y)
    {
        // Wrap the result in JSON so EvaluateJavaScriptAsync's structured
        // serializer carries the object back as a string we can deserialize.
        string js = $@"(function() {{
  var el = document.elementFromPoint({x}, {y});
  if (!el) return null;
  var r = el.getBoundingClientRect();
  return {{
    tag: el.tagName.toLowerCase(),
    id: el.id || null,
    className: typeof el.className === 'string' ? el.className : null,
    text: (el.textContent || '').slice(0, 200),
    role: el.getAttribute('role'),
    href: el.tagName === 'A' ? el.href : null,
    x: r.x, y: r.y, width: r.width, height: r.height
  }};
}})()";
        var json = await EvaluateJavaScriptAsync(js);
        if (string.IsNullOrEmpty(json) || json == "null") return null;
        return System.Text.Json.JsonSerializer.Deserialize<HitTestResult>(json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
    }

    internal void RaisePainted(IntPtr buffer, int width, int height)
    {
        if (_frameCaptureEnabled)
        {
            // Two independent copies — _lastFrame and PaintFrame each own
            // their bytes, so a consumer mutating one can't corrupt the
            // cache. Copy outside the lock so the paint thread doesn't
            // stall on a slow ~3MB memcpy.
            int byteCount = width * height * 4;
            byte[] cacheCopy = new byte[byteCount];
            Marshal.Copy(buffer, cacheCopy, 0, byteCount);
            byte[] frameCopy = (byte[])cacheCopy.Clone();
            var frame = new PaintFrame(frameCopy, width, height, DateTime.UtcNow);
            lock (_frameLock)
            {
                _lastFrame = cacheCopy;
                _lastFrameWidth = width;
                _lastFrameHeight = height;
                _frameChannel?.Writer.TryWrite(frame);
            }
        }
        Painted?.Invoke(this, new PaintEventArgs(buffer, width, height));
    }

    internal void RaiseClosed()
    {
        _closed = true;
        // Fail any pending PDF callbacks so callers' Tasks complete instead of hanging.
        lock (_pdfQueue)
        {
            foreach (var cb in _pdfQueue) cb(Id, 0);
            _pdfQueue.Clear();
        }
        // Fail any pending JS evals — without this, HitTestAtAsync and any
        // other awaiter hangs forever when the browser closes mid-eval.
        var browserClosedEx = new InvalidOperationException("browser closed");
        foreach (var reqId in _evalRequestIds.Keys)
        {
            if (Cef.s_evalRequests.TryRemove(reqId, out var tcs))
                tcs.TrySetException(browserClosedEx);
        }
        _evalRequestIds.Clear();
        // Same for string-visitor (GetSource/GetText) and nav-entry awaiters.
        foreach (var reqId in _stringVisitorRequestIds.Keys)
        {
            if (Cef.s_stringVisitorRequests.TryRemove(reqId, out var tcs))
                tcs.TrySetException(browserClosedEx);
        }
        _stringVisitorRequestIds.Clear();
        foreach (var reqId in _navEntryRequestIds.Keys)
        {
            if (Cef.s_navEntryRequests.TryRemove(reqId, out var tcs))
                tcs.TrySetException(browserClosedEx);
            Cef.s_navEntryAccum.TryRemove(reqId, out _);
        }
        _navEntryRequestIds.Clear();
        // Fail any pending DevTools method awaiters so callers' Tasks
        // complete instead of hanging.
        foreach (var kv in _devtoolsPending)
        {
            kv.Value.TrySetException(browserClosedEx);
        }
        _devtoolsPending.Clear();
        // Unblock any FrameStream consumers — without TryComplete, an
        // awaiter on ReadAsync hangs after browser close.
        lock (_frameLock)
        {
            _frameChannel?.Writer.TryComplete();
            _frameChannel = null;
            _lastFrame = null;
            _lastFrameWidth = _lastFrameHeight = 0;
            _frameCaptureEnabled = false;
        }
        Closed?.Invoke(this, EventArgs.Empty);
        Id = 0;
    }
}
