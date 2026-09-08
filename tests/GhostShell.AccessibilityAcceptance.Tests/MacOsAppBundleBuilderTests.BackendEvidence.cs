using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShell.Packaging;
using PackagingProgram = GhostShell.Packaging.Program;

namespace GhostShell.AccessibilityAcceptance;

public sealed partial class MacOsAppBundleBuilderTests
{
    [Theory]
    [InlineData("publish")]
    [InlineData("legal")]
    [InlineData("managed")]
    public void BackendEvidenceCommandRejectsLinkedOutputAncestors(string component)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var fixture = CreateBackendEvidenceFixture();
        var outside = Path.Combine(_temporaryDirectory, "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var publish = fixture.Publish;
        if (component == "publish")
        {
            publish = Path.Combine(outside, "publish-link");
            Directory.CreateSymbolicLink(publish, fixture.Publish);
        }
        else
        {
            var legal = Path.Combine(publish, "legal");
            if (component == "managed")
            {
                Directory.CreateDirectory(legal);
                Directory.CreateSymbolicLink(Path.Combine(legal, "managed"), outside);
            }
            else
            {
                Directory.CreateSymbolicLink(legal, outside);
            }
        }
        Assert.Equal(1, PackagingProgram.Main(["workspace-backend-evidence", publish, fixture.Publish, fixture.Catalog, fixture.Packages, "1.2.3"]));
        Assert.Empty(Directory.GetFiles(outside, "*", SearchOption.TopDirectoryOnly));
        Assert.False(File.Exists(Path.Combine(outside, "managed", "SBOM.spdx.json")));
    }

    [Fact]
    public void BackendEvidenceCommandWritesVerifiedEvidenceOnlyOnce()
    {
        var fixture = CreateBackendEvidenceFixture();
        string[] arguments = ["workspace-backend-evidence", fixture.Publish, fixture.Publish, fixture.Catalog, fixture.Packages, "1.2.3"];
        Assert.Equal(0, PackagingProgram.Main(arguments));
        Assert.True(File.Exists(Path.Combine(fixture.Publish, "legal", "managed", "SBOM.spdx.json")));
        Assert.Equal(1, PackagingProgram.Main(arguments));
    }

    [Fact]
    public void BackendEvidenceUsesItsOwnExactLinuxClosureAndSpdxRoot()
    {
        var fixture = CreateBackendEvidenceFixture();
        var evidence = BuildBackendEvidence(fixture);
        using var spdx = JsonDocument.Parse(Assert.Single(evidence.Files, file => file.RelativePath == "SBOM.spdx.json").Content);
        Assert.Contains(spdx.RootElement.GetProperty("packages").EnumerateArray(), package => package.GetProperty("name").GetString() == "GhostShell.Backend");
        Assert.DoesNotContain(spdx.RootElement.GetProperty("packages").EnumerateArray(), package => package.GetProperty("name").GetString() == "GhostShell");
    }

