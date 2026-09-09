using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace Asura.Packaging;

internal sealed record ManagedComponentEvidenceFile(
    string RelativePath,
    byte[] Content);

internal sealed record ManagedComponentEvidence(
    IReadOnlyList<ManagedComponentEvidenceFile> Files);

internal sealed record ManagedComponentEvidenceLimits(
    int MaximumFiles,
    int MaximumEntries,
    long MaximumBytes,
    int MaximumRelativePathDepth);

internal enum ManagedEvidenceProfile { MacOsDesktop, LinuxBackend, LinuxX64Backend }

internal static partial class ManagedComponentEvidenceBuilder
{
    private const int MaximumCatalogBytes = 4 * 1024 * 1024;
    internal const int MaximumGeneratedEvidenceFiles = 1_024;
    internal const long MaximumGeneratedEvidenceBytes = 64L * 1024 * 1024;
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumNuspecBytes = 1024 * 1024;
    private const int MaximumNoticeBytes = 4 * 1024 * 1024;
    private const int MaximumArchiveEntries = 50_000;
    private const long MaximumNupkgBytes = 2L * 1024 * 1024 * 1024;
    private const string SpdxFileName = "SBOM.spdx.json";
    private const string NoAssertion = "NOASSERTION";
    private const string ProductVersionPlaceholder = "${productVersion}";
    private const string BaseRuntimeTargetName = ".NETCoreApp,Version=v10.0";
    private static readonly string GeneratorCreator = CreateGeneratorCreator();
    private static readonly string[] RequiredRuntimeFallbacks =
    [
        "osx",
        "unix-arm64",
        "unix",
        "any",
        "base",
    ];

    private static readonly string[] RequiredProjectFiles =
    [
        "Exclr8Cef.dll",
        "Exclr8Cef.WebView.dll",
        "Asura.dll",
        "Asura.Agent.dll",
        "Asura.Agent.Providers.dll",
        "Asura.Agent.Runtime.dll",
        "Asura.App.dll",
        "Asura.Application.dll",
        "Asura.Browser.dll",
        "Asura.ConnectionBackend.dll",
        "Asura.Core.dll",
        "Asura.Databases.dll",
        "Asura.Docker.dll",
        "Asura.Docking.dll",
        "Asura.Files.dll",
        "Asura.Git.dll",
        "Asura.Infrastructure.dll",
        "Asura.Mcp.dll",
        "Asura.Mcp.Server.dll",
        "Asura.Monitoring.dll",
        "Asura.Previews.dll",
        "Asura.Protocol.dll",
        "Asura.Redis.dll",
        "Asura.SessionHost.dll",
        "Asura.Terminal.dll",
        "Asura.Updates.dll",
    ];

    private static readonly string[] TerminalNativeFiles =
    [
        "libghostty-vt.dylib",
    ];

    private static readonly string[] RequiredNoticePackageIds =
    [
        "HarfBuzzSharp.NativeAssets.macOS",
        "SkiaSharp.NativeAssets.macOS",
    ];

