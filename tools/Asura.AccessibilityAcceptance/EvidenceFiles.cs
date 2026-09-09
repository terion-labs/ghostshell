using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asura.AccessibilityAcceptance;

internal sealed record EvidencePaths(
    string Directory,
    string Json,
    string Markdown,
    string Digest);

internal static class EvidenceFiles
{
    private const string JsonFileName = "evidence.json";
    private const string MarkdownFileName = "evidence.md";
    private const string DigestFileName = "evidence.json.sha256";
    private const int MaximumEvidenceDirectoryEntries = 3;
    private const int MaximumEvidenceFileBytes = 1_000_000;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static EvidencePaths Write(string evidenceRoot, AcceptanceEvidence evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRoot);
        ArgumentNullException.ThrowIfNull(evidence);
        var validationErrors = EvidenceValidator.Validate(evidence);
        if (validationErrors.Count > 0)
        {
            throw new InvalidDataException(
                "Refusing to write invalid accessibility evidence: "
                + string.Join("; ", validationErrors));
        }

        var runDirectory = CreateExclusiveRunDirectory(evidenceRoot, evidence);
        var jsonPath = Path.Combine(runDirectory, JsonFileName);
        var markdownPath = Path.Combine(runDirectory, MarkdownFileName);
        var digestPath = Path.Combine(runDirectory, DigestFileName);
        var json = JsonSerializer.SerializeToUtf8Bytes(evidence, SerializerOptions);
        var jsonBytes = new byte[json.Length + 1];
        json.CopyTo(jsonBytes, 0);
        jsonBytes[^1] = (byte)'\n';
        var digest = Convert.ToHexString(SHA256.HashData(jsonBytes)).ToLowerInvariant();
        var suffix = $".tmp-{Guid.NewGuid():N}";
        var temporaryJson = jsonPath + suffix;
        var temporaryMarkdown = markdownPath + suffix;
        var temporaryDigest = digestPath + suffix;

