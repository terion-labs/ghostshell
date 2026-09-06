using Avalonia.Controls;
using GhostShell.Application;

namespace GhostShell.Browser.Tests;

internal sealed class RecordingEmbeddedBrowserView : IEmbeddedBrowserView
{
    private long? _activeNavigationGeneration;
    private long _lastNavigationGeneration;
    private Func<
        BrowserAddress,
        CancellationToken,
        ValueTask<bool>>? _activeNavigationRequestPolicy;
    private Func<
        BrowserAddress,
        CancellationToken,
        ValueTask<bool>>? _resourceRequestPolicy;

    public Control View { get; } = new Border();

    public bool CanGoBack { get; set; }

    public bool CanGoForward { get; set; }

    public bool SupportsPeerBoundTransport { get; set; } = true;

    public bool IsAgentActive { get; private set; }

    public bool IsDisposed { get; private set; }

    public bool AcceptBack { get; set; }

    public bool AcceptForward { get; set; }

    public bool AcceptReload { get; set; }

    public bool AcceptStop { get; set; }

    public bool AcceptDeveloperTools { get; set; } = true;

    public bool ThrowOnNavigate { get; set; }

    public bool ThrowOnReload { get; set; }

    public bool ThrowOnSnapshot { get; set; }

    public bool ThrowOnClick { get; set; }

    public bool ThrowOnFill { get; set; }

    public bool ThrowOnCheck { get; set; }

    public BrowserAddress? NavigatedAddress { get; private set; }

    public int NavigateCount { get; private set; }

    public int StopCount { get; private set; }

    public int ReloadCount { get; private set; }

    public int DeveloperToolsOpenCount { get; private set; }

    public string? LastFindText { get; private set; }

    public BrowserFindDirection? LastFindDirection { get; private set; }

    public int StopFindCount { get; private set; }

    public int SnapshotCount { get; private set; }

    public BrowserSnapshotQuery? LastSnapshotQuery { get; private set; }

    public int ClickCount { get; private set; }

    public int FillCount { get; private set; }

    public int CheckCount { get; private set; }

    public NativeBrowserElementHandle? LastClickHandle { get; private set; }

    public NativeBrowserElementHandle? LastFillHandle { get; private set; }

    public NativeBrowserElementHandle? LastCheckHandle { get; private set; }

    public string? LastFillText { get; private set; }

    public TaskCompletionSource<NativeBrowserSnapshotResult>?
        PendingSnapshot
    { get; set; }

    public NativeBrowserSnapshotResult SnapshotResult { get; set; } =
        NativeBrowserSnapshotResult.Success(
            new NativeBrowserSnapshot(
                [
                    new NativeBrowserSnapshotNode(
                        0,
                        "document",
                        "Example",
                        BrowserSnapshotNodeState.None,
                        Handle: null),
                ],
                IsTruncated: false));

    public TaskCompletionSource<NativeBrowserClickResult>? PendingClick
    { get; set; }

    public NativeBrowserClickResult ClickResult { get; set; } =
        NativeBrowserClickResult.Activated();

    public TaskCompletionSource<NativeBrowserFillResult>? PendingFill
    { get; set; }

    public NativeBrowserFillResult FillResult { get; set; } =
        NativeBrowserFillResult.Filled();

    public TaskCompletionSource<NativeBrowserCheckResult>? PendingCheck
    { get; set; }

    public NativeBrowserCheckResult CheckResult { get; set; } =
        NativeBrowserCheckResult.Checked();

    public NativeBrowserElementStateResult ElementStateResult { get; set; } =
        NativeBrowserElementStateResult.Success(
            new NativeBrowserElementState(
                Visible: true,
                Enabled: true,
                Checked: false,
                Selected: false,
                Editable: true,
                Focused: false));

    public NativeBrowserNetworkActivity NetworkActivity { get; set; } =
        new(
            IsObservable: true,
            ActiveRequestCount: 0,
            QuietFor: TimeSpan.FromSeconds(1));

    public int BeginNetworkActivityObservationCount { get; private set; }

    public int EndNetworkActivityObservationCount { get; private set; }

    public NativeBrowserViewport Viewport { get; set; } = new(800, 600);

    public NativeBrowserAutomationResult AutomationResult { get; set; } =
        NativeBrowserAutomationResult.Acknowledged();

    public NativeBrowserAutomationResult EvaluationResult { get; set; } =
        NativeBrowserAutomationResult.Acknowledged("null");

    public TaskCompletionSource<NativeBrowserAutomationResult>? PendingAutomation
    { get; set; }

    public BrowserMouseRequest? LastMouseRequest { get; private set; }

    public BrowserKeyRequest? LastKeyRequest { get; private set; }

    public BrowserScrollRequest? LastScrollRequest { get; private set; }

    public BrowserEvaluateRequest? LastEvaluateRequest { get; private set; }

    public long LastNavigationGeneration => _lastNavigationGeneration;

    public event EventHandler<NativeBrowserNavigationEventArgs>? NavigationStarted;

