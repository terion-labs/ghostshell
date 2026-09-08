using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GhostShell.Packaging;

namespace GhostShell.AccessibilityAcceptance;

public sealed partial class MacOsAppBundleBuilderTests
{
    private static readonly VendorFixture[] VendorFixtures =
    [
        new("Microsoft.Data.SqlClient.Routed/6.0.2", "Microsoft.Data.SqlClient/6.0.2", "sqlclient", "Microsoft.Data.SqlClient", new(6, 0, 0, 0),
            "https://github.com/dotnet/SqlClient/archive/b16dec0a5622fd5b3d5311191bac4cafadc43e60.tar.gz",
            "f4c2cfd1a7a48f5e4f0b6b97639e5c17730fca1eade31668732280b82df21870", "routed-transport.patch", "upstream/LICENSE"),
        new("SSH.NET/2026.0.0", "SSH.NET/2026.0.0", "sshnet", "Renci.SshNet", new(2026, 0, 0, 1),
            "https://github.com/sshnet/SSH.NET/archive/7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e.tar.gz",
            "e3c305e2bf41d00f7aba51b9cdd151567dfe32c616ad322561ac59e3d6a4593b", "rfc1929-authentication.patch", "upstream/LICENSE"),
        new("SharpCompress/0.50.3", "SharpCompress/0.50.3", "sharpcompress", "SharpCompress", new(0, 50, 3, 1),
            "https://codeload.github.com/adamhathcock/sharpcompress/tar.gz/67bd9289f99dc77e1b65730a08e8213405921488",
            "9a3a4d57b279243ce24332fbb342c152b5c286172fa57881d9536e83c777afeb", "uncached-zip-enumeration.patch", "upstream/LICENSE.txt"),
    ];

