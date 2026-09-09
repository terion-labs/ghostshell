using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asura.Packaging;

/// <summary>
/// Proves that a packaged terminal-font closure is the reviewed JetBrains Mono
/// 2.304 dependency pinned by Ghostty, rather than merely trusting filenames or
/// build-generated metadata supplied beside the package.
/// </summary>
internal static class TerminalFontPackageProvenance
{
    private const int MaximumDocumentBytes = 1024 * 1024;
    private const string CatalogFormat =
        "asura-terminal-font-assets-catalog-v1";
    private const string ReceiptFormat =
        "asura-terminal-font-assets-build-receipt-v1";
    private const string CatalogFileName = "terminal-font-assets.json";
    private const string ReceiptFileName =
        "terminal-font-assets-build-receipt.json";
    private const string FontDirectory = "fonts/JetBrainsMono";
    private const string ManifestPath = "fonts/JetBrainsMono/MANIFEST.sha256";
    private const string LicensePath = "fonts/JetBrainsMono/OFL.txt";
    private const string PackagedLicenseFileName = "JetBrainsMono-OFL.txt";
    private const string SourceRepository =
        "https://github.com/ghostty-org/ghostty.git";
    private const string SourceCommit =
        "08f039fbb3dea9c6b1cdb5ff4550666598122346";
    private const string DependencyName = "JetBrains Mono";
    private const string DependencyVersion = "2.304";
    private const string DependencyUrl =
        "https://deps.files.ghostty.org/JetBrainsMono-2.304.tar.gz";
    private const string DependencyZigPackageHash =
        "N-V-__8AAIC5lwAVPJJzxnCAahSvZTIlG-HhtOvnM1uh-66x";
    private const string DependencyLicense = "OFL-1.1";
    private const string ReceiptGenerator =
        "scripts/build-terminal-font-assets.sh";
    private const string CatalogId =
        "asura-jetbrains-mono-2.304-20260801";

    private static readonly Asset[] ExpectedAssets =
    [
        new(
            "JetBrainsMono-Regular.ttf",
            "fonts/ttf/JetBrainsMono-Regular.ttf",
            "normal",
            400,
            273900,
            "a0bf60ef0f83c5ed4d7a75d45838548b1f6873372dfac88f71804491898d138f"),
        new(
            "JetBrainsMono-Bold.ttf",
            "fonts/ttf/JetBrainsMono-Bold.ttf",
            "normal",
            700,
            277828,
            "5590990c82e097397517f275f430af4546e1c45cff408bde4255dad142479dcb"),
        new(
            "JetBrainsMono-Italic.ttf",
            "fonts/ttf/JetBrainsMono-Italic.ttf",
            "italic",
            400,
            276840,
            "9d0a1f7a708e6af183f1193b7e81d40da294f5c67682c085d8401c60aac8ded4"),
        new(
            "JetBrainsMono-BoldItalic.ttf",
            "fonts/ttf/JetBrainsMono-BoldItalic.ttf",
            "italic",
            700,
            279832,
            "4039d5ce0ed225bf9c8b2c8c6436290ae2f356b7e90d70fa666227238324aa3b"),
    ];

    private static readonly Asset ExpectedLicense = new(
        "OFL.txt",
        "OFL.txt",
        "license",
        0,
        4399,
        "30f0c136e3c88e422d0791acd97238870f9054a9729bc34cf2ff0d4ed8cac4ad");

