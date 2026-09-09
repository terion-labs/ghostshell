using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Asura.SecurityCampaign;

namespace Asura.AccessibilityAcceptance.Tests;

public sealed class SecurityCampaignEvidenceTests
{
    [Fact]
    public void SourceOnlyReceiptCannotClaimPass()
    {
        var receipt = Receipt() with { Overall = "pass" };

        var error = Assert.Throws<InvalidDataException>(
            () => CampaignAssembler.ValidateReceipt(receipt));

        Assert.Contains("cannot claim", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TestFixtureEvidenceClassIsRejected()
    {
        var receipt = Receipt() with
        {
            EvidenceClass = "test-fixture",
            Overall = "pass",
        };

        var error = Assert.Throws<InvalidDataException>(
            () => CampaignAssembler.ValidateReceipt(receipt));

        Assert.Contains("evidence class", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecrecyAggregateRejectsTamperedDigestOrCounts()
    {
        var receipt = Receipt();
        var serialized = JsonSerializer.Serialize(receipt, CampaignFiles.StrictJson);
        Assert.DoesNotContain("campaign-application-managed", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign-vault-resolved", serialized, StringComparison.Ordinal);

        var digestError = Assert.Throws<InvalidDataException>(
            () => CampaignAssembler.ValidateReceipt(receipt with
            {
                Secrecy = receipt.Secrecy with { CanarySetSha256 = new string('a', 64) },
            }));
        Assert.Contains("secrecy aggregate", digestError.Message, StringComparison.OrdinalIgnoreCase);

        var countError = Assert.Throws<InvalidDataException>(
            () => CampaignAssembler.ValidateReceipt(receipt with
            {
                Secrecy = receipt.Secrecy with { ZeroMatchCaseCount = 4 },
            }));
        Assert.Contains("secrecy aggregate", countError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeferredPlatformCannotBeReportedAsPass()
    {
        var scope = Receipt().ReleaseScope.ToArray();
        scope[1] = scope[1] with { Status = "pass" };
        var receipt = Receipt() with { ReleaseScope = scope };

        var error = Assert.Throws<InvalidDataException>(
            () => CampaignAssembler.ValidateReceipt(receipt));

        Assert.Contains("release scope", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceiptFilesRoundTripAndRejectTampering()
    {
        using var fixture = new DirectoryFixture();
        var evidence = Path.Combine(fixture.Path, "evidence");
        CampaignFiles.WriteReceipt(evidence, Receipt());
        var loaded = CampaignFiles.ReadReceipt(evidence);
        Assert.Equal("notEvaluated", loaded.Overall);

        File.AppendAllText(Path.Combine(evidence, "receipt.json"), " ", Encoding.UTF8);
        var error = Assert.Throws<InvalidDataException>(() => CampaignFiles.ReadReceipt(evidence));
        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrictJsonRejectsUnknownAndDuplicateFields()
    {
        using var fixture = new DirectoryFixture();
        var unknown = Path.Combine(fixture.Path, "unknown.json");
        File.WriteAllText(unknown, "{\"schemaVersion\":1,\"extra\":true}", new UTF8Encoding(false));
        Assert.Throws<InvalidDataException>(() => CampaignFiles.ReadJson<CampaignDefinition>(unknown));

        var duplicate = Path.Combine(fixture.Path, "duplicate.json");
        File.WriteAllText(duplicate, "{\"schemaVersion\":1,\"schemaVersion\":1}", new UTF8Encoding(false));
        var error = Assert.Throws<InvalidDataException>(
            () => CampaignFiles.ReadJson<CampaignDefinition>(duplicate));
        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrxReaderRequiresExactlyOnePassingStableCase()
    {
        using var fixture = new DirectoryFixture();
        var trx = Path.Combine(fixture.Path, "campaign.trx");
        WriteTrx(trx, "security content.browser-continuation", "Passed");
        var definition = new CampaignCaseDefinition(
            "content.browser-continuation",
            "content",
            "tests/Test.csproj",
            "tests/Test.cs",
            "content.browser-continuation");

        var result = Assert.Single(TrxEvidenceReader.Read(fixture.Path, [definition]));
        Assert.Equal("pass", result.Result);

        WriteTrx(trx, "security content.browser-continuation", "Failed");
        var error = Assert.Throws<InvalidDataException>(
            () => TrxEvidenceReader.Read(fixture.Path, [definition]));
        Assert.Contains("did not pass", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseReceiptCannotPassWithoutCandidateInputs()
    {
        var receipt = Receipt() with
        {
            EvidenceClass = "release-candidate",
            Overall = "pass",
            Limitations = ["windows-porting-deferred", "linux-porting-deferred"],
        };

        var error = Assert.Throws<InvalidDataException>(
            () => CampaignAssembler.ValidateReceipt(receipt));

        Assert.Contains("complete passing evidence", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseAssemblerRejectsLocalContextBeforeCandidateAccess()
    {
        var inputs = new ReleaseInputs(
            ".",
            new string('a', 40),
            new string('b', 40),
            "v0.0.0-local-context-rejection",
            "0",
            "0",
            "missing-source-seal",
            "missing-build-identity.json",
            "missing.zip",
            "missing.app",
            "missing-results",
            "missing-dependencies.json",
            "missing-signing.json");

        var error = Assert.Throws<InvalidDataException>(
            () => CampaignAssembler.RequireGitHubReleaseContext(inputs));
        Assert.Contains("GitHub Actions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactSourceTreeRejectsDirtyTrackedFile()
    {
        using var fixture = new GitRepositoryFixture();
        File.AppendAllText(Path.Combine(fixture.Path, "tracked.txt"), "dirty", Encoding.UTF8);

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateSeal());

        Assert.Contains("tracked working-tree changes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactSourceTreeRejectsStagedIndexChange()
    {
        using var fixture = new GitRepositoryFixture();
        File.AppendAllText(Path.Combine(fixture.Path, "tracked.txt"), "staged", Encoding.UTF8);
        RunGit(fixture.Path, "add", "tracked.txt");

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateSeal());

        Assert.Contains("staged index changes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactSourceTreeIgnoresRepositoryFilesExcludedFromSealedExport()
    {
        using var fixture = new GitRepositoryFixture();
        File.WriteAllText(Path.Combine(fixture.Path, "unexpected.cs"), "tamper", Encoding.UTF8);
        var localSecrets = Path.Combine(fixture.Path, ".apple");
        Directory.CreateDirectory(localSecrets);
        File.WriteAllText(Path.Combine(localSecrets, "AuthKey.p8"), "secret", Encoding.UTF8);

        var sealedSource = fixture.CreateSeal();

        Assert.Equal(fixture.Commit, sealedSource.Seal.Commit);
    }

    [Fact]
    public void ExactSourceTreeIgnoresOnlyUntrackedRootFinderMetadataExcludedFromTag()
    {
        using var fixture = new GitRepositoryFixture();
        File.WriteAllText(Path.Combine(fixture.Path, ".DS_Store"), "finder", Encoding.UTF8);
        File.WriteAllText(Path.Combine(fixture.ExportPath, ".DS_Store"), "finder", Encoding.UTF8);

        var sealedSource = fixture.CreateSeal();
        var verified = fixture.VerifySealedSource();

        Assert.DoesNotContain(sealedSource.Seal.Files, file => file.RelativePath == ".DS_Store");
        Assert.Equal(sealedSource.Seal.ManifestSha256, verified.ObservedManifestSha256);

        var nestedDirectory = Path.Combine(fixture.ExportPath, "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(nestedDirectory, ".DS_Store"), "finder", Encoding.UTF8);
        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateSeal());
        Assert.Contains("differs from the exact tagged archive", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceSealIncludesTrackedRootFinderMetadataAndDetectsMutation()
    {
        using var fixture = new GitRepositoryFixture(trackRootFinderMetadata: true);
        var sealedSource = fixture.CreateSeal();

        Assert.Contains(sealedSource.Seal.Files, file => file.RelativePath == ".DS_Store");
        File.WriteAllText(Path.Combine(fixture.ExportPath, ".DS_Store"), "tamper", Encoding.UTF8);
        var error = Assert.Throws<InvalidDataException>(() => fixture.VerifySealedSource());
        Assert.Contains("sealed tagged manifest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceSealRejectsIgnoredDirectoryBuildImport()
    {
        using var fixture = new GitRepositoryFixture();
        File.WriteAllText(
            Path.Combine(fixture.ExportPath, "Directory.Build.targets"),
            "<Project><Target Name=\"Tamper\" BeforeTargets=\"CoreCompile\" /></Project>",
            new UTF8Encoding(false));

        var error = Assert.Throws<InvalidDataException>(() => fixture.CreateSeal());

        Assert.Contains("differs from the exact tagged archive", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceSealAllowsOnlyClosedGeneratedRootsAfterSeal()
    {
        using var fixture = new GitRepositoryFixture();
        var sealedSource = fixture.CreateSeal();
        var cache = Path.Combine(fixture.ExportPath, ".deps", "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "generated.bin"), "generated", Encoding.UTF8);

        var verified = fixture.VerifySealedSource();

        Assert.Equal(sealedSource.Seal.ManifestSha256, verified.ObservedManifestSha256);
        File.WriteAllText(
            Path.Combine(fixture.ExportPath, "Directory.Build.targets"),
            "<Project />",
            new UTF8Encoding(false));
        var error = Assert.Throws<InvalidDataException>(() => fixture.VerifySealedSource());
        Assert.Contains("sealed tagged manifest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporaryBuildInputMutationLeavesRejectableBuildIdentityAfterRestore()
    {
        using var fixture = new GitRepositoryFixture();
        var sealedSource = fixture.CreateSeal();
        var source = Path.Combine(fixture.ExportPath, "tracked.txt");
        File.WriteAllText(source, "temporary compiler input", Encoding.UTF8);
        var identity = Path.Combine(fixture.EvidenceParent, "mutated-build-identity.json");

        Assert.Throws<InvalidDataException>(() => fixture.VerifySealedSource(identity));
        Assert.True(File.Exists(identity));
        File.WriteAllText(source, "tracked", Encoding.UTF8);
        var restored = fixture.VerifySealedSource();
        Assert.Equal(sealedSource.Seal.ManifestSha256, restored.ObservedManifestSha256);

        var error = Assert.Throws<InvalidDataException>(
            () => ReleaseSourceSeal.ValidateBuildIdentity(identity, restored));
        Assert.Contains("build identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckedInSchemasAreClosedJsonDocuments()
    {
        var root = RepositoryRoot();
        foreach (var name in new[]
                 {
                     "cases.schema.json",
                     "receipt.schema.json",
                     "release-build-identity.schema.json",
                     "source-seal.schema.json",
                 })
        {
            var path = Path.Combine(root, "scripts", "acceptance", "security-campaign", name);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.False(document.RootElement.GetProperty("additionalProperties").GetBoolean());
        }
    }

    [Fact]
    public void ReleaseWorkflowBuildsAndValidatesFromReadOnlySealedExport()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "repository-gate.yml"),
            Encoding.UTF8);
        var packageScript = File.ReadAllText(
            Path.Combine(root, "scripts", "package-macos.sh"),
            Encoding.UTF8);
        var sealStep = workflow.IndexOf(
            "Seal exact tagged source before any release build",
            StringComparison.Ordinal);
        var nativeBuild = workflow.IndexOf("Build verified native payloads", StringComparison.Ordinal);
        var packageBuild = workflow.IndexOf(
            "Assemble update-aware macOS release",
            StringComparison.Ordinal);

        Assert.True(sealStep >= 0 && sealStep < nativeBuild && nativeBuild < packageBuild);
        Assert.Contains("chmod -R a-w \"${sealed_source}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("--repository \"${ASURA_RELEASE_SOURCE_ROOT}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("--source-seal \"${ASURA_RELEASE_SOURCE_SEAL}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("AsuraReleaseSourceManifestSha256", packageScript, StringComparison.Ordinal);
        Assert.True(
            packageScript.Split("verify_release_source", StringSplitOptions.None).Length >= 9,
            "Packaging must verify the sealed manifest around restore, both compilations, and package assembly.");
    }

    [Fact]
    public void ReleaseTagsRequireTheCompleteLocalSignedRehearsal()
    {
        var root = RepositoryRoot();
        var hook = File.ReadAllText(
            Path.Combine(root, ".githooks", "pre-push"),
            Encoding.UTF8);
        var rehearsal = File.ReadAllText(
            Path.Combine(root, "scripts", "rehearse-macos-release.sh"),
            Encoding.UTF8);

        Assert.Contains("refs/tags/v*", hook, StringComparison.Ordinal);
        Assert.Contains("scripts/rehearse-macos-release.sh", hook, StringComparison.Ordinal);
        Assert.Contains("--reuse-pass", hook, StringComparison.Ordinal);
        Assert.Contains("./scripts/check.sh --full", rehearsal, StringComparison.Ordinal);
        Assert.Contains("chmod -R a-w \"${sealed_source}\"", rehearsal, StringComparison.Ordinal);
        Assert.Contains("./scripts/build-libghostty-vt.sh --rid osx-arm64", rehearsal, StringComparison.Ordinal);
        Assert.Contains("./scripts/build-sql-language-worker.sh --local --rid osx-arm64", rehearsal, StringComparison.Ordinal);
        Assert.Contains("./scripts/build-cef-runtime.sh --rid osx-arm64", rehearsal, StringComparison.Ordinal);
        Assert.Contains("--sign-identity \"${signing_identity}\"", rehearsal, StringComparison.Ordinal);
        Assert.Contains("--notary-profile \"${notary_profile}\"", rehearsal, StringComparison.Ordinal);
        Assert.Contains("APPLE_CERTIFICATE_P12_BASE64", rehearsal, StringComparison.Ordinal);
        Assert.Contains("APPLE_NOTARY_PRIVATE_KEY_BASE64", rehearsal, StringComparison.Ordinal);
        Assert.Contains("security create-keychain", rehearsal, StringComparison.Ordinal);
        Assert.Contains("security delete-keychain", rehearsal, StringComparison.Ordinal);
        Assert.Contains(
            "working_directory=\"$(cd -- \"${working_directory}\" && pwd -P)\"",
            rehearsal,
            StringComparison.Ordinal);
        Assert.Contains(
            "NUGET_PACKAGES=\"${nuget_packages}\" \"${dotnet}\" restore",
            rehearsal,
            StringComparison.Ordinal);
        Assert.Contains(
            "NUGET_PACKAGES=\"${nuget_packages}\" \"${dotnet}\" package list",
            rehearsal,
            StringComparison.Ordinal);
        Assert.Contains("codesign --verify --deep --strict", rehearsal, StringComparison.Ordinal);
        Assert.Contains("spctl --assess --type execute", rehearsal, StringComparison.Ordinal);
        Assert.Contains("validate-local-release-inputs", rehearsal, StringComparison.Ordinal);
        Assert.DoesNotContain("assemble-release-evidence", rehearsal, StringComparison.Ordinal);
        Assert.DoesNotContain("validate-release-evidence", rehearsal, StringComparison.Ordinal);
        Assert.Contains("--run-id 0", rehearsal, StringComparison.Ordinal);
        Assert.DoesNotContain("gh workflow run", rehearsal, StringComparison.Ordinal);
        Assert.DoesNotContain("gh run rerun", rehearsal, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOsSigningEvidencePassesTheCertificatePrefixAsOneCodesignArgument()
    {
        var root = RepositoryRoot();
        var signingScript = File.ReadAllText(
            Path.Combine(root, "scripts", "record-macos-signing-evidence.sh"),
            Encoding.UTF8);
        var nativeVerifier = File.ReadAllText(
            Path.Combine(
                root,
                "tools",
                "Asura.SecurityCampaign",
                "NativeMacVerifier.cs"),
            Encoding.UTF8);

        Assert.Contains(
            "\"--extract-certificates=${certificate_prefix}\" \"${app}\"",
            signingScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--extract-certificates \"${certificate_prefix}\"",
            signingScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -create xml1 \"${evidence_staging}\"",
            signingScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -convert json \"${evidence_staging}\"",
            signingScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "plutil -create json \"${evidence_staging}\"",
            signingScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$\"--extract-certificates={prefix}\"",
            nativeVerifier,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"--extract-certificates\", prefix",
            nativeVerifier,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MacOsSigningEvidencePlutilSequenceProducesJson()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new DirectoryFixture();
        var staging = Path.Combine(fixture.Path, "notarization.json.staging");
        RunProcess("/usr/bin/plutil", "-create", "xml1", staging);
        RunProcess(
            "/usr/bin/plutil",
            "-insert",
            "schemaVersion",
            "-integer",
            "1",
            staging);
        RunProcess("/usr/bin/plutil", "-convert", "json", staging);

        using var document = JsonDocument.Parse(File.ReadAllBytes(staging));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void CheckedInAuthorityRegistryMatchesCurrentActionCatalog()
    {
        var root = RepositoryRoot();
        var registry = CampaignFiles.ReadJson<CampaignDefinition>(Path.Combine(
            root,
            "scripts",
            "acceptance",
            "security-campaign",
            "cases.v1.json"));
        var expected = BuiltInAgentTools.Catalog.Tools
            .Where(static tool => tool.Risk != AgentActionRisk.Observation)
            .Select(static tool => tool.Name)
            .Where(static name => name is not (BuiltInAgentTools.TerminalWait or BuiltInAgentTools.BrowserWait))
            .Select(static name => "authority." + name)
            .Order(StringComparer.Ordinal);
        var actual = registry.Cases
            .Where(static item => item.Kind == "authority")
            .Select(static item => item.Id)
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CaseIdGrammarAcceptsProductionToolUnderscores()
    {
        CampaignDefinitionValidator.ValidateCaseId(
            "authority.terminal.jump_to_rendered_history");
    }

    [Theory]
    [InlineData("authority.browser/click")]
    [InlineData("authority.Browser.click")]
    [InlineData("authority.browser click")]
    [InlineData("authority.browser@click")]
    public void CaseIdGrammarRejectsUnclosedCharacters(string value)
    {
        Assert.Throws<InvalidDataException>(
            () => CampaignDefinitionValidator.ValidateCaseId(value));
    }

    [Fact]
    public void DependencyAssemblerAcceptsOnlyCleanNamedScannerOutputs()
    {
        using var fixture = new DirectoryFixture();
        var nuget = Path.Combine(fixture.Path, "nuget.json");
        var maven = Path.Combine(fixture.Path, "maven.json");
        File.WriteAllText(nuget, "{}", new UTF8Encoding(false));
        File.WriteAllText(
            maven,
            "{\"matches\":[],\"descriptor\":{\"name\":\"grype\",\"version\":\"test\"}}",
            new UTF8Encoding(false));
        var output = Path.Combine(fixture.Path, "dependency-evidence");

        var exit = Asura.SecurityCampaign.Program.Main(
        [
            "assemble-dependency-evidence",
            "--source-commit", new string('a', 40),
            "--nuget", nuget,
            "--maven", maven,
            "--output", output,
        ]);

        Assert.Equal(0, exit);
        var evidence = CampaignFiles.ReadJson<DependencyEvidenceDocument>(
            Path.Combine(output, "evidence.json"));
        Assert.Equal("pass", evidence.Status);
        Assert.Equal(2, evidence.Inputs.Count);
    }

    [Fact]
    public void DependencyAssemblerRejectsMavenAdvisory()
    {
        using var fixture = new DirectoryFixture();
        var nuget = Path.Combine(fixture.Path, "nuget.json");
        var maven = Path.Combine(fixture.Path, "maven.json");
        File.WriteAllText(nuget, "{}", new UTF8Encoding(false));
        File.WriteAllText(
            maven,
            "{\"matches\":[{}],\"descriptor\":{\"name\":\"grype\",\"version\":\"test\"}}",
            new UTF8Encoding(false));

        var exit = Asura.SecurityCampaign.Program.Main(
        [
            "assemble-dependency-evidence",
            "--source-commit", new string('a', 40),
            "--nuget", nuget,
            "--maven", maven,
            "--output", Path.Combine(fixture.Path, "dependency-evidence"),
        ]);

        Assert.Equal(1, exit);
    }

    [Fact]
    public void ArchiveBoundaryRejectsTraversalBeforeExtraction()
    {
        using var fixture = new DirectoryFixture();
        var archivePath = Path.Combine(fixture.Path, "candidate.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("blocked");
        }

        var error = Assert.Throws<InvalidDataException>(
            () => ExtractedMacCandidate.Open(archivePath));
        Assert.Contains("unsafe entry", error.Message, StringComparison.Ordinal);
    }

    private static CampaignReceipt Receipt() => new(
        1,
        "asura-security-campaign-receipt-v1",
        "source-only",
        [
            new("macOS", "osx-arm64", "inScope", "desktop-v1-release-target"),
            new("Windows", "win-*", "notApplicable", "porting-deferred"),
            new("Linux", "linux-*", "notApplicable", "porting-deferred"),
        ],
        new(
            "https://github.com/terion-labs/asura",
            new string('a', 40),
            new string('b', 40),
            new string('0', 64),
            null,
            null,
            null,
            new string('c', 64),
            null,
            null),
        new(new string('d', 64), new string('e', 64), new string('f', 64)),
        [
            new("content.browser-continuation", "content.browser-continuation", new string('1', 64), "pass"),
            new("secrecy.app-diagnostic-adapter", "secrecy.app-diagnostic-adapter", new string('2', 64), "pass"),
            new("secrecy.cef-console-adapter", "secrecy.cef-console-adapter", new string('3', 64), "pass"),
            new("secrecy.diagnostics-zip", "secrecy.diagnostics-zip", new string('4', 64), "pass"),
            new("secrecy.persistence-sqlite", "secrecy.persistence-sqlite", new string('5', 64), "pass"),
            new("secrecy.provider-tool-continuation", "secrecy.provider-tool-continuation", new string('6', 64), "pass"),
        ],
        new(
            "7fcd5c19c8906d9fb161e377897314678db04fc4dfb22bbce735a2949eae3e66",
            2,
            5,
            5),
        null,
        null,
        [],
        null,
        ["windows-porting-deferred", "linux-porting-deferred", "release-candidate-not-evaluated"],
        "notEvaluated");

    private static void WriteTrx(string path, string name, string outcome)
    {
        var content = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="{name}" outcome="{outcome}" />
              </Results>
            </TestRun>
            """;
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private static string RunGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git test fixture.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    private static void RunProcess(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"{fileName} failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{error}");
    }

    private sealed class GitRepositoryFixture : IDisposable
    {
        private readonly DirectoryFixture _directory = new();
        private readonly DirectoryFixture _evidence = new();
        private readonly DirectoryFixture _export = new();

        public GitRepositoryFixture(bool trackRootFinderMetadata = false)
        {
            RunGit(Path, "init", "--quiet");
            RunGit(Path, "config", "user.name", "Asura test");
            RunGit(Path, "config", "user.email", "asura-test@example.invalid");
            RunGit(Path, "remote", "add", "origin", "https://github.com/terion-labs/asura.git");
            File.WriteAllText(System.IO.Path.Combine(Path, "tracked.txt"), "tracked", Encoding.UTF8);
            if (trackRootFinderMetadata)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, ".DS_Store"), "tracked finder", Encoding.UTF8);
            }
            var schema = System.IO.Path.Combine(
                Path,
                "scripts",
                "acceptance",
                "security-campaign",
                "source-seal.schema.json");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(schema)!);
            File.WriteAllText(schema, "{}", new UTF8Encoding(false));
            RunGit(Path, "add", "tracked.txt", "scripts");
            if (trackRootFinderMetadata)
            {
                RunGit(Path, "add", ".DS_Store");
            }
            RunGit(Path, "commit", "--quiet", "-m", "fixture");
            Commit = RunGit(Path, "rev-parse", "HEAD");
            Tree = RunGit(Path, "rev-parse", "HEAD^{tree}");
            RunGit(Path, "tag", Tag);
            File.Copy(
                System.IO.Path.Combine(Path, "tracked.txt"),
                System.IO.Path.Combine(ExportPath, "tracked.txt"));
            if (trackRootFinderMetadata)
            {
                File.Copy(
                    System.IO.Path.Combine(Path, ".DS_Store"),
                    System.IO.Path.Combine(ExportPath, ".DS_Store"));
            }
            var exportedSchema = System.IO.Path.Combine(
                ExportPath,
                "scripts",
                "acceptance",
                "security-campaign",
                "source-seal.schema.json");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(exportedSchema)!);
            File.Copy(schema, exportedSchema);
        }

        public string Path => _directory.Path;

        public string ExportPath => _export.Path;

        public string EvidenceParent => _evidence.Path;

        private string SealPath => System.IO.Path.Combine(EvidenceParent, "source-seal");

        private const string Tag = "v1.2.3";

        public string Commit { get; }

        private string Tree { get; }

        public ReleaseSourceSealVerification CreateSeal() =>
            ReleaseSourceSeal.Create(Path, ExportPath, Commit, Tree, Tag, SealPath);

        public ReleaseSourceSealVerification VerifySealedSource(string? buildIdentityOutput = null) =>
            ReleaseSourceSeal.Verify(
                ExportPath,
                SealPath,
                Commit,
                Tree,
                Tag,
                buildIdentityOutput);

        public void Dispose()
        {
            _export.Dispose();
            _evidence.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class DirectoryFixture : IDisposable
    {
        public DirectoryFixture()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"asura-security-campaign-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
