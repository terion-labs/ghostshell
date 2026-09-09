namespace Asura.Application;

/// <summary>
/// A platform browser surface. Vendor controls and handles remain behind this
/// Asura-owned contract.
/// </summary>
public interface IBrowserRenderer :
    IBrowserNavigation,
    IOriginConstrainedBrowserNavigation,
    IOriginConstrainedBrowserElementClick,
    IOriginConstrainedBrowserElementFill,
    IOriginConstrainedBrowserElementCheck,
    IOriginConstrainedBrowserAutomation,
    IBrowserDocumentReader,
    IBrowserWaitObservation
{
    CapabilitySet Capabilities { get; }

    /// <remarks>
    /// Renderers may raise this event on their platform UI thread. Subscribers
    /// must return promptly and marshal longer-running work themselves.
    /// </remarks>
    event EventHandler<BrowserStateChangedEventArgs>? StateChanged;

}

/// <summary>
/// An interactive renderer that can ask its host shell for another browsing
/// surface. Headless and test renderers do not need to implement this optional
/// presentation capability.
/// </summary>
public interface IBrowserNewTabRequestSource
{
    event EventHandler<BrowserNewTabRequestedEventArgs>? NewTabRequested;
}

public sealed class BrowserNewTabRequestedEventArgs(
    BrowserAddress address,
    bool userGesture) : EventArgs
{
    public BrowserAddress Address { get; } =
        address ?? throw new ArgumentNullException(nameof(address));

    public bool UserGesture { get; } = userGesture;
}
