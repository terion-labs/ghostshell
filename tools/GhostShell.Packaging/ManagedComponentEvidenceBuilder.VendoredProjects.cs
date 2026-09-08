using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace GhostShell.Packaging;

internal static partial class ManagedComponentEvidenceBuilder
{
    // These are reviewed source builds, not arbitrary project license overrides.
    // Component version and CLR assembly version intentionally differ upstream.
    private static readonly IReadOnlyDictionary<string, VendoredProject> VendoredProjects =
        new Dictionary<string, VendoredProject>(StringComparer.Ordinal)
        {
            ["Microsoft.Data.SqlClient.Routed/6.0.2"] = new("Microsoft.Data.SqlClient/6.0.2", "sqlclient", "Microsoft.Data.SqlClient.dll", new(6, 0, 0, 0),
                "https://github.com/dotnet/SqlClient/archive/b16dec0a5622fd5b3d5311191bac4cafadc43e60.tar.gz",
                "f4c2cfd1a7a48f5e4f0b6b97639e5c17730fca1eade31668732280b82df21870", "routed-transport.patch", "upstream/LICENSE"),
            ["SSH.NET/2026.0.0"] = new("SSH.NET/2026.0.0", "sshnet", "Renci.SshNet.dll", new(2026, 0, 0, 1),
                "https://github.com/sshnet/SSH.NET/archive/7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e.tar.gz",
                "e3c305e2bf41d00f7aba51b9cdd151567dfe32c616ad322561ac59e3d6a4593b", "rfc1929-authentication.patch", "upstream/LICENSE"),
            ["SharpCompress/0.50.3"] = new("SharpCompress/0.50.3", "sharpcompress", "SharpCompress.dll", new(0, 50, 3, 1),
                "https://codeload.github.com/adamhathcock/sharpcompress/tar.gz/67bd9289f99dc77e1b65730a08e8213405921488",
                "9a3a4d57b279243ce24332fbb342c152b5c286172fa57881d9536e83c777afeb", "uncached-zip-enumeration.patch", "upstream/LICENSE.txt"),
        };

    private static void ValidateVendoredCatalogEntry(CatalogDependency component, VendoredProject vendor)
    {
        var source = component.VendorSource
            ?? throw CatalogError($"component {component.Identity} requires reviewed vendor source evidence");
        RequireEqual(component.File, vendor.File, component.Identity, "file");
        RequireEqual(component.LicenseDeclared, "MIT", component.Identity, "licenseDeclared");
        RequireEqual(source.UpstreamIdentity, vendor.UpstreamIdentity, component.Identity, "upstreamIdentity");
        RequireEqual(source.ArchiveUrl, vendor.ArchiveUrl, component.Identity, "archiveUrl");
        RequireEqual(source.ArchiveSha256, vendor.ArchiveSha256, component.Identity, "archiveSha256");
        ValidateSha256(source.SourceManifestSha256, component.Identity, "sourceManifestSha256");
        ValidateSha256(source.UpstreamManifestSha256, component.Identity, "upstreamManifestSha256");
        ValidateSha256(source.PatchSha256, component.Identity, "patchSha256");
        ValidateSha256(source.LicenseSha256, component.Identity, "licenseSha256");
    }

