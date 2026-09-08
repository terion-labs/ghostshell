using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Exclr8Cef.Native;

namespace Exclr8Cef;

/// <summary>
/// Static facade for the Exclr8CEF process-wide lifecycle: init / shutdown,
/// message-loop pumping, top-level browser creation, the off-screen browser
/// factory, and the global cookie manager. Use from any .NET host —
/// console, WPF, MAUI, ASP.NET, services. UI-framework integrations
/// (<c>Exclr8Cef.WebView</c> for Avalonia, future WPF / MAUI variants)
/// build on the per-browser <see cref="CefBrowser"/> instance returned by
/// <see cref="CreateOffscreenBrowser"/>.
/// </summary>
public static partial class Cef
{
    private const int BufferSize = 64;

    /// <summary>
    /// Returns the linked shim, CEF, and Chromium versions. Safe to call
    /// before <see cref="Initialize"/>; reads compile-time constants from
    /// the shim.
    /// </summary>
    public static CefVersions GetVersions()
    {
        var raw = new excef_versions();
        unsafe
        {
            Excef.excef_get_versions(&raw);
            return new CefVersions(
                ReadCString((sbyte*)&raw.shim_version, BufferSize),
                ReadCString((sbyte*)&raw.cef_version, BufferSize),
                ReadCString((sbyte*)&raw.chromium_version, BufferSize));
        }
    }

    /// <summary>
    /// On Windows and Linux the same binary is reused as the CEF subprocess
    /// (renderer / GPU / utility / etc.); CEF re-invokes it with
    /// <c>--type=*</c>. The host main() must call this first; if it returns
    /// >= 0 the process is a CEF subprocess and should exit immediately
    /// with that code, otherwise (-1) it's the main process and continues.
    ///
    /// On macOS the subprocess lives in a separate <c>Helper.app</c> bundle
    /// so this returns -1 immediately and is effectively a no-op.
    /// </summary>
    public static int ExecuteProcess(string[]? args = null)
    {
        if (OperatingSystem.IsMacOS()) return -1;
        args = NormalizeCommandLineArguments(args);
        unsafe
        {
            sbyte** argv = AllocArgv(args, out int argc);
            try { return Excef.excef_execute_process(argc, argv); }
            finally { FreeArgv(argv, argc); }
        }
    }

    /// <summary>
    /// Initialize CEF with its own internal message loop. Call once, then
    /// <see cref="RunMessageLoop"/>. Suited for console / non-UI hosts.
    /// </summary>
    public static void Initialize(string[]? args = null, string? subprocessPath = null)
    {
        args = NormalizeCommandLineArguments(args);
        RegisterEventCallbacks();  // idempotent — wires all per-browser trampolines
        unsafe
        {
            sbyte** argv = AllocArgv(args, out int argc);
            sbyte* subprocPtr = MarshalUtf8(subprocessPath);
            try
            {
                int rc = Excef.excef_initialize(argc, argv, subprocPtr);
                if (rc != 0) throw new InvalidOperationException($"excef_initialize failed with code {rc}");
            }
            finally
            {
                FreeUtf8(subprocPtr);
                FreeArgv(argv, argc);
            }
        }
    }

    /// <summary>
    /// Initialize CEF with an external message pump. Use from UI hosts
    /// that already own the platform message loop (Avalonia, WPF). The
    /// host must call <see cref="DoMessageLoopWork"/> from its UI thread
    /// whenever <paramref name="schedulePumpWork"/> fires.
    /// </summary>
    public static void InitializeExternalPump(
        string[]? args,
        string? subprocessPath,
        Action<long> schedulePumpWork)
    {
        ArgumentNullException.ThrowIfNull(schedulePumpWork);
        args = NormalizeCommandLineArguments(args);
        s_scheduleCallback = schedulePumpWork;
        RegisterEventCallbacks();

        unsafe
        {
            sbyte** argv = AllocArgv(args, out int argc);
            sbyte* subprocPtr = MarshalUtf8(subprocessPath);
            try
            {
                delegate* unmanaged[Cdecl]<long, void> trampoline = &SchedulePumpWorkTrampoline;
                int rc = Excef.excef_initialize_external_pump(argc, argv, subprocPtr, trampoline);
                if (rc != 0) throw new InvalidOperationException($"excef_initialize_external_pump failed with code {rc}");
            }
            finally
            {
                FreeUtf8(subprocPtr);
                FreeArgv(argv, argc);
            }
        }
    }

    public static void DoMessageLoopWork() => Excef.excef_do_message_loop_work();

    /// <summary>
    /// A useful subset of <c>CefSettings</c>. All properties are optional;
    /// null / 0 means "use CEF's default". Pass an instance to
    /// <see cref="SetInitSettings"/> before any Initialize* call.
    /// </summary>
    public sealed class CefSettings
    {
        /// <summary>Disk cache + cookies directory. Null = in-memory.</summary>
        public string? CachePath { get; init; }
        /// <summary>Parent dir for browser caches. Null = system default.</summary>
        public string? RootCachePath { get; init; }
        /// <summary>Full user-agent override (replaces Chromium's UA).</summary>
        public string? UserAgent { get; init; }
        /// <summary>Product token appended to Chromium's UA, e.g. "MyApp/1.0".</summary>
        public string? UserAgentProduct { get; init; }
        /// <summary>UI locale, e.g. "en-US". Drives accept-language fallback.</summary>
        public string? Locale { get; init; }
        /// <summary>Override accept-language list, e.g. "en-US,en;q=0.9".</summary>
        public string? AcceptLanguageList { get; init; }
        /// <summary>Log file path. Null = stderr.</summary>
        public string? LogFile { get; init; }
        /// <summary>V8 command-line flags, e.g. "--max-old-space-size=512".</summary>
        public string? JavascriptFlags { get; init; }
        public CefLogSeverity LogSeverity { get; init; }
        /// <summary>Persist session cookies to <see cref="CachePath"/>.</summary>
        public bool PersistSessionCookies { get; init; }
        /// <summary>Open a Chromium DevTools remote-debugging port (0 = disabled).</summary>
        public int RemoteDebuggingPort { get; init; }
        /// <summary>
        /// Inject <c>window.exclr8cef.invoke</c> into main-frame page JavaScript.
        /// Disabled by default because it exposes a privileged host IPC surface
        /// to every top-level document loaded by the browser. Never injected
        /// into child frames.
        /// </summary>
        public bool EnableJavascriptBridge { get; init; }
    }