    private static void AddVendoredProjectFixtures(string publish, string sourceRoot,
        IDictionary<string, object?> libraries, IDictionary<string, object?> target,
        ICollection<Dictionary<string, object?>> catalog)
    {
        foreach (var vendor in VendorFixtures)
        {
            WriteVendorAssembly(Path.Combine(publish, vendor.Assembly + ".dll"), vendor.Assembly, vendor.Version);
            var root = Path.Combine(sourceRoot, "vendor", vendor.Directory);
            WriteFile(root, vendor.License, "MIT license fixture\n" + new string('L', 1024));
            WriteFile(root, vendor.Patch, "reviewed runtime patch fixture\n");
            WriteFile(root, "upstream/source.cs", "reviewed patched source fixture\n");
            WriteFile(root, "UPSTREAM-SOURCE.sha256", new string('a', 64) + "  upstream/source.cs\n");
            var inventory = string.Join('\n', new[] { vendor.License, vendor.Patch, "upstream/source.cs", "UPSTREAM-SOURCE.sha256" }
                .Order(StringComparer.Ordinal).Select(path => HashFile(Path.Combine(root, path)) + "  " + path)) + "\n";
            WriteFile(root, "SOURCE-SNAPSHOT.sha256", inventory);
            libraries.Add(vendor.Identity, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "project",
                ["serviceable"] = false,
                ["sha512"] = string.Empty,
            });
            target.Add(vendor.Identity, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["runtime"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [vendor.Assembly + ".dll"] = new Dictionary<string, object?>(StringComparer.Ordinal),
                },
            });
            catalog.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["identity"] = vendor.Identity,
                ["kind"] = "project",
                ["depsType"] = "project",
                ["licenseDeclared"] = "MIT",
                ["file"] = vendor.Assembly + ".dll",
                ["vendorSource"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["upstreamIdentity"] = vendor.UpstreamIdentity,
                    ["archiveUrl"] = vendor.Url,
                    ["archiveSha256"] = vendor.ArchiveHash,
                    ["upstreamManifestSha256"] = HashFile(Path.Combine(root, "UPSTREAM-SOURCE.sha256")),
                    ["sourceManifestSha256"] = HashFile(Path.Combine(root, "SOURCE-SNAPSHOT.sha256")),
                    ["patchSha256"] = HashFile(Path.Combine(root, vendor.Patch)),
                    ["licenseSha256"] = HashFile(Path.Combine(root, vendor.License)),
                },
            });
        }
    }

    private static void WriteVendorAssembly(string path, string name, Version version, bool reference = false)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name) { Version = version }, typeof(object).Assembly);
        if (reference)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(ReferenceAssemblyAttribute).GetConstructor(Type.EmptyTypes)!, []));
        }
        var module = assembly.DefineDynamicModule(name);
        module.DefineType("SyntheticVendorType", TypeAttributes.Public).CreateType();
        assembly.Save(path);
    }

    private static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Vendored_projects_ship_reviewed_notices_source_hashes_and_actual_assembly_hashes()
    {
        var publish = CreatePublishPayload();
        var evidence = BuildVendorEvidence(publish);
        using var spdx = JsonDocument.Parse(Assert.Single(evidence.Files, file => file.RelativePath == "SBOM.spdx.json").Content);
        foreach (var vendor in VendorFixtures)
        {
            Assert.Contains(evidence.Files, file => file.RelativePath == vendor.Directory + "-MIT.txt");
            Assert.Contains(evidence.Files, file => file.RelativePath == "Sources/" + vendor.Directory + "/SOURCE-SNAPSHOT.sha256");
            var package = Assert.Single(spdx.RootElement.GetProperty("packages").EnumerateArray(),
                package => package.GetProperty("name").GetString() == vendor.UpstreamIdentity[..vendor.UpstreamIdentity.IndexOf('/', StringComparison.Ordinal)]);
            Assert.Equal("MIT", package.GetProperty("licenseDeclared").GetString());
            Assert.Equal(vendor.Url, package.GetProperty("downloadLocation").GetString());
            Assert.Equal(HashFile(Path.Combine(publish, vendor.Assembly + ".dll")),
                Assert.Single(package.GetProperty("checksums").EnumerateArray()).GetProperty("checksumValue").GetString());
            Assert.DoesNotContain("pkg:nuget/", package.ToString(), StringComparison.Ordinal);
            Assert.Contains(vendor.UpstreamIdentity, package.GetProperty("sourceInfo").GetString(), StringComparison.Ordinal);
            Assert.Contains(vendor.Identity, package.GetProperty("sourceInfo").GetString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("license")]
    [InlineData("patch")]
    [InlineData("manifest")]
    [InlineData("upstream-manifest")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("reference")]
    [InlineData("name")]
    public void Vendored_projects_reject_changed_inputs_or_nonruntime_assemblies(string change)
    {
        var publish = CreatePublishPayload();
        var root = Path.Combine(_evidenceInputs[publish].ProductIdentitySourceRoot, "vendor", "sqlclient");
        switch (change)
        {
            case "source": File.AppendAllText(Path.Combine(root, "upstream/source.cs"), "changed"); break;
            case "license": File.AppendAllText(Path.Combine(root, "upstream/LICENSE"), "changed"); break;
            case "patch": File.AppendAllText(Path.Combine(root, "routed-transport.patch"), "changed"); break;
            case "manifest": File.AppendAllText(Path.Combine(root, "SOURCE-SNAPSHOT.sha256"), "changed"); break;
            case "upstream-manifest": File.AppendAllText(Path.Combine(root, "UPSTREAM-SOURCE.sha256"), "changed"); break;
            case "missing": File.Delete(Path.Combine(root, "upstream/source.cs")); break;
            case "version": WriteVendorAssembly(Path.Combine(publish, "Microsoft.Data.SqlClient.dll"), "Microsoft.Data.SqlClient", new(0, 0, 1, 0)); break;
            case "reference": WriteVendorAssembly(Path.Combine(publish, "Microsoft.Data.SqlClient.dll"), "Microsoft.Data.SqlClient", new(6, 0, 0, 0), reference: true); break;
            case "name": WriteVendorAssembly(Path.Combine(publish, "Microsoft.Data.SqlClient.dll"), "SqlClient.Reference", new(6, 0, 0, 0)); break;
        }
        if (string.Equals(change, "missing", StringComparison.Ordinal))
        {
            Assert.Throws<FileNotFoundException>(() => BuildVendorEvidence(publish));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => BuildVendorEvidence(publish));
        }
    }

    private ManagedComponentEvidence BuildVendorEvidence(string publish)
    {
        var inputs = _evidenceInputs[publish];
        return ManagedComponentEvidenceBuilder.Build(publish, publish, inputs.CatalogPath,
            inputs.NuGetPackageRoot, "1.2.3", new(1024, 4096, 64 * 1024 * 1024, 61), inputs.ProductIdentitySourceRoot);
    }

    [Theory]
    [InlineData("sqlclient")]
    [InlineData("sshnet")]
    [InlineData("sharpcompress")]
    public void Vendored_inventory_rejects_unlisted_build_inputs(string vendor)
    {
        var publish = CreatePublishPayload();
        var root = Path.Combine(_evidenceInputs[publish].ProductIdentitySourceRoot, "vendor", vendor);
        WriteFile(root, "upstream/extra/source.cs", "unreviewed compile glob input");
        var error = Assert.Throws<InvalidDataException>(() => BuildVendorEvidence(publish));
        Assert.Contains("omits current input", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Vendored_inventory_ignores_only_documented_generated_inputs()
    {
        var publish = CreatePublishPayload();
        var root = Path.Combine(_evidenceInputs[publish].ProductIdentitySourceRoot, "vendor", "sqlclient");
        WriteFile(root, "bin/generated.cs", "generated output");
        WriteFile(root, "obj/generated.cs", "generated output");
        WriteFile(root, "tests/bin/generated.cs", "generated output");
        WriteFile(root, "upstream/src/Microsoft.Data.SqlClient/netcore/src/obj/generated.cs", "generated output");
        WriteFile(root, "tests/packages.lock.json", "RID-dependent lock fixture");
        WriteFile(root, "tests/packages.win-x64.lock.json", "RID-dependent lock fixture");
        WriteFile(root, "upstream/src/Microsoft.Data.SqlClient/netcore/src/packages.linux-x64.lock.json", "RID-dependent lock fixture");
        Assert.NotEmpty(BuildVendorEvidence(publish).Files);
    }

    [Theory]
    [InlineData("sqlclient", "upstream/src/Microsoft.Data.SqlClient/src/bin/Unexpected.cs")]
    [InlineData("sqlclient", "upstream/src/Microsoft.Data.SqlClient/src/obj/Unexpected.cs")]
    [InlineData("sshnet", "upstream/src/Renci.SshNet/bin/Unexpected.cs")]
    [InlineData("sshnet", "upstream/src/Renci.SshNet/obj/Unexpected.cs")]
    [InlineData("sharpcompress", "upstream/src/SharpCompress/bin/Unexpected.cs")]
    [InlineData("sharpcompress", "upstream/src/SharpCompress/obj/Unexpected.cs")]
    [InlineData("sqlclient", "upstream/packages.lock.json")]
    [InlineData("sqlclient", "upstream/packages.linux-x64.lock.json")]
    [InlineData("sshnet", "upstream/packages.osx-arm64.lock.json")]
    [InlineData("sshnet", "upstream/SOURCE-SNAPSHOT.sha256")]
    public void Vendored_inventory_does_not_exclude_generated_names_in_source_directories(string vendor, string path)
    {
        var publish = CreatePublishPayload();
        var root = Path.Combine(_evidenceInputs[publish].ProductIdentitySourceRoot, "vendor", vendor);
        WriteFile(root, path, "unlisted source input");
        var error = Assert.Throws<InvalidDataException>(() => BuildVendorEvidence(publish));
        Assert.Contains("omits current input", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Vendored_inventory_rejects_unlisted_directory_links_without_traversal()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var publish = CreatePublishPayload();
        var root = Path.Combine(_evidenceInputs[publish].ProductIdentitySourceRoot, "vendor", "sqlclient");
        var outside = Path.Combine(_temporaryDirectory, "unlisted-outside-source");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "source.cs"), "outside sentinel");
        Directory.CreateSymbolicLink(Path.Combine(root, "unlisted"), outside);
        Assert.Throws<InvalidDataException>(() => BuildVendorEvidence(publish));
        Assert.Equal("outside sentinel", File.ReadAllText(Path.Combine(outside, "source.cs")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("upstream\\source.cs")]
    [InlineData("upstream/./source.cs")]
    [InlineData("duplicate")]
    [InlineData("bad-hash")]
    public void Vendored_inventory_rejects_malformed_or_escaping_entries(string entry)
    {
        var publish = CreatePublishPayload();
        var root = Path.Combine(_evidenceInputs[publish].ProductIdentitySourceRoot, "vendor", "sqlclient");
        var valid = HashFile(Path.Combine(root, "upstream/source.cs")) + "  upstream/source.cs\n";
        var content = entry switch
        {
            "duplicate" => valid + valid,
            "bad-hash" => new string('z', 64) + "  upstream/source.cs\n",
            _ => new string('a', 64) + "  " + entry + "\n",
        };
        File.WriteAllText(Path.Combine(root, "SOURCE-SNAPSHOT.sha256"), content);
        UpdateVendorInventoryHash(publish, root);
        Assert.Throws<InvalidDataException>(() => BuildVendorEvidence(publish));
    }

    [Fact]
    public void Vendored_inventory_rejects_symlinked_ancestors()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var publish = CreatePublishPayload();
        var root = Path.Combine(_evidenceInputs[publish].ProductIdentitySourceRoot, "vendor", "sqlclient");
        var outside = Path.Combine(_temporaryDirectory, "outside-source");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "source.cs"), "outside sentinel");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
        File.WriteAllText(Path.Combine(root, "SOURCE-SNAPSHOT.sha256"),
            HashFile(Path.Combine(outside, "source.cs")) + "  linked/source.cs\n");
        UpdateVendorInventoryHash(publish, root);
        Assert.Throws<InvalidDataException>(() => BuildVendorEvidence(publish));
        Assert.Equal("outside sentinel", File.ReadAllText(Path.Combine(outside, "source.cs")));
    }

    [Fact]
    public void Ordinary_project_cannot_claim_a_vendor_license_override()
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var ordinary = catalog["dependencies"]!.AsArray().Select(node => node!.AsObject())
            .First(node => node["identity"]!.GetValue<string>().StartsWith("GhostShell/", StringComparison.Ordinal));
        ordinary["licenseDeclared"] = "MIT";
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
        Assert.Throws<InvalidDataException>(() => BuildVendorEvidence(publish));
    }

    private void UpdateVendorInventoryHash(string publish, string root)
    {
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var vendor = catalog["dependencies"]!.AsArray().Select(node => node!.AsObject())
            .Single(node => node["identity"]!.GetValue<string>() == "Microsoft.Data.SqlClient.Routed/6.0.2");
        vendor["vendorSource"]!["sourceManifestSha256"] = HashFile(Path.Combine(root, "SOURCE-SNAPSHOT.sha256"));
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
    }

    [Theory]
    [InlineData("Microsoft.Data.SqlClient.Routed/6.0.2", "Microsoft.Data.SqlClient.Routed/0.0.1")]
    [InlineData("Microsoft.Data.SqlClient.Routed/6.0.2", "Microsoft.Data.SqlClient/6.0.2")]
    [InlineData("Microsoft.Data.SqlClient.Routed/6.0.2", "Microsoft.Data.SqlClient.Reference/6.0.0.0")]
    [InlineData("SSH.NET/2026.0.0", "Renci.SshNet/0.0.1")]
    [InlineData("SSH.NET/2026.0.0", "Renci.SshNet/2026.0.0.1")]
    [InlineData("SharpCompress/0.50.3", "SharpCompress/0.0.1")]
    public void Stale_vendor_project_identity_cannot_fall_back_to_ordinary_project_rules(string identity, string staleIdentity)
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var vendor = catalog["dependencies"]!.AsArray().Select(node => node!.AsObject())
            .Single(node => node["identity"]!.GetValue<string>() == identity);
        vendor["identity"] = staleIdentity;
        vendor.Remove("vendorSource");
        vendor["licenseDeclared"] = "NOASSERTION";
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
        var failure = Assert.Throws<InvalidDataException>(() => BuildVendorEvidence(publish));
        Assert.Contains("not a reviewed vendor identity", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("UnrelatedProvider/6.0.2")]
    public void Local_vendor_identity_requires_the_exact_reviewed_upstream_component(string? upstreamIdentity)
    {
        var publish = CreatePublishPayload();
        var inputs = _evidenceInputs[publish];
        var catalog = JsonNode.Parse(File.ReadAllText(inputs.CatalogPath))!.AsObject();
        var vendor = catalog["dependencies"]!.AsArray().Select(node => node!.AsObject())
            .Single(node => node["identity"]!.GetValue<string>() == "Microsoft.Data.SqlClient.Routed/6.0.2");
        var source = vendor["vendorSource"]!.AsObject();
        if (upstreamIdentity is null)
        {
            source.Remove("upstreamIdentity");
        }
        else
        {
            source["upstreamIdentity"] = upstreamIdentity;
        }
        File.WriteAllText(inputs.CatalogPath, catalog.ToJsonString());
        var failure = Assert.Throws<InvalidDataException>(() => BuildVendorEvidence(publish));
        if (upstreamIdentity is not null)
        {
            Assert.Contains("upstreamIdentity", failure.Message, StringComparison.Ordinal);
        }
    }

    private sealed record VendorFixture(string Identity, string UpstreamIdentity, string Directory, string Assembly, Version Version,
        string Url, string ArchiveHash, string Patch, string License);
}
