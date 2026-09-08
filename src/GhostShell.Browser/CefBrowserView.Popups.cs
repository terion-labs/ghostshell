using Avalonia;
using Avalonia.Layout;
using Avalonia.Threading;
using Exclr8Cef;
using GhostShell.Application;

namespace GhostShell.Browser;

internal sealed partial class CefBrowserView
{
    private readonly List<HostedPopup> _hostedPopups = [];
    private CefBrowserView? _popupOpener;

    private Func<BrowserAddress, CancellationToken, ValueTask<bool>>? ReadResourceRequestPolicy() =>
        _activeNavigation?.ReadRequestPolicy()
        ?? Volatile.Read(ref _resourceRequestPolicy)
        ?? _popupOpener?.ReadResourceRequestPolicy();

    private void OnHostPopup(object? sender, HostPopupEventArgs args)
    {
        if (!CanHostPopup(_disposed, Dispatcher.UIThread.CheckAccess(),
                _contentPolicy, args.UserGesture, args.TargetUrl))
        {
            return;
        }

        var child = new CefBrowserView(
            contentPolicy: _contentPolicy,
            profile: _profile,
            authenticationResolver: _authenticationResolver,
            proxyAuthenticationResolver: _proxyAuthenticationResolver)
        {
            _popupOpener = this,
        };
        var chrome = new HostedBrowserPopupView(child.View)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
        };
        var popup = new HostedPopup(child, chrome, args.Browser)
        {
            AddressChanged = (_, url) => RunOnUiThread(() => chrome.SetAddress(url)),
        };
        popup.Closed = (_, _) => RunOnUiThread(() => RemoveHostedPopup(popup));
        popup.CloseRequested = (_, _) => RemoveHostedPopup(popup);
        args.Browser.AddressChanged += popup.AddressChanged;
        args.Browser.Closed += popup.Closed;
        chrome.CloseRequested += popup.CloseRequested;
        try
        {
            // CEF inherits the opener's native request context. Adopt the
            // reserved instance, never create/navigate a lookalike browser.
            child._webView.AdoptOffscreenBrowser(args.Browser);
            chrome.SetAddress(args.TargetUrl);
            _hostedPopups.Add(popup);
            ResizeHostedPopups();
            _view.Children.Add(chrome);
            args.IsHosted = true;
        }
        catch
        {
            RemoveHostedPopup(popup);
            throw;
        }
    }

    internal static bool CanHostPopup(bool disposed, bool hasUiAccess,
        CefBrowserContentPolicy policy, bool userGesture, string targetUrl) =>
        !disposed && hasUiAccess && userGesture
        && policy is CefBrowserContentPolicy.Ordinary
        && (string.IsNullOrEmpty(targetUrl)
            || GhostShell.Application.BrowserAddress.TryParse(targetUrl, out _));

    private void ResizeHostedPopups()
    {
        foreach (var popup in _hostedPopups)
        {
            // Keep the parent interactive: this is an owned transient surface,
            // not a modal veil or a restored workspace tab.
            popup.Chrome.Width = Math.Max(1, Math.Min(720, _view.Bounds.Width * 0.72));
            popup.Chrome.Height = Math.Max(1, Math.Min(560, _view.Bounds.Height * 0.78));
        }
    }

    private void RemoveHostedPopup(HostedPopup popup)
    {
        _hostedPopups.Remove(popup);
        popup.Native.AddressChanged -= popup.AddressChanged;
        popup.Native.Closed -= popup.Closed;
        popup.Chrome.CloseRequested -= popup.CloseRequested;
        _view.Children.Remove(popup.Chrome);
        popup.Browser.Dispose();
    }

    private void CloseHostedPopups()
    {
        foreach (var popup in _hostedPopups.ToArray())
        {
            RemoveHostedPopup(popup);
        }
    }

    private sealed class HostedPopup(CefBrowserView browser, HostedBrowserPopupView chrome, CefBrowser native)
    {
        public CefBrowserView Browser { get; } = browser;
        public HostedBrowserPopupView Chrome { get; } = chrome;
        public CefBrowser Native { get; } = native;
        public EventHandler<string>? AddressChanged { get; set; }
        public EventHandler? Closed { get; set; }
        public EventHandler? CloseRequested { get; set; }
    }
}
