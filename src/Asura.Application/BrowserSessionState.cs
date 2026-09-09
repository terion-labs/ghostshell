namespace Asura.Application;

/// <summary>
/// The engine-neutral state needed to render browser chrome.
/// </summary>
public sealed record BrowserSessionState
{
    public const int MaximumTitleLength = 1_024;

    public BrowserSessionState(
        BrowserAddress address,
        string title,
        BrowserLoadState loadState,
        bool canGoBack,
        bool canGoForward,
        long documentRevision,
        BrowserError? failure = null,
        BrowserViewportState? viewport = null,
        long viewportRevision = 0,
        long inputEpoch = 0)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(title);
        if (!Enum.IsDefined(loadState))
        {
            throw new ArgumentOutOfRangeException(nameof(loadState));
        }

        if (title.Length > MaximumTitleLength
            || title.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A browser title must be NUL-free and at most {MaximumTitleLength} characters.",
                nameof(title));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(documentRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(viewportRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(inputEpoch);
        if ((loadState == BrowserLoadState.Failed) != (failure is not null))
        {
            throw new ArgumentException(
                "A failed browser load must carry an error, and other load states cannot carry one.",
                nameof(failure));
        }

        Address = address;
        Title = title;
        LoadState = loadState;
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        DocumentRevision = documentRevision;
        Failure = failure;
        Viewport = viewport ?? BrowserViewportState.Empty;
        ViewportRevision = viewportRevision;
        InputEpoch = inputEpoch;
    }

    public BrowserAddress Address { get; }

    public string Title { get; }

    public BrowserLoadState LoadState { get; }

    public bool CanGoBack { get; }

    public bool CanGoForward { get; }

    /// <summary>
    /// Advances when the renderer commits a different document. Future
    /// short-lived element references bind to this revision.
    /// </summary>
    public long DocumentRevision { get; }

    public BrowserError? Failure { get; }

    public BrowserViewportState Viewport { get; }

    /// <summary>Advances whenever the CSS viewport geometry or scale changes.</summary>
    public long ViewportRevision { get; }

    /// <summary>Advances after accepted human or acknowledged agent input.</summary>
    public long InputEpoch { get; }

    public static BrowserSessionState Initial(BrowserAddress address) =>
        new(address, string.Empty, BrowserLoadState.Ready, false, false, 0);
}
