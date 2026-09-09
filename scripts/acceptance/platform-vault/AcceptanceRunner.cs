using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

namespace Asura.PlatformVaultAcceptance;

internal sealed class AcceptanceRunner(
    string repositoryRoot,
    string dotnetPath)
{
    public const string TestId =
        "Asura.Infrastructure.Tests.PlatformSecretVaultIntegrationTests.Native_vault_round_trips_only_when_explicitly_enabled";

    private const string EnabledEnvironmentVariable = "ASURA_RUN_SECRET_VAULT_INTEGRATION";
    private const string RunIdEnvironmentVariable = "ASURA_SECRET_VAULT_ACCEPTANCE_RUN_ID";
    private const string SecretReferenceEnvironmentVariable = "ASURA_SECRET_VAULT_ACCEPTANCE_REFERENCE";
    private const string RootEnvironmentVariable = "ASURA_SECRET_VAULT_ACCEPTANCE_ROOT";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(3);

    private readonly string _repositoryRoot = Path.GetFullPath(repositoryRoot);
    private readonly string _dotnetPath = Path.GetFullPath(dotnetPath);

    public async Task<AcceptanceReceipt> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var host = DetectHost();
        var provider = ResolveProvider(host.OsFamily);

        if (host.OsFamily == "Unsupported")
        {
            return CreateReceipt(
                "BLOCKED",
                "unsupported_platform",
                startedAt,
                stopwatch,
                host,
                null,
                provider,
                "NOT_RUN",
                null,
                0,
                new CleanupReceipt("NOT_STARTED", null));
        }

        var sdkVersion = await ReadDotnetSdkVersionAsync(cancellationToken).ConfigureAwait(false);
        if (sdkVersion is null)
        {
            return CreateReceipt(
                "BLOCKED",
                "dotnet_unavailable",
                startedAt,
                stopwatch,
                host,
                null,
                provider,
                "NOT_RUN",
                null,
                0,
                new CleanupReceipt("NOT_STARTED", null));
        }

        if (host.OsFamily == "Linux" && !LinuxSecretServicePrerequisitesArePresent())
        {
            return CreateReceipt(
                "BLOCKED",
                "provider_prerequisite_missing",
                startedAt,
                stopwatch,
                host,
                sdkVersion,
                provider,
                "NOT_RUN",
                null,
                0,
                new CleanupReceipt("NOT_STARTED", null));
        }

        var run = IsolatedVaultRun.Create();
        var preserveRunForRecovery = true;
        try
        {
            var execution = await ExecuteTestAsync(run, cancellationToken).ConfigureAwait(false);
            var state = AcceptanceState.TryRead(run.StatePath, run.RunId);
            var cleanup = ResolveCleanup(execution.Outcome, state, run);
            preserveRunForRecovery = cleanup.State == "RECOVERY_REQUIRED";
            var classification = Classify(execution, cleanup);
            return CreateReceipt(
                classification.Status,
                classification.Reason,
                startedAt,
                stopwatch,
                host,
                sdkVersion,
                provider,
                execution.Outcome,
                execution.ExitCode,
                execution.DurationMilliseconds,
                cleanup);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Once an isolated run exists, an unexpected runner failure must retain the
            // exact recovery identifiers. A generic failure receipt would strand a
            // possibly-created credential without telling the operator what to remove.
            var state = AcceptanceState.TryRead(run.StatePath, run.RunId);
            var cleanup = ResolveCleanup("FAILED", state, run);
            preserveRunForRecovery = cleanup.State == "RECOVERY_REQUIRED";
            return CreateReceipt(
                "FAIL",
                "runner_failed",
                startedAt,
                stopwatch,
                host,
                sdkVersion,
                provider,
                "NOT_RUN",
                null,
                0,
                cleanup);
        }
        finally
        {
            if (!preserveRunForRecovery)
            {
                TryDeleteIsolatedRun(run.RootPath);
            }
        }
    }

    private async Task<TestExecution> ExecuteTestAsync(
        IsolatedVaultRun run,
        CancellationToken cancellationToken)
    {
        var project = Path.Combine(
            _repositoryRoot,
            "tests",
            "Asura.Infrastructure.Tests",
            "Asura.Infrastructure.Tests.csproj");
        if (!File.Exists(project))
        {
            return new TestExecution("NOT_RUN", null, 0);
        }

        var trxDirectory = Path.Combine(run.RootPath, "trx");
        Directory.CreateDirectory(trxDirectory);
        var trxPath = Path.Combine(trxDirectory, "platform-vault.trx");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _dotnetPath,
                    WorkingDirectory = _repositoryRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var argument in new[]
                     {
                         "test",
                         project,
                         "--filter",
                         $"FullyQualifiedName={TestId}",
                         "--logger",
                         "trx;LogFileName=platform-vault.trx",
                         "--results-directory",
                         trxDirectory,
                     })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.Environment[EnabledEnvironmentVariable] = "1";
            process.StartInfo.Environment[RunIdEnvironmentVariable] = run.RunId;
            process.StartInfo.Environment[SecretReferenceEnvironmentVariable] = run.SecretReference;
            process.StartInfo.Environment[RootEnvironmentVariable] = run.RootPath;
            process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";

            var stopwatch = Stopwatch.StartNew();
            if (!process.Start())
            {
                return new TestExecution("NOT_RUN", null, stopwatch.ElapsedMilliseconds);
            }

            // Streams are deliberately drained and discarded. The receipt never embeds test output.
            var standardOutput = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            var standardError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TestTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            stopwatch.Stop();
            if (timedOut)
            {
                return new TestExecution("FAILED", process.ExitCode, stopwatch.ElapsedMilliseconds);
            }

            var result = ReadTrxResult(trxPath);
            return new TestExecution(
                result.Outcome,
                process.ExitCode,
                result.DurationMilliseconds ?? stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new TestExecution("NOT_RUN", null, 0);
        }
        finally
        {
            // TRX may contain framework diagnostics. It is parsed in memory and never retained as evidence.
            TryDeleteIsolatedRun(trxDirectory);
        }
    }

    private async Task<string?> ReadDotnetSdkVersionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_dotnetPath))
        {
            return null;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _dotnetPath,
                WorkingDirectory = _repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--version");
        if (!process.Start())
        {
            return null;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var version = output.Trim();
        return process.ExitCode == 0 && IsSanitizedVersion(version) ? version : null;
    }

    internal static TrxResult ReadTrxResult(string trxPath)
    {
        if (!File.Exists(trxPath))
        {
            return new TrxResult("NOT_RUN", null);
        }

        try
        {
            var document = XDocument.Load(trxPath, LoadOptions.None);
            var results = document
                .Descendants()
                .Where(element => element.Name.LocalName == "UnitTestResult")
                .ToArray();
            if (results is not [var result]
                || !string.Equals(
                    (string?)result.Attribute("testName"),
                    TestId,
                    StringComparison.Ordinal))
            {
                return new TrxResult("NOT_RUN", null);
            }

            var outcome = ((string?)result?.Attribute("outcome")) switch
            {
                "Passed" => "PASSED",
                "Failed" => "FAILED",
                "NotExecuted" => "SKIPPED",
                _ => "NOT_RUN",
            };
            var duration = TimeSpan.TryParse(
                (string?)result?.Attribute("duration"),
                CultureInfo.InvariantCulture,
                out var parsedDuration)
                    ? (long?)parsedDuration.TotalMilliseconds
                    : null;
            return new TrxResult(outcome, duration);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Xml.XmlException)
        {
            return new TrxResult("NOT_RUN", null);
        }
    }

    internal static CleanupReceipt ResolveCleanup(
        string testOutcome,
        AcceptanceState? state,
        IsolatedVaultRun run)
    {
        if (testOutcome == "SKIPPED")
        {
            return new CleanupReceipt("NOT_STARTED", null);
        }

        if (state?.Phase == "DELETED")
        {
            return new CleanupReceipt("CONFIRMED", null);
        }

        if (testOutcome == "NOT_RUN" && state is null)
        {
            return new CleanupReceipt("NOT_STARTED", null);
        }

        return new CleanupReceipt(
            "RECOVERY_REQUIRED",
            new RecoveryReceipt(run.ServiceName, run.SecretReference, run.MetadataPath));
    }

    private static RunClassification Classify(TestExecution execution, CleanupReceipt cleanup)
    {
        if (execution.Outcome == "SKIPPED")
        {
            return new RunClassification("BLOCKED", "test_skipped");
        }

        if (execution.Outcome == "PASSED" && execution.ExitCode == 0)
        {
            return cleanup.State == "CONFIRMED"
                ? new RunClassification("PASS", "accepted")
                : new RunClassification("FAIL", "cleanup_unconfirmed");
        }

        return execution.Outcome == "FAILED"
            ? new RunClassification("FAIL", "test_failed")
            : new RunClassification("FAIL", "test_execution_failed");
    }

    private static AcceptanceReceipt CreateReceipt(
        string status,
        string reason,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        HostReceipt host,
        string? sdkVersion,
        ProviderReceipt provider,
        string testOutcome,
        int? exitCode,
        long testDurationMilliseconds,
        CleanupReceipt cleanup)
    {
        stopwatch.Stop();
        return new AcceptanceReceipt(
            1,
            status,
            reason,
            startedAt.ToUniversalTime().ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            stopwatch.ElapsedMilliseconds,
            host,
            new DotnetReceipt(sdkVersion),
            provider,
            new TestReceipt(TestId, testOutcome, exitCode, testDurationMilliseconds),
            cleanup);
    }

    private static HostReceipt DetectHost()
    {
        var family = OperatingSystem.IsMacOS()
            ? "macOS"
            : OperatingSystem.IsWindows()
                ? "Windows"
                : OperatingSystem.IsLinux()
                    ? "Linux"
                    : "Unsupported";
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "unknown",
        };
        var description = RuntimeInformation.OSDescription;
        if (description.Length > 256 || description.Any(char.IsControl))
        {
            description = family;
        }

        return new HostReceipt(family, description, architecture);
    }

    private static ProviderReceipt ResolveProvider(string osFamily) => osFamily switch
    {
        "macOS" => new ProviderReceipt("macos-keychain", "os-protected-persistent"),
        "Windows" => new ProviderReceipt("windows-dpapi", "os-protected-persistent"),
        "Linux" => new ProviderReceipt("linux-secret-service", "os-protected-persistent"),
        _ => new ProviderReceipt("unavailable", "none"),
    };

    private static bool LinuxSecretServicePrerequisitesArePresent()
    {
        var hasSession = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
        return hasSession && FindExecutable("secret-tool") is not null;
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private static bool IsSanitizedVersion(string value)
    {
        if (value.Length is < 1 or > 64 || value.Any(char.IsControl))
        {
            return false;
        }

        return value.Split('.', StringSplitOptions.None) is [var major, var minor, var patch]
            && int.TryParse(major, out _)
            && int.TryParse(minor, out _)
            && int.TryParse(patch, out _);
    }

    private static void TryDeleteIsolatedRun(string rootPath)
    {
        try
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // The receipt has no secret material; a transient artifact cleanup failure is non-authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // The receipt has no secret material; a transient artifact cleanup failure is non-authoritative.
        }
    }

    internal static AcceptanceReceipt CreateRunnerFailureReceipt()
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var host = DetectHost();
        return new AcceptanceReceipt(
            1,
            "FAIL",
            "runner_failed",
            now,
            now,
            0,
            host,
            new DotnetReceipt(null),
            ResolveProvider(host.OsFamily),
            new TestReceipt(TestId, "NOT_RUN", null, 0),
            new CleanupReceipt("NOT_STARTED", null));
    }

    private sealed record TestExecution(
        string Outcome,
        int? ExitCode,
        long DurationMilliseconds);

    internal sealed record TrxResult(string Outcome, long? DurationMilliseconds);

    private sealed record RunClassification(string Status, string Reason);
}

