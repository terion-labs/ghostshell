using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Exclr8Cef;
using GhostShell.Application;
using CefWebView = Exclr8Cef.WebView.WebView;

namespace GhostShell.Browser;

/// <summary>
/// Adapts one CEF off-screen browser to GhostSHELL's engine-neutral browser
/// contract. The Exclr8 control owns rendering and input; this type owns the
/// stricter navigation, permission, and lifetime policy around it.
/// </summary>
internal sealed partial class CefBrowserView : IEmbeddedBrowserView
{
    private const string LoadStringHost = "loadstring.exclr8cef.internal";
    private const int HeadlessViewportWidth = 1280;
    private const int HeadlessViewportHeight = 720;
    private static readonly TimeSpan DestinationResolutionDeadline =
        TimeSpan.FromSeconds(5);

    private readonly CefWebView _webView;
    private readonly CefBrowserContentPolicy _contentPolicy;
    private readonly BrowserProfileBinding? _profile;
    private readonly IBrowserProfileAuthenticationResolver? _authenticationResolver;
    private readonly IWorkspaceProxyAuthenticationResolver? _proxyAuthenticationResolver;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Grid _view;
    private readonly CefAgentCursorOverlay _agentCursorOverlay;
    private readonly TaskCompletionSource<bool> _rendererReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private CefBrowser? _browser;
    private CefBrowserSemanticAdapter? _semanticAdapter;
    private CefBrowserAutomationAdapter? _automationAdapter;
    private CefHumanizedInput? _humanizedInput;
    private CefBrowserNetworkActivityTracker? _networkActivity;
    private CefBrowserDomActivityTracker? _domActivity;
    private volatile ActiveNativeNavigation? _activeNavigation;
    private Func<BrowserAddress, CancellationToken, ValueTask<bool>>?
        _resourceRequestPolicy;
    private BrowserAddress? _queuedAddress;
    private CefLocalDocumentAccessPolicy _localDocumentAccess =
        CefLocalDocumentAccessPolicy.None;
    private long _lastNavigationGeneration;
    private bool _ignoreInitialBlank = true;
    private bool _isAgentActive;
    private bool _disposed;

    public CefBrowserView(
        CefRequestContext? requestContext = null,
        CefBrowserContentPolicy contentPolicy = CefBrowserContentPolicy.Ordinary,
        BrowserProfileBinding? profile = null,
        IBrowserProfileAuthenticationResolver? authenticationResolver = null,
        IWorkspaceProxyAuthenticationResolver? proxyAuthenticationResolver = null)
    {
        _contentPolicy = contentPolicy;
        _profile = profile;
        _authenticationResolver = authenticationResolver;
        _proxyAuthenticationResolver = proxyAuthenticationResolver;
        _webView = new CefWebView
        {
            Url = BrowserAddress.Blank.Value.AbsoluteUri,
            RequestContext = requestContext,
        };
        _agentCursorOverlay = new CefAgentCursorOverlay();
        _view = new Grid();
        _view.Children.Add(_webView);
        _view.Children.Add(_agentCursorOverlay);
        _webView.BrowserReady += OnBrowserReady;
    }

    public Control View => _view;

    public bool CanGoBack => _browser?.CanGoBack ?? false;

    public bool CanGoForward => _browser?.CanGoForward ?? false;

    // Exclr8Cef exposes URL admission but neither the connected socket peer nor
    // a request-scoped proxy. Governed network mutations must therefore stop
    // before native dispatch instead of treating DNS preflight as peer binding.
    internal static bool HasPeerBoundTransport => false;

    public bool SupportsPeerBoundTransport => HasPeerBoundTransport;

    public void SetAgentActivity(bool isActive)
    {
        if (_disposed)
        {
            return;
        }

        _isAgentActive = isActive;
        _agentCursorOverlay.SetAgentActivity(isActive);
        if (isActive && _humanizedInput is { } humanizedInput)
        {
            _ = EnsureAgentCursorAsync(humanizedInput);
        }
    }

    public void SetActiveNavigationRequestPolicy(
        Func<BrowserAddress, CancellationToken, ValueTask<bool>> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ThrowIfDisposed();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException(
                "The active browser navigation policy must be set on the UI thread.");
        }