        try
        {
            File.WriteAllText(
                temporaryMarkdown,
                RenderMarkdown(evidence, digest),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(
                temporaryDigest,
                $"{digest}  {JsonFileName}\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllBytes(temporaryJson, jsonBytes);

            File.Move(temporaryMarkdown, markdownPath, overwrite: false);
            File.Move(temporaryDigest, digestPath, overwrite: false);
            // JSON is the completion marker and is therefore published last.
            File.Move(temporaryJson, jsonPath, overwrite: false);
        }
        catch
        {
            TryDelete(temporaryJson);
            TryDelete(temporaryMarkdown);
            TryDelete(temporaryDigest);
            throw;
        }

        return new EvidencePaths(runDirectory, jsonPath, markdownPath, digestPath);
    }

    internal static bool IsSameOrDescendantPath(string candidatePath, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var candidate = NormalizeForFilesystemComparison(ResolveExistingLinks(candidatePath));
        var root = NormalizeForFilesystemComparison(ResolveExistingLinks(rootPath));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(candidate, root, comparison))
        {
            return true;
        }

        var rootBoundary = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootBoundary, comparison);
    }

    public static IReadOnlyList<string> Validate(string jsonOrDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonOrDirectoryPath);
        var jsonPath = Directory.Exists(jsonOrDirectoryPath)
            ? Path.Combine(jsonOrDirectoryPath, JsonFileName)
            : Path.GetFullPath(jsonOrDirectoryPath);
        var directory = Path.GetDirectoryName(jsonPath)
            ?? throw new InvalidOperationException("Evidence JSON has no parent directory.");
        var markdownPath = Path.Combine(directory, MarkdownFileName);
        var digestPath = Path.Combine(directory, DigestFileName);
        var errors = new List<string>();
        if (!string.Equals(Path.GetFileName(jsonPath), JsonFileName, StringComparison.Ordinal))
        {
            errors.Add($"Evidence JSON must be named {JsonFileName}.");
        }

        if (!Directory.Exists(directory) || !File.Exists(jsonPath))
        {
            return [$"Evidence JSON does not exist: {jsonPath}"];
        }

        var fileNames = Directory
            .EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
            .Take(MaximumEvidenceDirectoryEntries + 1)
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedFiles = new[] { MarkdownFileName, JsonFileName, DigestFileName }
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!fileNames.SequenceEqual(expectedFiles, StringComparer.Ordinal))
        {
            errors.Add("Evidence directory must contain exactly the three published evidence files.");
        }

        var jsonBytes = ReadBoundedEvidenceFile(jsonPath, "Evidence JSON", errors);
        if (jsonBytes is null)
        {
            return errors;
        }

        AcceptanceEvidence? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<AcceptanceEvidence>(
                jsonBytes,
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            return [$"Evidence JSON is invalid: {exception.Message}"];
        }

        if (evidence is null)
        {
            return ["Evidence JSON contains null."];
        }

        errors.AddRange(EvidenceValidator.Validate(evidence));
        if (!File.Exists(digestPath))
        {
            errors.Add($"Digest sidecar is missing: {digestPath}");
            return errors;
        }

        var digestBytes = ReadBoundedEvidenceFile(digestPath, "Evidence digest", errors);
        if (digestBytes is null)
        {
            return errors;
        }

        var digestFields = Encoding.UTF8.GetString(digestBytes).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var expectedDigest = digestFields.FirstOrDefault() ?? string.Empty;
        if (digestFields.Length != 2
            || !string.Equals(digestFields[1], JsonFileName, StringComparison.Ordinal)
            || !EvidenceValidator.IsLowercaseSha256(expectedDigest))
        {
            errors.Add("Evidence digest sidecar has an invalid format.");
        }

        var actualDigest = Convert.ToHexString(SHA256.HashData(jsonBytes)).ToLowerInvariant();
        if (!string.Equals(expectedDigest, actualDigest, StringComparison.Ordinal))
        {
            errors.Add("Evidence JSON does not match evidence.json.sha256.");
        }

        var markdownBytes = ReadBoundedEvidenceFile(
            markdownPath,
            "Human-readable evidence",
            errors);
        if (markdownBytes is null)
        {
            return errors;
        }

        if (!string.Equals(
                Encoding.UTF8.GetString(markdownBytes),
                RenderMarkdown(evidence, actualDigest),
                StringComparison.Ordinal))
        {
            errors.Add("evidence.md does not match the validated JSON evidence.");
        }

        return errors;
    }

    internal static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Default,
            AllowDuplicateProperties = false,
            AllowTrailingCommas = false,
            MaxDepth = 64,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(
            new JsonStringEnumConverter<AcceptanceStatus>(JsonNamingPolicy.SnakeCaseUpper, false));
        options.Converters.Add(new JsonStringEnumConverter<TargetPlatform>(null, false));
        options.Converters.Add(new JsonStringEnumConverter<ScreenReaderKind>(null, false));
        return options;
    }

    internal static string RenderMarkdown(AcceptanceEvidence evidence, string digest)
    {
        var lines = new List<string>
        {
            "# Asura M1 accessibility acceptance",
            string.Empty,
            $"**Result: {FormatStatus(evidence.OverallResult)}**",
            string.Empty,
            $"- Platform: {evidence.Platform}",
            $"- Screen reader: {evidence.ScreenReader} ({EscapeMarkdown(evidence.AssistiveTechnology.Version)})",
            $"- Declared system: `{EscapeMarkdown(evidence.Host.DeclaredSystemName)}`",
            $"- Host fingerprint: `{evidence.Host.HostFingerprint}`",
            $"- Observer: `{EscapeMarkdown(evidence.Host.Observer)}`",
            $"- OS: {EscapeMarkdown(evidence.Host.OsDescription)} ({evidence.Host.OsArchitecture})",
            $"- Desktop session: {EscapeMarkdown(evidence.Host.DesktopSession)}",
            $"- Build label: `{EscapeMarkdown(evidence.Build.BuildLabel)}`",
            $"- Executable SHA-256: `{evidence.Build.ExecutableSha256}`",
            $"- Package manifest SHA-256: `{evidence.Build.PackageManifestSha256}` ({evidence.Build.PackageFileCount} files)",
            $"- Catalog SHA-256: `{evidence.CatalogSha256}`",
            $"- Evidence SHA-256: `{digest}`",
            $"- Started: `{evidence.StartedAtUtc:O}`",
            $"- Completed: `{evidence.CompletedAtUtc:O}`",
            $"- Cleanup: {EscapeMarkdown(evidence.CleanupDisposition)}",
            $"- Preference restoration: {EscapeMarkdown(evidence.PreferenceRestorationDisposition)}",
            string.Empty,
            "## Evidence boundaries",
            string.Empty,
        };
        lines.AddRange(evidence.Limitations.Select(item => $"- {EscapeMarkdown(item)}"));
        if (evidence.Host.EnvironmentWarnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Environment warnings:");
            lines.Add(string.Empty);
            lines.AddRange(evidence.Host.EnvironmentWarnings.Select(item => $"- {EscapeMarkdown(item)}"));
        }

        lines.Add(string.Empty);
        lines.Add("## Observations");
        lines.Add(string.Empty);
        lines.Add("| Check | Result | Mode | Assertions | Sanitized evidence note |");
        lines.Add("| --- | --- | --- | --- | --- |");
        foreach (var check in evidence.Checks)
        {
            var assertions = string.Join(
                ", ",
                check.Assertions.Select(assertion =>
                    $"{assertion.Id}={FormatStatus(assertion.Result)}"));
            lines.Add(
                $"| {EscapeMarkdown(check.Title)} | {FormatStatus(check.Result)} | "
                + $"{EscapeMarkdown(check.ObservationMode)} | {EscapeMarkdown(assertions)} | "
                + $"{EscapeMarkdown(check.Notes)} |");
        }

        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    private static string CreateExclusiveRunDirectory(
        string evidenceRoot,
        AcceptanceEvidence evidence)
    {
        var root = Path.GetFullPath(evidenceRoot);
        Directory.CreateDirectory(root);
        var platform = evidence.Platform switch
        {
            TargetPlatform.MacOS => "macos",
            TargetPlatform.Windows => "windows",
            TargetPlatform.LinuxX11 => "linux-x11",
            _ => throw new InvalidOperationException(
                "The evidence platform is not supported."),
        };

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            var name = $"{evidence.StartedAtUtc:yyyyMMddTHHmmssfffZ}-{platform}-"
                + $"{EvidenceSanitizer.SanitizeIdentifier(evidence.Host.DeclaredSystemName)}-"
                + $"{evidence.Build.PackageManifestSha256[..12]}-{nonce}";
            var reservation = Path.Combine(root, $".{name}.reserve");
            try
            {
                using (new FileStream(
                    reservation,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                }

                var directory = Path.Combine(root, name);
                if (Directory.Exists(directory))
                {
                    File.Delete(reservation);
                    continue;
                }

                Directory.CreateDirectory(directory);
                File.Delete(reservation);
                return directory;
            }
            catch (IOException)
            {
                TryDelete(reservation);
            }
        }

        throw new IOException("Could not reserve a unique accessibility evidence directory.");
    }

    private static string ResolveExistingLinks(string path) =>
        ResolveExistingLinks(path, linkDepth: 0);

    private static string ResolveExistingLinks(string path, int linkDepth)
    {
        if (linkDepth > 64)
        {
            throw new IOException("The path contains too many symbolic-link indirections.");
        }

        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The path has no filesystem root.");
        var segments = fullPath[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = pathRoot;
        for (var index = 0; index < segments.Length; index++)
        {
            var next = Path.Combine(current, segments[index]);
            FileSystemInfo? entry = Directory.Exists(next)
                ? new DirectoryInfo(next)
                : File.Exists(next)
                    ? new FileInfo(next)
                    : null;
            if (entry is null)
            {
                for (; index < segments.Length; index++)
                {
                    current = Path.Combine(current, segments[index]);
                }

                break;
            }

            if (entry.LinkTarget is not null
                || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var target = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException(
                        "The path contains an unresolved symbolic link or reparse point.");
                // Resolve aliases in the target's ancestors as well. On macOS, for
                // example, a link target can be expressed through /var while the
                // compared package root canonicalizes that ancestor to /private/var.
                current = ResolveExistingLinks(target, linkDepth + 1);
            }
            else
            {
                current = next;
            }
        }

        return Path.GetFullPath(current);
    }

    private static string NormalizeForFilesystemComparison(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsMacOS()
            ? trimmed.Normalize(NormalizationForm.FormC)
            : trimmed;
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("`", "'", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string FormatStatus(AcceptanceStatus status) =>
        status.ToString().ToUpperInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the original publication failure; a temporary file cannot form valid evidence.
        }
    }

    private static byte[]? ReadBoundedEvidenceFile(
        string path,
        string label,
        List<string> errors)
    {
        if (!File.Exists(path))
        {
            errors.Add($"{label} is missing: {path}");
            return null;
        }

        try
        {
            using var stream = OpenEvidenceFile(path);
            if (stream.Length < 0 || stream.Length > MaximumEvidenceFileBytes)
            {
                errors.Add(
                    $"{label} exceeds the {MaximumEvidenceFileBytes}-byte validation limit.");
                return null;
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                errors.Add($"{label} changed while it was being validated.");
                return null;
            }

            return bytes;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or NotSupportedException)
        {
            errors.Add($"{label} is not a bounded regular file: {exception.GetType().Name}.");
            return null;
        }
    }

    private static FileStream OpenEvidenceFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return PackageFingerprint.OpenUnixRegularFile(path, out _);
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory
                | FileAttributes.ReparsePoint
                | FileAttributes.Device)) != FileAttributes.None)
        {
            throw new InvalidDataException("Evidence contains a non-regular file.");
        }

        return File.OpenRead(path);
    }
}