    public static void Validate(
        string resourceDirectory,
        string licenseDirectory,
        string catalogPath,
        string receiptPath)
    {
        var catalogBytes = ReadRegularFile(
            catalogPath,
            MaximumDocumentBytes,
            "terminal-font asset catalog");
        var receiptBytes = ReadRegularFile(
            receiptPath,
            MaximumDocumentBytes,
            "terminal-font build receipt");
        using var catalog = Parse(catalogBytes, "terminal-font asset catalog");
        using var receipt = Parse(receiptBytes, "terminal-font build receipt");

        ValidateCatalog(catalog.RootElement);
        ValidateReceipt(receipt.RootElement, catalogBytes);

        var nativeLicenseDirectory = Path.Combine(licenseDirectory, "Native");
        RequireExactCopy(
            catalogBytes,
            Path.Combine(nativeLicenseDirectory, CatalogFileName),
            "packaged terminal-font asset catalog");
        RequireExactCopy(
            receiptBytes,
            Path.Combine(nativeLicenseDirectory, ReceiptFileName),
            "packaged terminal-font build receipt");

        var packageDirectory = Resolve(resourceDirectory, FontDirectory);
        ValidateDirectoryClosure(resourceDirectory, packageDirectory);
        ValidatePackagedAssets(packageDirectory);
        ValidateManifest(packageDirectory, receipt.RootElement);
        ValidateLicenseCopies(packageDirectory, licenseDirectory);
    }

    private static void ValidateCatalog(JsonElement root)
    {
        RequireShape(
            root,
            "terminal-font catalog",
            "schemaVersion",
            "format",
            "catalogId",
            "source",
            "dependency",
            "assets",
            "license");
        RequireInt64(root, "schemaVersion", 1);
        RequireString(root, "format", CatalogFormat);
        RequireString(root, "catalogId", CatalogId);
        ValidateSource(RequireObject(root, "source"));
        ValidateDependency(RequireObject(root, "dependency"));
        ValidateAssets(RequireArray(root, "assets"), catalogAssets: true);

        var license = RequireObject(root, "license");
        RequireShape(
            license,
            "terminal-font catalog license",
            "file",
            "sourcePath",
            "bytes",
            "sha256");
        RequireString(license, "file", ExpectedLicense.File);
        RequireString(license, "sourcePath", ExpectedLicense.SourcePath);
        RequireInt64(license, "bytes", ExpectedLicense.Bytes);
        RequireString(license, "sha256", ExpectedLicense.Sha256);
    }

    private static void ValidateReceipt(JsonElement root, byte[] catalogBytes)
    {
        RequireShape(
            root,
            "terminal-font receipt",
            "schemaVersion",
            "format",
            "generator",
            "catalogSha256",
            "source",
            "dependency",
            "directory",
            "manifest",
            "assets",
            "license");
        RequireInt64(root, "schemaVersion", 1);
        RequireString(root, "format", ReceiptFormat);
        RequireString(root, "generator", ReceiptGenerator);
        RequireString(
            root,
            "catalogSha256",
            Convert.ToHexStringLower(SHA256.HashData(catalogBytes)));
        ValidateSource(RequireObject(root, "source"));
        ValidateDependency(RequireObject(root, "dependency"));
        RequireString(root, "directory", FontDirectory);
        ValidateAssets(RequireArray(root, "assets"), catalogAssets: false);

        var license = RequireObject(root, "license");
        RequireShape(license, "terminal-font receipt license", "path", "bytes", "sha256");
        RequireString(license, "path", LicensePath);
        RequireInt64(license, "bytes", ExpectedLicense.Bytes);
        RequireString(license, "sha256", ExpectedLicense.Sha256);
    }

    private static void ValidateSource(JsonElement source)
    {
        RequireShape(source, "terminal-font source", "repository", "commit");
        RequireString(source, "repository", SourceRepository);
        RequireString(source, "commit", SourceCommit);
    }

    private static void ValidateDependency(JsonElement dependency)
    {
        RequireShape(
            dependency,
            "terminal-font dependency",
            "name",
            "version",
            "url",
            "zigPackageHash",
            "license");
        RequireString(dependency, "name", DependencyName);
        RequireString(dependency, "version", DependencyVersion);
        RequireString(dependency, "url", DependencyUrl);
        RequireString(dependency, "zigPackageHash", DependencyZigPackageHash);
        RequireString(dependency, "license", DependencyLicense);
    }

