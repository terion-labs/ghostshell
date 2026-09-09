using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Asura.AccessibilityAcceptance;

namespace Asura.SoakAcceptance;

internal static class Program
{
    private const string RunnerVersion = "1.0.0";

    public static int Main(string[] args)
    {
        try
        {
            if (args is ["validate-policy", var policyPath])
            {
                var loaded = SoakPolicyFiles.Load(policyPath);
                Console.WriteLine($"Valid policy {loaded.Policy.PolicyVersion} ({loaded.Sha256}).");
                return 0;
            }

            var options = RunOptions.Parse(args);
            return Run(options);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or InvalidDataException
                                          or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Soak acceptance failed closed: {exception.Message}");
            return 2;
        }
    }

    private static int Run(RunOptions options)
    {
        EnsureMacOsArm64InteractiveHost();
        var loadedPolicy = SoakPolicyFiles.Load(options.PolicyPath);
        var inspection = PackageFingerprint.Inspect(options.PackagePath, TargetPlatform.MacOS, options.BuildLabel);
        var host = CaptureHost(loadedPolicy.Policy.ReferenceConfigurationId);
        var started = DateTimeOffset.UtcNow;
        var observations = new List<ScenarioObservation>(SoakCatalog.Scenarios.Count);

        Console.WriteLine("Asura macOS arm64 release soak acceptance");
        Console.WriteLine($"Package manifest: {inspection.Build.PackageManifestSha256}");
        Console.WriteLine($"Policy: {loadedPolicy.Policy.PolicyVersion} ({loadedPolicy.Sha256})");
        Console.WriteLine("The runner records counters and stable failure codes only. Do not paste terminal, provider, MCP, command, path, URL, or credential content.");

        for (var index = 0; index < SoakCatalog.Scenarios.Count; index++)
        {
            var scenario = SoakCatalog.Scenarios[index];
            var budget = loadedPolicy.Policy.Scenarios[index];
            observations.Add(RunScenario(inspection.ExecutablePath, scenario, budget));
        }

        var finalInspection = PackageFingerprint.Inspect(options.PackagePath, TargetPlatform.MacOS, options.BuildLabel);
        var unchanged = inspection.Build == finalInspection.Build;
        var overall = SoakEvidenceFiles.ResolveOverall(observations, unchanged);
        var receipt = new SoakReceipt(
            1,
            "asura-macos-arm64-release-soak",
            RunnerVersion,
            SoakCatalog.Sha256,
            loadedPolicy.Sha256,
            loadedPolicy.Policy,
            host,
            inspection.Build,
            started,
            DateTimeOffset.UtcNow,
            overall,
            unchanged,
            observations);
        var paths = SoakEvidenceFiles.Write(options.EvidenceDirectory, receipt);
        Console.WriteLine($"Receipt: {paths.Directory}");
        Console.WriteLine($"Result: {overall}");
        return overall == SoakStatus.Pass ? 0 : 1;
    }