    [Theory]
    [InlineData("rid")]
    [InlineData("fallback")]
    [InlineData("root")]
    [InlineData("catalog")]
    [InlineData("hash")]
    public void BackendEvidenceRejectsManifestOrCatalogOutsideReviewedProfile(string mutation)
    {
        var fixture = CreateBackendEvidenceFixture();
        var manifestPath = Path.Combine(fixture.Publish, "GhostShell.Backend.deps.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var catalog = JsonNode.Parse(File.ReadAllText(fixture.Catalog))!;
        switch (mutation)
        {
            case "rid": manifest["runtimeTarget"]!["name"] = ".NETCoreApp,Version=v10.0/osx-arm64"; break;
            case "fallback": manifest["runtimes"]!["linux-musl-arm64"] = new JsonArray("linux", "any"); break;
            case "root": manifest["targets"]![".NETCoreApp,Version=v10.0/linux-arm64"]!["GhostShell.Backend/1.2.3"]!["dependencies"] = new JsonObject(); break;
            case "catalog": catalog["dependencies"]!.AsArray().RemoveAt(0); break;
            case "hash": catalog["dependencies"]!.AsArray().Last()!["nupkgSha512"] = Convert.ToBase64String(new byte[64]); break;
            default: throw new InvalidOperationException("Unknown fixture mutation.");
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        File.WriteAllText(fixture.Catalog, catalog.ToJsonString());
        Assert.Throws<InvalidDataException>(() => BuildBackendEvidence(fixture));
    }

    private (string Publish, string Catalog, string Packages) CreateBackendEvidenceFixture()
    {
        var publish = Path.Combine(_temporaryDirectory, "backend-" + Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(publish, "package-fixtures");
        Directory.CreateDirectory(packages);
        string[] projects = ["GhostShell.Backend", "GhostShell.ConnectionBackend", "GhostShell.Application", "GhostShell.Core",
            "GhostShell.Databases", "GhostShell.Files", "GhostShell.Infrastructure", "GhostShell.Redis"];
        var libraries = new JsonObject();
        var target = new JsonObject();
        var entries = new JsonArray();
        foreach (var project in projects)
        {
            File.WriteAllText(Path.Combine(publish, project + ".dll"), "fixture assembly " + project);
            var identity = project + "/1.2.3";
            libraries[identity] = new JsonObject { ["type"] = "project", ["serviceable"] = false, ["sha512"] = "" };
            target[identity] = new JsonObject { ["runtime"] = new JsonObject { [project + ".dll"] = new JsonObject() } };
            entries.Add(new JsonObject { ["identity"] = project + "/${productVersion}", ["kind"] = "project", ["depsType"] = "project", ["licenseDeclared"] = "NOASSERTION", ["file"] = project + ".dll" });
        }
        var runtime = CreateNuGetPackage(packages, "Microsoft.NETCore.App.Runtime.linux-arm64", "10.0.11", includeNotices: false);
        const string runtimeIdentity = "runtimepack.Microsoft.NETCore.App.Runtime.linux-arm64/10.0.11";
        libraries[runtimeIdentity] = new JsonObject { ["type"] = "runtimepack", ["serviceable"] = false, ["sha512"] = "" };
        target[runtimeIdentity] = new JsonObject();
        var runtimeEntry = CatalogPackage(runtime, "runtime", "runtimepack", "NOASSERTION");
        runtimeEntry["identity"] = runtimeIdentity;
        entries.Add(JsonSerializer.SerializeToNode(runtimeEntry));
        var dependencies = new JsonObject();
        foreach (var identity in libraries.Select(pair => pair.Key).Where(identity => identity != "GhostShell.Backend/1.2.3"))
        {
            var parts = identity.Split('/');
            dependencies[parts[0]] = parts[1];
        }
        target["GhostShell.Backend/1.2.3"]!["dependencies"] = dependencies;
        var manifest = new JsonObject
        {
            ["runtimeTarget"] = new JsonObject { ["name"] = ".NETCoreApp,Version=v10.0/linux-arm64", ["signature"] = "" },
            ["targets"] = new JsonObject { [".NETCoreApp,Version=v10.0"] = new JsonObject(), [".NETCoreApp,Version=v10.0/linux-arm64"] = target },
            ["libraries"] = libraries,
            ["runtimes"] = new JsonObject
            {
                ["linux-arm64"] = new JsonArray("linux", "unix-arm64", "unix", "any", "base"),
                ["android-arm64"] = new JsonArray("android", "linux-bionic-arm64", "linux-bionic", "linux-arm64", "linux", "unix-arm64", "unix", "any", "base"),
                ["linux-bionic-arm64"] = new JsonArray("linux-bionic", "linux-arm64", "linux", "unix-arm64", "unix", "any", "base"),
                ["linux-musl-arm64"] = new JsonArray("linux-musl", "linux-arm64", "linux", "unix-arm64", "unix", "any", "base"),
            },
        };
        File.WriteAllText(Path.Combine(publish, "GhostShell.Backend.deps.json"), manifest.ToJsonString());
        var catalog = Path.Combine(publish, "catalog.json");
        File.WriteAllText(catalog, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["documentName"] = "GhostSHELL ${productVersion} managed-component evidence",
            ["documentCreatedUtc"] = "2026-08-25T00:00:00Z",
            ["namespaceBase"] = "https://ghostshell.app/spdx/managed-components/${productVersion}",
            ["releaseBlockers"] = new JsonArray(),
            ["dependencies"] = entries,
            ["additionalComponents"] = new JsonArray(),
        }.ToJsonString());
        return (publish, catalog, packages);
    }

    private static ManagedComponentEvidence BuildBackendEvidence((string Publish, string Catalog, string Packages) fixture) =>
        ManagedComponentEvidenceBuilder.Build(fixture.Publish, fixture.Publish, fixture.Catalog, fixture.Packages, "1.2.3",
            new(1024, 4096, 64 * 1024 * 1024, 61), ManagedEvidenceProfile.LinuxBackend);
}
