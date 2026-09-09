using System.Diagnostics;
using System.Text.Json;
using Asura.AccessibilityAcceptance;
using Asura.Packaging;

namespace Asura.SecurityCampaign;

internal static class CampaignAssembler
{
    private const string RegistryRelativePath = "scripts/acceptance/security-campaign/cases.v1.json";
    private const string ReceiptSchemaRelativePath = "scripts/acceptance/security-campaign/receipt.schema.json";
    private const string WorkflowRelativePath = ".github/workflows/repository-gate.yml";
    private const string CanarySetSha256 = "7fcd5c19c8906d9fb161e377897314678db04fc4dfb22bbce735a2949eae3e66";
    private static readonly HashSet<string> SecrecyCaseIds = new(
    [
        "secrecy.app-diagnostic-adapter",
        "secrecy.cef-console-adapter",
        "secrecy.diagnostics-zip",
        "secrecy.persistence-sqlite",
        "secrecy.provider-tool-continuation",
    ],
    StringComparer.Ordinal);

    public static CampaignReceipt CreateSourceReceipt(
        string repository,
        string registryPath,
        string schemaPath,
        string testResults)
    {
        var root = Path.GetFullPath(repository);
        var definition = CampaignDefinitionValidator.Validate(root, registryPath);
        var source = ReadSource(root, tag: null, runId: null, runAttempt: null);
        var cases = TrxEvidenceReader.Read(testResults, definition.Cases);
        return new CampaignReceipt(
            1,
            "asura-security-campaign-receipt-v1",
            "source-only",
            ReleaseScope(),
            source,
            Definition(registryPath, schemaPath),
            cases,
            DeriveSecrecyEvidence(definition, cases),
            Candidate: null,
            Dependencies: null,
            Components: [],
            Signing: null,
            Limitations: ["windows-porting-deferred", "linux-porting-deferred", "release-candidate-not-evaluated"],
            Overall: "notEvaluated");
    }

    public static CampaignReceipt CreateReleaseReceipt(ReleaseInputs inputs)
    {
        RequireMacOsArm64();
        RequireGitHubReleaseContext(inputs);
        var evidence = ValidateReleaseInputs(inputs);
        return new CampaignReceipt(
            1,
            "asura-security-campaign-receipt-v1",
            "release-candidate",
            ReleaseScope(),
            evidence.Source,
            evidence.Definition,
            evidence.Cases,
            evidence.Secrecy,
            evidence.Candidate,
            evidence.Dependencies,
            evidence.Components,
            evidence.Signing,
            ["windows-porting-deferred", "linux-porting-deferred"],
            "pass");
    }

    public static void ValidateLocalReleaseInputs(ReleaseInputs inputs)
    {
        RequireMacOsArm64();
        _ = ValidateReleaseInputs(inputs);
    }

