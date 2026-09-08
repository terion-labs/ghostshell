using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace GhostShell.Packaging;

/// <summary>
/// Validates the small libghostty-vt-only receipt used by the managed terminal.
/// The former macOS renderer receipt describes a materially different Metal,
/// AppKit, font, theme, and shell-resource closure, so reusing it here would
/// make the package evidence misleading rather than more rigorous.
/// </summary>
internal static class NativeTerminalPackageProvenance
{
    private const int MaximumDocumentBytes = 1024 * 1024;
    private const string CatalogFormat =
        "ghostshell-native-terminal-component-catalog-v1";
    private const string ReceiptFormat =
        "ghostshell-native-terminal-build-receipt-v1";
    private const string CatalogFileName = "native-terminal-components.json";
    private const string ReceiptFileName = "native-terminal-build-receipt.json";
    private const string ExpectedTargetRid = "osx-arm64";
    private const string LibraryFileName = "libghostty-vt.dylib";
    private const string LicenseFileName = "GHOSTTY-LICENSE";
    private const string RequiredExportsFileName =
        "ghostty-vt-required-exports.txt";
    private const string ExtensionAbiExport =
        "ghostty_ghostshell_extension_abi";
    private const string ShellIntegrationDirectory = "ghostty/shell-integration";
    private const string ShellIntegrationManifest =
        "ghostty/shell-integration/MANIFEST.sha256";
    private static readonly HashSet<string> ShellIntegrationFiles =
    [
        "SHELL-INTEGRATION-NOTICE.md",
        "bash/bash-preexec.sh",
        "bash/ghostty.bash",
        "fish/vendor_conf.d/ghostty-shell-integration.fish",
        "zsh/.zshenv",
        "zsh/ghostty-integration",
    ];

    public static bool IsCatalog(string path)
    {
        var bytes = ReadDocument(path, "native terminal component catalog");
        using var document = Parse(bytes, "native terminal component catalog");
        return document.RootElement.TryGetProperty("format", out var format)
            && format.ValueKind == JsonValueKind.String
            && string.Equals(format.GetString(), CatalogFormat, StringComparison.Ordinal);
    }

    public static void Validate(
        string executableDirectory,
        string resourceDirectory,
        string nativeMetadataDirectory,
        string licenseDirectory,
        string catalogPath,
        string receiptPath)
    {
        ValidateCore(
            executableDirectory,
            resourceDirectory,
            nativeMetadataDirectory,
            licenseDirectory,
            catalogPath,
            receiptPath,
            requireExactLibrary: true);
    }

    /// <summary>
    /// Validates receipt-bound executable content after macOS code signing.
    /// The build receipt includes the digest after Apple's signature removal,
    /// so a new signature can change without admitting different program bytes.
    /// The release verifier also checks the package's deep signature identity.
    /// </summary>
    public static void ValidateAfterCodeSigning(
        string executableDirectory,
        string resourceDirectory,
        string nativeMetadataDirectory,
        string licenseDirectory,
        string catalogPath,
        string receiptPath)
    {
        ValidateCore(
            executableDirectory,
            resourceDirectory,
            nativeMetadataDirectory,
            licenseDirectory,
            catalogPath,
            receiptPath,
            requireExactLibrary: false);
    }

