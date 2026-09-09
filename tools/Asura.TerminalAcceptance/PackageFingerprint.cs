using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asura.TerminalAcceptance;

internal sealed record PackageInspection(
    string ExecutablePath,
    BuildIdentity Build,
    BackendIdentity Backend);

internal static class PackageFingerprint
{
    private const string PortaPtyLibraryPrefix = "Porta.Pty/";
    private const string NativeTerminalCatalogFileName = "native-terminal-components.json";
    private const string GhosttyVtIdentityPrefix = "libghostty-vt/";
    internal const string IdentitySourceDescription =
        "Package dependency and native-terminal manifests plus target platform; live PTY behavior requires the packaged-real-pty-backend observation.";

    public static PackageInspection Inspect(
        string packagePath,
        TargetPlatform platform,
        string buildLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildLabel);

        var executablePath = ResolveExecutable(packagePath, platform);
        var packageDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("The package executable has no parent directory.");
        var dependenciesPath = Path.Combine(packageDirectory, "Asura.deps.json");
        if (!File.Exists(dependenciesPath))
        {
            throw new FileNotFoundException(
                "The package does not contain Asura.deps.json, so its terminal backend cannot be identified.",
                dependenciesPath);
        }

        var portaPtyVersion = ReadPortaPtyVersion(dependenciesPath);
        var ghosttyVtVersion = ReadGhosttyVtVersion(packageDirectory, platform);
        var executableInfo = new FileInfo(executablePath);
        var executableSha256 = HashFile(executablePath);
        var (packageFileCount, packageManifestSha256) = HashPackage(packageDirectory);
        var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        var productVersion = versionInfo.ProductVersion
            ?? versionInfo.FileVersion
            ?? "unversioned";
        var build = new BuildIdentity(
            buildLabel,
            Path.GetFileName(executablePath),
            EvidenceSanitizer.SanitizeSingleLine(productVersion).Value,
            executableInfo.Length,
            executableSha256,
            packageFileCount,
            packageManifestSha256);
        var backend = new BackendIdentity(
            $"libghostty-vt {ghosttyVtVersion} state engine with Avalonia managed renderer",
            $"Porta.Pty {portaPtyVersion}",
            PtySubstrateFor(platform),
            IdentitySourceDescription);

        return new PackageInspection(executablePath, build, backend);
    }

    private static string ResolveExecutable(string packagePath, TargetPlatform platform)
    {
        var fullPath = Path.GetFullPath(packagePath);
        var executableName = platform == TargetPlatform.Windows ? "Asura.exe" : "Asura";
        if (File.Exists(fullPath))
        {
            if (!string.Equals(
                    Path.GetFileName(fullPath),
                    executableName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException(
                    $"The package executable must be named {executableName}.",
                    fullPath);
            }

            return fullPath;
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Package path does not exist: {fullPath}");
        }

        var executablePath = Path.Combine(fullPath, executableName);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                $"The package directory does not contain {executableName}.",
                executablePath);
        }

        return executablePath;
    }

    private static string ReadPortaPtyVersion(string dependenciesPath)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(dependenciesPath),
            new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        if (!document.RootElement.TryGetProperty("libraries", out var libraries)
            || libraries.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Asura.deps.json does not contain a libraries object.");
        }

        string? portaPtyVersion = null;
        foreach (var library in libraries.EnumerateObject())
        {
            if (library.Name.StartsWith(PortaPtyLibraryPrefix, StringComparison.Ordinal))
            {
                if (portaPtyVersion is not null)
                {
                    throw new InvalidDataException(
                        "Asura.deps.json identifies more than one Porta.Pty version.");
                }
                portaPtyVersion = library.Name[PortaPtyLibraryPrefix.Length..];
            }
        }

        if (string.IsNullOrWhiteSpace(portaPtyVersion))
        {
            throw new InvalidDataException(
                "Asura.deps.json does not identify Porta.Pty.");
        }

        return ParseDependencyVersion(portaPtyVersion, "Porta.Pty");
    }

    private static string ReadGhosttyVtVersion(
        string packageDirectory,
        TargetPlatform platform)
    {
        var libraryPath = Path.Combine(packageDirectory, GhosttyVtLibraryNameFor(platform));
        if (!File.Exists(libraryPath))
        {
            throw new FileNotFoundException(
                "The package does not contain its platform libghostty-vt runtime.",
                libraryPath);
        }

        var catalogPath = Path.Combine(packageDirectory, NativeTerminalCatalogFileName);
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException(
                "The package does not contain native-terminal-components.json, so libghostty-vt cannot be identified.",
                catalogPath);
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(catalogPath));
        if (!document.RootElement.TryGetProperty("component", out var component)
            || component.ValueKind != JsonValueKind.Object
            || !component.TryGetProperty("identity", out var identityElement)
            || identityElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                "native-terminal-components.json does not identify libghostty-vt.");
        }

        var identity = identityElement.GetString();
        if (identity is null
            || !identity.StartsWith(GhosttyVtIdentityPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "native-terminal-components.json has an unexpected terminal component identity.");
        }

        return ParseDependencyVersion(
            identity[GhosttyVtIdentityPrefix.Length..],
            "libghostty-vt");
    }

    private static string ParseDependencyVersion(string value, string libraryName)
    {
        var sanitized = EvidenceSanitizer.SanitizeIdentifier(value);
        if (!string.Equals(value, sanitized, StringComparison.Ordinal) || value.Length > 64)
        {
            throw new InvalidDataException(
                $"Asura.deps.json contains an invalid {libraryName} version.");
        }

        return value;
    }

    private static (int FileCount, string Digest) HashPackage(string packageDirectory)
    {
        var files = Directory
            .EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(
                path => Path.GetRelativePath(packageDirectory, path),
                StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException("The package directory is empty.");
        }

        using var packageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBuffer = stackalloc byte[sizeof(long)];
        foreach (var file in files)
        {
            var relativePath = Path
                .GetRelativePath(packageDirectory, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            packageHash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            packageHash.AppendData([0]);
            BinaryPrimitives.WriteInt64BigEndian(lengthBuffer, new FileInfo(file).Length);
            packageHash.AppendData(lengthBuffer);
            packageHash.AppendData(Convert.FromHexString(HashFile(file)));
        }

        return (files.Length, Convert.ToHexString(packageHash.GetHashAndReset()).ToLowerInvariant());
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string PtySubstrateFor(TargetPlatform platform) => platform switch
    {
        TargetPlatform.Windows => "Windows ConPTY through Porta.Pty",
        TargetPlatform.LinuxX11 => "Linux Unix PTY through Porta.Pty",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
    };

    private static string GhosttyVtLibraryNameFor(TargetPlatform platform) => platform switch
    {
        TargetPlatform.Windows => "ghostty-vt.dll",
        TargetPlatform.LinuxX11 => "libghostty-vt.so",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
    };
}