    private static PackageEvidence ValidateVendoredProject(string publishDirectory, string? sourceRoot,
        CatalogDependency component, VendoredProject vendor, EvidenceAccumulator evidence)
    {
        if (string.IsNullOrEmpty(sourceRoot))
        {
            throw CatalogError("vendored projects require the reviewed source root");
        }
        var root = ResolveVendorPath(Path.GetFullPath(sourceRoot), "vendor/" + vendor.Directory);
        var source = component.VendorSource!;
        var manifest = ReadVendorEvidence(root, "SOURCE-SNAPSHOT.sha256", source.SourceManifestSha256);
        var upstream = ReadVendorEvidence(root, "UPSTREAM-SOURCE.sha256", source.UpstreamManifestSha256);
        var patch = ReadVendorEvidence(root, vendor.Patch, source.PatchSha256);
        var license = ReadVendorEvidence(root, vendor.License, source.LicenseSha256);
        ValidateVendorInputManifest(root, manifest, vendor.Directory);
        var prefix = "Sources/" + vendor.Directory + "/";
        evidence.Add(prefix + "SOURCE-SNAPSHOT.sha256", manifest);
        evidence.Add(prefix + "UPSTREAM-SOURCE.sha256", upstream);
        evidence.Add(prefix + vendor.Patch, patch);
        evidence.Add(vendor.Directory + "-MIT.txt", license);

        var checksum = ValidatePublishedVendorAssembly(Path.Combine(publishDirectory, vendor.File), vendor);
        var (name, version) = ParseIdentity(vendor.UpstreamIdentity);
        return new PackageEvidence(component.Identity, name, version, "MIT", vendor.ArchiveUrl, checksum,
            $"Patched source build of {vendor.UpstreamIdentity}; local dependency identity {component.Identity}. "
            + $"Upstream archive SHA256 {vendor.ArchiveSha256}; "
            + $"upstream inventory SHA256 {source.UpstreamManifestSha256}; current input inventory SHA256 {source.SourceManifestSha256}; "
            + $"patch SHA256 {source.PatchSha256}; MIT license SHA256 {source.LicenseSha256}. "
            + $"All listed current inputs were rehashed. Component checksum is the actual published {vendor.File}.",
            null, "Source-built vendored component; not an unmodified NuGet binary or an upstream private signing assertion.", false);
    }