    private static void ValidateCore(
        string executableDirectory,
        string resourceDirectory,
        string nativeMetadataDirectory,
        string licenseDirectory,
        string catalogPath,
        string receiptPath,
        bool requireExactLibrary)
    {
        var catalogBytes = ReadDocument(
            catalogPath,
            "native terminal component catalog");
        var receiptBytes = ReadDocument(receiptPath, "native terminal build receipt");
        using var catalog = Parse(catalogBytes, "native terminal component catalog");
        using var receipt = Parse(receiptBytes, "native terminal build receipt");

        RequireFormat(catalog.RootElement, CatalogFormat, "component catalog");
        RequireFormat(receipt.RootElement, ReceiptFormat, "build receipt");

        var nativeLicenseDirectory = Path.Combine(licenseDirectory, "Native");
        RequireExactCopy(
            catalogBytes,
            Path.Combine(nativeLicenseDirectory, CatalogFileName),
            "native terminal component catalog");
        RequireExactCopy(
            receiptBytes,
            Path.Combine(nativeLicenseDirectory, ReceiptFileName),
            "native terminal build receipt");

        var catalogSha = Convert.ToHexStringLower(SHA256.HashData(catalogBytes));
        RequireString(receipt.RootElement, "catalogSha256", catalogSha);
        RequireString(receipt.RootElement, "targetRid", ExpectedTargetRid);

        var catalogComponent = RequireObject(catalog.RootElement, "component");
        var receiptSource = RequireObject(receipt.RootElement, "source");
        RequireString(
            receiptSource,
            "commit",
            RequireString(catalogComponent, "sourceCommit"));

        var artifact = RequireObject(receipt.RootElement, "artifact");
        RequireString(artifact, "path", LibraryFileName);
        // Fail before signing when an old receipt lacks the identity needed by
        // post-sign verification, rather than discovering that after notarizing.
        var contentSha256 = RequireString(artifact, "signatureRemovedSha256");
        if (contentSha256.Length != 64
            || contentSha256.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException("The native terminal receipt has an invalid signature-independent SHA-256.");
        }
        var libraryPath = Path.Combine(executableDirectory, LibraryFileName);
        if (requireExactLibrary)
        {
            ValidateFile(
                libraryPath,
                artifact,
                "packaged libghostty-vt");
        }
        else
        {
            ValidateSignedLibraryContent(libraryPath, artifact);
        }

        var build = RequireObject(receipt.RootElement, "build");
        ValidatePty(executableDirectory, licenseDirectory, catalog.RootElement, receipt.RootElement, requireExactLibrary);
        RequireBoolean(build, "testsPassed", expected: true);

        var abi = RequireObject(receipt.RootElement, "abi");
        if (RequireInt64(abi, "ghostShellExtension") != 1)
        {
            throw new InvalidDataException(
                "The native terminal receipt has an incompatible GhostSHELL extension ABI.");
        }
        RequireString(
            abi,
            "ghostShellExtensionExport",
            ExtensionAbiExport);
        RequireString(abi, "requiredExportsPath", RequiredExportsFileName);
        var requiredExportsPath = Path.Combine(
            nativeMetadataDirectory,
            RequiredExportsFileName);
        ValidateFile(
            requiredExportsPath,
            RequireInt64(abi, "requiredExportsBytes"),
            RequireString(abi, "requiredExportsSha256"),
            "packaged libghostty-vt export manifest");
        ValidateRequiredExports(
            requiredExportsPath,
            RequireInt64(abi, "requiredExportsCount"));

        var license = RequireObject(receipt.RootElement, "license");
        RequireString(license, "path", LicenseFileName);
        ValidateFile(
            Path.Combine(licenseDirectory, LicenseFileName),
            license,
            "packaged Ghostty license");

        var shellIntegration = RequireObject(
            receipt.RootElement,
            "shellIntegration");
        RequireString(
            shellIntegration,
            "directory",
            ShellIntegrationDirectory);
        RequireString(
            shellIntegration,
            "manifestPath",
            ShellIntegrationManifest);
        var manifestPath = Path.Combine(
            resourceDirectory,
            ShellIntegrationManifest.Replace('/', Path.DirectorySeparatorChar));
        ValidateFile(
            manifestPath,
            RequireInt64(shellIntegration, "manifestBytes"),
            RequireString(shellIntegration, "manifestSha256"),
            "packaged shell-integration manifest");
        if (RequireInt64(shellIntegration, "fileCount") != ShellIntegrationFiles.Count)
        {
            throw new InvalidDataException(
                "The shell-integration receipt has an unexpected file count.");
        }

        ValidateShellIntegrationFiles(resourceDirectory, manifestPath);

        foreach (var retiredFile in new[]
                 {
                     "libghostshell-ghostty.dylib",
                     "libghostty.dylib",
                 })
        {
            if (File.Exists(Path.Combine(executableDirectory, retiredFile)))
            {
                throw new InvalidDataException(
                    $"The libghostty-vt package still contains retired runtime {retiredFile}.");
            }
        }
    }

