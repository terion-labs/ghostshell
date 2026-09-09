using System.Diagnostics;
using Asura.Application;
using Asura.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Asura.App.Controls;

public sealed class BrowserPresentationHost : ContentControl
{
    private static readonly IBrush WaitingBrush = Brush.Parse("#8B8B91");
    private static readonly IBrush ReadyBrush = Brush.Parse("#3FB950");
    private static readonly IBrush LoadingBrush = Brush.Parse("#D79B57");
    private static readonly IBrush FailedBrush = Brush.Parse("#FF7A55");

    public static readonly StyledProperty<ISessionHostClient?> SessionClientProperty =
        AvaloniaProperty.Register<BrowserPresentationHost, ISessionHostClient?>(
            nameof(SessionClient));

    public static readonly StyledProperty<EnsureBrowserSessionRequest?> SessionRequestProperty =
        AvaloniaProperty.Register<BrowserPresentationHost, EnsureBrowserSessionRequest?>(
            nameof(SessionRequest));

    public static readonly StyledProperty<ClientId?> ClientIdProperty =
        AvaloniaProperty.Register<BrowserPresentationHost, ClientId?>(
            nameof(ClientId));

    public static readonly StyledProperty<BrowserRendererView?> RendererViewProperty =
        AvaloniaProperty.Register<BrowserPresentationHost, BrowserRendererView?>(
            nameof(RendererView));

    public static readonly StyledProperty<bool> IsAgentActiveProperty =
        AvaloniaProperty.Register<BrowserPresentationHost, bool>(
            nameof(IsAgentActive));

