namespace Asura.Packaging;

internal sealed record MacOsPackagingCommand(
    string PublishDirectory,
    string ManagedEvidenceDirectory,
    string DestinationPath,
    string ProductVersion,
    string BuildVersion,
    string ProductIdentityManifestPath,
    string ProductIdentitySourceRoot,
    string AssetCatalogPath,
    string ComponentCatalogPath,
    string NativeComponentCatalogPath,
    string NativeBuildReceiptPath,
    string FontAssetsCatalogPath,
    string FontAssetsBuildReceiptPath,
    string NuGetPackageRoot,
    string CefRuntimeRoot,
    string CefRuntimeCatalogPath,
    string RuntimeIdentifier)
{
    public static MacOsPackagingCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 34)
        {
            throw new PackagingUsageException(
                "macos requires --publish, --managed-evidence, --output, "
                + "--version, --build-version, "
                + "--product-identity-manifest, --product-identity-source-root, "
                + "--asset-catalog, "
                + "--component-catalog, --native-component-catalog, "
                + "--native-build-receipt, --font-assets-catalog, "
                + "--font-assets-build-receipt, --nuget-packages, "
                + "--cef-runtime-root, --cef-runtime-catalog, and "
                + "--runtime-identifier.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var name = arguments[index];
            if (name is not (
                    "--publish"
                    or "--managed-evidence"
                    or "--output"
                    or "--version"
                    or "--build-version"
                    or "--product-identity-manifest"
                    or "--product-identity-source-root"
                    or "--asset-catalog"
                    or "--component-catalog"
                    or "--native-component-catalog"
                    or "--native-build-receipt"
                    or "--font-assets-catalog"
                    or "--font-assets-build-receipt"
                    or "--nuget-packages"
                    or "--cef-runtime-root"
                    or "--cef-runtime-catalog"
                    or "--runtime-identifier"))
            {
                throw new PackagingUsageException($"Unknown option {name}.");
            }

            if (!values.TryAdd(name, arguments[index + 1]))
            {
                throw new PackagingUsageException($"Option {name} was supplied more than once.");
            }
        }

        return new MacOsPackagingCommand(
            Required(values, "--publish"),
            Required(values, "--managed-evidence"),
            Required(values, "--output"),
            Required(values, "--version"),
            Required(values, "--build-version"),
            Required(values, "--product-identity-manifest"),
            Required(values, "--product-identity-source-root"),
            Required(values, "--asset-catalog"),
            Required(values, "--component-catalog"),
            Required(values, "--native-component-catalog"),
            Required(values, "--native-build-receipt"),
            Required(values, "--font-assets-catalog"),
            Required(values, "--font-assets-build-receipt"),
            Required(values, "--nuget-packages"),
            Required(values, "--cef-runtime-root"),
            Required(values, "--cef-runtime-catalog"),
            RequireSupportedAppRuntimeIdentifier(values));
    }

    public MacOsAppBundleRequest ToRequest() => new(
        PublishDirectory,
        DestinationPath,
        ProductVersion,
        BuildVersion,
        ProductIdentityManifestPath,
        ProductIdentitySourceRoot,
        AssetCatalogPath,
        ComponentCatalogPath,
        NativeComponentCatalogPath,
        NativeBuildReceiptPath,
        FontAssetsCatalogPath,
        FontAssetsBuildReceiptPath,
        NuGetPackageRoot,
        CefRuntimeRoot,
        CefRuntimeCatalogPath,
        RuntimeIdentifier,
        ManagedEvidenceDirectory);

    private static string RequireSupportedAppRuntimeIdentifier(
        IReadOnlyDictionary<string, string> values)
    {
        var value = Required(values, "--runtime-identifier");
        return string.Equals(value, "osx-arm64"
, StringComparison.Ordinal) ? value
            : throw new PackagingUsageException(
                "Full macOS application packaging currently supports only "
                + "osx-arm64; osx-x64 lacks a reviewed managed catalog and "
                + "libghostty-vt receipt.");
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new PackagingUsageException($"{name} is required.");
        }

        return value;
    }
}

internal sealed class PackagingUsageException : Exception
{

    public PackagingUsageException(string message)
        : base(message)
    {
    }

    public PackagingUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
