using System.Text.Json;
using Exclr8Cef;

namespace GhostShell.Browser;

/// <summary>
/// Owns one isolated CEF request context configured for an SSH-backed SOCKS
/// route. Chromium normally bypasses proxies for loopback names; removing that
/// implicit rule is what makes http://localhost refer to the SSH host.
/// </summary>
internal sealed class CefBrowserNetworkContext : IDisposable
{
    private readonly CefRequestContext _context;
    private readonly CefBrowserContentPolicy _contentPolicy;

    private CefBrowserNetworkContext(
        CefRequestContext context,
        CefBrowserContentPolicy contentPolicy)
    {
        _context = context;
        _contentPolicy = contentPolicy;
    }

    public static CefBrowserNetworkContext CreateIsolatedHtmlPreview()
    {
        return CreateConfigured(
            HtmlPreviewPreferences(),
            CefBrowserContentPolicy.RestrictedLocalPreview,
            "The embedded browser could not create a restricted HTML-preview context.");
    }

    public static CefBrowserNetworkContext Create(int socksProxyPort)
        => Create(SocksProxyEndpoint(socksProxyPort));

    public static CefBrowserNetworkContext Create(Uri proxyEndpoint)
    {
        return CreateConfigured(
            RequiredPreferences(proxyEndpoint),
            CefBrowserContentPolicy.Ordinary,
            "The embedded browser could not create an isolated network context.");
    }

    public static CefBrowserNetworkContext CreateIsolatedAgentWeb()
    {
        return CreateConfigured(
            AgentWebPreferences(),
            CefBrowserContentPolicy.Ordinary,
            "The embedded browser could not create an isolated agent web context.");
    }

    public static CefBrowserNetworkContext CreateIsolatedAgentWeb(int socksProxyPort)
        => CreateIsolatedAgentWeb(
            SocksProxyEndpoint(socksProxyPort));

    public static CefBrowserNetworkContext CreateIsolatedAgentWeb(Uri proxyEndpoint)
    {
        return CreateConfigured(
            AgentWebPreferences(proxyEndpoint),
            CefBrowserContentPolicy.Ordinary,
            "The embedded browser could not create an isolated agent web context.");
    }

    public CefBrowserView CreateView() => CreateView(proxyAuthenticationResolver: null);

    public CefBrowserView CreateView(
        IWorkspaceProxyAuthenticationResolver? proxyAuthenticationResolver) =>
        new(
            _context,
            _contentPolicy,
            proxyAuthenticationResolver: proxyAuthenticationResolver);

    public void Dispose() => _context.Dispose();

    internal static IReadOnlyDictionary<string, string> HtmlPreviewPreferences() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Context-local content settings keep JavaScript disabled across
            // replacement renderers without changing ordinary browser panels.
            ["profile.default_content_setting_values.javascript"] = "2",
            ["profile.default_content_setting_values.popups"] = "2",
        };

    internal static IReadOnlyDictionary<string, string> AgentWebPreferences() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["proxy"] = """{"mode":"direct"}""",
            ["profile.default_content_setting_values.popups"] = "2",
            ["webrtc.ip_handling_policy"] = "\"disable_non_proxied_udp\"",
        };

    internal static IReadOnlyDictionary<string, string> AgentWebPreferences(
        int socksProxyPort)
        => AgentWebPreferences(
            SocksProxyEndpoint(socksProxyPort));

    internal static IReadOnlyDictionary<string, string> AgentWebPreferences(
        Uri proxyEndpoint)
    {
        var preferences = new Dictionary<string, string>(
            RequiredPreferences(proxyEndpoint),
            StringComparer.Ordinal)
        {
            ["profile.default_content_setting_values.popups"] = "2",
        };
        return preferences;
    }

    private static CefBrowserNetworkContext CreateConfigured(
        IReadOnlyDictionary<string, string> preferences,
        CefBrowserContentPolicy contentPolicy,
        string creationFailure)
    {
        var context = Cef.CreateRequestContext()
            ?? throw new InvalidOperationException(creationFailure);
        try
        {
            foreach (var preference in preferences)
            {
                SetRequiredPreferenceJson(context, preference.Key, preference.Value);
            }

            return new CefBrowserNetworkContext(context, contentPolicy);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the JSON values accepted by CefRequestContext.SetPreference.
    /// Chromium registers proxy configuration as one dictionary-valued
    /// preference named "proxy"; proxy.mode and similar dotted names are not
    /// independently registered preferences.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RequiredPreferences(
        int socksProxyPort)
        => RequiredPreferences(
            SocksProxyEndpoint(socksProxyPort));

    internal static IReadOnlyDictionary<string, string> RequiredPreferences(
        Uri proxyEndpoint)
    {
        ArgumentNullException.ThrowIfNull(proxyEndpoint);
        if (!proxyEndpoint.IsAbsoluteUri
            || proxyEndpoint.Port is < 1 or > 65_535
            || proxyEndpoint.Scheme is not ("socks5" or "http"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(proxyEndpoint),
                "A browser proxy must be an absolute HTTP or SOCKS5 endpoint.");
        }

        var proxy = $"{proxyEndpoint.Scheme}://{proxyEndpoint.Host}:{proxyEndpoint.Port}";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["proxy"] = $$"""{"mode":"fixed_servers","server":"{{proxy}}","bypass_list":"<-loopback>"}""",
            ["webrtc.ip_handling_policy"] = "\"disable_non_proxied_udp\"",
        };
    }

    private static Uri SocksProxyEndpoint(int port)
    {
        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                "A SOCKS proxy port must be between 1 and 65535.");
        }

        return new Uri($"socks5://127.0.0.1:{port}", UriKind.Absolute);
    }

    private static void SetRequiredPreferenceJson(
        CefRequestContext context,
        string name,
        string valueJson)
    {
        if (!context.SetPreference(name, valueJson))
        {
            throw new InvalidOperationException(
                $"The embedded browser rejected the required '{name}' network setting.");
        }
    }
}