    private static void ValidatePty(string executableDirectory, string licenseDirectory,
        JsonElement catalog, JsonElement receipt, bool requireExactLibrary)
    {
        var expected = RequireObject(catalog, "pty");
        var pty = RequireObject(receipt, "pty");
        foreach (var name in new[] { "sourceCommit", "sourceSha256", "patchSha256", "boundarySha256" })
        {
            RequireString(pty, name, RequireString(expected, name));
        }
        if (RequireInt64(pty, "abi") != 1)
        {
            throw new InvalidDataException("The PTY descriptor boundary ABI is incompatible.");
        }
        var artifact = RequireObject(pty, "artifact");
        RequireString(artifact, "path", "libghostshell_pty.dylib");
        var contentHash = RequireString(artifact, "signatureRemovedSha256");
        if (contentHash.Length != 64 || contentHash.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException("The PTY receipt has an invalid signature-independent SHA-256.");
        }
        var path = Path.Combine(executableDirectory, "libghostshell_pty.dylib");
        if (requireExactLibrary)
        {
            ValidateFile(path, artifact, "packaged descriptor-safe PTY shim");
        }
        else
        {
            ValidateSignedLibraryContent(path, artifact);
        }
        var license = RequireObject(pty, "license");
        RequireString(license, "path", "PORTA-PTY-LICENSE");
        ValidateFile(Path.Combine(licenseDirectory, "PORTA-PTY-LICENSE"), license, "packaged PTY license");
    }

    private static void ValidateSignedLibraryContent(
        string path,
        JsonElement expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length == 0)
        {
            throw new InvalidDataException(
                "The signed packaged libghostty-vt is missing, linked, or empty.");
        }

        if (RequireInt64(expected, "bytes") == 0)
        {
            throw new InvalidDataException(
                "The native terminal build receipt has an empty library artifact.");
        }

