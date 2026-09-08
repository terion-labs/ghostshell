using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShell.Packaging;

namespace GhostShell.AccessibilityAcceptance;

public sealed partial class MacOsAppBundleBuilderTests
{
    [Theory]
    [InlineData("SSH.NET", "2026.0.0")]
    [InlineData("SharpCompress", "0.50.3")]
    [InlineData("Microsoft.Data.SqlClient", "6.0.2")]
    public void Unmodified_dependencies_use_nuget_package_provenance(string name, string version)
    {
        var publish = CreatePublishPayload();
        var evidence = BuildPackageEvidence(publish);
        using var spdx = JsonDocument.Parse(Assert.Single(evidence.Files, file => file.RelativePath == "SBOM.spdx.json").Content);
        var package = Assert.Single(spdx.RootElement.GetProperty("packages").EnumerateArray(),
            item => string.Equals(item.GetProperty("name").GetString(), name, StringComparison.Ordinal));
        Assert.Equal(version, package.GetProperty("versionInfo").GetString());
        Assert.Equal("MIT", package.GetProperty("licenseDeclared").GetString());
        Assert.Contains("pkg:nuget/", package.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Patched source build", package.ToString(), StringComparison.Ordinal);
        var checksum = Assert.Single(package.GetProperty("checksums").EnumerateArray());
        Assert.Equal("SHA512", checksum.GetProperty("algorithm").GetString());
        var archive = NuGetPackagePath(_evidenceInputs[publish].NuGetPackageRoot, name, version);
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(File.ReadAllBytes(archive))),
            checksum.GetProperty("checksumValue").GetString());
        Assert.DoesNotContain(evidence.Files, file => file.RelativePath.StartsWith("Sources/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SSH.NET", "2026.0.0")]
    [InlineData("SharpCompress", "0.50.3")]
    [InlineData("Microsoft.Data.SqlClient", "6.0.2")]
    public void Unmodified_dependencies_reject_changed_package_bytes(string name, string version)
    {
        var publish = CreatePublishPayload();
        var archive = NuGetPackagePath(_evidenceInputs[publish].NuGetPackageRoot, name, version);
        using (var stream = File.Open(archive, FileMode.Append, FileAccess.Write, FileShare.None)) { stream.WriteByte(0); }
        var error = Assert.Throws<InvalidDataException>(() => BuildPackageEvidence(publish));
        Assert.Contains("SHA-512 mismatch", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SSH.NET/2026.0.0", "Renci.SshNet.dll")]
    [InlineData("SharpCompress/0.50.3", "SharpCompress.dll")]
    [InlineData("Microsoft.Data.SqlClient/6.0.2", "Microsoft.Data.SqlClient.dll")]
    public void Removed_source_projects_cannot_replace_stock_package_catalog_entries(string identity, string file)
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var entry = catalog["dependencies"]!.AsArray().Select(node => node!.AsObject())
            .Single(node => string.Equals(node["identity"]!.GetValue<string>(), identity, StringComparison.Ordinal));
        entry.Clear();
        entry["identity"] = identity; entry["kind"] = "project"; entry["depsType"] = "project";
        entry["licenseDeclared"] = "NOASSERTION"; entry["file"] = file;
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
        Assert.Throws<InvalidDataException>(() => BuildPackageEvidence(publish));
    }

    [Fact]
    public void Obsolete_vendor_source_provenance_is_not_accepted_by_package_catalog()
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var entry = catalog["dependencies"]!.AsArray().Select(node => node!.AsObject())
            .Single(node => node["identity"]!.GetValue<string>() == "Microsoft.Data.SqlClient/6.0.2");
        entry["vendorSource"] = new JsonObject { ["upstreamIdentity"] = "Microsoft.Data.SqlClient/6.0.2" };
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
        Assert.Throws<InvalidDataException>(() => BuildPackageEvidence(publish));
    }

    [Fact]
    public void Ordinary_project_cannot_claim_a_package_license_override()
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var ordinary = catalog["dependencies"]!.AsArray().Select(node => node!.AsObject())
            .First(node => node["identity"]!.GetValue<string>().StartsWith("GhostShell/", StringComparison.Ordinal));
        ordinary["licenseDeclared"] = "MIT";
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
        Assert.Throws<InvalidDataException>(() => BuildPackageEvidence(publish));
    }

    private ManagedComponentEvidence BuildPackageEvidence(string publish)
    {
        var inputs = _evidenceInputs[publish];
        return ManagedComponentEvidenceBuilder.Build(publish, publish, inputs.CatalogPath,
            inputs.NuGetPackageRoot, "1.2.3", new(1024, 4096, 64 * 1024 * 1024, 61));
    }
}