        var navigation = _activeNavigation
            ?? throw new InvalidOperationException(
                "The browser has no active navigation to protect.");
        navigation.SetRequestPolicy(policy);
    }

    public void SetResourceRequestPolicy(
        Func<BrowserAddress, CancellationToken, ValueTask<bool>> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ThrowIfDisposed();
        Volatile.Write(ref _resourceRequestPolicy, policy);
    }

    public event EventHandler<NativeBrowserNavigationEventArgs>? NavigationStarted;

    public event EventHandler<NativeBrowserNavigationCompletedEventArgs>?
        NavigationCompleted;

    public event EventHandler<NativeBrowserAddressChangedEventArgs>?
        AddressChanged;

    public event EventHandler<NativeBrowserNavigationRejectedEventArgs>?
        NavigationRejected;

    public event EventHandler? RenderProcessFailed;

    public event EventHandler<BrowserNewTabRequestedEventArgs>? NewTabRequested;

    public event EventHandler<BrowserProductEvent>? ProductEvent;

    public void Navigate(BrowserAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ThrowIfDisposed();
        if (address != BrowserAddress.Blank
            && !TryStartHeadlessRenderer())
        {
            throw new InvalidOperationException(
                "The CEF renderer could not be started.");
        }

        var navigation = BeginExplicitNavigation(address);
        _queuedAddress = address;
        if (_ignoreInitialBlank
            || _browser is not { IsInitialized: true } browser)
        {
            return;
        }

        DispatchQueuedNavigation(browser, navigation);
    }

    public bool GoBack() => InvokeHistoryNavigation(
        static browser => browser.CanGoBack,
        static browser => browser.GoBack());

    public bool GoForward() => InvokeHistoryNavigation(
        static browser => browser.CanGoForward,
        static browser => browser.GoForward());

    public bool Reload()
    {
        ThrowIfDisposed();
        if (_ignoreInitialBlank
            || _browser is not { IsInitialized: true } browser)
        {
            return false;
        }

        var navigation = BeginExplicitNavigation(pendingAddress: null);
        try
        {
            _ignoreInitialBlank = false;
            browser.Reload();
            return true;
        }
        catch
        {
            ClearUnstartedNavigation(navigation);
            throw;
        }
    }

    public bool Stop()
    {
        ThrowIfDisposed();
        if (_browser is not { IsInitialized: true } browser)
        {
            return false;
        }

        if (_activeNavigation is { } navigation)
        {
            navigation.StopRequested = true;
            _queuedAddress = null;
            RollBackLocalDocumentAccess();
        }

        browser.StopLoad();
        return true;
    }

    public bool OpenDeveloperTools()
    {
        ThrowIfDisposed();
        if (_browser is not { IsInitialized: true } browser)
        {
            return false;
        }

        browser.ShowDevTools();
        return true;
    }

    public async Task<NativeBrowserSnapshotResult> CaptureSnapshotAsync(
        BrowserSnapshotQuery? query = null)
    {
        if (!await EnsureRendererReadyAsync().ConfigureAwait(false))
        {
            return NativeBrowserSnapshotResult.Unavailable();
        }

        return await (_semanticAdapter?.CaptureSnapshotAsync(
                query ?? BrowserSnapshotQuery.Lean)
            ?? Task.FromResult(NativeBrowserSnapshotResult.Unavailable()))
            .ConfigureAwait(false);
    }

    public async Task<NativeBrowserClickResult> ClickAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!await EnsureRendererReadyAsync().ConfigureAwait(false))
        {
            return NativeBrowserClickResult.Stale();
        }

        return await (_semanticAdapter?.ClickAsync(handle)
            ?? Task.FromResult(NativeBrowserClickResult.Stale()))
            .ConfigureAwait(false);
    }

    public async Task<NativeBrowserFillResult> FillAsync(
        NativeBrowserElementHandle handle,
        string text)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(text);
        if (!await EnsureRendererReadyAsync().ConfigureAwait(false))
        {
            return NativeBrowserFillResult.Stale();
        }

        return await (_semanticAdapter?.FillAsync(handle, text)
            ?? Task.FromResult(NativeBrowserFillResult.Stale()))
            .ConfigureAwait(false);
    }

    public async Task<NativeBrowserCheckResult> CheckAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!await EnsureRendererReadyAsync().ConfigureAwait(false))
        {
            return NativeBrowserCheckResult.Stale();
        }

        return await (_semanticAdapter?.CheckAsync(handle)
            ?? Task.FromResult(NativeBrowserCheckResult.Stale()))
            .ConfigureAwait(false);
    }

    public async Task<NativeBrowserElementStateResult> ReadElementStateAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!await EnsureRendererReadyAsync().ConfigureAwait(false))
        {
            return NativeBrowserElementStateResult.Stale();
        }

        return await (_semanticAdapter?.ReadElementStateAsync(handle)
            ?? Task.FromResult(NativeBrowserElementStateResult.Stale()))
            .ConfigureAwait(false);
    }

    public void BeginNetworkActivityObservation() =>
        _networkActivity?.BeginObservation();

    public void EndNetworkActivityObservation() =>
        _networkActivity?.EndObservation();

    public NativeBrowserNetworkActivity ReadNetworkActivity() =>
        _networkActivity?.Snapshot()
        ?? new NativeBrowserNetworkActivity(
            IsObservable: false,
            ActiveRequestCount: 0,
            QuietFor: TimeSpan.Zero);

    internal async Task<bool> BeginDomObservationWhenReadyAsync()
    {
        if (!await EnsureRendererReadyAsync().ConfigureAwait(false)
            || _domActivity is null)
        {
            return false;
        }

        return await _domActivity.BeginObservationAsync().ConfigureAwait(false);
    }

    internal long MarkDomActivity() =>
        _domActivity?.MarkActivity()
        ?? throw new InvalidOperationException("CEF DOM observation is unavailable.");

    internal Task<long> WaitForDomQuietAsync(
        TimeSpan quietWindow,
        CancellationToken cancellationToken) =>
        _domActivity?.WaitForQuietAsync(quietWindow, cancellationToken)
        ?? Task.FromException<long>(
            new InvalidOperationException("CEF DOM observation is unavailable."));

    internal Task<long> WaitForDomActivityAfterAsync(
        long generation,
        CancellationToken cancellationToken) =>
        _domActivity?.WaitForActivityAfterAsync(generation, cancellationToken)
        ?? Task.FromException<long>(
            new InvalidOperationException("CEF DOM observation is unavailable."));

    internal void EndDomObservation() =>
        _domActivity?.EndObservation();

    public async Task<NativeBrowserViewport> ReadViewportAsync()
    {
        if (!await EnsureRendererReadyAsync().ConfigureAwait(false)
            || _automationAdapter is not { } adapter)
        {
            throw new InvalidOperationException("The CEF renderer is not ready.");
        }

        return await adapter.ReadViewportAsync().ConfigureAwait(false);
    }

    public async Task<NativeBrowserAutomationResult> DispatchMouseAsync(
        BrowserMouseRequest request) =>
        await DispatchAutomationAsync(
            adapter => adapter.DispatchMouseAsync(request));

    public async Task<NativeBrowserAutomationResult> DispatchKeyAsync(
        BrowserKeyRequest request) =>
        await DispatchAutomationAsync(
            adapter => adapter.DispatchKeyAsync(request));

    public async Task<NativeBrowserAutomationResult> DispatchScrollAsync(
        BrowserScrollRequest request) =>
        await DispatchAutomationAsync(
            adapter => adapter.DispatchScrollAsync(request));

    public async Task<NativeBrowserAutomationResult> EvaluateAsync(
        BrowserEvaluateRequest request) =>
        await DispatchAutomationAsync(
            adapter => adapter.EvaluateAsync(request));

    public async Task<NativeBrowserAutomationResult>
        ExtractWebSearchDocumentAsync(int maximumResults) =>
        await DispatchAutomationAsync(
            adapter => adapter.ExtractWebSearchDocumentAsync(maximumResults));

    public async Task<NativeBrowserAutomationResult> ExtractReadableArticleAsync() =>
        await DispatchAutomationAsync(
            static adapter => adapter.ExtractReadableArticleAsync());

    public async Task<NativeBrowserAutomationResult> ExtractRenderedDocumentAsync() =>
        await DispatchAutomationAsync(
            static adapter => adapter.ExtractRenderedDocumentAsync());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _queuedAddress = null;
        _activeNavigation = null;
        Volatile.Write(ref _resourceRequestPolicy, null);
        Volatile.Write(
            ref _localDocumentAccess,
            CefLocalDocumentAccessPolicy.None);
        _webView.BrowserReady -= OnBrowserReady;
        if (_browser is { } browser)
        {
            Unsubscribe(browser);
            _browser = null;
        }

        _semanticAdapter?.InvalidateDocument();
        _semanticAdapter = null;
        _automationAdapter = null;
        _humanizedInput?.Dispose();
        _humanizedInput = null;
        _agentCursorOverlay.SetAgentActivity(isActive: false);
        _networkActivity?.Dispose();
        _networkActivity = null;
        _domActivity?.Dispose();
        _domActivity = null;
        _rendererReady.TrySetResult(false);

        _webView.Dispose();
    }

    private void OnBrowserReady(object? sender, EventArgs args)
    {
        if (_disposed || _webView.Browser is not { } browser)
        {
            return;
        }

        if (!ReferenceEquals(_browser, browser))
        {
            if (_browser is { } previous)
            {
                Unsubscribe(previous);
            }

            _browser = browser;
            _semanticAdapter?.InvalidateDocument();
            _networkActivity?.Dispose();
            _domActivity?.Dispose();
            _humanizedInput?.Dispose();
            var transport = new CefDevToolsTransport(browser);
            var humanizedInput = new CefHumanizedInput(
                transport,
                cursorActivity: _agentCursorOverlay.ShowAt);
            _humanizedInput = humanizedInput;
            _semanticAdapter = new CefBrowserSemanticAdapter(
                new CefSemanticBrowser(browser, humanizedInput));
            _automationAdapter = new CefBrowserAutomationAdapter(
                transport,
                humanizedInput);
            _networkActivity = new CefBrowserNetworkActivityTracker(browser);
            _domActivity = new CefBrowserDomActivityTracker(browser);
            Subscribe(browser);
            if (_isAgentActive)
            {
                _ = EnsureAgentCursorAsync(humanizedInput);
            }
        }

        _rendererReady.TrySetResult(true);

        // CEF reports BrowserReady while its initial about:blank load is still
        // in flight. Dispatching here can silently lose LoadUrl/LoadString.
        // OnLoadEnd owns the handoff after that bootstrap navigation settles.
    }

    private async Task EnsureAgentCursorAsync(CefHumanizedInput humanizedInput)
    {
        try
        {
            await humanizedInput.EnsureCursorVisibleAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (!_disposed)
        {
            SecretSafeDiagnosticProjection.WriteTrace(
                "browser.agent-cursor.initialize-failed",
                exception);
        }
    }

    private async Task<NativeBrowserAutomationResult> DispatchAutomationAsync(
        Func<CefBrowserAutomationAdapter, Task<NativeBrowserAutomationResult>> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (!await EnsureRendererReadyAsync().ConfigureAwait(false)
            || _automationAdapter is not { } adapter)
        {
            return NativeBrowserAutomationResult.Rejected("renderer_unavailable");
        }

        return await dispatch(adapter).ConfigureAwait(false);
    }

    private async Task<bool> EnsureRendererReadyAsync()
    {
        if (_disposed)
        {
            return false;
        }

        bool started;
        if (Dispatcher.UIThread.CheckAccess())
        {
            started = TryStartHeadlessRenderer();
        }
        else
        {
            started = await Dispatcher.UIThread.InvokeAsync(
                TryStartHeadlessRenderer);
        }

        return started
            && await _rendererReady.Task.ConfigureAwait(false);
    }

    private bool TryStartHeadlessRenderer()
    {
        if (_disposed)
        {
            return false;
        }

        return _webView.Browser is not null
            || _webView.EnsureOffscreenBrowserCreated(
                HeadlessViewportWidth,
                HeadlessViewportHeight);
    }

    private void Subscribe(CefBrowser browser)
    {
        browser.ResourceRequest += OnResourceRequest;
        browser.BeforeBrowse += OnBeforeBrowse;
        browser.AddressChanged += OnAddressChanged;
        browser.LoadingStateChanged += OnLoadingStateChanged;
        browser.LoadStart += OnLoadStart;
        browser.LoadEnd += OnLoadEnd;
        browser.LoadError += OnLoadError;
        browser.RenderProcessGone += OnRenderProcessGone;
        browser.ConsoleMessage += CefConsoleMessagePolicy.Handle;

        // CEF has no host UI for these prompts in OSR mode. Every privileged
        // or filesystem-affecting operation therefore defaults closed until a
        // future typed product contract owns the corresponding user decision.
        browser.BeforePopup += OnBeforePopup;
        browser.JsDialog += BlockJavaScriptDialog;
        browser.FileDialog += BlockFileDialog;
        browser.DownloadStarting += OnDownloadStarting;
        browser.DownloadProgress += OnDownloadProgress;
        browser.FindResult += OnFindResult;
        browser.AuthRequest += OnAuthenticationRequested;
        browser.PermissionRequest += BlockPermission;
        browser.MediaAccessRequest += BlockMediaAccess;
        browser.CertError += BlockCertificateError;
    }

    private void Unsubscribe(CefBrowser browser)
    {
        browser.ResourceRequest -= OnResourceRequest;
        browser.BeforeBrowse -= OnBeforeBrowse;
        browser.AddressChanged -= OnAddressChanged;
        browser.LoadingStateChanged -= OnLoadingStateChanged;
        browser.LoadStart -= OnLoadStart;
        browser.LoadEnd -= OnLoadEnd;
        browser.LoadError -= OnLoadError;
        browser.RenderProcessGone -= OnRenderProcessGone;
        browser.ConsoleMessage -= CefConsoleMessagePolicy.Handle;
        browser.BeforePopup -= OnBeforePopup;
        browser.JsDialog -= BlockJavaScriptDialog;
        browser.FileDialog -= BlockFileDialog;
        browser.DownloadStarting -= OnDownloadStarting;
        browser.DownloadProgress -= OnDownloadProgress;
        browser.FindResult -= OnFindResult;
        browser.AuthRequest -= OnAuthenticationRequested;
        browser.PermissionRequest -= BlockPermission;
        browser.MediaAccessRequest -= BlockMediaAccess;
        browser.CertError -= BlockCertificateError;
    }

    private void DispatchQueuedNavigation(
        CefBrowser browser,
        ActiveNativeNavigation navigation)
    {
        var address = _queuedAddress;
        if (_ignoreInitialBlank
            || address is null
            || !navigation.MayDispatchQueuedNavigation
            || !ReferenceEquals(navigation, _activeNavigation))
        {
            return;
        }

        _queuedAddress = null;
        try
        {
            if (address.Document is { } document)
            {
                browser.LoadString(document);
            }
            else
            {
                browser.LoadUrl(address.Value.AbsoluteUri);
            }
        }
        catch
        {
            ClearUnstartedNavigation(navigation);
            throw;
        }
    }

    private bool InvokeHistoryNavigation(
        Func<CefBrowser, bool> canNavigate,
        Action<CefBrowser> navigate)
    {
        ThrowIfDisposed();
        if (_ignoreInitialBlank
            || _browser is not { IsInitialized: true } browser
            || !canNavigate(browser))
        {
            return false;
        }

        var navigation = BeginExplicitNavigation(pendingAddress: null);
        try
        {
            _ignoreInitialBlank = false;
            navigate(browser);
            return true;
        }
        catch
        {
            ClearUnstartedNavigation(navigation);
            throw;
        }
    }

    private void OnResourceRequest(
        object? sender,
        ResourceRequestEventArgs args)
    {
        try
        {
            if (_disposed)
            {
                args.Cancel();
                return;
            }

            var permittedPage = Volatile.Read(
                ref _localDocumentAccess).PermittedPage;
            if (_contentPolicy is CefBrowserContentPolicy.RestrictedLocalPreview)
            {
                if (IsPermittedRestrictedHtmlPreviewRequest(
                        args.Url,
                        args.Method,
                        args.Type,
                        permittedPage))
                {
                    args.Continue();
                }
                else
                {
                    args.Cancel();
                }

                return;
            }

            if (args.Type is not Cef.ResourceType.MainFrame)
            {
                ResolveSubresource(args, Volatile.Read(ref _resourceRequestPolicy));
                return;
            }

            var requestPolicy = _activeNavigation?.ReadRequestPolicy();
            if (requestPolicy is null)
            {
                args.Continue();
                return;
            }

            if (!BrowserAddress.TryParse(args.Url, out var address))
            {
                args.Cancel();
                return;
            }

            _ = ResolveMainFrameRequestAsync(args, address, requestPolicy);
        }
        catch
        {
            // Every ResourceRequest token must be resolved. Host or dispatcher
            // failures deny the request instead of hanging or bypassing policy.
            args.Cancel();
        }
    }

    private static async Task ResolveMainFrameRequestAsync(
        ResourceRequestEventArgs args,
        BrowserAddress address,
        Func<BrowserAddress, CancellationToken, ValueTask<bool>> requestPolicy)
    {
        try
        {
            using var deadline = new CancellationTokenSource(
                DestinationResolutionDeadline);
            if (await requestPolicy(address, deadline.Token).ConfigureAwait(false))
            {
                args.Continue();
            }
            else
            {
                args.Cancel();
            }
        }
        catch
        {
            // DNS failure, timeout, cancellation, and host policy failures are
            // all denials at this pre-request boundary.
            args.Cancel();
        }
    }

    private void ResolveSubresource(
        ResourceRequestEventArgs args,
        Func<BrowserAddress, CancellationToken, ValueTask<bool>>? requestPolicy)
    {
        if (Uri.TryCreate(args.Url, UriKind.Absolute, out var uri)
            && uri.IsFile
            && !IsPermittedLocalSubresource(
                uri,
                Volatile.Read(ref _localDocumentAccess).PermittedPage))
        {
            args.Cancel();
            return;
        }

        if (requestPolicy is not null)
        {
            if (!BrowserAddress.TryParse(args.Url, out var address))
            {
                args.Cancel();
                return;
            }

            _ = ResolveMainFrameRequestAsync(args, address, requestPolicy);
            return;
        }

        args.Continue();
    }

    private void OnBeforeBrowse(object? sender, BeforeBrowseEventArgs args)
    {
        try
        {
            if (_disposed || !Dispatcher.UIThread.CheckAccess())
            {
                args.Cancel = true;
                return;
            }

            if (_ignoreInitialBlank && IsBlank(args.Url))
            {
                return;
            }

            // The host creates the only allowed top-level navigation. Links,
            // forms, refresh directives, and other document-initiated attempts
            // arrive with no active explicit generation and fail closed.
            if (_contentPolicy is CefBrowserContentPolicy.RestrictedLocalPreview
                && _activeNavigation is null)
            {
                args.Cancel = true;
                return;
            }

            var navigation = EnsureActiveNavigation();
            if (!TryResolveNavigationAddress(args.Url, navigation, out var address))
            {
                RejectNavigation(
                    navigation,
                    NativeBrowserNavigationRejectionReason.UnsupportedAddress);
                args.Cancel = true;
                return;
            }

            navigation.PendingAddress = address;
            if (!TryAdmitNavigation(navigation, address))
            {
                args.Cancel = true;
                return;
            }

            navigation.AdmitLeg(args.Url, args.IsRedirect);
            AdmitLocalDocumentAccess(address);
        }
        catch
        {
            args.Cancel = true;
        }
    }

    private void OnAddressChanged(object? sender, string url) =>
        RunOnUiThread(() =>
        {
            if (_disposed || (_ignoreInitialBlank && IsBlank(url)))
            {
                return;
            }

            if (_activeNavigation is { } navigation)
            {
                ObserveAddressChangeDuringNavigation(navigation, url);
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !TryResolveNavigation(
                    uri,
                    Volatile.Read(ref _localDocumentAccess).PermittedPage,
                    out var address))
            {
                return;
            }

            PublishSameDocumentAddress(address);
            _semanticAdapter?.InvalidateDocument();
        });

    private void OnLoadingStateChanged(object? sender, LoadingState state) =>
        RunOnUiThread(() =>
        {
            if (_disposed || state.IsLoading)
            {
                return;
            }

            TryCompleteSameDocumentNavigation();
        });

    private void ObserveAddressChangeDuringNavigation(
        ActiveNativeNavigation navigation,
        string url)
    {
        if (!TryResolveNavigationAddress(url, navigation, out var address))
        {
            return;
        }

        navigation.PendingAddress = address;
        navigation.HasObservedAddressChange = true;
        if (!navigation.HasStarted && !TryAdmitNavigation(navigation, address))
        {
            return;
        }

        navigation.AdmitLeg(url, isRedirect: false);
        AdmitLocalDocumentAccess(address);
    }

    private void TryCompleteSameDocumentNavigation()
    {
        if (_activeNavigation is not
            {
                HasObservedAddressChange: true,
                HasDocumentLoadStarted: false,
            } navigation)
        {
            return;
        }

        var address = navigation.PendingAddress;
        CompleteNavigation(
            address?.Value.AbsoluteUri,
            isSuccess: !navigation.WasRejected,
            wasStopped: navigation.StopRequested);
    }

    private void PublishSameDocumentAddress(BrowserAddress address)
    {
        try
        {
            AddressChanged?.Invoke(
                this,
                new NativeBrowserAddressChangedEventArgs(address));
        }
        catch
        {
            // Browser callbacks are isolation boundaries. A host observer
            // cannot be allowed to escape into CEF's display callback.
        }
    }

    private void OnLoadStart(object? sender, LoadStartEventArgs args)
    {
        if (!args.IsMainFrame)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (_disposed || (_ignoreInitialBlank && IsBlank(args.Url)))
            {
                return;
            }

            var navigation = EnsureActiveNavigation();
            _semanticAdapter?.InvalidateDocument();
            navigation.HasDocumentLoadStarted = true;
            if (navigation.HasStarted)
            {
                return;
            }

            if (!TryResolveNavigationAddress(args.Url, navigation, out var address))
            {
                _browser?.StopLoad();
                RejectNavigation(
                    navigation,
                    NativeBrowserNavigationRejectionReason.UnsupportedAddress);
                return;
            }

            navigation.PendingAddress = address;
            if (!TryAdmitNavigation(navigation, address))
            {
                _browser?.StopLoad();
                return;
            }

            navigation.AdmitLeg(args.Url, isRedirect: false);
            AdmitLocalDocumentAccess(address);
        });
    }

    private void OnLoadEnd(object? sender, LoadEndEventArgs args)
    {
        if (!args.IsMainFrame)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (TryFinishInitialBlank(args.Url))
            {
                return;
            }

            var wasStopped = _activeNavigation?.StopRequested == true;
            CompleteNavigation(
                args.Url,
                isSuccess: !wasStopped,
                wasStopped: wasStopped);
        });
    }

    private void OnLoadError(object? sender, LoadErrorEventArgs args)
    {
        if (!args.IsMainFrame)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (TryFinishInitialBlank(args.FailedUrl))
            {
                return;
            }

            if (args.ErrorCode is Cef.CefErrorCode.Aborted
                && _activeNavigation?.ShouldAwaitCompletionAfterAbort(
                    args.FailedUrl) == true)
            {
                // BeforeBrowse identified this exact failed URL as the leg
                // replaced by an admitted redirect. Only that known abort is
                // non-terminal; window.stop, canceled downloads, and unknown
                // aborts must release the active generation.
                return;
            }

            CompleteNavigation(
                args.FailedUrl,
                isSuccess: false,
                wasStopped: _activeNavigation?.StopRequested == true,
                failureKind: MapLoadFailure(args.ErrorCode));
        });
    }

    private void OnRenderProcessGone(
        object? sender,
        RenderProcessGoneEventArgs args) =>
        RunOnUiThread(() =>
        {
            // Only numeric process metadata is safe; CEF's error text may contain page data.
            SecretSafeDiagnosticProjection.WriteStandardError(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"browser.renderer.exited.status-{(int)args.Status}.exit-{args.ErrorCode}"),
                SecretSafeDiagnosticKind.Unexpected);
            try
            {
                CompleteNavigation(
                    _browser?.Url,
                    isSuccess: false,
                    wasStopped: false);
            }
            finally
            {
                try
                {
                    RenderProcessFailed?.Invoke(this, EventArgs.Empty);
                }
                catch
                {
                    // A host observer cannot be allowed to escape into CEF's
                    // process-lifecycle callback.
                }
            }
        });

    private void CompleteNavigation(
        string? url,
        bool isSuccess,
        bool wasStopped = false,
        NativeBrowserLoadFailureKind failureKind =
            NativeBrowserLoadFailureKind.None)
    {
        var navigation = _activeNavigation;
        if (_disposed || navigation is null)
        {
            return;
        }

        BrowserAddress? address = null;
        if (navigation.PendingAddress?.Document is not null)
        {
            address = navigation.PendingAddress;
        }
        else if (!string.IsNullOrWhiteSpace(url)
            && TryResolveNavigationAddress(url, navigation, out var resolved))
        {
            address = resolved;
        }
        else
        {
            address = navigation.PendingAddress;
        }

        var completedSuccessfully = isSuccess
            && !wasStopped
            && !navigation.WasRejected
            && address is not null;
        CompleteLocalDocumentAccess(
            completedSuccessfully,
            address);
        _activeNavigation = null;
        try
        {
            NavigationCompleted?.Invoke(
                this,
                new NativeBrowserNavigationCompletedEventArgs(
                    address,
                    completedSuccessfully,
                    navigation.Generation,
                    wasStopped,
                    failureKind));
        }
        catch
        {
            // Browser callbacks are isolation boundaries. The state owner has
            // already received the terminal event if it ran before a failing
            // secondary subscriber.
        }
    }

    private bool TryAdmitNavigation(
        ActiveNativeNavigation navigation,
        BrowserAddress address)
    {
        navigation.HasStarted = true;
        var starting = new NativeBrowserNavigationEventArgs(
            address,
            navigation.Generation);
        try
        {
            NavigationStarted?.Invoke(this, starting);
        }
        catch
        {
            starting.Cancel = true;
        }
        if (!starting.Cancel)
        {
            navigation.PendingAddress = address;
            return true;
        }

        RejectNavigation(
            navigation,
            NativeBrowserNavigationRejectionReason.OriginPolicy);
        return false;
    }

    private void RejectNavigation(
        ActiveNativeNavigation navigation,
        NativeBrowserNavigationRejectionReason reason)
    {
        if (!ReferenceEquals(_activeNavigation, navigation)
            || navigation.WasRejected)
        {
            return;
        }

        // Keep the generation active until CEF reports the documented
        // ERR_ABORTED terminal event. BrowserSurface uses that event to end
        // its drain fence before accepting another governed navigation.
        navigation.WasRejected = true;
        _queuedAddress = null;
        RollBackLocalDocumentAccess();
        try
        {
            NavigationRejected?.Invoke(
                this,
                new NativeBrowserNavigationRejectedEventArgs(
                    reason,
                    navigation.Generation));
        }
        catch
        {
            // Do not let a host callback escape into CEF's request gate.
        }
    }

    private ActiveNativeNavigation BeginExplicitNavigation(
        BrowserAddress? pendingAddress)
    {
        if (_activeNavigation is not null)
        {
            throw new InvalidOperationException(
                "A CEF top-level navigation is already active.");
        }

        _semanticAdapter?.InvalidateDocument();
        return _activeNavigation = NewNavigation(pendingAddress);
    }

    private ActiveNativeNavigation EnsureActiveNavigation() =>
        _activeNavigation ??= NewNavigation(pendingAddress: null);

    private ActiveNativeNavigation NewNavigation(BrowserAddress? pendingAddress)
    {
        _lastNavigationGeneration = checked(_lastNavigationGeneration + 1);
        return new ActiveNativeNavigation(
            _lastNavigationGeneration,
            pendingAddress);
    }

    private void ClearUnstartedNavigation(ActiveNativeNavigation navigation)
    {
        if (ReferenceEquals(_activeNavigation, navigation)
            && !navigation.HasStarted)
        {
            _activeNavigation = null;
            _queuedAddress = null;
        }
    }

    private bool TryFinishInitialBlank(string? url)
    {
        if (!_ignoreInitialBlank
            || string.IsNullOrWhiteSpace(url)
            || !IsBlank(url))
        {
            return false;
        }

        _ignoreInitialBlank = false;
        if (_activeNavigation is { StopRequested: true })
        {
            _queuedAddress = null;
            CompleteNavigation(
                url,
                isSuccess: false,
                wasStopped: true);
            return true;
        }

        if (_browser is { IsInitialized: true } browser
            && _activeNavigation is { } navigation
            && _queuedAddress is not null)
        {
            DispatchQueuedNavigation(browser, navigation);
        }

        return true;
    }

    private bool TryResolveNavigationAddress(
        string? value,
        ActiveNativeNavigation navigation,
        [NotNullWhen(true)] out BrowserAddress? address)
    {
        if (navigation.PendingAddress is { Document: not null } document
            && IsLoadStringAddress(value))
        {
            address = document;
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (navigation.PendingAddress is { IsLocalFile: true } pending
                && IsPermittedTopLevelLocalPage(uri, pending))
            {
                address = pending;
                return true;
            }

            return TryResolveNavigation(
                uri,
                Volatile.Read(ref _localDocumentAccess).PermittedPage,
                out address);
        }

        address = null;
        return false;
    }

    internal static bool TryResolveNavigation(
        Uri? request,
        BrowserAddress? permittedLocalPage,
        [NotNullWhen(true)] out BrowserAddress? address)
    {
        if (BrowserAddress.TryParse(request?.AbsoluteUri, out address))
        {
            return true;
        }

        if (request is not null
            && permittedLocalPage is { } permitted
            && IsPermittedTopLevelLocalPage(request, permitted))
        {
            address = permitted;
            return true;
        }

        address = null;
        return false;
    }

    private static bool IsPermittedTopLevelLocalPage(
        Uri request,
        BrowserAddress? permittedLocalPage) =>
        permittedLocalPage is { IsLocalFile: true } permitted
        && string.Equals(
            request.AbsoluteUri,
            permitted.Value.AbsoluteUri,
            StringComparison.Ordinal);

    internal static bool IsPermittedLocalSubresource(
        Uri request,
        BrowserAddress? permittedLocalPage)
    {
        if (!request.IsFile
            || permittedLocalPage is not { IsLocalFile: true } permitted)
        {
            return false;
        }

        if (IsPermittedTopLevelLocalPage(request, permitted))
        {
            return true;
        }

        try
        {
            var pagePath = Path.GetFullPath(permitted.Value.LocalPath);
            var resourcePath = Path.GetFullPath(request.LocalPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(
                    Path.GetDirectoryName(pagePath),
                    Path.GetDirectoryName(resourcePath),
                    comparison))
            {
                return false;
            }

            // Adjacent CSS/images/fonts are part of a generated preview, but
            // a link must not turn that narrow directory capability into a
            // path outside it. Existing symlink files are denied as well.
            return !File.Exists(resourcePath)
                || new FileInfo(resourcePath).LinkTarget is null;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsPermittedRestrictedHtmlPreviewRequest(
        string? requestUrl,
        string? method,
        Cef.ResourceType resourceType,
        BrowserAddress? permittedLocalPage)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var request))
        {
            return false;
        }

        if (resourceType is Cef.ResourceType.MainFrame)
        {
            return IsPermittedTopLevelLocalPage(request, permittedLocalPage);
        }

        if (!IsPermittedLocalSubresource(request, permittedLocalPage))
        {
            return false;
        }

        var extension = Path.GetExtension(request.LocalPath);
        return resourceType switch
        {
            Cef.ResourceType.Stylesheet => HasExtension(extension, ".css"),
            Cef.ResourceType.Image or Cef.ResourceType.Favicon =>
                HasExtension(
                    extension,
                    ".png",
                    ".jpg",
                    ".jpeg",
                    ".gif",
                    ".webp",
                    ".bmp",
                    ".ico",
                    ".avif"),
            Cef.ResourceType.Font => HasExtension(
                extension,
                ".woff",
                ".woff2",
                ".ttf",
                ".otf"),
            _ => false,
        };
    }

    private static bool HasExtension(string value, params string[] allowed) =>
        allowed.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool IsLoadStringAddress(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals(LoadStringHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlank(string value) =>
        string.Equals(
            value,
            BrowserAddress.Blank.Value.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);

    private void AdmitLocalDocumentAccess(BrowserAddress address)
    {
        var current = Volatile.Read(ref _localDocumentAccess);
        Volatile.Write(
            ref _localDocumentAccess,
            current.Admit(address));
    }

    private void CompleteLocalDocumentAccess(
        bool isSuccess,
        BrowserAddress? address)
    {
        var current = Volatile.Read(ref _localDocumentAccess);
        Volatile.Write(
            ref _localDocumentAccess,
            current.Complete(isSuccess, address));
    }

    private void RollBackLocalDocumentAccess()
    {
        var current = Volatile.Read(ref _localDocumentAccess);
        Volatile.Write(
            ref _localDocumentAccess,
            current.RollBack());
    }

    private static void RunOnUiThread(Action operation)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            operation();
            return;
        }

        Dispatcher.UIThread.Post(operation);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private void OnBeforePopup(object? sender, BeforePopupEventArgs args)
    {
        // Subscribing is the cancellation signal in Exclr8CEF. Only addresses
        // accepted by GhostSHELL's normal navigation boundary are promoted to
        // a shell tab; unsupported popup schemes stay closed.
        if (!BrowserAddress.TryParse(args.TargetUrl, out var address))
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (!_disposed)
            {
                NewTabRequested?.Invoke(
                    this,
                    new BrowserNewTabRequestedEventArgs(
                        address,
                        args.UserGesture));
            }
        });
    }

    private async void OnAuthenticationRequested(
        object? sender,
        AuthRequestEventArgs args)
    {
        _ = sender;
        if (_disposed)
        {
            args.Cancel();
            return;
        }

        var challenge = new BrowserAuthenticationChallenge(
            args.IsProxy,
            args.Host,
            args.Port,
            args.Realm,
            args.Scheme);
        if (args.IsProxy)
        {
            var proxyCredentials = _proxyAuthenticationResolver?.Resolve(challenge);
            if (proxyCredentials is null)
            {
                args.Cancel();
            }
            else
            {
                args.Continue(proxyCredentials.Username, proxyCredentials.Password);
            }

            return;
        }

        if (_profile is null
            || _authenticationResolver is null
            || _profile.Definition.Authentication is null)
        {
            args.Cancel();
            return;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var credentials = await _authenticationResolver.ResolveAsync(
                    _profile,
                    challenge,
                    deadline.Token)
                .ConfigureAwait(false);
            if (credentials is null || _disposed)
            {
                args.Cancel();
                return;
            }

            args.Continue(credentials.Username, credentials.Password);
        }
        catch
        {
            args.Cancel();
        }
    }

    internal sealed class ActiveNativeNavigation(
        long generation,
        BrowserAddress? pendingAddress)
    {
        private Func<
            BrowserAddress,
            CancellationToken,
            ValueTask<bool>>? _requestPolicy;
        private HashSet<string>? _supersededLegs;
        private string? _currentLeg;

        public long Generation { get; } = generation;

        public BrowserAddress? PendingAddress { get; set; } = pendingAddress;

        public bool HasStarted { get; set; }

        public bool HasDocumentLoadStarted { get; set; }

        public bool HasObservedAddressChange { get; set; }

        public bool StopRequested { get; set; }

        public bool WasRejected { get; set; }

        public bool MayDispatchQueuedNavigation =>
            !StopRequested && !WasRejected;

        public void SetRequestPolicy(
            Func<BrowserAddress, CancellationToken, ValueTask<bool>> policy) =>
            Volatile.Write(
                ref _requestPolicy,
                policy ?? throw new ArgumentNullException(nameof(policy)));

        public Func<BrowserAddress, CancellationToken, ValueTask<bool>>?
            ReadRequestPolicy() => Volatile.Read(ref _requestPolicy);

        public void AdmitLeg(string? url, bool isRedirect)
        {
            var nextLeg = NormalizeNavigationUrl(url);
            if (nextLeg is null)
            {
                return;
            }

            if (isRedirect && _currentLeg is not null)
            {
                (_supersededLegs ??= new(StringComparer.Ordinal))
                    .Add(_currentLeg);
            }

            _currentLeg = nextLeg;
        }

        public bool ShouldAwaitCompletionAfterAbort(string? failedUrl)
        {
            if (StopRequested || WasRejected)
            {
                return false;
            }

            var failedLeg = NormalizeNavigationUrl(failedUrl);
            return failedLeg is not null
                && _supersededLegs?.Remove(failedLeg) == true;
        }

        private static string? NormalizeNavigationUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                ? uri.AbsoluteUri
                : value;
        }
    }
}