    public event EventHandler<NativeBrowserNavigationCompletedEventArgs>? NavigationCompleted;

    public event EventHandler<NativeBrowserAddressChangedEventArgs>? AddressChanged;

    public event EventHandler<NativeBrowserNavigationRejectedEventArgs>?
        NavigationRejected;

    public event EventHandler? RenderProcessFailed;

    public event EventHandler<BrowserNewTabRequestedEventArgs>? NewTabRequested;

    public event EventHandler<BrowserProductEvent>? ProductEvent;

    public void SetAgentActivity(bool isActive) => IsAgentActive = isActive;

    public void SetActiveNavigationRequestPolicy(
        Func<BrowserAddress, CancellationToken, ValueTask<bool>> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (_activeNavigationGeneration is null)
        {
            throw new InvalidOperationException(
                "The fake browser has no active navigation to protect.");
        }

        _activeNavigationRequestPolicy = policy;
    }

    public ValueTask<bool> AllowsActiveNavigationRequestAsync(
        BrowserAddress address,
        CancellationToken cancellationToken = default) =>
        (_activeNavigationRequestPolicy
            ?? throw new InvalidOperationException(
                "The fake browser has no active navigation request policy."))(
            address,
            cancellationToken);

    public void SetResourceRequestPolicy(
        Func<BrowserAddress, CancellationToken, ValueTask<bool>> policy) =>
        _resourceRequestPolicy = policy ?? throw new ArgumentNullException(nameof(policy));

    public ValueTask<bool> AllowsResourceRequestAsync(
        BrowserAddress address,
        CancellationToken cancellationToken = default) =>
        (_resourceRequestPolicy
            ?? throw new InvalidOperationException(
                "The fake browser has no resource request policy."))(
            address,
            cancellationToken);

    public void Dispose() => IsDisposed = true;

    public void Navigate(BrowserAddress address)
    {
        if (ThrowOnNavigate)
        {
            throw new InvalidOperationException("vendor details must stay inside the engine");
        }

        NavigateCount++;
        NavigatedAddress = address;
        BeginNavigation();
    }

    public bool GoBack()
    {
        if (AcceptBack)
        {
            BeginNavigation();
        }

        return AcceptBack;
    }

    public bool GoForward()
    {
        if (AcceptForward)
        {
            BeginNavigation();
        }

        return AcceptForward;
    }

    public bool Reload()
    {
        if (ThrowOnReload)
        {
            throw new InvalidOperationException(
                "vendor reload details must stay inside the engine");
        }

        ReloadCount++;
        if (AcceptReload)
        {
            BeginNavigation();
        }

        return AcceptReload;
    }

    public bool Stop()
    {
        StopCount++;
        return AcceptStop;
    }

    public bool OpenDeveloperTools()
    {
        DeveloperToolsOpenCount++;
        return AcceptDeveloperTools;
    }

    public bool StartFind(string searchText)
    {
        LastFindText = searchText;
        LastFindDirection = BrowserFindDirection.Next;
        return true;
    }

    public bool FindNext(BrowserFindDirection direction)
    {
        LastFindDirection = direction;
        return LastFindText is not null;
    }

    public bool StopFind()
    {
        StopFindCount++;
        LastFindText = null;
        return true;
    }

    public Task<NativeBrowserSnapshotResult> CaptureSnapshotAsync(
        BrowserSnapshotQuery? query = null)
    {
        SnapshotCount++;
        LastSnapshotQuery = query;
        if (ThrowOnSnapshot)
        {
            throw new InvalidOperationException(
                "vendor snapshot details must stay private");
        }

        return PendingSnapshot?.Task
            ?? Task.FromResult(SnapshotResult);
    }

    public Task<NativeBrowserClickResult> ClickAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ClickCount++;
        LastClickHandle = handle;
        if (ThrowOnClick)
        {
            throw new InvalidOperationException(
                "vendor click details must stay private");
        }

