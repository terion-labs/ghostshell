using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Asura.Packaging;

internal sealed record VelopackMacOsReleaseInspection(
    string PackageFileName,
    string PackageSha256,
    int ApplicationFileCount);

/// <summary>
/// Proves that the channel feed and full update package describe the same
/// Velopack-aware application that was extracted from the portable archive.
/// Package file modes are intentionally not compared: Velopack 1.2 normalizes
/// macOS update payloads to mode 755 while applying them.
/// </summary>
internal static partial class VelopackMacOsRelease
{
    private const string PackageId = "sh.asura";
    private const string PackagePrefix = "lib/app/";
    private const string MetadataPath = "Contents/Resources/sq.version";
    private const string MetadataLinkPath = "Contents/MacOS/sq.version";
    private const string MetadataLinkTarget = "../Resources/sq.version";
    private const int MaximumFiles = 20_000;
    private const long MaximumBytes = 8L * 1024 * 1024 * 1024;

    [GeneratedRegex(
        "^[0-9]+\\.[0-9]+\\.[0-9]+$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(
        "^osx-arm64-[a-z0-9][a-z0-9-]{0,31}$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex ChannelPattern();

    public static VelopackMacOsReleaseInspection Validate(
        VelopackMacOsReleaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!VersionPattern().IsMatch(command.Version))
        {
            throw new InvalidDataException(
                "The Velopack release version must contain three unsigned integer parts.");
        }

        if (!ChannelPattern().IsMatch(command.Channel))
        {
            throw new InvalidDataException(
                "The Velopack macOS arm64 channel is invalid.");
        }

        var releaseDirectory = MacOsPackagePaths.RequireExistingDirectory(
            command.ReleaseDirectory,
            nameof(command.ReleaseDirectory));
        var applicationPath = MacOsPackagePaths.RequireExistingDirectory(
            command.ApplicationPath,
            nameof(command.ApplicationPath));
        if (!string.Equals(
                Path.GetFileName(applicationPath),
                MacOsPackagePaths.BundleName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The Velopack application must be named {MacOsPackagePaths.BundleName}.");
        }

        var expectedPackageName =
            $"{PackageId}-{command.Version}-{command.Channel}-full.nupkg";
        var suppliedPackagePath = Path.GetFullPath(command.FullPackagePath);
        var suppliedPackageDirectory = Path.GetDirectoryName(suppliedPackagePath)
            ?? throw new InvalidDataException(
                "The full update package has no parent directory.");
        var physicalPackageDirectory = MacOsPackagePaths.RequireExistingDirectory(
            suppliedPackageDirectory,
            nameof(command.FullPackagePath));
        var packagePath = Path.Combine(
            physicalPackageDirectory,
            Path.GetFileName(suppliedPackagePath));
        if (!string.Equals(
                physicalPackageDirectory,
                releaseDirectory,
                StringComparison.Ordinal)
            || !string.Equals(
                Path.GetFileName(packagePath),
                expectedPackageName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The full update package has an unexpected path or file name.");
        }

        using var packageStream = RegularPackageFileReader.Open(
            packagePath,
            out var packageFile);
        if (packageFile.Length is <= 0 or > MaximumBytes)
        {
            throw new InvalidDataException(
                "The full update package exceeds its size boundary.");
        }

        var packageSha256 = Convert.ToHexString(
            SHA256.HashData(packageStream)).ToLowerInvariant();
        ValidateFeed(
            releaseDirectory,
            command,
            expectedPackageName,
            packageFile.Length,
            packageSha256);
        packageStream.Position = 0;
        var fileCount = ValidatePackageApplication(
            packageStream,
            applicationPath,
            command);

        return new VelopackMacOsReleaseInspection(
            expectedPackageName,
            packageSha256,
            fileCount);
    }

    private static void ValidateFeed(
        string releaseDirectory,
        VelopackMacOsReleaseCommand command,
        string packageName,
        long packageLength,
        string packageSha256)
    {
        var feedPath = Path.Combine(
            releaseDirectory,
            $"releases.{command.Channel}.json");
        using var feedStream = RegularPackageFileReader.Open(feedPath, out var feedFile);
        if (feedFile.Length is <= 0 or > 1_000_000)
        {
            throw new InvalidDataException("The Velopack channel feed is outside its size boundary.");
        }

        using var document = JsonDocument.Parse(feedStream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !HasExactProperties(root, "Assets")
            || root.GetProperty("Assets") is not { ValueKind: JsonValueKind.Array } assets
            || assets.GetArrayLength() != 1)
        {
            throw new InvalidDataException(
                "The Velopack channel feed must contain exactly one asset.");
        }

        var asset = assets[0];
        if (asset.ValueKind != JsonValueKind.Object
            || !HasExactProperties(
                asset,
                "PackageId",
                "Version",
                "Type",
                "FileName",
                "SHA1",
                "SHA256",
                "Size")
            || !StringPropertyEquals(asset, "PackageId", PackageId)
            || !StringPropertyEquals(asset, "Version", command.Version)
            || !StringPropertyEquals(asset, "Type", "Full")
            || !StringPropertyEquals(asset, "FileName", packageName)
            || !StringPropertyEquals(
                asset,
                "SHA256",
                packageSha256.ToUpperInvariant())
            || asset.GetProperty("SHA1").GetString() is not { Length: 40 } sha1
            || !sha1.All(Uri.IsHexDigit)
            || !asset.GetProperty("Size").TryGetInt64(out var feedSize)
            || feedSize != packageLength)
        {
            throw new InvalidDataException(
                "The Velopack channel feed does not identify the exact full package.");
        }
    }

    private static int ValidatePackageApplication(
        Stream packageStream,
        string applicationPath,
        VelopackMacOsReleaseCommand command)
    {
        using var archive = new ZipArchive(
            packageStream,
            ZipArchiveMode.Read,
            leaveOpen: true);
        var packageEntries = new Dictionary<string, ZipArchiveEntry>(
            StringComparer.Ordinal);
        ZipArchiveEntry? nuspecEntry = null;
        long uncompressedBytes = 0;
        if (archive.Entries.Count > MaximumFiles + 16)
        {
            throw new InvalidDataException(
                "The full update package contains too many entries.");
        }

        foreach (var entry in archive.Entries)
        {
            ValidateEntryName(entry.FullName);
            uncompressedBytes = checked(uncompressedBytes + entry.Length);
            if (uncompressedBytes > MaximumBytes)
            {
                throw new InvalidDataException(
                    "The full update package exceeds its uncompressed size boundary.");
            }

            if (entry.FullName.StartsWith(PackagePrefix, StringComparison.Ordinal)
                && !entry.FullName.EndsWith('/')
                && !packageEntries.TryAdd(entry.FullName[PackagePrefix.Length..], entry))
            {
                throw new InvalidDataException(
                    "The full update package contains a duplicate application entry.");
            }
            else if (string.Equals(
                entry.FullName,
                PackageId + ".nuspec",
                StringComparison.Ordinal))
            {
                if (nuspecEntry is not null)
                {
                    throw new InvalidDataException(
                        "The full update package contains duplicate nuspec metadata.");
                }

                nuspecEntry = entry;
            }
            else if (!entry.FullName.StartsWith(PackagePrefix, StringComparison.Ordinal)
                && entry.FullName is not "[Content_Types].xml" and not "_rels/.rels")
            {
                throw new InvalidDataException(
                    "The full update package contains an unexpected metadata entry.");
            }
        }

        if (nuspecEntry is null)
        {
            throw new InvalidDataException(
                "The full update package does not contain its nuspec metadata.");
        }

        var applicationEntries = EnumerateApplication(applicationPath);
        if (applicationEntries.Count == 0 || applicationEntries.Count > MaximumFiles)
        {
            throw new InvalidDataException(
                "The Velopack application file count is outside its boundary.");
        }

        long totalBytes = 0;
        foreach (var applicationEntry in applicationEntries)
        {
            var packageEntryName = applicationEntry.IsLink
                ? applicationEntry.RelativePath + ".__symlink"
                : applicationEntry.RelativePath;
            if (!packageEntries.Remove(packageEntryName, out var packageEntry))
            {
                throw new InvalidDataException(
                    $"The full update package is missing {applicationEntry.RelativePath}.");
            }

            using var packageFile = packageEntry.Open();
            if (applicationEntry.IsLink)
            {
                using var reader = new StreamReader(
                    packageFile,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: false);
                if (!string.Equals(
                        reader.ReadToEnd(),
                        MetadataLinkTarget,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The full update package contains an invalid metadata link.");
                }

                continue;
            }

            using var applicationFile = RegularPackageFileReader.Open(
                applicationEntry.FullPath,
                out var inspection);
            totalBytes = checked(totalBytes + inspection.Length);
            if (totalBytes > MaximumBytes
                || packageEntry.Length != inspection.Length
                || !SHA256.HashData(packageFile).SequenceEqual(
                    SHA256.HashData(applicationFile)))
            {
                throw new InvalidDataException(
                    $"The full update package differs at {applicationEntry.RelativePath}.");
            }
        }

        if (packageEntries.Count != 0)
        {
            throw new InvalidDataException(
                "The full update package contains application files outside the portable bundle.");
        }

        ValidateMetadata(applicationPath, command, nuspecEntry);
        return applicationEntries.Count;
    }

    private static IReadOnlyList<ApplicationEntry> EnumerateApplication(
        string applicationPath)
    {
        var entries = new List<ApplicationEntry>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(applicationPath));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                var relativePath = Path.GetRelativePath(
                        applicationPath,
                        entry.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (entry is not FileInfo
                        || !string.Equals(
                            relativePath,
                            MetadataLinkPath,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            entry.LinkTarget,
                            MetadataLinkTarget,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The Velopack application contains an unsupported symbolic link.");
                    }

                    entries.Add(new ApplicationEntry(
                        entry.FullName,
                        relativePath,
                        IsLink: true));
                    continue;
                }

                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory);
                }
                else if (entry is FileInfo)
                {
                    entries.Add(new ApplicationEntry(
                        entry.FullName,
                        relativePath,
                        IsLink: false));
                }
                else
                {
                    throw new InvalidDataException(
                        "The Velopack application contains an unsupported filesystem entry.");
                }
            }
        }

        return entries;
    }

