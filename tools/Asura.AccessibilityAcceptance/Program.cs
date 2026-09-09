using System.Diagnostics;

namespace Asura.AccessibilityAcceptance;

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
                "inspect-package" => InspectPackage(PackageInspectOptions.Parse(args[1..])),
                "publish-macos-package" => PublishMacOsPackage(
                    MacOsPackagePublishOptions.Parse(args[1..])),
                "--help" or "-h" or "help" => PrintHelpAndReturn(),
                _ => throw new UsageException(
                    "Expected the `run`, `validate`, `inspect-package`, or `publish-macos-package` command."),
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
            Console.Error.WriteLine($"Accessibility acceptance runner failed: {exception.Message}");
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
        if (EvidenceFiles.IsSameOrDescendantPath(
                options.EvidenceDirectory,
                inspection.PackageRoot))
        {
            throw new UsageException(
                "--evidence-dir must be outside the fingerprinted package so publishing evidence cannot mutate the accepted build.");
        }

        var host = HostIdentity.Capture(
            options.Platform,
            options.SystemName,
            options.Observer);
        var readerBefore = ScreenReaderProbe.Capture(options.Platform, options.ScreenReader);
        var readerAfter = readerBefore with
        {
            Verified = false,
            StatusCode = "NOT_RECHECKED",
        };
        var observations = new List<CheckObservation>(AcceptanceCatalog.All.Count);
        var prompter = new AcceptancePrompter(Console.In, Console.Out);
        Process? process = null;
        ProcessTreeTracker? processTree = null;
        var cleanupDisposition = "Package was not started because an acceptance boundary did not pass.";
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = false;
            TryTerminateInterruptedPackage(processTree);
        };
        Console.CancelKeyPress += cancelHandler;
        PrintRunIdentity(options, host, inspection, readerBefore);

        try
        {
            CollectObservations(
                options,
                host,
                inspection,
                readerBefore,
                prompter,
                observations,
                ref process,
                ref processTree,
                ref cleanupDisposition);
        }
        catch (Exception exception)
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
                    ApplyPackageExitBoundary(process, processTree, observations);
                    cleanupDisposition = CleanUpPackageProcess(processTree, observations);
                }
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        readerAfter = ScreenReaderProbe.Capture(options.Platform, options.ScreenReader);
        ApplyScreenReaderExitBoundary(readerBefore, readerAfter, observations);
        VerifyPackageUnchanged(options, inspection, observations);
        FillRemainingBlocked(
            observations,
            "The runner ended before this physical accessibility observation was supplied.");
        process?.Dispose();

        var completedAt = TimeProvider.System.GetUtcNow();
        var overall = AcceptanceEvidence.ResolveOverall(observations);
        var restorationDisposition = RestorationDisposition(observations);
        var technology = new AssistiveTechnologyIdentity(
            options.ScreenReader,
            readerBefore.Product,
            readerBefore.Version,
            readerBefore.IdentitySource,
            readerBefore.StatusCode,
            readerAfter.StatusCode,
            readerBefore.AccessibilityBusStatus);
        var evidence = new AcceptanceEvidence(
            AcceptanceEvidence.CurrentSchemaVersion,
            AcceptanceEvidence.CurrentEvidenceKind,
            AcceptanceEvidence.CurrentRunnerVersion,
            AcceptanceEvidence.CurrentCatalogVersion,
            AcceptanceCatalog.Digest,
            options.Platform,
            options.ScreenReader,
            host,
            technology,
            inspection.Build,
            startedAt,
            completedAt,
            overall,
            EvidenceSanitizer.SanitizeNote(cleanupDisposition).Value,
            restorationDisposition,
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

    private static int InspectPackage(PackageInspectOptions options)
    {
        var inspection = PackageFingerprint.Inspect(
            options.PackagePath,
            options.Platform,
            options.BuildLabel);
        PrintPackageInspection(inspection);
        return 0;
    }

    private static int PublishMacOsPackage(MacOsPackagePublishOptions options)
    {
        var inspection = ValidatedMacOsAppBundlePublisher.Publish(options);
        PrintPackageInspection(inspection);
        Console.WriteLine("Published the validated Asura.app candidate.");
        return 0;
    }

    private static void PrintPackageInspection(PackageInspection inspection)
    {
        Console.WriteLine($"Package kind: {inspection.Build.PackageKind}");
        Console.WriteLine($"Application identity: {inspection.Build.ApplicationIdentity}");
        Console.WriteLine($"Product version: {inspection.Build.ProductVersion}");
        Console.WriteLine($"Executable: {inspection.Build.PackageExecutable}");
        Console.WriteLine($"Executable SHA-256: {inspection.Build.ExecutableSha256}");
        Console.WriteLine($"Package files: {inspection.Build.PackageFileCount}");
        Console.WriteLine($"Package manifest SHA-256: {inspection.Build.PackageManifestSha256}");
    }

    private static void CollectObservations(
        RunOptions options,
        HostIdentity host,
        PackageInspection inspection,
        ScreenReaderSnapshot readerBefore,
        AcceptancePrompter prompter,
        List<CheckObservation> observations,
        ref Process? process,
        ref ProcessTreeTracker? processTree,
        ref string cleanupDisposition)
    {
        var hostBlocker = HostBoundaryBlocker(host);
        if (hostBlocker is not null)
        {
            observations.Add(AcceptancePrompter.CreateAutomatic(
                AcceptanceCatalog.All[0],
                AcceptanceStatus.Blocked,
                hostBlocker));
            FillRemainingBlocked(
                observations,
                "The named local interactive-host boundary did not pass, so physical accessibility behavior was not observed.");
            return;
        }

        var hostObservation = prompter.Prompt(AcceptanceCatalog.All[0], options.Platform);
        observations.Add(hostObservation);
        if (hostObservation.Result != AcceptanceStatus.Pass)
        {
            FillRemainingBlocked(
                observations,
                "The operator did not pass the named local interactive-host boundary, so later checks were not run.");
            return;
        }

        if (!readerBefore.Verified)
        {
            observations.Add(AcceptancePrompter.CreateAutomatic(
                AcceptanceCatalog.All[1],
                AcceptanceStatus.Blocked,
                $"The expected screen reader identity was not active and verified: {readerBefore.StatusCode}."));
            FillRemainingBlocked(
                observations,
                "The expected screen reader boundary did not pass, so physical screen-reader checks were not run.");
            return;
        }

        var readerObservation = prompter.Prompt(
            AcceptanceCatalog.All[1],
            options.Platform,
            new Dictionary<string, AcceptanceStatus>(StringComparer.Ordinal)
            {
                ["expected-reader-running"] = AcceptanceStatus.Pass,
            });
        observations.Add(readerObservation);
        if (readerObservation.Result != AcceptanceStatus.Pass)
        {
            FillRemainingBlocked(
                observations,
                "The operator did not pass the expected screen-reader boundary, so later checks were not run.");
            return;
        }

        try
        {
            process = StartPackage(inspection.ExecutablePath);
            processTree = ProcessTreeTracker.Attach(process);
            processTree.CaptureSnapshot();
            cleanupDisposition = "Runner still owns the packaged process.";
        }
        catch (Exception exception)
        {
            observations.Add(AcceptancePrompter.CreateAutomatic(
                AcceptanceCatalog.All[2],
                new Dictionary<string, AcceptanceStatus>(StringComparer.Ordinal)
                {
                    ["exact-package-identity"] = AcceptanceStatus.Pass,
                    ["package-launched"] = AcceptanceStatus.Fail,
                    ["package-remained-unchanged"] = AcceptanceStatus.Blocked,
                },
                $"The fingerprinted package could not start: {exception.GetType().Name}."));
            FillRemainingBlocked(
                observations,
                "The package could not start, so this physical check could not be observed.");
            cleanupDisposition = "Package launch failed before accessibility observations began.";
            return;
        }

        if (process.WaitForExit(milliseconds: 1_500))
        {
            observations.Add(AcceptancePrompter.CreateAutomatic(
                AcceptanceCatalog.All[2],
                new Dictionary<string, AcceptanceStatus>(StringComparer.Ordinal)
                {
                    ["exact-package-identity"] = AcceptanceStatus.Pass,
                    ["package-launched"] = AcceptanceStatus.Fail,
                    ["package-remained-unchanged"] = AcceptanceStatus.Blocked,
                },
                $"The packaged application exited before observation with exit code {process.ExitCode}."));
            FillRemainingBlocked(
                observations,
                "The package did not remain running, so this physical check could not be observed.");
            cleanupDisposition = "Package exited before accessibility observations began.";
            return;
        }

        processTree.CaptureSnapshot();

        observations.Add(AcceptancePrompter.CreateAutomatic(
            AcceptanceCatalog.All[2],
            new Dictionary<string, AcceptanceStatus>(StringComparer.Ordinal)
            {
                ["exact-package-identity"] = AcceptanceStatus.Pass,
                ["package-launched"] = AcceptanceStatus.Pass,
                ["package-remained-unchanged"] = AcceptanceStatus.Blocked,
            },
            "The exact fingerprinted package started and remained live for operator observation."));

        for (var index = 3; index < AcceptanceCatalog.All.Count; index++)
        {
            var check = AcceptanceCatalog.All[index];
            var finalCheck = index == AcceptanceCatalog.All.Count - 1;
            if (!finalCheck && process.HasExited)
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

            processTree.CaptureSnapshot();
            var observation = prompter.Prompt(check, options.Platform);
            observations.Add(observation);
            processTree.CaptureSnapshot();
        }
    }

    private static Process StartPackage(string executablePath)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"Starting fingerprinted `{Path.GetFileName(executablePath)}`. Absolute paths are never written to evidence.");
        return Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("The packaged Asura process did not start.");
    }

    internal static void ApplyPackageExitBoundary(
        Process process,
        ProcessTreeTracker? processTree,
        List<CheckObservation> observations)
    {
        if (observations.Count != AcceptanceCatalog.All.Count)
        {
            return;
        }

        var parentExitedNormally = false;
        var allCapturedExited = false;
        string treeNote;
        try
        {
            if (processTree is null)
            {
                treeNote = "The runner did not capture a stable package process-tree identity.";
            }
            else
            {
                processTree.StopSampling();
                processTree.CaptureSnapshot();
                // Reap the packaged parent before probing retained identities. On Unix a
                // successfully exited child can remain observable as a zombie until the
                // owning Process performs waitpid(2), which would otherwise make the exact
                // identity tracker report a false live-process failure.
                parentExitedNormally = process.WaitForExit(milliseconds: 2_000)
                    && process.ExitCode == 0;
                allCapturedExited = processTree.WaitForAllExited(TimeSpan.FromSeconds(2));
                treeNote = allCapturedExited
                    ? $"The runner confirmed all {processTree.CapturedCount} captured package process identities exited."
                    : $"At least one of {processTree.CapturedCount} captured package process identities remained live.";
            }
        }
        catch (Exception exception) when (exception is ProcessTreeProbeException
            or InvalidOperationException)
        {
            treeNote = $"Package process-tree verification failed with {exception.GetType().Name}.";
        }

        var boundaryPassed = parentExitedNormally && allCapturedExited;
        var current = AssertionResult(
            observations,
            "terminal-quick-terminal-cleanup",
            "package-exited");
        var status = AcceptanceBoundary.Constrain(current, boundaryPassed);
        ReplaceAssertion(
            observations,
            "terminal-quick-terminal-cleanup",
            "package-exited",
            status,
            boundaryPassed
                ? $"The runner confirmed normal packaged-parent exit. {treeNote}"
                : $"The runner rejected package-exit acceptance because the parent did not exit normally or the tracked process tree was not gone. {treeNote}");
    }

    private static void ApplyScreenReaderExitBoundary(
        ScreenReaderSnapshot before,
        ScreenReaderSnapshot after,
        List<CheckObservation> observations)
    {
        if (observations.Count != AcceptanceCatalog.All.Count)
        {
            return;
        }

        var sameIdentity = before.Verified
            && after.Verified
            && before.Kind == after.Kind
            && string.Equals(before.Product, after.Product, StringComparison.Ordinal)
            && string.Equals(before.Version, after.Version, StringComparison.Ordinal);
        var current = AssertionResult(
            observations,
            "terminal-quick-terminal-cleanup",
            "screen-reader-remained-active");
        ReplaceAssertion(
            observations,
            "terminal-quick-terminal-cleanup",
            "screen-reader-remained-active",
            AcceptanceBoundary.Constrain(current, sameIdentity),
            sameIdentity
                ? "The runner reverified the same screen-reader identity after package cleanup."
                : "The runner could not reverify the same screen-reader identity after observations.");
    }

    private static string CleanUpPackageProcess(
        ProcessTreeTracker? processTree,
        List<CheckObservation> observations)
    {
        if (processTree is null)
        {
            ReplaceAssertion(
                observations,
                "terminal-quick-terminal-cleanup",
                "package-exited",
                AcceptanceStatus.Fail,
                "Stable process-tree identity was unavailable; automatic cleanup was refused.");
            return "Stable process-tree identity was unavailable; manual package cleanup is required.";
        }

        try
        {
            var cleanup = processTree.TerminateAndWait(TimeSpan.FromSeconds(10));
            if (!cleanup.AllCapturedExited)
            {
                ReplaceAssertion(
                    observations,
                    "terminal-quick-terminal-cleanup",
                    "package-exited",
                    AcceptanceStatus.Fail,
                    "Tracked process cleanup timed out; manual package cleanup is required.");
                return "Tracked process cleanup timed out; manual package cleanup is required.";
            }

            return cleanup.TerminationAttempted
                ? $"Runner attempted exact cleanup and reverified {cleanup.CapturedCount} captured package process identities after observations."
                : AcceptanceEvidence.CleanExitDisposition;
        }
        catch (Exception exception)
        {
            ReplaceAssertion(
                observations,
                "terminal-quick-terminal-cleanup",
                "package-exited",
                AcceptanceStatus.Fail,
                $"Package cleanup failed with {exception.GetType().Name}; manual cleanup is required.");
            return $"Package cleanup failed with {exception.GetType().Name}; manual cleanup is required.";
        }
    }

    private static void VerifyPackageUnchanged(
        RunOptions options,
        PackageInspection initial,
        List<CheckObservation> observations)
    {
        if (observations.Count < 3)
        {
            return;
        }

        try
        {
            var final = PackageFingerprint.Inspect(
                options.PackagePath,
                options.Platform,
                options.BuildLabel);
            var unchanged = final.Build == initial.Build;
            ReplaceAssertion(
                observations,
                "fingerprinted-package",
                "package-remained-unchanged",
                unchanged ? AcceptanceStatus.Pass : AcceptanceStatus.Fail,
                unchanged
                    ? "The complete package fingerprint matched after cleanup."
                    : "The package fingerprint changed during accessibility acceptance.");
        }
        catch (Exception exception)
        {
            ReplaceAssertion(
                observations,
                "fingerprinted-package",
                "package-remained-unchanged",
                AcceptanceStatus.Fail,
                $"The post-run package fingerprint failed with {exception.GetType().Name}.");
        }
    }

    private static void ReplaceAssertion(
        List<CheckObservation> observations,
        string checkId,
        string assertionId,
        AcceptanceStatus status,
        string note)
    {
        var index = observations.FindIndex(check =>
            string.Equals(check.Id, checkId, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        var check = observations[index];
        var assertionIndex = check.Assertions
            .Select((assertion, position) => (assertion, position))
            .FirstOrDefault(item =>
                string.Equals(item.assertion.Id, assertionId, StringComparison.Ordinal))
            .position;
        if (assertionIndex < 0
            || assertionIndex >= check.Assertions.Count
            || !string.Equals(
                check.Assertions[assertionIndex].Id,
                assertionId,
                StringComparison.Ordinal))
        {
            return;
        }

        var assertions = check.Assertions.ToArray();
        assertions[assertionIndex] = assertions[assertionIndex] with { Result = status };
        var sanitized = EvidenceSanitizer.SanitizeNote($"{check.Notes} {note}");
        observations[index] = check with
        {
            Assertions = assertions,
            Result = CheckObservation.ResolveResult(assertions),
            ObservationMode = AcceptanceBoundary.AddRunnerObservation(
                check.ObservationMode),
            Notes = sanitized.Value,
            RedactionsApplied = check.RedactionsApplied + sanitized.RedactionsApplied,
        };
    }

    private static AcceptanceStatus AssertionResult(
        IReadOnlyList<CheckObservation> observations,
        string checkId,
        string assertionId)
    {
        var check = observations.First(item =>
            string.Equals(item.Id, checkId, StringComparison.Ordinal));
        return check.Assertions.First(item =>
            string.Equals(item.Id, assertionId, StringComparison.Ordinal)).Result;
    }

    private static string? HostBoundaryBlocker(HostIdentity host)
    {
        if (!host.InteractiveUser)
        {
            return "The operating system did not report an interactive user session.";
        }

        return host.EnvironmentSignals.BlocksNamedHostAcceptance
            ? "The runner detected a redirected, automated, containerized, remote, virtual-display, or unsupported desktop boundary."
            : null;
    }

    private static string RestorationDisposition(IReadOnlyList<CheckObservation> observations)
    {
        var final = observations.First(check => string.Equals(
            check.Id,
            "terminal-quick-terminal-cleanup",
            StringComparison.Ordinal));
        return final.Assertions.Single(assertion => string.Equals(
            assertion.Id,
            "preferences-restored",
            StringComparison.Ordinal)).Result switch
        {
            AcceptanceStatus.Pass => AcceptanceEvidence.PreferencesRestoredDisposition,
            AcceptanceStatus.Fail => AcceptanceEvidence.PreferencesNotRestoredDisposition,
            _ => AcceptanceEvidence.PreferencesUnconfirmedDisposition,
        };
    }

    private static void RecordOperationalFailure(
        List<CheckObservation> observations,
        string note)
    {
        if (observations.Count >= AcceptanceCatalog.All.Count)
        {
            var check = AcceptanceCatalog.All[^1];
            foreach (var assertion in check.Assertions)
            {
                ReplaceAssertion(
                    observations,
                    check.Id,
                    assertion.Id,
                    AcceptanceStatus.Fail,
                    note);
            }

            return;
        }

        observations.Add(AcceptancePrompter.CreateAutomatic(
            AcceptanceCatalog.All[observations.Count],
            AcceptanceStatus.Fail,
            note));
        FillRemainingBlocked(observations, "An operational failure prevented this observation.");
    }

    private static void FillRemainingBlocked(
        List<CheckObservation> observations,
        string note)
    {
        while (observations.Count < AcceptanceCatalog.All.Count)
        {
            observations.Add(AcceptancePrompter.CreateAutomatic(
                AcceptanceCatalog.All[observations.Count],
                AcceptanceStatus.Blocked,
                note));
        }
    }

    private static void PrintRunIdentity(
        RunOptions options,
        HostIdentity host,
        PackageInspection inspection,
        ScreenReaderSnapshot screenReader)
    {
        Console.WriteLine("Asura named-host M1 accessibility acceptance");
        Console.WriteLine($"Declared system: {options.SystemName}");
        Console.WriteLine($"Host fingerprint: {host.HostFingerprint}");
        Console.WriteLine($"OS: {host.OsDescription} ({host.OsArchitecture})");
        Console.WriteLine($"Desktop session: {host.DesktopSession}");
        Console.WriteLine($"Screen reader: {options.ScreenReader} ({screenReader.StatusCode})");
        Console.WriteLine($"Build label: {inspection.Build.BuildLabel}");
        Console.WriteLine($"Package manifest SHA-256: {inspection.Build.PackageManifestSha256}");
        Console.WriteLine($"Catalog SHA-256: {AcceptanceCatalog.Digest}");
        foreach (var warning in host.EnvironmentWarnings)
        {
            Console.WriteLine($"WARNING: {warning}");
        }
    }

    private static void TryTerminateInterruptedPackage(ProcessTreeTracker? processTree)
    {
        try
        {
            processTree?.TerminateAndWait(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is ProcessTreeProbeException
            or InvalidOperationException
            or NotSupportedException)
        {
            // Ctrl+C is already terminating the runner. Identity uncertainty deliberately
            // leaves the package for manual cleanup rather than signaling a numeric PID.
        }
    }

    private static int Validate(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1)
        {
            throw new UsageException("validate requires one evidence JSON or directory path.");
        }

        var errors = EvidenceFiles.Validate(arguments[0]);
        if (errors.Count == 0)
        {
            Console.WriteLine("Accessibility acceptance evidence is valid.");
            return 0;
        }

        foreach (var error in errors)
        {
            Console.Error.WriteLine(error);
        }

        return FailedExitCode;
    }

    private static void EnsureCurrentPlatform(TargetPlatform target)
    {
        var matches = target switch
        {
            TargetPlatform.MacOS => OperatingSystem.IsMacOS(),
            TargetPlatform.Windows => OperatingSystem.IsWindows(),
            TargetPlatform.LinuxX11 => OperatingSystem.IsLinux(),
            _ => false,
        };
        if (!matches)
        {
            throw new UsageException(
                $"The {target} acceptance runner must execute on that target platform.");
        }
    }

    private static int PrintHelpAndReturn()
    {
        PrintHelp();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Usage:
              Asura.AccessibilityAcceptance run \
                --platform <MacOS|Windows|LinuxX11> \
                --screen-reader <VoiceOver|Narrator|Orca> \
                --system-name <named-host-id> \
                --observer <operator-id> \
                --build-label <release-label> \
                --package <package-path> \
                [--evidence-dir <output-root>]

              Asura.AccessibilityAcceptance validate <evidence.json-or-directory>

              Asura.AccessibilityAcceptance inspect-package \
                --platform <MacOS|Windows|LinuxX11> \
                --build-label <release-label> \
                --package <package-path>

              Asura.AccessibilityAcceptance publish-macos-package \
                --build-label <release-label> \
                --package <private-candidate/Asura.app> \
                --output <Asura.app>

            Fixed mappings: MacOS/VoiceOver, Windows/Narrator, LinuxX11/Orca.
            Exit codes: 0 PASS, 1 FAIL, 2 BLOCKED, 64 usage error.
            """);
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
}
