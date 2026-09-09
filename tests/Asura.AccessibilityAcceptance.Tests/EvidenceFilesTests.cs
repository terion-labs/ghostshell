using System.Text.Json;

namespace Asura.AccessibilityAcceptance.Tests;

public sealed class EvidenceFilesTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateTempSubdirectory(
        "asura-accessibility-evidence-tests-").FullName;

    [Fact]
    public void Writer_creates_exclusive_complete_valid_evidence()
    {
        var first = EvidenceFiles.Write(_temporaryDirectory, EvidenceFixture.Valid());
        var second = EvidenceFiles.Write(_temporaryDirectory, EvidenceFixture.Valid());

        Assert.NotEqual(first.Directory, second.Directory, StringComparer.Ordinal);
        Assert.Empty(EvidenceFiles.Validate(first.Directory));
        Assert.Empty(EvidenceFiles.Validate(second.Json));
        Assert.Equal(
            ["evidence.json", "evidence.json.sha256", "evidence.md"],
            [.. Directory.EnumerateFiles(first.Directory)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void Validator_rejects_json_digest_markdown_and_extra_file_tampering()
    {
        var paths = EvidenceFiles.Write(_temporaryDirectory, EvidenceFixture.Valid());
        File.AppendAllText(paths.Json, " ");
        Assert.Contains(
            EvidenceFiles.Validate(paths.Directory),
            error => error.Contains("sha256", StringComparison.OrdinalIgnoreCase));

        paths = EvidenceFiles.Write(_temporaryDirectory, EvidenceFixture.Valid());
        File.AppendAllText(paths.Markdown, "tampered\n");
        Assert.Contains(
            EvidenceFiles.Validate(paths.Directory),
            error => error.Contains("evidence.md", StringComparison.Ordinal));

        paths = EvidenceFiles.Write(_temporaryDirectory, EvidenceFixture.Valid());
        File.WriteAllText(Path.Combine(paths.Directory, "unexpected.txt"), "unexpected");
        Assert.Contains(
            EvidenceFiles.Validate(paths.Directory),
            error => error.Contains("exactly the three", StringComparison.Ordinal));

        paths = EvidenceFiles.Write(_temporaryDirectory, EvidenceFixture.Valid());
        Directory.CreateDirectory(Path.Combine(paths.Directory, "unexpected-directory"));
        Assert.Contains(
            EvidenceFiles.Validate(paths.Directory),
            error => error.Contains("exactly the three", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_an_oversized_receipt_before_reading_its_content()
    {
        var paths = EvidenceFiles.Write(_temporaryDirectory, EvidenceFixture.Valid());
        using (var stream = new FileStream(paths.Markdown, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(1_000_001);
        }

        Assert.Contains(
            EvidenceFiles.Validate(paths.Directory),
            error => error.Contains("byte validation limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validator_rejects_a_fifo_receipt_entry_without_blocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = EvidenceFiles.Write(_temporaryDirectory, EvidenceFixture.Valid());
        File.Delete(paths.Markdown);
        using var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/usr/bin/mkfifo",
            UseShellExecute = false,
            ArgumentList = { paths.Markdown },
        });
        Assert.NotNull(mkfifo);
        Assert.True(mkfifo.WaitForExit(milliseconds: 2_000));
        Assert.Equal(0, mkfifo.ExitCode);

        var validation = Task.Run(() => EvidenceFiles.Validate(paths.Directory));
        var errors = await validation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(
            errors,
            error => error.Contains("bounded regular file", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_duplicate_and_unknown_json_properties()
    {
        var paths = EvidenceFiles.Write(_temporaryDirectory, EvidenceFixture.Valid());
        var json = File.ReadAllText(paths.Json);
        File.WriteAllText(paths.Json, json.Replace(
            "{",
            "{\n  \"schemaVersion\": 1,",
            StringComparison.Ordinal));
        Assert.Contains(
            EvidenceFiles.Validate(paths.Json),
            error => error.Contains("invalid", StringComparison.OrdinalIgnoreCase));

        paths = EvidenceFiles.Write(_temporaryDirectory, EvidenceFixture.Valid());
        json = File.ReadAllText(paths.Json);
        File.WriteAllText(paths.Json, json.Replace(
            "{",
            "{\n  \"unexpected\": true,",
            StringComparison.Ordinal));
        Assert.Contains(
            EvidenceFiles.Validate(paths.Json),
            error => error.Contains("invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Model_validator_rejects_matrix_result_and_mapping_tampering()
    {
        var valid = EvidenceFixture.Valid();
        var reordered = valid with { Checks = [.. valid.Checks.Reverse()] };
        var wrongOverall = valid with { OverallResult = AcceptanceStatus.Blocked };
        var wrongReader = valid with { ScreenReader = ScreenReaderKind.Orca };
        var incomplete = valid.Checks.ToArray();
        incomplete[0] = incomplete[0] with { Assertions = [.. incomplete[0].Assertions.Skip(1)] };

        Assert.NotEmpty(EvidenceValidator.Validate(reordered));
        Assert.NotEmpty(EvidenceValidator.Validate(wrongOverall));
        Assert.NotEmpty(EvidenceValidator.Validate(wrongReader));
        Assert.NotEmpty(EvidenceValidator.Validate(valid with { Checks = incomplete }));
    }

    [Fact]
    public void Model_validator_rejects_hidden_lifecycle_and_restoration_failures()
    {
        var valid = EvidenceFixture.Valid();
        var final = valid.Checks[^1];
        var assertions = final.Assertions.ToArray();
        var preferenceIndex = Array.FindIndex(
            assertions,
            assertion => string.Equals(assertion.Id, "preferences-restored", StringComparison.Ordinal));
        assertions[preferenceIndex] = assertions[preferenceIndex] with
        {
            Result = AcceptanceStatus.Blocked,
        };
        var checks = valid.Checks.ToArray();
        checks[^1] = final with
        {
            Assertions = assertions,
            Result = CheckObservation.ResolveResult(assertions),
        };
        var hidden = valid with
        {
            Checks = checks,
            OverallResult = AcceptanceEvidence.ResolveOverall(checks),
            PreferenceRestorationDisposition = AcceptanceEvidence.PreferencesRestoredDisposition,
        };

        Assert.Contains(
            EvidenceValidator.Validate(hidden),
            error => error.Contains("restoration", StringComparison.OrdinalIgnoreCase));

        var wrongCleanup = valid with
        {
            CleanupDisposition = "Runner terminated the package after operator observations.",
        };
        Assert.Contains(
            EvidenceValidator.Validate(wrongCleanup),
            error => error.Contains("clean exit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Model_validator_rejects_unsafe_notes_and_inactive_reader_pass()
    {
        var valid = EvidenceFixture.Valid();
        var checks = valid.Checks.ToArray();
        checks[0] = checks[0] with { Notes = "token=super-secret-value" };
        Assert.NotEmpty(EvidenceValidator.Validate(valid with { Checks = checks }));

        var inactive = valid with
        {
            AssistiveTechnology = valid.AssistiveTechnology with
            {
                StatusAfter = "NOT_EXACTLY_ONE_RUNNING",
            },
        };
        Assert.Contains(
            EvidenceValidator.Validate(inactive),
            error => error.Contains("screen reader", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Model_validator_rejects_a_hidden_reader_exit_failure_in_blocked_evidence()
    {
        var blocked = EvidenceFixture.Valid(
            TargetPlatform.Windows,
            AcceptanceStatus.Blocked);
        var checks = blocked.Checks.ToArray();
        var final = checks[^1];
        var assertions = final.Assertions.ToArray();
        var readerIndex = Array.FindIndex(
            assertions,
            assertion => string.Equals(assertion.Id, "screen-reader-remained-active", StringComparison.Ordinal));
        assertions[readerIndex] = assertions[readerIndex] with
        {
            Result = AcceptanceStatus.Pass,
        };
        checks[^1] = final with
        {
            Assertions = assertions,
            Result = CheckObservation.ResolveResult(assertions),
        };
        var tampered = blocked with
        {
            AssistiveTechnology = blocked.AssistiveTechnology with
            {
                StatusAfter = "NOT_EXACTLY_ONE_RUNNING",
            },
            Checks = checks,
            OverallResult = AcceptanceEvidence.ResolveOverall(checks),
        };

        Assert.Contains(
            EvidenceValidator.Validate(tampered),
            error => error.Contains(
                "screen-reader-active assertion",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Model_validator_accepts_honest_macos_text_scale_results()
    {
        var observedPass = EvidenceFixture.Valid(TargetPlatform.MacOS);
        var honestBlocked = EvidenceFixture.Valid(
            TargetPlatform.MacOS,
            AcceptanceStatus.Blocked);

        Assert.Empty(EvidenceValidator.Validate(observedPass));
        Assert.Empty(EvidenceValidator.Validate(honestBlocked));
    }

    [Fact]
    public void Strict_deserializer_rejects_unknown_properties_directly()
    {
        var options = EvidenceFiles.CreateSerializerOptions();
        var json = JsonSerializer.Serialize(EvidenceFixture.Valid(), options);
        var tampered = json.Replace("{", "{\"unknown\":true,", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AcceptanceEvidence>(tampered, options));
    }

    public void Dispose() => Directory.Delete(_temporaryDirectory, recursive: true);
}