    private static void ValidateAssets(JsonElement assets, bool catalogAssets)
    {
        if (assets.GetArrayLength() != ExpectedAssets.Length)
        {
            throw new InvalidDataException(
                "The terminal-font evidence has an unexpected asset count.");
        }

        var expectedByFile = ExpectedAssets.ToDictionary(
            static asset => asset.File,
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assetElement in assets.EnumerateArray())
        {
            RequireShape(
                assetElement,
                catalogAssets
                    ? "terminal-font catalog asset"
                    : "terminal-font receipt asset",
                "file",
                "sourcePath",
                "style",
                "weight",
                "bytes",
                "sha256");

            var file = RequireString(assetElement, "file");
            if (!expectedByFile.TryGetValue(file, out var expected) || !seen.Add(file))
            {
                throw new InvalidDataException(
                    "The terminal-font evidence contains an unexpected or duplicate asset.");
            }

            RequireString(assetElement, "sourcePath", expected.SourcePath);
            RequireString(assetElement, "style", expected.Style);
            RequireInt64(assetElement, "weight", expected.Weight);
            RequireInt64(assetElement, "bytes", expected.Bytes);
            RequireString(assetElement, "sha256", expected.Sha256);
        }
    }

    private static void ValidateDirectoryClosure(
        string executableDirectory,
        string packageDirectory)
    {
        RequireRegularDirectory(executableDirectory, "package executable directory");
        RequireRegularDirectory(
            Path.Combine(executableDirectory, "fonts"),
            "packaged font directory");
        RequireRegularDirectory(packageDirectory, "packaged JetBrains Mono directory");

        var expectedNames = ExpectedAssets
            .Select(static asset => asset.File)
            .Append(ExpectedLicense.File)
            .Append(Path.GetFileName(ManifestPath))
            .ToHashSet(StringComparer.Ordinal);
        var entries = new DirectoryInfo(packageDirectory)
            .EnumerateFileSystemInfos()
            .ToArray();
        if (entries.Length != expectedNames.Count
            || entries.Any(entry =>
                entry is not FileInfo
                || entry.LinkTarget is not null
                || !expectedNames.Contains(entry.Name)))
        {
            throw new InvalidDataException(
                "The packaged JetBrains Mono directory has an unexpected file closure.");
        }
    }

    private static void ValidatePackagedAssets(string packageDirectory)
    {
        foreach (var asset in ExpectedAssets)
        {
            var bytes = ValidateFile(
                Path.Combine(packageDirectory, asset.File),
                asset,
                $"packaged terminal font {asset.File}");
            if (bytes.Length < 4
                || bytes[0] != 0x00
                || bytes[1] != 0x01
                || bytes[2] != 0x00
                || bytes[3] != 0x00)
            {
                throw new InvalidDataException(
                    $"The packaged terminal font {asset.File} is not a TrueType font.");
            }
        }

        _ = ValidateFile(
            Path.Combine(packageDirectory, ExpectedLicense.File),
            ExpectedLicense,
            "packaged JetBrains Mono license");
    }

    private static void ValidateManifest(string packageDirectory, JsonElement receipt)
    {
        var manifest = RequireObject(receipt, "manifest");
        RequireShape(
            manifest,
            "terminal-font manifest evidence",
            "path",
            "fileCount",
            "bytes",
            "sha256");
        RequireString(manifest, "path", ManifestPath);
        RequireInt64(
            manifest,
            "fileCount",
            ExpectedAssets.Length + 1);

        var manifestPath = Path.Combine(packageDirectory, "MANIFEST.sha256");
        var bytes = ReadRegularFile(
            manifestPath,
            64 * 1024,
            "packaged terminal-font manifest");
        RequireInt64(manifest, "bytes", bytes.LongLength);
        RequireString(
            manifest,
            "sha256",
            Convert.ToHexStringLower(SHA256.HashData(bytes)));

        var expectedEntries = ExpectedAssets
            .Append(ExpectedLicense)
            .OrderBy(static asset => asset.File, StringComparer.Ordinal)
            .Select(static asset => $"{asset.Sha256}  {asset.File}");
        var expectedBytes = Encoding.UTF8.GetBytes(
            string.Join('\n', expectedEntries) + "\n");
        if (!bytes.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new InvalidDataException(
                "The terminal-font manifest is invalid, unsorted, or incomplete.");
        }
    }