    private static void ValidateMetadata(
        string applicationPath,
        VelopackMacOsReleaseCommand command,
        ZipArchiveEntry nuspecEntry)
    {
        var metadataPath = Path.Combine(
            applicationPath,
            MetadataPath.Replace('/', Path.DirectorySeparatorChar));
        using var stream = RegularPackageFileReader.Open(metadataPath, out var metadataFile);
        if (metadataFile.Length is <= 0 or > 100_000)
        {
            throw new InvalidDataException("Velopack metadata exceeds its size boundary.");
        }

        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = 100_000,
            XmlResolver = null,
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var metadata = document.Root?.Elements().SingleOrDefault(
            element => string.Equals(
                element.Name.LocalName,
                "metadata",
                StringComparison.Ordinal));
        if (metadata is null
            || !ElementEquals(metadata, "id", PackageId)
            || !ElementEquals(metadata, "version", command.Version)
            || !ElementEquals(metadata, "channel", command.Channel)
            || !ElementEquals(metadata, "mainExe", "Contents/MacOS/Asura")
            || !ElementEquals(metadata, "os", "osx")
            || !ElementEquals(metadata, "rid", "osx-arm64")
            || !ElementEquals(metadata, "machineArchitecture", "arm64"))
        {
            throw new InvalidDataException(
                "The Velopack bundle metadata does not match the release identity.");
        }

        stream.Position = 0;
        using var nuspecStream = nuspecEntry.Open();
        if (nuspecEntry.Length != metadataFile.Length
            || !SHA256.HashData(stream).SequenceEqual(
                SHA256.HashData(nuspecStream)))
        {
            throw new InvalidDataException(
                "The full update package nuspec differs from the bundled metadata.");
        }

        var updater = Path.Combine(
            applicationPath,
            "Contents",
            "MacOS",
            "UpdateMac");
        using var updaterStream = RegularPackageFileReader.Open(updater, out var updaterFile);
        if (updaterFile.Length <= 0)
        {
            throw new InvalidDataException("The Velopack updater is empty.");
        }
    }

    private static bool ElementEquals(
        XElement parent,
        string name,
        string expected)
    {
        var elements = parent.Elements()
            .Where(element => string.Equals(
                element.Name.LocalName,
                name,
                StringComparison.Ordinal))
            .ToArray();
        return elements.Length == 1
            && string.Equals(elements[0].Value, expected, StringComparison.Ordinal);
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name.Contains('\\')
            || name.StartsWith('/')
            || name.Split('/').Any(component => component is "." or ".."))
        {
            throw new InvalidDataException(
                "The full update package contains an unsafe entry name.");
        }
    }

    private static bool HasExactProperties(
        JsonElement element,
        params string[] expected)
    {
        var names = element.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        return names.Length == expected.Length
            && names.Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool StringPropertyEquals(
        JsonElement element,
        string name,
        string expected) => element.GetProperty(name).ValueKind == JsonValueKind.String
            && string.Equals(
                element.GetProperty(name).GetString(),
                expected,
                StringComparison.Ordinal);

    private sealed record ApplicationEntry(
        string FullPath,
        string RelativePath,
        bool IsLink);
}
