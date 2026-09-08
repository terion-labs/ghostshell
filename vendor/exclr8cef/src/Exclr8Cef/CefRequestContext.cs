using System.Runtime.InteropServices;
using Exclr8Cef.Native;

namespace Exclr8Cef;

/// <summary>
/// Isolated request context — its own cookie jar, cache, and per-origin
/// storage. Browsers created in different request contexts can't see each
/// other's cookies / localStorage / IndexedDB / cache; useful for
/// multi-profile, "container tab" / incognito-like flows.
///
/// Created via <see cref="Cef.CreateRequestContext"/>. Disposing drops
/// the shim's reference; CEF keeps the underlying context alive while
/// any browser is using it, then tears it down once the last browser
/// closes.
///
/// In-memory (incognito) contexts vanish entirely when the last browser
/// closes; on-disk contexts persist to <c>CachePath</c> across runs.
/// </summary>
public sealed class CefRequestContext : IDisposable
{
    // Disposed sentinel. Handle 0 is the GLOBAL context — using 0 as the
    // disposed marker would make every post-dispose call on an isolated
    // profile silently read/write the global profile instead.
    private const int Disposed = -1;

    private int _handle;
    private readonly CancellationTokenSource _closed = new();

    internal int Handle
    {
        get
        {
            int h = _handle;
            ObjectDisposedException.ThrowIf(h == Disposed, this);
            return h;
        }
    }
    public bool IsClosed => _handle == Disposed;

    internal CefRequestContext(int handle) { _handle = handle; }

    /// <summary>
    /// Drop the shim's reference to this context. Outstanding browsers
    /// using it keep it alive until they close. Idempotent. Further calls
    /// on this instance throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        // Don't release handle 0 — that's the global context, owned by CEF
        // itself (and Global stays usable; only explicit user contexts
        // transition to the disposed state).
        if (ReferenceEquals(this, Global)) return;
        int h = System.Threading.Interlocked.Exchange(ref _handle, Disposed);
        if (h == Disposed) return;
        _closed.Cancel();
        if (h > 0) Excef.excef_release_request_context(h);
    }

    /// <summary>
    /// Set a Chromium preference by dotted name with a JSON value.
    /// Examples: <c>proxy.mode</c> ("direct" | "auto_detect" | "fixed_servers"),
    /// <c>intl.accept_languages</c>, <c>webrtc.ip_handling_policy</c>,
    /// <c>spellcheck.languages</c>. Returns true if accepted.
    /// </summary>
    public bool SetPreference(string name, string valueJson)
    {
        ArgumentNullException.ThrowIfNull(name);
        unsafe
        {
            sbyte* n = (sbyte*)Marshal.StringToCoTaskMemUTF8(name);
            sbyte* v = valueJson is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(valueJson);
            try { return Excef.excef_set_preference(Handle, n, v) != 0; }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)n);
                if (v != null) Marshal.FreeCoTaskMem((IntPtr)v);
            }
        }
    }

    /// <summary>Waits for context initialization and actual preference acceptance.</summary>
    public async Task<bool> SetPreferenceAsync(string name, string valueJson)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(valueJson);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return await Cef.SetPreferenceAsyncInContext(Handle, name, valueJson, timeout.Token)
            .ConfigureAwait(false) == 1;
    }

    /// <summary>Read a preference as JSON, or null if unset.</summary>
    public string? GetPreference(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        unsafe
        {
            sbyte* n = (sbyte*)Marshal.StringToCoTaskMemUTF8(name);
            try
            {
                sbyte* p = Excef.excef_get_preference(Handle, n);
                if (p == null) return null;
                string s = Marshal.PtrToStringUTF8((IntPtr)p) ?? "";
                Excef.excef_free_string(p);
                return s;
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)n); }
        }
    }

    /// <summary>Drop cached HTTP-Basic / NTLM / Digest credentials.</summary>
    public void ClearHttpAuthCredentials() => Excef.excef_clear_http_auth_credentials(Handle);

    public Task ClearHttpAuthCredentialsAsync() =>
        Cef.ClearHttpAuthCredentialsAsyncInContext(Handle);

    /// <summary>Tear down all live TCP connections (useful on logout).</summary>
    public void CloseAllConnections() => Excef.excef_close_all_connections(Handle);

    public Task CloseAllConnectionsAsync() =>
        Cef.CloseAllConnectionsAsyncInContext(Handle);

    // ---- Cookies (per-context) ----------------------------------------

    /// <summary>
    /// Read cookies from this context's cookie jar. Empty/null url returns
    /// all cookies.
    /// </summary>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread.
    /// Marshal back to the UI thread before any follow-up
    /// <see cref="CefBrowser"/> call.
    /// </remarks>
    public Task<List<Cef.CookieInfo>> GetCookiesAsync(string? url = null)
        => Cef.GetCookiesAsyncInContext(Handle, url);

    /// <summary>Set a cookie in this context's cookie jar.</summary>
    public bool SetCookie(string url, string name, string value,
                            string? domain = null, string? path = null,
                            bool secure = false, bool httpOnly = false)
        => Cef.SetCookieInContext(Handle, url, name, value, domain, path, secure, httpOnly);

    /// <summary>
    /// Delete cookies in this context's cookie jar matching url + name
    /// (either or both may be empty).
    /// </summary>
    public void DeleteCookies(string? url = null, string? name = null)
        => Cef.DeleteCookiesInContext(Handle, url, name);

    public Task<int> DeleteCookiesAsync(string? url = null, string? name = null) =>
        Cef.DeleteCookiesAsyncInContext(Handle, url, name);

    public Task FlushCookieStoreAsync() =>
        Cef.FlushCookieStoreAsyncInContext(Handle);

    /// <summary>
    /// Process-wide / global request context — used by browsers created
    /// without a specific context.
    /// </summary>
    public static CefRequestContext Global { get; } = new CefRequestContext(0);
}