    private static byte[] ReadVendorEvidence(string root, string relativePath, string expectedHash)
    {
        var content = ReadRegularFile(ResolveVendorPath(root, relativePath), MaximumCatalogBytes, "vendor evidence");
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), expectedHash, StringComparison.Ordinal))
        {
            throw CatalogError($"reviewed vendor evidence hash differs for {relativePath}");
        }
        return content;
    }

    private static void ValidateVendorInputManifest(string root, byte[] content, string vendorDirectory)
    {
        var text = new UTF8Encoding(false, true).GetString(content);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) { continue; }
            if (line.Length < 67 || line.Length > 2048 || line[64] != ' ' || line[65] != ' ')
            {
                throw CatalogError("vendor source inventory has a malformed line");
            }
            var hash = line[..64];
            ValidateSha256(hash, "vendor input", "sha256");
            var relativePath = line[66..];
            if (!paths.Add(relativePath) || paths.Count > MaximumArchiveEntries)
            {
                throw CatalogError("vendor source inventory has duplicate or excessive entries");
            }
            using var stream = RegularPackageFileReader.Open(ResolveVendorPath(root, relativePath), out var file);
            totalBytes = checked(totalBytes + file.Length);
            if (totalBytes > MacOsAppBundleBuilder.MaximumPackageBytes
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), hash, StringComparison.Ordinal))
            {
                throw CatalogError($"reviewed vendor input differs for {relativePath}");
            }
        }
        if (paths.Count == 0) { throw CatalogError("vendor source inventory is empty"); }
        ValidateVendorInputSet(root, paths, vendorDirectory);
    }

    private static void ValidateVendorInputSet(string root, HashSet<string> expected, string vendorDirectory)
    {
        // Match update-managed-vendor-inventory.mjs and
        // update-sqlclient-provenance.mjs exclusions.
        // Rehashing listed inputs alone misses new files consumed by build globs.
        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);
        var directories = new Stack<string>();
        directories.Push(root);
        var entries = 0;
        while (directories.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaximumArchiveEntries)
                {
                    throw CatalogError("vendor source tree has excessive entries");
                }
                RejectVendorLink(path);
                var relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                var isDirectory = File.GetAttributes(path).HasFlag(FileAttributes.Directory);
                if (IsGeneratedVendorPath(vendorDirectory, relativePath, isDirectory)) { continue; }
                if (isDirectory)
                {
                    directories.Push(path);
                    continue;
                }
                if (!remaining.Remove(relativePath))
                {
                    throw CatalogError($"vendor source inventory omits current input {relativePath}");
                }
            }
        }
        if (remaining.Count != 0)
        {
            throw CatalogError("vendor source inventory does not match the current input set");
        }
    }

    private static bool IsGeneratedVendorPath(string vendorDirectory, string relativePath, bool isDirectory)
    {
        // Explicit Compile globs can include nested directories named bin/obj.
        // Only these integration project output roots are outside the inventory.
        if (isDirectory)
        {
            return relativePath is "bin" or "obj"
                || (vendorDirectory is "sqlclient" && relativePath is "tests/bin" or "tests/obj"
                    or "upstream/src/Microsoft.Data.SqlClient/netcore/src/bin"
                    or "upstream/src/Microsoft.Data.SqlClient/netcore/src/obj");
        }
        if (relativePath is "SOURCE-SNAPSHOT.sha256") { return true; }
        var separator = relativePath.LastIndexOf('/');
        var name = relativePath[(separator + 1)..];
        if (name is not ("packages.lock.json" or "packages.linux-x64.lock.json" or "packages.linux-arm64.lock.json"
            or "packages.osx-x64.lock.json" or "packages.osx-arm64.lock.json" or "packages.win-x64.lock.json"))
        {
            return false;
        }
        var directory = separator < 0 ? string.Empty : relativePath[..separator];
        return vendorDirectory is "sqlclient"
            ? directory is "tests" or "upstream/src/Microsoft.Data.SqlClient/netcore/src"
            : directory.Length == 0;
    }

    private static string ResolveVendorPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Any(character => char.IsControl(character) || character is '\\' or ':'))
        {
            throw CatalogError("vendor evidence path is invalid");
        }
        var path = root;
        RejectVendorLink(path);
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment is "" or "." or "..") { throw CatalogError("vendor evidence path escapes its root"); }
            path = Path.Combine(path, segment);
            RejectVendorLink(path);
        }
        return path;
    }

    private static void RejectVendorLink(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw CatalogError("vendor evidence contains a filesystem link");
        }
    }

    private static PackageChecksum ValidatePublishedVendorAssembly(string path, VendoredProject vendor)
    {
        using var stream = RegularPackageFileReader.Open(path, out var file);
        if (file.Length <= 0 || file.Length > MacOsAppBundleBuilder.MaximumPackageBytes)
        {
            throw CatalogError("published vendor assembly has invalid size");
        }
        using (var pe = new PEReader(stream, PEStreamOptions.LeaveOpen))
        {
            if (!pe.HasMetadata) { throw CatalogError("published vendor component is not a managed assembly"); }
            var metadata = pe.GetMetadataReader();
            var definition = metadata.GetAssemblyDefinition();
            if (!string.Equals(metadata.GetString(definition.Name), Path.GetFileNameWithoutExtension(vendor.File), StringComparison.Ordinal)
                || definition.Version != vendor.AssemblyVersion)
            {
                throw CatalogError("published vendor assembly identity is stale or incorrect");
            }
            foreach (var attributeHandle in definition.GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(attributeHandle);
                var type = attribute.Constructor.Kind switch
                {
                    HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                    HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                    _ => default,
                };
                var (name, ns) = type.Kind switch
                {
                    HandleKind.TypeReference => (metadata.GetTypeReference((TypeReferenceHandle)type).Name, metadata.GetTypeReference((TypeReferenceHandle)type).Namespace),
                    HandleKind.TypeDefinition => (metadata.GetTypeDefinition((TypeDefinitionHandle)type).Name, metadata.GetTypeDefinition((TypeDefinitionHandle)type).Namespace),
                    _ => default,
                };
                if (string.Equals(metadata.GetString(name), "ReferenceAssemblyAttribute", StringComparison.Ordinal)
                    && string.Equals(metadata.GetString(ns), "System.Runtime.CompilerServices", StringComparison.Ordinal))
                {
                    throw CatalogError("published vendor component is a reference assembly");
                }
            }
        }
        stream.Position = 0;
        return new PackageChecksum("SHA256", Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    private sealed record VendoredProject(string UpstreamIdentity, string Directory, string File, Version AssemblyVersion,
        string ArchiveUrl, string ArchiveSha256, string Patch, string License);

    private sealed class CatalogVendorSource
    {
        public required string UpstreamIdentity { get; init; }
        public required string ArchiveUrl { get; init; }
        public required string ArchiveSha256 { get; init; }
        public required string UpstreamManifestSha256 { get; init; }
        public required string SourceManifestSha256 { get; init; }
        public required string PatchSha256 { get; init; }
        public required string LicenseSha256 { get; init; }
    }
}
