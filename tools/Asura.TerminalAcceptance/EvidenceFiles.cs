using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asura.TerminalAcceptance;

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

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static EvidencePaths Write(string evidenceRoot, AcceptanceEvidence evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRoot);
        ArgumentNullException.ThrowIfNull(evidence);

        var errors = EvidenceValidator.Validate(evidence);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "Refusing to write invalid acceptance evidence: " + string.Join("; ", errors));
        }

        var runDirectory = CreateRunDirectory(evidenceRoot, evidence);
        var jsonPath = Path.Combine(runDirectory, JsonFileName);
        var markdownPath = Path.Combine(runDirectory, MarkdownFileName);
        var digestPath = Path.Combine(runDirectory, DigestFileName);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(evidence, SerializerOptions);
        var jsonWithNewline = new byte[jsonBytes.Length + 1];
        jsonBytes.CopyTo(jsonWithNewline, 0);
        jsonWithNewline[^1] = (byte)'\n';
        File.WriteAllBytes(jsonPath, jsonWithNewline);

        var digest = Convert.ToHexString(SHA256.HashData(jsonWithNewline)).ToLowerInvariant();
        File.WriteAllText(
            digestPath,
            $"{digest}  {JsonFileName}\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            markdownPath,
            RenderMarkdown(evidence, digest),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new EvidencePaths(runDirectory, jsonPath, markdownPath, digestPath);
    }

    public static IReadOnlyList<string> Validate(string jsonOrDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonOrDirectoryPath);

        var jsonPath = Directory.Exists(jsonOrDirectoryPath)
            ? Path.Combine(jsonOrDirectoryPath, JsonFileName)
            : Path.GetFullPath(jsonOrDirectoryPath);
        var markdownPath = Path.Combine(
            Path.GetDirectoryName(jsonPath)
                ?? throw new InvalidOperationException("Evidence JSON has no parent directory."),
            MarkdownFileName);
        var digestPath = Path.Combine(
            Path.GetDirectoryName(jsonPath)
                ?? throw new InvalidOperationException("Evidence JSON has no parent directory."),
            DigestFileName);
        var errors = new List<string>();
        if (!string.Equals(Path.GetFileName(jsonPath), JsonFileName, StringComparison.Ordinal))
        {
            errors.Add($"Evidence JSON must be named {JsonFileName}.");
        }

        if (!File.Exists(jsonPath))
        {
            return [$"Evidence JSON does not exist: {jsonPath}"];
        }

        AcceptanceEvidence? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<AcceptanceEvidence>(
                File.ReadAllBytes(jsonPath),
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

        var digestFields = File.ReadAllText(digestPath).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var expectedDigest = digestFields.FirstOrDefault() ?? string.Empty;
        if (digestFields.Length != 2
            || !string.Equals(digestFields[1], JsonFileName, StringComparison.Ordinal)
            || expectedDigest.Length != 64
            || !expectedDigest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            errors.Add("Evidence digest sidecar has an invalid format.");
        }

        var actualDigest = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(jsonPath))).ToLowerInvariant();
        if (!string.Equals(expectedDigest, actualDigest, StringComparison.Ordinal))
        {
            errors.Add("Evidence JSON does not match evidence.json.sha256.");
        }

        if (!File.Exists(markdownPath))
        {
            errors.Add($"Human-readable evidence is missing: {markdownPath}");
        }
        else
        {
            var expectedMarkdown = RenderMarkdown(evidence, actualDigest);
            var actualMarkdown = File.ReadAllText(markdownPath);
            if (!string.Equals(actualMarkdown, expectedMarkdown, StringComparison.Ordinal))
            {
                errors.Add("evidence.md does not match the validated JSON evidence.");
            }
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
        return options;
    }

    private static string CreateRunDirectory(string evidenceRoot, AcceptanceEvidence evidence)
    {
        var root = Path.GetFullPath(evidenceRoot);
        Directory.CreateDirectory(root);
        var platform = evidence.Platform == TargetPlatform.Windows ? "windows" : "linux-x11";
        var baseName = $"{evidence.StartedAtUtc:yyyyMMddTHHmmssfffZ}-{platform}-"
            + $"{EvidenceSanitizer.SanitizeIdentifier(evidence.Host.DeclaredSystemName)}-"
            + evidence.Build.PackageManifestSha256[..12];

        for (var suffix = 0; suffix < 100; suffix++)
        {
            var name = suffix == 0 ? baseName : $"{baseName}-{suffix + 1}";
            var candidate = Path.Combine(root, name);
            if (!Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a unique acceptance evidence directory.");
    }

    private static string RenderMarkdown(AcceptanceEvidence evidence, string digest)
    {
        var lines = new List<string>
        {
            "# Asura named-host M2 terminal acceptance",
            string.Empty,
            $"- Overall: **{FormatStatus(evidence.OverallResult)}**",
            $"- System: `{EscapeMarkdown(evidence.Host.DeclaredSystemName)}`",
            $"- Actual host: `{EscapeMarkdown(evidence.Host.ActualHostName)}`",
            $"- Observer: `{EscapeMarkdown(evidence.Host.Observer)}`",
            $"- Platform: `{evidence.Platform}`",
            $"- OS: `{EscapeMarkdown(evidence.Host.OsDescription)}` (`{evidence.Host.OsArchitecture}`)",
            $"- Desktop session: {EscapeMarkdown(evidence.Host.DesktopSession)}",
            $"- Renderer: {EscapeMarkdown(evidence.Backend.Renderer)}",
            $"- PTY: {EscapeMarkdown(evidence.Backend.PtyAdapter)}; {EscapeMarkdown(evidence.Backend.PtySubstrate)}",
            $"- Build label: `{EscapeMarkdown(evidence.Build.BuildLabel)}`",
            $"- Executable SHA-256: `{evidence.Build.ExecutableSha256}`",
            $"- Package manifest SHA-256: `{evidence.Build.PackageManifestSha256}` ({evidence.Build.PackageFileCount} files)",
            $"- Evidence SHA-256: `{digest}`",
            $"- Started: `{evidence.StartedAtUtc:O}`",
            $"- Completed: `{evidence.CompletedAtUtc:O}`",
            $"- Cleanup: {EscapeMarkdown(evidence.CleanupDisposition)}",
            string.Empty,
            "## Evidence boundaries",
            string.Empty,
        };
        lines.AddRange(evidence.Limitations.Select(limitation => $"- {EscapeMarkdown(limitation)}"));
        if (evidence.Host.EnvironmentWarnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Environment warnings:");
            lines.Add(string.Empty);
            lines.AddRange(evidence.Host.EnvironmentWarnings.Select(warning => $"- {EscapeMarkdown(warning)}"));
        }

        lines.Add(string.Empty);
        lines.Add("## Observations");
        lines.Add(string.Empty);
        lines.Add("| Check | Result | Mode | Sanitized evidence note |");
        lines.Add("| --- | --- | --- | --- |");
        foreach (var check in evidence.Checks)
        {
            lines.Add(
                $"| {EscapeMarkdown(check.Title)} | {FormatStatus(check.Result)} | "
                + $"{EscapeMarkdown(check.ObservationMode)} | {EscapeMarkdown(check.Notes)} |");
        }

        lines.Add(string.Empty);
        return string.Join('\n', lines);
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
}

internal static class EvidenceValidator
{
    public static IReadOnlyList<string> Validate(AcceptanceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var errors = new List<string>();
        if (evidence.SchemaVersion != AcceptanceEvidence.CurrentSchemaVersion)
        {
            errors.Add($"Unsupported schema version {evidence.SchemaVersion}.");
        }

        if (!string.Equals(
                evidence.EvidenceKind,
                AcceptanceEvidence.CurrentEvidenceKind,
                StringComparison.Ordinal))
        {
            errors.Add("Evidence kind is invalid.");
        }

        if (!string.Equals(
                evidence.RunnerVersion,
                AcceptanceEvidence.CurrentRunnerVersion,
                StringComparison.Ordinal))
        {
            errors.Add("Runner version is invalid.");
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

        if (!EvidenceSanitizer.IsSanitizedNote(evidence.CleanupDisposition))
        {
            errors.Add("Cleanup disposition contains unsanitized text.");
        }

        ValidateIdentity(evidence, errors);
        var expectedChecks = AcceptanceCatalog.All;
        if (evidence.Checks.Count != expectedChecks.Count)
        {
            errors.Add(
                $"Expected {expectedChecks.Count} acceptance checks, found {evidence.Checks.Count}.");
        }

        for (var index = 0; index < Math.Min(evidence.Checks.Count, expectedChecks.Count); index++)
        {
            var actual = evidence.Checks[index];
            var expected = expectedChecks[index];
            if (!string.Equals(actual.Id, expected.Id, StringComparison.Ordinal)
                || !string.Equals(actual.Title, expected.Title, StringComparison.Ordinal))
            {
                errors.Add($"Check {index + 1} does not match catalog entry {expected.Id}.");
            }

            if (actual.Notes.Length < 12 || actual.Notes.Length > EvidenceSanitizer.MaximumNoteLength + 12)
            {
                errors.Add($"Check {actual.Id} has an invalid evidence-note length.");
            }

            if (!EvidenceSanitizer.IsSanitizedNote(actual.Notes))
            {
                errors.Add($"Check {actual.Id} contains unsanitized evidence text.");
            }

            if (actual.ObservedAtUtc < evidence.StartedAtUtc
                || actual.ObservedAtUtc > evidence.CompletedAtUtc)
            {
                errors.Add($"Check {actual.Id} has an observation time outside the run.");
            }

            if (actual.ObservationMode is not (
                    "operator-observed"
                    or "runner-observed-boundary"
                    or "operator-observed+runner-verified"))
            {
                errors.Add($"Check {actual.Id} has an unknown observation mode.");
            }

            if (actual.Result == AcceptanceStatus.Pass
                && actual.ObservationMode is not (
                    "operator-observed"
                    or "operator-observed+runner-verified"))
            {
                errors.Add($"Check {actual.Id} claims PASS without an operator observation.");
            }

            if (actual.RedactionsApplied < 0)
            {
                errors.Add($"Check {actual.Id} has a negative redaction count.");
            }
        }

        var expectedOverall = AcceptanceEvidence.ResolveOverall(evidence.Checks);
        if (evidence.OverallResult != expectedOverall)
        {
            errors.Add(
                $"Overall result {evidence.OverallResult} does not match computed result {expectedOverall}.");
        }

        if (evidence.OverallResult == AcceptanceStatus.Pass && !evidence.Host.InteractiveUser)
        {
            errors.Add("A non-interactive host cannot produce overall PASS evidence.");
        }

        if (evidence.OverallResult == AcceptanceStatus.Pass
            && evidence.Host.EnvironmentSignals.BlocksNamedHostAcceptance)
        {
            errors.Add("A blocked host environment cannot produce overall PASS evidence.");
        }

        if (evidence.OverallResult == AcceptanceStatus.Pass
            && !string.Equals(
                evidence.CleanupDisposition,
                AcceptanceEvidence.CleanExitDisposition,
                StringComparison.Ordinal))
        {
            errors.Add("Overall PASS requires runner-confirmed packaged-parent exit without cleanup termination.");
        }

        if (evidence.OverallResult == AcceptanceStatus.Pass
            && (evidence.Checks.Count == 0
                || !string.Equals(evidence.Checks[^1].ObservationMode, "operator-observed+runner-verified", StringComparison.Ordinal)))
        {
            errors.Add("Overall PASS requires runner verification of the lifecycle observation.");
        }

        return errors;
    }

    private static void ValidateIdentity(AcceptanceEvidence evidence, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(evidence.Host.DeclaredSystemName)
            || string.IsNullOrWhiteSpace(evidence.Host.Observer)
            || string.IsNullOrWhiteSpace(evidence.Build.BuildLabel))
        {
            errors.Add("System name, observer, and build label are required.");
        }

        else if (!EvidenceSanitizer.IsSafeIdentifier(evidence.Host.DeclaredSystemName)
            || !EvidenceSanitizer.IsSafeIdentifier(evidence.Host.Observer)
            || !EvidenceSanitizer.IsSafeIdentifier(evidence.Build.BuildLabel))
        {
            errors.Add("System name, observer, and build label must be bounded safe identifiers.");
        }

        if (string.IsNullOrWhiteSpace(evidence.Host.ActualHostName)
            || !string.Equals(
                evidence.Host.ActualHostName,
                EvidenceSanitizer.SanitizeIdentifier(evidence.Host.ActualHostName),
                StringComparison.Ordinal))
        {
            errors.Add("Actual host name is not sanitized.");
        }

        if (evidence.Platform == TargetPlatform.LinuxX11
            && evidence.OverallResult == AcceptanceStatus.Pass
            && !string.Equals(
                evidence.Host.DesktopSession,
                "Linux x11 (DISPLAY present, Wayland display absent)",
                StringComparison.Ordinal))
        {
            errors.Add("Linux X11 PASS evidence has an inconsistent desktop-session identity.");
        }

        if (evidence.Platform == TargetPlatform.Windows
            && !string.Equals(
                evidence.Host.DesktopSession,
                "Windows interactive desktop",
                StringComparison.Ordinal))
        {
            errors.Add("Windows evidence has an inconsistent desktop-session identity.");
        }

        if (!EvidenceSanitizer.IsSanitizedNote(evidence.Host.OsDescription)
            || !EvidenceSanitizer.IsSanitizedNote(evidence.Host.DesktopSession)
            || evidence.Host.EnvironmentWarnings.Any(
                warning => !EvidenceSanitizer.IsSanitizedNote(warning)))
        {
            errors.Add("Host identity contains unsanitized text.");
        }

        if (string.IsNullOrWhiteSpace(evidence.Backend.Renderer)
            || string.IsNullOrWhiteSpace(evidence.Backend.PtyAdapter)
            || string.IsNullOrWhiteSpace(evidence.Backend.PtySubstrate))
        {
            errors.Add("Terminal backend identity is incomplete.");
        }
        else if (!EvidenceSanitizer.IsSanitizedNote(evidence.Backend.Renderer)
            || !EvidenceSanitizer.IsSanitizedNote(evidence.Backend.PtyAdapter)
            || !EvidenceSanitizer.IsSanitizedNote(evidence.Backend.PtySubstrate)
            || !EvidenceSanitizer.IsSanitizedNote(evidence.Backend.IdentitySource))
        {
            errors.Add("Terminal backend identity contains unsanitized text.");
        }
        else if (!evidence.Backend.Renderer.StartsWith("libghostty-vt ", StringComparison.Ordinal)
            || !evidence.Backend.Renderer.EndsWith(
                " state engine with Avalonia managed renderer",
                StringComparison.Ordinal)
            || !evidence.Backend.PtyAdapter.StartsWith("Porta.Pty ", StringComparison.Ordinal)
            || !string.Equals(
                evidence.Backend.PtySubstrate,
                PackageFingerprint.PtySubstrateFor(evidence.Platform),
                StringComparison.Ordinal)
            || !string.Equals(
                evidence.Backend.IdentitySource,
                PackageFingerprint.IdentitySourceDescription,
                StringComparison.Ordinal))
        {
            errors.Add("Terminal backend identity is inconsistent with this runner and platform.");
        }

        var expectedExecutable = evidence.Platform == TargetPlatform.Windows
            ? "Asura.exe"
            : "Asura";
        if (!string.Equals(
                evidence.Build.PackageExecutable,
                expectedExecutable,
                StringComparison.Ordinal)
            || Path.IsPathRooted(evidence.Build.PackageExecutable)
            || !string.Equals(
                evidence.Build.ProductVersion,
                EvidenceSanitizer.SanitizeSingleLine(evidence.Build.ProductVersion).Value,
                StringComparison.Ordinal))
        {
            errors.Add("Build identity has an unexpected executable name or unsanitized product version.");
        }

        if (!IsSha256(evidence.Build.ExecutableSha256)
            || !IsSha256(evidence.Build.PackageManifestSha256))
        {
            errors.Add("Build fingerprints must be lowercase SHA-256 values.");
        }

        if (evidence.Build.ExecutableLengthBytes <= 0 || evidence.Build.PackageFileCount <= 0)
        {
            errors.Add("Build size and package file count must be positive.");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