    /// <summary>
    /// Register a custom URL scheme (e.g. "app") that routes through
    /// <see cref="SchemeRequest"/>. MUST be called before
    /// <see cref="Initialize"/> / <see cref="InitializeForOsr"/> — late
    /// registration has no effect this process.
    /// </summary>
    /// <param name="name">Scheme name without the colon (e.g. "app", "myapp").</param>
    /// <param name="standard">URLs have host/path structure like http (recommended).</param>
    /// <param name="secure">Page is a "secure context" (enables crypto / service workers).</param>
    /// <param name="corsEnabled">Allow CORS requests to/from this scheme.</param>
    /// <param name="local">Local (file-like) — blocks XHR from non-local origins.</param>
    /// <param name="displayIsolated">Can only render in same-origin iframes.</param>
    /// <param name="cspBypassing">Exempt from Content-Security-Policy.</param>
    public static void RegisterCustomScheme(
        string name,
        bool standard = true, bool secure = true, bool corsEnabled = true,
        bool local = false, bool displayIsolated = false, bool cspBypassing = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(name);
            try
            {
                int rc = Excef.excef_register_custom_scheme(p,
                    standard ? 1 : 0, local ? 1 : 0, displayIsolated ? 1 : 0,
                    secure ? 1 : 0, corsEnabled ? 1 : 0, cspBypassing ? 1 : 0);
                if (rc != 0) throw new InvalidOperationException($"register_custom_scheme failed (rc={rc})");
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    /// <summary>
    /// Fires once per incoming request to a custom scheme registered via
    /// <see cref="RegisterCustomScheme"/>. The host MUST call
    /// <see cref="SchemeRequestEventArgs.Continue"/> or
    /// <see cref="SchemeRequestEventArgs.NotFound"/> to dismiss the request;
    /// leaving it unresolved hangs the renderer waiting for the response.
    /// </summary>
    public static event EventHandler<SchemeRequestEventArgs>? SchemeRequest;
    internal static bool HasSchemeRequestSubscriber => SchemeRequest is not null;

    /// <summary>
    /// Apply host-provided init settings. Call before any
    /// <see cref="Initialize"/> / <see cref="InitializeForOsr"/> /
    /// <see cref="InitializeExternalPump"/>; settings later than the
    /// init call have no effect on that process.
    /// </summary>
    public static void SetInitSettings(CefSettings? settings)
    {
        unsafe
        {
            if (settings is null) { Excef.excef_set_init_settings(null); return; }

            sbyte* cache    = MarshalUtf8(settings.CachePath);
            sbyte* rootC    = MarshalUtf8(settings.RootCachePath);
            sbyte* ua       = MarshalUtf8(settings.UserAgent);
            sbyte* uaProd   = MarshalUtf8(settings.UserAgentProduct);
            sbyte* locale   = MarshalUtf8(settings.Locale);
            sbyte* accept   = MarshalUtf8(settings.AcceptLanguageList);
            sbyte* logf     = MarshalUtf8(settings.LogFile);
            sbyte* jsFlags  = MarshalUtf8(settings.JavascriptFlags);
            try
            {
                var raw = new excef_init_settings
                {
                    cache_path = cache,
                    root_cache_path = rootC,
                    user_agent = ua,
                    user_agent_product = uaProd,
                    locale = locale,
                    accept_language_list = accept,
                    log_file = logf,
                    javascript_flags = jsFlags,
                    log_severity = (int)settings.LogSeverity,
                    persist_session_cookies = settings.PersistSessionCookies ? 1 : 0,
                    remote_debugging_port = settings.RemoteDebuggingPort,
                    enable_javascript_bridge = settings.EnableJavascriptBridge ? 1 : 0,
                };
                Excef.excef_set_init_settings(&raw);
            }
            finally
            {
                FreeUtf8(cache); FreeUtf8(rootC); FreeUtf8(ua); FreeUtf8(uaProd);
                FreeUtf8(locale); FreeUtf8(accept); FreeUtf8(logf); FreeUtf8(jsFlags);
            }
        }
    }

    /// <summary>
    /// Open a top-level Chromium browser window. Safe before or after
    /// <see cref="Initialize"/>; URLs requested pre-init are queued.
    /// Use <see cref="CreateOffscreenBrowser"/> for embedded OSR scenarios.
    /// </summary>
    public static void CreateBrowser(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        unsafe
        {
            sbyte* ptr = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try
            {
                int rc = Excef.excef_create_browser(ptr);
                if (rc != 0) throw new InvalidOperationException($"excef_create_browser failed with code {rc}");
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)ptr); }
        }
    }

    /// <summary>
    /// Create a native host view containing a CEF browser. Returns a handle
    /// to an <c>NSView</c> on macOS. Caller takes ownership.
    /// </summary>
    public static IntPtr CreateBrowserView(int width, int height, string url) =>
        CreateBrowserView(width, height, url, out _);