        return PendingClick?.Task
            ?? Task.FromResult(ClickResult);
    }

    public Task<NativeBrowserFillResult> FillAsync(
        NativeBrowserElementHandle handle,
        string text)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(text);
        FillCount++;
        LastFillHandle = handle;
        LastFillText = text;
        if (ThrowOnFill)
        {
            throw new InvalidOperationException(
                "vendor fill details must stay private");
        }

        return PendingFill?.Task
            ?? Task.FromResult(FillResult);
    }

    public Task<NativeBrowserCheckResult> CheckAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        CheckCount++;
        LastCheckHandle = handle;
        if (ThrowOnCheck)
        {
            throw new InvalidOperationException(
                "vendor check details must stay private");
        }

        return PendingCheck?.Task
            ?? Task.FromResult(CheckResult);
    }

    public Task<NativeBrowserElementStateResult> ReadElementStateAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return Task.FromResult(ElementStateResult);
    }

    public void BeginNetworkActivityObservation() =>
        BeginNetworkActivityObservationCount++;

    public void EndNetworkActivityObservation() =>
        EndNetworkActivityObservationCount++;

    public NativeBrowserNetworkActivity ReadNetworkActivity() =>
        NetworkActivity;

    public Task<NativeBrowserViewport> ReadViewportAsync() =>
        Task.FromResult(Viewport);

    public Task<NativeBrowserAutomationResult> DispatchMouseAsync(
        BrowserMouseRequest request)
    {
        LastMouseRequest = request;
        return PendingAutomation?.Task ?? Task.FromResult(AutomationResult);
    }

    public Task<NativeBrowserAutomationResult> DispatchKeyAsync(
        BrowserKeyRequest request)
    {
        LastKeyRequest = request;
        return PendingAutomation?.Task ?? Task.FromResult(AutomationResult);
    }

    public Task<NativeBrowserAutomationResult> DispatchScrollAsync(
        BrowserScrollRequest request)
    {
        LastScrollRequest = request;
        return PendingAutomation?.Task ?? Task.FromResult(AutomationResult);
    }

    public Task<NativeBrowserAutomationResult> EvaluateAsync(
        BrowserEvaluateRequest request)
    {
        LastEvaluateRequest = request;
        return PendingAutomation?.Task ?? Task.FromResult(EvaluationResult);
    }

    public Task<NativeBrowserAutomationResult> ExtractWebSearchDocumentAsync(
        int maximumResults) =>
        PendingAutomation?.Task ?? Task.FromResult(EvaluationResult);

    public Task<NativeBrowserAutomationResult> ExtractReadableArticleAsync() =>
        PendingAutomation?.Task ?? Task.FromResult(EvaluationResult);

    public Task<NativeBrowserAutomationResult> ExtractRenderedDocumentAsync() =>
        PendingAutomation?.Task ?? Task.FromResult(EvaluationResult);

    public bool RaiseNavigationStarted(
        BrowserAddress address,
        long? navigationGeneration = null)
    {
        var generation = navigationGeneration
            ?? _activeNavigationGeneration
            ?? BeginNavigation();
        var args = new NativeBrowserNavigationEventArgs(
            address,
            generation);
        NavigationStarted?.Invoke(this, args);
        if (args.Cancel)
        {
            NavigationRejected?.Invoke(
                this,
                new NativeBrowserNavigationRejectedEventArgs(
                    NativeBrowserNavigationRejectionReason.OriginPolicy,
                    generation));
        }

        return args.Cancel;
    }

    public void RaiseNavigationCompleted(
        BrowserAddress? address,
        bool isSuccess,
        long? navigationGeneration = null,
        bool wasStopped = false,
        NativeBrowserLoadFailureKind failureKind =
            NativeBrowserLoadFailureKind.None)
    {
        var generation = navigationGeneration
            ?? _activeNavigationGeneration
            ?? throw new InvalidOperationException(
                "No native navigation generation is active.");
        if (_activeNavigationGeneration == generation)
        {
            _activeNavigationGeneration = null;
        }

        NavigationCompleted?.Invoke(
            this,
            new NativeBrowserNavigationCompletedEventArgs(
                address,
                isSuccess,
                generation,
                wasStopped,
                failureKind));
    }

    public void RaiseNavigationRejected(
        NativeBrowserNavigationRejectionReason reason =
            NativeBrowserNavigationRejectionReason.UnsupportedAddress)
    {
        var generation = _activeNavigationGeneration
            ?? BeginNavigation();
        NavigationRejected?.Invoke(
            this,
            new NativeBrowserNavigationRejectedEventArgs(
                reason,
                generation));
    }

    public void RaiseRenderProcessFailed() =>
        RenderProcessFailed?.Invoke(this, EventArgs.Empty);

    public void RaiseNewTabRequested(
        BrowserAddress address,
        bool userGesture = true) =>
        NewTabRequested?.Invoke(
            this,
            new BrowserNewTabRequestedEventArgs(address, userGesture));

    public void RaiseProductEvent(BrowserProductEvent productEvent) =>
        ProductEvent?.Invoke(this, productEvent);

    public void RaiseAddressChanged(BrowserAddress address) =>
        AddressChanged?.Invoke(
            this,
            new NativeBrowserAddressChangedEventArgs(address));

    public Action CaptureNavigationCompletedCallback(
        BrowserAddress? address,
        bool isSuccess,
        long navigationGeneration)
    {
        var capturedHandlers = NavigationCompleted;
        return () => capturedHandlers?.Invoke(
            this,
            new NativeBrowserNavigationCompletedEventArgs(
                address,
                isSuccess,
                navigationGeneration));
    }

    private long BeginNavigation()
    {
        if (_activeNavigationGeneration is not null)
        {
            throw new InvalidOperationException(
                "A native navigation generation is already active.");
        }

        _activeNavigationRequestPolicy = null;
        _lastNavigationGeneration = checked(
            _lastNavigationGeneration + 1);
        _activeNavigationGeneration = _lastNavigationGeneration;
        return _lastNavigationGeneration;
    }
}
