namespace Asura.TerminalAcceptance.Tests;

public sealed class EvidenceFilesTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateTempSubdirectory(
        "asura-terminal-evidence-tests-").FullName;

    [Fact]
    public void Writer_emits_sanitized_machine_and_human_evidence_with_a_valid_digest()
    {
        var evidence = CreateEvidence(AcceptanceStatus.Pass);
        var checks = evidence.Checks.ToArray();
        checks[0] = checks[0] with
        {
            Notes = "Synthetic <script> marker remained inert in Markdown evidence.",
        };

        var paths = EvidenceFiles.Write(_temporaryDirectory, evidence with { Checks = checks });

        Assert.Empty(EvidenceFiles.Validate(paths.Directory));
        Assert.True(File.Exists(paths.Json));
        Assert.True(File.Exists(paths.Markdown));
        Assert.True(File.Exists(paths.Digest));
        var json = File.ReadAllText(paths.Json);
        Assert.Contains("\"overallResult\": \"PASS\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("packagePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SKIP", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", File.ReadAllText(paths.Markdown), StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_rejects_tampered_evidence()
    {
        var paths = EvidenceFiles.Write(
            _temporaryDirectory,
            CreateEvidence(AcceptanceStatus.Pass));
        var json = File.ReadAllText(paths.Json).Replace(
            "\"overallResult\": \"PASS\"",
            "\"overallResult\": \"BLOCKED\"",
            StringComparison.Ordinal);
        File.WriteAllText(paths.Json, json);

        var errors = EvidenceFiles.Validate(paths.Directory);

        Assert.Contains(errors, error => error.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_duplicate_json_properties()
    {
        var paths = EvidenceFiles.Write(
            _temporaryDirectory,
            CreateEvidence(AcceptanceStatus.Pass));
        var json = File.ReadAllText(paths.Json).Replace(
            "\"schemaVersion\": 3,",
            "\"schemaVersion\": 3,\n  \"schemaVersion\": 3,",
            StringComparison.Ordinal);
        File.WriteAllText(paths.Json, json);

        var errors = EvidenceFiles.Validate(paths.Directory);

        Assert.Contains(errors, error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_rejects_a_tampered_human_readable_summary()
    {
        var paths = EvidenceFiles.Write(
            _temporaryDirectory,
            CreateEvidence(AcceptanceStatus.Pass));
        File.AppendAllText(paths.Markdown, "Tampered summary.\n");

        var errors = EvidenceFiles.Validate(paths.Directory);

        Assert.Contains(errors, error => error.Contains("evidence.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_validator_rejects_an_incomplete_matrix()
    {
        var valid = CreateEvidence(AcceptanceStatus.Pass);
        var incomplete = valid with
        {
            Checks = [.. valid.Checks.Skip(1)],
            OverallResult = AcceptanceStatus.Blocked,
        };

        var errors = EvidenceValidator.Validate(incomplete);

        Assert.Contains(errors, error => error.Contains("Expected 12", StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_validator_rejects_a_runner_inferred_pass()
    {
        var valid = CreateEvidence(AcceptanceStatus.Pass);
        var checks = valid.Checks.ToArray();
        checks[0] = checks[0] with { ObservationMode = "runner-observed-boundary" };

        var errors = EvidenceValidator.Validate(valid with { Checks = checks });

        Assert.Contains(errors, error => error.Contains("without an operator", StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_validator_rejects_pass_from_a_blocked_host_environment()
    {
        var valid = CreateEvidence(AcceptanceStatus.Pass);
        var blockedHost = valid.Host with
        {
            EnvironmentSignals = valid.Host.EnvironmentSignals with { ContainerDetected = true },
        };

        var errors = EvidenceValidator.Validate(valid with { Host = blockedHost });

        Assert.Contains(errors, error => error.Contains("blocked host", StringComparison.Ordinal));
    }

    [Fact]
    public void Overall_result_preserves_fail_and_blocked()
    {
        Assert.Equal(
            AcceptanceStatus.Pass,
            AcceptanceEvidence.ResolveOverall(CreateChecks(
                AcceptanceStatus.Pass,
                AcceptanceStatus.Pass)));
        Assert.Equal(
            AcceptanceStatus.Blocked,
            AcceptanceEvidence.ResolveOverall(CreateChecks(
                AcceptanceStatus.Pass,
                AcceptanceStatus.Blocked)));
        Assert.Equal(
            AcceptanceStatus.Fail,
            AcceptanceEvidence.ResolveOverall(CreateChecks(
                AcceptanceStatus.Blocked,
                AcceptanceStatus.Fail)));
    }

    public void Dispose() => Directory.Delete(_temporaryDirectory, recursive: true);

    private static AcceptanceEvidence CreateEvidence(AcceptanceStatus status)
    {
        var started = DateTimeOffset.Parse("2026-07-23T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var completed = started.AddMinutes(20);
        var checks = AcceptanceCatalog.All
            .Select((check, index) => new CheckObservation(
                check.Id,
                check.Title,
                status,
                index == AcceptanceCatalog.All.Count - 1 && status == AcceptanceStatus.Pass
                    ? "operator-observed+runner-verified"
                    : "operator-observed",
                "Synthetic fixture passed without sensitive payloads.",
                0,
                started.AddMinutes(10)))
            .ToArray();
        return new AcceptanceEvidence(
            AcceptanceEvidence.CurrentSchemaVersion,
            AcceptanceEvidence.CurrentEvidenceKind,
            AcceptanceEvidence.CurrentRunnerVersion,
            TargetPlatform.Windows,
            new HostIdentity(
                "win11-lab-01",
                "WIN11-LAB-01",
                "operator-01",
                "Microsoft Windows 11",
                "X64",
                "X64",
                "Windows interactive desktop",
                true,
                false,
                new HostEnvironmentSignals(false, false, false, false),
                []),
            new BackendIdentity(
                "libghostty-vt 0.1.0-dev state engine with Avalonia managed renderer",
                "Porta.Pty 1.0.7",
                "Windows ConPTY through Porta.Pty",
                PackageFingerprint.IdentitySourceDescription),
            new BuildIdentity(
                "rc-20260723-1",
                "Asura.exe",
                "1.0.0",
                100,
                new string('a', 64),
                10,
                new string('b', 64)),
            started,
            completed,
            AcceptanceEvidence.ResolveOverall(checks),
            AcceptanceEvidence.CleanExitDisposition,
            AcceptanceEvidence.StandardLimitations,
            checks);
    }

    private static CheckObservation[] CreateChecks(
        AcceptanceStatus first,
        AcceptanceStatus second) =>
        [.. AcceptanceCatalog.All
            .Select((check, index) => new CheckObservation(
                check.Id,
                check.Title,
                index == 0 ? first : index == 1 ? second : AcceptanceStatus.Pass,
                "operator-observed",
                "Concrete synthetic acceptance note.",
                0,
                DateTimeOffset.UtcNow))];
}