        var sha256 = RequireString(expected, "sha256");
        if (sha256.Length != 64
            || sha256.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "The native terminal build receipt has an invalid library SHA-256.");
        }

        RequireString(
            expected,
            "signatureRemovedSha256",
            ComputeSignatureRemovedSha256(path));
    }

    /// <summary>
    /// Hashes a private copy after Apple's codesign removes its signature.
    /// Never mutates the build input or signed package. macOS-only by design.
    /// </summary>
    public static string ComputeSignatureRemovedSha256(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Mach-O signature normalization requires macOS.");
        }

        var source = new FileInfo(path);
        if (!source.Exists || source.LinkTarget is not null || source.Length == 0)
        {
            throw new InvalidDataException("The Mach-O identity input must be a nonempty regular file.");
        }

        var temporary = Directory.CreateTempSubdirectory("ghostshell-native-identity-");
        try
        {
            var copy = Path.Combine(temporary.FullName, "native-library");
            File.Copy(source.FullName, copy);
            var start = new ProcessStartInfo("/usr/bin/codesign")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("--remove-signature");
            start.ArgumentList.Add(copy);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start Mach-O signature normalization.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new InvalidDataException("Mach-O signature normalization timed out.");
            }
            _ = output.GetAwaiter().GetResult();
            _ = error.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("Apple codesign rejected Mach-O signature normalization.");
            }

            using var stream = File.OpenRead(copy);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static void ValidateFile(
        string path,
        JsonElement expected,
        string label)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null)
        {
            throw new InvalidDataException($"The {label} is missing or is not a regular file.");
        }

        var expectedBytes = RequireInt64(expected, "bytes");
        if (info.Length != expectedBytes)
        {
            throw new InvalidDataException($"The {label} length differs from its build receipt.");
        }

        using var stream = File.OpenRead(path);
        var actualSha = Convert.ToHexStringLower(SHA256.HashData(stream));
        RequireString(expected, "sha256", actualSha);
    }

    private static void ValidateFile(
        string path,
        long expectedBytes,
        string expectedSha256,
        string label)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"The {label} is missing, linked, or differs in length.");
        }

        using var stream = File.OpenRead(path);
        var actualSha = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actualSha, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The {label} differs from its build receipt.");
        }
    }

    private static void ValidateShellIntegrationFiles(
        string executableDirectory,
        string manifestPath)
    {
        var manifest = File.ReadAllLines(manifestPath);
        if (manifest.Length != ShellIntegrationFiles.Count)
        {
            throw new InvalidDataException(
                "The shell-integration manifest has an unexpected entry count.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in manifest)
        {
            if (line.Length < 67 || !string.Equals(line[64..66], "  ", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The shell-integration manifest has an invalid entry.");
            }

            var sha256 = line[..64];
            var relativePath = line[66..];
            if (!sha256.All(character => character is >= '0' and <= '9'
                    or >= 'a' and <= 'f')
                || !ShellIntegrationFiles.Contains(relativePath)
                || !seen.Add(relativePath))
            {
                throw new InvalidDataException(
                    "The shell-integration manifest has an unexpected entry.");
            }

            var path = Path.Combine(
                executableDirectory,
                ShellIntegrationDirectory.Replace('/', Path.DirectorySeparatorChar),
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null)
            {
                throw new InvalidDataException(
                    $"The packaged shell-integration resource {relativePath} is unavailable.");
            }

            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(actual, sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The packaged shell-integration resource {relativePath} differs from its manifest.");
            }
        }

        if (!seen.SetEquals(ShellIntegrationFiles))
        {
            throw new InvalidDataException(
                "The shell-integration manifest does not cover the expected resource set.");
        }
    }

    private static void ValidateRequiredExports(
        string manifestPath,
        long expectedCount)
    {
        var exports = File.ReadAllLines(manifestPath);
        if (exports.LongLength != expectedCount
            || exports.Length == 0
            || !exports.Contains(ExtensionAbiExport, StringComparer.Ordinal)
            || exports.Any(static export =>
                export.Length <= "ghostty_".Length
                || !export.StartsWith("ghostty_", StringComparison.Ordinal)
                || export.Any(static character =>
                    character is not (>= 'a' and <= 'z'
                        or >= '0' and <= '9'
                        or '_')))
            || !exports.SequenceEqual(
                exports.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The packaged libghostty-vt export manifest is invalid, unsorted, or incomplete.");
        }
    }

    private static void RequireExactCopy(
        byte[] expected,
        string copiedPath,
        string label)
    {
        var copied = ReadDocument(copiedPath, label);
        if (!expected.AsSpan().SequenceEqual(copied))
        {
            throw new InvalidDataException(
                $"The packaged {label} differs from its reviewed source.");
        }
    }

    private static byte[] ReadDocument(string path, string label)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null
            || info.Length < 2 || info.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"The {label} is missing, linked, empty, or exceeds its size bound.");
        }

        return File.ReadAllBytes(path);
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
                    MaxDepth = 12,
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {label} is not valid JSON.", exception);
        }
    }

    private static void RequireFormat(
        JsonElement root,
        string expected,
        string label)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The native terminal {label} must be an object.");
        }

        RequireString(root, "format", expected);
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"The native terminal evidence requires object {name}.");
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
                $"The native terminal evidence requires string {name}.");
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
                $"The native terminal evidence has an unexpected {name} value.");
        }
    }

    private static long RequireInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result)
            || result < 0)
        {
            throw new InvalidDataException(
                $"The native terminal evidence requires non-negative integer {name}.");
        }

        return result;
    }

    private static void RequireBoolean(
        JsonElement parent,
        string name,
        bool expected)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || value.GetBoolean() != expected)
        {
            throw new InvalidDataException(
                $"The native terminal evidence has an unexpected {name} value.");
        }
    }
}