    public static readonly DirectProperty<BrowserPresentationHost, string>
        AddressTextProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(AddressText),
                control => control.AddressText,
                (control, value) => control.AddressText = value);

    public static readonly DirectProperty<BrowserPresentationHost, string>
        StatusTextProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(StatusText),
                control => control.StatusText);

    public static readonly DirectProperty<BrowserPresentationHost, string>
        StatusMessageProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(StatusMessage),
                control => control.StatusMessage);

    public static readonly DirectProperty<BrowserPresentationHost, IBrush>
        StatusBrushProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, IBrush>(
                nameof(StatusBrush),
                control => control.StatusBrush);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        IsLiveProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(IsLive),
                control => control.IsLive);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        IsLoadingProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(IsLoading),
                control => control.IsLoading);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        CanGoBackProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(CanGoBack),
                control => control.CanGoBack);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        CanGoForwardProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(CanGoForward),
                control => control.CanGoForward);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        ShowFallbackProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(ShowFallback),
                control => control.ShowFallback);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        IsFindVisibleProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(IsFindVisible),
                control => control.IsFindVisible);

    public static readonly DirectProperty<BrowserPresentationHost, string>
        FindTextProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(FindText),
                control => control.FindText,
                (control, value) => control.FindText = value);

    public static readonly DirectProperty<BrowserPresentationHost, string>
        FindResultTextProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(FindResultText),
                control => control.FindResultText);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        IsProductNoticeVisibleProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(IsProductNoticeVisible),
                control => control.IsProductNoticeVisible);

    public static readonly DirectProperty<BrowserPresentationHost, string>
        ProductHeadingProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(ProductHeading),
                control => control.ProductHeading);

    public static readonly DirectProperty<BrowserPresentationHost, string>
        ProductMessageProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(ProductMessage),
                control => control.ProductMessage);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        HasProductActionProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(HasProductAction),
                control => control.HasProductAction);

    public static readonly DirectProperty<BrowserPresentationHost, string>
        ProductActionLabelProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, string>(
                nameof(ProductActionLabel),
                control => control.ProductActionLabel);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        HasDownloadProgressProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(HasDownloadProgress),
                control => control.HasDownloadProgress);

    public static readonly DirectProperty<BrowserPresentationHost, bool>
        IsDownloadProgressIndeterminateProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, bool>(
                nameof(IsDownloadProgressIndeterminate),
                control => control.IsDownloadProgressIndeterminate);

    public static readonly DirectProperty<BrowserPresentationHost, double>
        DownloadProgressProperty =
            AvaloniaProperty.RegisterDirect<BrowserPresentationHost, double>(
                nameof(DownloadProgress),
                control => control.DownloadProgress);

    private CancellationTokenSource? _attachmentLifetime;
    private ISessionHostClient? _attachedClient;
    private ClientId? _attachedClientId;
    private SessionId? _attachedSessionId;
    private AttachmentId? _attachmentId;
    private IBrowserRenderer? _subscribedRenderer;
    private BrowserRendererView? _hostedRendererView;
    private long _initializationGeneration;
    private bool _isAttachedToVisualTree;
    private string _addressText = string.Empty;
    private string _statusText = "Waiting";
    private string _statusMessage = string.Empty;
    private IBrush _statusBrush = WaitingBrush;
    private bool _isLive;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _showFallback = true;
    private BrowserAddress _presentedAddress = BrowserAddress.Blank;
    private bool _isFindVisible;
    private string _findText = string.Empty;
    private string _findResultText = string.Empty;
    private bool _isProductNoticeVisible;
    private string _productHeading = string.Empty;
    private string _productMessage = string.Empty;
    private bool _hasProductAction;
    private string _productActionLabel = string.Empty;
    private bool _hasDownloadProgress;
    private bool _isDownloadProgressIndeterminate;
    private double _downloadProgress;
    private BrowserAddress? _recoveryAddress;

    public BrowserPresentationHost()
    {
        Focusable = true;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        AutomationProperties.SetName(this, "Web browser");
        AutomationProperties.SetHelpText(
            this,
            "Web content for this browser panel.");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetItemStatus(this, StatusText);
    }

    public event EventHandler<BrowserStateChangedEventArgs>? BrowserStateChanged;

    public ISessionHostClient? SessionClient
    {
        get => GetValue(SessionClientProperty);
        set => SetValue(SessionClientProperty, value);
    }

    public EnsureBrowserSessionRequest? SessionRequest
    {
        get => GetValue(SessionRequestProperty);
        set => SetValue(SessionRequestProperty, value);
    }

    public ClientId? ClientId
    {
        get => GetValue(ClientIdProperty);
        set => SetValue(ClientIdProperty, value);
    }

    public BrowserRendererView? RendererView
    {
        get => GetValue(RendererViewProperty);
        set => SetValue(RendererViewProperty, value);
    }

    public bool IsAgentActive
    {
        get => GetValue(IsAgentActiveProperty);
        set => SetValue(IsAgentActiveProperty, value);
    }

    public string AddressText
    {
        get => _addressText;
        set => SetAndRaise(
            AddressTextProperty,
            ref _addressText,
            value ?? string.Empty);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetAndRaise(StatusTextProperty, ref _statusText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetAndRaise(
            StatusMessageProperty,
            ref _statusMessage,
            value);
    }

    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetAndRaise(StatusBrushProperty, ref _statusBrush, value);
    }

    public bool IsLive
    {
        get => _isLive;
        private set => SetAndRaise(IsLiveProperty, ref _isLive, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        private set => SetAndRaise(CanGoBackProperty, ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        private set => SetAndRaise(CanGoForwardProperty, ref _canGoForward, value);
    }

    public bool ShowFallback
    {
        get => _showFallback;
        private set => SetAndRaise(ShowFallbackProperty, ref _showFallback, value);
    }

    public bool IsFindVisible
    {
        get => _isFindVisible;
        private set => SetAndRaise(IsFindVisibleProperty, ref _isFindVisible, value);
    }

    public string FindText
    {
        get => _findText;
        set => SetAndRaise(FindTextProperty, ref _findText, value ?? string.Empty);
    }

    public string FindResultText
    {
        get => _findResultText;
        private set => SetAndRaise(FindResultTextProperty, ref _findResultText, value);
    }

    public bool IsProductNoticeVisible
    {
        get => _isProductNoticeVisible;
        private set => SetAndRaise(
            IsProductNoticeVisibleProperty,
            ref _isProductNoticeVisible,
            value);
    }

    public string ProductHeading
    {
        get => _productHeading;
        private set => SetAndRaise(ProductHeadingProperty, ref _productHeading, value);
    }

    public string ProductMessage
    {
        get => _productMessage;
        private set => SetAndRaise(ProductMessageProperty, ref _productMessage, value);
    }

    public bool HasProductAction
    {
        get => _hasProductAction;
        private set => SetAndRaise(HasProductActionProperty, ref _hasProductAction, value);
    }

    public string ProductActionLabel
    {
        get => _productActionLabel;
        private set => SetAndRaise(
            ProductActionLabelProperty,
            ref _productActionLabel,
            value);
    }

    public bool HasDownloadProgress
    {
        get => _hasDownloadProgress;
        private set => SetAndRaise(
            HasDownloadProgressProperty,
            ref _hasDownloadProgress,
            value);
    }

    public bool IsDownloadProgressIndeterminate
    {
        get => _isDownloadProgressIndeterminate;
        private set => SetAndRaise(
            IsDownloadProgressIndeterminateProperty,
            ref _isDownloadProgressIndeterminate,
            value);
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetAndRaise(
            DownloadProgressProperty,
            ref _downloadProgress,
            value);
    }

    internal bool RequestInputFocus() =>
        RendererView?.View.Focus() ?? Focus();

    internal void OpenDeveloperTools()
    {
        if (RendererView?.OpenDeveloperTools() is true)
        {
            return;
        }

        SetOperationMessage(
            "Developer tools are unavailable until the browser is ready.");
    }

    internal void OpenFind()
    {
        IsFindVisible = true;
        FindResultText = string.IsNullOrWhiteSpace(FindText)
            ? string.Empty
            : "Searching…";
    }

    internal void UpdateFind(string? searchText)
    {
        FindText = searchText ?? string.Empty;
        if (_subscribedRenderer is not IBrowserFindController controller)
        {
            FindResultText = "Unavailable";
            return;
        }

        if (string.IsNullOrWhiteSpace(FindText))
        {
            _ = controller.StopFind();
            FindResultText = string.Empty;
            return;
        }

        FindResultText = controller.StartFind(FindText)
            ? "Searching…"
            : "Unavailable";
    }

    internal void FindNext(BrowserFindDirection direction)
    {
        if (_subscribedRenderer is not IBrowserFindController controller
            || !controller.FindNext(direction))
        {
            FindResultText = "Unavailable";
        }
    }

    internal void CloseFind()
    {
        if (_subscribedRenderer is IBrowserFindController controller)
        {
            _ = controller.StopFind();
        }

        IsFindVisible = false;
        FindResultText = string.Empty;
        RequestInputFocus();
    }

    internal void DismissProductNotice()
    {
        IsProductNoticeVisible = false;
        HasProductAction = false;
        HasDownloadProgress = false;
        _recoveryAddress = null;
    }

    internal async ValueTask PerformProductActionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_recoveryAddress is not { } address)
        {
            return;
        }

        DismissProductNotice();
        await NavigateAddressAsync(address.ToString(), cancellationToken);
    }

    internal void OpenInSystemBrowser()
    {
        if (!TryGetSystemBrowserAddress(_presentedAddress, out var address))
        {
            SetOperationMessage(
                "Navigate this panel to an HTTP or HTTPS page before opening it in the system browser.");
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = address.AbsoluteUri,
                UseShellExecute = true,
            });
            if (process is null)
            {
                SetOperationMessage("The system browser could not be opened.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            SetOperationMessage("The system browser could not be opened.");
        }
    }

    public async ValueTask NavigateAddressAsync(
        string? text,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveAddress(text, out var address))
        {
            SetOperationMessage(
                "Enter a complete HTTP or HTTPS address, such as https://example.com.");
            return;
        }

        var client = RequireAttachedClient();
        var sessionId = RequireAttachedSessionId();
        var result = await client.NavigateBrowserAsync(
            new BrowserNavigateRequest(sessionId, address),
            NewContext(),
            cancellationToken);
        ApplyOperationResult(result);
    }

    public async ValueTask GoBackAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequireAttachedClient().GoBackBrowserAsync(
            RequireAttachedSessionId(),
            NewContext(),
            cancellationToken);
        ApplyOperationResult(result);
    }

    public async ValueTask GoForwardAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequireAttachedClient().GoForwardBrowserAsync(
            RequireAttachedSessionId(),
            NewContext(),
            cancellationToken);
        ApplyOperationResult(result);
    }

    public async ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequireAttachedClient().ReloadBrowserAsync(
            RequireAttachedSessionId(),
            NewContext(),
            cancellationToken);
        ApplyOperationResult(result);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequireAttachedClient().StopBrowserAsync(
            RequireAttachedSessionId(),
            NewContext(),
            cancellationToken);
        ApplyOperationResult(result);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        HostRendererVisual();
        RestartSession();
    }

    /// <summary>
    /// The panel is off screen — another tab is in front, or this view is being
    /// rebuilt because panels moved. Neither is a reason to detach or dispose
    /// the renderer, because both belong to the panel rather than this host.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttachedToVisualTree = false;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Places the panel-owned browser visual directly in this content control.
    /// A dock rebuild may create the arriving host before unbinding the departing
    /// one, so adoption explicitly releases the previous host first.
    /// </summary>
    private void HostRendererVisual()
    {
        if (ReferenceEquals(_hostedRendererView, RendererView))
        {
            return;
        }

        if (_hostedRendererView is { } previous)
        {
            previous.SetAgentActivity(isActive: false);
            ReleaseRendererVisual(previous);
        }

        if (RendererView is not { } rendererView)
        {
            return;
        }

        rendererView.PresentationHost?.ReleaseRendererVisual(rendererView);
        _hostedRendererView = rendererView;
        rendererView.PresentationHost = this;
        rendererView.SetAgentActivity(IsAgentActive);
        Content = rendererView.View;
    }

    /// <summary>
    /// Stops this host from drawing a renderer without ending the panel-owned
    /// attachment or renderer lifetime.
    /// </summary>
    internal void ReleaseRendererVisual(BrowserRendererView rendererView)
    {
        if (!ReferenceEquals(_hostedRendererView, rendererView))
        {
            return;
        }

        _hostedRendererView = null;
        if (ReferenceEquals(Content, rendererView.View))
        {
            Content = null;
        }

        if (ReferenceEquals(rendererView.PresentationHost, this))
        {
            rendererView.PresentationHost = null;
        }

        StopSession();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (ReferenceEquals(e.Source, this))
        {
            RendererView?.View.Focus();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RendererViewProperty)
        {
            HostRendererVisual();
        }

        if (change.Property == IsAgentActiveProperty)
        {
            RendererView?.SetAgentActivity(IsAgentActive);
        }

        if (change.Property == SessionClientProperty
            || change.Property == SessionRequestProperty
            || change.Property == ClientIdProperty
            || change.Property == RendererViewProperty)
        {
            if (_isAttachedToVisualTree)
            {
                RestartSession();
            }
        }
    }

    private void RestartSession()
    {
        StopSession();
        if (SessionClient is null
            || SessionRequest is null
            || ClientId is null
            || RendererView is not { } rendererView)
        {
            SetWaitingState("Waiting for the browser renderer.");
            return;
        }

        // The panel may already be attached — this view is simply the newest one
        // to draw it. Adopting that attachment is what makes a layout change cost
        // nothing: no detach, no re-attach, no navigation, and the document that
        // was on screen a moment ago is still the one on screen now.
        if (rendererView.Attachment is { } existing
            && existing.Matches(SessionClient, ClientId, SessionRequest.SessionId))
        {
            AdoptAttachment(rendererView, existing);
            return;
        }

        SetWaitingState("Starting the browser…", "Starting");
        var generation = ++_initializationGeneration;
        _attachmentLifetime = new CancellationTokenSource();
        _ = InitializeSessionAsync(generation, _attachmentLifetime.Token);
    }

    private void AdoptAttachment(
        BrowserRendererView rendererView,
        BrowserRendererAttachment attachment)
    {
        _initializationGeneration++;
        _attachedClient = attachment.Client;
        _attachedClientId = attachment.ClientId;
        _attachedSessionId = attachment.SessionId;
        _attachmentId = attachment.AttachmentId;
        IsLive = true;
        ShowFallback = false;
        SubscribeRenderer(rendererView.Renderer);
    }

    /// <summary>
    /// Lets go of what this view was watching. It does not detach: the
    /// attachment belongs to the panel, and is ended by the panel — see
    /// <see cref="BrowserRendererView.Dispose"/>.
    /// </summary>
    private void StopSession()
    {
        _initializationGeneration++;
        _attachmentLifetime?.Cancel();
        _attachmentLifetime?.Dispose();
        _attachmentLifetime = null;
        UnsubscribeRenderer();
        _attachedClient = null;
        _attachedClientId = null;
        _attachedSessionId = null;
        _attachmentId = null;
        IsLive = false;
        IsLoading = false;
        CanGoBack = false;
        CanGoForward = false;
    }

    private async Task InitializeSessionAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = SessionClient
                ?? throw new InvalidOperationException(
                    "The session-host client is unavailable.");
            var request = SessionRequest
                ?? throw new InvalidOperationException(
                    "The browser session request is unavailable.");
            var clientId = ClientId
                ?? throw new InvalidOperationException(
                    "The desktop client identity is unavailable.");
            var rendererView = RendererView
                ?? throw new InvalidOperationException(
                    "The browser renderer is unavailable.");
            var context = OperationContext.ForHuman(clientId);
            var attachment = await rendererView.EnsureAttachmentAsync(
                client,
                clientId,
                request,
                CurrentViewport(),
                cancellationToken);
            if (generation != _initializationGeneration)
            {
                return;
            }

            var state = RequireSuccess(await client.ReadBrowserStateAsync(
                request.SessionId,
                context,
                cancellationToken));
            if (generation != _initializationGeneration)
            {
                return;
            }

            AdoptAttachment(rendererView, attachment);
            if (state.IsSuccess && state.Value is { } browserState)
            {
                ApplyBrowserState(browserState);
            }
            else
            {
                SetOperationMessage(
                    state.Error?.Message ?? "The browser state is unavailable.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == _initializationGeneration)
            {
                SetWaitingState("Browser renderer stopped.", "Stopped");
            }
        }
        catch (Exception exception)
        {
            if (generation == _initializationGeneration)
            {
                UnsubscribeRenderer();
                SetFailureState("The browser could not be initialized.");
            }

            SecretSafeDiagnostics.WriteTrace(
                "browser.renderer-attachment.failed",
                exception);
        }
    }

    private void ApplyOperationResult(
        HostResult<BrowserResult<BrowserSessionState>> result)
    {
        switch (result)
        {
            case HostResult<BrowserResult<BrowserSessionState>>.Success
            {
                Value.IsSuccess: true,
                Value.Value: { } state,
            }:
                ApplyBrowserState(state);
                break;
            case HostResult<BrowserResult<BrowserSessionState>>.Success success:
                SetOperationMessage(
                    success.Value.Error?.Message
                    ?? "The browser operation could not be completed.");
                break;
            case HostResult<BrowserResult<BrowserSessionState>>.Failure failure:
                SetOperationMessage(failure.Error.Message);
                break;
        }
    }

    private void SubscribeRenderer(IBrowserRenderer renderer)
    {
        if (ReferenceEquals(_subscribedRenderer, renderer))
        {
            return;
        }

        UnsubscribeRenderer();
        _subscribedRenderer = renderer;
        _subscribedRenderer.StateChanged += OnRendererStateChanged;
        if (_subscribedRenderer is IBrowserProductEventSource productEventSource)
        {
            productEventSource.ProductEvent += OnBrowserProductEvent;
        }

        ApplyBrowserState(renderer.State);
    }

    private void UnsubscribeRenderer()
    {
        if (_subscribedRenderer is null)
        {
            return;
        }

        _subscribedRenderer.StateChanged -= OnRendererStateChanged;
        if (_subscribedRenderer is IBrowserProductEventSource productEventSource)
        {
            productEventSource.ProductEvent -= OnBrowserProductEvent;
        }

        _subscribedRenderer = null;
    }

    private void OnRendererStateChanged(
        object? sender,
        BrowserStateChangedEventArgs eventArgs)
    {
        _ = sender;
        ApplyBrowserState(eventArgs.State);
    }

    private void OnBrowserProductEvent(
        object? sender,
        BrowserProductEvent productEvent)
    {
        _ = sender;
        ApplyProductEvent(productEvent);
    }

    internal void ApplyProductEvent(BrowserProductEvent productEvent)
    {
        ArgumentNullException.ThrowIfNull(productEvent);
        switch (productEvent)
        {
            case BrowserProductEvent.FindUpdated find:
                FindResultText = find.MatchCount == 0
                    ? "No matches"
                    : $"{find.ActiveMatchOrdinal} of {find.MatchCount}";
                return;
            case BrowserProductEvent.JavaScriptDialogBlocked dialog:
                ShowProductNotice(
                    "Page dialog blocked",
                    string.IsNullOrWhiteSpace(dialog.Message)
                        ? "This page tried to interrupt the browser with a dialog."
                        : dialog.Message);
                return;
            case BrowserProductEvent.FileDialogBlocked fileDialog:
                ShowProductNotice(
                    "File access blocked",
                    string.IsNullOrWhiteSpace(fileDialog.Title)
                        ? "This page tried to open a file picker."
                        : fileDialog.Title);
                return;
            case BrowserProductEvent.PermissionDenied permission:
                ShowProductNotice(
                    "Permission denied",
                    $"{permission.Origin} requested {FormatPermissions(permission.Permissions)}. Asura denied it.");
                return;
            case BrowserProductEvent.CertificateRejected certificate:
                ShowProductNotice(
                    "Certificate rejected",
                    $"Asura did not trust the certificate for {certificate.Address}.");
                return;
            case BrowserProductEvent.DownloadRequested download:
                ShowProductNotice(
                    "Choose download location",
                    $"Select where to save {download.FileName}. The saved file is outside the encrypted browser profile.",
                    hasDownloadProgress: true,
                    progress: null);
                return;
            case BrowserProductEvent.DownloadProgressed download:
                ShowProductNotice(
                    $"Downloading {download.FileName}",
                    FormatDownloadProgress(download.ReceivedBytes, download.TotalBytes),
                    hasDownloadProgress: true,
                    progress: download.PercentComplete);
                return;
            case BrowserProductEvent.DownloadCompleted download:
                ShowProductNotice(
                    "Download complete",
                    string.IsNullOrWhiteSpace(download.FileName)
                        ? "The file was saved to the location you selected."
                        : $"{download.FileName} was saved to the location you selected.");
                return;
            case BrowserProductEvent.DownloadCancelled:
                ShowProductNotice(
                    "Download cancelled",
                    "No file was saved by this download.");
                return;
            case BrowserProductEvent.RendererRecovered recovered:
                _recoveryAddress = recovered.LostAddress == BrowserAddress.Blank
                    ? null
                    : recovered.LostAddress;
                ShowProductNotice(
                    "Page process restarted",
                    "The page process stopped. Cookies and persisted site data remain, but unsaved form input and other volatile page state were lost.",
                    hasAction: _recoveryAddress is not null,
                    actionLabel: "Reload page");
                return;
            case BrowserProductEvent.RendererFailed failed:
                _recoveryAddress = failed.LastAddress == BrowserAddress.Blank
                    ? null
                    : failed.LastAddress;
                ShowProductNotice(
                    "Page process stopped",
                    "The browser could not start a replacement process. Volatile page state was lost.");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(productEvent));
        }
    }

    private void ApplyBrowserState(BrowserSessionState state)
    {
        _presentedAddress = state.Address;
        AddressText = state.Address == BrowserAddress.Blank
            ? string.Empty
            : state.Address.ToString();
        IsLoading = state.LoadState == BrowserLoadState.Loading;
        CanGoBack = state.CanGoBack;
        CanGoForward = state.CanGoForward;
        StatusText = state.LoadState switch
        {
            BrowserLoadState.Ready => "Ready",
            BrowserLoadState.Loading => "Loading",
            BrowserLoadState.Failed => state.Failure?.Code switch
            {
                BrowserErrorCode.NetworkUnavailable => "Offline",
                BrowserErrorCode.NavigationTimedOut => "Timed out",
                BrowserErrorCode.CertificateRejected => "Blocked",
                BrowserErrorCode.RendererUnavailable => "Process stopped",
                _ => "Failed",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        StatusBrush = state.LoadState switch
        {
            BrowserLoadState.Ready => ReadyBrush,
            BrowserLoadState.Loading => LoadingBrush,
            BrowserLoadState.Failed => FailedBrush,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        StatusMessage = state.Failure?.Message ?? string.Empty;
        ShowFallback = false;
        UpdateAutomationStatus();
        BrowserStateChanged?.Invoke(this, new BrowserStateChangedEventArgs(state));
    }

    private void SetWaitingState(string message, string status = "Waiting")
    {
        StatusText = status;
        StatusMessage = message;
        StatusBrush = WaitingBrush;
        ShowFallback = true;
        UpdateAutomationStatus();
    }

    private void SetFailureState(string message)
    {
        IsLive = false;
        IsLoading = false;
        CanGoBack = false;
        CanGoForward = false;
        StatusText = "Error";
        StatusMessage = message;
        StatusBrush = FailedBrush;
        ShowFallback = true;
        UpdateAutomationStatus();
    }

    private void SetOperationMessage(string message)
    {
        StatusMessage = message;
        UpdateAutomationStatus();
    }

    private void ShowProductNotice(
        string heading,
        string message,
        bool hasAction = false,
        string actionLabel = "",
        bool hasDownloadProgress = false,
        int? progress = null)
    {
        ProductHeading = heading;
        ProductMessage = message;
        HasProductAction = hasAction;
        ProductActionLabel = actionLabel;
        HasDownloadProgress = hasDownloadProgress;
        IsDownloadProgressIndeterminate = hasDownloadProgress && progress is null;
        DownloadProgress = progress ?? 0;
        IsProductNoticeVisible = true;
        UpdateAutomationStatus();
    }

    private static string FormatPermissions(BrowserPermissionKind permissions) =>
        permissions == BrowserPermissionKind.None
            ? "an unspecified browser permission"
            : permissions.ToString().ToLowerInvariant();

    private static string FormatDownloadProgress(long received, long? total)
    {
        static string Megabytes(long bytes) => $"{bytes / 1_048_576d:0.0} MB";
        return total is { } totalBytes && totalBytes > 0
            ? $"Received {Megabytes(received)} of {Megabytes(totalBytes)}."
            : $"Received {Megabytes(received)}.";
    }

    private void UpdateAutomationStatus() =>
        AutomationProperties.SetItemStatus(
            this,
            string.IsNullOrWhiteSpace(StatusMessage)
                ? StatusText
                : $"{StatusText}: {StatusMessage}");

    private ViewportDescriptor CurrentViewport() =>
        new(
            Bounds.Width,
            Bounds.Height,
            TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);

    private OperationContext NewContext() => OperationContext.ForHuman(
        _attachedClientId
        ?? ClientId
        ?? throw new InvalidOperationException(
            "The desktop client identity is unavailable."));

    private ISessionHostClient RequireAttachedClient() =>
        _attachedClient
        ?? throw new InvalidOperationException("The browser session is not attached.");

    private SessionId RequireAttachedSessionId() =>
        _attachedSessionId
        ?? throw new InvalidOperationException("The browser session is not attached.");

    private static bool TryResolveAddress(
        string? text,
        out BrowserAddress address)
    {
        if (BrowserAddress.TryParse(text, out var exact))
        {
            address = exact;
            return true;
        }

        var candidate = text?.Trim();
        if (!string.IsNullOrWhiteSpace(candidate)
            && candidate.Length <= BrowserAddress.MaximumLength - "https://".Length
            && !candidate.Contains("://", StringComparison.Ordinal)
            && BrowserAddress.TryParse($"https://{candidate}", out var https))
        {
            address = https;
            return true;
        }

        address = BrowserAddress.Blank;
        return false;
    }

    internal static bool TryGetSystemBrowserAddress(
        BrowserAddress? presentedAddress,
        out Uri address)
    {
        if (presentedAddress is { IsLocalFile: false, Document: null }
            && (presentedAddress.Value.Scheme.Equals(
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase)
                || presentedAddress.Value.Scheme.Equals(
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)))
        {
            address = presentedAddress.Value;
            return true;
        }

        address = null!;
        return false;
    }

    private static T RequireSuccess<T>(HostResult<T> result) => result switch
    {
        HostResult<T>.Success success => success.Value,
        HostResult<T>.Failure failure => throw new InvalidOperationException(
            $"{failure.Error.StableCode}: {failure.Error.Message}"),
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

}