    /// <summary>
    /// Create a native host view containing a CEF browser. The host view
    /// is returned (an <c>NSView</c> handle on macOS, <c>HWND</c> on
    /// Windows); <paramref name="browser"/> gets the corresponding
    /// <see cref="CefBrowser"/> instance so the host can subscribe to
    /// per-browser events the same way as
    /// <see cref="CreateOffscreenBrowser"/>. Pass <paramref name="context"/>
    /// to isolate cookies / cache / storage; null = global context.
    /// </summary>
    public static IntPtr CreateBrowserView(int width, int height, string url, out CefBrowser? browser, CefRequestContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        RegisterEventCallbacks();  // make sure per-browser trampolines are wired
        browser = null;
        unsafe
        {
            sbyte* ptr = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            int id = 0;
            try
            {
                int ctxHandle = context?.Handle ?? 0;
                void* viewPtr = Excef.excef_create_browser_view_in_context(width, height, ptr, &id, ctxHandle);
                if (viewPtr == null) return IntPtr.Zero;
                if (id > 0)
                {
                    browser = new CefBrowser(id);
                    s_browsers[id] = browser;
                }
                return (IntPtr)viewPtr;
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)ptr); }
        }
    }

    /// <summary>
    /// Resize a host view returned by <see cref="CreateBrowserView"/>.
    /// Call on every layout change so the embedded Chromium tracks the
    /// host's size.
    /// </summary>
    public static void ResizeBrowserView(IntPtr hostView, int width, int height)
    {
        if (hostView == IntPtr.Zero || width <= 0 || height <= 0) return;
        unsafe { Excef.excef_resize_browser_view((void*)hostView, width, height); }
    }

    /// <summary>
    /// Hide / show the embedded host widget (NSView on mac, HWND on win)
    /// WITHOUT destroying its <see cref="CefBrowser"/>. Use when an
    /// embedded browser would otherwise paint over sibling host content
    /// (tab-switch surfaces, popovers, etc.) — Avalonia's
    /// <c>ZIndex</c> / <c>Opacity</c> don't reorder native children,
    /// so the host stays on top of every Avalonia draw call until the
    /// UI framework hides it. Also notifies Chromium via
    /// <c>CefBrowserHost::WasHidden</c> so it can throttle rendering
    /// while hidden.
    /// </summary>
    public static void SetEmbeddedHostHidden(IntPtr hostView, bool hidden)
    {
        if (hostView == IntPtr.Zero) return;
        unsafe { Excef.excef_set_embedded_host_hidden((void*)hostView, hidden ? 1 : 0); }
    }

    /// <summary>
    /// Phase 1 of two-phase embedded browser creation: allocate an empty
    /// host NSView. Return it to the UI framework via NativeControlHost so
    /// it gets parented to the window. Then call
    /// <see cref="AttachEmbeddedBrowser"/> after parenting — at that point
    /// Chromium can read the correct backingScaleFactor.
    /// </summary>
    public static IntPtr CreateEmbeddedHost(int width, int height)
    {
        unsafe { return (IntPtr)Excef.excef_create_embedded_host(width, height); }
    }

    /// <summary>
    /// Parent-aware variant of <see cref="CreateEmbeddedHost(int, int)"/>.
    /// REQUIRED on Windows: a <c>WS_CHILD</c> HWND cannot be created
    /// without a parent, so the parentless overload fails there. Pass the
    /// handle the UI framework supplies (Avalonia's
    /// <c>CreateNativeControlCore(IPlatformHandle parent)</c>). Ignored on
    /// macOS.
    /// </summary>
    public static IntPtr CreateEmbeddedHost(IntPtr parent, int width, int height)
    {
        unsafe { return (IntPtr)Excef.excef_create_embedded_host_in_parent((void*)parent, width, height); }
    }

    /// <summary>
    /// Release a host widget created by <see cref="CreateEmbeddedHost(int, int)"/>
    /// (balances the native retain on macOS / destroys the HWND on
    /// Windows). Safe to call while the attached browser is still closing —
    /// the native side defers actual destruction until the browser's close
    /// completes. Without this every create/destroy cycle leaks the native
    /// widget.
    /// </summary>
    public static void DestroyEmbeddedHost(IntPtr hostView)
    {
        if (hostView == IntPtr.Zero) return;
        unsafe { Excef.excef_destroy_embedded_host((void*)hostView); }
    }

    /// <summary>
    /// Phase 2 of two-phase embedded browser creation: attach a CEF
    /// browser to a previously-created host widget. Returns the browser
    /// instance, or null on failure. Wires up to <see cref="s_browsers"/>
    /// so the per-browser event surface fires. Pass <paramref name="context"/>
    /// to isolate cookies / cache / storage; null = global context.
    /// </summary>
    public static CefBrowser? AttachEmbeddedBrowser(IntPtr hostView, int width, int height, string url, CefRequestContext? context = null, uint backgroundColor = 0)
    {
        if (hostView == IntPtr.Zero) return null;
        ArgumentNullException.ThrowIfNull(url);
        RegisterEventCallbacks();
        unsafe
        {
            sbyte* ptr = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try
            {
                int ctxHandle = context?.Handle ?? 0;
                int id = Excef.excef_attach_embedded_browser_in_context_v2((void*)hostView, width, height, ptr, ctxHandle, backgroundColor);
                if (id <= 0) return null;
                var browser = new CefBrowser(id);
                s_browsers[id] = browser;
                return browser;
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)ptr); }
        }
    }

    /// <summary>
    /// Initialize CEF in off-screen-rendering mode with an external message
    /// pump. Use from UI hosts that want to embed Chromium without giving
    /// CEF a native window — typical for Avalonia / WPF integration.
    /// Combine with <see cref="CreateOffscreenBrowser"/>.
    /// </summary>
    public static void InitializeForOsr(
        string[]? args,
        string? subprocessPath,
        Action<long> schedulePumpWork)
    {
        ArgumentNullException.ThrowIfNull(schedulePumpWork);
        args = NormalizeCommandLineArguments(args);
        s_scheduleCallback = schedulePumpWork;
        RegisterEventCallbacks();

        unsafe
        {
            sbyte** argv = AllocArgv(args, out int argc);
            sbyte* subprocPtr = MarshalUtf8(subprocessPath);
            try
            {
                delegate* unmanaged[Cdecl]<long, void> trampoline = &SchedulePumpWorkTrampoline;
                int rc = Excef.excef_initialize_offscreen(argc, argv, subprocPtr, trampoline);
                if (rc != 0) throw new InvalidOperationException($"excef_initialize_offscreen failed with code {rc}");
            }
            finally
            {
                FreeUtf8(subprocPtr);
                FreeArgv(argv, argc);
            }
        }
    }

    /// <summary>
    /// Create an off-screen browser whose view rect is <paramref name="width"/> ×
    /// <paramref name="height"/> DIPs. <paramref name="deviceScaleFactor"/> drives
    /// HiDPI: CEF allocates a paint buffer of (w × scale) × (h × scale) physical
    /// pixels while the page lays out at DIP size. Pass <c>1.0f</c> for non-HiDPI.
    /// Passing a <paramref name="context"/> isolates this browser's cookies /
    /// cache / storage from any other browser in a different context; pass
    /// <c>null</c> for the global default context (shared with other no-context
    /// browsers).
    /// Returns a <see cref="CefBrowser"/> with the full per-browser event/command
    /// surface, or <c>null</c> if creation failed.
    /// </summary>
    public static CefBrowser? CreateOffscreenBrowser(int width, int height, float deviceScaleFactor, string url, CefRequestContext? context = null)
        => CreateOffscreenBrowserEx(width, height, deviceScaleFactor, url, context, OffscreenFlags.None);

    /// <summary>
    /// Optional flags for <see cref="CreateOffscreenBrowserEx"/>. Map to
    /// CefWindowInfo bits set at browser-creation time and can't be
    /// toggled afterwards.
    /// </summary>
    [Flags]
    public enum OffscreenFlags
    {
        None = 0,
        /// <summary>
        /// Set <c>external_begin_frame_enabled = true</c>. The host drives
        /// frames via <see cref="CefBrowser.SendExternalBeginFrame"/>;
        /// the normal windowless-frame-rate timer stops driving paints.
        /// </summary>
        ExternalBeginFrame = 0x1,
        /// <summary>
        /// Set <c>shared_texture_enabled = true</c>. CEF delivers paints
        /// via <see cref="CefBrowser.AcceleratedPaint"/> with a
        /// platform-specific shared-texture handle instead of the
        /// <see cref="CefBrowser.Painted"/> CPU buffer. Consumer code must
        /// do GPU interop to use the texture (IOSurface on macOS, NT
        /// handle on Windows, dma-buf on Linux).
        /// </summary>
        SharedTexture = 0x2,
    }

    /// <summary>
    /// Extended OSR browser factory — same as <see cref="CreateOffscreenBrowser"/>
    /// plus <see cref="OffscreenFlags"/> for the rarer CefWindowInfo
    /// toggles (external begin-frame, GPU shared-texture path).
    /// </summary>
    public static CefBrowser? CreateOffscreenBrowserEx(int width, int height, float deviceScaleFactor, string url, CefRequestContext? context, OffscreenFlags flags)
    {
        ArgumentNullException.ThrowIfNull(url);
        unsafe
        {
            sbyte* urlPtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try
            {
                delegate* unmanaged[Cdecl]<int, void*, int, int, void> trampoline = &PaintTrampoline;
                int id = Excef.excef_create_offscreen_browser_ex(
                    width, height, deviceScaleFactor, urlPtr, trampoline,
                    context?.Handle ?? 0, (int)flags);
                if (id <= 0) return null;
                var browser = new CefBrowser(id);
                s_browsers[id] = browser;
                return browser;
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)urlPtr); }
        }
    }

    /// <summary>
    /// Create an isolated request context — its own cookie jar, cache,
    /// and per-origin storage. Pass <paramref name="cachePath"/> to
    /// persist to disk; pass <c>null</c> for an in-memory ("incognito")
    /// context that disappears when the last browser using it closes.
    /// </summary>
    /// <returns>A <see cref="CefRequestContext"/> wrapper, or <c>null</c> on failure.</returns>
    public static CefRequestContext? CreateRequestContext(string? cachePath = null)
    {
        unsafe
        {
            sbyte* p = cachePath is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(cachePath);
            try
            {
                int handle = Excef.excef_create_request_context(p);
                return handle > 0 ? new CefRequestContext(handle) : null;
            }
            finally { if (p is not null) Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    /// <summary>Iterates the currently-live OSR browsers.</summary>
    public static IEnumerable<CefBrowser> Browsers => s_browsers.Values;

    /// <summary>
    /// Append an extra Chromium command-line switch that will be applied
    /// explicitly to the browser process at
    /// <c>OnBeforeCommandLineProcessing</c> time. Chromium forwards supported
    /// switches to the subprocesses that need them. Examples:
    /// <c>AddCommandLineSwitch("enable-features", "WebGPU,WebAssemblyDynamicTiering")</c>,
    /// <c>AddCommandLineSwitch("disable-blink-features", "AutomationControlled")</c>.
    /// MUST be called before any Initialize* call.
    /// </summary>
    public static void AddCommandLineSwitch(string name, string? value = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        unsafe
        {
            sbyte* n = (sbyte*)Marshal.StringToCoTaskMemUTF8(name);
            sbyte* v = value is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(value);
            try { Excef.excef_add_command_line_switch(n, v); }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)n);
                if (v != null) Marshal.FreeCoTaskMem((IntPtr)v);
            }
        }
    }

    // ---- Cookies (process-wide cookie manager) -------------------------

    public sealed record CookieInfo(
        string Name,
        string Value,
        string Domain,
        string Path,
        bool Secure,
        bool HttpOnly);

    /// <summary>
    /// Get cookies from the global cookie manager. Pass empty/null
    /// <paramref name="url"/> to get all cookies. For partitioned cookies
    /// (per-profile) use <see cref="CefRequestContext.GetCookiesAsync"/>.
    /// </summary>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread.
    /// Marshal back to the UI thread before any follow-up
    /// <see cref="CefBrowser"/> call.
    /// </remarks>
    public static Task<List<CookieInfo>> GetCookiesAsync(string? url = null)
        => GetCookiesAsyncInContext(0, url);

    public static bool SetCookie(string url, string name, string value,
                                  string? domain = null, string? path = null,
                                  bool secure = false, bool httpOnly = false)
        => SetCookieInContext(0, url, name, value, domain, path, secure, httpOnly);

    public static void DeleteCookies(string? url = null, string? name = null)
        => DeleteCookiesInContext(0, url, name);

    internal static Task<List<CookieInfo>> GetCookiesAsyncInContext(int contextHandle, string? url)
    {
        int reqId = Interlocked.Increment(ref s_nextCookieRequestId);
        var tcs = new TaskCompletionSource<List<CookieInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_cookieRequests[reqId] = (new List<CookieInfo>(), tcs);

        unsafe
        {
            sbyte* urlPtr = url is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try
            {
                int got = Excef.excef_get_cookies_in_context(contextHandle, urlPtr, reqId);
                if (got == 0)
                {
                    s_cookieRequests.TryRemove(reqId, out _);
                    tcs.TrySetException(new InvalidOperationException("CefCookieManager unavailable"));
                }
            }
            finally
            {
                if (urlPtr is not null) Marshal.FreeCoTaskMem((IntPtr)urlPtr);
            }
        }
        return tcs.Task;
    }

    internal static bool SetCookieInContext(int contextHandle,
                                              string url, string name, string value,
                                              string? domain, string? path,
                                              bool secure, bool httpOnly)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(name);
        unsafe
        {
            sbyte* urlPtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            sbyte* namePtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(name);
            sbyte* valuePtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(value ?? "");
            sbyte* domainPtr = domain is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(domain);
            sbyte* pathPtr = path is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                return Excef.excef_set_cookie_in_context(contextHandle, urlPtr, namePtr, valuePtr,
                                                          domainPtr, pathPtr,
                                                          secure ? 1 : 0, httpOnly ? 1 : 0) != 0;
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)urlPtr);
                Marshal.FreeCoTaskMem((IntPtr)namePtr);
                Marshal.FreeCoTaskMem((IntPtr)valuePtr);
                if (domainPtr is not null) Marshal.FreeCoTaskMem((IntPtr)domainPtr);
                if (pathPtr is not null) Marshal.FreeCoTaskMem((IntPtr)pathPtr);
            }
        }
    }

    internal static void DeleteCookiesInContext(int contextHandle, string? url, string? name)
    {
        unsafe
        {
            sbyte* urlPtr = url is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            sbyte* namePtr = name is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(name);
            try { Excef.excef_delete_cookies_in_context(contextHandle, urlPtr, namePtr); }
            finally
            {
                if (urlPtr is not null) Marshal.FreeCoTaskMem((IntPtr)urlPtr);
                if (namePtr is not null) Marshal.FreeCoTaskMem((IntPtr)namePtr);
            }
        }
    }

    // ---- Response filter (body rewrite) -------------------------------
    //
    // Process-wide hooks; the per-response token lets the host keep state
    // across chunks of a single response (e.g. a streaming HTML rewriter).
    // Callbacks fire on CEF's network/IO thread — they MUST be fast and
    // non-blocking. Do not touch the UI thread from here.

    public enum ResponseFilterStatus
    {
        /// <summary>More input expected — leave bytes_read &lt; size_in unconsumed if needed.</summary>
        NeedMoreData = 0,
        /// <summary>Response body is finished — no more bytes from CEF after this call.</summary>
        Done = 1,
        /// <summary>Abort the response — CEF will fail the request.</summary>
        Error = -1,
    }

    /// <summary>
    /// Decision-point info passed to <see cref="ShouldFilterResponse"/>.
    /// <see cref="Browser"/> may be null if the response originated from
    /// a browser that's already torn down (rare, but possible).
    /// </summary>
    public readonly record struct ResponseFilterDecision(
        CefBrowser? Browser,
        ulong Token,
        string Url,
        int HttpStatus,
        string MimeType);

    /// <summary>
    /// Host predicate: return true to install a filter for the given
    /// response. Called once per response (per resource the browser
    /// fetches). The same Token is then handed to <see cref="ResponseFilter"/>
    /// for every chunk of that response.
    /// </summary>
    public static Func<ResponseFilterDecision, bool>? ShouldFilterResponse
    {
        get => s_shouldFilterResponse;
        set => s_shouldFilterResponse = value;
    }

    /// <summary>
    /// Per-chunk transform. Read bytes from <paramref name="dataIn"/>, write
    /// to <paramref name="dataOut"/>. <paramref name="bytesRead"/> /
    /// <paramref name="bytesWritten"/> tell CEF how much you consumed and
    /// produced respectively. Both spans may be empty (e.g. at end-of-stream
    /// CEF calls Filter with size_in=0 expecting you to flush remaining
    /// buffered output and return Done).
    /// </summary>
    public delegate ResponseFilterStatus ResponseFilterDelegate(
        int browserId, ulong token,
        ReadOnlySpan<byte> dataIn, Span<byte> dataOut,
        out int bytesRead, out int bytesWritten);

    public static ResponseFilterDelegate? ResponseFilter
    {
        get => s_responseFilter;
        set => s_responseFilter = value;
    }

    /// <summary>
    /// Fires once per filter end-of-life — host can drop any per-token
    /// state (HTML parser, injection state machine, etc.).
    /// </summary>
    public static Action<int, ulong>? ResponseFilterFinalize
    {
        get => s_responseFilterFinalize;
        set => s_responseFilterFinalize = value;
    }

    private static Func<ResponseFilterDecision, bool>? s_shouldFilterResponse;
    private static ResponseFilterDelegate? s_responseFilter;
    private static Action<int, ulong>? s_responseFilterFinalize;

    // ---- Command handler (Chrome runtime only) ------------------------
    //
    // Intercept Chrome's menu commands, toolbar buttons, and page-action
    // icons. Only meaningful when CEF is initialized with the Chrome
    // runtime (windowed mode without OSR). All hooks are synchronous host
    // queries — keep them fast.

    /// <summary>Subset of Chromium's IDC_* command IDs that hosts most often intercept.</summary>
    public enum ChromeCommand
    {
        NewTab = 34014,
        CloseTab = 34015,
        NewWindow = 34000,
        Print = 41001,
        OpenFile = 34045,
        Find = 41007,
        ZoomPlus = 33001,
        ZoomMinus = 33002,
        ZoomNormal = 33003,
        DevTools = 40005,
        ViewSource = 40002,
        SavePage = 34046,
        Bookmarks = 35000,
        History = 34030,
    }

    /// <summary>cef_chrome_page_action_icon_type_t.</summary>
    public enum ChromePageActionIcon
    {
        BookmarkStar = 0,
        CookieControls = 2,
        Find = 4,
        ManagePasswords = 8,
    }

    /// <summary>cef_chrome_toolbar_button_type_t.</summary>
    public enum ChromeToolbarButton
    {
        Media = 8,
        TabSearch = 9,
        BatterySaver = 10,
        Avatar = 11,
    }

    /// <summary>
    /// Return true to suppress Chrome's default handling of the command.
    /// Return false to let Chrome execute its normal behavior. The
    /// browser argument may be null if the command originated outside
    /// any tracked browser (e.g. menu bar with no active window).
    /// </summary>
    public static Func<CefBrowser?, int, WindowOpenDisposition, bool>? OnChromeCommand
    {
        get => s_onChromeCommand;
        set => s_onChromeCommand = value;
    }

    /// <summary>Return true = item visible in app menu; false = hide.</summary>
    public static Func<CefBrowser?, int, bool>? IsChromeAppMenuItemVisible
    {
        get => s_isAppMenuItemVisible;
        set => s_isAppMenuItemVisible = value;
    }

    /// <summary>Return true = enabled; false = greyed out.</summary>
    public static Func<CefBrowser?, int, bool>? IsChromeAppMenuItemEnabled
    {
        get => s_isAppMenuItemEnabled;
        set => s_isAppMenuItemEnabled = value;
    }

    public static Func<ChromePageActionIcon, bool>? IsChromePageActionIconVisible
    {
        get => s_isPageActionIconVisible;
        set => s_isPageActionIconVisible = value;
    }

    public static Func<ChromeToolbarButton, bool>? IsChromeToolbarButtonVisible
    {
        get => s_isToolbarButtonVisible;
        set => s_isToolbarButtonVisible = value;
    }

    private static Func<CefBrowser?, int, WindowOpenDisposition, bool>? s_onChromeCommand;
    private static Func<CefBrowser?, int, bool>? s_isAppMenuItemVisible;
    private static Func<CefBrowser?, int, bool>? s_isAppMenuItemEnabled;
    private static Func<ChromePageActionIcon, bool>? s_isPageActionIconVisible;
    private static Func<ChromeToolbarButton, bool>? s_isToolbarButtonVisible;

    // ---- Custom resource handler (per-URL intercept) ------------------
    //
    // Claim a URL via <see cref="ShouldHandleResource"/>; the host then
    // calls <see cref="ResolveResourceHandlerRequest"/> with the response.
    // Unlike <see cref="CefSchemeHandler"/>-style registration, this fires
    // for any URL (http://, https://, file://, app://, …) and lets the
    // host decide per-request rather than per-scheme.

    /// <summary>
    /// Info passed to <see cref="ShouldHandleResource"/>.
    /// <see cref="Browser"/> may be null if the request originated from a
    /// browser that's been torn down (rare).
    /// </summary>
    public readonly record struct ResourceHandlerDecision(
        CefBrowser? Browser,
        ulong Token,
        string Url,
        string Method);

    /// <summary>
    /// Per-URL claim predicate. Return true to take over the request —
    /// you must then call <see cref="ResolveResourceHandlerRequest"/>
    /// with the response payload (sync or async). The same Token threads
    /// through both calls.
    /// </summary>
    public static Func<ResourceHandlerDecision, bool>? ShouldHandleResource
    {
        get => s_shouldHandleResource;
        set => s_shouldHandleResource = value;
    }

    private static Func<ResourceHandlerDecision, bool>? s_shouldHandleResource;

    /// <summary>
    /// Provide the response body for a request previously claimed via
    /// <see cref="ShouldHandleResource"/>. <paramref name="headers"/> is
    /// a flat list of (name, value) pairs added on top of Content-Type.
    /// </summary>
    public static void ResolveResourceHandlerRequest(
        ulong token,
        int statusCode,
        string statusText,
        string mimeType,
        ReadOnlySpan<byte> body,
        IEnumerable<(string Name, string Value)>? headers = null)
    {
        string? headerLines = null;
        if (headers is not null)
        {
            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (var (n, v) in headers)
            {
                if (!first) sb.Append('\n');
                first = false;
                sb.Append(n).Append(": ").Append(v);
            }
            if (sb.Length > 0) headerLines = sb.ToString();
        }

        unsafe
        {
            sbyte* statusTextPtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(statusText ?? "");
            sbyte* mimePtr       = (sbyte*)Marshal.StringToCoTaskMemUTF8(mimeType ?? "text/plain");
            sbyte* headersPtr    = headerLines is null ? null
                                    : (sbyte*)Marshal.StringToCoTaskMemUTF8(headerLines);
            try
            {
                fixed (byte* bodyPtr = body)
                {
                    Excef.excef_resolve_resource_handler_request(
                        token, statusCode, statusTextPtr, mimePtr, headersPtr,
                        bodyPtr, body.Length);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)statusTextPtr);
                Marshal.FreeCoTaskMem((IntPtr)mimePtr);
                if (headersPtr != null) Marshal.FreeCoTaskMem((IntPtr)headersPtr);
            }
        }
    }

    // ---- Process lifecycle ---------------------------------------------

    public static void RunMessageLoop() => Excef.excef_run_message_loop();
    public static void QuitMessageLoop() => Excef.excef_quit_message_loop();

    public static void Shutdown()
    {
        // Tear down live browsers' managed state FIRST so their pending
        // awaiters (evals, DevTools methods, PDF callbacks, frame streams)
        // complete with an error rather than hanging forever — native
        // CefShutdown won't deliver any further callbacks to resolve them.
        foreach (var id in s_browsers.Keys)
        {
            if (s_browsers.TryRemove(id, out var b))
            {
                try { b.RaiseClosed(); }
                catch { }
            }
        }
        Excef.excef_shutdown();
        s_scheduleCallback = null;
        var shutdownEx = new InvalidOperationException("CEF shut down");
        // Anything still pending after RaiseClosed (requests not tracked to
        // a live browser): fail, don't strand — Clear() would leave the
        // awaiting Tasks incomplete forever.
        foreach (var id in s_evalRequests.Keys)
            if (s_evalRequests.TryRemove(id, out var tcs)) tcs.TrySetException(shutdownEx);
        foreach (var id in s_cookieRequests.Keys)
            if (s_cookieRequests.TryRemove(id, out var entry)) entry.Tcs.TrySetException(shutdownEx);
        foreach (var id in s_stringVisitorRequests.Keys)
            if (s_stringVisitorRequests.TryRemove(id, out var tcs)) tcs.TrySetException(shutdownEx);
        foreach (var id in s_navEntryRequests.Keys)
            if (s_navEntryRequests.TryRemove(id, out var tcs)) tcs.TrySetException(shutdownEx);
        s_navEntryAccum.Clear();
    }

    // ---- Shared enums (used by CefBrowser methods + native ABI) --------

    [Flags]
    public enum CefModifiers : uint
    {
        None = 0,
        CapsLock = 1u << 0,
        Shift = 1u << 1,
        Control = 1u << 2,
        Alt = 1u << 3,
        LeftMouseButton = 1u << 4,
        MiddleMouseButton = 1u << 5,
        RightMouseButton = 1u << 6,
        Command = 1u << 7,
    }

    public enum CefMouseButton { Left = 0, Middle = 1, Right = 2 }

    public enum CefKeyEventType { RawKeyDown = 0, KeyDown = 1, KeyUp = 2, Char = 3 }

    /// <summary>CEF's <c>cef_zoom_command_t</c>; argument to <see cref="CefBrowser.CanZoom"/>.</summary>
    public enum CefZoomCommand { Out = 0, Reset = 1, In = 2 }

    /// <summary>Which paint surface — main view or popup (e.g. dropdown overlay).</summary>
    public enum PaintElementType { Main = 0, Popup = 1 }

    /// <summary>CEF's <c>cef_touch_event_type_t</c>.</summary>
    public enum CefTouchEventType { Released = 0, Pressed = 1, Moved = 2, Cancelled = 3 }

    /// <summary>CEF's <c>cef_pointer_type_t</c>.</summary>
    public enum CefPointerType { Touch = 0, Mouse = 1, Pen = 2, Eraser = 3, Unknown = 4 }

    /// <summary>
    /// CEF's <c>cef_text_input_mode_t</c> — argument to
    /// <see cref="CefBrowser.VirtualKeyboardRequested"/>.
    /// <see cref="None"/> means "hide any existing on-screen keyboard."
    /// </summary>
    public enum CefTextInputMode
    {
        Default = 0,
        None = 1,
        Text = 2,
        Tel = 3,
        Url = 4,
        Email = 5,
        Numeric = 6,
        Decimal = 7,
        Search = 8,
    }

    /// <summary>CEF's <c>cef_horizontal_alignment_t</c> — touch handle side.</summary>
    public enum HorizontalAlignment { Left = 0, Center = 1, Right = 2 }

    /// <summary>
    /// CEF's <c>cef_color_type_t</c> — pixel format reported by
    /// <see cref="CefBrowser.AcceleratedPaint"/>. Values mirror CEF's
    /// header order (NOT alphabetical): RGBA = 0, BGRA = 1.
    /// </summary>
    public enum CefColorType
    {
        Rgba8888 = 0,
        Bgra8888 = 1,
    }

    /// <summary>
    /// Which JS dialog primitive triggered <see cref="CefBrowser.JsDialog"/>.
    /// Mirrors <c>cef_jsdialog_type_t</c>; <c>BeforeUnload</c> is our own
    /// extension for the unload-confirmation case (CEF surfaces it via a
    /// separate handler method, but we collapse both into one event).
    /// </summary>
    public enum JsDialogType { Alert = 0, Confirm = 1, Prompt = 2, BeforeUnload = 3 }

    /// <summary>What kind of file picker the page is asking for.</summary>
    public enum FileDialogMode { Open = 0, OpenMultiple = 1, OpenFolder = 2, Save = 3 }

    /// <summary>Lifecycle state on a <see cref="DownloadProgressEventArgs"/>.</summary>
    public enum DownloadState { InProgress = 0, Complete = 1, Canceled = 2 }

    /// <summary>
    /// Drag-and-drop operation mask. Mirrors <c>cef_drag_operations_mask_t</c>;
    /// flag combinations are valid (e.g. <c>Copy | Move</c>).
    /// </summary>
    [Flags]
    public enum DragOperations : uint
    {
        None    = 0,
        Copy    = 1,
        Link    = 2,
        Generic = 4,
        Private = 8,
        Move    = 16,
        Delete  = 32,
        Every   = 0xFFFFFFFF,
    }

    /// <summary>
    /// Permission categories the page can ask for (Notifications,
    /// Geolocation, …). Bitmask; mirrors <c>cef_permission_request_types_t</c>.
    /// </summary>
    [Flags]
    public enum PermissionRequestType : uint
    {
        None                       = 0,
        ArSession                  = 1 << 0,
        CameraPanTiltZoom          = 1 << 1,
        CameraStream               = 1 << 2,
        CapturedSurfaceControl     = 1 << 3,
        Clipboard                  = 1 << 4,
        TopLevelStorageAccess      = 1 << 5,
        DiskQuota                  = 1 << 6,
        LocalFonts                 = 1 << 7,
        Geolocation                = 1 << 8,
        HandTracking               = 1 << 9,
        IdentityProvider           = 1 << 10,
        IdleDetection              = 1 << 11,
        MicStream                  = 1 << 12,
        MidiSysex                  = 1 << 13,
        MultipleDownloads          = 1 << 14,
        Notifications              = 1 << 15,
        KeyboardLock               = 1 << 16,
        PointerLock                = 1 << 17,
        ProtectedMediaIdentifier   = 1 << 18,
        RegisterProtocolHandler    = 1 << 19,
        StorageAccess              = 1 << 20,
        VrSession                  = 1 << 21,
        WebAppInstallation         = 1 << 22,
        WindowManagement           = 1 << 23,
        FileSystemAccess           = 1 << 24,
    }

    /// <summary>Source of a CefFocusHandler::OnSetFocus request. Mirrors <c>cef_focus_source_t</c>.</summary>
    public enum FocusSource { Navigation = 0, System = 1 }

    /// <summary>How a permission request was resolved. Mirrors <c>cef_permission_request_result_t</c>.</summary>
    public enum PermissionResult
    {
        Accept = 0,
        Deny = 1,
        Dismiss = 2,
        Ignore = 3,
    }

    /// <summary>
    /// Sub-categories of media-access requests (getUserMedia). Bitmask;
    /// mirrors <c>cef_media_access_permission_types_t</c>.
    /// </summary>
    [Flags]
    public enum MediaAccessPermissions : uint
    {
        None                = 0,
        DeviceAudioCapture  = 1 << 0,
        DeviceVideoCapture  = 1 << 1,
        DesktopAudioCapture = 1 << 2,
        DesktopVideoCapture = 1 << 3,
    }

    /// <summary>
    /// How the page wanted to open the popup. Mirrors
    /// <c>cef_window_open_disposition_t</c>. Most pages use
    /// <see cref="NewForegroundTab"/> for <c>window.open()</c>.
    /// </summary>
    public enum WindowOpenDisposition
    {
        Unknown = 0,
        CurrentTab = 1,
        SingletonTab = 2,
        NewForegroundTab = 3,
        NewBackgroundTab = 4,
        NewPopup = 5,
        NewWindow = 6,
        SaveToDisk = 7,
        OffTheRecord = 8,
        IgnoreAction = 9,
        SwitchToTab = 10,
        NewPictureInPicture = 11,
    }

    /// <summary>
    /// Args for <see cref="SchemeRequest"/>. Host MUST call exactly one of
    /// <see cref="Continue"/> / <see cref="NotFound"/>.
    /// </summary>
    public sealed class SchemeRequestEventArgs : EventArgs
    {
        private readonly ulong _token;
        private int _resolved;

        /// <summary>The CefBrowser id that initiated the request (0 for non-browser-originated).</summary>
        public int BrowserId { get; }
        public string Url { get; }
        /// <summary>HTTP method — "GET", "POST", etc.</summary>
        public string Method { get; }

        internal SchemeRequestEventArgs(ulong token, int browserId, string url, string method)
        {
            _token = token; BrowserId = browserId; Url = url; Method = method;
        }

        /// <summary>
        /// Resolve the request with body bytes + status. <paramref name="mimeType"/>
        /// is required so the renderer parses the body correctly (HTML pages, JS,
        /// images, etc. all need a sensible Content-Type).
        /// </summary>
        public void Continue(byte[] body,
                              string mimeType = "application/octet-stream",
                              int statusCode = 200,
                              string statusText = "OK")
        {
            if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
            ArgumentNullException.ThrowIfNull(body);
            unsafe
            {
                sbyte* text = (sbyte*)Marshal.StringToCoTaskMemUTF8(statusText ?? "");
                sbyte* mime = (sbyte*)Marshal.StringToCoTaskMemUTF8(mimeType ?? "");
                fixed (byte* bp = body)
                {
                    try { Excef.excef_resolve_scheme_request(_token, statusCode, text, mime, bp, body.Length); }
                    finally { Marshal.FreeCoTaskMem((IntPtr)text); Marshal.FreeCoTaskMem((IntPtr)mime); }
                }
            }
        }

        public void NotFound()
        {
            if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
            unsafe { Excef.excef_resolve_scheme_request(_token, 404, null, null, null, 0); }
        }
    }

    /// <summary>
    /// Why a renderer subprocess terminated. Mirrors cef_termination_status_t.
    /// </summary>
    public enum TerminationStatus
    {
        AbnormalTermination = 0,
        ProcessWasKilled    = 1,
        ProcessCrashed      = 2,
        OutOfMemory         = 3,
        LaunchFailed        = 4,
        IntegrityFailure    = 5,
    }

    /// <summary>
    /// What a resource request is for. Mirrors cef_resource_type_t (most
    /// values present; less-common ones omitted from the enum but pass
    /// through as the underlying int).
    /// </summary>
    public enum ResourceType
    {
        MainFrame              = 0,
        SubFrame               = 1,
        Stylesheet             = 2,
        Script                 = 3,
        Image                  = 4,
        Font                   = 5,
        SubResource            = 6,
        Object                 = 7,
        Media                  = 8,
        Worker                 = 9,
        SharedWorker           = 10,
        Prefetch               = 11,
        Favicon                = 12,
        Xhr                    = 13,
        Ping                   = 14,
        ServiceWorker          = 15,
        CspReport              = 16,
        PluginResource         = 17,
        NavigationPreloadMainFrame = 19,
        NavigationPreloadSubFrame  = 20,
    }

    /// <summary>
    /// CEF's <c>cef_log_severity_t</c>. Carried by <see cref="CefBrowser.ConsoleMessage"/>;
    /// derived from the JS console method (<c>console.log</c> → Info,
    /// <c>console.warn</c> → Warning, <c>console.error</c> → Error, …).
    /// </summary>
    public enum CefLogSeverity
    {
        Default = 0,
        Verbose = 1,  // a.k.a. Debug
        Info    = 2,
        Warning = 3,
        Error   = 4,
        Fatal   = 5,
        Disable = 99,
    }

    /// <summary>
    /// A useful subset of CEF's <c>cef_errorcode_t</c> (cef_errors.h). All
    /// codes are negative; 0 = <c>None</c>. Pass unknown values through
    /// untouched — the underlying int is preserved.
    /// </summary>
    public enum CefErrorCode
    {
        None = 0,
        Failed = -2,
        Aborted = -3,
        InvalidArgument = -4,
        InvalidHandle = -5,
        FileNotFound = -6,
        TimedOut = -7,
        FileTooBig = -8,
        Unexpected = -9,
        AccessDenied = -10,
        NotImplemented = -11,
        InsufficientResources = -12,
        OutOfMemory = -13,
        ConnectionClosed = -100,
        ConnectionReset = -101,
        ConnectionRefused = -102,
        ConnectionAborted = -103,
        ConnectionFailed = -104,
        NameNotResolved = -105,
        InternetDisconnected = -106,
        SslProtocolError = -107,
        AddressInvalid = -108,
        AddressUnreachable = -109,
        SslClientAuthCertNeeded = -110,
        TunnelConnectionFailed = -111,
        NoSslVersionsEnabled = -112,
        SslVersionOrCipherMismatch = -113,
        SslRenegotiationRequested = -114,
        CertCommonNameInvalid = -200,
        CertDateInvalid = -201,
        CertAuthorityInvalid = -202,
        CertContainsErrors = -203,
        CertNoRevocationMechanism = -204,
        CertUnableToCheckRevocation = -205,
        CertRevoked = -206,
        CertInvalid = -207,
        InvalidUrl = -300,
        DisallowedUrlScheme = -301,
        UnknownUrlScheme = -302,
        UnsafeRedirect = -310,
        UnsafePort = -311,
        InvalidResponse = -320,
        InvalidChunkedEncoding = -321,
        MethodNotSupported = -322,
        UnexpectedProxyAuth = -323,
        EmptyResponse = -324,
        ResponseHeadersTooBig = -325,
    }

    /// <summary>CEF's <c>cef_cursor_type_t</c>. Maps to OnCursorChange values.</summary>
    public enum CefCursorType
    {
        Pointer = 0, Cross = 1, Hand = 2, IBeam = 3, Wait = 4, Help = 5,
        EastResize = 6, NorthResize = 7, NorthEastResize = 8, NorthWestResize = 9,
        SouthResize = 10, SouthEastResize = 11, SouthWestResize = 12, WestResize = 13,
        NorthSouthResize = 14, EastWestResize = 15,
        NorthEastSouthWestResize = 16, NorthWestSouthEastResize = 17,
        ColumnResize = 18, RowResize = 19,
        MiddlePanning = 20, EastPanning = 21, NorthPanning = 22,
        NorthEastPanning = 23, NorthWestPanning = 24, SouthPanning = 25,
        SouthEastPanning = 26, SouthWestPanning = 27, WestPanning = 28,
        Move = 29, VerticalText = 30, Cell = 31, ContextMenu = 32,
        Alias = 33, Progress = 34, NoDrop = 35, Copy = 36, None = 37,
        NotAllowed = 38, ZoomIn = 39, ZoomOut = 40, Grab = 41, Grabbing = 42,
        MiddlePanningVertical = 43, MiddlePanningHorizontal = 44,
        Custom = 45,
        DndNone = 46, DndMove = 47, DndCopy = 48, DndLink = 49,
    }

    // ---- Native callback wiring ----------------------------------------

    private static bool s_eventsRegistered;
    private static readonly object s_eventsLock = new();

    /// <summary>
    /// Register the native event callbacks. Idempotent. Called automatically
    /// from <see cref="InitializeForOsr"/>; exposed for hosts that initialize
    /// CEF themselves but still want events.
    /// </summary>
    public static void RegisterEventCallbacks()
    {
        lock (s_eventsLock)
        {
            if (s_eventsRegistered) return;
            unsafe
            {
                Excef.excef_set_address_change_callback(&AddressChangeTrampoline);
                Excef.excef_set_title_change_callback(&TitleChangeTrampoline);
                Excef.excef_set_loading_state_callback(&LoadingStateTrampoline);
                Excef.excef_set_before_browse_callback(&BeforeBrowseTrampoline);
                Excef.excef_set_browser_closed_callback(&BrowserClosedTrampoline);
                Excef.excef_set_eval_result_callback(&EvalResultTrampoline);
                Excef.excef_set_cookie_visit_callback(&CookieVisitTrampoline);
                Excef.excef_set_request_context_completion_callback(
                    &RequestContextCompletionTrampoline);
                Excef.excef_set_cursor_change_callback(&CursorChangeTrampoline);
                Excef.excef_set_console_message_callback(&ConsoleMessageTrampoline);
                Excef.excef_set_load_start_callback(&LoadStartTrampoline);
                Excef.excef_set_load_end_callback(&LoadEndTrampoline);
                Excef.excef_set_load_error_callback(&LoadErrorTrampoline);
                Excef.excef_set_loading_progress_callback(&LoadingProgressTrampoline);
                Excef.excef_set_status_message_callback(&StatusMessageTrampoline);
                Excef.excef_set_tooltip_callback(&TooltipTrampoline);
                Excef.excef_set_favicon_callback(&FaviconTrampoline);
                Excef.excef_set_fullscreen_callback(&FullscreenTrampoline);
                Excef.excef_set_browser_initialized_callback(&BrowserInitializedTrampoline);
                Excef.excef_set_scroll_offset_callback(&ScrollOffsetTrampoline);
                Excef.excef_set_auto_resize_callback(&AutoResizeTrampoline);
                Excef.excef_set_js_dialog_callback(&JsDialogTrampoline);
                Excef.excef_set_file_dialog_callback(&FileDialogTrampoline);
                Excef.excef_set_context_menu_callback(&ContextMenuTrampoline);
                Excef.excef_set_download_starting_callback(&DownloadStartingTrampoline);
                Excef.excef_set_download_progress_callback(&DownloadProgressTrampoline);
                Excef.excef_set_auth_request_callback_v2(&AuthRequestTrampoline);
                Excef.excef_set_find_result_callback(&FindResultTrampoline);
                Excef.excef_set_render_process_gone_callback(&RenderProcessGoneTrampoline);
                Excef.excef_set_scheme_request_callback(&SchemeRequestTrampoline);
                Excef.excef_set_resource_request_callback(&ResourceRequestTrampoline);
                Excef.excef_set_popup_show_callback(&PopupShowTrampoline);
                Excef.excef_set_popup_size_callback(&PopupSizeTrampoline);
                Excef.excef_set_popup_paint_callback(&PopupPaintTrampoline);
                Excef.excef_set_js_invoke_callback(&JsInvokeTrampoline);
                Excef.excef_set_accessibility_tree_callback(&AccessibilityTreeTrampoline);
                Excef.excef_set_accessibility_location_callback(&AccessibilityLocationTrampoline);
                Excef.excef_set_start_drag_callback(&StartDragTrampoline);
                Excef.excef_set_drag_image_callback(&DragImageTrampoline);
                Excef.excef_set_permission_prompt_callback(&PermissionPromptTrampoline);
                Excef.excef_set_media_access_callback(&MediaAccessTrampoline);
                Excef.excef_set_before_popup_callback(&BeforePopupTrampoline);
                Excef.excef_set_host_popup_callback(&HostPopupTrampoline);
                Excef.excef_set_cert_error_callback(&CertErrorTrampoline);
                Excef.excef_set_take_focus_callback(&TakeFocusTrampoline);
                Excef.excef_set_set_focus_callback(&SetFocusTrampoline);
                Excef.excef_set_got_focus_callback(&GotFocusTrampoline);
                Excef.excef_set_pre_key_callback(&PreKeyTrampoline);
                Excef.excef_set_key_event_callback(&KeyEventTrampoline);
                Excef.excef_set_devtools_message_callback(&DevToolsMessageTrampoline);
                Excef.excef_set_string_visitor_callback(&StringVisitorTrampoline);
                Excef.excef_set_nav_entry_callback(&NavEntryTrampoline);
                Excef.excef_set_frame_lifecycle_callback(&FrameLifecycleTrampoline);
                Excef.excef_set_main_frame_changed_callback(&MainFrameChangedTrampoline);
                Excef.excef_set_audio_stream_started_callback(&AudioStreamStartedTrampoline);
                Excef.excef_set_audio_stream_packet_callback(&AudioStreamPacketTrampoline);
                Excef.excef_set_audio_stream_stopped_callback(&AudioStreamStoppedTrampoline);
                Excef.excef_set_audio_stream_error_callback(&AudioStreamErrorTrampoline);
                Excef.excef_set_should_filter_response_callback(&ShouldFilterResponseTrampoline);
                Excef.excef_set_response_filter_callback(&ResponseFilterTrampoline);
                Excef.excef_set_response_filter_finalize_callback(&ResponseFilterFinalizeTrampoline);
                Excef.excef_set_chrome_command_callback(&ChromeCommandTrampoline);
                Excef.excef_set_app_menu_visible_callback(&AppMenuVisibleTrampoline);
                Excef.excef_set_app_menu_enabled_callback(&AppMenuEnabledTrampoline);
                Excef.excef_set_page_action_visible_callback(&PageActionVisibleTrampoline);
                Excef.excef_set_toolbar_button_visible_callback(&ToolbarButtonVisibleTrampoline);
                Excef.excef_set_should_handle_resource_callback(&ShouldHandleResourceTrampoline);
                Excef.excef_set_touch_handle_size_callback(&TouchHandleSizeTrampoline);
                Excef.excef_set_touch_handle_state_callback(&TouchHandleStateTrampoline);
                Excef.excef_set_ime_composition_range_callback(&ImeCompositionRangeTrampoline);
                Excef.excef_set_virtual_keyboard_callback(&VirtualKeyboardTrampoline);
                Excef.excef_set_accelerated_paint_callback(&AcceleratedPaintTrampoline);
            }
            s_eventsRegistered = true;
        }
    }


    // ---- Internals ------------------------------------------------------

    private static Action<long>? s_scheduleCallback;

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, CefBrowser> s_browsers = new();

    internal static int s_nextEvalRequestId;
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<string>> s_evalRequests = new();

    private static int s_nextCookieRequestId;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (List<CookieInfo> List, TaskCompletionSource<List<CookieInfo>> Tcs)> s_cookieRequests = new();
    private static int s_nextRequestContextOperationId;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<int>> s_requestContextOperations = new();

    internal static Task<int> DeleteCookiesAsyncInContext(
        int contextHandle,
        string? url,
        string? name)
    {
        unsafe
        {
            sbyte* urlPtr = url is null
                ? null
                : (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            sbyte* namePtr = name is null
                ? null
                : (sbyte*)Marshal.StringToCoTaskMemUTF8(name);
            try
            {
                return StartRequestContextOperation(requestId =>
                    Excef.excef_delete_cookies_in_context_async(
                        contextHandle,
                        urlPtr,
                        namePtr,
                        requestId));
            }
            finally
            {
                if (urlPtr is not null) Marshal.FreeCoTaskMem((IntPtr)urlPtr);
                if (namePtr is not null) Marshal.FreeCoTaskMem((IntPtr)namePtr);
            }
        }
    }

    internal static Task<int> FlushCookieStoreAsyncInContext(int contextHandle) =>
        StartRequestContextOperation(requestId =>
            Excef.excef_flush_cookie_store_async(contextHandle, requestId));

    internal static Task<int> SetPreferenceAsyncInContext(
        int contextHandle, string name, string valueJson, CancellationToken cancellationToken) =>
        StartRequestContextOperation(requestId =>
        {
            unsafe
            {
                var n = (sbyte*)Marshal.StringToCoTaskMemUTF8(name);
                var v = (sbyte*)Marshal.StringToCoTaskMemUTF8(valueJson);
                try { return Excef.excef_set_preference_async(contextHandle, n, v, requestId); }
                finally { Marshal.FreeCoTaskMem((IntPtr)n); Marshal.FreeCoTaskMem((IntPtr)v); }
            }
        }, cancellationToken);

    internal static Task<int> ClearHttpAuthCredentialsAsyncInContext(
        int contextHandle) =>
        StartRequestContextOperation(requestId =>
            Excef.excef_clear_http_auth_credentials_async(
                contextHandle,
                requestId));

    internal static Task<int> CloseAllConnectionsAsyncInContext(
        int contextHandle) =>
        StartRequestContextOperation(requestId =>
            Excef.excef_close_all_connections_async(contextHandle, requestId));

    private static Task<int> StartRequestContextOperation(
        Func<int, int> schedule, CancellationToken cancellationToken = default)
    {
        var requestId = Interlocked.Increment(ref s_nextRequestContextOperationId);
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        s_requestContextOperations[requestId] = completion;
        if (schedule(requestId) == 0)
        {
            s_requestContextOperations.TryRemove(requestId, out _);
            completion.TrySetException(new InvalidOperationException(
                "The CEF request-context operation could not be scheduled."));
        }

        return cancellationToken.CanBeCanceled
            ? AwaitRequestContextOperationAsync(requestId, completion.Task, cancellationToken)
            : completion.Task;
    }

    private static async Task<int> AwaitRequestContextOperationAsync(
        int requestId, Task<int> completion, CancellationToken cancellationToken)
    {
        try { return await completion.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { s_requestContextOperations.TryRemove(requestId, out _); }
    }

    // ---- argv / utf-8 helpers ------------------------------------------

    private static string[] NormalizeCommandLineArguments(string[]? args)
        => NormalizeCommandLineArguments(args, Environment.GetCommandLineArgs());

    // .NET Main(string[] args) deliberately omits argv[0], while CEF's Linux
    // CefMainArgs contract requires it. Accept either the Main shape or the
    // full Environment.GetCommandLineArgs shape and always produce a full
    // native argv. Kept internal and pure for focused regression tests.
    internal static string[] NormalizeCommandLineArguments(
        string[]? args,
        string[] processArguments)
    {
        ArgumentNullException.ThrowIfNull(processArguments);

        string? program = processArguments.Length > 0
            ? processArguments[0]
            : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(program))
        {
            throw new InvalidOperationException(
                "Unable to determine argv[0] for CEF process initialization.");
        }

        if (args is null)
        {
            return processArguments.Length > 0
                ? (string[])processArguments.Clone()
                : [program];
        }

        if (args.Length > 0 && IsSameProgramArgument(args[0], program))
        {
            return (string[])args.Clone();
        }

        return [program, .. args];
    }

    private static bool IsSameProgramArgument(string candidate, string program)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(candidate, program, comparison)) return true;

        try
        {
            return string.Equals(
                Path.GetFullPath(candidate),
                Path.GetFullPath(program),
                comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static unsafe sbyte** AllocArgv(string[] args, out int argc)
    {
        argc = args.Length;
        nuint size = (nuint)(IntPtr.Size * (argc + 1));
        sbyte** argv = (sbyte**)NativeMemory.AllocZeroed(size);
        for (int i = 0; i < argc; i++)
        {
            argv[i] = (sbyte*)Marshal.StringToCoTaskMemUTF8(args[i]);
        }
        return argv;
    }

    private static unsafe void FreeArgv(sbyte** argv, int argc)
    {
        if (argv == null) return;
        for (int i = 0; i < argc; i++)
        {
            if (argv[i] is not null) Marshal.FreeCoTaskMem((IntPtr)argv[i]);
        }
        NativeMemory.Free(argv);
    }

    private static unsafe sbyte* MarshalUtf8(string? s)
        => s is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(s);

    private static unsafe void FreeUtf8(sbyte* p)
    {
        if (p is not null) Marshal.FreeCoTaskMem((IntPtr)p);
    }

    private static unsafe string ReadCString(sbyte* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != 0) len++;
        return Encoding.UTF8.GetString((byte*)ptr, len);
    }
}