internal enum CefBrowserContentPolicy
{
    Ordinary,
    RestrictedLocalPreview,
}

/// <summary>
/// Immutable cross-thread capability for local resources. CEF's IO thread only
/// observes published snapshots; the UI thread replaces the whole policy as a
/// main-document navigation is admitted or reaches a terminal state.
/// </summary>
internal sealed record CefLocalDocumentAccessPolicy
{
    private CefLocalDocumentAccessPolicy(
        BrowserAddress? committedPage,
        BrowserAddress? provisionalPage,
        bool hasProvisionalPage)
    {
        CommittedPage = committedPage;
        ProvisionalPage = provisionalPage;
        HasProvisionalPage = hasProvisionalPage;
    }

    public static CefLocalDocumentAccessPolicy None { get; } =
        new(
            committedPage: null,
            provisionalPage: null,
            hasProvisionalPage: false);

    public BrowserAddress? CommittedPage { get; }

    public BrowserAddress? ProvisionalPage { get; }

    public bool HasProvisionalPage { get; }

    public BrowserAddress? PermittedPage =>
        HasProvisionalPage ? ProvisionalPage : CommittedPage;

    public CefLocalDocumentAccessPolicy Admit(BrowserAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new CefLocalDocumentAccessPolicy(
            CommittedPage,
            address.IsLocalFile ? address : null,
            hasProvisionalPage: true);
    }

    public CefLocalDocumentAccessPolicy Complete(
        bool isSuccess,
        BrowserAddress? address)
    {
        if (!isSuccess)
        {
            return RollBack();
        }

        ArgumentNullException.ThrowIfNull(address);
        return new CefLocalDocumentAccessPolicy(
            address.IsLocalFile ? address : null,
            provisionalPage: null,
            hasProvisionalPage: false);
    }

    public CefLocalDocumentAccessPolicy RollBack() =>
        new(
            CommittedPage,
            provisionalPage: null,
            hasProvisionalPage: false);
}