    private sealed record TargetProfile(string Root, string Rid, string[] ProjectFiles,
        string[] Fallbacks, string[] NoticePackages, string[] NativeFiles)
    {
        internal string RuntimeTarget => BaseRuntimeTargetName + "/" + Rid;

        internal static TargetProfile For(ManagedEvidenceProfile profile) => profile switch
        {
            ManagedEvidenceProfile.MacOsDesktop => new("Asura", "osx-arm64", RequiredProjectFiles,
                RequiredRuntimeFallbacks, RequiredNoticePackageIds, TerminalNativeFiles),
            ManagedEvidenceProfile.LinuxBackend => new("Asura.Backend", "linux-arm64",
                ["Asura.Backend.dll", "Asura.ConnectionBackend.dll", "Asura.Application.dll",
                    "Asura.Core.dll", "Asura.Databases.dll", "Asura.Files.dll",
                    "Asura.Infrastructure.dll", "Asura.Redis.dll"],
                ["linux", "unix-arm64", "unix", "any", "base"], [], []),
            ManagedEvidenceProfile.LinuxX64Backend => new("Asura.Backend", "linux-x64",
                ["Asura.Backend.dll", "Asura.ConnectionBackend.dll", "Asura.Application.dll",
                    "Asura.Core.dll", "Asura.Databases.dll", "Asura.Files.dll",
                    "Asura.Infrastructure.dll", "Asura.Redis.dll"],
                ["linux", "unix-x64", "unix", "any", "base"], [], []),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }

    private static readonly HashSet<string> SupportedNuspecNamespaces =
    [
        "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd",
        "http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd",
        "http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd",
        "http://schemas.microsoft.com/packaging/2013/01/nuspec.xsd",
        "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd",
    ];

    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 24,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ManagedComponentEvidence Build(
        string publishDirectory,
        string licenseDirectory,
        string catalogPath,
        string nugetPackageRoot,
        string productVersion,
        ManagedComponentEvidenceLimits limits,
        ManagedEvidenceProfile profile = ManagedEvidenceProfile.MacOsDesktop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            publishDirectory,
            nameof(publishDirectory));
        ArgumentException.ThrowIfNullOrWhiteSpace(
            licenseDirectory,
            nameof(licenseDirectory));
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath, nameof(catalogPath));
        ArgumentException.ThrowIfNullOrWhiteSpace(
            nugetPackageRoot,
            nameof(nugetPackageRoot));
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion, nameof(productVersion));
        ValidateEvidenceLimits(limits);

        var target = TargetProfile.For(profile);
        var catalogBytes = ReadRegularFile(
            catalogPath,
            MaximumCatalogBytes,
            "managed-component catalog");
        var catalog = ParseCatalog(catalogBytes, productVersion);
        var dependenciesPath = Path.Combine(
            publishDirectory,
            target.Root + ".deps.json");
        var dependenciesBytes = ReadRegularFile(
            dependenciesPath,
            MaximumCatalogBytes,
            "publish dependency manifest");
        var dependencyManifest = ParseDependencyManifest(dependenciesBytes, target);
        ValidateExactDependencySet(
            catalog.Dependencies,
            dependencyManifest.Libraries);

        var packageEvidence = new List<PackageEvidence>();
        var evidence = new EvidenceAccumulator(limits, SpdxFileName);
        foreach (var dependency in catalog.Dependencies
                     .OrderBy(component => component.Identity, StringComparer.Ordinal))
        {
            packageEvidence.Add(dependency.Kind switch
            {
                "project" => ValidateProject(
                    publishDirectory,
                    dependency,
                    dependencyManifest.Libraries[dependency.Identity], target),
                "nuget" or "runtime" => ValidateNuGetPackage(
                    nugetPackageRoot,
                    dependency,
                    dependencyManifest.Libraries[dependency.Identity],
                    evidence),
                _ => throw CatalogError(
                    $"component {dependency.Identity} has unsupported kind {dependency.Kind}"),
            });
        }

        ValidateRequiredProjectSet(catalog.Dependencies, target);
        ValidateRequiredNoticeSet(catalog.Dependencies, target);
        ValidateRequiredNativeSet(catalog.AdditionalComponents, target);
        foreach (var component in catalog.AdditionalComponents
                     .OrderBy(component => component.Identity, StringComparer.Ordinal))
        {
            packageEvidence.Add(ValidateNativeComponent(
                publishDirectory,
                licenseDirectory,
                component));
        }

        var namespaceDigest = ComputeEvidenceDigest(
            catalogBytes,
            dependenciesBytes,
            packageEvidence,
            evidence.Files);
        var spdxBytes = WriteSpdx(
            catalog,
            packageEvidence,
            namespaceDigest,
            evidence.RemainingBytes);
        evidence.CompleteReserved(SpdxFileName, spdxBytes);
        return new ManagedComponentEvidence(
            [.. evidence.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)]);
    }

    private static CatalogDocument ParseCatalog(byte[] bytes, string productVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 24,
                });
            ValidateNoDuplicateJsonProperties(document.RootElement);
            var catalog = JsonSerializer.Deserialize<CatalogDocument>(
                              bytes,
                              CatalogJsonOptions)
                          ?? throw CatalogError("document is empty");
            BindProductVersion(catalog, productVersion);
            ValidateCatalog(catalog);
            return catalog;
        }
        catch (JsonException exception)
        {
            throw CatalogError("document is malformed", exception);
        }
    }

    private static void BindProductVersion(
        CatalogDocument catalog,
        string productVersion)
    {
        if (catalog.SchemaVersion != 2)
        {
            throw CatalogError("schemaVersion must be 2");
        }

        const string documentNameTemplate =
            "Asura ${productVersion} managed-component evidence";
        if (!string.Equals(
                catalog.DocumentName,
                documentNameTemplate,
                StringComparison.Ordinal))
        {
            throw CatalogError(
                $"documentName must be {documentNameTemplate}");
        }

        if (catalog.NamespaceBase is null
            || !catalog.NamespaceBase.EndsWith(
                $"/{ProductVersionPlaceholder}",
                StringComparison.Ordinal))
        {
            throw CatalogError(
                $"namespaceBase must end with /{ProductVersionPlaceholder}");
        }

        catalog.DocumentName = catalog.DocumentName.Replace(
            ProductVersionPlaceholder,
            productVersion,
            StringComparison.Ordinal);
        catalog.NamespaceBase = catalog.NamespaceBase.Replace(
            ProductVersionPlaceholder,
            productVersion,
            StringComparison.Ordinal);

        if (catalog.Dependencies is null)
        {
            throw CatalogError("dependencies must be an array");
        }

        foreach (var dependency in catalog.Dependencies)
        {
            var separator = dependency.Identity.LastIndexOf('/');
            var name = separator > 0
                ? dependency.Identity[..separator]
                : dependency.Identity;
            var isFirstParty = string.Equals(name, "Asura", StringComparison.Ordinal)
                               || name.StartsWith("Asura.", StringComparison.Ordinal);
            var hasPlaceholder = dependency.Identity.Contains(
                ProductVersionPlaceholder,
                StringComparison.Ordinal);
            if (isFirstParty)
            {
                if (!string.Equals(dependency.Kind, "project", StringComparison.Ordinal)
                    || !dependency.Identity.EndsWith(
                        $"/{ProductVersionPlaceholder}",
                        StringComparison.Ordinal))
                {
                    throw CatalogError(
                        $"first-party component {dependency.Identity} must be a project "
                        + $"whose identity version is {ProductVersionPlaceholder}");
                }

                dependency.Identity = $"{name}/{productVersion}";
            }
            else if (hasPlaceholder)
            {
                throw CatalogError(
                    $"only first-party project identities may use {ProductVersionPlaceholder}");
            }
        }
    }

    private static void ValidateCatalog(CatalogDocument catalog)
    {
        if (catalog.SchemaVersion != 2)
        {
            throw CatalogError("schemaVersion must be 2");
        }

        ValidateText(catalog.DocumentName, "documentName", 200);
        if (!DateTimeOffset.TryParseExact(
                catalog.DocumentCreatedUtc,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
                | DateTimeStyles.AdjustToUniversal,
                out _))
        {
            throw CatalogError(
                "documentCreatedUtc must be a fixed UTC second timestamp");
        }

        if (!Uri.TryCreate(
                catalog.NamespaceBase,
                UriKind.Absolute,
                out var namespaceBase)
            || !string.Equals(namespaceBase.Scheme, Uri.UriSchemeHttps
, StringComparison.Ordinal) || namespaceBase.UserInfo.Length != 0
            || namespaceBase.Query.Length != 0
            || namespaceBase.Fragment.Length != 0
            || catalog.NamespaceBase.EndsWith('/'))
        {
            throw CatalogError(
                "namespaceBase must be an absolute HTTPS URI without a trailing slash");
        }

        if (catalog.ReleaseBlockers is null
            || catalog.Dependencies is null
            || catalog.AdditionalComponents is null)
        {
            throw CatalogError(
                "releaseBlockers, dependencies, and additionalComponents must be arrays");
        }

        foreach (var blocker in catalog.ReleaseBlockers)
        {
            ValidateText(blocker, "release blocker", 1_000);
        }

        if (catalog.Dependencies.Count == 0)
        {
            throw CatalogError("dependencies must not be empty");
        }

        EnsureUnique(
            catalog.Dependencies.Select(component => component.Identity),
            "dependency identity");
        EnsureUnique(
            catalog.AdditionalComponents.Select(component => component.Identity),
            "additional-component identity");
        EnsureUnique(
            catalog.Dependencies.Select(component => component.Identity)
                .Concat(catalog.AdditionalComponents.Select(component => component.Identity)),
            "component identity");
        foreach (var component in catalog.Dependencies)
        {
            ValidateDependencyCatalogEntry(component);
        }

        EnsureUnique(
            catalog.Dependencies
                .SelectMany(component => component.Notices)
                .Select(notice => notice.OutputPath)
                .Append(SpdxFileName),
            "generated evidence path");
        foreach (var component in catalog.AdditionalComponents)
        {
            ValidateNativeCatalogEntry(component);
        }
    }

    private static void ValidateDependencyCatalogEntry(
        CatalogDependency component)
    {
        if (component.Notices is null)
        {
            throw CatalogError(
                $"component {component.Identity} notices must be an array");
        }

        var (name, version) = ParseIdentity(component.Identity);
        ValidateText(component.DepsType, "depsType", 32);
        ValidateLicenseValue(component.LicenseDeclared, "licenseDeclared");

        switch (component.Kind)
        {
            case "project":
                RequireEqual(component.DepsType, "project", component.Identity, "depsType");
                RequirePresent(component.File, component.Identity, "file");
                RequireAbsent(component.NuGetId, component.Identity, "nugetId");
                RequireAbsent(component.ContentHash, component.Identity, "contentHash");
                RequireAbsent(component.NupkgSha512, component.Identity, "nupkgSha512");
                RequireAbsent(
                    component.NuspecLicenseType,
                    component.Identity,
                    "nuspecLicenseType");
                RequireAbsent(
                    component.NuspecLicense,
                    component.Identity,
                    "nuspecLicense");
                RequireEmpty(component.Notices, component.Identity, "notices");
                RequireEqual(
                    component.LicenseDeclared,
                    NoAssertion,
                    component.Identity,
                    "licenseDeclared");
                RequireEqual(component.File, $"{name}.dll", component.Identity, "file");
                ValidateSimpleFileName(component.File!, component.Identity);
                break;
            case "nuget":
            case "runtime":
                RequireEqual(
                    component.DepsType,
string.Equals(component.Kind, "runtime", StringComparison.Ordinal) ? "runtimepack" : "package",
                    component.Identity,
                    "depsType");
                RequirePresent(component.NuGetId, component.Identity, "nugetId");
                RequirePresent(component.ContentHash, component.Identity, "contentHash");
                RequirePresent(component.NupkgSha512, component.Identity, "nupkgSha512");
                RequirePresent(
                    component.NuspecLicenseType,
                    component.Identity,
                    "nuspecLicenseType");
                RequirePresent(
                    component.NuspecLicense,
                    component.Identity,
                    "nuspecLicense");
                RequireAbsent(component.File, component.Identity, "file");
                ValidateNuGetSegment(component.NuGetId!, component.Identity, "nugetId");
                ValidateNuGetSegment(version, component.Identity, "version");
                if (string.Equals(component.Kind, "nuget"
, StringComparison.Ordinal) && !string.Equals(name, component.NuGetId, StringComparison.Ordinal))
                {
                    throw CatalogError(
                        $"component {component.Identity} nugetId does not match its identity");
                }

                ValidateBase64Sha512(
                    component.ContentHash!,
                    component.Identity,
                    "contentHash");
                ValidateBase64Sha512(
                    component.NupkgSha512!,
                    component.Identity,
                    "nupkgSha512");
                if (component.NuspecLicenseType is not ("expression" or "file"))
                {
                    throw CatalogError(
                        $"component {component.Identity} has unsupported nuspecLicenseType");
                }

                ValidateText(
                    component.NuspecLicense!,
                    $"{component.Identity} nuspecLicense",
                    500);
                if (string.Equals(component.NuspecLicenseType, "file", StringComparison.Ordinal))
                {
                    RequireEqual(
                        component.LicenseDeclared,
                        NoAssertion,
                        component.Identity,
                        "licenseDeclared");
                }
                else if (string.Equals(component.Kind, "nuget", StringComparison.Ordinal))
                {
                    RequireEqual(
                        component.LicenseDeclared,
                        component.NuspecLicense,
                        component.Identity,
                        "licenseDeclared");
                }
                else
                {
                    RequireEqual(
                        component.LicenseDeclared,
                        NoAssertion,
                        component.Identity,
                        "licenseDeclared");
                }

                foreach (var notice in component.Notices)
                {
                    ValidateNotice(component.Identity, notice);
                }

                EnsureUnique(
                    component.Notices.Select(notice => notice.ArchivePath),
                    $"{component.Identity} notice archive path");
                if (string.Equals(component.NuspecLicenseType, "file"
, StringComparison.Ordinal) && !component.Notices.Any(notice => string.Equals(
                        notice.ArchivePath,
                        component.NuspecLicense,
                        StringComparison.Ordinal)))
                {
                    throw CatalogError(
                        $"component {component.Identity} must extract its exact "
                        + "nuspec license file as reviewed evidence");
                }

                break;
            default:
                throw CatalogError(
                    $"component {component.Identity} has unsupported kind {component.Kind}");
        }
    }

    private static void ValidateNativeCatalogEntry(
        CatalogNativeComponent component)
    {
        _ = ParseIdentity(component.Identity);
        RequireEqual(component.Kind, "native", component.Identity, "kind");
        ValidateSimpleFileName(component.File, component.Identity);
        ValidateLicenseValue(component.LicenseDeclared, "licenseDeclared");
        ValidateText(component.Comment, $"{component.Identity} comment", 1_000);
        if (!string.Equals(component.DownloadLocation, NoAssertion
, StringComparison.Ordinal) && (!Uri.TryCreate(
                    component.DownloadLocation,
                    UriKind.Absolute,
                    out var location)
                || !string.Equals(location.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            throw CatalogError(
                $"component {component.Identity} downloadLocation is invalid");
        }

        if ((component.LicenseEvidenceFile is null)
            != (component.LicenseEvidenceSha256 is null))
        {
            throw CatalogError(
                $"component {component.Identity} license evidence is incomplete");
        }

        if (component.LicenseEvidenceFile is not null)
        {
            ValidateSimpleFileName(component.LicenseEvidenceFile, component.Identity);
            ValidateSha256(
                component.LicenseEvidenceSha256!,
                component.Identity,
                "licenseEvidenceSha256");
            if (component.LicenseEvidenceMinimumBytes < 1_024)
            {
                throw CatalogError(
                    $"component {component.Identity} license evidence is not required to be nontrivial");
            }
        }
        else if (!string.Equals(component.LicenseDeclared, NoAssertion, StringComparison.Ordinal))
        {
            throw CatalogError(
                $"component {component.Identity} declares a license without evidence");
        }
        else if (component.LicenseEvidenceMinimumBytes is not null)
        {
            throw CatalogError(
                $"component {component.Identity} has an unexpected licenseEvidenceMinimumBytes");
        }
    }

    private static void ValidateNotice(
        string identity,
        CatalogNotice notice)
    {
        ValidateArchivePath(notice.ArchivePath, identity);
        ValidateRelativeOutputPath(notice.OutputPath, identity);
        ValidateSha256(notice.Sha256, identity, "notice sha256");
        if (notice.MinimumBytes < 1_024
            || notice.MinimumBytes > MaximumNoticeBytes)
        {
            throw CatalogError(
                $"component {identity} notice minimumBytes is outside the allowed range");
        }
    }

    private static DependencyManifest ParseDependencyManifest(byte[] bytes, TargetProfile target)
    {
        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            ValidateNoDuplicateJsonProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "libraries",
                    out var libraries)
                || libraries.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Asura.deps.json is missing its libraries object.");
            }

            var result = new Dictionary<string, DependencyManifestEntry>(
                StringComparer.Ordinal);
            foreach (var library in libraries.EnumerateObject())
            {
                _ = ParseIdentity(library.Name);
                if (library.Value.ValueKind != JsonValueKind.Object
                    || !library.Value.TryGetProperty("type", out var typeNode)
                    || typeNode.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException(
                        $"Asura.deps.json library {library.Name} has no valid type.");
                }

                var type = typeNode.GetString()!;
                if (type is not ("project" or "package" or "runtimepack"))
                {
                    throw new InvalidDataException(
                        $"Asura.deps.json library {library.Name} has unsupported type {type}.");
                }

                var packagePath = ReadOptionalManifestString(
                    library.Name,
                    library.Value,
                    "path");
                var hashPath = ReadOptionalManifestString(
                    library.Name,
                    library.Value,
                    "hashPath");
                if (string.Equals(type, "package", StringComparison.Ordinal))
                {
                    if (packagePath is null || hashPath is null)
                    {
                        throw new InvalidDataException(
                            $"Asura.deps.json library {library.Name} is missing "
                            + "path or hashPath.");
                    }
                }
                else if (packagePath is not null || hashPath is not null)
                {
                    throw new InvalidDataException(
                        $"Asura.deps.json library {library.Name} has an unexpected "
                        + "path or hashPath.");
                }

                string? contentHash = null;
                if (library.Value.TryGetProperty("sha512", out var hashNode))
                {
                    if (hashNode.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException(
                            $"Asura.deps.json library {library.Name} has a malformed sha512.");
                    }

                    var serializedHash = hashNode.GetString()!;
                    if (string.Equals(type, "package", StringComparison.Ordinal))
                    {
                        const string prefix = "sha512-";
                        if (!serializedHash.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                $"Asura.deps.json library {library.Name} has a malformed sha512.");
                        }

                        contentHash = serializedHash[prefix.Length..];
                        ValidateBase64Sha512(
                            contentHash,
                            library.Name,
                            "dependency manifest sha512");
                    }
                    else if (serializedHash.Length != 0)
                    {
                        throw new InvalidDataException(
                            $"Asura.deps.json library {library.Name} has an unexpected sha512.");
                    }
                }
                else if (string.Equals(type, "package", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Asura.deps.json library {library.Name} is missing sha512.");
                }

                result.Add(
                    library.Name,
                    new DependencyManifestEntry(
                        type,
                        contentHash,
                        packagePath,
                        hashPath));
            }

            ValidateRuntimeTargetGraph(document.RootElement, result, target);
            return new DependencyManifest(result);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Asura.deps.json is malformed.",
                exception);
        }
    }

    private static void ValidateRuntimeTargetGraph(
        JsonElement root,
        IReadOnlyDictionary<string, DependencyManifestEntry> libraries,
        TargetProfile profile)
    {
        if (!root.TryGetProperty("runtimeTarget", out var runtimeTarget)
            || runtimeTarget.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Asura.deps.json is missing its runtimeTarget object.");
        }

        RequireExactJsonProperties(
            runtimeTarget,
            ["name", "signature"],
            "Asura.deps.json runtimeTarget");
        var selectedTargetName = ReadRequiredManifestString(
            "runtimeTarget",
            runtimeTarget,
            "name");
        if (!string.Equals(
                selectedTargetName,
                profile.RuntimeTarget,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Asura.deps.json runtimeTarget must be "
                + $"{profile.RuntimeTarget}.");
        }

        var signature = ReadRequiredManifestString(
            "runtimeTarget",
            runtimeTarget,
            "signature",
            allowEmpty: true);
        if (signature.Length != 0)
        {
            throw new InvalidDataException(
                "Asura.deps.json runtimeTarget signature must be empty.");
        }

        if (!root.TryGetProperty("targets", out var targets)
            || targets.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Asura.deps.json is missing its targets object.");
        }

        var targetNames = targets.EnumerateObject()
            .Select(target => target.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (targetNames.Count != 2
            || !targetNames.SetEquals(
                [BaseRuntimeTargetName, profile.RuntimeTarget])
            || !targets.TryGetProperty(
                BaseRuntimeTargetName,
                out var baseTarget)
            || baseTarget.ValueKind != JsonValueKind.Object
            || baseTarget.EnumerateObject().Any()
            || !targets.TryGetProperty(
                profile.RuntimeTarget,
                out var selectedTarget)
            || selectedTarget.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Asura.deps.json must contain one empty base target and "
                + "one selected osx-arm64 target.");
        }

        ValidateRuntimeFallbacks(root, profile);
        var selectedKeys = selectedTarget.EnumerateObject()
            .Select(component => component.Name)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = selectedKeys
            .Where(identity => !libraries.ContainsKey(identity))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missing = libraries.Keys
            .Where(identity => !selectedKeys.Contains(identity))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length != 0 || missing.Length != 0)
        {
            throw new InvalidDataException(
                "Asura.deps.json selected target does not exactly match its "
                + $"libraries. Unknown: {FormatIdentities(unknown)}. "
                + $"Missing: {FormatIdentities(missing)}.");
        }

        var dependencyGraph =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var component in selectedTarget.EnumerateObject())
        {
            if (component.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Asura.deps.json target component {component.Name} "
                    + "must be an object.");
            }

            dependencyGraph.Add(
                component.Name,
                ValidateTargetComponent(
                    component.Name,
                    component.Value,
                    libraries));
        }

        ValidateDependencyGraph(dependencyGraph, profile);
    }

    private static void ValidateRuntimeFallbacks(JsonElement root, TargetProfile profile)
    {
        if (!root.TryGetProperty("runtimes", out var runtimes)
            || runtimes.ValueKind != JsonValueKind.Object
            || !HasExactJsonProperties(runtimes, profile.Rid switch
            {
                "linux-arm64" => ["android-arm64", "linux-arm64", "linux-bionic-arm64", "linux-musl-arm64"],
                "linux-x64" => ["android-x64", "linux-x64", "linux-bionic-x64", "linux-musl-x64"],
                _ => [profile.Rid],
            })
            || runtimes.GetProperty(profile.Rid).ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Asura.deps.json has an invalid osx-arm64 runtime fallback map.");
        }

        var actual = runtimes.GetProperty(profile.Rid)
            .EnumerateArray()
            .Select(item =>
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException(
                        "Asura.deps.json has a malformed runtime fallback.");
                }

                return item.GetString()!;
            })
            .ToArray();
        if (!actual.SequenceEqual(
                profile.Fallbacks,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Asura.deps.json has an unexpected osx-arm64 runtime fallback chain.");
        }
        if (string.Equals(profile.Rid, "linux-arm64", StringComparison.Ordinal))
        {
            ValidateFallback("android-arm64", ["android", "linux-bionic-arm64", "linux-bionic", "linux-arm64", "linux", "unix-arm64", "unix", "any", "base"]);
            ValidateFallback("linux-bionic-arm64", ["linux-bionic", "linux-arm64", "linux", "unix-arm64", "unix", "any", "base"]);
            ValidateFallback("linux-musl-arm64", ["linux-musl", "linux-arm64", "linux", "unix-arm64", "unix", "any", "base"]);
        }
        if (string.Equals(profile.Rid, "linux-x64", StringComparison.Ordinal))
        {
            ValidateFallback("android-x64", ["android", "linux-bionic-x64", "linux-bionic", "linux-x64", "linux", "unix-x64", "unix", "any", "base"]);
            ValidateFallback("linux-bionic-x64", ["linux-bionic", "linux-x64", "linux", "unix-x64", "unix", "any", "base"]);
            ValidateFallback("linux-musl-x64", ["linux-musl", "linux-x64", "linux", "unix-x64", "unix", "any", "base"]);
        }

        void ValidateFallback(string rid, string[] expected)
        {
            var value = runtimes.GetProperty(rid);
            if (value.ValueKind != JsonValueKind.Array
                || !value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                    .SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new InvalidDataException("The backend runtime fallback graph differs from its reviewed profile.");
            }
        }
    }

    private static void ValidateDependencyGraph(
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph, TargetProfile profile)
    {
        var roots = graph.Keys
            .Where(identity => string.Equals(ParseIdentity(identity).Name, profile.Root, StringComparison.Ordinal))
            .ToArray();
        if (roots.Length != 1)
        {
            throw new InvalidDataException(
                "Asura.deps.json must contain exactly one Asura root.");
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(roots[0]);
        while (pending.Count > 0)
        {
            var identity = pending.Pop();
            if (!reachable.Add(identity))
            {
                continue;
            }

            foreach (var dependency in graph[identity])
            {
                pending.Push(dependency);
            }
        }

        var unreachable = graph.Keys
            .Where(identity => !reachable.Contains(identity))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unreachable.Length != 0)
        {
            throw new InvalidDataException(
                "Asura.deps.json selected target contains unreachable "
                + $"components: {FormatIdentities(unreachable)}.");
        }

        var incomingEdges = graph.Keys.ToDictionary(
            identity => identity,
            _ => 0,
            StringComparer.Ordinal);
        foreach (var dependencies in graph.Values)
        {
            foreach (var dependency in dependencies)
            {
                incomingEdges[dependency] = checked(incomingEdges[dependency] + 1);
            }
        }

        var rootsWithoutIncomingEdges = new Queue<string>(
            incomingEdges
                .Where(entry => entry.Value == 0)
                .Select(entry => entry.Key));
        var visited = 0;
        while (rootsWithoutIncomingEdges.TryDequeue(out var identity))
        {
            visited++;
            foreach (var dependency in graph[identity])
            {
                incomingEdges[dependency]--;
                if (incomingEdges[dependency] == 0)
                {
                    rootsWithoutIncomingEdges.Enqueue(dependency);
                }
            }
        }

        if (visited != graph.Count)
        {
            throw new InvalidDataException(
                "Asura.deps.json selected target dependency graph contains a cycle.");
        }
    }

    private static IReadOnlyList<string> ValidateTargetComponent(
        string identity,
        JsonElement component,
        IReadOnlyDictionary<string, DependencyManifestEntry> libraries)
    {
        var assetPaths = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string> dependencies = [];
        foreach (var group in component.EnumerateObject())
        {
            if (group.Name is not (
                    "dependencies"
                    or "runtime"
                    or "native"
                    or "resources"
                    or "runtimeTargets")
                || group.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Asura.deps.json target component {identity} has "
                    + $"unsupported group {group.Name} or value shape.");
            }

            if (string.Equals(group.Name, "dependencies", StringComparison.Ordinal))
            {
                dependencies = ValidateTargetDependencies(
                    identity,
                    group.Value,
                    libraries);
                continue;
            }

            foreach (var asset in group.Value.EnumerateObject())
            {
                ValidateManifestAssetPath(identity, group.Name, asset.Name);
                if (!assetPaths.Add(asset.Name))
                {
                    throw new InvalidDataException(
                        $"Asura.deps.json target component {identity} "
                        + $"references asset {asset.Name} more than once.");
                }

                ValidateTargetAssetMetadata(
                    identity,
                    group.Name,
                    asset.Name,
                    asset.Value);
            }
        }

        return dependencies;
    }

    private static IReadOnlyList<string> ValidateTargetDependencies(
        string identity,
        JsonElement dependencies,
        IReadOnlyDictionary<string, DependencyManifestEntry> libraries)
    {
        var resolved = new List<string>();
        foreach (var dependency in dependencies.EnumerateObject())
        {
            if (dependency.Name.Length == 0
                || dependency.Name.Length > 200
                || dependency.Name.Contains('/', StringComparison.Ordinal)
                || dependency.Name.Any(char.IsControl)
                || dependency.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"Asura.deps.json target component {identity} has "
                    + "a malformed dependency reference.");
            }

            var version = dependency.Value.GetString();
            if (string.IsNullOrWhiteSpace(version)
                || version.Length > 100
                || version.Any(char.IsControl)
                || !libraries.ContainsKey($"{dependency.Name}/{version}"))
            {
                throw new InvalidDataException(
                    $"Asura.deps.json target component {identity} references "
                    + $"unknown dependency {dependency.Name}/{version}.");
            }

            resolved.Add($"{dependency.Name}/{version}");
        }

        return resolved;
    }

    private static void ValidateTargetAssetMetadata(
        string identity,
        string group,
        string assetPath,
        JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Asura.deps.json target asset {identity}:{assetPath} "
                + "must have object metadata.");
        }

        var allowedProperties = group switch
        {
            "runtime" => new HashSet<string>(
                ["assemblyVersion", "fileVersion"],
                StringComparer.Ordinal),
            "native" => new HashSet<string>(["fileVersion"], StringComparer.Ordinal),
            "resources" => new HashSet<string>(["locale"], StringComparer.Ordinal),
            "runtimeTargets" => new HashSet<string>(
                ["assetType", "rid"],
                StringComparer.Ordinal),
            _ => throw new InvalidOperationException(
                $"Unexpected target asset group {group}."),
        };
        foreach (var property in metadata.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name)
                || property.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.Value.GetString())
                || property.Value.GetString()!.Length > 100
                || property.Value.GetString()!.Any(char.IsControl))
            {
                throw new InvalidDataException(
                    $"Asura.deps.json target asset {identity}:{assetPath} "
                    + $"has malformed {group} metadata.");
            }
        }

        var hasExpectedShape = group switch
        {
            "runtime" => !metadata.EnumerateObject().Any()
                         || HasExactJsonProperties(
                             metadata,
                             ["assemblyVersion", "fileVersion"]),
            "native" => HasExactJsonProperties(metadata, ["fileVersion"]),
            "resources" => HasExactJsonProperties(metadata, ["locale"]),
            "runtimeTargets" => HasExactJsonProperties(
                                    metadata,
                                    ["assetType", "rid"])
                                && (metadata.GetProperty("assetType").GetString()
                                    is "runtime" or "native"),
            _ => false,
        };
        if (!hasExpectedShape)
        {
            throw new InvalidDataException(
                $"Asura.deps.json target asset {identity}:{assetPath} "
                + $"has malformed {group} metadata shape.");
        }
    }

    private static void ValidateManifestAssetPath(
        string identity,
        string group,
        string path)
    {
        if (!IsSafeRelativePath(path, 1_000))
        {
            throw new InvalidDataException(
                $"Asura.deps.json target component {identity} has "
                + $"an unsafe {group} asset path.");
        }
    }

    private static void RequireExactJsonProperties(
        JsonElement element,
        IReadOnlyCollection<string> expected,
        string description)
    {
        if (!HasExactJsonProperties(element, expected))
        {
            throw new InvalidDataException(
                $"{description} has unexpected properties.");
        }
    }

    private static bool HasExactJsonProperties(
        JsonElement element,
        IReadOnlyCollection<string> expected)
    {
        var actual = element.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        return actual.Count == expected.Count
               && actual.SetEquals(expected);
    }

    private static string ReadRequiredManifestString(
        string identity,
        JsonElement value,
        string propertyName,
        bool allowEmpty = false)
    {
        if (!value.TryGetProperty(propertyName, out var node)
            || node.ValueKind != JsonValueKind.String
            || node.GetString() is not { } text
            || (!allowEmpty && string.IsNullOrWhiteSpace(text))
            || text.Length > 300
            || text.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Asura.deps.json {identity} has malformed {propertyName}.");
        }

        return text;
    }

    private static string? ReadOptionalManifestString(
        string identity,
        JsonElement library,
        string propertyName)
    {
        if (!library.TryGetProperty(propertyName, out var node))
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(node.GetString()))
        {
            throw new InvalidDataException(
                $"Asura.deps.json library {identity} has malformed {propertyName}.");
        }

        return node.GetString();
    }

    private static void ValidateExactDependencySet(
        IReadOnlyList<CatalogDependency> catalogDependencies,
        IReadOnlyDictionary<string, DependencyManifestEntry> manifest)
    {
        var expected = catalogDependencies
            .Select(component => component.Identity)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = manifest.Keys
            .Where(identity => !expected.Contains(identity))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missing = expected
            .Where(identity => !manifest.ContainsKey(identity))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length != 0 || missing.Length != 0)
        {
            throw new InvalidDataException(
                "Asura.deps.json does not exactly match the reviewed managed-component "
                + $"catalog. Unknown: {FormatIdentities(unknown)}. "
                + $"Missing: {FormatIdentities(missing)}.");
        }

        foreach (var component in catalogDependencies)
        {
            var actualType = manifest[component.Identity].Type;
            if (!string.Equals(
                    component.DepsType,
                    actualType,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Asura.deps.json library {component.Identity} has type "
                    + $"{actualType}; expected {component.DepsType}.");
            }
        }
    }

    private static PackageEvidence ValidateProject(
        string publishDirectory,
        CatalogDependency component,
        DependencyManifestEntry manifest,
        TargetProfile profile)
    {
        if (!string.Equals(manifest.Type, "project", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Managed project {component.Identity} has an unexpected dependency type.");
        }

        var file = component.File!;
        var checksum = HashPublishedFile(
            Path.Combine(publishDirectory, file),
            file);
        var (name, version) = ParseIdentity(component.Identity);
        return new PackageEvidence(
            component.Identity,
            name,
            version,
            component.LicenseDeclared,
            NoAssertion,
            checksum,
            $"SHA-256 was computed from the published project assembly {file}.",
            null,
            "Managed project assembly from Asura.",
            IsRoot: string.Equals(file, profile.Root + ".dll", StringComparison.Ordinal));
    }

    private static PackageEvidence ValidateNuGetPackage(
        string nugetPackageRoot,
        CatalogDependency component,
        DependencyManifestEntry manifest,
        EvidenceAccumulator evidence)
    {
        if (string.Equals(component.Kind, "nuget"
, StringComparison.Ordinal) && !string.Equals(
                manifest.ContentHash,
                component.ContentHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Dependency content hash mismatch for {component.Identity}.");
        }

        var (_, version) = ParseIdentity(component.Identity);
        var packageId = component.NuGetId!;
        var normalizedId = packageId.ToLowerInvariant();
        var normalizedVersion = version.ToLowerInvariant();
        var expectedPackagePath = $"{normalizedId}/{normalizedVersion}";
        var expectedHashPath =
            $"{normalizedId}.{normalizedVersion}.nupkg.sha512";
        if (string.Equals(component.Kind, "nuget"
, StringComparison.Ordinal) && (!string.Equals(
                    manifest.PackagePath,
                    expectedPackagePath,
                    StringComparison.Ordinal)
                || !string.Equals(
                    manifest.HashPath,
                    expectedHashPath,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Dependency package path metadata mismatch for {component.Identity}.");
        }

        var packageDirectory = Path.Combine(
            nugetPackageRoot,
            normalizedId,
            normalizedVersion);
        var packageStem = $"{normalizedId}.{normalizedVersion}";
        var nupkgPath = Path.Combine(packageDirectory, $"{packageStem}.nupkg");
        var hashPath = Path.Combine(packageDirectory, $"{packageStem}.nupkg.sha512");
        var metadataPath = Path.Combine(packageDirectory, ".nupkg.metadata");

        var hashReceipt = ReadBoundedText(
            hashPath,
            MaximumMetadataBytes,
            $"{component.Identity} NuGet hash receipt").Trim();
        if (!string.Equals(
                hashReceipt,
                component.NupkgSha512,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"NuGet hash receipt mismatch for {component.Identity}.");
        }

        using var packageStream = RegularPackageFileReader.Open(
            nupkgPath,
            out var packageFile);
        if (packageFile.Length <= 0 || packageFile.Length > MaximumNupkgBytes)
        {
            throw new InvalidDataException(
                $"NuGet package {component.Identity} is outside the allowed size range.");
        }

        var actualNupkgHash = SHA512.HashData(packageStream);
        if (!CryptographicOperations.FixedTimeEquals(
                actualNupkgHash,
                Convert.FromBase64String(component.NupkgSha512!)))
        {
            throw new InvalidDataException(
                $"NuGet package SHA-512 mismatch for {component.Identity}.");
        }

        var metadataBytes = ReadRegularFile(
            metadataPath,
            MaximumMetadataBytes,
            $"{component.Identity} NuGet metadata");
        ValidateNuGetMetadata(
            component.Identity,
            component.ContentHash!,
            metadataBytes);

        packageStream.Position = 0;
        ValidateNuspecAndExtractNotices(
            packageStream,
            component,
            version,
            evidence);
        return new PackageEvidence(
            component.Identity,
            packageId,
            version,
            component.LicenseDeclared,
            NoAssertion,
            new PackageChecksum(
                "SHA512",
                Convert.ToHexString(actualNupkgHash).ToLowerInvariant()),
            $"Validated NuGet nupkg SHA-512 {component.NupkgSha512}, "
            + $"NuGet contentHash {component.ContentHash}, and nuspec license metadata "
            + $"{component.NuspecLicenseType}:{component.NuspecLicense}.",
            $"pkg:nuget/{Uri.EscapeDataString(packageId)}@{Uri.EscapeDataString(version)}",
string.Equals(component.Kind, "runtime"
, StringComparison.Ordinal) ? "The runtime package may contain components under additional terms; "
                  + "licenseDeclared remains NOASSERTION."
                : null,
            IsRoot: false);
    }

    private static void ValidateNuGetMetadata(
        string identity,
        string expectedContentHash,
        byte[] metadataBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(
                metadataBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            ValidateNoDuplicateJsonProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "contentHash",
                    out var hashNode)
                || hashNode.ValueKind != JsonValueKind.String
                || !string.Equals(
                    hashNode.GetString(),
                    expectedContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"NuGet contentHash mismatch for {identity}.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"NuGet metadata is malformed for {identity}.",
                exception);
        }
    }

    private static void ValidateNuspecAndExtractNotices(
        Stream packageStream,
        CatalogDependency component,
        string version,
        EvidenceAccumulator evidence)
    {
        try
        {
            using var archive = new ZipArchive(
                packageStream,
                ZipArchiveMode.Read,
                leaveOpen: true);
            if (archive.Entries.Count == 0
                || archive.Entries.Count > MaximumArchiveEntries)
            {
                throw new InvalidDataException(
                    $"NuGet package {component.Identity} has an invalid archive entry count.");
            }

            var entries = new Dictionary<string, ZipArchiveEntry>(
                StringComparer.Ordinal);
            var nuspecEntries = new List<ZipArchiveEntry>();
            long uncompressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length < 0
                    || entry.Length > MacOsAppBundleBuilder.MaximumPackageBytes
                    || entry.CompressedLength < 0
                    || entry.CompressedLength > MaximumNupkgBytes)
                {
                    throw new InvalidDataException(
                        $"NuGet package {component.Identity} has an archive entry "
                        + "outside the allowed size range.");
                }

                try
                {
                    uncompressedBytes = checked(uncompressedBytes + entry.Length);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException(
                        $"NuGet package {component.Identity} archive size overflowed.",
                        exception);
                }

                if (uncompressedBytes > MacOsAppBundleBuilder.MaximumPackageBytes)
                {
                    throw new InvalidDataException(
                        $"NuGet package {component.Identity} has too much uncompressed content.");
                }

                if (!entries.TryAdd(entry.FullName, entry))
                {
                    throw new InvalidDataException(
                        $"NuGet package {component.Identity} has duplicate archive entry "
                        + $"{entry.FullName}.");
                }

                if (!entry.FullName.Contains('/', StringComparison.Ordinal)
                    && entry.FullName.EndsWith(
                        ".nuspec",
                        StringComparison.OrdinalIgnoreCase))
                {
                    nuspecEntries.Add(entry);
                }
            }

            if (nuspecEntries.Count != 1)
            {
                throw new InvalidDataException(
                    $"NuGet package {component.Identity} must contain one unambiguous root nuspec.");
            }

            var nuspecBytes = ReadArchiveEntry(
                nuspecEntries[0],
                1,
                MaximumNuspecBytes,
                component.Identity,
                "nuspec");
            ValidateNuspec(component, version, nuspecBytes);
            foreach (var notice in component.Notices)
            {
                var matches = archive.Entries
                    .Where(entry => string.Equals(
                        entry.FullName,
                        notice.ArchivePath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length != 1
                    || !string.Equals(
                        matches[0].FullName,
                        notice.ArchivePath,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"NuGet package {component.Identity} has missing or ambiguous evidence "
                        + $"{notice.ArchivePath}.");
                }

                var entry = matches[0];
                var entryLength = ValidateArchiveEntrySize(
                    entry,
                    notice.MinimumBytes,
                    MaximumNoticeBytes,
                    component.Identity,
                    notice.ArchivePath);
                evidence.EnsureCanAdd(notice.OutputPath, entryLength);
                var content = ReadArchiveEntry(
                    entry,
                    notice.MinimumBytes,
                    MaximumNoticeBytes,
                    component.Identity,
                    notice.ArchivePath);
                var actualHash = Convert.ToHexString(
                        SHA256.HashData(content))
                    .ToLowerInvariant();
                if (!string.Equals(
                        actualHash,
                        notice.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"NuGet package evidence hash mismatch for "
                        + $"{component.Identity}:{notice.ArchivePath}.");
                }

                evidence.Add(notice.OutputPath, content);
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                or NotSupportedException
                or XmlException)
        {
            throw new InvalidDataException(
                $"NuGet package archive is malformed for {component.Identity}.",
                exception);
        }
    }

    private static void ValidateNuspec(
        CatalogDependency component,
        string version,
        byte[] nuspecBytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = false,
            MaxCharactersInDocument = MaximumNuspecBytes,
            XmlResolver = null,
        };
        using var stream = new MemoryStream(nuspecBytes, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root is null
            || !string.Equals(root.Name.LocalName, "package"
, StringComparison.Ordinal) || !SupportedNuspecNamespaces.Contains(root.Name.NamespaceName)
            || root.DescendantsAndSelf().Any(element =>
                element.Name.Namespace != root.Name.Namespace))
        {
            throw new InvalidDataException(
                $"Nuspec for {component.Identity} has an invalid package root or namespace.");
        }

        var nuspecNamespace = root.Name.Namespace;
        var metadata = SingleElement(
            root,
            nuspecNamespace + "metadata",
            component.Identity);
        var id = SingleElement(
            metadata,
            nuspecNamespace + "id",
            component.Identity).Value;
        var nuspecVersion = SingleElement(
            metadata,
            nuspecNamespace + "version",
            component.Identity).Value;
        var license = SingleElement(
            metadata,
            nuspecNamespace + "license",
            component.Identity);
        var licenseTypeAttributes = license.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .ToArray();
        if (licenseTypeAttributes.Length != 1
            || licenseTypeAttributes[0].Name != "type"
            || !string.Equals(
                id,
                component.NuGetId,
                StringComparison.Ordinal)
            || !string.Equals(nuspecVersion, version, StringComparison.Ordinal)
            || !string.Equals(
                licenseTypeAttributes[0].Value,
                component.NuspecLicenseType,
                StringComparison.Ordinal)
            || !string.Equals(
                license.Value,
                component.NuspecLicense,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Nuspec identity, version, or license metadata mismatch for "
                + $"{component.Identity}.");
        }
    }

    private static XElement SingleElement(
        XContainer? parent,
        XName name,
        string identity)
    {
        var matches = parent?.Elements()
                          .Where(element => element.Name == name)
                          .ToArray()
                      ?? [];
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Nuspec for {identity} must contain one {name.LocalName} element.");
        }

        return matches[0];
    }

    private static byte[] ReadArchiveEntry(
        ZipArchiveEntry entry,
        int minimumBytes,
        int maximumBytes,
        string identity,
        string description)
    {
        var length = ValidateArchiveEntrySize(
            entry,
            minimumBytes,
            maximumBytes,
            identity,
            description);
        var content = new byte[length];
        using var stream = entry.Open();
        stream.ReadExactly(content);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"NuGet package {identity} {description} changed while it was read.");
        }

        return content;
    }

    private static int ValidateArchiveEntrySize(
        ZipArchiveEntry entry,
        int minimumBytes,
        int maximumBytes,
        string identity,
        string description)
    {
        if (entry.Length < minimumBytes
            || entry.Length > maximumBytes
            || entry.CompressedLength < 0
            || entry.CompressedLength > MaximumNupkgBytes
            || entry.Length > int.MaxValue)
        {
            throw new InvalidDataException(
                $"NuGet package {identity} {description} is outside the allowed size range.");
        }

        return (int)entry.Length;
    }

    private static PackageEvidence ValidateNativeComponent(
        string publishDirectory,
        string licenseDirectory,
        CatalogNativeComponent component)
    {
        var checksum = HashPublishedFile(
            Path.Combine(publishDirectory, component.File),
            component.File);
        if (component.LicenseEvidenceFile is not null)
        {
            var licenseBytes = ReadRegularFile(
                Path.Combine(
                    licenseDirectory,
                    component.LicenseEvidenceFile),
                MaximumNoticeBytes,
                $"{component.Identity} license evidence");
            if (licenseBytes.Length < component.LicenseEvidenceMinimumBytes!.Value
                || !string.Equals(
                    Convert.ToHexString(SHA256.HashData(licenseBytes))
                        .ToLowerInvariant(),
                    component.LicenseEvidenceSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Published license evidence mismatch for {component.Identity}.");
            }
        }

        var (name, version) = ParseIdentity(component.Identity);
        return new PackageEvidence(
            component.Identity,
            name,
            version,
            component.LicenseDeclared,
            component.DownloadLocation,
            checksum,
            $"SHA-256 was computed from the published native library {component.File}.",
            null,
            component.Comment,
            IsRoot: false);
    }

    private static PackageChecksum HashPublishedFile(
        string path,
        string description)
    {
        using var stream = RegularPackageFileReader.Open(path, out var file);
        if (file.Length <= 0 || file.Length > MacOsAppBundleBuilder.MaximumPackageBytes)
        {
            throw new InvalidDataException(
                $"Published component {description} is outside the allowed size range.");
        }

        return new PackageChecksum(
            "SHA256",
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    private static void ValidateRequiredProjectSet(
        IReadOnlyList<CatalogDependency> dependencies, TargetProfile profile)
    {
        var projectFiles = dependencies
            .Where(component => string.Equals(component.Kind, "project", StringComparison.Ordinal))
            .Select(component => component.File!)
            .ToHashSet(StringComparer.Ordinal);
        if (!projectFiles.SetEquals(profile.ProjectFiles))
        {
            throw CatalogError(
                "project entries must model the exact Asura and vendored binding assemblies");
        }

        var runtimeComponents = dependencies
            .Where(component => string.Equals(component.Kind, "runtime", StringComparison.Ordinal))
            .ToArray();
        // Desktop hosting now includes the optional ASP.NET Core MCP transport.
        // Backend packages still contain exactly the .NET runtime, with no HTTP host.
        var expectedRuntimes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft.NETCore.App.Runtime." + profile.Rid,
        };
        if (string.Equals(profile.Root, "Asura", StringComparison.Ordinal))
        {
            expectedRuntimes.Add("Microsoft.AspNetCore.App.Runtime." + profile.Rid);
        }

        if (runtimeComponents.Length != expectedRuntimes.Count
            || !expectedRuntimes.SetEquals(runtimeComponents.Select(component => component.NuGetId!)))
        {
            throw CatalogError(
                "dependencies must model the exact runtime packages for this product");
        }
    }

    private static void ValidateRequiredNoticeSet(
        IReadOnlyList<CatalogDependency> dependencies, TargetProfile profile)
    {
        foreach (var requiredId in profile.NoticePackages)
        {
            var component = dependencies.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.NuGetId,
                    requiredId,
                    StringComparison.Ordinal)) ?? throw CatalogError(
                    $"dependencies must include reviewed notices for {requiredId}");
            var archivePaths = component.Notices
                .Select(notice => notice.ArchivePath)
                .ToHashSet(StringComparer.Ordinal);
            if (component.Notices.Count != 2
                || !archivePaths.SetEquals(
                ["LICENSE.txt", "THIRD-PARTY-NOTICES.txt"]))
            {
                throw CatalogError(
                    $"component {component.Identity} must include exact license and notice evidence");
            }
        }
    }

    private static void ValidateRequiredNativeSet(
        IReadOnlyList<CatalogNativeComponent> components, TargetProfile profile)
    {
        var files = components
            .Select(component => component.File)
            .ToHashSet(StringComparer.Ordinal);
        if (components.Count != profile.NativeFiles.Length
            || !files.SetEquals(profile.NativeFiles))
        {
            throw CatalogError(
                "additionalComponents must model the published libghostty-vt payload");
        }
    }

    private static string ComputeEvidenceDigest(
        byte[] catalogBytes,
        byte[] dependenciesBytes,
        IReadOnlyList<PackageEvidence> packages,
        IReadOnlyList<ManagedComponentEvidenceFile> extractedFiles)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestValue(
            hash,
            "generator",
            Encoding.UTF8.GetBytes(GeneratorCreator));
        AppendDigestValue(hash, "catalog", catalogBytes);
        AppendDigestValue(hash, "dependencies", dependenciesBytes);
        foreach (var package in packages
                     .OrderBy(package => package.Identity, StringComparer.Ordinal))
        {
            AppendDigestValue(
                hash,
                $"package:{package.Identity}:{package.Checksum.Algorithm}",
                Encoding.UTF8.GetBytes(package.Checksum.Value));
        }

        foreach (var file in extractedFiles
                     .OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            AppendDigestValue(hash, $"evidence:{file.RelativePath}", file.Content);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendDigestValue(
        IncrementalHash hash,
        string label,
        byte[] value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(label));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(
            value.Length.ToString(CultureInfo.InvariantCulture)));
        hash.AppendData([0]);
        hash.AppendData(value);
        hash.AppendData([0]);
    }

    private static byte[] WriteSpdx(
        CatalogDocument catalog,
        IReadOnlyList<PackageEvidence> packages,
        string namespaceDigest,
        long maximumBytes)
    {
        var orderedPackages = packages
            .OrderBy(package => package.Identity, StringComparer.Ordinal)
            .ToArray();
        var rootPackages = orderedPackages
            .Where(package => package.IsRoot)
            .ToArray();
        if (rootPackages.Length != 1)
        {
            throw new InvalidDataException(
                "The managed-component evidence must contain exactly one Asura root.");
        }

        var rootPackage = rootPackages[0];
        var identifiers = orderedPackages.ToDictionary(
            package => package.Identity,
            package => CreateSpdxIdentifier(package.Identity),
            StringComparer.Ordinal);
        EnsureUnique(identifiers.Values, "SPDX package identifier");

        var buffer = new BoundedBufferWriter(maximumBytes);
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Indented = true,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("spdxVersion", "SPDX-2.3");
            writer.WriteString("dataLicense", "CC0-1.0");
            writer.WriteString("SPDXID", "SPDXRef-DOCUMENT");
            writer.WriteString("name", catalog.DocumentName);
            writer.WriteString(
                "documentNamespace",
                $"{catalog.NamespaceBase}/{namespaceDigest}");
            writer.WritePropertyName("creationInfo");
            writer.WriteStartObject();
            writer.WriteString("created", catalog.DocumentCreatedUtc);
            writer.WritePropertyName("creators");
            writer.WriteStartArray();
            writer.WriteStringValue(GeneratorCreator);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString(
                "comment",
                "Evidence scope: the exact managed dependency closure from "
                + "Asura.deps.json, the exact .NET runtime package, the reviewed "
                + "project assemblies, and the published libghostty-vt dylib. "
                + "This is not legal clearance or a complete native dependency SBOM. "
                + "Release blockers: "
                + string.Join(" | ", catalog.ReleaseBlockers));
            writer.WritePropertyName("packages");
            writer.WriteStartArray();
            foreach (var package in orderedPackages)
            {
                writer.WriteStartObject();
                writer.WriteString("name", package.Name);
                writer.WriteString("SPDXID", identifiers[package.Identity]);
                writer.WriteString("versionInfo", package.Version);
                writer.WriteString("downloadLocation", package.DownloadLocation);
                writer.WriteBoolean("filesAnalyzed", false);
                writer.WritePropertyName("checksums");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("algorithm", package.Checksum.Algorithm);
                writer.WriteString("checksumValue", package.Checksum.Value);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteString("licenseConcluded", NoAssertion);
                writer.WriteString("licenseDeclared", package.LicenseDeclared);
                writer.WriteString("copyrightText", NoAssertion);
                writer.WriteString("sourceInfo", package.SourceInfo);
                if (package.ExternalReference is not null)
                {
                    writer.WritePropertyName("externalRefs");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("referenceCategory", "PACKAGE-MANAGER");
                    writer.WriteString("referenceType", "purl");
                    writer.WriteString(
                        "referenceLocator",
                        package.ExternalReference);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }

                if (package.Comment is not null)
                {
                    writer.WriteString("comment", package.Comment);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("relationships");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("spdxElementId", "SPDXRef-DOCUMENT");
            writer.WriteString("relationshipType", "DESCRIBES");
            writer.WriteString(
                "relatedSpdxElement",
                identifiers[rootPackage.Identity]);
            writer.WriteEndObject();
            foreach (var package in orderedPackages.Where(package => !package.IsRoot))
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "spdxElementId",
                    identifiers[rootPackage.Identity]);
                writer.WriteString("relationshipType", "DEPENDS_ON");
                writer.WriteString(
                    "relatedSpdxElement",
                    identifiers[package.Identity]);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArrayWithNewline();
    }

    private static string CreateSpdxIdentifier(string identity)
    {
        var builder = new StringBuilder("SPDXRef-Package-");
        foreach (var character in identity)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-'
                    ? character
                    : '-');
        }

        return builder.ToString();
    }

    private static byte[] ReadRegularFile(
        string path,
        int maximumBytes,
        string description)
    {
        using var stream = RegularPackageFileReader.Open(path, out var file);
        if (file.Length <= 0
            || file.Length > maximumBytes
            || file.Length > int.MaxValue)
        {
            throw new InvalidDataException(
                $"The {description} is outside the allowed size range.");
        }

        var bytes = new byte[(int)file.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"The {description} changed while it was read.");
        }

        return bytes;
    }

    private static string ReadBoundedText(
        string path,
        int maximumBytes,
        string description) =>
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true)
            .GetString(ReadRegularFile(path, maximumBytes, description));

    private static void ValidateNoDuplicateJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException(
                        $"Duplicate JSON property {property.Name}.");
                }

                ValidateNoDuplicateJsonProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateJsonProperties(item);
            }
        }
    }

    private static (string Name, string Version) ParseIdentity(string identity)
    {
        ValidateText(identity, "component identity", 300);
        var separator = identity.LastIndexOf('/');
        if (separator <= 0
            || separator == identity.Length - 1
            || identity.IndexOf('/', StringComparison.Ordinal) != separator)
        {
            throw CatalogError(
                $"component identity {identity} must be name/version");
        }

        var name = identity[..separator];
        var version = identity[(separator + 1)..];
        ValidateText(name, $"{identity} name", 200);
        ValidateText(version, $"{identity} version", 100);
        return (name, version);
    }

    private static void ValidateText(
        string value,
        string description,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw CatalogError($"{description} is invalid");
        }
    }

    private static void ValidateLicenseValue(
        string value,
        string description)
    {
        ValidateText(value, description, 500);
        if (value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character is '-' or '.' or '+' or '(' or ')' or ' ')))
        {
            throw CatalogError($"{description} contains unsupported characters");
        }
    }

    private static void ValidateNuGetSegment(
        string value,
        string identity,
        string field)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 200
            || value is "." or ".."
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character is '.' or '-' or '_' or '+')))
        {
            throw CatalogError(
                $"component {identity} has invalid {field}");
        }
    }

    private static void ValidateSimpleFileName(
        string value,
        string identity)
    {
        if (string.IsNullOrEmpty(value)
            || !string.Equals(value, Path.GetFileName(value)
, StringComparison.Ordinal) || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw CatalogError(
                $"component {identity} has an invalid file name");
        }
    }

    private static void ValidateArchivePath(
        string path,
        string identity)
    {
        if (!IsSafeRelativePath(path, 300))
        {
            throw CatalogError(
                $"component {identity} has an invalid archivePath");
        }
    }

    private static bool IsSafeRelativePath(string path, int maximumLength) =>
        !string.IsNullOrEmpty(path)
        && path.Length <= maximumLength
        && !Path.IsPathFullyQualified(path)
        && !path.StartsWith('/')
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains(':', StringComparison.Ordinal)
        && !path.Any(char.IsControl)
        && !path.Split('/').Any(segment =>
            segment.Length == 0 || segment is "." or "..");

    private static void ValidateRelativeOutputPath(
        string path,
        string identity)
    {
        ValidateArchivePath(path, identity);
        if (string.Equals(path, SpdxFileName, StringComparison.Ordinal))
        {
            throw CatalogError(
                $"component {identity} outputPath collides with the SPDX document");
        }
    }

    private static void ValidateBase64Sha512(
        string value,
        string identity,
        string field)
    {
        try
        {
            if (Convert.FromBase64String(value).Length != 64
                || !string.Equals(Convert.ToBase64String(Convert.FromBase64String(value)), value, StringComparison.Ordinal))
            {
                throw CatalogError(
                    $"component {identity} has invalid {field}");
            }
        }
        catch (FormatException exception)
        {
            throw CatalogError(
                $"component {identity} has invalid {field}",
                exception);
        }
    }

    private static void ValidateSha256(
        string value,
        string identity,
        string field)
    {
        if (value.Length != 64
            || value.Any(character =>
                !(character is >= '0' and <= '9'
                  || character is >= 'a' and <= 'f')))
        {
            throw CatalogError(
                $"component {identity} has invalid {field}");
        }
    }

    private static void EnsureUnique(
        IEnumerable<string> values,
        string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw CatalogError($"{description} {value} is duplicated");
            }
        }
    }

    private static void RequirePresent(
        string? value,
        string identity,
        string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw CatalogError(
                $"component {identity} is missing {field}");
        }
    }

    private static void RequireAbsent(
        string? value,
        string identity,
        string field)
    {
        if (value is not null)
        {
            throw CatalogError(
                $"component {identity} has unexpected {field}");
        }
    }

    private static void RequireEmpty(
        IReadOnlyList<CatalogNotice> values,
        string identity,
        string field)
    {
        if (values.Count != 0)
        {
            throw CatalogError(
                $"component {identity} has unexpected {field}");
        }
    }

    private static void RequireEqual(
        string? actual,
        string? expected,
        string identity,
        string field)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw CatalogError(
                $"component {identity} has invalid {field}");
        }
    }

    private static InvalidDataException CatalogError(
        string message,
        Exception? innerException = null) =>
        new(
            $"The managed-component catalog is invalid: {message}.",
            innerException);

    private static string FormatIdentities(IReadOnlyList<string> values) =>
        values.Count == 0 ? "(none)" : string.Join(", ", values);

    private static void ValidateEvidenceLimits(ManagedComponentEvidenceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumFiles < 1
            || limits.MaximumFiles > MaximumGeneratedEvidenceFiles
            || limits.MaximumEntries < 1
            || limits.MaximumBytes < 1
            || limits.MaximumBytes > MaximumGeneratedEvidenceBytes
            || limits.MaximumRelativePathDepth < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "Managed evidence limits are outside the supported range.");
        }
    }

    private static string CreateGeneratorCreator()
    {
        var version = typeof(ManagedComponentEvidenceBuilder)
            .Assembly
            .GetName()
            .Version
            ?? throw new InvalidOperationException(
                "Asura.Packaging has no assembly version.");
        if (version.Major < 0 || version.Minor < 0 || version.Build < 0)
        {
            throw new InvalidOperationException(
                "Asura.Packaging has an incomplete assembly version.");
        }

        return FormattableString.Invariant(
            $"Tool: Asura.Packaging-{version.Major}.{version.Minor}.{version.Build}");
    }

    private sealed class EvidenceAccumulator
    {
        private readonly ManagedComponentEvidenceLimits _limits;
        private readonly List<ManagedComponentEvidenceFile> _files = [];
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
        private readonly HashSet<string> _reservedPaths = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        private long _bytes;

        public EvidenceAccumulator(
            ManagedComponentEvidenceLimits limits,
            params string[] reservedPaths)
        {
            _limits = limits;
            foreach (var path in reservedPaths)
            {
                if (!IsSafeRelativePath(path, 300))
                {
                    throw new InvalidDataException(
                        $"Managed evidence reserved path {path} is invalid.");
                }

                if (!_paths.Add(path) || !_reservedPaths.Add(path))
                {
                    throw new InvalidDataException(
                        $"Managed evidence path {path} is reserved more than once.");
                }

                AddParentDirectories(path, _directories);
            }

            EnsureShapeWithinLimits();
        }

        public IReadOnlyList<ManagedComponentEvidenceFile> Files => _files;

        public long RemainingBytes => _limits.MaximumBytes - _bytes;

        public void EnsureCanAdd(string relativePath, int length)
        {
            if (length < 0)
            {
                throw new InvalidDataException(
                    "Managed evidence has a negative byte count.");
            }

            if (_paths.Contains(relativePath))
            {
                throw new InvalidDataException(
                    $"Managed evidence path {relativePath} is duplicated.");
            }

            var candidateDirectories = new HashSet<string>(
                _directories,
                StringComparer.Ordinal);
            AddParentDirectories(relativePath, candidateDirectories);
            var candidateFiles = checked(
                _files.Count + _reservedPaths.Count + 1);
            var candidateEntries = checked(
                candidateFiles + candidateDirectories.Count);
            long candidateBytes;
            try
            {
                candidateBytes = checked(_bytes + length);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "Managed evidence byte count overflowed.",
                    exception);
            }

            if (candidateFiles > _limits.MaximumFiles
                || candidateEntries > _limits.MaximumEntries
                || candidateBytes > _limits.MaximumBytes
                || RelativePathDepth(relativePath)
                    > _limits.MaximumRelativePathDepth)
            {
                throw new InvalidDataException(
                    "Managed evidence exceeds its incremental file, entry, byte, "
                    + "or path-depth budget.");
            }
        }

        public void Add(string relativePath, byte[] content)
        {
            ArgumentNullException.ThrowIfNull(content);
            EnsureCanAdd(relativePath, content.Length);
            _paths.Add(relativePath);
            AddParentDirectories(relativePath, _directories);
            _bytes = checked(_bytes + content.Length);
            _files.Add(new ManagedComponentEvidenceFile(relativePath, content));
        }

        public void CompleteReserved(string relativePath, byte[] content)
        {
            ArgumentNullException.ThrowIfNull(content);
            if (!_reservedPaths.Remove(relativePath))
            {
                throw new InvalidOperationException(
                    $"Managed evidence path {relativePath} was not reserved.");
            }

            long candidateBytes;
            try
            {
                candidateBytes = checked(_bytes + content.Length);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "Managed evidence byte count overflowed.",
                    exception);
            }

            if (candidateBytes > _limits.MaximumBytes)
            {
                throw new InvalidDataException(
                    "Managed evidence exceeds its incremental byte budget.");
            }

            _bytes = candidateBytes;
            _files.Add(new ManagedComponentEvidenceFile(relativePath, content));
        }

        private void EnsureShapeWithinLimits()
        {
            if (_reservedPaths.Count > _limits.MaximumFiles
                || _reservedPaths.Count + _directories.Count
                    > _limits.MaximumEntries
                || _reservedPaths.Any(path =>
                    RelativePathDepth(path) > _limits.MaximumRelativePathDepth))
            {
                throw new InvalidDataException(
                    "Reserved managed evidence exceeds its file, entry, "
                    + "or path-depth budget.");
            }
        }

        private static void AddParentDirectories(
            string relativePath,
            ISet<string> directories)
        {
            var parent = Path.GetDirectoryName(relativePath);
            while (!string.IsNullOrEmpty(parent))
            {
                directories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }

        private static int RelativePathDepth(string path) =>
            path.Count(character => character == '/');
    }

    private sealed class BoundedBufferWriter : IBufferWriter<byte>
    {
        private readonly int _maximumBytes;
        private byte[] _buffer = [];
        private int _written;

        public BoundedBufferWriter(long maximumBytes)
        {
            if (maximumBytes < 1 || maximumBytes > int.MaxValue)
            {
                throw new InvalidDataException(
                    "The SPDX document has no valid byte budget.");
            }

            _maximumBytes = (int)maximumBytes;
        }

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _written)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public byte[] ToArrayWithNewline()
        {
            if (_written >= _maximumBytes)
            {
                throw new InvalidDataException(
                    "The SPDX document exceeds its managed evidence byte budget.");
            }

            var result = new byte[_written + 1];
            _buffer.AsSpan(0, _written).CopyTo(result);
            result[^1] = (byte)'\n';
            return result;
        }

        private void EnsureCapacity(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

            sizeHint = Math.Max(sizeHint, 1);
            if (sizeHint > _maximumBytes - _written)
            {
                throw new InvalidDataException(
                    "The SPDX document exceeds its managed evidence byte budget.");
            }

            var required = _written + sizeHint;
            if (required <= _buffer.Length)
            {
                return;
            }

            var doubled = _buffer.Length == 0
                ? 256
                : checked(_buffer.Length * 2);
            var capacity = Math.Min(
                _maximumBytes,
                Math.Max(required, doubled));
            Array.Resize(ref _buffer, capacity);
        }
    }

    private sealed class CatalogDocument
    {
        public required int SchemaVersion { get; init; }

        public required string DocumentName { get; set; }

        public required string DocumentCreatedUtc { get; init; }

        public required string NamespaceBase { get; set; }

        public required List<string> ReleaseBlockers { get; init; }

        public required List<CatalogDependency> Dependencies { get; init; }

        public required List<CatalogNativeComponent> AdditionalComponents { get; init; }
    }

    private sealed class CatalogDependency
    {
        public required string Identity { get; set; }

        public required string Kind { get; init; }

        public required string DepsType { get; init; }

        public required string LicenseDeclared { get; init; }

        public string? File { get; init; }

        public string? NuGetId { get; init; }

        public string? ContentHash { get; init; }

        public string? NupkgSha512 { get; init; }

        public string? NuspecLicenseType { get; init; }

        public string? NuspecLicense { get; init; }

        public List<CatalogNotice> Notices { get; init; } = [];

    }

    private sealed class CatalogNotice
    {
        public required string ArchivePath { get; init; }

        public required string OutputPath { get; init; }

        public required string Sha256 { get; init; }

        public required int MinimumBytes { get; init; }
    }

    private sealed class CatalogNativeComponent
    {
        public required string Identity { get; init; }

        public required string Kind { get; init; }

        public required string File { get; init; }

        public required string LicenseDeclared { get; init; }

        public required string DownloadLocation { get; init; }

        public required string Comment { get; init; }

        public string? LicenseEvidenceFile { get; init; }

        public string? LicenseEvidenceSha256 { get; init; }

        public int? LicenseEvidenceMinimumBytes { get; init; }
    }

    private sealed record DependencyManifestEntry(
        string Type,
        string? ContentHash,
        string? PackagePath,
        string? HashPath);

    private sealed record DependencyManifest(
        IReadOnlyDictionary<string, DependencyManifestEntry> Libraries);

    private sealed record PackageChecksum(
        string Algorithm,
        string Value);

    private sealed record PackageEvidence(
        string Identity,
        string Name,
        string Version,
        string LicenseDeclared,
        string DownloadLocation,
        PackageChecksum Checksum,
        string SourceInfo,
        string? ExternalReference,
        string? Comment,
        bool IsRoot);
}