internal sealed record IsolatedVaultRun(
    string RunId,
    string SecretReference,
    string RootPath,
    string MetadataPath,
    string StatePath,
    string ServiceName)
{
    public const string RecoveryManifestFileName = "recovery.json";

    public static IsolatedVaultRun Create()
    {
        var runId = Guid.NewGuid().ToString("N");
        var secretReference = Guid.NewGuid().ToString("N");
        var rootPath = Path.Combine(Path.GetTempPath(), $"asura-platform-vault-{runId}");
        Directory.CreateDirectory(rootPath);
        var run = new IsolatedVaultRun(
            runId,
            secretReference,
            rootPath,
            Path.Combine(rootPath, "metadata"),
            Path.Combine(rootPath, "state.json"),
            $"sh.asura.integration-tests.{runId}");
        WriteRecoveryManifest(run);
        return run;
    }

    private static void WriteRecoveryManifest(IsolatedVaultRun run)
    {
        var path = Path.Combine(run.RootPath, RecoveryManifestFileName);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(
            new RecoveryReceipt(run.ServiceName, run.SecretReference, run.MetadataPath),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed record AcceptanceState(int SchemaVersion, string RunId, string Phase)
{
    public static AcceptanceState? TryRead(string path, string expectedRunId)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<AcceptanceState>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return state is { SchemaVersion: 1 }
                && string.Equals(state.RunId, expectedRunId, StringComparison.Ordinal)
                && state.Phase is "INITIALIZED" or "CREATED" or "DELETED" or "CLEANUP_FAILED"
                    ? state
                    : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
