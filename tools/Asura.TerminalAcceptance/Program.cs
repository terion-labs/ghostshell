using System.Diagnostics;

namespace Asura.TerminalAcceptance;

internal static class Program
{
    private const int FailedExitCode = 1;
    private const int BlockedExitCode = 2;
    private const int UsageExitCode = 64;

    public static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "run" => Run(RunOptions.Parse(args[1..])),
                "validate" => Validate(args[1..]),
                "--help" or "-h" or "help" => PrintHelpAndReturn(),
                _ => throw new UsageException("Expected the `run` or `validate` command."),
            };
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            PrintHelp();
            return UsageExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Acceptance runner failed: {exception.Message}");
            return FailedExitCode;
        }
    }

    private static int Run(RunOptions options)
    {
        EnsureCurrentPlatform(options.Platform);
        var startedAt = TimeProvider.System.GetUtcNow();
        var inspection = PackageFingerprint.Inspect(
            options.PackagePath,
            options.Platform,
            options.BuildLabel);
        var host = HostIdentity.Capture(options.Platform, options.SystemName, options.Observer);
        var observations = new List<CheckObservation>(AcceptanceCatalog.All.Count);
        var prompter = new AcceptancePrompter(Console.In, Console.Out);
        Process? process = null;
        var cleanupDisposition = "Package was not started.";
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = false;
            TryTerminateInterruptedPackage(process);
        };
        Console.CancelKeyPress += cancelHandler;

        PrintRunIdentity(options, host, inspection);

        try
        {
            var hostBoundary = GetHostBoundaryBlocker(options.Platform, host);
            if (hostBoundary is not null)
            {
                observations.Add(AcceptancePrompter.CreateAutomatic(
                    AcceptanceCatalog.All[0],
                    AcceptanceStatus.Blocked,
                    hostBoundary));
                FillRemainingBlocked(
                    observations,
                    "The named interactive-host boundary did not pass, so physical terminal behavior was not observed.");
            }
            else
            {
                var hostObservation = prompter.Prompt(AcceptanceCatalog.All[0], options.Platform);
                observations.Add(hostObservation);
                if (hostObservation.Result != AcceptanceStatus.Pass)
                {
                    FillRemainingBlocked(
                        observations,
                        "The operator did not pass the named interactive-host boundary, so later physical checks were not run.");
                }
                else
                {
                    try
                    {
                        process = StartPackage(inspection.ExecutablePath);
                    }
                    catch (Exception exception)
                    {
                        observations.Add(AcceptancePrompter.CreateAutomatic(
                            AcceptanceCatalog.All[1],
                            AcceptanceStatus.Fail,
                            $"The packaged application could not start: {exception.GetType().Name}."));
                        FillRemainingBlocked(
                            observations,
                            "The package could not start, so this physical check could not be observed.");
                        cleanupDisposition = "Package launch failed before terminal observations began.";
                    }

                    if (process is not null)
                    {
                        cleanupDisposition = "Runner still owns the packaged process.";
                        if (process.WaitForExit(milliseconds: 1_500))
                        {
                            observations.Add(AcceptancePrompter.CreateAutomatic(
                                AcceptanceCatalog.All[1],
                                AcceptanceStatus.Fail,
                                $"The packaged application exited before terminal observation with exit code {process.ExitCode}."));
                            FillRemainingBlocked(
                                observations,
                                "The package did not remain running, so this physical check could not be observed.");
                            cleanupDisposition = "Package exited before terminal observations began.";
                        }
                        else
                        {
                            PromptPackageChecks(options.Platform, process, prompter, observations);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (process is not null)
        {
            RecordOperationalFailure(
                observations,
                $"The runner stopped collecting observations after an operational failure: {exception.GetType().Name}.");
            cleanupDisposition = "Runner encountered an operational failure before cleanup.";
        }
        finally
        {
            try
            {
                if (process is not null)
                {
                    cleanupDisposition = CleanUpPackageProcess(process, observations, cleanupDisposition);
                }
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
                process?.Dispose();
            }
        }

        VerifyPackageUnchanged(options, inspection, observations);
        FillRemainingBlocked(
            observations,
            "The runner ended before this physical observation was supplied.");
        var completedAt = TimeProvider.System.GetUtcNow();
        var evidence = new AcceptanceEvidence(
            AcceptanceEvidence.CurrentSchemaVersion,
            AcceptanceEvidence.CurrentEvidenceKind,
            AcceptanceEvidence.CurrentRunnerVersion,
            options.Platform,
            host,
            inspection.Backend,
            inspection.Build,
            startedAt,
            completedAt,
            AcceptanceEvidence.ResolveOverall(observations),
            EvidenceSanitizer.SanitizeSingleLine(cleanupDisposition).Value,
            AcceptanceEvidence.StandardLimitations,
            observations);
        var paths = EvidenceFiles.Write(options.EvidenceDirectory, evidence);

        Console.WriteLine();
        Console.WriteLine($"Overall result: {FormatStatus(evidence.OverallResult)}");
        Console.WriteLine($"JSON evidence: {paths.Json}");
        Console.WriteLine($"Markdown evidence: {paths.Markdown}");
        Console.WriteLine($"Digest: {paths.Digest}");
        return ExitCodeFor(evidence.OverallResult);
    }

    private static void PromptPackageChecks(
        TargetPlatform platform,
        Process process,
        AcceptancePrompter prompter,
        List<CheckObservation> observations)
    {
        for (var index = 1; index < AcceptanceCatalog.All.Count; index++)
        {
            var check = AcceptanceCatalog.All[index];
            var isLifecycleCheck = index == AcceptanceCatalog.All.Count - 1;
            if (!isLifecycleCheck && process.HasExited)
            {
                observations.Add(AcceptancePrompter.CreateAutomatic(
                    check,
                    AcceptanceStatus.Fail,
                    "The packaged application exited unexpectedly before this observation."));
                FillRemainingBlocked(
                    observations,
                    "The package exited unexpectedly, so this physical check could not be observed.");
                return;
            }

            var observation = prompter.Prompt(check, platform);
            if (isLifecycleCheck && observation.Result == AcceptanceStatus.Pass)
            {
                if (!process.HasExited)
                {
                    observation = observation with
                    {
                        Result = AcceptanceStatus.Fail,
                        Notes = EvidenceSanitizer.SanitizeNote(
                            observation.Notes
                            + " The harness rejected PASS because the packaged parent process remained alive after the lifecycle check.").Value,
                        ObservationMode = "operator-observed+runner-verified",
                    };
                }
                else
                {
                    observation = observation with
                    {
                        ObservationMode = "operator-observed+runner-verified",
                    };
                }
            }

            observations.Add(observation);
        }
    }

    private static Process StartPackage(string executablePath)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"Starting fingerprinted package executable `{Path.GetFileName(executablePath)}`. Absolute paths are not written to evidence.");
        return Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("The packaged Asura process did not start.");
    }

    private static void PrintRunIdentity(
        RunOptions options,
        HostIdentity host,
        PackageInspection inspection)
    {
        Console.WriteLine("Asura named-host M2 terminal acceptance");
        Console.WriteLine($"Declared system: {options.SystemName}");
        Console.WriteLine($"Actual host: {host.ActualHostName}");
        Console.WriteLine($"OS: {host.OsDescription} ({host.OsArchitecture})");
        Console.WriteLine($"Desktop session: {host.DesktopSession}");
        Console.WriteLine($"Renderer: {inspection.Backend.Renderer}");
        Console.WriteLine($"PTY: {inspection.Backend.PtyAdapter}; {inspection.Backend.PtySubstrate}");
        Console.WriteLine($"Build label: {inspection.Build.BuildLabel}");
        Console.WriteLine($"Package manifest SHA-256: {inspection.Build.PackageManifestSha256}");
        foreach (var warning in host.EnvironmentWarnings)
        {
            Console.WriteLine($"WARNING: {warning}");
        }
    }

    private static string CleanUpPackageProcess(
        Process process,
        List<CheckObservation> observations,
        string currentDisposition)
    {
        if (process.HasExited)
        {
            return AcceptanceEvidence.CleanExitDisposition;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                MarkLifecycleFailure(
                    observations,
                    "The runner requested process-tree termination but the packaged parent did not exit within ten seconds.");
                return "Process-tree termination timed out; manual cleanup is required.";
            }

            return "Runner requested process-tree termination and confirmed packaged-parent exit after observations.";
        }
        catch (Exception exception)
        {
            MarkLifecycleFailure(
                observations,
                $"Runner cleanup failed with {exception.GetType().Name}; manual process cleanup is required.");
            return currentDisposition
                + $" Cleanup failed with {exception.GetType().Name}; manual cleanup is required.";
        }
    }

    private static void MarkLifecycleFailure(List<CheckObservation> observations, string note)
    {
        var lifecycleIndex = observations.FindIndex(
            observation => string.Equals(
                observation.Id,
                "pty-lifecycle",
                StringComparison.Ordinal));
        var automaticFailure = AcceptancePrompter.CreateAutomatic(
            AcceptanceCatalog.All[^1],
            AcceptanceStatus.Fail,
            note);
        if (lifecycleIndex >= 0)
        {
            observations[lifecycleIndex] = automaticFailure;
        }
        else
        {
            observations.Add(automaticFailure);
        }
    }

    private static void VerifyPackageUnchanged(
        RunOptions options,
        PackageInspection initialInspection,
        List<CheckObservation> observations)
    {
        try
        {
            var finalInspection = PackageFingerprint.Inspect(
                options.PackagePath,
                options.Platform,
                options.BuildLabel);
            if (finalInspection.Build == initialInspection.Build
                && finalInspection.Backend == initialInspection.Backend)
            {
                return;
            }

            MarkCheckFailure(
                observations,
                AcceptanceCatalog.All[1],
                "The package or declared terminal backend changed after its initial fingerprint; this run cannot identify one exact build.");
        }
        catch (Exception exception)
        {
            MarkCheckFailure(
                observations,
                AcceptanceCatalog.All[1],
                $"The package could not be fingerprinted again after observations: {exception.GetType().Name}.");
        }
    }

    private static void MarkCheckFailure(
        List<CheckObservation> observations,
        AcceptanceCheck check,
        string note)
    {
        var checkIndex = observations.FindIndex(
            observation => string.Equals(observation.Id, check.Id, StringComparison.Ordinal));
        var failure = AcceptancePrompter.CreateAutomatic(
            check,
            AcceptanceStatus.Fail,
            note);
        if (checkIndex >= 0)
        {
            observations[checkIndex] = failure;
        }
        else
        {
            observations.Add(failure);
        }
    }

    private static void TryTerminateInterruptedPackage(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 2_000);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Interrupted-run package cleanup failed with {exception.GetType().Name}; manual cleanup may be required.");
        }
    }

    private static void FillRemainingBlocked(List<CheckObservation> observations, string note)
    {
        var existingIds = observations.Select(observation => observation.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var check in AcceptanceCatalog.All)
        {
            if (!existingIds.Contains(check.Id))
            {
                observations.Add(AcceptancePrompter.CreateAutomatic(
                    check,
                    AcceptanceStatus.Blocked,
                    note));
            }
        }

        observations.Sort((left, right) =>
            IndexOfCheck(left.Id).CompareTo(IndexOfCheck(right.Id)));
    }

    private static void RecordOperationalFailure(
        List<CheckObservation> observations,
        string note)
    {
        var existingIds = observations.Select(observation => observation.Id).ToHashSet(StringComparer.Ordinal);
        var failedCheck = AcceptanceCatalog.All.FirstOrDefault(check => !existingIds.Contains(check.Id));
        if (failedCheck is not null)
        {
            observations.Add(AcceptancePrompter.CreateAutomatic(
                failedCheck,
                AcceptanceStatus.Fail,
                note));
        }
        else
        {
            MarkLifecycleFailure(observations, note);
        }

        FillRemainingBlocked(
            observations,
            "An operational runner failure prevented this physical observation.");
    }

    private static int IndexOfCheck(string id)
    {
        for (var index = 0; index < AcceptanceCatalog.All.Count; index++)
        {
            if (string.Equals(AcceptanceCatalog.All[index].Id, id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static string? GetHostBoundaryBlocker(TargetPlatform platform, HostIdentity host)
    {
        if (Console.IsInputRedirected)
        {
            return "Standard input is redirected; a live operator is required for named physical acceptance.";
        }

        if (Console.IsOutputRedirected)
        {
            return "Standard output is redirected; the live operator must be able to read the bounded checklist.";
        }

        if (!host.InteractiveUser)
        {
            return "The operating system reports a non-interactive user session.";
        }

        if (host.EnvironmentSignals.AutomationDetected)
        {
            return "An automation-environment marker is present; unattended automation cannot produce named physical acceptance.";
        }

        if (host.EnvironmentSignals.ContainerDetected)
        {
            return "A container-environment marker is present; a container cannot produce named physical acceptance.";
        }

        if (host.EnvironmentSignals.WaylandDisplayDetected)
        {
            return "Wayland or XWayland is present; the current Linux acceptance contract requires a real X11 session.";
        }

        if (host.EnvironmentSignals.UnsupportedDisplayServerDetected)
        {
            return "The active DISPLAY belongs to a virtual or unsupported X server.";
        }

        if (platform == TargetPlatform.LinuxX11
            && (!string.Equals(
                    Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                    "x11",
                    StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))))
        {
            return "Linux acceptance is not running in a confirmed X11 session with DISPLAY.";
        }

        return null;
    }

    private static void EnsureCurrentPlatform(TargetPlatform platform)
    {
        var matches = platform switch
        {
            TargetPlatform.Windows => OperatingSystem.IsWindows(),
            TargetPlatform.LinuxX11 => OperatingSystem.IsLinux(),
            _ => false,
        };
        if (!matches)
        {
            throw new UsageException(
                $"{platform} acceptance must run on that target operating system.");
        }
    }

    private static int Validate(string[] args)
    {
        if (args.Length != 1)
        {
            throw new UsageException("`validate` requires one evidence JSON file or run directory.");
        }

        var errors = EvidenceFiles.Validate(args[0]);
        if (errors.Count == 0)
        {
            Console.WriteLine("Evidence schema, matrix, sanitization, and SHA-256 sidecar are valid.");
            return 0;
        }

        Console.Error.WriteLine("Evidence validation failed:");
        foreach (var error in errors)
        {
            Console.Error.WriteLine($"- {error}");
        }

        return FailedExitCode;
    }

    private static int ExitCodeFor(AcceptanceStatus status) => status switch
    {
        AcceptanceStatus.Pass => 0,
        AcceptanceStatus.Fail => FailedExitCode,
        AcceptanceStatus.Blocked => BlockedExitCode,
        _ => FailedExitCode,
    };

    private static string FormatStatus(AcceptanceStatus status) =>
        status.ToString().ToUpperInvariant();

    private static int PrintHelpAndReturn()
    {
        PrintHelp();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Asura named-host M2 terminal acceptance

            Run on a named Windows 11 or Linux X11 interactive desktop:

              dotnet run --project tools/Asura.TerminalAcceptance -- run \
                --platform Windows|LinuxX11 \
                --system-name <release-record-host-id> \
                --observer <operator-id> \
                --build-label <release-candidate-id> \
                --package <published-package-directory-or-executable> \
                [--evidence-dir <directory>]

            Validate archived evidence and its digest:

              dotnet run --project tools/Asura.TerminalAcceptance -- validate <run-directory-or-evidence.json>

            Results and run exit codes: PASS=0, FAIL=1, BLOCKED=2. Usage errors=64.
            """);
    }
}

internal sealed record RunOptions(
    TargetPlatform Platform,
    string SystemName,
    string Observer,
    string BuildLabel,
    string PackagePath,
    string EvidenceDirectory)
{
    public static RunOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException("Run options must be supplied as `--name value` pairs.");
            }

            if (!values.TryAdd(args[index], args[index + 1]))
            {
                throw new UsageException($"Option {args[index]} was supplied more than once.");
            }
        }

        var knownOptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "--platform",
            "--system-name",
            "--observer",
            "--build-label",
            "--package",
            "--evidence-dir",
        };
        var unknownOption = values.Keys.FirstOrDefault(option => !knownOptions.Contains(option));
        if (unknownOption is not null)
        {
            throw new UsageException($"Unknown option: {unknownOption}");
        }

        var platformText = Require(values, "--platform");
        if (!Enum.TryParse<TargetPlatform>(platformText, ignoreCase: true, out var platform))
        {
            throw new UsageException("Platform must be Windows or LinuxX11.");
        }

        var systemName = RequireIdentifier(values, "--system-name");
        var observer = RequireIdentifier(values, "--observer");
        var buildLabel = RequireIdentifier(values, "--build-label");
        var package = Require(values, "--package");
        var evidenceDirectory = values.GetValueOrDefault(
            "--evidence-dir",
            Path.Combine("artifacts", "platform-acceptance"));
        return new RunOptions(
            platform,
            systemName,
            observer,
            buildLabel,
            package,
            evidenceDirectory);
    }

    private static string Require(
        IReadOnlyDictionary<string, string> values,
        string option)
    {
        if (!values.TryGetValue(option, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"Required option is missing: {option}");
        }

        return value;
    }

    private static string RequireIdentifier(
        IReadOnlyDictionary<string, string> values,
        string option)
    {
        var value = Require(values, option);
        if (!EvidenceSanitizer.IsSafeIdentifier(value))
        {
            throw new UsageException(
                $"{option} must be 3-64 ASCII letters, digits, periods, underscores, or hyphens.");
        }

        return value;
    }
}

internal sealed class UsageException : Exception
{
    public UsageException(string message)
        : base(message)
    {
    }


    public UsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