internal static class EvidenceValidator
{
    private static readonly HashSet<string> ObservationModes = new(
        ["operator-observed", "runner-observed-boundary", "operator-observed+runner-boundary"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> ScreenReaderStatuses = new(
        [
            "ACTIVE_VERIFIED",
            "NOT_RECHECKED",
            "PLATFORM_MISMATCH",
            "NOT_EXACTLY_ONE_RUNNING",
            "IDENTITY_UNVERIFIED",
            "VERSION_UNAVAILABLE",
            "ACCESSIBILITY_BUS_UNAVAILABLE",
            "UNSUPPORTED_MAPPING",
            "PROBE_FAILED",
        ],
        StringComparer.Ordinal);
    private static readonly HashSet<string> AccessibilityBusStatuses = new(
        [
            "NATIVE_PLATFORM_ACCESSIBILITY",
            "AT_SPI_SESSION_BUS_PRESENT",
            "AT_SPI_SESSION_BUS_UNAVAILABLE",
            "UNAVAILABLE",
        ],
        StringComparer.Ordinal);

    public static IReadOnlyList<string> Validate(AcceptanceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var errors = new List<string>();
        if (evidence.SchemaVersion != AcceptanceEvidence.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported schema version {evidence.SchemaVersion}.");
        }

        if (!string.Equals(evidence.EvidenceKind, AcceptanceEvidence.CurrentEvidenceKind
, StringComparison.Ordinal) || !string.Equals(evidence.RunnerVersion, AcceptanceEvidence.CurrentRunnerVersion
, StringComparison.Ordinal) || !string.Equals(evidence.CatalogVersion, AcceptanceEvidence.CurrentCatalogVersion
, StringComparison.Ordinal) || !string.Equals(evidence.CatalogSha256, AcceptanceCatalog.Digest, StringComparison.Ordinal))
        {
            errors.Add("Evidence or catalog identity does not match this runner.");
        }

        if (evidence.ScreenReader != AcceptanceEvidence.ScreenReaderFor(evidence.Platform))
        {
            errors.Add("Platform and screen-reader mapping is invalid.");
        }

        if (evidence.CompletedAtUtc < evidence.StartedAtUtc)
        {
            errors.Add("Completion time precedes start time.");
        }

        if (!evidence.Limitations.SequenceEqual(
                AcceptanceEvidence.StandardLimitations,
                StringComparer.Ordinal))
        {
            errors.Add("Evidence boundaries do not match this runner version.");
        }

        ValidateHost(evidence, errors);
        ValidateAssistiveTechnology(evidence, errors);
        ValidateBuild(evidence, errors);
        ValidateChecks(evidence, errors);
        ValidateCrossFieldRules(evidence, errors);
        return errors;
    }

    internal static bool IsLowercaseSha256(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateHost(AcceptanceEvidence evidence, List<string> errors)
    {
        var host = evidence.Host;
        if (!EvidenceSanitizer.IsValidIdentifier(host.DeclaredSystemName)
            || !EvidenceSanitizer.IsHostFingerprint(host.HostFingerprint)
            || !EvidenceSanitizer.IsValidIdentifier(host.Observer))
        {
            errors.Add("Host or observer identifier is invalid.");
        }

        if (!EvidenceSanitizer.IsBoundedSingleLine(host.OsDescription, 3, 256)
            || !EvidenceSanitizer.IsBoundedSingleLine(host.OsArchitecture, 3, 32)
            || !EvidenceSanitizer.IsBoundedSingleLine(host.ProcessArchitecture, 3, 32)
            || !EvidenceSanitizer.IsBoundedSingleLine(host.DesktopSession, 3, 256))
        {
            errors.Add("Host metadata is unsafe or outside its bounds.");
        }

        if (!host.EnvironmentWarnings.SequenceEqual(
                HostEnvironmentProbe.DescribeBlockers(host.EnvironmentSignals),
                StringComparer.Ordinal))
        {
            errors.Add("Environment warnings do not match captured host signals.");
        }

        if (evidence.OverallResult == AcceptanceStatus.Pass
            && (!host.InteractiveUser || host.EnvironmentSignals.BlocksNamedHostAcceptance))
        {
            errors.Add("PASS requires an unblocked interactive named host.");
        }
    }

    private static void ValidateAssistiveTechnology(
        AcceptanceEvidence evidence,
        List<string> errors)
    {
        var technology = evidence.AssistiveTechnology;
        if (technology.Kind != evidence.ScreenReader
            || !EvidenceSanitizer.IsSafeSingleLine(technology.Product, 3, 128)
            || !EvidenceSanitizer.IsSafeVersionText(technology.Version)
            || !EvidenceSanitizer.IsSafeSingleLine(technology.IdentitySource, 3, 256)
            || !ScreenReaderStatuses.Contains(technology.StatusBefore)
            || !ScreenReaderStatuses.Contains(technology.StatusAfter)
            || !AccessibilityBusStatuses.Contains(technology.AccessibilityBusStatus))
        {
            errors.Add("Assistive-technology identity is invalid.");
        }


        var expectedVerifiedIdentity = evidence.ScreenReader switch
        {
            ScreenReaderKind.VoiceOver => (
                "Apple VoiceOver",
                "running system application with bundle identifier com.apple.VoiceOver",
                "NATIVE_PLATFORM_ACCESSIBILITY"),
            ScreenReaderKind.Narrator => (
                "Microsoft Narrator",
                "running executable verified as Windows System32 Narrator.exe",
                "NATIVE_PLATFORM_ACCESSIBILITY"),
            ScreenReaderKind.Orca => (
                "GNOME Orca",
                ScreenReaderProbe.OrcaIdentitySource,
                "AT_SPI_SESSION_BUS_PRESENT"),
            _ => (string.Empty, string.Empty, string.Empty),
        };
        if (string.Equals(technology.StatusBefore, "ACTIVE_VERIFIED"
, StringComparison.Ordinal) && (!string.Equals(technology.Product, expectedVerifiedIdentity.Item1
, StringComparison.Ordinal) || !string.Equals(technology.IdentitySource, expectedVerifiedIdentity.Item2
, StringComparison.Ordinal) || !string.Equals(technology.AccessibilityBusStatus, expectedVerifiedIdentity.Item3, StringComparison.Ordinal)))
        {
            errors.Add("Verified screen-reader identity does not match the platform contract.");
        }

        if (evidence.OverallResult == AcceptanceStatus.Pass
            && (!string.Equals(technology.StatusBefore, "ACTIVE_VERIFIED"
, StringComparison.Ordinal) || !string.Equals(technology.StatusAfter, "ACTIVE_VERIFIED", StringComparison.Ordinal)))
        {
            errors.Add("PASS requires the expected screen reader before and after observations.");
        }
    }

    private static void ValidateBuild(AcceptanceEvidence evidence, List<string> errors)
    {
        var build = evidence.Build;
        if (!EvidenceSanitizer.IsValidIdentifier(build.BuildLabel)
            || build.ExecutableLengthBytes <= 0
            || build.PackageFileCount <= 0
            || !EvidenceSanitizer.IsSafeVersionText(build.ProductVersion)
            || !IsLowercaseSha256(build.ExecutableSha256)
            || !IsLowercaseSha256(build.PackageManifestSha256))
        {
            errors.Add("Build identity is invalid.");
        }

        var expected = evidence.Platform switch
        {
            TargetPlatform.MacOS => ("macos-application-bundle", "Asura", "sh.asura"),
            TargetPlatform.Windows => ("windows-package", "Asura.exe", "Asura.exe"),
            TargetPlatform.LinuxX11 => ("linux-x11-package", "Asura", "Asura"),
            _ => (string.Empty, string.Empty, string.Empty),
        };
        if (!string.Equals(build.PackageKind, expected.Item1
, StringComparison.Ordinal) || !string.Equals(build.PackageExecutable, expected.Item2
, StringComparison.Ordinal) || !string.Equals(build.ApplicationIdentity, expected.Item3, StringComparison.Ordinal))
        {
            errors.Add("Build identity does not match the target platform.");
        }
    }

    private static void ValidateChecks(AcceptanceEvidence evidence, List<string> errors)
    {
        if (evidence.Checks.Count != AcceptanceCatalog.All.Count)
        {
            errors.Add(
                $"Expected {AcceptanceCatalog.All.Count} checks, found {evidence.Checks.Count}.");
        }

        for (var index = 0; index < Math.Min(evidence.Checks.Count, AcceptanceCatalog.All.Count); index++)
        {
            var actual = evidence.Checks[index];
            var expected = AcceptanceCatalog.All[index];
            if (!string.Equals(actual.Id, expected.Id, StringComparison.Ordinal) || !string.Equals(actual.Title, expected.Title, StringComparison.Ordinal))
            {
                errors.Add($"Check {index + 1} does not match catalog entry {expected.Id}.");
            }

            if (!ObservationModes.Contains(actual.ObservationMode))
            {
                errors.Add($"Check {actual.Id} has an invalid observation mode.");
            }

            if (actual.Result == AcceptanceStatus.Pass
                && !string.Equals(actual.ObservationMode, PassingObservationMode(index), StringComparison.Ordinal))
            {
                errors.Add(
                    $"Passing check {actual.Id} does not have its required observation mode.");
            }

            if (!EvidenceSanitizer.IsSanitizedNote(actual.Notes))
            {
                errors.Add($"Check {actual.Id} contains an unsafe or invalid evidence note.");
            }

            if (actual.RedactionsApplied is < 0 or > 10_000)
            {
                errors.Add($"Check {actual.Id} has an invalid redaction count.");
            }

            if (actual.ObservedAtUtc < evidence.StartedAtUtc
                || actual.ObservedAtUtc > evidence.CompletedAtUtc)
            {
                errors.Add($"Check {actual.Id} has an out-of-range observation time.");
            }


            if (index > 0
                && actual.ObservedAtUtc < evidence.Checks[index - 1].ObservedAtUtc)
            {
                errors.Add($"Check {actual.Id} precedes the prior catalog observation.");
            }

            if (actual.Assertions.Count != expected.Assertions.Count)
            {
                errors.Add($"Check {actual.Id} has an incomplete assertion matrix.");
            }

            for (var assertionIndex = 0;
                 assertionIndex < Math.Min(actual.Assertions.Count, expected.Assertions.Count);
                 assertionIndex++)
            {
                if (!string.Equals(actual.Assertions[assertionIndex].Id, expected.Assertions[assertionIndex].Id, StringComparison.Ordinal))
                {
                    errors.Add(
                        $"Check {actual.Id} assertion {assertionIndex + 1} does not match the catalog.");
                }
            }

            if (actual.Result != CheckObservation.ResolveResult(actual.Assertions))
            {
                errors.Add($"Check {actual.Id} result does not match its assertions.");
            }
        }

        if (evidence.OverallResult != AcceptanceEvidence.ResolveOverall(evidence.Checks))
        {
            errors.Add("Overall result does not match the fixed check matrix.");
        }
    }

    private static void ValidateCrossFieldRules(
        AcceptanceEvidence evidence,
        List<string> errors)
    {
        if (!EvidenceSanitizer.IsSanitizedNote(evidence.CleanupDisposition)
            || !EvidenceSanitizer.IsSanitizedNote(evidence.PreferenceRestorationDisposition))
        {
            errors.Add("Cleanup or restoration disposition is not safely bounded.");
        }

        if (evidence.Checks.Count != AcceptanceCatalog.All.Count)
        {
            return;
        }

        var packageCheck = evidence.Checks[2];
        var finalCheck = evidence.Checks[^1];
        var packageUnchanged = AssertionResult(packageCheck, "package-remained-unchanged");
        var preferencesRestored = AssertionResult(finalCheck, "preferences-restored");
        var packageExited = AssertionResult(finalCheck, "package-exited");
        var readerActive = AssertionResult(finalCheck, "screen-reader-remained-active");
        if (packageUnchanged is null
            || preferencesRestored is null
            || packageExited is null
            || readerActive is null)
        {
            errors.Add("Lifecycle or restoration assertions are missing from the fixed matrix.");
            return;
        }

        var expectedRestoration = preferencesRestored switch
        {
            AcceptanceStatus.Pass => AcceptanceEvidence.PreferencesRestoredDisposition,
            AcceptanceStatus.Fail => AcceptanceEvidence.PreferencesNotRestoredDisposition,
            _ => AcceptanceEvidence.PreferencesUnconfirmedDisposition,
        };
        if (!string.Equals(evidence.PreferenceRestorationDisposition, expectedRestoration, StringComparison.Ordinal))
        {
            errors.Add("Preference-restoration disposition does not match the final assertion.");
        }

        if (packageExited == AcceptanceStatus.Pass
            && !string.Equals(evidence.CleanupDisposition, AcceptanceEvidence.CleanExitDisposition, StringComparison.Ordinal))
        {
            errors.Add("A passing package-exit assertion requires runner-confirmed clean exit.");
        }

        if (readerActive == AcceptanceStatus.Pass
            && (!string.Equals(evidence.AssistiveTechnology.StatusBefore, "ACTIVE_VERIFIED"
, StringComparison.Ordinal) || !string.Equals(evidence.AssistiveTechnology.StatusAfter, "ACTIVE_VERIFIED", StringComparison.Ordinal)))
        {
            errors.Add(
                "A passing screen-reader-active assertion requires verified reader identity before and after observations.");
        }

        if (evidence.OverallResult == AcceptanceStatus.Pass
            && (packageUnchanged != AcceptanceStatus.Pass
                || preferencesRestored != AcceptanceStatus.Pass
                || packageExited != AcceptanceStatus.Pass
                || readerActive != AcceptanceStatus.Pass))
        {
            errors.Add("PASS lacks package, screen-reader, lifecycle, or restoration evidence.");
        }
    }

    private static AcceptanceStatus? AssertionResult(CheckObservation check, string id) =>
        check.Assertions.FirstOrDefault(assertion => string.Equals(assertion.Id, id, StringComparison.Ordinal))?.Result;

    private static string PassingObservationMode(int checkIndex) => checkIndex switch
    {
        0 or >= 3 and <= 10 => "operator-observed",
        1 or 11 => "operator-observed+runner-boundary",
        2 => "runner-observed-boundary",
        _ => throw new ArgumentOutOfRangeException(nameof(checkIndex)),
    };
}
