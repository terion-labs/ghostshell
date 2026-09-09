using Asura.Application;
using Avalonia.Controls;

namespace Asura.Browser;

/// <summary>
/// Keeps the vendor control behind one small, testable boundary. Browser state
/// published outside this project uses only Asura application contracts.
/// </summary>
internal interface IEmbeddedBrowserView : IDisposable
{
    Control View { get; }

    bool CanGoBack { get; }

    bool CanGoForward { get; }

    /// <summary>
    /// True only when the view's transport binds policy admission to the
    /// actual connected peer for every redirect and subresource.
    /// </summary>
    bool SupportsPeerBoundTransport { get; }

    void SetAgentActivity(bool isActive);

    /// <summary>
    /// Applies an asynchronous destination check to the currently active
    /// main-frame navigation before its network request is released.
    /// </summary>
    void SetActiveNavigationRequestPolicy(
        Func<BrowserAddress, CancellationToken, ValueTask<bool>> policy);

    /// <summary>
    /// Applies an asynchronous destination check to every network resource
    /// requested by this view, including subframes, scripts, images, and fetch.
    /// </summary>
    void SetResourceRequestPolicy(
        Func<BrowserAddress, CancellationToken, ValueTask<bool>> policy);

    event EventHandler<NativeBrowserNavigationEventArgs>? NavigationStarted;

    event EventHandler<NativeBrowserNavigationCompletedEventArgs>? NavigationCompleted;

    /// <summary>
    /// Raised when the main document changes its visible address without
    /// committing a different document, for example via history.pushState.
    /// </summary>
    event EventHandler<NativeBrowserAddressChangedEventArgs>? AddressChanged;

    event EventHandler<NativeBrowserNavigationRejectedEventArgs>?
        NavigationRejected;

    /// <summary>
    /// Raised after Chromium's renderer process terminates unexpectedly. The
    /// owner must replace this view; continuing to use a frozen OSR surface
    /// would make the visible browser disagree with its session state.
    /// </summary>
    event EventHandler? RenderProcessFailed;

    event EventHandler<BrowserNewTabRequestedEventArgs>? NewTabRequested;

    event EventHandler<BrowserProductEvent>? ProductEvent;

    void Navigate(BrowserAddress address);

    bool GoBack();

    bool GoForward();

    bool Reload();

    bool Stop();

    bool OpenDeveloperTools();

    bool StartFind(string searchText);

    bool FindNext(BrowserFindDirection direction);

    bool StopFind();

    Task<NativeBrowserSnapshotResult> CaptureSnapshotAsync(
        BrowserSnapshotQuery? query = null);

    Task<NativeBrowserClickResult> ClickAsync(
        NativeBrowserElementHandle handle);

    Task<NativeBrowserFillResult> FillAsync(
        NativeBrowserElementHandle handle,
        string text);

    Task<NativeBrowserCheckResult> CheckAsync(
        NativeBrowserElementHandle handle);

    Task<NativeBrowserElementStateResult> ReadElementStateAsync(
        NativeBrowserElementHandle handle);

    void BeginNetworkActivityObservation();

    void EndNetworkActivityObservation();

    NativeBrowserNetworkActivity ReadNetworkActivity();

    Task<NativeBrowserViewport> ReadViewportAsync();

    Task<NativeBrowserAutomationResult> DispatchMouseAsync(BrowserMouseRequest request);

    Task<NativeBrowserAutomationResult> DispatchKeyAsync(BrowserKeyRequest request);

    Task<NativeBrowserAutomationResult> DispatchScrollAsync(BrowserScrollRequest request);

    Task<NativeBrowserAutomationResult> EvaluateAsync(BrowserEvaluateRequest request);

    Task<NativeBrowserAutomationResult> ExtractWebSearchDocumentAsync(
        int maximumResults);

    Task<NativeBrowserAutomationResult> ExtractReadableArticleAsync();

    Task<NativeBrowserAutomationResult> ExtractRenderedDocumentAsync();
}

internal sealed class NativeBrowserAddressChangedEventArgs(
    BrowserAddress address) : EventArgs
{
    public BrowserAddress Address { get; } =
        address ?? throw new ArgumentNullException(nameof(address));
}

internal sealed class NativeBrowserNavigationEventArgs(
    BrowserAddress address,
    long navigationGeneration) : EventArgs
{
    public BrowserAddress Address { get; } =
        address ?? throw new ArgumentNullException(nameof(address));

    public long NavigationGeneration { get; } =
        navigationGeneration > 0
            ? navigationGeneration
            : throw new ArgumentOutOfRangeException(
                nameof(navigationGeneration));

    public bool Cancel { get; set; }
}

internal sealed class NativeBrowserNavigationCompletedEventArgs(
    BrowserAddress? address,
    bool isSuccess,
    long navigationGeneration,
    bool wasStopped = false,
    NativeBrowserLoadFailureKind failureKind = NativeBrowserLoadFailureKind.None) : EventArgs
{
    public BrowserAddress? Address { get; } = address;

    public bool IsSuccess { get; } = wasStopped && isSuccess
        ? throw new ArgumentException(
            "A stopped navigation cannot also be successful.",
            nameof(isSuccess))
        : isSuccess;

    /// <summary>
    /// The host explicitly requested Stop and CEF acknowledged it with this
    /// terminal event. This is neither a committed document nor a load error.
    /// </summary>
    public bool WasStopped { get; } = wasStopped;

    public NativeBrowserLoadFailureKind FailureKind { get; } =
        isSuccess || wasStopped
            ? NativeBrowserLoadFailureKind.None
            : Enum.IsDefined(failureKind)
                ? failureKind
                : throw new ArgumentOutOfRangeException(nameof(failureKind));

    public long NavigationGeneration { get; } =
        navigationGeneration > 0
            ? navigationGeneration
            : throw new ArgumentOutOfRangeException(
                nameof(navigationGeneration));
}

internal enum NativeBrowserLoadFailureKind
{
    None,
    NetworkUnavailable,
    TimedOut,
    CertificateRejected,
    Other,
}

internal sealed class NativeBrowserNavigationRejectedEventArgs(
    NativeBrowserNavigationRejectionReason reason,
    long navigationGeneration) : EventArgs
{
    public NativeBrowserNavigationRejectionReason Reason { get; } =
        Enum.IsDefined(reason)
            ? reason
            : throw new ArgumentOutOfRangeException(nameof(reason));

    public long NavigationGeneration { get; } =
        navigationGeneration > 0
            ? navigationGeneration
            : throw new ArgumentOutOfRangeException(
                nameof(navigationGeneration));
}

internal enum NativeBrowserNavigationRejectionReason
{
    UnsupportedAddress,
    OriginPolicy,
}