    private static ScenarioObservation RunScenario(
        string executable,
        SoakScenario scenario,
        ScenarioBudget budget)
    {
        Console.WriteLine();
        Console.WriteLine($"[{scenario.Id}] {scenario.Title}");
        Console.WriteLine(scenario.Instructions);
        Console.WriteLine($"v1 budget: {budget.DurationSeconds}s, {budget.RequiredLoad} {budget.LoadUnit}, RSS growth <= {budget.MaximumWorkingSetGrowthBytes} bytes, failures <= {budget.MaximumFailures}, cleanup <= {budget.CleanupTimeoutSeconds}s with <= {budget.MaximumLiveProcessesAfterCleanup} live captured processes.");
        RequireEnter("Press Enter to launch the exact package and begin.");
        var started = DateTimeOffset.UtcNow;
        var samples = new List<ResourceSample>();
        var cleanupPassed = false;
        var capturedCount = 0;
        var abruptExits = 0;
        var failureCodes = new List<string>();

        try
        {
            var firstDuration = scenario.ExpectedAbruptExits == 1
                ? TimeSpan.FromSeconds(budget.DurationSeconds / 2)
                : TimeSpan.FromSeconds(budget.DurationSeconds);
            using (var process = StartPackage(executable))
            using (var tracker = ProcessTreeTracker.Attach(process))
            {
                SampleFor(process, tracker, firstDuration, samples);
                capturedCount += tracker.CapturedCount;
                if (scenario.ExpectedAbruptExits == 1)
                {
                    tracker.CaptureSnapshot();
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(10_000);
                    abruptExits++;
                    if (!tracker.WaitForAllExited(TimeSpan.FromSeconds(budget.CleanupTimeoutSeconds)))
                    {
                        failureCodes.Add("crash-phase-process-retained");
                    }
                }
                else
                {
                    RequireEnter("Close Asura normally, then press Enter.");
                    tracker.CaptureSnapshot();
                    cleanupPassed = tracker.WaitForAllExited(TimeSpan.FromSeconds(budget.CleanupTimeoutSeconds));
                    if (!cleanupPassed)
                    {
                        failureCodes.Add("captured-process-retained");
                    }
                }
            }

            if (scenario.ExpectedAbruptExits == 1)
            {
                RequireEnter("The runner performed the one expected abrupt exit. Press Enter to relaunch and inspect recovery.");
                using var recoveryProcess = StartPackage(executable);
                using var recoveryTracker = ProcessTreeTracker.Attach(recoveryProcess);
                SampleFor(recoveryProcess, recoveryTracker, TimeSpan.FromSeconds(budget.DurationSeconds - budget.DurationSeconds / 2), samples);
                RequireEnter("Confirm recovery did not fabricate success or widen authority. Close Asura normally, then press Enter.");
                recoveryTracker.CaptureSnapshot();
                capturedCount += recoveryTracker.CapturedCount;
                cleanupPassed = recoveryTracker.WaitForAllExited(TimeSpan.FromSeconds(budget.CleanupTimeoutSeconds));
                if (!cleanupPassed)
                {
                    failureCodes.Add("recovery-process-retained");
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or ProcessTreeProbeException)
        {
            failureCodes.Add(exception is ProcessTreeProbeException
                ? "process-observation-failed"
                : "package-lifecycle-failed");
        }

        var completedLoad = PromptNonNegativeInt($"Completed {budget.LoadUnit}: ");
        var observedFailures = PromptNonNegativeInt("Observed product failures: ");
        var operatorResult = PromptResult();
        var resources = Summarize(samples, capturedCount);
        EvaluateBudget(scenario, budget, completedLoad, observedFailures, abruptExits, cleanupPassed, resources, failureCodes);
        var machineResult = failureCodes.Count > 0 || operatorResult == SoakStatus.Fail
            ? SoakStatus.Fail
            : operatorResult == SoakStatus.Pass ? SoakStatus.Pass : SoakStatus.Blocked;
        return new ScenarioObservation(
            scenario.Id,
            started,
            DateTimeOffset.UtcNow,
            completedLoad,
            observedFailures,
            abruptExits,
            operatorResult,
            machineResult,
            failureCodes,
            resources,
            cleanupPassed);
    }

    internal static void EvaluateBudget(
        SoakScenario scenario,
        ScenarioBudget budget,
        int completedLoad,
        int observedFailures,
        int abruptExits,
        bool cleanupPassed,
        ResourceObservation resources,
        ICollection<string> failureCodes)
    {
        if (completedLoad < budget.RequiredLoad)
        {
            failureCodes.Add("required-load-not-met");
        }

        if (observedFailures > budget.MaximumFailures)
        {
            failureCodes.Add("failure-budget-exceeded");
        }

        if (abruptExits != scenario.ExpectedAbruptExits)
        {
            failureCodes.Add("unexpected-exit-count");
        }

        if (resources.SampleCount == 0)
        {
            failureCodes.Add("resource-samples-missing");
        }

        if (resources.WorkingSetGrowthBytes > budget.MaximumWorkingSetGrowthBytes)
        {
            failureCodes.Add("rss-growth-budget-exceeded");
        }

        if (!cleanupPassed)
        {
            failureCodes.Add("cleanup-invariant-failed");
        }
    }

    private static void SampleFor(
        Process rootProcess,
        ProcessTreeTracker tracker,
        TimeSpan duration,
        ICollection<ResourceSample> samples)
    {
        var probe = new MacProcessTreeProbe();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            if (rootProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "The package exited before scenario sampling completed.");
            }

            tracker.CaptureSnapshot();
            var inspection = tracker.Inspect();
            long rss = 0;
            long cpuMilliseconds = 0;
            foreach (var tracked in inspection.LiveProcesses)
            {
                if (probe.ReadIdentity(tracked.Identity.ProcessId) != tracked.Identity)
                {
                    continue;
                }

                using var process = Process.GetProcessById(tracked.Identity.ProcessId);
                rss += process.WorkingSet64;
                cpuMilliseconds += (long)process.TotalProcessorTime.TotalMilliseconds;
                if (probe.ReadIdentity(tracked.Identity.ProcessId) != tracked.Identity)
                {
                    throw new ProcessTreeProbeException("A sampled process identity changed during resource attribution.");
                }
            }

            samples.Add(new ResourceSample(rss, cpuMilliseconds, inspection.LiveProcesses.Count));
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
    }

    private static ResourceObservation Summarize(IReadOnlyList<ResourceSample> samples, int capturedCount)
    {
        if (samples.Count == 0)
        {
            return new ResourceObservation(0, 0, 0, 0, 0, 0, 0, capturedCount);
        }

        var initial = samples[0];
        var final = samples[^1];
        return new ResourceObservation(
            samples.Count,
            initial.WorkingSetBytes,
            samples.Max(sample => sample.WorkingSetBytes),
            final.WorkingSetBytes,
            Math.Max(0, final.WorkingSetBytes - initial.WorkingSetBytes),
            Math.Max(0, final.CpuMilliseconds - initial.CpuMilliseconds),
            samples.Max(sample => sample.LiveProcessCount),
            capturedCount);
    }

    private static Process StartPackage(string executable)
    {
        var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable),
        }) ?? throw new InvalidOperationException("The package process did not start.");
        if (process.WaitForExit(1_500))
        {
            throw new InvalidOperationException("The package exited before soak observation began.");
        }

        return process;
    }

    private static SoakHost CaptureHost(string referenceConfigurationId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName.Normalize(NormalizationForm.FormKC)))).ToLowerInvariant();
        return new SoakHost(
            referenceConfigurationId,
            $"host-{digest[..16]}",
            EvidenceSanitizer.SanitizeSingleLine(RuntimeInformation.OSDescription).Value,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            ReadPowerSource());
    }

    private static string ReadPowerSource()
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/pmset", "-g batt")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        if (process is null || !process.WaitForExit(5_000))
        {
            return "unknown";
        }

        var output = process.StandardOutput.ReadToEnd();
        return output.Contains("AC Power", StringComparison.Ordinal) ? "ac"
            : output.Contains("Battery Power", StringComparison.Ordinal) ? "battery"
            : "unknown";
    }

    private static void EnsureMacOsArm64InteractiveHost()
    {
        if (!OperatingSystem.IsMacOS()
            || RuntimeInformation.OSArchitecture != Architecture.Arm64
            || RuntimeInformation.ProcessArchitecture != Architecture.Arm64
            || Console.IsInputRedirected
            || Console.IsOutputRedirected)
        {
            throw new InvalidDataException("Release soak acceptance requires a local interactive macOS arm64 host and terminal.");
        }
    }

    private static void RequireEnter(string prompt)
    {
        Console.WriteLine(prompt);
        if (Console.ReadLine() is null)
        {
            throw new InvalidDataException("Interactive input ended before the scenario completed.");
        }
    }

    private static int PromptNonNegativeInt(string prompt)
    {
        Console.Write(prompt);
        var input = Console.ReadLine();
        if (!int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result < 0)
        {
            throw new InvalidDataException("Expected one non-negative integer, with no notes or payload content.");
        }

        return result;
    }

    private static SoakStatus PromptResult()
    {
        Console.Write("Operator result (PASS/FAIL/BLOCKED): ");
        return Console.ReadLine()?.Trim().ToUpperInvariant() switch
        {
            "PASS" => SoakStatus.Pass,
            "FAIL" => SoakStatus.Fail,
            "BLOCKED" => SoakStatus.Blocked,
            _ => throw new InvalidDataException("Operator result must be PASS, FAIL, or BLOCKED."),
        };
    }

    private sealed record RunOptions(string PackagePath, string BuildLabel, string PolicyPath, string EvidenceDirectory)
    {
        public static RunOptions Parse(IReadOnlyList<string> args)
        {
            if (args.Count != 9 || !string.Equals(args[0], "run", StringComparison.Ordinal))
            {
                throw new ArgumentException("Usage: run --package Asura.app --build-label ID --policy policy.json --evidence-dir DIR");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Count; index += 2)
            {
                if (!values.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException("Each run option must appear exactly once.");
                }
            }

            if (values.Count != 4
                || !values.TryGetValue("--package", out var package)
                || !values.TryGetValue("--build-label", out var buildLabel)
                || !values.TryGetValue("--policy", out var policy)
                || !values.TryGetValue("--evidence-dir", out var evidenceDirectory)
                || !EvidenceSanitizer.IsValidIdentifier(buildLabel))
            {
                throw new ArgumentException("Run options or build label are invalid.");
            }

            return new RunOptions(package, buildLabel, policy, evidenceDirectory);
        }
    }

    private sealed record ResourceSample(long WorkingSetBytes, long CpuMilliseconds, int LiveProcessCount);
}
