using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShell.Packaging;

namespace GhostShell.AccessibilityAcceptance;

public sealed partial class MacOsAppBundleBuilderTests : IDisposable
{
    private const string NuspecNamespace =
        "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd";
    private const string RuntimeTargetName =
        ".NETCoreApp,Version=v10.0/osx-arm64";

    private static readonly string[] ProjectAssemblyNames =
    [
        "Exclr8Cef",
        "Exclr8Cef.WebView",
        "GhostShell",
        "GhostShell.Agent",
        "GhostShell.Agent.Providers",
        "GhostShell.Agent.Runtime",
        "GhostShell.App",
        "GhostShell.Application",
        "GhostShell.Browser",
        "GhostShell.Core",
        "GhostShell.Databases",
        "GhostShell.Docker",
        "GhostShell.Docking",
        "GhostShell.Files",
        "GhostShell.Git",
        "GhostShell.Infrastructure",
        "GhostShell.Mcp",
        "GhostShell.Monitoring",
        "GhostShell.Previews",
        "GhostShell.Protocol",
        "GhostShell.Redis",
        "GhostShell.SessionHost",
        "GhostShell.Terminal",
        "GhostShell.Updates",
    ];

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ghostshell-package-tests-{Guid.NewGuid():N}");
    private readonly Dictionary<string, EvidenceInputs> _evidenceInputs =
        new(StringComparer.Ordinal);

    public MacOsAppBundleBuilderTests() => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Gateway_metadata_is_packaged_as_resources()
    {
        var publish = CreatePublishPayload();
        var relativeDirectory = Path.Combine("runtimes", "osx-arm64", "native");
        string[] resources = ["workspace-network-gateway-MANIFEST.sha256", "workspace-network-gateway-THIRD-PARTY-NOTICES.md", "workspace-network-gateway-GO-LICENSE.txt"];
        Directory.CreateDirectory(Path.Combine(publish, relativeDirectory));
        foreach (var name in resources)
        {
            File.WriteAllText(Path.Combine(publish, relativeDirectory, name), "gateway metadata");
        }
        var output = OutputPath();
        _ = new MacOsAppBundleBuilder().Build(Request(publish, output));
        foreach (var name in resources)
        {
            Assert.Equal("gateway metadata", File.ReadAllText(Path.Combine(output, "Contents", "Resources", relativeDirectory, name)));
            Assert.False(File.Exists(Path.Combine(output, "Contents", "MacOS", relativeDirectory, name)));
        }
    }

    [Fact]
    public void Workspace_boot_descriptor_and_privacy_manifests_are_resources()
    {
        var publish = CreatePublishPayload();
        var relativeDirectory = Path.Combine("runtimes", "osx-arm64", "workspace-runtime");
        string[] resources = ["boot-assets.json", Path.Combine("dependency.bundle", "PrivacyInfo.xcprivacy")];
        foreach (var name in resources.Append("workspace-runtime").Append("libswift_Concurrency.dylib"))
        {
            var path = Path.Combine(publish, relativeDirectory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "runtime payload");
        }
        var output = OutputPath();
        _ = new MacOsAppBundleBuilder().Build(Request(publish, output));
        foreach (var name in resources)
        {
            Assert.Equal("runtime payload", File.ReadAllText(Path.Combine(output, "Contents", "Resources", relativeDirectory, name)));
            Assert.False(File.Exists(Path.Combine(output, "Contents", "MacOS", relativeDirectory, name)));
        }
        Assert.True(File.Exists(Path.Combine(output, "Contents", "MacOS", relativeDirectory, "workspace-runtime")));
        Assert.True(File.Exists(Path.Combine(output, "Contents", "MacOS", relativeDirectory, "libswift_Concurrency.dylib")));
    }