    private static ValidatedReleaseInputs ValidateReleaseInputs(ReleaseInputs inputs)
    {
        var root = Path.GetFullPath(inputs.Repository);
        var initialSeal = ReleaseSourceSeal.Verify(
            root,
            inputs.SourceSeal,
            inputs.SourceCommit,
            inputs.SourceTree,
            inputs.Tag,
            buildIdentityOutput: null);
        _ = ReleaseSourceSeal.ValidateBuildIdentity(inputs.BuildIdentity, initialSeal);
        var registryPath = Path.Combine(root, RegistryRelativePath);
        var schemaPath = Path.Combine(root, ReceiptSchemaRelativePath);
        var definition = CampaignDefinitionValidator.Validate(root, registryPath);
        RequireRunIdentity(inputs.RunId, "run ID");
        RequireRunIdentity(inputs.RunAttempt, "run attempt");
        var source = ReadSealedSource(initialSeal, inputs.RunId, inputs.RunAttempt, root);
        var cases = TrxEvidenceReader.Read(inputs.TestResults, definition.Cases);
        var dependencies = ReadDependencies(inputs.DependencyEvidence, source.Commit);
        var signing = ReadSigning(inputs.NotarizationEvidence);
        using var extracted = ExtractedMacCandidate.Open(inputs.Archive);
        var suppliedPackage = PackageFingerprint.Inspect(
            inputs.Package,
            TargetPlatform.MacOS,
            "release-candidate");
        var extractedPackage = PackageFingerprint.Inspect(
            extracted.PackagePath,
            TargetPlatform.MacOS,
            "release-candidate");
        if (suppliedPackage.Build != extractedPackage.Build)
        {
            throw new InvalidDataException("The supplied package differs from the application extracted from the archive.");
        }

        var candidate = InspectCandidate(
            inputs.Archive,
            extracted.PackagePath,
            inputs.SourceSeal,
            inputs.BuildIdentity,
            initialSeal);
        NativeMacVerifier.Verify(
            extracted.PackagePath,
            signing.TeamIdentifier,
            signing.CertificateSha256);
        var components = InspectComponents(root, extracted.PackagePath);
        var finalSeal = ReleaseSourceSeal.Verify(
            root,
            inputs.SourceSeal,
            inputs.SourceCommit,
            inputs.SourceTree,
            inputs.Tag,
            buildIdentityOutput: null);
        _ = ReleaseSourceSeal.ValidateBuildIdentity(inputs.BuildIdentity, finalSeal);
        if (!string.Equals(initialSeal.SealSha256, finalSeal.SealSha256, StringComparison.Ordinal)
            || !string.Equals(initialSeal.ObservedManifestSha256, finalSeal.ObservedManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The sealed source changed while release evidence was assembled.");
        }

        return new ValidatedReleaseInputs(
            source,
            Definition(registryPath, schemaPath),
            cases,
            DeriveSecrecyEvidence(definition, cases),
            candidate,
            dependencies,
            components,
            signing);
    }

    public static void ValidateReceipt(CampaignReceipt receipt)
    {
        if (receipt.SchemaVersion != 1
            || !string.Equals(receipt.EvidenceKind, "asura-security-campaign-receipt-v1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The campaign receipt identity is invalid.");
        }

        if (!receipt.ReleaseScope.SequenceEqual(ReleaseScope()))
        {
            throw new InvalidDataException("The release scope must retain explicit macOS, Windows, and Linux rows.");
        }

        if (!string.Equals(
                receipt.Source.Repository,
                "https://github.com/terion-labs/asura",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The campaign receipt has an invalid repository identity.");
        }

        RequireHex(receipt.Source.Commit, 40, "source commit");
        RequireHex(receipt.Source.Tree, 40, "source tree");
        RequireHex(receipt.Source.SourceArchiveSha256, 64, "source archive SHA-256");
        RequireHex(receipt.Source.WorkflowSha256 ?? string.Empty, 64, "workflow SHA-256");
        RequireHex(receipt.Definition.RegistrySha256, 64, "registry SHA-256");
        RequireHex(receipt.Definition.ReceiptSchemaSha256, 64, "receipt schema SHA-256");
        RequireHex(receipt.Definition.ToolCatalogSha256, 64, "tool catalog SHA-256");
        RequireHex(receipt.Secrecy.CanarySetSha256, 64, "secrecy canary-set SHA-256");
        foreach (var item in receipt.Cases)
        {
            RequireHex(item.TrxSha256, 64, $"TRX SHA-256 for {item.Id}");
            if (!item.TestName.Contains(item.Id, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Campaign case {item.Id} is not bound into its TRX name.");
            }
        }

        IReadOnlyList<string> expectedLimitations = receipt.EvidenceClass switch
        {
            "source-only" => ["windows-porting-deferred", "linux-porting-deferred", "release-candidate-not-evaluated"],
            "release-candidate" => ["windows-porting-deferred", "linux-porting-deferred"],
            _ => throw new InvalidDataException("The campaign evidence class is invalid."),
        };
        if (!receipt.Limitations.SequenceEqual(expectedLimitations, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The campaign limitations are invalid.");
        }

        var allCasesPass = receipt.Cases.Count > 0
            && receipt.Cases.All(static item => string.Equals(item.Result, "pass", StringComparison.Ordinal))
            && receipt.Cases.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() == receipt.Cases.Count;
        if (!allCasesPass)
        {
            throw new InvalidDataException("Every registered campaign case must pass exactly once.");
        }

        var receiptSecrecyIds = receipt.Cases
            .Where(item => SecrecyCaseIds.Contains(item.Id))
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var zeroMatchCaseCount = receipt.Cases.Count(item =>
            SecrecyCaseIds.Contains(item.Id)
            && string.Equals(item.Result, "pass", StringComparison.Ordinal));
        if (!receiptSecrecyIds.SetEquals(SecrecyCaseIds)
            || !string.Equals(receipt.Secrecy.CanarySetSha256, CanarySetSha256, StringComparison.Ordinal)
            || receipt.Secrecy.CanaryCount != 2
            || receipt.Secrecy.SecrecyCaseCount != SecrecyCaseIds.Count
            || receipt.Secrecy.ZeroMatchCaseCount != zeroMatchCaseCount)
        {
            throw new InvalidDataException("The secrecy aggregate does not match the closed canary campaign.");
        }

        if (string.Equals(receipt.EvidenceClass, "source-only", StringComparison.Ordinal))
        {
            if (!string.Equals(receipt.Overall, "notEvaluated", StringComparison.Ordinal)
                || receipt.Source.SourceSealSha256 is not null
                || receipt.Source.SourceManifestSha256 is not null
                || receipt.Candidate is not null
                || receipt.Dependencies is not null
                || receipt.Signing is not null)
            {
                throw new InvalidDataException("Source-only evidence cannot claim a release result.");
            }

            return;
        }

        if (string.Equals(receipt.EvidenceClass, "release-candidate", StringComparison.Ordinal)
            && (!string.Equals(receipt.Overall, "pass", StringComparison.Ordinal)
                || receipt.Source.SourceSealSha256 is null
                || receipt.Source.SourceManifestSha256 is null
                || receipt.Candidate is null
                || receipt.Dependencies is null
                || receipt.Signing is null
                || !string.Equals(receipt.Dependencies.Status, "pass", StringComparison.Ordinal)
                || receipt.Dependencies.UntriagedAdvisories != 0
                || receipt.Dependencies.ReleaseBlockingAdvisories != 0
                || !string.Equals(receipt.Signing.NotarizationStatus, "Accepted", StringComparison.Ordinal)
                || !receipt.Signing.CodeSignatureValid
                || !receipt.Signing.StapleValid
                || !receipt.Signing.GatekeeperAccepted))
        {
            throw new InvalidDataException("The release-candidate result is not derived from complete passing evidence.");
        }

        if (receipt.Candidate is not null)
        {
            RequireHex(receipt.Source.SourceSealSha256 ?? string.Empty, 64, "source seal SHA-256");
            RequireHex(receipt.Source.SourceManifestSha256 ?? string.Empty, 64, "source manifest SHA-256");
            RequireHex(receipt.Candidate.SourceSealSha256, 64, "candidate source seal SHA-256");
            RequireHex(receipt.Candidate.SourceManifestSha256, 64, "candidate source manifest SHA-256");
            RequireHex(receipt.Candidate.BuildIdentitySha256, 64, "candidate build identity SHA-256");
            if (!string.Equals(receipt.Candidate.SourceSealSha256, receipt.Source.SourceSealSha256, StringComparison.Ordinal)
                || !string.Equals(receipt.Candidate.SourceManifestSha256, receipt.Source.SourceManifestSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The release candidate is not bound to the sealed source manifest.");
            }
        }
    }

    private static DefinitionEvidence Definition(string registryPath, string schemaPath) => new(
        CampaignFiles.Sha256File(registryPath, 1024 * 1024),
        CampaignFiles.Sha256File(schemaPath, 1024 * 1024),
        CampaignDefinitionValidator.CatalogDigest());

    private static SecrecyEvidence DeriveSecrecyEvidence(
        CampaignDefinition definition,
        IReadOnlyList<CaseEvidence> cases)
    {
        var registered = definition.Cases
            .Where(static item => string.Equals(item.Kind, "secrecy", StringComparison.Ordinal))
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!registered.SetEquals(SecrecyCaseIds))
        {
            throw new InvalidDataException("The registry secrecy campaign does not match the closed case set.");
        }

        var zeroMatchCaseCount = cases.Count(item =>
            SecrecyCaseIds.Contains(item.Id)
            && string.Equals(item.Result, "pass", StringComparison.Ordinal));
        return new SecrecyEvidence(
            CanarySetSha256,
            CanaryCount: 2,
            SecrecyCaseCount: registered.Count,
            ZeroMatchCaseCount: zeroMatchCaseCount);
    }

    private static SourceEvidence ReadSource(string repository, string? tag, string? runId, string? runAttempt)
    {
        var commit = Git(repository, "rev-parse", "HEAD");
        var tree = Git(repository, "rev-parse", "HEAD^{tree}");
        var origin = Git(repository, "config", "--get", "remote.origin.url");
        if (origin is not ("git@github.com:terion-labs/asura.git"
            or "https://github.com/terion-labs/asura.git"
            or "https://github.com/terion-labs/asura"))
        {
            throw new InvalidDataException("The security campaign must run from the canonical Asura repository.");
        }

        RequireHex(commit, 40, "source commit");
        RequireHex(tree, 40, "source tree");
        return new SourceEvidence(
            "https://github.com/terion-labs/asura",
            commit,
            tree,
            GitArchiveSha256(repository, commit),
            SourceSealSha256: null,
            SourceManifestSha256: null,
            tag,
            CampaignFiles.Sha256File(Path.Combine(repository, WorkflowRelativePath), 1024 * 1024),
            runId,
            runAttempt);
    }

    private static SourceEvidence ReadSealedSource(
        ReleaseSourceSealVerification verification,
        string runId,
        string runAttempt,
        string sourceRoot) => new(
            verification.Seal.Repository,
            verification.Seal.Commit,
            verification.Seal.Tree,
            verification.Seal.SourceArchiveSha256,
            verification.SealSha256,
            verification.Seal.ManifestSha256,
            verification.Seal.Tag,
            CampaignFiles.Sha256File(Path.Combine(sourceRoot, WorkflowRelativePath), 1024 * 1024),
            runId,
            runAttempt);

    private static CandidateEvidence InspectCandidate(
        string archivePath,
        string packagePath,
        string sourceSealDirectory,
        string buildIdentityPath,
        ReleaseSourceSealVerification verification)
    {
        var archive = new FileInfo(Path.GetFullPath(archivePath));
        if (!archive.Exists || !string.Equals(archive.Name, "Asura-macOS-arm64.zip", StringComparison.Ordinal))
        {
            throw new FileNotFoundException("The candidate archive must be Asura-macOS-arm64.zip.", archive.FullName);
        }

        var inspection = PackageFingerprint.Inspect(packagePath, TargetPlatform.MacOS, "release-candidate");
        var releaseEvidence = Path.Combine(
            packagePath,
            "Contents",
            "Resources",
            "Licenses",
            "Release");
        var packagedSeal = Path.Combine(releaseEvidence, ReleaseSourceSeal.SealFileName);
        var packagedIdentity = Path.Combine(releaseEvidence, ReleaseSourceSeal.BuildIdentityFileName);
        var suppliedSeal = Path.Combine(sourceSealDirectory, ReleaseSourceSeal.SealFileName);
        if (!CampaignFiles.ReadFile(packagedSeal, 16 * 1024 * 1024)
                .SequenceEqual(CampaignFiles.ReadFile(suppliedSeal, 16 * 1024 * 1024))
            || !CampaignFiles.ReadFile(packagedIdentity, 1024 * 1024)
                .SequenceEqual(CampaignFiles.ReadFile(buildIdentityPath, 1024 * 1024)))
        {
            throw new InvalidDataException("The candidate does not embed its exact source seal and build identity.");
        }

        var executablePath = Path.Combine(packagePath, "Contents", "MacOS", inspection.Build.PackageExecutable);
        if (!ContainsAscii(executablePath, verification.Seal.ManifestSha256))
        {
            throw new InvalidDataException("The candidate executable does not embed the sealed source manifest identity.");
        }

        return new CandidateEvidence(
            archive.Name,
            archive.Length,
            CampaignFiles.Sha256File(archive.FullName),
            inspection.Build.PackageManifestSha256,
            inspection.Build.PackageFileCount,
            inspection.Build.PackageExecutable,
            inspection.Build.ExecutableLengthBytes,
            inspection.Build.ExecutableSha256,
            verification.SealSha256,
            verification.Seal.ManifestSha256,
            CampaignFiles.Sha256File(packagedIdentity, 1024 * 1024),
            inspection.Build.ApplicationIdentity,
            inspection.Build.ProductVersion);
    }

    private static IReadOnlyList<FileEvidence> InspectComponents(string repository, string packagePath)
    {
        var package = Path.GetFullPath(packagePath);
        var contents = Path.Combine(package, "Contents");
        var executable = Path.Combine(contents, "MacOS");
        var resources = Path.Combine(contents, "Resources");
        var licenses = Path.Combine(resources, "Licenses");
        var nativeLicenses = Path.Combine(licenses, "Native");
        var releaseLicenses = Path.Combine(licenses, "Release");
        TerminalFontPackageProvenance.Validate(
            resources,
            licenses,
            Path.Combine(nativeLicenses, "terminal-font-assets.json"),
            Path.Combine(nativeLicenses, "terminal-font-assets-build-receipt.json"));
        var legal = MacOsReleaseLegalClosure.Validate(
            Path.Combine(repository, "licenses", "macos-release-legal.json"),
            repository);
        MacOsReleaseLegalClosure.RequirePublicationClearance(legal);
        var packagedLegal = Path.Combine(licenses, "MACOS-RELEASE-LEGAL.json");
        if (!CampaignFiles.ReadFile(packagedLegal, 1024 * 1024).SequenceEqual(legal.Record))
        {
            throw new InvalidDataException("The packaged legal record differs from the validated source record.");
        }

        var componentPaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["legal"] = packagedLegal,
            ["native-terminal-library"] = Path.Combine(executable, "libghostty-vt.dylib"),
            ["native-terminal-receipt"] = Path.Combine(nativeLicenses, "native-terminal-build-receipt.json"),
            ["terminal-font-receipt"] = Path.Combine(nativeLicenses, "terminal-font-assets-build-receipt.json"),
            ["cef-receipt"] = Path.Combine(nativeLicenses, "cef-runtime-build-receipt.json"),
            ["sql-worker-receipt"] = Path.Combine(resources, "Native", "SqlLanguage", "build-receipt.json"),
            ["sql-runtime-dependencies"] = Path.Combine(resources, "Native", "SqlLanguage", "runtime-dependencies.txt"),
            ["sql-third-party-notices"] = Path.Combine(resources, "Native", "SqlLanguage", "THIRD-PARTY-NOTICES.md"),
            ["release-source-seal"] = Path.Combine(releaseLicenses, ReleaseSourceSeal.SealFileName),
            ["release-build-identity"] = Path.Combine(releaseLicenses, ReleaseSourceSeal.BuildIdentityFileName),
        };
        return [.. componentPaths
            .Select(item => CampaignFiles.InspectFile(
                item.Key,
                item.Value,
                Path.GetRelativePath(package, item.Value).Replace('\\', '/'),
                64 * 1024 * 1024))
            .OrderBy(static item => item.Kind, StringComparer.Ordinal)];
    }

    private static bool ContainsAscii(string path, string value)
    {
        var expected = System.Text.Encoding.ASCII.GetBytes(value);
        var buffer = new byte[64 * 1024 + expected.Length - 1];
        var retained = 0;
        using var stream = File.OpenRead(path);
        while (true)
        {
            var read = stream.Read(buffer, retained, buffer.Length - retained);
            if (read == 0)
            {
                return buffer.AsSpan(0, retained).IndexOf(expected) >= 0;
            }

            var available = retained + read;
            if (buffer.AsSpan(0, available).IndexOf(expected) >= 0)
            {
                return true;
            }

            retained = Math.Min(expected.Length - 1, available);
            buffer.AsSpan(available - retained, retained).CopyTo(buffer);
        }
    }

    private static DependencyEvidence ReadDependencies(string path, string commit)
    {
        var document = CampaignFiles.ReadJson<DependencyEvidenceDocument>(path);
        var inputKinds = document.Inputs.Select(static input => input.Kind).ToArray();
        var inputPaths = document.Inputs.Select(static input => input.RelativePath).ToArray();
        if (document.SchemaVersion != 1
            || !string.Equals(document.Format, "asura-dependency-security-evidence-v1", StringComparison.Ordinal)
            || !string.Equals(document.SourceCommit, commit, StringComparison.Ordinal)
            || !string.Equals(document.Status, "pass", StringComparison.Ordinal)
            || document.UntriagedAdvisories != 0
            || document.ReleaseBlockingAdvisories != 0
            || !inputKinds.ToHashSet(StringComparer.Ordinal).SetEquals(["nuget-audit", "maven-audit"])
            || inputKinds.Length != 2
            || inputPaths.Distinct(StringComparer.Ordinal).Count() != inputPaths.Length)
        {
            throw new InvalidDataException("Dependency evidence is incomplete or release-blocking.");
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var input in document.Inputs)
        {
            RequireRelativeEvidencePath(input.RelativePath);
            var inspected = CampaignFiles.InspectFile(input.Kind, Path.Combine(root, input.RelativePath), input.RelativePath, 64 * 1024 * 1024);
            if (inspected != input)
            {
                throw new InvalidDataException($"Dependency evidence input {input.RelativePath} changed.");
            }
        }

        return new DependencyEvidence(
            document.Format,
            document.SourceCommit,
            document.Status,
            document.Inputs,
            document.UntriagedAdvisories,
            document.ReleaseBlockingAdvisories);
    }

    private static SigningEvidence ReadSigning(string path)
    {
        var value = CampaignFiles.ReadJson<SigningEvidenceDocument>(path);
        if (value.SchemaVersion != 1
            || !string.Equals(value.Format, "asura-macos-signing-evidence-v1", StringComparison.Ordinal)
            || !Guid.TryParseExact(value.NotarizationId, "D", out _)
            || !string.Equals(value.NotarizationStatus, "Accepted", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(value.TeamIdentifier)
            || value.TeamIdentifier.Length > 32
            || !value.TeamIdentifier.All(char.IsAsciiLetterOrDigit)
            || !value.CodeSignatureValid
            || !value.StapleValid
            || !value.GatekeeperAccepted)
        {
            throw new InvalidDataException("Signing evidence is incomplete or not accepted.");
        }

        RequireHex(value.CertificateSha256, 64, "certificate SHA-256");
        return new SigningEvidence(
            value.Format,
            value.NotarizationId,
            value.NotarizationStatus,
            value.TeamIdentifier,
            value.CertificateSha256,
            value.CodeSignatureValid,
            value.StapleValid,
            value.GatekeeperAccepted);
    }

    private static IReadOnlyList<PlatformEvidence> ReleaseScope() =>
    [
        new("macOS", "osx-arm64", "inScope", "desktop-v1-release-target"),
        new("Windows", "win-*", "notApplicable", "porting-deferred"),
        new("Linux", "linux-*", "notApplicable", "porting-deferred"),
    ];

    private static string Git(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repository,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"git {arguments[0]} failed: {error.Trim()}");
        }

        return output.Trim();
    }

    private static string GitArchiveSha256(string repository, string commit)
    {
        var start = GitStartInfo(repository, ["archive", "--format=tar", commit], redirectOutput: true);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        using var archive = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(archive);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"git archive failed: {error.Trim()}");
        }

        return CampaignFiles.Sha256(archive.ToArray());
    }

