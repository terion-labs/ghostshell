using Exclr8Cef;
using GhostShell.Application;

namespace GhostShell.Browser;

internal sealed partial class CefBrowserView
{
    private const int MaximumProductTextLength = 512;
    private readonly BrowserProductEventDispatch _productEvents;
    private string? _activeFindText;
    private readonly Dictionary<int, string> _downloadFileNames = [];

    public bool StartFind(string searchText)
    {
        ArgumentNullException.ThrowIfNull(searchText);
        if (_disposed
            || string.IsNullOrWhiteSpace(searchText)
            || _browser is not { IsInitialized: true } browser)
        {
            return false;
        }

        _activeFindText = searchText;
        browser.Find(searchText, forward: true, matchCase: false, findNext: false);
        return true;
    }

    public bool FindNext(BrowserFindDirection direction)
    {
        if (_disposed
            || !Enum.IsDefined(direction)
            || _activeFindText is not { } searchText
            || _browser is not { IsInitialized: true } browser)
        {
            return false;
        }

        browser.Find(
            searchText,
            forward: direction == BrowserFindDirection.Next,
            matchCase: false,
            findNext: true);
        return true;
    }

    public bool StopFind()
    {
        _activeFindText = null;
        if (_disposed || _browser is not { IsInitialized: true } browser)
        {
            return false;
        }

        browser.StopFinding(clearSelection: true);
        return true;
    }

    private void BlockJavaScriptDialog(object? sender, JsDialogEventArgs args)
    {
        _ = sender;
        args.Cancel();
        PublishProductEvent(new BrowserProductEvent.JavaScriptDialogBlocked(
            args.Type switch
            {
                Cef.JsDialogType.Alert => BrowserJavaScriptDialogKind.Alert,
                Cef.JsDialogType.Confirm => BrowserJavaScriptDialogKind.Confirmation,
                Cef.JsDialogType.Prompt => BrowserJavaScriptDialogKind.Prompt,
                Cef.JsDialogType.BeforeUnload => BrowserJavaScriptDialogKind.BeforeUnload,
                _ => BrowserJavaScriptDialogKind.Alert,
            },
            Bound(args.Message)));
    }

    private void BlockFileDialog(object? sender, FileDialogEventArgs args)
    {
        _ = sender;
        args.Cancel();
        PublishProductEvent(new BrowserProductEvent.FileDialogBlocked(
            args.Mode switch
            {
                Cef.FileDialogMode.Open => BrowserFileDialogKind.OpenFile,
                Cef.FileDialogMode.OpenMultiple => BrowserFileDialogKind.OpenFiles,
                Cef.FileDialogMode.OpenFolder => BrowserFileDialogKind.OpenFolder,
                Cef.FileDialogMode.Save => BrowserFileDialogKind.SaveFile,
                _ => BrowserFileDialogKind.OpenFile,
            },
            Bound(args.Title)));
    }