    [Fact]
    public void Builder_creates_the_exact_acceptance_bundle_without_modifying_publish_payload()
    {
        var publish = CreatePublishPayload();
        var output = OutputPath();
        var result = new MacOsAppBundleBuilder().Build(
            Request(publish, output));

        var inspection = PackageFingerprint.Inspect(
            output,
            TargetPlatform.MacOS,
            "rc-42");

        Assert.Equal("macos-application-bundle", inspection.Build.PackageKind);
        Assert.Equal("app.ghostshell", inspection.Build.ApplicationIdentity);
        Assert.Equal("1.2.3", inspection.Build.ProductVersion);
        Assert.Equal("GhostShell.app", Path.GetFileName(result.DestinationPath));
        Assert.True(Directory.Exists(result.DestinationPath));
        Assert.Equal("42", result.BuildVersion);
        var appIcon = Path.Combine(
            output,
            "Contents",
            "Resources",
            "GhostShell.icns");
        Assert.True(File.Exists(appIcon));
        Assert.Equal(
            "icns",
            Encoding.ASCII.GetString(File.ReadAllBytes(appIcon), 0, 4));
        Assert.Equal(
            "BOMStore",
            Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(
                output,
                "Contents",
                "Resources",
                "Assets.car")), 0, 8));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "Resources",
            "Licenses",
            "ProductIdentity",
            "product-identity.json")));
        var distributionManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(
            output,
            "Contents",
            "Resources",
            "distribution.json")))!.AsObject();
        Assert.Equal(1, (int)distributionManifest["schemaVersion"]!);
        Assert.Equal("github-release", (string?)distributionManifest["source"]);
        Assert.Equal("velopack", (string?)distributionManifest["updateStrategy"]);
        Assert.Equal("app.ghostshell", (string?)distributionManifest["packageId"]);
        Assert.Equal("osx-arm64-stable", (string?)distributionManifest["channel"]);
        Assert.Equal("osx-arm64", (string?)distributionManifest["runtimeIdentifier"]);
        var infoPlist = File.ReadAllText(Path.Combine(
            output,
            "Contents",
            "Info.plist"));
        Assert.Contains(
            "<key>CFBundleIconFile</key>",
            infoPlist,
            StringComparison.Ordinal);
        Assert.Contains(
            "<string>GhostShell</string>",
            infoPlist,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "Resources",
            "Licenses",
            "GHOSTTY-LICENSE")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "Resources",
            "Licenses",
            "THIRD-PARTY-NOTICES.md")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "Resources",
            "Licenses",
            "DOTNET-LICENSE.txt")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "Resources",
            "Licenses",
            "DOTNET-THIRD-PARTY-NOTICES.txt")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "Resources",
            "Licenses",
            "JetBrainsMono-OFL.txt")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "Resources",
            "Licenses",
            "Native",
            "terminal-font-assets.json")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "Resources",
            "Licenses",
            "Native",
            "terminal-font-assets-build-receipt.json")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "Resources",
            "fonts",
            "JetBrainsMono",
            "JetBrainsMono-BoldItalic.ttf")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "Resources",
            "ghostty",
            "shell-integration",
            "fish",
            "vendor_conf.d",
            "ghostty-shell-integration.fish")));
        Assert.False(Directory.Exists(Path.Combine(
            output,
            "Contents",
            "MacOS",
            "ghostty")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "MacOS",
            "GhostShell.Agent.dll")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "MacOS",
            "GhostShell.Agent.Providers.dll")));
        Assert.True(File.Exists(Path.Combine(
            output,
            "Contents",
            "MacOS",
            "GhostShell.Mcp.dll")));
        var licenseDirectory = Path.Combine(
            output,
            "Contents",
            "Resources",
            "Licenses");
        var spdxPath = Path.Combine(licenseDirectory, "SBOM.spdx.json");
        Assert.True(File.Exists(spdxPath));
        Assert.True(new FileInfo(Path.Combine(
            licenseDirectory,
            "HarfBuzzSharp.NativeAssets.macOS-8.3.1.3",
            "LICENSE.txt")).Length >= 1_024);
        Assert.True(new FileInfo(Path.Combine(
            licenseDirectory,
            "SkiaSharp.NativeAssets.macOS-3.119.3-preview.1.1",
            "THIRD-PARTY-NOTICES.txt")).Length >= 100_000);
        Assert.Equal(
            Directory.EnumerateFiles(
                output,
                "*",
                SearchOption.AllDirectories).Count(),
            result.FileCount);
        var spdxText = File.ReadAllText(spdxPath);
        Assert.DoesNotContain(
            _evidenceInputs[publish].NuGetPackageRoot,
            spdxText,
            StringComparison.Ordinal);
        using (var document = JsonDocument.Parse(spdxText))
        {
            Assert.Equal(
                "GhostSHELL 1.2.3 managed-component evidence",
                document.RootElement.GetProperty("name").GetString());
            Assert.StartsWith(
                "https://ghostshell.test/spdx/managed-components/1.2.3/",
                document.RootElement.GetProperty("documentNamespace").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(
                "Tool: GhostShell.Packaging-1.0.0",
                document.RootElement
                    .GetProperty("creationInfo")
                    .GetProperty("creators")
                    .EnumerateArray()
                    .Single()
                    .GetString());
            var packages = document.RootElement
                .GetProperty("packages")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(ProjectAssemblyNames.Length + VendorFixtures.Length + 4, packages.Length);
            AssertProjectPackage(
                packages,
                "Exclr8Cef",
                "Exclr8Cef.dll");
            AssertProjectPackage(
                packages,
                "Exclr8Cef.WebView",
                "Exclr8Cef.WebView.dll");
            AssertProjectPackage(
                packages,
                "GhostShell.Agent",
                "GhostShell.Agent.dll");
            AssertProjectPackage(
                packages,
                "GhostShell.Agent.Providers",
                "GhostShell.Agent.Providers.dll");
            AssertProjectPackage(
                packages,
                "GhostShell.Agent.Runtime",
                "GhostShell.Agent.Runtime.dll");
            AssertProjectPackage(
                packages,
                "GhostShell.Browser",
                "GhostShell.Browser.dll");
            AssertProjectPackage(
                packages,
                "GhostShell.Databases",
                "GhostShell.Databases.dll");
            AssertProjectPackage(
                packages,
                "GhostShell.Docking",
                "GhostShell.Docking.dll");
            AssertProjectPackage(
                packages,
                "GhostShell.Mcp",
                "GhostShell.Mcp.dll");
            AssertProjectPackage(
                packages,
                "GhostShell.Previews",
                "GhostShell.Previews.dll");
            Assert.Single(packages, package =>
                string.Equals(
                    package.GetProperty("name").GetString(),
                    "libghostty-vt",
                    StringComparison.Ordinal)
                && string.Equals(
                    package.GetProperty("licenseDeclared").GetString(),
                    "MIT",
                    StringComparison.Ordinal));
            Assert.Equal(
                "NOASSERTION",
                packages.Single(package =>
                        string.Equals(
                            package.GetProperty("name").GetString(),
                            "Microsoft.NETCore.App.Runtime.osx-arm64",
                            StringComparison.Ordinal))
                    .GetProperty("licenseDeclared")
                    .GetString());
            var relationships = document.RootElement
                .GetProperty("relationships")
                .EnumerateArray()
                .ToArray();
            Assert.Single(relationships, relationship => string.Equals(
                relationship.GetProperty("relationshipType").GetString(),
                "DESCRIBES",
                StringComparison.Ordinal));
            Assert.Equal(
                ProjectAssemblyNames.Length + VendorFixtures.Length + 3,
                relationships.Count(relationship =>
                    string.Equals(
                        relationship.GetProperty("relationshipType").GetString(),
                        "DEPENDS_ON",
                        StringComparison.Ordinal)));
        }

        Assert.False(File.Exists(Path.Combine(
            output,
            "Contents",
            "MacOS",
            "GHOSTTY-LICENSE")));
        Assert.True(File.Exists(Path.Combine(publish, "GHOSTTY-LICENSE")));
        Assert.Equal("publish sentinel", File.ReadAllText(Path.Combine(publish, "sentinel.txt")));
    }

    [Fact]
    public void Builder_emits_byte_identical_managed_evidence_on_a_second_run()
    {
        var publish = CreatePublishPayload();
        var firstOutput = OutputPath();
        var secondOutput = OutputPath();

        _ = new MacOsAppBundleBuilder().Build(
            Request(publish, firstOutput));
        _ = new MacOsAppBundleBuilder().Build(
            Request(publish, secondOutput));

        Assert.Equal(
            File.ReadAllBytes(SpdxPath(firstOutput)),
            File.ReadAllBytes(SpdxPath(secondOutput)));
    }

    [Fact]
    public void Builder_rejects_packaged_legal_evidence_that_differs_from_reviewed_source()
    {
        var publish = CreatePublishPayload();
        File.AppendAllText(
            Path.Combine(publish, "SMBLIBRARY-SOURCE.json"),
            "drift");

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(Request(publish, OutputPath())));

        Assert.Contains(
            "packaged legal file SMBLIBRARY-SOURCE.json differs",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_product_artwork_that_drifted_from_the_reviewed_manifest()
    {
        var publish = CreatePublishPayload();
        var evidence = _evidenceInputs[publish];
        File.AppendAllText(Path.Combine(
            evidence.ProductIdentitySourceRoot,
            "assets",
            "macos",
            "GhostShell.icon",
            "Assets",
            "logo.svg"), "drift");

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(Request(publish, OutputPath())));

        Assert.Contains("identity hash", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_an_icns_fallback_without_every_required_size()
    {
        var publish = CreatePublishPayload();
        var evidence = _evidenceInputs[publish];
        var fallbackPath = Path.Combine(
            evidence.ProductIdentitySourceRoot,
            "assets",
            "macos",
            "GhostShell.icns");
        File.WriteAllBytes(fallbackPath, CreateIcns(["ic10"]));
        UpdateIdentityFileHash(
            evidence.ProductIdentityManifestPath,
            "icns-fallback",
            fallbackPath);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(Request(publish, OutputPath())));

        Assert.Contains("missing required sizes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_a_non_car_adaptive_icon_output()
    {
        var publish = CreatePublishPayload();
        var evidence = _evidenceInputs[publish];
        File.WriteAllBytes(evidence.AssetCatalogPath, "not-a-car"u8.ToArray());

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(Request(publish, OutputPath())));

        Assert.Contains("not an Assets.car", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_the_legacy_fake_ratc_asset_marker()
    {
        var publish = CreatePublishPayload();
        var evidence = _evidenceInputs[publish];
        File.WriteAllBytes(
            evidence.AssetCatalogPath,
            [.. "RATC"u8.ToArray(), 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(Request(publish, OutputPath())));

        Assert.Contains("not an Assets.car", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_identity_fields_that_disagree_with_the_application()
    {
        var publish = CreatePublishPayload();
        var evidence = _evidenceInputs[publish];
        var manifest = JsonNode.Parse(
            File.ReadAllText(evidence.ProductIdentityManifestPath))!.AsObject();
        manifest["bundleIdentifier"] = "test.wrong";
        File.WriteAllText(
            evidence.ProductIdentityManifestPath,
            manifest.ToJsonString());

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(Request(publish, OutputPath())));

        Assert.Contains("bundleIdentifier", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_an_unknown_dependency_not_in_the_reviewed_catalog()
    {
        var publish = CreatePublishPayload();
        var dependenciesPath = Path.Combine(
            publish,
            "GhostShell.deps.json");
        var dependencies = JsonNode.Parse(
            File.ReadAllText(dependenciesPath))!.AsObject();
        dependencies["libraries"]!.AsObject().Add(
            "Unexpected.Package/1.0.0",
            new JsonObject
            {
                ["type"] = "project",
                ["serviceable"] = false,
                ["sha512"] = string.Empty,
            });
        File.WriteAllText(dependenciesPath, dependencies.ToJsonString());
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains(
            "does not exactly match",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Unexpected.Package/1.0.0",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_rejects_a_fixed_first_party_version_in_the_reviewed_catalog()
    {
        var publish = CreatePublishPayload();
        var catalogPath = _evidenceInputs[publish].CatalogPath;
        var catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
        var component = catalog["dependencies"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(node => string.Equals(
                node["identity"]!.GetValue<string>(),
                "GhostShell.Core/${productVersion}",
                StringComparison.Ordinal));
        component["identity"] = "GhostShell.Core/1.2.3";
        File.WriteAllText(catalogPath, catalog.ToJsonString());
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(Request(publish, output)));

        Assert.Contains(
            "identity version is ${productVersion}",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData(4, 100, 67_108_864L)]
    [InlineData(10, 2, 67_108_864L)]
    [InlineData(10, 100, 1_024L)]
    public void Evidence_builder_enforces_incremental_limits_before_retaining_notices(
        int maximumFiles,
        int maximumEntries,
        long maximumBytes)
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];

        var exception = Assert.Throws<InvalidDataException>(() =>
            ManagedComponentEvidenceBuilder.Build(
                publish,
                publish,
                inputs.CatalogPath,
                inputs.NuGetPackageRoot,
                "1.2.3",
                new ManagedComponentEvidenceLimits(
                    maximumFiles,
                    maximumEntries,
                    maximumBytes,
                    MaximumRelativePathDepth: 61),
                inputs.ProductIdentitySourceRoot));

        Assert.Contains(
            "incremental file, entry, byte, or path-depth budget",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_duplicate_notice_archive_paths_before_extraction()
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(
            File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var component = catalog["dependencies"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(node => string.Equals(node["identity"]!.GetValue<string>()
, "SkiaSharp.NativeAssets.macOS/3.119.3-preview.1.1", StringComparison.Ordinal));
        component["notices"]![1]!["archivePath"] = "LICENSE.txt";
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "notice archive path LICENSE.txt is duplicated",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_requires_the_fixed_osx_arm64_runtime_target()
    {
        var publish = CreatePublishPayload();
        var (path, dependencies) = ReadDependencies(publish);
        dependencies["runtimeTarget"]!["name"] =
            ".NETCoreApp,Version=v9.0/osx-arm64";
        WriteDependencies(path, dependencies);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "runtimeTarget must be",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_requires_the_exact_osx_arm64_runtime_fallback_chain()
    {
        var publish = CreatePublishPayload();
        var (path, dependencies) = ReadDependencies(publish);
        dependencies["runtimes"]!["osx-arm64"] =
            new JsonArray("osx", "any", "base");
        WriteDependencies(path, dependencies);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "unexpected osx-arm64 runtime fallback chain",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_requires_selected_target_keys_to_match_libraries()
    {
        var publish = CreatePublishPayload();
        var (path, dependencies) = ReadDependencies(publish);
        SelectedTarget(dependencies).Add(
            "Unexpected.Package/1.0.0",
            new JsonObject());
        WriteDependencies(path, dependencies);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "selected target does not exactly match",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Unexpected.Package/1.0.0",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_an_unresolved_selected_target_dependency()
    {
        var publish = CreatePublishPayload();
        var (path, dependencies) = ReadDependencies(publish);
        var root = SelectedTarget(dependencies)["GhostShell/1.2.3"]!.AsObject();
        root["dependencies"]!["Unexpected.Package"] = "1.0.0";
        WriteDependencies(path, dependencies);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "unknown dependency Unexpected.Package/1.0.0",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_an_unreachable_selected_target_component()
    {
        var publish = CreatePublishPayload();
        var (path, dependencies) = ReadDependencies(publish);
        var root = SelectedTarget(dependencies)["GhostShell/1.2.3"]!.AsObject();
        root["dependencies"]!.AsObject().Remove(
            "HarfBuzzSharp.NativeAssets.macOS");
        WriteDependencies(path, dependencies);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "unreachable components",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "HarfBuzzSharp.NativeAssets.macOS/8.3.1.3",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_a_selected_target_dependency_cycle()
    {
        var publish = CreatePublishPayload();
        var (path, dependencies) = ReadDependencies(publish);
        var component = SelectedTarget(dependencies)[
            "HarfBuzzSharp.NativeAssets.macOS/8.3.1.3"]!.AsObject();
        component["dependencies"] = new JsonObject
        {
            ["GhostShell"] = "1.2.3",
        };
        WriteDependencies(path, dependencies);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "dependency graph contains a cycle",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("runtimes\\osx-arm64\\native\\escape.dylib")]
    [InlineData("lib/\u0001escape.dll")]
    public void Builder_rejects_unsafe_selected_target_asset_paths(
        string unsafePath)
    {
        var publish = CreatePublishPayload();
        var (path, dependencies) = ReadDependencies(publish);
        var root = SelectedTarget(dependencies)["GhostShell/1.2.3"]!.AsObject();
        root["runtime"]!.AsObject().Add(unsafePath, new JsonObject());
        WriteDependencies(path, dependencies);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "unsafe runtime asset path",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_unknown_selected_target_groups()
    {
        var publish = CreatePublishPayload();
        var (path, dependencies) = ReadDependencies(publish);
        var root = SelectedTarget(dependencies)["GhostShell/1.2.3"]!.AsObject();
        root.Add("compile", new JsonObject());
        WriteDependencies(path, dependencies);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "unsupported group compile or value shape",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_partial_selected_target_asset_metadata()
    {
        var publish = CreatePublishPayload();
        var (path, dependencies) = ReadDependencies(publish);
        var root = SelectedTarget(dependencies)["GhostShell/1.2.3"]!.AsObject();
        root["runtime"]!["GhostShell.dll"] =
            new JsonObject { ["assemblyVersion"] = "1.2.3.0" };
        WriteDependencies(path, dependencies);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "malformed runtime metadata shape",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_a_nupkg_that_does_not_match_the_reviewed_sha512()
    {
        var publish = CreatePublishPayload();
        var packagePath = NuGetPackagePath(
            _evidenceInputs[publish].NuGetPackageRoot,
            "SkiaSharp.NativeAssets.macOS",
            "3.119.3-preview.1.1");
        using (var stream = new FileStream(
                   packagePath,
                   FileMode.Append,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.WriteByte(0);
        }

        var output = OutputPath();
        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains("SHA-512 mismatch", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_rejects_a_nuget_content_hash_metadata_mismatch()
    {
        var publish = CreatePublishPayload();
        var packagePath = NuGetPackagePath(
            _evidenceInputs[publish].NuGetPackageRoot,
            "SkiaSharp.NativeAssets.macOS",
            "3.119.3-preview.1.1");
        var metadataPath = Path.Combine(
            Path.GetDirectoryName(packagePath)!,
            ".nupkg.metadata");
        var metadata = JsonNode.Parse(
            File.ReadAllText(metadataPath))!.AsObject();
        metadata["contentHash"] = Convert.ToBase64String(new byte[64]);
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains(
            "contentHash mismatch",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_rejects_noncanonical_dependency_package_paths()
    {
        var publish = CreatePublishPayload();
        var dependenciesPath = Path.Combine(
            publish,
            "GhostShell.deps.json");
        var dependencies = JsonNode.Parse(
            File.ReadAllText(dependenciesPath))!.AsObject();
        dependencies["libraries"]!
            ["SkiaSharp.NativeAssets.macOS/3.119.3-preview.1.1"]!
            ["path"] = "SkiaSharp.NativeAssets.macOS/3.119.3-preview.1.1";
        File.WriteAllText(dependenciesPath, dependencies.ToJsonString());
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains(
            "package path metadata mismatch",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_rejects_nuspec_metadata_even_after_archive_hashes_are_updated()
    {
        var publish = CreatePublishPayload();
        const string id = "HarfBuzzSharp.NativeAssets.macOS";
        const string version = "8.3.1.3";
        var inputs = _evidenceInputs[publish];
        var packagePath = NuGetPackagePath(
            inputs.NuGetPackageRoot,
            id,
            version);
        RewriteNuspec(
            packagePath,
            id,
            version,
            "Apache-2.0");
        UpdatePackageHashReceipts(
            inputs,
            id,
            version,
            packagePath);
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains(
            "Nuspec identity, version, or license metadata mismatch",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_rejects_a_file_license_without_exact_archived_evidence()
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(
            File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var runtime = catalog["dependencies"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(node => string.Equals(node["identity"]!.GetValue<string>()
, "runtimepack.Microsoft.NETCore.App.Runtime.osx-arm64/10.0.11", StringComparison.Ordinal));
        runtime["nuspecLicenseType"] = "file";
        runtime["nuspecLicense"] = "LICENSE.txt";
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains(
            "must extract its exact nuspec license file",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("wrong-root")]
    [InlineData("mixed-namespace")]
    public void Builder_rejects_nuspec_root_and_namespace_confusion(string mutation)
    {
        var publish = CreatePublishPayload();
        const string id = "HarfBuzzSharp.NativeAssets.macOS";
        const string version = "8.3.1.3";
        var inputs = _evidenceInputs[publish];
        var packagePath = NuGetPackagePath(
            inputs.NuGetPackageRoot,
            id,
            version);
        var rootName = string.Equals(mutation, "wrong-root", StringComparison.Ordinal) ? "not-package" : "package";
        var metadataNamespace = string.Equals(mutation, "mixed-namespace"
, StringComparison.Ordinal) ? " xmlns=\"urn:ghostshell:test:wrong\""
            : string.Empty;
        RewriteNuspecDocument(
            packagePath,
            id,
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <{rootName} xmlns="{NuspecNamespace}">
               <metadata{metadataNamespace}>
                 <id>{id}</id>
                 <version>{version}</version>
                 <license type="expression">MIT</license>
               </metadata>
             </{rootName}>
             """);
        UpdatePackageHashReceipts(
            inputs,
            id,
            version,
            packagePath);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, OutputPath())));

        Assert.Contains(
            "invalid package root or namespace",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_null_catalog_collections_as_invalid_data()
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(
            File.ReadAllText(inputs.CatalogPath))!.AsObject();
        catalog["releaseBlockers"] = null;
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains(
            "must be arrays",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_rejects_generated_evidence_colliding_with_static_notices()
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(
            File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var component = catalog["dependencies"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(node => string.Equals(node["identity"]!.GetValue<string>()
, "SkiaSharp.NativeAssets.macOS/3.119.3-preview.1.1", StringComparison.Ordinal));
        component["notices"]![0]!["outputPath"] = "GHOSTTY-LICENSE";
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains("collides", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_refuses_an_existing_destination_without_mutating_it()
    {
        var publish = CreatePublishPayload();
        var output = OutputPath();
        Directory.CreateDirectory(output);
        var marker = Path.Combine(output, "owned.txt");
        File.WriteAllText(marker, "keep");

        var exception = Assert.Throws<IOException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains("will not be overwritten", exception.Message, StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(marker));
    }

    [Fact]
    public void Exclusive_move_preserves_a_distinct_existing_destination()
    {
        var source = Path.Combine(
            _temporaryDirectory,
            $"exclusive-source-{Guid.NewGuid():N}");
        var destination = Path.Combine(
            _temporaryDirectory,
            $"exclusive-destination-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var sourceMarker = Path.Combine(source, "source.txt");
        File.WriteAllText(sourceMarker, "source");

        Assert.Throws<IOException>(() =>
            ExclusiveDirectoryMover.Move(source, destination));

        Assert.Equal("source", File.ReadAllText(sourceMarker));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Theory]
    [InlineData("libghostty-vt.dylib")]
    [InlineData("GHOSTTY-LICENSE")]
    [InlineData("THIRD-PARTY-NOTICES.md")]
    [InlineData("DOTNET-LICENSE.txt")]
    [InlineData("DOTNET-THIRD-PARTY-NOTICES.txt")]
    [InlineData("GhostShell.deps.json")]
    [InlineData("GhostShell.runtimeconfig.json")]
    public void Builder_fails_closed_when_a_required_file_is_missing(string missingFile)
    {
        var publish = CreatePublishPayload();
        File.Delete(Path.Combine(publish, missingFile));
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains(missingFile, exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_keeps_managed_evidence_out_of_the_native_aot_bundle()
    {
        var managedEvidence = CreatePublishPayload();
        var nativePublish = CreatePublishPayload();
        File.Delete(Path.Combine(nativePublish, "GhostShell.deps.json"));
        File.Delete(Path.Combine(nativePublish, "GhostShell.runtimeconfig.json"));
        foreach (var assembly in Directory.EnumerateFiles(
                     nativePublish,
                     "*.dll",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(assembly);
        }

        var output = OutputPath();
        var request = Request(nativePublish, output) with
        {
            ManagedEvidenceDirectory = managedEvidence,
        };

        _ = new MacOsAppBundleBuilder().Build(request);

        var executableDirectory = Path.Combine(output, "Contents", "MacOS");
        Assert.Empty(Directory.EnumerateFiles(
            executableDirectory,
            "*.dll",
            SearchOption.TopDirectoryOnly));
        Assert.False(File.Exists(Path.Combine(executableDirectory, "GhostShell.deps.json")));
        Assert.False(File.Exists(Path.Combine(
            executableDirectory,
            "GhostShell.runtimeconfig.json")));
    }

    [Fact]
    public void Builder_rejects_managed_host_files_in_a_native_aot_publish()
    {
        var managedEvidence = CreatePublishPayload();
        var nativePublish = CreatePublishPayload();
        File.Delete(Path.Combine(nativePublish, "GhostShell.deps.json"));
        File.Delete(Path.Combine(nativePublish, "GhostShell.runtimeconfig.json"));
        foreach (var assembly in Directory.EnumerateFiles(
                     nativePublish,
                     "*.dll",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(assembly);
        }
        File.WriteAllText(Path.Combine(nativePublish, "Unexpected.dll"), "managed");
        var output = OutputPath();
        var request = Request(nativePublish, output) with
        {
            ManagedEvidenceDirectory = managedEvidence,
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(request));

        Assert.Contains("Unexpected.dll", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_fails_closed_when_shell_integration_resources_are_empty()
    {
        var publish = CreatePublishPayload();
        const string directory = "ghostty";
        Directory.Delete(Path.Combine(publish, directory), recursive: true);
        Directory.CreateDirectory(Path.Combine(publish, directory));
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains("shell-integration", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_rejects_symbolic_links_in_the_publish_payload()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var publish = CreatePublishPayload();
        File.CreateSymbolicLink(
            Path.Combine(publish, "linked-runtime"),
            Path.Combine(publish, "GhostShell.runtimeconfig.json"));
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains("symbolic link", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_rejects_debug_symbols_in_the_publish_payload()
    {
        var publish = CreatePublishPayload();
        var symbols = Path.Combine(publish, "GhostShell.dSYM");
        Directory.CreateDirectory(Path.Combine(
            symbols,
            "Contents",
            "Resources",
            "DWARF"));
        File.WriteAllText(Path.Combine(
            symbols,
            "Contents",
            "Resources",
            "DWARF",
            "GhostShell"), "symbols");
        var output = OutputPath();

        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains("outside", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_resolves_destination_ancestor_links_before_containment_checks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var publish = CreatePublishPayload();
        var alias = Path.Combine(
            _temporaryDirectory,
            $"publish-alias-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(alias, publish);
        var output = Path.Combine(alias, "GhostShell.app");

        var exception = Assert.Throws<ArgumentException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains("cannot contain one another", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(publish, "GhostShell.app")));
    }

    [Fact]
    public async Task Builder_rejects_a_fifo_without_blocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var publish = CreatePublishPayload();
        var fifo = Path.Combine(publish, "blocking-pipe");
        using (var process = System.Diagnostics.Process.Start(
                   new System.Diagnostics.ProcessStartInfo
                   {
                       FileName = "/usr/bin/mkfifo",
                       UseShellExecute = false,
                       ArgumentList = { fifo },
                   }))
        {
            Assert.NotNull(process);
            Assert.True(process.WaitForExit(milliseconds: 2_000));
            Assert.Equal(0, process.ExitCode);
        }

        var output = OutputPath();
        var build = Task.Run(() => new MacOsAppBundleBuilder().Build(
            Request(publish, output)));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await build.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("non-regular file", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_rejects_a_sparse_payload_beyond_the_fingerprint_byte_limit()
    {
        var publish = CreatePublishPayload();
        using (var stream = new FileStream(
                   Path.Combine(publish, "oversized-sparse-file"),
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(MacOsAppBundleBuilder.MaximumPackageBytes);
        }

        var output = OutputPath();
        var exception = Assert.Throws<InvalidDataException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains("exceeds", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_reserves_fingerprint_overhead_for_every_package_boundary()
    {
        const int infoPlistBytes = 1_024;
        MacOsAppBundleBuilder.ValidateSourceBudget(
            MacOsAppBundleBuilder.MaximumSourceFiles,
            MacOsAppBundleBuilder.MaximumSourceEntries,
            MacOsAppBundleBuilder.MaximumSourceDirectoryDepth,
            MacOsAppBundleBuilder.MaximumPackageBytes - infoPlistBytes,
            infoPlistBytes);

        Assert.Throws<InvalidDataException>(() =>
            MacOsAppBundleBuilder.ValidateSourceBudget(
                MacOsAppBundleBuilder.MaximumSourceFiles + 1,
                MacOsAppBundleBuilder.MaximumSourceEntries,
                MacOsAppBundleBuilder.MaximumSourceDirectoryDepth,
                0,
                infoPlistBytes));
        Assert.Throws<InvalidDataException>(() =>
            MacOsAppBundleBuilder.ValidateSourceBudget(
                0,
                MacOsAppBundleBuilder.MaximumSourceEntries + 1,
                MacOsAppBundleBuilder.MaximumSourceDirectoryDepth,
                0,
                infoPlistBytes));
        Assert.Throws<InvalidDataException>(() =>
            MacOsAppBundleBuilder.ValidateSourceBudget(
                0,
                0,
                MacOsAppBundleBuilder.MaximumSourceDirectoryDepth + 1,
                0,
                infoPlistBytes));
        Assert.Throws<InvalidDataException>(() =>
            MacOsAppBundleBuilder.ValidateSourceBudget(
                0,
                0,
                0,
                MacOsAppBundleBuilder.MaximumPackageBytes - infoPlistBytes + 1,
                infoPlistBytes));
    }

    [Theory]
    [InlineData("1.2", "42")]
    [InlineData("1.2.3-beta", "42")]
    [InlineData("1.2.3", "build-42")]
    [InlineData("1.2.3", "")]
    public void Builder_rejects_invalid_bundle_versions(
        string productVersion,
        string buildVersion)
    {
        var publish = CreatePublishPayload();
        var output = OutputPath();

        Assert.Throws<ArgumentException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(
                    publish,
                    output,
                    productVersion,
                    buildVersion)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Builder_rejects_a_destination_inside_the_publish_payload()
    {
        var publish = CreatePublishPayload();
        var output = Path.Combine(publish, "GhostShell.app");

        var exception = Assert.Throws<ArgumentException>(() =>
            new MacOsAppBundleBuilder().Build(
                Request(publish, output)));

        Assert.Contains("cannot contain one another", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Validated_publisher_fingerprints_and_moves_a_private_candidate()
    {
        var outputParent = Path.Combine(
            _temporaryDirectory,
            $"publish-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputParent);
        var candidateParent = Path.Combine(
            outputParent,
            $".ghostshell-package.{Guid.NewGuid():N}");
        Directory.CreateDirectory(candidateParent);
        var candidate = Path.Combine(candidateParent, "GhostShell.app");
        var publish = CreatePublishPayload();
        _ = new MacOsAppBundleBuilder().Build(
            Request(publish, candidate));
        var output = Path.Combine(outputParent, "GhostShell.app");

        var inspection = ValidatedMacOsAppBundlePublisher.Publish(new(
            "rc-42",
            candidate,
            output));

        Assert.True(Directory.Exists(output));
        Assert.False(Directory.Exists(candidate));
        Assert.Equal("app.ghostshell", inspection.Build.ApplicationIdentity);
        Assert.Throws<IOException>(() =>
            MacOsAppBundlePublisher.Publish(output, output));
    }

    [Fact]
    public void Validated_publisher_rejects_an_empty_named_candidate()
    {
        var outputParent = Path.Combine(
            _temporaryDirectory,
            $"empty-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputParent);
        var candidateParent = Path.Combine(
            outputParent,
            $".ghostshell-package.{Guid.NewGuid():N}");
        var candidate = Path.Combine(candidateParent, "GhostShell.app");
        Directory.CreateDirectory(candidate);
        var output = Path.Combine(outputParent, "GhostShell.app");

        Assert.Throws<FileNotFoundException>(() =>
            ValidatedMacOsAppBundlePublisher.Publish(new(
                "rc-42",
                candidate,
                output)));

        Assert.True(Directory.Exists(candidate));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Command_parser_requires_each_option_exactly_once()
    {
        var command = MacOsPackagingCommand.Parse(
        [
            "--publish", "/publish",
            "--managed-evidence", "/managed-evidence",
            "--output", "/output/GhostShell.app",
            "--version", "1.2.3",
            "--build-version", "42",
            "--product-identity-manifest", "/repo/assets/macos/product-identity.json",
            "--product-identity-source-root", "/repo",
            "--asset-catalog", "/tmp/icon/Assets.car",
            "--component-catalog", "/repo/licenses/managed-components.json",
            "--native-component-catalog", "/repo/licenses/native-terminal-components.json",
            "--native-build-receipt", "/repo/native/native-terminal-build-receipt.json",
            "--font-assets-catalog", "/repo/licenses/terminal-font-assets.json",
            "--font-assets-build-receipt", "/repo/native/terminal-font-assets-build-receipt.json",
            "--nuget-packages", "/packages",
            "--cef-runtime-root", "/repo/native/cef/osx-arm64",
            "--cef-runtime-catalog", "/repo/licenses/cef-runtime-components.json",
            "--runtime-identifier", "osx-arm64",
        ]);

        Assert.Equal("/publish", command.PublishDirectory);
        Assert.Equal("/managed-evidence", command.ManagedEvidenceDirectory);
        Assert.Equal(
            "/repo/assets/macos/product-identity.json",
            command.ProductIdentityManifestPath);
        Assert.Equal("/repo", command.ProductIdentitySourceRoot);
        Assert.Equal("/tmp/icon/Assets.car", command.AssetCatalogPath);
        Assert.Equal(
            "/repo/licenses/managed-components.json",
            command.ComponentCatalogPath);
        Assert.Equal(
            "/repo/licenses/native-terminal-components.json",
            command.NativeComponentCatalogPath);
        Assert.Equal(
            "/repo/native/native-terminal-build-receipt.json",
            command.NativeBuildReceiptPath);
        Assert.Equal(
            "/repo/licenses/terminal-font-assets.json",
            command.FontAssetsCatalogPath);
        Assert.Equal(
            "/repo/native/terminal-font-assets-build-receipt.json",
            command.FontAssetsBuildReceiptPath);
        Assert.Equal("/packages", command.NuGetPackageRoot);
        Assert.Equal("/repo/native/cef/osx-arm64", command.CefRuntimeRoot);
        Assert.Equal(
            "/repo/licenses/cef-runtime-components.json",
            command.CefRuntimeCatalogPath);
        Assert.Equal("osx-arm64", command.RuntimeIdentifier);
        Assert.Throws<PackagingUsageException>(() => MacOsPackagingCommand.Parse(
        [
            "--publish", "/publish",
            "--publish", "/other",
            "--version", "1.2.3",
            "--build-version", "42",
            "--component-catalog", "/repo/licenses/managed-components.json",
            "--native-component-catalog", "/repo/licenses/native-terminal-components.json",
            "--native-build-receipt", "/repo/native/native-terminal-build-receipt.json",
            "--nuget-packages", "/packages",
        ]));
    }

    [Fact]
    public void Command_parser_rejects_x64_until_its_full_evidence_closure_exists()
    {
        var exception = Assert.Throws<PackagingUsageException>(() =>
            MacOsPackagingCommand.Parse(
            [
                "--publish", "/publish",
                "--managed-evidence", "/managed-evidence",
                "--output", "/output/GhostShell.app",
                "--version", "1.2.3",
                "--build-version", "42",
                "--product-identity-manifest", "/repo/assets/macos/product-identity.json",
                "--product-identity-source-root", "/repo",
                "--asset-catalog", "/tmp/icon/Assets.car",
                "--component-catalog", "/repo/licenses/managed-components.json",
                "--native-component-catalog", "/repo/licenses/native-terminal-components.json",
                "--native-build-receipt", "/repo/native/native-terminal-build-receipt.json",
                "--font-assets-catalog", "/repo/licenses/terminal-font-assets.json",
                "--font-assets-build-receipt", "/repo/native/terminal-font-assets-build-receipt.json",
                "--nuget-packages", "/packages",
                "--cef-runtime-root", "/repo/native/cef/osx-x64",
                "--cef-runtime-catalog", "/repo/licenses/cef-runtime-components.json",
                "--runtime-identifier", "osx-x64",
            ]));

        Assert.Contains(
            "supports only osx-arm64",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_x64_before_inspecting_the_publish_payload()
    {
        var request = new MacOsAppBundleRequest(
            "/missing/publish",
            "/missing/output/GhostShell.app",
            "1.2.3",
            "42",
            "/missing/product-identity.json",
            "/missing/source-root",
            "/missing/Assets.car",
            "/missing/managed-components.json",
            "/missing/native-components.json",
            "/missing/native-receipt.json",
            "/missing/font-components.json",
            "/missing/font-receipt.json",
            "/missing/packages",
            "/missing/cef",
            "/missing/cef-components.json",
            "osx-x64");

        var exception = Assert.Throws<ArgumentException>(() =>
            new MacOsAppBundleBuilder().Build(request));

        Assert.Contains(
            "supports only osx-arm64",
            exception.Message,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private string OutputPath()
    {
        var parent = Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, "GhostShell.app");
    }

    private static string SpdxPath(string appBundle) => Path.Combine(
        appBundle,
        "Contents",
        "Resources",
        "Licenses",
        "SBOM.spdx.json");

    private static void AssertProjectPackage(
        IReadOnlyList<JsonElement> packages,
        string name,
        string assemblyFile)
    {
        var package = Assert.Single(
            packages,
            candidate => string.Equals(candidate.GetProperty("name").GetString(), name, StringComparison.Ordinal));
        Assert.Equal("1.2.3", package.GetProperty("versionInfo").GetString());
        Assert.Equal(
            "NOASSERTION",
            package.GetProperty("downloadLocation").GetString());
        Assert.Equal(
            "NOASSERTION",
            package.GetProperty("licenseDeclared").GetString());
        Assert.Equal(
            $"SHA-256 was computed from the published project assembly {assemblyFile}.",
            package.GetProperty("sourceInfo").GetString());
        var checksum = Assert.Single(
            package.GetProperty("checksums").EnumerateArray());
        Assert.Equal("SHA256", checksum.GetProperty("algorithm").GetString());
        Assert.Equal(
            64,
            checksum.GetProperty("checksumValue").GetString()!.Length);
    }

    private static (string Path, JsonObject Document) ReadDependencies(
        string publishDirectory)
    {
        var path = Path.Combine(publishDirectory, "GhostShell.deps.json");
        return (
            path,
            JsonNode.Parse(File.ReadAllText(path))!.AsObject());
    }

    private static JsonObject SelectedTarget(JsonObject dependencies) =>
        dependencies["targets"]![RuntimeTargetName]!.AsObject();

    private static void WriteDependencies(string path, JsonObject dependencies) =>
        File.WriteAllText(path, dependencies.ToJsonString());

    private static string NuGetPackagePath(
        string packageRoot,
        string id,
        string version)
    {
        var normalizedId = id.ToLowerInvariant();
        var normalizedVersion = version.ToLowerInvariant();
        return Path.Combine(
            packageRoot,
            normalizedId,
            normalizedVersion,
            $"{normalizedId}.{normalizedVersion}.nupkg");
    }

    private static void RewriteNuspec(
        string packagePath,
        string id,
        string version,
        string licenseExpression)
    {
        RewriteNuspecDocument(
            packagePath,
            id,
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <package xmlns="{NuspecNamespace}">
               <metadata>
                 <id>{id}</id>
                 <version>{version}</version>
                 <license type="expression">{licenseExpression}</license>
               </metadata>
             </package>
             """);
    }

    private static void RewriteNuspecDocument(
        string packagePath,
        string id,
        string nuspec)
    {
        Dictionary<string, byte[]> entries;
        using (var source = new FileStream(
                   packagePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        using (var archive = new ZipArchive(
                   source,
                   ZipArchiveMode.Read,
                   leaveOpen: false))
        {
            entries = archive.Entries.ToDictionary(
                entry => entry.FullName,
                entry =>
                {
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                },
                StringComparer.Ordinal);
        }

        entries[$"{id}.nuspec"] = Encoding.UTF8.GetBytes(nuspec);
        using var target = new FileStream(
            packagePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        using var replacement = new ZipArchive(
            target,
            ZipArchiveMode.Create,
            leaveOpen: false);
        foreach (var entry in entries.OrderBy(
                     entry => entry.Key,
                     StringComparer.Ordinal))
        {
            WriteArchiveEntry(replacement, entry.Key, entry.Value);
        }
    }

    private static void UpdatePackageHashReceipts(
        EvidenceInputs inputs,
        string id,
        string version,
        string packagePath)
    {
        var hash = Convert.ToBase64String(
            SHA512.HashData(File.ReadAllBytes(packagePath)));
        var normalizedId = id.ToLowerInvariant();
        var normalizedVersion = version.ToLowerInvariant();
        File.WriteAllText(
            Path.Combine(
                inputs.NuGetPackageRoot,
                normalizedId,
                normalizedVersion,
                $"{normalizedId}.{normalizedVersion}.nupkg.sha512"),
            hash);
        var catalog = JsonNode.Parse(
            File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var component = catalog["dependencies"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(node => string.Equals(node["identity"]!.GetValue<string>(), $"{id}/{version}", StringComparison.Ordinal));
        component["nupkgSha512"] = hash;
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
    }

    private string CreatePublishPayload()
    {
        var directory = Path.Combine(
            _temporaryDirectory,
            $"publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        WriteFile(directory, "GhostShell", "executable");
        WriteFile(directory, "GhostShell.runtimeconfig.json", "{}");
        WriteFile(directory, "GHOSTTY-LICENSE", new string('L', 2_048));
        WriteFile(directory, "THIRD-PARTY-NOTICES.md", "notices");
        WriteFile(directory, "DOTNET-LICENSE.txt", "dotnet license");
        WriteFile(directory, "DOTNET-THIRD-PARTY-NOTICES.txt", "dotnet notices");
        WriteFile(directory, "sentinel.txt", "publish sentinel");
        foreach (var assemblyName in ProjectAssemblyNames)
        {
            WriteFile(
                directory,
                $"{assemblyName}.dll",
                $"managed assembly {assemblyName}");
        }

        CreateEvidenceInputs(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                Path.Combine(directory, "GhostShell"),
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute);
        }

        return directory;
    }

    private MacOsAppBundleRequest Request(
        string publishDirectory,
        string destinationPath,
        string productVersion = "1.2.3",
        string buildVersion = "42")
    {
        var evidence = _evidenceInputs[publishDirectory];
        return new MacOsAppBundleRequest(
            publishDirectory,
            destinationPath,
            productVersion,
            buildVersion,
            evidence.ProductIdentityManifestPath,
            evidence.ProductIdentitySourceRoot,
            evidence.AssetCatalogPath,
            evidence.CatalogPath,
            evidence.NativeCatalogPath,
            evidence.NativeReceiptPath,
            evidence.FontCatalogPath,
            evidence.FontReceiptPath,
            evidence.NuGetPackageRoot);
    }

    private void CreateEvidenceInputs(string publishDirectory)
    {
        var fixtureDirectory = Path.Combine(
            _temporaryDirectory,
            $"evidence-{Guid.NewGuid():N}");
        var packageRoot = Path.Combine(fixtureDirectory, "packages");
        Directory.CreateDirectory(packageRoot);
        var productIdentity = CreateProductIdentityInputs(fixtureDirectory);
        var harfBuzz = CreateNuGetPackage(
            packageRoot,
            "HarfBuzzSharp.NativeAssets.macOS",
            "8.3.1.3",
            includeNotices: true);
        var skia = CreateNuGetPackage(
            packageRoot,
            "SkiaSharp.NativeAssets.macOS",
            "3.119.3-preview.1.1",
            includeNotices: true);
        var runtime = CreateNuGetPackage(
            packageRoot,
            "Microsoft.NETCore.App.Runtime.osx-arm64",
            "10.0.11",
            includeNotices: false);

        var libraries = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var selectedTarget =
            new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var catalogDependencies = new List<Dictionary<string, object?>>();
        foreach (var assemblyName in ProjectAssemblyNames)
        {
            var identity = $"{assemblyName}/1.2.3";
            libraries.Add(
                identity,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "project",
                    ["serviceable"] = false,
                    ["sha512"] = string.Empty,
                });
            catalogDependencies.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["identity"] = assemblyName.StartsWith(
                    "GhostShell",
                    StringComparison.Ordinal)
                    ? $"{assemblyName}/${{productVersion}}"
                    : identity,
                ["kind"] = "project",
                ["depsType"] = "project",
                ["licenseDeclared"] = "NOASSERTION",
                ["file"] = $"{assemblyName}.dll",
            });
            selectedTarget.Add(
                identity,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["runtime"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [$"{assemblyName}.dll"] =
                            new Dictionary<string, object?>(StringComparer.Ordinal),
                    },
                });
        }

        AddVendoredProjectFixtures(publishDirectory, productIdentity.SourceRoot,
            libraries, selectedTarget, catalogDependencies);

        foreach (var package in new[] { harfBuzz, skia })
        {
            libraries.Add(
                package.Identity,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "package",
                    ["serviceable"] = true,
                    ["sha512"] = $"sha512-{package.ContentHash}",
                    ["path"] = package.PackagePath,
                    ["hashPath"] = package.HashPath,
                });
            catalogDependencies.Add(CatalogPackage(
                package,
                kind: "nuget",
                depsType: "package",
                licenseDeclared: "MIT"));
            selectedTarget.Add(
                package.Identity,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["runtime"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [$"lib/net10.0/{package.Id}.dll"] =
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["assemblyVersion"] = "1.0.0.0",
                                ["fileVersion"] = "1.0.0.0",
                            },
                    },
                });
        }

        const string runtimeIdentity =
            "runtimepack.Microsoft.NETCore.App.Runtime.osx-arm64/10.0.11";
        libraries.Add(
            runtimeIdentity,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "runtimepack",
                ["serviceable"] = false,
                ["sha512"] = string.Empty,
            });
        var runtimeCatalog = CatalogPackage(
            runtime,
            kind: "runtime",
            depsType: "runtimepack",
            licenseDeclared: "NOASSERTION");
        runtimeCatalog["identity"] = runtimeIdentity;
        catalogDependencies.Add(runtimeCatalog);
        selectedTarget.Add(
            runtimeIdentity,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["runtime"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["System.Private.CoreLib.dll"] =
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["assemblyVersion"] = "10.0.0.0",
                            ["fileVersion"] = "10.0.0.0",
                        },
                },
                ["native"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["libcoreclr.dylib"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["fileVersion"] = "0.0.0.0",
                    },
                },
            });
        var rootTarget = (Dictionary<string, object?>)selectedTarget[
            "GhostShell/1.2.3"]!;
        rootTarget["dependencies"] = libraries.Keys
            .Where(identity => !string.Equals(identity, "GhostShell/1.2.3", StringComparison.Ordinal))
            .ToDictionary(
                identity => identity[..identity.LastIndexOf('/')],
                identity => identity[(identity.LastIndexOf('/') + 1)..],
                StringComparer.Ordinal);

        WriteFile(
            publishDirectory,
            "GhostShell.deps.json",
            JsonSerializer.Serialize(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["runtimeTarget"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = RuntimeTargetName,
                        ["signature"] = string.Empty,
                    },
                    ["targets"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [".NETCoreApp,Version=v10.0"] =
                            new Dictionary<string, object?>(StringComparer.Ordinal),
                        [RuntimeTargetName] = selectedTarget,
                    },
                    ["runtimes"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["osx-arm64"] = new[]
                        {
                            "osx",
                            "unix-arm64",
                            "unix",
                            "any",
                            "base",
                        },
                    },
                    ["libraries"] = libraries,
                },
                new JsonSerializerOptions { WriteIndented = true }));

        var ghosttyLicense = File.ReadAllBytes(
            Path.Combine(publishDirectory, "GHOSTTY-LICENSE"));
        var catalogPath = Path.Combine(
            fixtureDirectory,
            "managed-components.json");
        File.WriteAllText(
            catalogPath,
            JsonSerializer.Serialize(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["schemaVersion"] = 2,
                    ["documentName"] =
                        "GhostSHELL ${productVersion} managed-component evidence",
                    ["documentCreatedUtc"] = "2026-07-23T00:00:00Z",
                    ["namespaceBase"] =
                        "https://ghostshell.test/spdx/managed-components/${productVersion}",
                    ["releaseBlockers"] = Array.Empty<string>(),
                    ["dependencies"] = catalogDependencies
                        .OrderBy(
                            component => (string)component["identity"]!,
                            StringComparer.Ordinal)
                        .ToArray(),
                    ["additionalComponents"] = new object[]
                    {
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["identity"] = "libghostty-vt/0.1.0-dev",
                            ["kind"] = "native",
                            ["file"] = "libghostty-vt.dylib",
                            ["licenseDeclared"] = "MIT",
                            ["downloadLocation"] =
                                "https://github.com/ghostty-org/ghostty/tree/test",
                            ["comment"] =
                                "Test libghostty-vt payload with unresolved transitive provenance.",
                            ["licenseEvidenceFile"] = "GHOSTTY-LICENSE",
                            ["licenseEvidenceSha256"] = Convert.ToHexString(
                                    SHA256.HashData(ghosttyLicense))
                                .ToLowerInvariant(),
                            ["licenseEvidenceMinimumBytes"] = 1_024,
                        },
                    },
                },
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var nativeEvidence = NativeTerminalTestProvenance.AddToPublish(
            publishDirectory,
            fixtureDirectory);
        AddLegalClosureInputs(
            productIdentity.SourceRoot,
            publishDirectory,
            catalogPath,
            nativeEvidence);
        _evidenceInputs.Add(
            publishDirectory,
            new EvidenceInputs(
                catalogPath,
                packageRoot,
                nativeEvidence.CatalogPath,
                nativeEvidence.ReceiptPath,
                nativeEvidence.FontCatalogPath,
                nativeEvidence.FontReceiptPath,
                productIdentity.ManifestPath,
                productIdentity.SourceRoot,
                productIdentity.AssetCatalogPath));
    }

    private static Dictionary<string, object?> CatalogPackage(
        NuGetPackageFixture package,
        string kind,
        string depsType,
        string licenseDeclared)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["identity"] = package.Identity,
            ["kind"] = kind,
            ["depsType"] = depsType,
            ["licenseDeclared"] = licenseDeclared,
            ["nuGetId"] = package.Id,
            ["contentHash"] = package.ContentHash,
            ["nupkgSha512"] = package.NupkgSha512,
            ["nuspecLicenseType"] = "expression",
            ["nuspecLicense"] = "MIT",
        };
        if (package.Notices.Count != 0)
        {
            result["notices"] = package.Notices;
        }

        return result;
    }

    private static NuGetPackageFixture CreateNuGetPackage(
        string packageRoot,
        string id,
        string version,
        bool includeNotices)
    {
        var normalizedId = id.ToLowerInvariant();
        var normalizedVersion = version.ToLowerInvariant();
        var packageDirectory = Path.Combine(
            packageRoot,
            normalizedId,
            normalizedVersion);
        Directory.CreateDirectory(packageDirectory);
        var packageStem = $"{normalizedId}.{normalizedVersion}";
        var packagePath = Path.Combine(
            packageDirectory,
            $"{packageStem}.nupkg");
        var noticeContent = Encoding.UTF8.GetBytes(new string('N', 110_000));
        var licenseContent = Encoding.UTF8.GetBytes(new string('L', 2_048));
        using (var stream = new FileStream(
                   packagePath,
                   FileMode.CreateNew,
                   FileAccess.ReadWrite,
                   FileShare.None))
        using (var archive = new ZipArchive(
                   stream,
                   ZipArchiveMode.Create,
                   leaveOpen: false))
        {
            WriteArchiveEntry(
                archive,
                $"{id}.nuspec",
                Encoding.UTF8.GetBytes(
                    $"""
                     <?xml version="1.0" encoding="utf-8"?>
                     <package xmlns="{NuspecNamespace}">
                       <metadata>
                         <id>{id}</id>
                         <version>{version}</version>
                         <license type="expression">MIT</license>
                       </metadata>
                     </package>
                     """));
            if (includeNotices)
            {
                WriteArchiveEntry(archive, "LICENSE.txt", licenseContent);
                WriteArchiveEntry(
                    archive,
                    "THIRD-PARTY-NOTICES.txt",
                    noticeContent);
            }
        }

        var nupkgHash = Convert.ToBase64String(
            SHA512.HashData(File.ReadAllBytes(packagePath)));
        var contentHash = Convert.ToBase64String(
            SHA512.HashData(
                Encoding.UTF8.GetBytes($"content:{id}:{version}")));
        File.WriteAllText(
            Path.Combine(packageDirectory, $"{packageStem}.nupkg.sha512"),
            nupkgHash);
        File.WriteAllText(
            Path.Combine(packageDirectory, ".nupkg.metadata"),
            JsonSerializer.Serialize(new
            {
                version = 2,
                contentHash,
                source = "https://api.nuget.org/v3/index.json",
            }));

        IReadOnlyList<Dictionary<string, object?>> notices = includeNotices
            ?
            [
                new Dictionary<string, object?>(global::System.StringComparer.Ordinal)
                {
                    ["archivePath"] = "LICENSE.txt",
                    ["outputPath"] = $"{id}-{version}/LICENSE.txt",
                    ["sha256"] = Convert.ToHexString(
                            SHA256.HashData(licenseContent))
                        .ToLowerInvariant(),
                    ["minimumBytes"] = 1_024,
                },
                new Dictionary<string, object?>(global::System.StringComparer.Ordinal)
                {
                    ["archivePath"] = "THIRD-PARTY-NOTICES.txt",
                    ["outputPath"] =
                        $"{id}-{version}/THIRD-PARTY-NOTICES.txt",
                    ["sha256"] = Convert.ToHexString(
                            SHA256.HashData(noticeContent))
                        .ToLowerInvariant(),
                    ["minimumBytes"] = 100_000,
                },
            ]
            : [];
        return new NuGetPackageFixture(
            id,
            version,
            $"{id}/{version}",
            contentHash,
            nupkgHash,
            $"{normalizedId}/{normalizedVersion}",
            $"{packageStem}.nupkg.sha512",
            notices);
    }

    private static void WriteArchiveEntry(
        ZipArchive archive,
        string path,
        byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(
            2020,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static ProductIdentityInputs CreateProductIdentityInputs(string fixtureDirectory)
    {
        var sourceRoot = Path.Combine(fixtureDirectory, "identity-source");
        var documentPath = Path.Combine(
            sourceRoot,
            "assets",
            "macos",
            "GhostShell.icon",
            "icon.json");
        var artworkPath = Path.Combine(
            sourceRoot,
            "assets",
            "macos",
            "GhostShell.icon",
            "Assets",
            "logo.svg");
        var fallbackPath = Path.Combine(
            sourceRoot,
            "assets",
            "macos",
            "GhostShell.icns");
        Directory.CreateDirectory(Path.GetDirectoryName(documentPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(artworkPath)!);
        File.WriteAllText(documentPath, "{\"test\":true}");
        File.WriteAllText(artworkPath, "<svg>test</svg>");
        Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
        File.WriteAllBytes(fallbackPath, CreateCompleteIcns());

        var manifestPath = Path.Combine(
            sourceRoot,
            "assets",
            "macos",
            "product-identity.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                format = "ghostshell-macos-product-identity-v1",
                platform = "macos",
                displayName = "GhostSHELL",
                bundleName = "GhostSHELL",
                executableName = "GhostShell",
                bundleIdentifier = "app.ghostshell",
                iconName = "GhostShell",
                artwork = new
                {
                    source = "Original first-party GhostSHELL artwork",
                    owner = "Terion Labs",
                    license = "MIT",
                    copyright = "Copyright (c) 2026 Terion Labs",
                },
                approval = new
                {
                    status = "approved",
                    approvedBy = "terion-labs/ghostshell maintainer",
                    approvedAt = "2026-08-25",
                    evidence = "https://github.com/terion-labs/ghostshell/issues/42",
                },
                requiredAppearances = new[]
                {
                    "Default",
                    "Dark",
                    "TintedLight",
                    "TintedDark",
                    "ClearLight",
                    "ClearDark",
                },
                files = new[]
                {
                    IdentityFile(
                        "icon-composer-document",
                        "assets/macos/GhostShell.icon/icon.json",
                        documentPath),
                    IdentityFile(
                        "source-artwork",
                        "assets/macos/GhostShell.icon/Assets/logo.svg",
                        artworkPath),
                    IdentityFile(
                        "icns-fallback",
                        "assets/macos/GhostShell.icns",
                        fallbackPath),
                },
            }, new JsonSerializerOptions { WriteIndented = true }));
        var assetCatalogPath = Path.Combine(fixtureDirectory, "Assets.car");
        File.WriteAllBytes(
            assetCatalogPath,
            [.. "BOMStore"u8.ToArray(), 0, 0, 0, 1, 0, 0, 0, 0]);
        return new ProductIdentityInputs(
            manifestPath,
            sourceRoot,
            assetCatalogPath);

        static object IdentityFile(string role, string path, string sourcePath) => new
        {
            role,
            path,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourcePath))),
        };
    }

    private static void AddLegalClosureInputs(
        string sourceRoot,
        string publishDirectory,
        string managedCatalogPath,
        NativeTerminalTestEvidence nativeEvidence)
    {
        var sourceFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["LICENSE"] = "fixture MIT license"u8.ToArray(),
            ["licenses/GPL-3.0.txt"] = "fixture GPL license"u8.ToArray(),
            ["licenses/SMBLIBRARY-LGPL-3.0.txt"] =
                "fixture LGPL license"u8.ToArray(),
            ["licenses/SMBLIBRARY-SOURCE-AND-RELINKING.md"] =
                "fixture relinking instructions"u8.ToArray(),
            ["licenses/SMBLIBRARY-SOURCE.json"] =
                "{\"fixture\":\"SMB source\"}"u8.ToArray(),
            ["licenses/THIRD-PARTY-NOTICES.md"] =
                File.ReadAllBytes(Path.Combine(
                    publishDirectory,
                    "THIRD-PARTY-NOTICES.md")),
            ["licenses/cef-runtime-components.json"] =
                "{\"fixture\":\"CEF catalog\"}"u8.ToArray(),
            ["licenses/managed-components.json"] =
                File.ReadAllBytes(managedCatalogPath),
            ["licenses/native-terminal-components.json"] =
                File.ReadAllBytes(nativeEvidence.CatalogPath),
            ["licenses/terminal-font-assets.json"] =
                File.ReadAllBytes(nativeEvidence.FontCatalogPath),
            ["native/ghostty-vt/SHELL-INTEGRATION-NOTICE.md"] =
                "fixture shell notice"u8.ToArray(),
            ["native/sql-language-worker/src/legal/legal-review.tsv"] =
                "fixture\treview"u8.ToArray(),
            ["native/sql-language-worker/src/legal/runtime-license-map.tsv"] =
                "fixture\tmap"u8.ToArray(),
        };
        foreach (var (relativePath, content) in sourceFiles)
        {
            var path = Path.Combine(
                sourceRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        var productIdentityPath = Path.Combine(
            sourceRoot,
            "assets",
            "macos",
            "product-identity.json");
        sourceFiles.Add(
            "assets/macos/product-identity.json",
            File.ReadAllBytes(productIdentityPath));
        var evidence = sourceFiles
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new
            {
                path = item.Key,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(item.Value)),
            })
            .ToArray();
        var record = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            format = "ghostshell-macos-release-legal-closure-v1",
            platform = "macos-arm64",
            legalClearance = false,
            releaseBlockers = new[] { "Fixture owner decision remains open." },
            excludedPlatforms = new[] { "windows", "linux" },
            review = new
            {
                status = "pending-project-owner-decision",
                basis = (string?)null,
                reviewedBy = (string?)null,
                reviewedAtUtc = (string?)null,
            },
            dispositions = new
            {
                managedComponents = PendingDisposition("managed fixture"),
                nativeTerminalAndShell = PendingDisposition("terminal fixture"),
                cefMacos = PendingDisposition("CEF fixture"),
                sqlLanguageWorker = PendingDisposition("SQL fixture"),
            },
            evidence,
        });
        var recordPath = Path.Combine(
            sourceRoot,
            "licenses",
            "macos-release-legal.json");
        File.WriteAllBytes(recordPath, record);

        var publishedFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["GHOSTSHELL-LICENSE.txt"] = sourceFiles["LICENSE"],
            ["MACOS-RELEASE-LEGAL.json"] = record,
            ["GPL-3.0.txt"] = sourceFiles["licenses/GPL-3.0.txt"],
            ["SMBLIBRARY-LGPL-3.0.txt"] =
                sourceFiles["licenses/SMBLIBRARY-LGPL-3.0.txt"],
            ["SMBLIBRARY-SOURCE-AND-RELINKING.md"] =
                sourceFiles["licenses/SMBLIBRARY-SOURCE-AND-RELINKING.md"],
            ["SMBLIBRARY-SOURCE.json"] =
                sourceFiles["licenses/SMBLIBRARY-SOURCE.json"],
        };
        foreach (var (fileName, content) in publishedFiles)
        {
            File.WriteAllBytes(Path.Combine(publishDirectory, fileName), content);
        }

        static object PendingDisposition(string comment) => new
        {
            status = "pending-project-owner-decision",
            scope = "macos-arm64",
            comment,
        };
    }

    private static byte[] CreateCompleteIcns()
    {
        string[] types =
        [
            "ic04", "ic05", "ic07", "ic08", "ic09",
            "ic10", "ic11", "ic12", "ic13", "ic14",
        ];
        return CreateIcns(types);
    }

    private static byte[] CreateIcns(IReadOnlyList<string> types)
    {
        var result = new byte[8 + (types.Count * 8)];
        Encoding.ASCII.GetBytes("icns").CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), (uint)result.Length);
        for (var index = 0; index < types.Count; index++)
        {
            var offset = 8 + (index * 8);
            Encoding.ASCII.GetBytes(types[index]).CopyTo(result, offset);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset + 4, 4), 8);
        }

        return result;
    }

    private static void UpdateIdentityFileHash(
        string manifestPath,
        string role,
        string sourcePath)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var file = manifest["files"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(node => string.Equals(
                node["role"]!.GetValue<string>(),
                role,
                StringComparison.Ordinal));
        file["sha256"] = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(sourcePath)));
        File.WriteAllText(manifestPath, manifest.ToJsonString());
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed record EvidenceInputs(
        string CatalogPath,
        string NuGetPackageRoot,
        string NativeCatalogPath,
        string NativeReceiptPath,
        string FontCatalogPath,
        string FontReceiptPath,
        string ProductIdentityManifestPath,
        string ProductIdentitySourceRoot,
        string AssetCatalogPath);

    private sealed record ProductIdentityInputs(
        string ManifestPath,
        string SourceRoot,
        string AssetCatalogPath);

    private sealed record NuGetPackageFixture(
        string Id,
        string Version,
        string Identity,
        string ContentHash,
        string NupkgSha512,
        string PackagePath,
        string HashPath,
        IReadOnlyList<Dictionary<string, object?>> Notices);
}