    private static ProcessStartInfo GitStartInfo(
        string repository,
        IReadOnlyList<string> arguments,
        bool redirectOutput)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = redirectOutput,
            UseShellExecute = false,
            WorkingDirectory = repository,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static void RequireMacOsArm64()
    {
        if (!OperatingSystem.IsMacOS() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
        {
            throw new PlatformNotSupportedException("Release-candidate evidence requires a native macOS arm64 process.");
        }
    }

    internal static void RequireGitHubReleaseContext(ReleaseInputs inputs)
    {
        var expectedWorkflowPrefix =
            "terion-labs/asura/.github/workflows/repository-gate.yml@refs/tags/";
        var workflowReference = Environment.GetEnvironmentVariable("GITHUB_WORKFLOW_REF") ?? string.Empty;
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"), "terion-labs/asura", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_REF_TYPE"), "tag", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_REF_NAME"), inputs.Tag, StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_SHA"), inputs.SourceCommit, StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), inputs.RunId, StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), inputs.RunAttempt, StringComparison.Ordinal)
            || !workflowReference.StartsWith(expectedWorkflowPrefix, StringComparison.Ordinal)
            || !workflowReference.EndsWith("/" + inputs.Tag, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Release-candidate PASS can be assembled only by the tagged repository-gate GitHub Actions run.");
        }
    }

    private static void RequireHex(string value, int length, string field)
    {
        if (value.Length != length || !value.All(static character => char.IsAsciiHexDigit(character) && !char.IsUpper(character)))
        {
            throw new InvalidDataException($"The {field} must be {length} lowercase hexadecimal characters.");
        }
    }

    private static void RequireRunIdentity(string value, string field)
    {
        if (value.Length is < 1 or > 32 || !value.All(char.IsAsciiDigit))
        {
            throw new InvalidDataException($"The GitHub {field} must be a bounded unsigned integer.");
        }
    }

    private static void RequireRelativeEvidencePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Split(['/', '\\']).Any(static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Evidence input paths must be canonical relative paths.");
        }
    }
}

internal sealed record ReleaseInputs(
    string Repository,
    string SourceCommit,
    string SourceTree,
    string Tag,
    string RunId,
    string RunAttempt,
    string SourceSeal,
    string BuildIdentity,
    string Archive,
    string Package,
    string TestResults,
    string DependencyEvidence,
    string NotarizationEvidence);

internal sealed record ValidatedReleaseInputs(
    SourceEvidence Source,
    DefinitionEvidence Definition,
    IReadOnlyList<CaseEvidence> Cases,
    SecrecyEvidence Secrecy,
    CandidateEvidence Candidate,
    DependencyEvidence Dependencies,
    IReadOnlyList<FileEvidence> Components,
    SigningEvidence Signing);