    private static void ValidateLicenseCopies(
        string packageDirectory,
        string licenseDirectory)
    {
        var source = ReadRegularFile(
            Path.Combine(packageDirectory, ExpectedLicense.File),
            checked((int)ExpectedLicense.Bytes),
            "packaged JetBrains Mono license");
        var installed = ReadRegularFile(
            Path.Combine(licenseDirectory, PackagedLicenseFileName),
            checked((int)ExpectedLicense.Bytes),
            "installed JetBrains Mono license");
        if (!source.AsSpan().SequenceEqual(installed))
        {
            throw new InvalidDataException(
                "The installed JetBrains Mono license differs from the packaged font license.");
        }
    }

    private static byte[] ValidateFile(string path, Asset expected, string label)
    {
        var bytes = ReadRegularFile(path, checked((int)expected.Bytes), label);
        if (bytes.LongLength != expected.Bytes
            || !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                expected.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The {label} differs from the reviewed dependency.");
        }

        return bytes;
    }

    private static void RequireExactCopy(byte[] expected, string path, string label)
    {
        var actual = ReadRegularFile(path, MaximumDocumentBytes, label);
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidDataException(
                $"The {label} differs from its reviewed source.");
        }
    }

    private static byte[] ReadRegularFile(string path, int maximumBytes, string label)
    {
        try
        {
            using var stream = RegularPackageFileReader.Open(path, out var inspection);
            if (inspection.Length < 1 || inspection.Length > maximumBytes)
            {
                throw new InvalidDataException(
                    $"The {label} is empty or exceeds its size bound.");
            }

            var bytes = new byte[checked((int)inspection.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"The {label} is unavailable or is not a regular file.",
                exception);
        }
    }

    private static JsonDocument Parse(byte[] content, string label)
    {
        try
        {
            return JsonDocument.Parse(
                content,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 10,
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {label} is not valid JSON.", exception);
        }
    }

    private static void RequireRegularDirectory(string path, string label)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || directory.LinkTarget is not null)
        {
            throw new InvalidDataException(
                $"The {label} is missing or is a symbolic link.");
        }
    }

    private static string Resolve(string root, string relativePath) =>
        Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void RequireShape(
        JsonElement element,
        string label,
        params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {label} must be an object.");
        }

        var expected = propertyNames.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The {label} contains an unexpected or duplicate property.");
            }
        }

        if (!seen.SetEquals(expected))
        {
            throw new InvalidDataException(
                $"The {label} is missing a required property.");
        }
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"The terminal-font evidence requires object {name}.");
        }

        return value;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"The terminal-font evidence requires array {name}.");
        }

        return value;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"The terminal-font evidence requires string {name}.");
        }

        return value.GetString()!;
    }

    private static void RequireString(
        JsonElement parent,
        string name,
        string expected)
    {
        if (!string.Equals(
                RequireString(parent, name),
                expected,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The terminal-font evidence has an unexpected {name} value.");
        }
    }

    private static void RequireInt64(
        JsonElement parent,
        string name,
        long expected)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var actual)
            || actual != expected)
        {
            throw new InvalidDataException(
                $"The terminal-font evidence has an unexpected {name} value.");
        }
    }

    private sealed record Asset(
        string File,
        string SourcePath,
        string Style,
        long Weight,
        long Bytes,
        string Sha256);
}