    private void OnDownloadStarting(object? sender, DownloadStartingEventArgs args)
    {
        _ = sender;
        var fileName = SafeDownloadFileName(args.SuggestedName, args.DownloadId);
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            args.Continue(Path.Combine(downloads, fileName), showDialog: true);
            _downloadFileNames[args.DownloadId] = fileName;
            PublishProductEvent(new BrowserProductEvent.DownloadRequested(
                args.DownloadId,
                fileName,
                args.TotalBytes >= 0 ? args.TotalBytes : null));
        }
        catch
        {
            args.Cancel();
            PublishProductEvent(new BrowserProductEvent.DownloadCancelled(
                args.DownloadId));
        }
    }

    private void OnDownloadProgress(object? sender, DownloadProgressEventArgs args)
    {
        _ = sender;
        var fileName = _downloadFileNames.GetValueOrDefault(
            args.DownloadId,
            $"download-{args.DownloadId}");
        BrowserProductEvent productEvent = args.State switch
        {
            Cef.DownloadState.InProgress =>
                new BrowserProductEvent.DownloadProgressed(
                    args.DownloadId,
                    fileName,
                    args.ReceivedBytes,
                    args.TotalBytes >= 0 ? args.TotalBytes : null,
                    args.PercentComplete >= 0 ? args.PercentComplete : null),
            Cef.DownloadState.Complete =>
                new BrowserProductEvent.DownloadCompleted(
                    args.DownloadId,
                    string.IsNullOrWhiteSpace(args.FullPath)
                        ? fileName
                        : Bound(Path.GetFileName(args.FullPath))),
            Cef.DownloadState.Canceled =>
                new BrowserProductEvent.DownloadCancelled(args.DownloadId),
            _ => new BrowserProductEvent.DownloadCancelled(args.DownloadId),
        };
        if (args.State is not Cef.DownloadState.InProgress)
        {
            _downloadFileNames.Remove(args.DownloadId);
        }

        PublishProductEvent(productEvent);
    }

    private void OnFindResult(object? sender, FindResultEventArgs args)
    {
        _ = sender;
        PublishProductEvent(new BrowserProductEvent.FindUpdated(
            args.Count,
            args.ActiveMatchOrdinal,
            args.FinalUpdate));
    }

    private void BlockPermission(object? sender, PermissionRequestEventArgs args)
    {
        _ = sender;
        args.Deny();
        PublishProductEvent(new BrowserProductEvent.PermissionDenied(
            Bound(args.Origin),
            MapPermissions(args.RequestedPermissions)));
    }

    private void BlockMediaAccess(object? sender, MediaAccessRequestEventArgs args)
    {
        _ = sender;
        args.Deny();
        var permissions = BrowserPermissionKind.None;
        if (args.RequestedPermissions.HasFlag(Cef.MediaAccessPermissions.DeviceAudioCapture))
        {
            permissions |= BrowserPermissionKind.Microphone;
        }

        if (args.RequestedPermissions.HasFlag(Cef.MediaAccessPermissions.DeviceVideoCapture))
        {
            permissions |= BrowserPermissionKind.Camera;
        }

        if ((args.RequestedPermissions
                & (Cef.MediaAccessPermissions.DesktopAudioCapture
                    | Cef.MediaAccessPermissions.DesktopVideoCapture)) != 0)
        {
            permissions |= BrowserPermissionKind.ScreenCapture;
        }

        PublishProductEvent(new BrowserProductEvent.PermissionDenied(
            Bound(args.Origin),
            permissions == BrowserPermissionKind.None
                ? BrowserPermissionKind.Other
                : permissions));
    }

    private void BlockCertificateError(object? sender, CertErrorEventArgs args)
    {
        _ = sender;
        args.Cancel();
        if (!BrowserAddress.TryParse(args.RequestUrl, out var address))
        {
            address = BrowserAddress.Blank;
        }

        PublishProductEvent(new BrowserProductEvent.CertificateRejected(
            address,
            MapCertificateError(args.ErrorCode),
            Bound(args.SubjectCommonName),
            Bound(args.IssuerCommonName)));
    }

    private void PublishProductEvent(BrowserProductEvent productEvent) =>
        _productEvents.Publish(productEvent);

    internal static NativeBrowserLoadFailureKind MapLoadFailure(
        Cef.CefErrorCode errorCode) => errorCode switch
        {
            Cef.CefErrorCode.TimedOut => NativeBrowserLoadFailureKind.TimedOut,
            Cef.CefErrorCode.InternetDisconnected
                or Cef.CefErrorCode.NameNotResolved
                or Cef.CefErrorCode.AddressUnreachable
                or Cef.CefErrorCode.ConnectionFailed
                or Cef.CefErrorCode.ConnectionClosed
                or Cef.CefErrorCode.ConnectionReset
                or Cef.CefErrorCode.ConnectionRefused
                or Cef.CefErrorCode.TunnelConnectionFailed =>
                    NativeBrowserLoadFailureKind.NetworkUnavailable,
            Cef.CefErrorCode.CertCommonNameInvalid
                or Cef.CefErrorCode.CertDateInvalid
                or Cef.CefErrorCode.CertAuthorityInvalid
                or Cef.CefErrorCode.CertContainsErrors
                or Cef.CefErrorCode.CertNoRevocationMechanism
                or Cef.CefErrorCode.CertUnableToCheckRevocation
                or Cef.CefErrorCode.CertRevoked
                or Cef.CefErrorCode.CertInvalid =>
                    NativeBrowserLoadFailureKind.CertificateRejected,
            _ => NativeBrowserLoadFailureKind.Other,
        };

    internal static BrowserCertificateErrorKind MapCertificateError(
        Cef.CefErrorCode errorCode) => errorCode switch
        {
            Cef.CefErrorCode.CertCommonNameInvalid =>
                BrowserCertificateErrorKind.NameMismatch,
            Cef.CefErrorCode.CertDateInvalid =>
                BrowserCertificateErrorKind.ExpiredOrNotYetValid,
            Cef.CefErrorCode.CertAuthorityInvalid =>
                BrowserCertificateErrorKind.UntrustedAuthority,
            Cef.CefErrorCode.CertRevoked => BrowserCertificateErrorKind.Revoked,
            _ => BrowserCertificateErrorKind.Invalid,
        };

    internal static BrowserPermissionKind MapPermissions(
        Cef.PermissionRequestType permissions)
    {
        var result = BrowserPermissionKind.None;
        if ((permissions & (Cef.PermissionRequestType.CameraPanTiltZoom
                | Cef.PermissionRequestType.CameraStream)) != 0)
        {
            result |= BrowserPermissionKind.Camera;
        }

        if (permissions.HasFlag(Cef.PermissionRequestType.MicStream))
        {
            result |= BrowserPermissionKind.Microphone;
        }

        if (permissions.HasFlag(Cef.PermissionRequestType.Geolocation))
        {
            result |= BrowserPermissionKind.Location;
        }

        if (permissions.HasFlag(Cef.PermissionRequestType.Notifications))
        {
            result |= BrowserPermissionKind.Notifications;
        }

        if (permissions.HasFlag(Cef.PermissionRequestType.Clipboard))
        {
            result |= BrowserPermissionKind.Clipboard;
        }

        if (permissions.HasFlag(Cef.PermissionRequestType.FileSystemAccess))
        {
            result |= BrowserPermissionKind.FileSystem;
        }

        if ((permissions & (Cef.PermissionRequestType.StorageAccess
                | Cef.PermissionRequestType.TopLevelStorageAccess
                | Cef.PermissionRequestType.DiskQuota)) != 0)
        {
            result |= BrowserPermissionKind.Storage;
        }

        const Cef.PermissionRequestType known =
            Cef.PermissionRequestType.CameraPanTiltZoom
            | Cef.PermissionRequestType.CameraStream
            | Cef.PermissionRequestType.MicStream
            | Cef.PermissionRequestType.Geolocation
            | Cef.PermissionRequestType.Notifications
            | Cef.PermissionRequestType.Clipboard
            | Cef.PermissionRequestType.FileSystemAccess
            | Cef.PermissionRequestType.StorageAccess
            | Cef.PermissionRequestType.TopLevelStorageAccess
            | Cef.PermissionRequestType.DiskQuota;
        if ((permissions & ~known) != 0 || result == BrowserPermissionKind.None)
        {
            result |= BrowserPermissionKind.Other;
        }

        return result;
    }

    private static string SafeDownloadFileName(string value, int downloadId)
    {
        var name = Path.GetFileName(value);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name)
            ? $"download-{downloadId}"
            : Bound(name);
    }

    private static string Bound(string? value)
    {
        var safe = (value ?? string.Empty).Replace('\0', '\uFFFD');
        return safe.Length <= MaximumProductTextLength
            ? safe
            : safe[..MaximumProductTextLength];
    }
}
