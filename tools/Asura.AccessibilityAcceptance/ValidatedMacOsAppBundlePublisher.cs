using Asura.Packaging;

namespace Asura.AccessibilityAcceptance;

internal static class ValidatedMacOsAppBundlePublisher
{
    public static PackageInspection Publish(MacOsPackagePublishOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var inspection = PackageFingerprint.Inspect(
            options.PackagePath,
            TargetPlatform.MacOS,
            options.BuildLabel);
        _ = MacOsAppBundlePublisher.Publish(
            options.PackagePath,
            options.DestinationPath);
        return inspection;
    }
}
