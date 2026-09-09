using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Asura.Packaging;

internal sealed record MacOsProductIdentityEvidence(
    byte[] Manifest,
    byte[] IcnsFallback,
    byte[] AssetCatalog);

/// <summary>
/// Parses the reviewed identity once, then binds every packaged icon byte to its
/// checked-in source. Xcode remains responsible for interpreting the layered
/// document; this boundary only accepts the exact approved inputs and its CAR.
/// </summary>
internal static class MacOsProductIdentity
{
    internal const string DisplayName = "Asura";
    internal const string ExecutableName = "Asura";
    internal const string BundleIdentifier = "sh.asura";
    internal const string IconName = "Asura";

    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumIcnsBytes = 16 * 1024 * 1024;
    private const int MaximumAssetCatalogBytes = 64 * 1024 * 1024;
    private const string ManifestFormat = "asura-macos-product-identity-v1";
    private const string FallbackRole = "icns-fallback";

    private static readonly string[] RequiredAppearances =
    [
        "Default",
        "Dark",
        "TintedLight",
        "TintedDark",
        "ClearLight",
        "ClearDark",
    ];

    private static readonly IReadOnlyDictionary<string, string> RequiredFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["icon-composer-document"] = "assets/macos/Asura.icon/icon.json",
            ["source-artwork"] = "assets/macos/Asura.icon/Assets/logo.svg",
            [FallbackRole] = "assets/macos/Asura.icns",
        };

    private static readonly string[] RequiredIcnsTypes =
    [
        "ic04",
        "ic05",
        "ic07",
        "ic08",
        "ic09",
        "ic10",
        "ic11",
        "ic12",
        "ic13",
        "ic14",
    ];

    public static MacOsProductIdentityEvidence Validate(
        string manifestPath,
        string sourceRoot,
        string assetCatalogPath)
    {
        var manifest = ReadRegularFile(
            manifestPath,
            "product identity manifest",
            MaximumManifestBytes);
        var root = MacOsPackagePaths.RequireExistingDirectory(
            sourceRoot,
            nameof(sourceRoot));
        var files = ParseManifest(manifest);
        byte[]? fallback = null;
        foreach (var file in files)
        {
            var path = ResolveSourcePath(root, file.Path);
            var content = ReadRegularFile(path, file.Role, MaximumIcnsBytes);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
            if (!string.Equals(sha256, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The reviewed product identity hash does not match {file.Path}.");
            }

            if (string.Equals(file.Role, FallbackRole, StringComparison.Ordinal))
            {
                fallback = content;
            }
        }

        ValidateIcns(fallback
            ?? throw new InvalidDataException(
                "The product identity manifest has no ICNS fallback."));
        var assetCatalog = ReadRegularFile(
            assetCatalogPath,
            "Xcode asset catalog",
            MaximumAssetCatalogBytes);
        if (assetCatalog.Length < 16
            || !assetCatalog.AsSpan(0, 8).SequenceEqual("BOMStore"u8))
        {
            throw new InvalidDataException(
                "The compiled macOS application icon is not an Assets.car archive.");
        }

        return new MacOsProductIdentityEvidence(manifest, fallback, assetCatalog);
    }

    private static IReadOnlyList<ManifestFile> ParseManifest(byte[] content)
    {
        using var document = JsonDocument.Parse(
            content,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        var root = document.RootElement;
        RequireObject(root, "product identity manifest");
        RequireProperties(
            root,
            "format",
            "platform",
            "displayName",
            "bundleName",
            "executableName",
            "bundleIdentifier",
            "iconName",
            "artwork",
            "approval",
            "requiredAppearances",
            "files");
        RequireExactString(root, "format", ManifestFormat);
        RequireExactString(root, "platform", "macos");
        RequireExactString(root, "displayName", DisplayName);
        RequireExactString(root, "bundleName", DisplayName);
        RequireExactString(root, "executableName", ExecutableName);
        RequireExactString(root, "bundleIdentifier", BundleIdentifier);
        RequireExactString(root, "iconName", IconName);
        ValidateArtwork(root.GetProperty("artwork"));
        ValidateApproval(root.GetProperty("approval"));
        ValidateAppearances(root.GetProperty("requiredAppearances"));
        return ParseFiles(root.GetProperty("files"));
    }

    private static void ValidateArtwork(JsonElement artwork)
    {
        RequireObject(artwork, "artwork");
        RequireProperties(artwork, "source", "owner", "license", "copyright");
        RequireExactString(artwork, "source", "Original first-party Asura artwork");
        RequireExactString(artwork, "owner", "Terion Labs");
        RequireExactString(artwork, "license", "MIT");
        RequireExactString(
            artwork,
            "copyright",
            "Copyright (c) 2026 Terion Labs");
    }

    private static void ValidateApproval(JsonElement approval)
    {
        RequireObject(approval, "approval");
        RequireProperties(approval, "status", "approvedBy", "approvedAt", "evidence");
        RequireExactString(approval, "status", "approved");
        RequireExactString(
            approval,
            "approvedBy",
            "terion-labs/asura maintainer");
        RequireExactString(approval, "approvedAt", "2026-08-25");
        RequireExactString(
            approval,
            "evidence",
            "https://github.com/terion-labs/asura/issues/42");
    }

    private static void ValidateAppearances(JsonElement appearances)
    {
        if (appearances.ValueKind != JsonValueKind.Array
            || !appearances.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : null)
                .SequenceEqual(RequiredAppearances, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The product identity manifest does not declare every reviewed appearance.");
        }
    }

    private static IReadOnlyList<ManifestFile> ParseFiles(JsonElement files)
    {
        if (files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The product identity manifest files value must be an array.");
        }

        var parsed = new List<ManifestFile>();
        foreach (var file in files.EnumerateArray())
        {
            RequireObject(file, "product identity file");
            RequireProperties(file, "role", "path", "sha256");
            var role = RequireString(file, "role");
            var path = RequireString(file, "path");
            var sha256 = RequireString(file, "sha256");
            if (!RequiredFiles.TryGetValue(role, out var requiredPath)
                || !string.Equals(path, requiredPath, StringComparison.Ordinal)
                || sha256.Length != 64
                || !sha256.All(character => character is >= '0' and <= '9'
                    or >= 'a' and <= 'f'))
            {
                throw new InvalidDataException(
                    "The product identity manifest contains an unreviewed file entry.");
            }

            parsed.Add(new ManifestFile(role, path, sha256));
        }

        if (parsed.Count != RequiredFiles.Count
            || parsed.Select(file => file.Role).Distinct(StringComparer.Ordinal).Count()
                != RequiredFiles.Count)
        {
            throw new InvalidDataException(
                "The product identity manifest file closure is incomplete or duplicated.");
        }

        return parsed;
    }

    private static string ResolveSourcePath(string root, string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(root, normalizedRelativePath));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The product identity manifest references a file outside its source root.");
        }

        return resolved;
    }

    private static byte[] ReadRegularFile(
        string path,
        string description,
        int maximumBytes)
    {
        using var stream = RegularPackageFileReader.Open(path, out var inspection);
        if (inspection.Length is < 8 || inspection.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The {description} has an invalid size.");
        }

        var content = new byte[(int)inspection.Length];
        stream.ReadExactly(content);
        return content;
    }

    private static void ValidateIcns(byte[] icon)
    {
        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(4, 4));
        if (!icon.AsSpan(0, 4).SequenceEqual("icns"u8)
            || declaredLength != icon.Length)
        {
            throw new InvalidDataException(
                "The reviewed macOS fallback icon is not a valid ICNS container.");
        }

        var types = new HashSet<string>(StringComparer.Ordinal);
        var offset = 8;
        while (offset < icon.Length)
        {
            if (icon.Length - offset < 8)
            {
                throw new InvalidDataException(
                    "The reviewed macOS fallback icon has a truncated entry.");
            }

            var type = System.Text.Encoding.ASCII.GetString(icon, offset, 4);
            var length = BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(offset + 4, 4));
            if (length < 8 || length > icon.Length - offset)
            {
                throw new InvalidDataException(
                    "The reviewed macOS fallback icon has an invalid entry length.");
            }

            types.Add(type);
            offset += checked((int)length);
        }

        var missing = RequiredIcnsTypes.Where(type => !types.Contains(type)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"The reviewed macOS fallback icon is missing required sizes: {string.Join(", ", missing)}.");
        }
    }

    private static void RequireObject(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {description} must be an object.");
        }
    }

    private static void RequireProperties(JsonElement value, params string[] expected)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length
            || actual.Except(expected, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException(
                "The product identity manifest contains an unknown or missing property.");
        }
    }

    private static void RequireExactString(
        JsonElement value,
        string propertyName,
        string expected)
    {
        if (!string.Equals(
                RequireString(value, propertyName),
                expected,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The product identity manifest has an unexpected {propertyName}.");
        }
    }

    private static string RequireString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"The product identity manifest requires string property {propertyName}.");
        }

        return property.GetString()!;
    }

    private sealed record ManifestFile(string Role, string Path, string Sha256);
}
