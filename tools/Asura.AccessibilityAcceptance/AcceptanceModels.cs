using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Asura.AccessibilityAcceptance;

internal enum TargetPlatform
{
    MacOS,
    Windows,
    LinuxX11,
}

internal enum ScreenReaderKind
{
    VoiceOver,
    Narrator,
    Orca,
}

internal enum AcceptanceStatus
{
    Pass,
    Fail,
    Blocked,
}

internal static class AcceptanceBoundary
{
    public static AcceptanceStatus Constrain(
        AcceptanceStatus operatorResult,
        bool runnerBoundaryPassed) =>
        runnerBoundaryPassed ? operatorResult : AcceptanceStatus.Fail;

    public static string AddRunnerObservation(string observationMode) => observationMode switch
    {
        "operator-observed" => "operator-observed+runner-boundary",
        "operator-observed+runner-boundary" => observationMode,
        "runner-observed-boundary" => observationMode,
        _ => throw new ArgumentOutOfRangeException(
            nameof(observationMode),
            observationMode,
            "The observation mode is outside the acceptance schema."),
    };
}

internal sealed record AcceptanceAssertion(string Id, string Instructions);

internal sealed record AcceptanceCheck(
    string Id,
    string Title,
    string CommonInstructions,
    string MacOSInstructions,
    string WindowsInstructions,
    string LinuxInstructions,
    IReadOnlyList<AcceptanceAssertion> Assertions)
{
    public string InstructionsFor(TargetPlatform platform) =>
        $"{CommonInstructions} " + (platform switch
        {
            TargetPlatform.MacOS => MacOSInstructions,
            TargetPlatform.Windows => WindowsInstructions,
            TargetPlatform.LinuxX11 => LinuxInstructions,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        });
}

internal sealed record AssertionObservation(string Id, AcceptanceStatus Result);

internal sealed record CheckObservation(
    string Id,
    string Title,
    AcceptanceStatus Result,
    string ObservationMode,
    IReadOnlyList<AssertionObservation> Assertions,
    string Notes,
    int RedactionsApplied,
    DateTimeOffset ObservedAtUtc)
{
    public static AcceptanceStatus ResolveResult(
        IReadOnlyCollection<AssertionObservation> assertions)
    {
        ArgumentNullException.ThrowIfNull(assertions);
        if (assertions.Any(assertion => assertion.Result == AcceptanceStatus.Fail))
        {
            return AcceptanceStatus.Fail;
        }

        return assertions.Count > 0
            && assertions.All(assertion => assertion.Result == AcceptanceStatus.Pass)
                ? AcceptanceStatus.Pass
                : AcceptanceStatus.Blocked;
    }
}

internal sealed record HostEnvironmentSignals(
    bool AutomationDetected,
    bool ContainerDetected,
    bool RemoteSessionDetected,
    bool UnsupportedDisplayServerDetected,
    bool WaylandDisplayDetected,
    bool StandardInputInteractive,
    bool StandardOutputInteractive)
{
    public bool BlocksNamedHostAcceptance =>
        AutomationDetected
        || ContainerDetected
        || RemoteSessionDetected
        || UnsupportedDisplayServerDetected
        || WaylandDisplayDetected
        || !StandardInputInteractive
        || !StandardOutputInteractive;
}

internal sealed record HostIdentity(
    string DeclaredSystemName,
    string HostFingerprint,
    string Observer,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string DesktopSession,
    bool InteractiveUser,
    HostEnvironmentSignals EnvironmentSignals,
    IReadOnlyList<string> EnvironmentWarnings)
{
    public static HostIdentity Capture(
        TargetPlatform platform,
        string declaredSystemName,
        string observer)
    {
        var environment = HostEnvironmentProbe.Capture(platform);
        var warnings = HostEnvironmentProbe.DescribeBlockers(environment);
        return new HostIdentity(
            declaredSystemName,
            ComputeHostFingerprint(),
            observer,
            EvidenceSanitizer.SanitizeSingleLine(RuntimeInformation.OSDescription).Value,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            HostEnvironmentProbe.DescribeDesktopSession(platform),
            Environment.UserInteractive,
            environment,
            warnings);
    }

    private static string ComputeHostFingerprint()
    {
        var machineName = Environment.MachineName.Normalize(NormalizationForm.FormKC);
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(machineName))).ToLowerInvariant();
        return $"host-{digest[..16]}";
    }
}

