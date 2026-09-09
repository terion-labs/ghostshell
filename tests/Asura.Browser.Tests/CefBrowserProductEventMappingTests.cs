using Asura.Application;
using Exclr8Cef;

namespace Asura.Browser.Tests;

public sealed class CefBrowserProductEventMappingTests
{
    [Theory]
    [InlineData(Cef.CefErrorCode.InternetDisconnected, 1)]
    [InlineData(Cef.CefErrorCode.NameNotResolved, 1)]
    [InlineData(Cef.CefErrorCode.TimedOut, 2)]
    [InlineData(Cef.CefErrorCode.CertAuthorityInvalid, 3)]
    [InlineData(Cef.CefErrorCode.Failed, 4)]
    public void NativeLoadFailuresMapToClosedEngineNeutralKinds(
        Cef.CefErrorCode errorCode,
        int expectedKind)
    {
        Assert.Equal(
            (NativeBrowserLoadFailureKind)expectedKind,
            CefBrowserView.MapLoadFailure(errorCode));
    }

    [Fact]
    public void NativePermissionFlagsAreGroupedWithoutLeakingVendorTypes()
    {
        var mapped = CefBrowserView.MapPermissions(
            Cef.PermissionRequestType.CameraStream
            | Cef.PermissionRequestType.MicStream
            | Cef.PermissionRequestType.Geolocation
            | Cef.PermissionRequestType.FileSystemAccess);

        Assert.Equal(
            BrowserPermissionKind.Camera
            | BrowserPermissionKind.Microphone
            | BrowserPermissionKind.Location
            | BrowserPermissionKind.FileSystem,
            mapped);
    }

    [Theory]
    [InlineData(
        Cef.CefErrorCode.CertCommonNameInvalid,
        BrowserCertificateErrorKind.NameMismatch)]
    [InlineData(
        Cef.CefErrorCode.CertDateInvalid,
        BrowserCertificateErrorKind.ExpiredOrNotYetValid)]
    [InlineData(
        Cef.CefErrorCode.CertAuthorityInvalid,
        BrowserCertificateErrorKind.UntrustedAuthority)]
    [InlineData(
        Cef.CefErrorCode.CertRevoked,
        BrowserCertificateErrorKind.Revoked)]
    public void CertificateFailuresKeepTheirProductMeaning(
        Cef.CefErrorCode errorCode,
        BrowserCertificateErrorKind expected) =>
        Assert.Equal(expected, CefBrowserView.MapCertificateError(errorCode));
}
