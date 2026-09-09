namespace Asura.Application;

/// <summary>
/// Closed, engine-neutral events that require visible browser chrome. Native
/// browser types must not cross this boundary.
/// </summary>
public abstract record BrowserProductEvent
{
    private BrowserProductEvent()
    {
    }

    public sealed record JavaScriptDialogBlocked(
        BrowserJavaScriptDialogKind Kind,
        string Message) : BrowserProductEvent;

    public sealed record FileDialogBlocked(
        BrowserFileDialogKind Kind,
        string Title) : BrowserProductEvent;

    public sealed record PermissionDenied(
        string Origin,
        BrowserPermissionKind Permissions) : BrowserProductEvent;

    public sealed record CertificateRejected(
        BrowserAddress Address,
        BrowserCertificateErrorKind Error,
        string Subject,
        string Issuer) : BrowserProductEvent;

    public sealed record DownloadRequested(
        int DownloadId,
        string FileName,
        long? TotalBytes) : BrowserProductEvent;

    public sealed record DownloadProgressed(
        int DownloadId,
        string FileName,
        long ReceivedBytes,
        long? TotalBytes,
        int? PercentComplete) : BrowserProductEvent;

    public sealed record DownloadCompleted(
        int DownloadId,
        string FileName) : BrowserProductEvent;

    public sealed record DownloadCancelled(
        int DownloadId) : BrowserProductEvent;

    public sealed record FindUpdated(
        int MatchCount,
        int ActiveMatchOrdinal,
        bool IsFinal) : BrowserProductEvent;

    /// <summary>
    /// Chromium was replaced with a fresh renderer. The previous page address
    /// is offered only as a reload target; no volatile page state was restored.
    /// </summary>
    public sealed record RendererRecovered(
        BrowserAddress LostAddress) : BrowserProductEvent;

    public sealed record RendererFailed(
        BrowserAddress LastAddress) : BrowserProductEvent;
}

public interface IBrowserProductEventSource
{
    event EventHandler<BrowserProductEvent>? ProductEvent;
}

public interface IBrowserFindController
{
    bool StartFind(string searchText);

    bool FindNext(BrowserFindDirection direction);

    bool StopFind();
}

public enum BrowserFindDirection
{
    Previous,
    Next,
}

public enum BrowserJavaScriptDialogKind
{
    Alert,
    Confirmation,
    Prompt,
    BeforeUnload,
}

public enum BrowserFileDialogKind
{
    OpenFile,
    OpenFiles,
    OpenFolder,
    SaveFile,
}

[Flags]
public enum BrowserPermissionKind
{
    None = 0,
    Camera = 1 << 0,
    Microphone = 1 << 1,
    ScreenCapture = 1 << 2,
    Location = 1 << 3,
    Notifications = 1 << 4,
    Clipboard = 1 << 5,
    FileSystem = 1 << 6,
    Storage = 1 << 7,
    Device = 1 << 8,
    Other = 1 << 9,
}

public enum BrowserCertificateErrorKind
{
    NameMismatch,
    ExpiredOrNotYetValid,
    UntrustedAuthority,
    Revoked,
    Invalid,
}