internal sealed record AssistiveTechnologyIdentity(
    ScreenReaderKind Kind,
    string Product,
    string Version,
    string IdentitySource,
    string StatusBefore,
    string StatusAfter,
    string AccessibilityBusStatus);

internal sealed record BuildIdentity(
    string BuildLabel,
    string PackageKind,
    string PackageExecutable,
    string ProductVersion,
    long ExecutableLengthBytes,
    string ExecutableSha256,
    int PackageFileCount,
    string PackageManifestSha256,
    string ApplicationIdentity);

internal sealed record AcceptanceEvidence(
    int SchemaVersion,
    string EvidenceKind,
    string RunnerVersion,
    string CatalogVersion,
    string CatalogSha256,
    TargetPlatform Platform,
    ScreenReaderKind ScreenReader,
    HostIdentity Host,
    AssistiveTechnologyIdentity AssistiveTechnology,
    BuildIdentity Build,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    AcceptanceStatus OverallResult,
    string CleanupDisposition,
    string PreferenceRestorationDisposition,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<CheckObservation> Checks)
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentEvidenceKind =
        "asura-named-host-m1-accessibility-acceptance";
    public const string CurrentRunnerVersion = "1.1.0";
    public const string CurrentCatalogVersion = "1.1";
    public const string CleanExitDisposition =
        "All captured package process identities exited before runner cleanup; no process termination was required.";
    public const string PreferencesRestoredDisposition =
        "Operator confirmed original host accessibility preferences were restored.";
    public const string PreferencesNotRestoredDisposition =
        "Operator reported original host accessibility preferences were not restored; manual restoration is required.";
    public const string PreferencesUnconfirmedDisposition =
        "Preference restoration could not be confirmed; manual verification is required.";

    public static IReadOnlyList<string> StandardLimitations { get; } =
    [
        "PASS means a named operator used the expected running screen reader with the fingerprinted package on this one local interactive host.",
        "The runner captures no screenshots, audio, speech transcript, raw accessibility tree, terminal contents, clipboard payload, environment dump, raw host name, username, address, credential, or absolute package path; the actual host is represented by a one-way truncated fingerprint.",
        "Operator observations are not cryptographic attestation; the digest detects file changes but does not authenticate the operator or host.",
        "The truncated host-name hash supports receipt correlation but is not an anonymity guarantee for a guessable machine name.",
        "Environment probes detect common automation, container, remote-session, redirected-terminal, and virtual-display markers; the required operator assertion covers remote-control paths that software cannot reliably enumerate.",
        "Process cleanup retains stable identities found by bounded background sampling; it is not OS-level containment and cannot prove absence of a process that fully detached between samples.",
        "This evidence does not apply to another OS, screen reader, package digest, display server, accessibility configuration, or application version.",
        "Agent-state announcements are outside this M1 catalog and require acceptance when the governed agent surface is implemented.",
    ];

    public static AcceptanceStatus ResolveOverall(IReadOnlyCollection<CheckObservation> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Any(check => check.Result == AcceptanceStatus.Fail))
        {
            return AcceptanceStatus.Fail;
        }

        return checks.Count == AcceptanceCatalog.All.Count
            && checks.All(check => check.Result == AcceptanceStatus.Pass)
                ? AcceptanceStatus.Pass
                : AcceptanceStatus.Blocked;
    }

    public static ScreenReaderKind ScreenReaderFor(TargetPlatform platform) => platform switch
    {
        TargetPlatform.MacOS => ScreenReaderKind.VoiceOver,
        TargetPlatform.Windows => ScreenReaderKind.Narrator,
        TargetPlatform.LinuxX11 => ScreenReaderKind.Orca,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
    };
}
